using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Project21Launcher;

internal sealed record RuntimeSnapshot(
    string Root,
    bool IsCompatible,
    string GameState,
    string ConnectionState);

internal sealed record LauncherSettingsSnapshot(
    string GamePath,
    string SourcePath,
    string SettingsPath);

internal static class RuntimeCompatibility
{
    public static RuntimeSnapshot Discover()
    {
        string root = ResolveRuntimeRoot();
        bool compatible =
            File.Exists(Path.Combine(root, "Launch-LocalFUT.ps1")) &&
            File.Exists(Path.Combine(root, "Stop-LocalFUT.ps1")) &&
            File.Exists(Path.Combine(root, "runtime-source", "localfut21", "server.py")) &&
            File.Exists(Path.Combine(root, "runtime-source", "redirector_tls.py"));

        string gameState = "Not running";
        string connectionState = "Not connected";
        ReadRuntimeStatus(root, ref gameState, ref connectionState);
        return new RuntimeSnapshot(root, compatible, gameState, connectionState);
    }

    public static LauncherSettingsSnapshot LoadSettings(string root)
    {
        string settingsPath = Path.Combine(ResolveSettingsRoot(), "launcher-settings.json");
        string gamePath = string.Empty;
        if (File.Exists(settingsPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject settings)
                {
                    gamePath = settings["gamePath"]?.GetValue<string>() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                // The settings screen stays available so the player can replace invalid data.
            }
            catch (IOException)
            {
                // The file may be briefly locked by a launch already in progress.
            }
        }

        // The standalone launcher always uses its bundled connection relay. A
        // sourcePath left by an older launcher must never select developer files.
        string sourcePath = Path.Combine(root, "runtime-source");
        return new LauncherSettingsSnapshot(gamePath, sourcePath, settingsPath);
    }

    public static void SavePlayerSettings(string root, string gamePath)
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
                // Replace malformed JSON with a valid player settings object.
            }
        }

        settings["gamePath"] = gamePath;
        settings["sourcePath"] = Path.Combine(root, "runtime-source");
        settings["gameReadyDelaySeconds"] ??= 5;
        string temporaryPath = settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, settingsPath, true);
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

    private static void ReadRuntimeStatus(string root, ref string gameState, ref string connectionState)
    {
        string statusPath = Path.Combine(root, "runtime", "status.json");
        if (!File.Exists(statusPath)) return;
        try
        {
            using JsonDocument status = JsonDocument.Parse(File.ReadAllText(statusPath));
            gameState = ReadStatusValue(status.RootElement, "game", gameState);
            connectionState = ReadStatusValue(status.RootElement, "connection", connectionState);
        }
        catch (JsonException)
        {
            // Keep fallback values if a partial status update is observed.
        }
        catch (IOException)
        {
            // Keep the launcher responsive while another process updates status.
        }
    }

    private static string ReadStatusValue(JsonElement status, string propertyName, string fallback)
    {
        if (status.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return fallback;
    }

    private static string ResolveSettingsRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("LOCALFUT21_USER_CONFIG_ROOT");
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Project21", "LocalFUT");
    }
}
