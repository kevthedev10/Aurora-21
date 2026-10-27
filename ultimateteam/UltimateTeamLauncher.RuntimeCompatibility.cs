using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aurora21Launcher;

internal sealed record RuntimeSnapshot(
    string Root,
    bool IsCompatible,
    string ProfileName,
    string ClubName,
    string ServerState,
    string GameState,
    string ConnectionState);

internal sealed record LauncherSettingsSnapshot(
    string GamePath,
    string SourcePath,
    string ClubName,
    string ClubAbbreviation,
    string RuntimeConfigPath,
    string SettingsPath,
    string? Problem);

internal static class RuntimeCompatibility
{
    /// <summary>A runtime source folder holds the bundled catalogs and the redirector certificate.</summary>
    public static bool HasRuntimeData(string sourcePath) =>
        Directory.Exists(sourcePath) &&
        File.Exists(Path.Combine(sourcePath, "ultimateteam21", "players.json")) &&
        File.Exists(Path.Combine(sourcePath, "certs", "spring18-cert.pem"));

    public static RuntimeSnapshot Discover()
    {
        string root = ResolveRuntimeRoot();
        bool hasLauncher = File.Exists(Path.Combine(root, "Launch-UltimateTeam.ps1"));
        bool hasStopper = File.Exists(Path.Combine(root, "Stop-UltimateTeam.ps1"));
        // The C# server hosts every service, including the TLS redirector subcommand.
        bool hasServer = File.Exists(Path.Combine(root, "Aurora21-UltimateTeam-Server.exe"));
        bool hasRuntimeData = HasRuntimeData(Path.Combine(root, "runtime-source"));
        bool compatible = hasLauncher && hasStopper && hasServer && hasRuntimeData;

        string profileName = "Local Player";
        string clubName = "Local FC";
        string serverState = compatible ? "Ready" : "Runtime missing";
        string gameState = "Not running";
        string connectionState = "Inactive";

        ReadProfile(root, ref profileName, ref clubName);
        ReadRuntimeStatus(root, ref serverState, ref gameState, ref connectionState);

        return new RuntimeSnapshot(
            root,
            compatible,
            profileName,
            clubName,
            serverState,
            gameState,
            connectionState);
    }

    public static LauncherSettingsSnapshot LoadSettings(string root)
    {
        string settingsRoot = ResolveSettingsRoot();
        Directory.CreateDirectory(settingsRoot);
        string settingsPath = Path.Combine(settingsRoot, "launcher-settings.json");
        string configPath = Path.Combine(settingsRoot, "config.json");
        string gamePath = string.Empty;
        string sourcePath = Path.Combine(root, "runtime-source");
        string clubName = "Local FC";
        string clubAbbreviation = "LCL";
        string? problem = null;

        if (!File.Exists(settingsPath))
        {
            WriteJsonAtomically(settingsPath, new JsonObject
            {
                ["sourcePath"] = string.Empty,
                ["gamePath"] = string.Empty,
                ["gameReadyDelaySeconds"] = 5,
            });
        }

        try
        {
            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(settingsPath));
            if (parsed is not JsonObject settings)
            {
                throw new JsonException("launcher-settings.json must contain a JSON object");
            }
            gamePath = settings["gamePath"]?.GetValue<string>() ?? string.Empty;
            string configuredSource = settings["sourcePath"]?.GetValue<string>() ?? string.Empty;
            sourcePath = string.IsNullOrWhiteSpace(configuredSource) ? sourcePath : configuredSource;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IOException)
        {
            problem = $"Cannot read {settingsPath}. Correct it or save explicit replacement settings. The file was not overwritten. {error.Message}";
        }

