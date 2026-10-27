using System.Diagnostics;
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
    string SettingsPath,
    bool EaActivation,
    bool AlwaysOnTop,
    double MusicVolume,
    bool MusicMuted,
    double SoundEffectsVolume,
    bool SoundEffectsMuted);

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

        // status.json is intentionally persisted for diagnostics, so it can
        // describe a previous launcher session. Never trust its FIFA state on
        // its own: confirm the live Windows process every time Discover runs.
        gameState = GetLiveFifaState(gameState);

        return new RuntimeSnapshot(root, compatible, gameState, connectionState);
    }

    public static LauncherSettingsSnapshot LoadSettings(string root)
    {
        string settingsPath = Path.Combine(ResolveSettingsRoot(), "launcher-settings.json");
        string gamePath = string.Empty;
        bool eaActivation = false;
        bool alwaysOnTop = true;
        double musicVolume = 0.12;
        bool musicMuted = false;
        double soundEffectsVolume = 0.20;
        bool soundEffectsMuted = false;
        if (File.Exists(settingsPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject settings)
                {
                    gamePath = settings["gamePath"]?.GetValue<string>() ?? string.Empty;
                    if (settings["launchViaEAApp"] is JsonValue activationValue &&
                        activationValue.TryGetValue(out bool savedActivation))
                    {
                        eaActivation = savedActivation;
                    }

                    if (settings["alwaysOnTop"] is JsonValue alwaysOnTopValue &&
                        alwaysOnTopValue.TryGetValue(out bool savedAlwaysOnTop))
                    {
                        alwaysOnTop = savedAlwaysOnTop;
                    }

                    if (settings["musicVolume"] is JsonValue volumeValue &&
                        volumeValue.TryGetValue(out double savedMusicVolume) &&
                        double.IsFinite(savedMusicVolume))
                    {
                        musicVolume = Math.Clamp(savedMusicVolume, 0, 1);
                    }

                    if (settings["musicMuted"] is JsonValue mutedValue &&
                        mutedValue.TryGetValue(out bool savedMusicMuted))
                    {
                        musicMuted = savedMusicMuted;
                    }

                    if (settings["soundEffectsVolume"] is JsonValue effectsVolumeValue &&
                        effectsVolumeValue.TryGetValue(out double savedSoundEffectsVolume) &&
                        double.IsFinite(savedSoundEffectsVolume))
                    {
                        soundEffectsVolume = Math.Clamp(savedSoundEffectsVolume, 0, 1);
                    }

                    if (settings["soundEffectsMuted"] is JsonValue effectsMutedValue &&
                        effectsMutedValue.TryGetValue(out bool savedSoundEffectsMuted))
                    {
                        soundEffectsMuted = savedSoundEffectsMuted;
                    }
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
        return new LauncherSettingsSnapshot(
            gamePath,
            sourcePath,
            settingsPath,
            eaActivation,
            alwaysOnTop,
            musicVolume,
            musicMuted,
            soundEffectsVolume,
            soundEffectsMuted);
    }

    public static void SaveMusicSettings(string root, double musicVolume, bool musicMuted)
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
                // Replace malformed settings with a minimal valid object.
            }
        }

        settings["sourcePath"] ??= Path.Combine(root, "runtime-source");
        settings["musicVolume"] = Math.Clamp(musicVolume, 0, 1);
        settings["musicMuted"] = musicMuted;

        string temporaryPath = settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, settingsPath, true);
    }

    public static void SaveSoundEffectsSettings(string root, double soundEffectsVolume, bool soundEffectsMuted)
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
                // Replace malformed settings with a minimal valid object.
            }
        }

        settings["sourcePath"] ??= Path.Combine(root, "runtime-source");
        settings["soundEffectsVolume"] = Math.Clamp(soundEffectsVolume, 0, 1);
        settings["soundEffectsMuted"] = soundEffectsMuted;

        string temporaryPath = settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, settingsPath, true);
    }

    public static void SavePlayerSettings(string root, string gamePath, bool eaActivation, bool alwaysOnTop)
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
        settings["launchViaEAApp"] = eaActivation;
        settings["alwaysOnTop"] = alwaysOnTop;
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


    private static string GetLiveFifaState(string reportedState)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("FIFA21");
        }
        catch
        {
            // If Windows refuses process enumeration, prefer a conservative
            // state rather than displaying a persisted RUNNING result.
            return "Not running (process check unavailable)";
        }

        try
        {
            Process? live = processes.FirstOrDefault(process =>
            {
                try { return !process.HasExited; }
                catch { return false; }
            });

            if (live is null) return "Not running";

            int pid;
            try { pid = live.Id; }
            catch { return "RUNNING (live FIFA21.exe process detected)"; }

            // Preserve richer current status text only when it references the
            // PID that Windows confirms is alive. Otherwise replace it with a
            // fresh live-process result so an old PID cannot leak into the UI.
            string pidMarker = $"PID {pid}";
            if (reportedState.Contains("running", StringComparison.OrdinalIgnoreCase) &&
                reportedState.Contains(pidMarker, StringComparison.OrdinalIgnoreCase))
            {
                return reportedState;
            }

            return $"RUNNING (PID {pid}; live FIFA21.exe detected)";
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
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
