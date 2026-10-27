using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// The React panel's build output (Resources/panel/) is committed, so the server, Docker and the launcher build without
/// Node. This test catches a build that no longer matches its ultimateteam/panel-web sources: it recomputes the hash that
/// panel-web/scripts/stamp.mjs writes to build-stamp.txt. Keep the two algorithms in step.
/// </summary>
public sealed class PanelBuildTests
{
    private static readonly string[] Files = ["index.html", "package.json", "package-lock.json", "tsconfig.json", "vite.config.ts"];
    private static readonly string[] Folders = ["src", "public", "scripts"];

    [Fact]
    public void TheCommittedBuildMatchesItsSources()
    {
        var (sources, stamp) = Locate();
        Assert.True(File.Exists(stamp), $"{stamp} is missing: run `npm run build` in ultimateteam/panel-web.");
        var recorded = File.ReadAllText(stamp).Trim();
        Assert.True(recorded == SourceHash(sources),
            "Resources/panel is stale: ultimateteam/panel-web changed since its last build. Run `npm run build` there and commit the output.");
    }

    /// <summary>
    /// SHA-256 over every source file sorted by its '/'-separated path relative to panel-web (ordinal), each as UTF-8
    /// "path\n", its bytes without CR (0x0D), then "\n". Test files (*.test.ts, *.test.tsx) are left out.
    /// </summary>
    private static string SourceHash(string root)
    {
        var files = Files.Select(name => Path.Combine(root, name)).Where(File.Exists).ToList();
        foreach (var folder in Folders.Select(name => Path.Combine(root, name)).Where(Directory.Exists))
            files.AddRange(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(file => !file.EndsWith(".test.ts", StringComparison.Ordinal) && !file.EndsWith(".test.tsx", StringComparison.Ordinal)));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, file) in files.Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), file))
                     .OrderBy(entry => entry.Item1, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\n"));
            hash.AppendData(File.ReadAllBytes(file).Where(value => value != 0x0D).ToArray());
            hash.AppendData("\n"u8);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static (string Sources, string Stamp) Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var sources = Path.Combine(directory.FullName, "ultimateteam", "panel-web");
            if (File.Exists(Path.Combine(sources, "package.json")))
                return (sources, Path.Combine(directory.FullName, "ultimateteam", "server", "src", "Aurora21.UltimateTeam.Server",
                    "Resources", "panel", "build-stamp.txt"));
        }
        throw new InvalidOperationException("Could not locate ultimateteam/panel-web");
    }
}