        if (File.Exists(configPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(configPath)) is JsonObject config)
                {
                    clubName = config["club_name"]?.GetValue<string>() ?? clubName;
                    clubAbbreviation = config["club_abbr"]?.GetValue<string>() ?? clubAbbreviation;
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or IOException)
            {
                problem = $"Cannot read {configPath}. Correct it before saving club settings. The file was not overwritten. {error.Message}";
            }
        }

        return new LauncherSettingsSnapshot(
            gamePath, sourcePath, clubName, clubAbbreviation, configPath, settingsPath, problem);
    }

    public static void SaveSettings(
        string root, string gamePath, string sourcePath, string clubName, string clubAbbreviation)
    {
        string settingsRoot = ResolveSettingsRoot();
        Directory.CreateDirectory(settingsRoot);
        string settingsPath = Path.Combine(settingsRoot, "launcher-settings.json");
        JsonObject settings = new();

        if (File.Exists(settingsPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject existing)
                {
                    settings = existing;
                }
            }
            catch (JsonException)
            {
                // Replace malformed JSON with a valid settings object.
            }
        }

        settings["gamePath"] = gamePath;
        string bundledSource = Path.GetFullPath(Path.Combine(root, "runtime-source"));
        settings["sourcePath"] = PathsEqual(sourcePath, bundledSource) ? string.Empty : sourcePath;
        settings["gameReadyDelaySeconds"] ??= 5;

        WriteJsonAtomically(settingsPath, settings);

        string configPath = Path.Combine(settingsRoot, "config.json");
        JsonObject config = new();
        if (File.Exists(configPath))
        {
            if (JsonNode.Parse(File.ReadAllText(configPath)) is JsonObject existingConfig)
            {
                config = existingConfig;
            }
        }
        config["club_name"] = clubName;
        config["club_abbr"] = clubAbbreviation;
        WriteJsonAtomically(configPath, config);
    }

    private static string ResolveRuntimeRoot()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int index = 1; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], "--runtime-root", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        return AppContext.BaseDirectory;
    }

    private static void ReadProfile(string root, ref string profileName, ref string clubName)
    {
        string configPath = Path.Combine(ResolveSettingsRoot(), "config.json");
        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            using JsonDocument config = JsonDocument.Parse(File.ReadAllText(configPath));
            JsonElement rootElement = config.RootElement;
            if (rootElement.TryGetProperty("display_name", out JsonElement displayName) &&
                !string.IsNullOrWhiteSpace(displayName.GetString()))
            {
                profileName = displayName.GetString()!;
            }

            if (rootElement.TryGetProperty("club_name", out JsonElement configuredClub) &&
                !string.IsNullOrWhiteSpace(configuredClub.GetString()))
            {
                clubName = configuredClub.GetString()!;
            }
        }
        catch (JsonException)
        {
            // A malformed optional profile file must not prevent the shell opening.
        }
        catch (IOException)
        {
            // Runtime files can be momentarily locked while the server starts.
        }
    }

    private static void ReadRuntimeStatus(
        string root,
        ref string serverState,
        ref string gameState,
        ref string connectionState)
    {
        string statusPath = Path.Combine(root, "runtime", "status.json");
        if (!File.Exists(statusPath))
        {
            return;
        }

        try
        {
            using JsonDocument status = JsonDocument.Parse(File.ReadAllText(statusPath));
            JsonElement statusRoot = status.RootElement;
            serverState = ReadStatusValue(statusRoot, "server", serverState);
            gameState = ReadStatusValue(statusRoot, "game", gameState);
            connectionState = ReadStatusValue(statusRoot, "connection", connectionState);
        }
        catch (JsonException)
        {
            // Keep the discovered fallback values when a partial status write is read.
        }
        catch (IOException)
        {
            // Keep the shell available even if the runtime status is temporarily locked.
        }
    }

    private static string ReadStatusValue(JsonElement status, string propertyName, string fallback)
    {
        if (status.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return fallback;
    }

    private static string ResolveSettingsRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("ULTIMATETEAM21_USER_CONFIG_ROOT");
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aurora21", "UltimateTeam");
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void WriteJsonAtomically(string path, JsonObject value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
