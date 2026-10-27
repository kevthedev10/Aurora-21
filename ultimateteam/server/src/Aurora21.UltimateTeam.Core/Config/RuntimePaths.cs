namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>
/// Every filesystem location used by the runtime. Bundled content (catalogs, certificates)
/// is resolved relative to the executable; mutable state stays outside the game install.
/// </summary>
public sealed class RuntimePaths
{
    public const string RuntimeRootVariable = "ULTIMATETEAM_RUNTIME_ROOT";
    public const string UserConfigRootVariable = "ULTIMATETEAM21_USER_CONFIG_ROOT";
    public const string DataRootVariable = "ULTIMATETEAM_DATA_ROOT";
    public const string PostgresRootVariable = "ULTIMATETEAM_POSTGRES_ROOT";

    /// <summary>
    /// A certificate folder apart from the bundled data (a container volume): the server's CAs and leaf certificates must
    /// persist and never be baked into an image.
    /// </summary>
    public const string CertificateRootVariable = "ULTIMATETEAM_CERTIFICATE_ROOT";

    private readonly string? _certificateRoot;

    internal RuntimePaths(string dataRoot, string runtimeRoot, string userConfigRoot, bool runtimeRootOverridden, string? certificateRoot = null)
    {
        DataRoot = dataRoot;
        RuntimeRoot = runtimeRoot;
        UserConfigRoot = userConfigRoot;
        RuntimeRootOverridden = runtimeRootOverridden;
        _certificateRoot = certificateRoot;
    }

    /// <summary>Bundled read-only content: players.json, managers.json, sbc.json, contentfifa/ ...</summary>
    public string DataRoot { get; }

    /// <summary>
    /// Directory holding <c>certs/</c> and the trust-bridge modulus files: <see cref="CertificateRootVariable"/> when set,
    /// otherwise the folder above the data root.
    /// </summary>
    public string CertificateRoot => _certificateRoot ?? Path.GetDirectoryName(DataRoot) ?? DataRoot;

    public string RuntimeRoot { get; }

    public bool RuntimeRootOverridden { get; }

    public string UserConfigRoot { get; }

    public string DataDirectory => Path.Combine(RuntimeRoot, "data");

    public string LogsDirectory => Path.Combine(RuntimeRoot, "logs");

    /// <summary>Card, player, badge and flag images the operator panel serves (filled by ultimateteam/download-card-images.py).</summary>
    public string PanelAssetsDirectory
    {
        get
        {
            // Read-only art travels with the runtime-source content in a checkout/distribution.
            // Explicit --panel-assets-dir remains authoritative (including the hosted Docker mount).
            var bundled = Path.GetFullPath(Path.Combine(DataRoot, "..", "..", "panel-assets"));
            return Directory.Exists(bundled) ? bundled : Path.Combine(RuntimeRoot, "panel-assets");
        }
    }

    /// <summary>Players' names by identity id, written by the sign-in gateway (see <see cref="PlayerNames"/>).</summary>
    public string PlayerNamesPath => Path.Combine(RuntimeRoot, "state", "player-names.json");

    /// <summary>Each player's Windows build and maths bridge report (<see cref="Sessions.MathGroups"/>).</summary>
    public string WindowsBuildsPath => Path.Combine(RuntimeRoot, "state", "windows-builds.json");

    public string ContentCacheDirectory => Path.Combine(RuntimeRoot, "content-cache");

    public string ContentRoot => Path.Combine(DataRoot, "contentfifa");

    public string CustomPlayerHeadsDirectory => Path.Combine(DataRoot, "custom-playerheads");

    /// <summary>Portable PostgreSQL builds, one folder per platform (<c>win-x64</c>, <c>linux-x64</c>).</summary>
    public string PostgresInstallRoot
    {
        get
        {
            var explicitRoot = Environment.GetEnvironmentVariable(PostgresRootVariable);
            // Beside runtime-source, derived from the data root (not the certificate override, which may be a volume).
            var bundledRoot = Path.GetDirectoryName(DataRoot) ?? DataRoot;
            return string.IsNullOrEmpty(explicitRoot)
                ? Path.Combine(Path.GetDirectoryName(bundledRoot) ?? bundledRoot, "postgres")
                : Path.GetFullPath(explicitRoot);
        }
    }

    public string PostgresBinDirectory => Path.Combine(PostgresInstallRoot, PostgresPlatform, "bin");

    /// <summary>The managed cluster. The folder name carries the major version its files belong to.</summary>
    public string PostgresDataDirectory => Path.Combine(RuntimeRoot, "postgres", "data-18");

    public string PostgresPasswordPath => Path.Combine(RuntimeRoot, "postgres", "ultimateteam.password");

