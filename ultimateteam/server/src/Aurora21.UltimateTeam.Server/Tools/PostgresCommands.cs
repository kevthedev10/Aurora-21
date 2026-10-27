using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Server.Hosting;

namespace Aurora21.UltimateTeam.Server.Tools;

public sealed class PostgresBuild
{
    public required string Artifact { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary>Resources/postgres.json: the pinned portable PostgreSQL download.</summary>
public sealed class PostgresRelease
{
    public required string Version { get; init; }
    public required string DownloadUrl { get; init; }
    public required Dictionary<string, PostgresBuild> Builds { get; init; }

    public static PostgresRelease Load() =>
        RulesJson.Deserialize<PostgresRelease>(EmbeddedResources.ReadText(typeof(PostgresRelease).Assembly, "postgres.json"), "postgres.json");

    public string UrlFor(PostgresBuild build) => RuleText.Format(DownloadUrl, ("artifact", build.Artifact), ("version", Version));
}

/// <summary>Installs and controls the portable PostgreSQL cluster (see Resources/postgres.json).</summary>
public static class PostgresCommands
{
    private const string VersionMarkerFile = "VERSION";

    public static async Task<int> RunAsync(string action, string? dataRoot)
    {
        var paths = RuntimePaths.Resolve(dataRoot);
        return action switch
        {
            "install" => await InstallAsync(paths),
            "status" => Status(paths),
            "start" => Start(paths),
            "stop" => Stop(paths),
            _ => throw new UsageException($"unknown postgres action '{action}' (install, status, start, stop)"),
        };
    }

    private static async Task<int> InstallAsync(RuntimePaths paths)
    {
        var release = PostgresRelease.Load();
        var platform = RuntimePaths.PostgresPlatform;
        if (!release.Builds.TryGetValue(platform, out var build))
        {
            throw new UsageException($"no pinned PostgreSQL build for {platform}");
        }
        var target = Path.Combine(paths.PostgresInstallRoot, platform);
        var marker = Path.Combine(target, VersionMarkerFile);
        var expectedMarker = $"{release.Version} {build.Sha256}";
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == expectedMarker && File.Exists(Path.Combine(paths.PostgresBinDirectory, LocalPostgres.ExecutableName("pg_ctl"))))
        {
            Report("already-installed", release.Version, target);
            return 0;
        }
        if (new LocalPostgres(LocalPostgresLayout.From(paths), NullLogger.Instance).RunningPort() is not null)
        {
            throw new RuntimeSetupException("The local PostgreSQL cluster is running from these binaries; stop UltimateTeam first.");
        }

        var work = Path.Combine(Path.GetTempPath(), "aurora21-postgres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string? stagingToClean = null;
        try
        {
            var jar = Path.Combine(work, "postgres.jar");
            var url = release.UrlFor(build);
            Console.WriteLine($"Downloading PostgreSQL {release.Version} ({platform}) from {url}");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await using (var download = await http.GetStreamAsync(url))
            await using (var file = File.Create(jar))
            {
                await download.CopyToAsync(file);
            }
            string actual;
            await using (var file = File.OpenRead(jar))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
            }
            if (actual != build.Sha256)
            {
                throw new RuntimeSetupException($"PostgreSQL download checksum mismatch: expected {build.Sha256}, got {actual}");
            }

            var archive = Path.Combine(work, "postgres.txz");
            using (var zip = ZipFile.OpenRead(jar))
            {
                var entry = zip.Entries.SingleOrDefault(e => e.FullName.EndsWith(".txz", StringComparison.OrdinalIgnoreCase))
                    ?? throw new RuntimeSetupException("The PostgreSQL package does not contain a .txz archive");
                entry.ExtractToFile(archive);
            }
            // Extract beside the target so the final move stays on one volume.
            var staging = target + ".installing-" + Guid.NewGuid().ToString("N")[..8];
            Directory.CreateDirectory(staging);
            stagingToClean = staging;
            // .NET has no xz decoder; tar ships with Windows 10+ and every Linux distribution.
            var tar = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "tar.exe") : "tar";
            using (var process = Process.Start(new ProcessStartInfo(tar) { ArgumentList = { "-xf", archive, "-C", staging }, UseShellExecute = false })!)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    throw new RuntimeSetupException($"tar exited with {process.ExitCode} while extracting PostgreSQL");
                }
            }
            if (!File.Exists(Path.Combine(staging, "bin", LocalPostgres.ExecutableName("pg_ctl"))))
            {
                throw new RuntimeSetupException("The extracted PostgreSQL package has no bin/pg_ctl");
            }
            File.WriteAllText(Path.Combine(staging, VersionMarkerFile), expectedMarker + Environment.NewLine);

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
            stagingToClean = null;
            Report("installed", release.Version, target);
            return 0;
        }
        finally
        {
            foreach (var directory in new[] { work, stagingToClean })
            {
                try
                {
                    if (directory is not null && Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static int Status(RuntimePaths paths)
    {
        var cluster = Cluster(paths);
        Console.WriteLine(FutJson.Serialize(new JsonObject
        {
            ["binaries"] = cluster.BinariesInstalled ? paths.PostgresBinDirectory : null,
            ["dataDirectory"] = paths.PostgresDataDirectory,
            ["initialized"] = cluster.Initialized,
            ["port"] = cluster.RunningPort(),
        }));
        return 0;
    }

    private static int Start(RuntimePaths paths)
    {
        paths.EnsureDirectories();
        var settings = ServerConfig.Load(new UserConfigStore(paths)).Database;
        var instance = new LocalPostgres(LocalPostgresLayout.From(paths, settings), Loggers.CreateLogger("UltimateTeam.Postgres")).Start();
        Console.WriteLine(FutJson.Serialize(new JsonObject { ["status"] = instance.StartedHere ? "started" : "already-running", ["port"] = instance.Port }));
        return 0;
    }

    private static int Stop(RuntimePaths paths)
    {
        var stopped = Cluster(paths).Stop();
        Console.WriteLine(FutJson.Serialize(new JsonObject { ["status"] = stopped ? "stopped" : "not-running" }));
        return 0;
    }

    private static readonly ILoggerFactory Loggers = ConsoleLogging.Quiet();

    private static LocalPostgres Cluster(RuntimePaths paths) =>
        new(LocalPostgresLayout.From(paths), Loggers.CreateLogger("UltimateTeam.Postgres"));

    private static void Report(string status, string version, string target) =>
        Console.WriteLine(FutJson.Serialize(new JsonObject { ["status"] = status, ["version"] = version, ["path"] = target }));
}