    public static string PostgresPlatform =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-"
        + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    public static string DefaultRuntimeRoot => Path.GetFullPath(Path.Combine(LocalAppData, "FIFA21UltimateTeam"));

    private static string LocalAppData
    {
        get
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            return string.IsNullOrEmpty(local)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local")
                : local;
        }
    }

    public static RuntimePaths Resolve(string? dataRoot = null)
    {
        var runtimeOverride = Environment.GetEnvironmentVariable(RuntimeRootVariable);
        var runtimeRoot = string.IsNullOrEmpty(runtimeOverride)
            ? DefaultRuntimeRoot
            : Path.GetFullPath(ExpandHome(runtimeOverride));

        var configOverride = Environment.GetEnvironmentVariable(UserConfigRootVariable);
        string userConfigRoot;
        if (!string.IsNullOrEmpty(configOverride))
        {
            if (!Path.IsPathRooted(configOverride))
            {
                throw new RuntimeSetupException($"{UserConfigRootVariable} must be an absolute path");
            }
            userConfigRoot = Path.GetFullPath(configOverride);
        }
        else
        {
            userConfigRoot = Path.Combine(LocalAppData, "Aurora21", "UltimateTeam");
        }

        var certificateOverride = Environment.GetEnvironmentVariable(CertificateRootVariable);
        if (!string.IsNullOrEmpty(certificateOverride) && !Path.IsPathRooted(certificateOverride))
        {
            throw new RuntimeSetupException($"{CertificateRootVariable} must be an absolute path");
        }

        return new RuntimePaths(ResolveDataRoot(dataRoot), runtimeRoot, userConfigRoot, !string.IsNullOrEmpty(runtimeOverride),
            string.IsNullOrEmpty(certificateOverride) ? null : Path.GetFullPath(certificateOverride));
    }

    /// <summary>
    /// HTTP-only test runs must never touch the maintainer's real club or preferences:
    /// both roots must be overridden, separate from the default root, and the config root
    /// must sit inside the runtime root.
    /// </summary>
    public void EnsureIsolatedTestRoots()
    {
        var runtimeOverride = Environment.GetEnvironmentVariable(RuntimeRootVariable);
        var configRoot = Path.GetFullPath(Environment.GetEnvironmentVariable(UserConfigRootVariable) ?? ".");
        var defaultRoot = DefaultRuntimeRoot;
        var isolated = !string.IsNullOrEmpty(runtimeOverride)
            && Path.IsPathRooted(runtimeOverride)
            && !PathEquals(RuntimeRoot, defaultRoot)
            && !IsParentOf(defaultRoot, RuntimeRoot)
            && !IsParentOf(RuntimeRoot, defaultRoot)
            && IsParentOf(RuntimeRoot, configRoot);
        if (!isolated)
        {
            throw new RuntimeSetupException("HTTP-only tests require separate runtime and user-config roots");
        }
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ContentCacheDirectory);
    }

    /// <summary>Resolves <paramref name="relative"/> under <paramref name="root"/>, refusing escapes.</summary>
    public static string? ContainedPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative.TrimStart('/', '\\')));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison) ? candidate : null;
    }

    /// <summary>
    /// Like <see cref="ContainedPath"/>, but matches each segment case-insensitively so
    /// lower-cased request paths resolve on case-sensitive filesystems too.
    /// </summary>
    public static string? ContainedPathIgnoreCase(string root, string relative)
    {
        var exact = ContainedPath(root, relative);
        if (exact is null || File.Exists(exact) || OperatingSystem.IsWindows())
        {
            return exact;
        }
        var current = Path.GetFullPath(root);
        foreach (var segment in relative.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current))
            {
                return exact;
            }
            var match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), segment, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return exact;
            }
            current = match;
        }
        return ContainedPath(root, Path.GetRelativePath(root, current));
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), PathComparison);

    private static bool IsParentOf(string parent, string child)
    {
        var prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, PathComparison);
    }

    private static string ExpandHome(string path) =>
        path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path;

    private static string ResolveDataRoot(string? explicitRoot)
    {
        var candidate = explicitRoot ?? Environment.GetEnvironmentVariable(DataRootVariable);
        if (!string.IsNullOrEmpty(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        // Walk up from the executable: a published build sits beside the repository's
        // runtime-source/ folder, a development build is several levels deeper.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var relative in new[] { "ultimateteam21", Path.Combine("runtime-source", "ultimateteam21"), Path.Combine("ultimateteam", "runtime-source", "ultimateteam21") })
            {
                var probe = Path.Combine(directory.FullName, relative);
                if (File.Exists(Path.Combine(probe, "players.json")))
                {
                    return probe;
                }
            }
            directory = directory.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "ultimateteam21");
    }
}
