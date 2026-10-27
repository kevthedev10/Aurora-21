using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using IOPath = System.IO.Path;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Wave;
using WinForms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Project21Launcher;

public partial class MainWindow : Window
{
    private readonly RuntimeSnapshot runtimeSnapshot;
    private readonly DispatcherTimer statusTimer;
    private readonly DispatcherTimer banStatusTimer;
    private Process? startProcess;
    private Process? stopProcess;
    private Process? wireGuardProcess;
    private Process? pythonSetupProcess;
    private Process? banCheckProcess;
    private bool pythonReady;
    private bool secureConnectionVerified;
    private bool clubRequestRunning;
    private bool dailyDropRequestRunning;
    private bool dailyDropAvailable;
    private DateTimeOffset? dailyDropNextAvailableUtc;
    private const int DailyDropTileWidth = 104;
    private const int DailyDropTileMargin = 4;
    private const int DailyDropTileCount = 72;
    private const int DailyDropLandingIndex = 60;
    private static readonly int[] DailyDropRewardValues = { 100, 250, 500, 1000, 2500, 5000 };
    private bool banWarningShown;
    private readonly List<Window> banWarningWindows = new();
    private const string BanMarkerPrefix = "PROJECT21_BANNED:";

    private enum ConnectionLogSeverity
    {
        Info,
        Success,
        Warning,
        Error
    }

    private sealed class ProcessDiagnosticCapture
    {
        private readonly object sync = new();
        private readonly Queue<string> output = new();
        private readonly Queue<string> errors = new();

        public ProcessDiagnosticCapture(string stageName, string? scriptPath)
        {
            StageName = stageName;
            ScriptPath = scriptPath;
        }

        public string StageName { get; }
        public string? ScriptPath { get; }

        public void Add(string line, bool isError)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            lock (sync)
            {
                Queue<string> target = isError ? errors : output;
                target.Enqueue(line.Trim());
                while (target.Count > 24)
                {
                    target.Dequeue();
                }
            }
        }

        public string[] GetOutput()
        {
            lock (sync) return output.ToArray();
        }

        public string[] GetErrors()
        {
            lock (sync) return errors.ToArray();
        }
    }

    private bool connectionLogReady;
    private string? lastLoggedGameState;
    private string? lastLoggedConnectionState;
    private bool? lastLoggedCompatibility;
    private bool processEnumerationWarningLogged;
    private readonly DateTime launcherSessionStartedUtc = DateTime.UtcNow;
    private DateTime? runtimeStatusBaselineWriteUtc;
    private bool startupConnectionCheckStarted;

    // Local media lives beside the launcher in the sources folder. Nothing is
    // embedded into the EXE so artwork/music can be replaced without rebuilding.
    private readonly MediaPlayer musicPlayer = new();
    private readonly List<WaveOutEvent> launcherSoundOutputs = new();
    private readonly Random launcherSoundRandom = new();
    private int lastTabSoundVariant = -1;
    private readonly List<string> musicPlaylist = new();
    private int currentMusicIndex = -1;
    private int consecutiveMusicFailures;
    private bool musicIsPlaying;
    private bool musicStartRequested;
    private bool musicMuted;
    private bool soundEffectsMuted;
    private bool mediaControlsReady;
    private readonly DispatcherTimer musicVolumeSaveTimer;
    private readonly DispatcherTimer soundEffectsVolumeSaveTimer;
    private double pendingMusicVolume = 0.12;
    private double pendingSoundEffectsVolume = 0.20;
    private double welcomeMusicFadeFactor;
    private bool startupMusicFadePending = true;
    private bool musicMediaIsOpen;
    private TaskCompletionSource<bool> startupMusicOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? startupMusicFadeCancellation;

    // Per-track loudness matching keeps shuffled songs at a much more consistent
    // perceived level while preserving the user's volume slider as the master level.
    // Gains are measured from representative decoded samples and cached per file.
    private readonly Dictionary<string, MusicGainCacheEntry> musicGainCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? musicTrackLoadCancellation;
    private CancellationTokenSource? musicGainWarmupCancellation;
    private CancellationTokenSource? cloudMediaSyncCancellation;
    private int musicTrackRequestVersion;
    private double currentTrackGain = 1.0;
    private const int MusicGainCacheVersion = 1;
    private const double TargetMusicRmsDb = -18.0;
    private const double MinTrackGainDb = -8.0;
    private const double MaxTrackGainDb = 6.0;

    private sealed class MusicGainCacheEntry
    {
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public double Gain { get; set; } = 1.0;
    }

    // First-run shortcut prompt state is stored per Windows user, outside the
    // launcher folder, so moving/updating the launcher does not show it again.
    private TaskCompletionSource<bool>? firstRunShortcutPromptCompletion;
    private bool firstRunShortcutChoiceInProgress;

    // The post-launch sequence is armed only after the user starts a new FIFA 21
    // launch. This avoids treating a FIFA process that was already open before
    // the launch button was pressed as a successful new launch.
    private bool gameLaunchDetectionArmed;
    private bool gameLaunchSequenceStarted;
    private bool manualExitSequenceStarted;
    private bool allowImmediateClose;
    private bool minimizeAnimationInProgress;
    private bool restoreAnimationInProgress;
    private bool launcherWasMinimized;
    private readonly Dictionary<int, IntPtr> fifaWindowsPresentBeforeLaunch = new();
    private DateTime gameLaunchDetectionStartedUtc;

    public MainWindow()
    {
        InitializeComponent();
        PopulateDailyDropReel(null);

        // Keep Aurora 21 above normal desktop windows for the entire launcher session.
        Topmost = true;

        // Keep slider change events quiet until persisted settings have been restored.
        mediaControlsReady = false;

        runtimeSnapshot = RuntimeCompatibility.Discover();
        runtimeStatusBaselineWriteUtc = GetRuntimeStatusWriteTimeUtc();
        ApplyRuntimeSnapshot(runtimeSnapshot);
        LoadSettings();

        pendingMusicVolume = MusicVolumeSlider.Value;
        musicVolumeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        musicVolumeSaveTimer.Tick += (_, _) =>
        {
            musicVolumeSaveTimer.Stop();
            PersistMusicSettings(pendingMusicVolume, musicMuted);
        };
        pendingSoundEffectsVolume = SoundEffectsVolumeSlider.Value;
        soundEffectsVolumeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        soundEffectsVolumeSaveTimer.Tick += (_, _) =>
        {
            soundEffectsVolumeSaveTimer.Stop();
            PersistSoundEffectsSettings(pendingSoundEffectsVolume, soundEffectsMuted);
        };
        mediaControlsReady = true;

        musicPlayer.Volume = MusicVolumeSlider.Value;
        musicPlayer.MediaOpened += (_, _) => Dispatcher.BeginInvoke(OnMusicMediaOpened);
        musicPlayer.MediaEnded += (_, _) => Dispatcher.BeginInvoke(PlayNextMusicTrack);
        musicPlayer.MediaFailed += (_, _) => Dispatcher.BeginInvoke(HandleMusicFailure);

        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statusTimer.Tick += (_, _) =>
        {
            RefreshRuntimeStatus();
            CheckForSuccessfulGameLaunch();
            UpdateDailyDropCountdown();
        };
        statusTimer.Start();
        banStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        banStatusTimer.Tick += (_, _) => RunBanStatusCheck();
        Loaded += async (_, _) =>
        {
            connectionLogReady = true;
            ResetConnectionStateForStartup();
            AddConnectionLog("Launcher started", "Aurora 21 is running fresh checks for this launcher session. Previous runtime status is not trusted until it is verified again.", ConnectionLogSeverity.Info);
            RunStartupStaticChecks();
            RunBanStatusCheck();
            banStatusTimer.Start();

            AttachButtonParticleHoverHandlers(this);
            cloudMediaSyncCancellation = new CancellationTokenSource();
            await RunInitialCloudMediaSyncAsync(cloudMediaSyncCancellation.Token);
            InitializeHeroVideo();
            PrepareStartupMusicFade();
            StartupMediaOverlay.Visibility = Visibility.Collapsed;
            RunPythonSetup(StartStartupConnectionCheck);
            PlayLauncherSound(CreateStartupElectronicStinger(), latencyMilliseconds: 90);
            await RunWelcomeSequenceAsync();
            await RunFirstLaunchShortcutPromptAsync();
            AnimateViewEntrance(OverviewView);
        };
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        Closed += (_, _) =>
        {
            statusTimer.Stop();
            banStatusTimer.Stop();
            musicVolumeSaveTimer.Stop();
            soundEffectsVolumeSaveTimer.Stop();
            PersistMusicSettings(MusicVolumeSlider.Value, musicMuted);
            PersistSoundEffectsSettings(SoundEffectsVolumeSlider.Value, soundEffectsMuted);
            musicTrackLoadCancellation?.Cancel();
            musicTrackLoadCancellation?.Dispose();
            musicGainWarmupCancellation?.Cancel();
            musicGainWarmupCancellation?.Dispose();
            cloudMediaSyncCancellation?.Cancel();
            cloudMediaSyncCancellation?.Dispose();
            try { HeroVideo.Stop(); } catch { }
            try { musicPlayer.Close(); } catch { }
            StopLauncherSounds();
        };
    }

    private void InitializeHeroVideo()
    {
        try { HeroVideo.Stop(); } catch { }
        HeroVideo.Visibility = Visibility.Collapsed;
        HeroVideoFallback.Visibility = Visibility.Visible;

        string? videoPath = FindHeroVideoPath();
        if (string.IsNullOrWhiteSpace(videoPath)) return;

        try
        {
            HeroVideo.Source = new Uri(videoPath, UriKind.Absolute);
            HeroVideo.Visibility = Visibility.Visible;
            HeroVideo.Play();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UriFormatException)
        {
            HeroVideo.Visibility = Visibility.Collapsed;
            HeroVideoFallback.Visibility = Visibility.Visible;
        }
    }

    private string? FindHeroVideoPath()
    {
        string? cloudVideo = CloudMediaManager.GetCachedVideoFiles().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(cloudVideo)) return cloudVideo;

        string sourcesFolder = IOPath.Combine(runtimeSnapshot.Root, "sources");
        if (!Directory.Exists(sourcesFolder)) return null;

        string[] preferredNames = { "background.mp4", "hero.mp4", "launcher.mp4" };
        foreach (string fileName in preferredNames)
        {
            string candidate = IOPath.Combine(sourcesFolder, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        string[] supportedExtensions = { ".mp4", ".m4v", ".wmv", ".avi", ".mov" };
        try
        {
            return Directory.EnumerateFiles(sourcesFolder)
                .Where(path => supportedExtensions.Contains(IOPath.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => IOPath.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void HeroVideo_MediaOpened(object sender, RoutedEventArgs e)
    {
        HeroVideoFallback.Visibility = Visibility.Collapsed;
        HeroVideo.Visibility = Visibility.Visible;
        HeroVideo.Play();
    }

    private void HeroVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        HeroVideo.Position = TimeSpan.Zero;
        HeroVideo.Play();
    }

    private void HeroVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        HeroVideo.Stop();
        HeroVideo.Visibility = Visibility.Collapsed;
        HeroVideoFallback.Visibility = Visibility.Visible;
    }

    private void PrepareStartupMusicFade()
    {
        startupMusicFadeCancellation?.Cancel();
        startupMusicFadeCancellation?.Dispose();
        startupMusicFadeCancellation = null;

        startupMusicFadePending = true;
        musicMediaIsOpen = false;
        welcomeMusicFadeFactor = 0;
        startupMusicOpened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        musicPlayer.Volume = 0;
    }

    private void InitializeMusic()
    {
        musicPlaylist.Clear();
        currentMusicIndex = -1;
        consecutiveMusicFailures = 0;
        musicIsPlaying = false;
        musicStartRequested = false;
        currentTrackGain = 1.0;
        LoadMusicGainCache();

        HashSet<string> discoveredMusic = new(StringComparer.OrdinalIgnoreCase);
        string sourcesFolder = IOPath.Combine(runtimeSnapshot.Root, "sources");
        if (Directory.Exists(sourcesFolder))
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(sourcesFolder, "*.mp3"))
                {
                    discoveredMusic.Add(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Keep the controls disabled if the media folder cannot be read.
            }
        }

        foreach (string path in CloudMediaManager.GetCachedMusicFiles())
        {
            discoveredMusic.Add(path);
        }
        musicPlaylist.AddRange(discoveredMusic.OrderBy(path => IOPath.GetFileName(path), StringComparer.OrdinalIgnoreCase));
        ShuffleMusicPlaylist();

        bool hasMusic = musicPlaylist.Count > 0;
        MusicPlayPauseButton.IsEnabled = hasMusic;
        MusicSkipButton.IsEnabled = musicPlaylist.Count > 1;
        MusicMuteButton.IsEnabled = hasMusic;
        MusicVolumeSlider.IsEnabled = hasMusic;
        MusicPlayerBorder.Opacity = hasMusic ? 1 : 0.48;
        UpdateMusicControls();

        if (hasMusic)
        {
            StartMusicTrack(0);
            StartMusicGainWarmup();
        }
    }

    private async Task SyncCloudMediaAsync(CancellationToken cancellationToken)
    {
        try
        {
            CloudMediaSyncResult result = await CloudMediaManager.SyncAsync(runtimeSnapshot.Root, cancellationToken);
            if (!result.Enabled)
            {
                if (secureConnectionVerified) WorkflowStatusText.Text = "Your access is ready.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                AddConnectionLog(
                    "Cloud media unavailable",
                    $"{result.Error}\nThe launcher will continue with {result.Available} cached or bundled media item(s).",
                    ConnectionLogSeverity.Warning);
                if (secureConnectionVerified) WorkflowStatusText.Text = "Connected securely. Using available launcher media.";
                return;
            }

            if (result.Changed)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    InitializeHeroVideo();
                    // Initial cloud sync completes before the welcome cue. Music
                    // is deliberately initialized later by RunWelcomeSequenceAsync
                    // so both audio paths do not compete for the device at startup.
                    if (!startupMusicFadePending)
                    {
                        InitializeMusic();
                    }
                });
            }

            if (result.Downloaded > 0)
            {
                AddConnectionLog(
                    "Cloud media updated",
                    $"Downloaded {result.Downloaded} item(s); {result.Available} cloud media item(s) are active.",
                    ConnectionLogSeverity.Success);
            }
            if (secureConnectionVerified)
            {
                WorkflowStatusText.Text = result.Downloaded > 0
                    ? $"Connected securely. {result.Downloaded} launcher media item(s) updated."
                    : "Connected securely. Launcher media is ready.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal during launcher shutdown.
        }
        catch (Exception exception)
        {
            AddConnectionLog(
                "Cloud media unavailable",
                $"{exception.GetType().Name}: {exception.Message}\nBundled and previously cached media remain available.",
                ConnectionLogSeverity.Warning);
            if (secureConnectionVerified) WorkflowStatusText.Text = "Connected securely. Using available launcher media.";
        }
    }

    private async Task RunInitialCloudMediaSyncAsync(CancellationToken cancellationToken)
    {
        Task syncTask = SyncCloudMediaAsync(cancellationToken);
        Task completed = await Task.WhenAny(syncTask, Task.Delay(TimeSpan.FromSeconds(8)));
        if (completed == syncTask)
        {
            await ObserveCloudMediaSyncAsync(syncTask, cancellationToken);
            return;
        }

        AddConnectionLog(
            "Launcher media continues in background",
            "The initial media check exceeded eight seconds. Aurora 21 is opening with cached or bundled media while the verified download continues.",
            ConnectionLogSeverity.Info);
        _ = ObserveCloudMediaSyncAsync(syncTask, cancellationToken);
    }

    private async Task ObserveCloudMediaSyncAsync(Task syncTask, CancellationToken cancellationToken)
    {
        try
        {
            await syncTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the launcher closes during a background media sync.
        }
        catch (Exception exception)
        {
            AddConnectionLog(
                "Launcher media check failed",
                $"{exception.GetType().Name}: {exception.Message}\nAurora 21 continued with cached or bundled media.",
                ConnectionLogSeverity.Warning);
        }
    }


    private void ShuffleMusicPlaylist()
    {
        // Shuffle once per launcher session. The resulting order is then kept
        // unchanged, so reaching the end loops back to track 0 in the same order.
        for (int i = musicPlaylist.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (musicPlaylist[i], musicPlaylist[j]) = (musicPlaylist[j], musicPlaylist[i]);
        }
    }

    private async void StartMusicTrack(int index)
    {
        if (musicPlaylist.Count == 0) return;

        int requestedIndex = (index % musicPlaylist.Count + musicPlaylist.Count) % musicPlaylist.Count;
        string requestedPath = musicPlaylist[requestedIndex];
        int requestVersion = ++musicTrackRequestVersion;

        musicTrackLoadCancellation?.Cancel();
        musicTrackLoadCancellation?.Dispose();
        musicTrackLoadCancellation = new CancellationTokenSource();
        CancellationToken token = musicTrackLoadCancellation.Token;

        currentMusicIndex = requestedIndex;
        musicStartRequested = true;
        musicIsPlaying = false;
        musicMediaIsOpen = false;
        UpdateMusicControls();

        // The analysis reads only short representative windows, so an uncached song
        // normally adds only a very small delay before opening. Cached songs are instant.
        double gain = 1.0;
        try
        {
            gain = await GetOrAnalyzeMusicGainAsync(requestedPath, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Audio normalization must never make the launcher unable to play music.
            gain = 1.0;
        }

        if (token.IsCancellationRequested || requestVersion != musicTrackRequestVersion) return;
        currentTrackGain = Math.Clamp(gain, 0.25, 2.0);

        try
        {
            musicPlayer.Open(new Uri(requestedPath, UriKind.Absolute));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UriFormatException)
        {
            HandleMusicFailure();
        }
    }

    private void OnMusicMediaOpened()
    {
        consecutiveMusicFailures = 0;
        musicMediaIsOpen = true;

        // During startup, every newly opened track begins at the current fade
        // level. The first successful track therefore always starts silently,
        // even if decoding/opening the MP3 took longer than expected.
        ApplyMusicVolume();
        musicPlayer.IsMuted = musicMuted || MusicVolumeSlider.Value <= 0.001;

        if (gameLaunchSequenceStarted)
        {
            musicStartRequested = false;
            musicIsPlaying = false;
            musicPlayer.Pause();
            musicPlayer.Volume = 0;
            UpdateMusicControls();
            return;
        }

        if (!musicStartRequested) return;
        musicStartRequested = false;
        musicPlayer.Play();
        musicIsPlaying = true;

        if (startupMusicFadePending)
        {
            startupMusicOpened.TrySetResult(true);
        }

        UpdateMusicControls();
    }

    private void PlayNextMusicTrack()
    {
        if (gameLaunchSequenceStarted || musicPlaylist.Count == 0) return;
        StartMusicTrack(currentMusicIndex + 1);
    }

    private void HandleMusicFailure()
    {
        musicStartRequested = false;
        musicIsPlaying = false;
        consecutiveMusicFailures++;

        if (musicPlaylist.Count > 1 && consecutiveMusicFailures < musicPlaylist.Count)
        {
            StartMusicTrack(currentMusicIndex + 1);
            return;
        }

        UpdateMusicControls();
    }

    private void MusicPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (musicPlaylist.Count == 0) return;

        if (musicIsPlaying)
        {
            musicPlayer.Pause();
            musicIsPlaying = false;
        }
        else if (currentMusicIndex >= 0 && !musicStartRequested)
        {
            musicPlayer.Play();
            musicIsPlaying = true;
        }
        else
        {
            StartMusicTrack(Math.Max(currentMusicIndex, 0));
        }

        UpdateMusicControls();
    }

    private void MusicSkipButton_Click(object sender, RoutedEventArgs e)
    {
        if (gameLaunchSequenceStarted || musicPlaylist.Count <= 1) return;

        try { musicPlayer.Stop(); } catch { }
        StartMusicTrack(currentMusicIndex + 1);
    }

    private void MusicMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (musicPlaylist.Count == 0) return;

        musicMuted = !musicMuted;
        musicPlayer.IsMuted = musicMuted || MusicVolumeSlider.Value <= 0.001;
        UpdateMusicControls();

        // Mute is an independent preference from the volume slider. Persist it
        // immediately so reopening Aurora 21 restores the exact audible state.
        PersistMusicSettings(MusicVolumeSlider.Value, musicMuted);
    }

    private void MusicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!mediaControlsReady) return;

        ApplyMusicVolume(e.NewValue);
        if (e.NewValue <= 0.001)
        {
            musicMuted = true;
        }
        else if (musicMuted)
        {
            // Moving the volume slider back above zero behaves like iOS/Windows
            // media controls and automatically unmutes playback.
            musicMuted = false;
        }

        musicPlayer.IsMuted = musicMuted;
        UpdateMusicControls();
        QueueMusicVolumeSave(e.NewValue);
    }

    private void QueueMusicVolumeSave(double value)
    {
        if (!mediaControlsReady) return;

        pendingMusicVolume = Math.Clamp(value, 0, 1);
        musicVolumeSaveTimer.Stop();
        musicVolumeSaveTimer.Start();
    }

    private void PersistMusicSettings(double value, bool muted)
    {
        try
        {
            RuntimeCompatibility.SaveMusicSettings(
                runtimeSnapshot.Root,
                Math.Clamp(value, 0, 1),
                muted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AddConnectionLog(
                "Could not save music settings",
                $"{exception.GetType().Name}: {exception.Message}",
                ConnectionLogSeverity.Warning);
        }
    }

    private void SoundEffectsVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!mediaControlsReady) return;

        if (e.NewValue <= 0.001)
        {
            soundEffectsMuted = true;
            SoundEffectsMuteCheckBox.IsChecked = true;
        }
        else if (soundEffectsMuted)
        {
            soundEffectsMuted = false;
            SoundEffectsMuteCheckBox.IsChecked = false;
        }

        pendingSoundEffectsVolume = Math.Clamp(e.NewValue, 0, 1);
        soundEffectsVolumeSaveTimer.Stop();
        soundEffectsVolumeSaveTimer.Start();
    }

    private void SoundEffectsMuteCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!mediaControlsReady) return;

        soundEffectsMuted = SoundEffectsMuteCheckBox.IsChecked == true;
        PersistSoundEffectsSettings(SoundEffectsVolumeSlider.Value, soundEffectsMuted);
    }

    private void PersistSoundEffectsSettings(double value, bool muted)
    {
        try
        {
            RuntimeCompatibility.SaveSoundEffectsSettings(
                runtimeSnapshot.Root,
                Math.Clamp(value, 0, 1),
                muted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AddConnectionLog(
                "Could not save sound-effect settings",
                $"{exception.GetType().Name}: {exception.Message}",
                ConnectionLogSeverity.Warning);
        }
    }

    private void ApplyMusicVolume(double? sliderOverride = null)
    {
        if (!mediaControlsReady) return;

        double masterVolume = Math.Clamp(sliderOverride ?? MusicVolumeSlider.Value, 0, 1);
        double fadeFactor = startupMusicFadePending ? welcomeMusicFadeFactor : 1.0;
        musicPlayer.Volume = Math.Clamp(masterVolume * currentTrackGain * fadeFactor, 0, 1);
    }

    private string GetMusicGainCachePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return IOPath.Combine(localAppData, "Project21", "music-gain-cache-v1.json");
    }

    private void LoadMusicGainCache()
    {
        musicGainCache.Clear();
        string cachePath = GetMusicGainCachePath();
        if (!File.Exists(cachePath)) return;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(cachePath));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("version", out JsonElement versionElement) ||
                versionElement.GetInt32() != MusicGainCacheVersion ||
                !root.TryGetProperty("tracks", out JsonElement tracksElement) ||
                tracksElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty property in tracksElement.EnumerateObject())
            {
                JsonElement value = property.Value;
                if (!value.TryGetProperty("length", out JsonElement lengthElement) ||
                    !value.TryGetProperty("lastWriteUtcTicks", out JsonElement writeElement) ||
                    !value.TryGetProperty("gain", out JsonElement gainElement))
                {
                    continue;
                }

                double gain = gainElement.GetDouble();
                if (!double.IsFinite(gain) || gain <= 0) continue;

                musicGainCache[property.Name] = new MusicGainCacheEntry
                {
                    Length = lengthElement.GetInt64(),
                    LastWriteUtcTicks = writeElement.GetInt64(),
                    Gain = Math.Clamp(gain, 0.25, 2.0)
                };
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            musicGainCache.Clear();
        }
    }

    private void SaveMusicGainCache()
    {
        try
        {
            string cachePath = GetMusicGainCachePath();
            string? directory = IOPath.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var payload = new
            {
                version = MusicGainCacheVersion,
                tracks = musicGainCache
            };
            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            string tempPath = cachePath + ".tmp";
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, cachePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Gain matching remains session-local if the cache cannot be persisted.
        }
    }

    private bool TryGetCachedMusicGain(string path, out double gain)
    {
        gain = 1.0;
        try
        {
            FileInfo info = new(path);
            string key = IOPath.GetFullPath(path);
            if (!musicGainCache.TryGetValue(key, out MusicGainCacheEntry? entry)) return false;
            if (entry.Length != info.Length || entry.LastWriteUtcTicks != info.LastWriteTimeUtc.Ticks)
            {
                musicGainCache.Remove(key);
                return false;
            }

            gain = Math.Clamp(entry.Gain, 0.25, 2.0);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task<double> GetOrAnalyzeMusicGainAsync(string path, CancellationToken token)
    {
        if (TryGetCachedMusicGain(path, out double cachedGain)) return cachedGain;

        double measuredGain = await Task.Run(() => AnalyzeMusicGain(path, token), token);
        token.ThrowIfCancellationRequested();

        try
        {
            FileInfo info = new(path);
            musicGainCache[IOPath.GetFullPath(path)] = new MusicGainCacheEntry
            {
                Length = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Gain = measuredGain
            };
            SaveMusicGainCache();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Use the measured gain for this session even if metadata caching fails.
        }

        return measuredGain;
    }

    private static double AnalyzeMusicGain(string path, CancellationToken token)
    {
        using AudioFileReader reader = new(path);
        if (reader.TotalTime <= TimeSpan.Zero || reader.WaveFormat.SampleRate <= 0 || reader.WaveFormat.Channels <= 0)
        {
            return 1.0;
        }

        // Three windows spread through the song avoid intros/outros dominating the result.
        // Each window is gated in short blocks so silence does not make a quiet intro
        // artificially boost the whole track. This is deliberately a lightweight
        // loudness matcher rather than a mastering limiter.
        double[] positions = { 0.18, 0.52, 0.82 };
        const double windowSeconds = 8.0;
        const double gateDb = -45.0;
        const int blockFrames = 2048;

        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;
        int blockSamples = Math.Max(channels, blockFrames * channels);
        float[] buffer = new float[blockSamples];

        double acceptedSquareSum = 0;
        long acceptedSampleCount = 0;

        foreach (double position in positions)
        {
            token.ThrowIfCancellationRequested();
            double durationSeconds = reader.TotalTime.TotalSeconds;
            double maxStart = Math.Max(0, durationSeconds - windowSeconds);
            double startSeconds = Math.Clamp(durationSeconds * position, 0, maxStart);
            reader.CurrentTime = TimeSpan.FromSeconds(startSeconds);

            long samplesRemaining = (long)(windowSeconds * sampleRate * channels);
            while (samplesRemaining > 0)
            {
                token.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(buffer.Length, samplesRemaining);
                int read = reader.Read(buffer, 0, wanted);
                if (read <= 0) break;
                samplesRemaining -= read;

                double square = 0;
                for (int i = 0; i < read; i++)
                {
                    double sample = buffer[i];
                    square += sample * sample;
                }

                if (square <= 0) continue;
                double blockMeanSquare = square / read;
                double blockDb = 10.0 * Math.Log10(blockMeanSquare);
                if (blockDb < gateDb) continue;

                acceptedSquareSum += square;
                acceptedSampleCount += read;
            }
        }

        if (acceptedSampleCount == 0 || acceptedSquareSum <= 0) return 1.0;

        double meanSquare = acceptedSquareSum / acceptedSampleCount;
        double measuredDb = 10.0 * Math.Log10(meanSquare);
        if (!double.IsFinite(measuredDb)) return 1.0;

        double requestedGainDb = Math.Clamp(TargetMusicRmsDb - measuredDb, MinTrackGainDb, MaxTrackGainDb);
        double gain = Math.Pow(10.0, requestedGainDb / 20.0);
        return Math.Clamp(gain, 0.25, 2.0);
    }

    private void StartMusicGainWarmup()
    {
        musicGainWarmupCancellation?.Cancel();
        musicGainWarmupCancellation?.Dispose();
        musicGainWarmupCancellation = new CancellationTokenSource();
        CancellationToken token = musicGainWarmupCancellation.Token;

        _ = Task.Run(async () =>
        {
            // Analyze the shuffled queue sequentially to keep CPU usage modest.
            // The currently requested track already gets priority through StartMusicTrack.
            foreach (string path in musicPlaylist.ToArray())
            {
                if (token.IsCancellationRequested) break;

                bool isCurrent = await Dispatcher.InvokeAsync(() =>
                    currentMusicIndex >= 0 &&
                    currentMusicIndex < musicPlaylist.Count &&
                    string.Equals(musicPlaylist[currentMusicIndex], path, StringComparison.OrdinalIgnoreCase));
                if (isCurrent) continue;

                bool alreadyCached = await Dispatcher.InvokeAsync(() => TryGetCachedMusicGain(path, out _));
                if (alreadyCached) continue;

                try
                {
                    double gain = AnalyzeMusicGain(path, token);
                    token.ThrowIfCancellationRequested();
                    FileInfo info = new(path);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        musicGainCache[IOPath.GetFullPath(path)] = new MusicGainCacheEntry
                        {
                            Length = info.Length,
                            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                            Gain = gain
                        };
                        SaveMusicGainCache();
                    });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
                {
                    // A single unreadable MP3 should not stop the rest of the playlist.
                }
            }
        }, token);
    }

    private void UpdateMusicControls()
    {
        if (!mediaControlsReady) return;

        MusicPlayPauseButton.Content = musicIsPlaying ? "Ⅱ" : "▶";
        bool effectivelyMuted = musicMuted || MusicVolumeSlider.Value <= 0.001;
        MusicMuteButton.Content = effectivelyMuted ? "🔇" : "🔊";
    }

    private async Task RunWelcomeSequenceAsync()
    {
        WelcomeOverlay.Visibility = Visibility.Visible;
        WelcomeOverlay.Opacity = 1;
        WelcomeContent.Opacity = 0;
        WelcomeScale.ScaleX = 0.93;
        WelcomeScale.ScaleY = 0.93;

        // Playback is prepared at zero volume. FadeInWelcomeMusicAsync waits for
        // the MP3 to ACTUALLY open before starting its clock, preventing a late
        // MediaOpened event from skipping most of the audible fade.
        welcomeMusicFadeFactor = 0;
        musicPlayer.Volume = 0;

        CubicEase easeOut = new() { EasingMode = EasingMode.EaseOut };
        DoubleAnimation contentFade = new(0, 1, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        DoubleAnimation scaleX = new(0.93, 1, TimeSpan.FromMilliseconds(760))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        DoubleAnimation scaleY = new(0.93, 1, TimeSpan.FromMilliseconds(760))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };

        WelcomeContent.BeginAnimation(UIElement.OpacityProperty, contentFade);
        WelcomeScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        WelcomeScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);

        // Intentionally longer than the visual Welcome transition. The song
        // begins rising while Welcome is visible, then gently settles at 12%
        // as the main launcher becomes usable.
        // Finish the one-shot welcome cue before decoding the first MP3 and
        // opening another Windows audio path. Running both initializations at
        // once caused brief crackles on some USB and Bluetooth audio drivers.
        await Task.Delay(1250);
        InitializeMusic();
        _ = FadeInWelcomeMusicAsync(TimeSpan.FromMilliseconds(3600));

        await Task.Delay(500);

        QuadraticEase easeIn = new() { EasingMode = EasingMode.EaseIn };
        DoubleAnimation overlayFade = new(1, 0, TimeSpan.FromMilliseconds(560))
        {
            EasingFunction = easeIn,
            FillBehavior = FillBehavior.Stop
        };

        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        overlayFade.Completed += (_, _) => completion.TrySetResult(true);
        WelcomeOverlay.BeginAnimation(UIElement.OpacityProperty, overlayFade);
        await completion.Task;

        WelcomeOverlay.Opacity = 0;
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        // Do not force the volume to 12% here. The independent startup fade
        // continues smoothly after the overlay disappears until it reaches
        // the slider's target value.
    }

    private string GetFirstRunStatePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return IOPath.Combine(localAppData, "Project21", "launcher-first-run-v1.complete");
    }

    private bool HasCompletedFirstRunShortcutPrompt()
    {
        try
        {
            return File.Exists(GetFirstRunStatePath());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AddConnectionLog(
                "Could not read first-run state",
                $"State file: {GetFirstRunStatePath()}\n{exception.GetType().Name}: {exception.Message}\nThe shortcut prompt may appear again on the next launch.",
                ConnectionLogSeverity.Warning);
            return false;
        }
    }

    private async Task RunFirstLaunchShortcutPromptAsync()
    {
        if (HasCompletedFirstRunShortcutPrompt())
        {
            return;
        }

        firstRunShortcutPromptCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        firstRunShortcutChoiceInProgress = false;

        FirstRunShortcutStatusText.Text = "Choose where you want quick access to Aurora 21.";
        FirstRunShortcutStatusText.Foreground = new SolidColorBrush(Color.FromRgb(168, 181, 194));
        FirstRunShortcutOverlay.Visibility = Visibility.Visible;
        FirstRunShortcutOverlay.Opacity = 0;
        FirstRunShortcutContent.Opacity = 0;
        FirstRunShortcutScale.ScaleX = 0.95;
        FirstRunShortcutScale.ScaleY = 0.95;

        CubicEase easeOut = new() { EasingMode = EasingMode.EaseOut };
        FirstRunShortcutOverlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });
        FirstRunShortcutContent.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(430))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });
        FirstRunShortcutScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });
        FirstRunShortcutScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        });

        await firstRunShortcutPromptCompletion.Task;

        TaskCompletionSource<bool> fadeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DoubleAnimation fade = new(1, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd
        };
        fade.Completed += (_, _) => fadeCompletion.TrySetResult(true);
        FirstRunShortcutOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
        await fadeCompletion.Task;

        FirstRunShortcutOverlay.Visibility = Visibility.Collapsed;
        FirstRunShortcutOverlay.Opacity = 0;
    }

    private async void FirstRunDesktopAndStartButton_Click(object sender, RoutedEventArgs e)
    {
        await CompleteFirstRunShortcutChoiceAsync(createDesktop: true, createStartMenu: true, choiceLabel: "Desktop + Start Menu");
    }

    private async void FirstRunStartMenuOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        await CompleteFirstRunShortcutChoiceAsync(createDesktop: false, createStartMenu: true, choiceLabel: "Start Menu only");
    }

    private async void FirstRunNoButton_Click(object sender, RoutedEventArgs e)
    {
        await CompleteFirstRunShortcutChoiceAsync(createDesktop: false, createStartMenu: false, choiceLabel: "No shortcuts");
    }

    private async Task CompleteFirstRunShortcutChoiceAsync(bool createDesktop, bool createStartMenu, string choiceLabel)
    {
        if (firstRunShortcutChoiceInProgress)
        {
            return;
        }

        firstRunShortcutChoiceInProgress = true;
        SetFirstRunShortcutButtonsEnabled(false);

        bool shortcutSucceeded = true;
        string? failureDetail = null;

        try
        {
            if (createDesktop || createStartMenu)
            {
                FirstRunShortcutStatusText.Text = "Creating shortcuts…";
                string launcherExecutable = ResolveLauncherExecutablePath();

                if (createDesktop)
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    string desktopShortcut = IOPath.Combine(desktop, "Aurora 21 (Alpha v1).lnk");
                    await CreateWindowsShortcutAsync(desktopShortcut, launcherExecutable);
                }

                if (createStartMenu)
                {
                    string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
                    string programsFolder = IOPath.Combine(startMenu, "Programs");
                    string startMenuShortcut = IOPath.Combine(programsFolder, "Aurora 21 (Alpha v1).lnk");
                    await CreateWindowsShortcutAsync(startMenuShortcut, launcherExecutable);
                }

                FirstRunShortcutStatusText.Text = createDesktop
                    ? "Desktop and Start Menu shortcuts added."
                    : "Start Menu shortcut added.";
                FirstRunShortcutStatusText.Foreground = new SolidColorBrush(Color.FromRgb(121, 221, 174));
                AddConnectionLog(
                    "Launcher shortcuts created",
                    $"Choice: {choiceLabel}\nTarget: {launcherExecutable}",
                    ConnectionLogSeverity.Success);
            }
            else
            {
                FirstRunShortcutStatusText.Text = "No shortcuts added.";
                AddConnectionLog("First-run shortcuts skipped", "The user chose not to create launcher shortcuts.", ConnectionLogSeverity.Info);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            shortcutSucceeded = false;
            failureDetail = $"{exception.GetType().Name}: {exception.Message}";
            FirstRunShortcutStatusText.Text = "Could not create the shortcut. See Connection for details.";
            FirstRunShortcutStatusText.Foreground = new SolidColorBrush(Color.FromRgb(244, 166, 172));
            AddConnectionLog(
                "Shortcut creation failed",
                $"Choice: {choiceLabel}\n{failureDetail}",
                ConnectionLogSeverity.Error);
        }

        // The prompt is deliberately one-time regardless of the selected option
        // (or a shortcut creation failure), matching the first-launch-only flow.
        try
        {
            MarkFirstRunShortcutPromptCompleted(choiceLabel, shortcutSucceeded, failureDetail);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AddConnectionLog(
                "Could not save first-run state",
                $"State file: {GetFirstRunStatePath()}\n{exception.GetType().Name}: {exception.Message}\nThe prompt may appear again next launch.",
                ConnectionLogSeverity.Warning);
        }

        await Task.Delay(shortcutSucceeded ? 700 : 1250);
        firstRunShortcutPromptCompletion?.TrySetResult(true);
    }

    private void SetFirstRunShortcutButtonsEnabled(bool enabled)
    {
        FirstRunDesktopAndStartButton.IsEnabled = enabled;
        FirstRunStartMenuOnlyButton.IsEnabled = enabled;
        FirstRunNoButton.IsEnabled = enabled;
    }

    private string ResolveLauncherExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(processPath))
        {
            return IOPath.GetFullPath(processPath);
        }

        string packagedLauncher = IOPath.Combine(AppContext.BaseDirectory, "Aurora21-Launcher.exe");
        if (File.Exists(packagedLauncher))
        {
            return IOPath.GetFullPath(packagedLauncher);
        }

        throw new FileNotFoundException("Aurora21-Launcher.exe could not be resolved for shortcut creation.", packagedLauncher);
    }

    private async Task CreateWindowsShortcutAsync(string shortcutPath, string launcherExecutable)
    {
        string? shortcutDirectory = IOPath.GetDirectoryName(shortcutPath);
        if (string.IsNullOrWhiteSpace(shortcutDirectory))
        {
            throw new InvalidOperationException($"Could not determine the shortcut directory for {shortcutPath}.");
        }

        Directory.CreateDirectory(shortcutDirectory);
        string workingDirectory = IOPath.GetDirectoryName(launcherExecutable) ?? AppContext.BaseDirectory;

        static string PsQuote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

        string script =
            "$ErrorActionPreference='Stop';" +
            "$shell=New-Object -ComObject WScript.Shell;" +
            $"$shortcut=$shell.CreateShortcut('{PsQuote(shortcutPath)}');" +
            $"$shortcut.TargetPath='{PsQuote(launcherExecutable)}';" +
            $"$shortcut.WorkingDirectory='{PsQuote(workingDirectory)}';" +
            $"$shortcut.IconLocation='{PsQuote(launcherExecutable)},0';" +
            "$shortcut.Description='Aurora 21 (Alpha v1) - FIFA 21 Revival Launcher';" +
            "$shortcut.Save();";

        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows PowerShell could not be started to create the shortcut.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (process.ExitCode != 0 || !File.Exists(shortcutPath))
        {
            string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(
                $"Shortcut creation failed for '{shortcutPath}' (PowerShell exit code {process.ExitCode}). " +
                (string.IsNullOrWhiteSpace(detail) ? "No additional PowerShell output was returned." : detail.Trim()));
        }
    }

    private void MarkFirstRunShortcutPromptCompleted(string choiceLabel, bool shortcutSucceeded, string? failureDetail)
    {
        string statePath = GetFirstRunStatePath();
        string? stateDirectory = IOPath.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            Directory.CreateDirectory(stateDirectory);
        }

        string state = JsonSerializer.Serialize(new
        {
            completed = true,
            completedUtc = DateTime.UtcNow,
            shortcutChoice = choiceLabel,
            shortcutSucceeded,
            failure = failureDetail
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(statePath, state, Encoding.UTF8);
    }

    private async Task FadeInWelcomeMusicAsync(TimeSpan duration)
    {
        if (musicPlaylist.Count == 0)
        {
            startupMusicFadePending = false;
            welcomeMusicFadeFactor = 1;
            return;
        }

        startupMusicFadeCancellation?.Cancel();
        startupMusicFadeCancellation?.Dispose();
        startupMusicFadeCancellation = new CancellationTokenSource();
        CancellationToken token = startupMusicFadeCancellation.Token;

        try
        {
            // Wait for MediaOpened/Play so the audible fade always starts from
            // zero. A generous timeout prevents a corrupt MP3 from holding the
            // startup fade state forever.
            if (!musicMediaIsOpen)
            {
                Task timeout = Task.Delay(TimeSpan.FromSeconds(8), token);
                Task completed = await Task.WhenAny(startupMusicOpened.Task, timeout);
                if (completed != startupMusicOpened.Task || token.IsCancellationRequested)
                {
                    startupMusicFadePending = false;
                    welcomeMusicFadeFactor = 1;
                    return;
                }
            }

            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed < duration)
            {
                token.ThrowIfCancellationRequested();

                double progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
                // Smoothstep gives a soft, professional rise with no sudden
                // jump at either end of the fade.
                welcomeMusicFadeFactor = progress * progress * (3 - (2 * progress));

                if (!musicMuted && musicIsPlaying)
                {
                    ApplyMusicVolume();
                }

                await Task.Delay(40, token);
            }

            welcomeMusicFadeFactor = 1;
            startupMusicFadePending = false;
            if (!musicMuted && musicIsPlaying)
            {
                ApplyMusicVolume();
            }
        }
        catch (OperationCanceledException)
        {
            // A post-launch fade-out or window close can intentionally cancel
            // the startup fade. In that case, do not fight the newer volume
            // transition.
        }
    }

    private void ArmGameLaunchDetection()
    {
        gameLaunchDetectionArmed = true;
        gameLaunchSequenceStarted = false;
        gameLaunchDetectionStartedUtc = DateTime.UtcNow;
        processEnumerationWarningLogged = false;
        fifaWindowsPresentBeforeLaunch.Clear();

        try
        {
            foreach (Process process in Process.GetProcessesByName("FIFA21"))
            {
                using (process)
                {
                    process.Refresh();
                    fifaWindowsPresentBeforeLaunch[process.Id] = process.MainWindowHandle;
                }
            }
        }
        catch (Exception exception)
        {
            // Failure to enumerate an already-running copy should not block launch,
            // but record it so the diagnostics page explains why detection may be less precise.
            AddConnectionLog(
                "Could not snapshot existing FIFA processes",
                $"Step: pre-launch process snapshot\nException: {exception.GetType().Name}\nMessage: {exception.Message}\nAurora 21 will continue and retry process detection after launch.",
                ConnectionLogSeverity.Warning);
        }
    }

    private void CheckForSuccessfulGameLaunch()
    {
        if (!gameLaunchDetectionArmed || gameLaunchSequenceStarted) return;

        // Do not leave detection armed forever after an abandoned/failed attempt.
        if (DateTime.UtcNow - gameLaunchDetectionStartedUtc > TimeSpan.FromMinutes(3))
        {
            gameLaunchDetectionArmed = false;
            RuntimeSnapshot timedOutSnapshot = RuntimeCompatibility.Discover();
            AddConnectionLog(
                "FIFA launch detection timed out",
                $"Step: waiting for the FIFA21.exe game window\nObserved result: no new visible FIFA21.exe window was confirmed within 3 minutes.\nRuntime game state: {timedOutSnapshot.GameState}\nRuntime connection state: {timedOutSnapshot.ConnectionState}\n\n{CollectRuntimeDiagnostics()}",
                ConnectionLogSeverity.Error);
            return;
        }

        // Require Aurora 21's runtime status to say the game is running as well
        // as a visible FIFA window. This prevents the launcher closing during an
        // early FIFA process bootstrap before the LocalFUT/trust setup is ready.
        RuntimeSnapshot latestRuntime = RuntimeCompatibility.Discover();
        if (!IsRunningState(latestRuntime.GameState)) return;

        try
        {
            foreach (Process process in Process.GetProcessesByName("FIFA21"))
            {
                using (process)
                {
                    if (process.HasExited) continue;
                    process.Refresh();

                    bool existedWithVisibleWindow =
                        fifaWindowsPresentBeforeLaunch.TryGetValue(process.Id, out IntPtr previousWindow) &&
                        previousWindow != IntPtr.Zero;
                    if (existedWithVisibleWindow) continue;

                    // MainWindowHandle becomes non-zero when the actual FIFA 21
                    // game window has appeared. Some EA/FIFA launch modes keep this
                    // value at zero for borderless DirectX windows, so a new or newly
                    // activated FIFA process that remains alive for eight seconds is
                    // also accepted after the live-process check above.
                    bool launchStable = DateTime.UtcNow - gameLaunchDetectionStartedUtc >= TimeSpan.FromSeconds(8);
                    if (process.MainWindowHandle != IntPtr.Zero || launchStable)
                    {
                        gameLaunchDetectionArmed = false;
                        gameLaunchSequenceStarted = true;
                        AddConnectionLog(
                            "FIFA 21 is fully launched",
                            $"A new or newly activated FIFA21.exe process is running and ready.\nProcess ID: {process.Id}\nWindow handle: 0x{process.MainWindowHandle.ToInt64():X}\nStable fallback used: {process.MainWindowHandle == IntPtr.Zero}\nRuntime state: {latestRuntime.GameState}",
                            ConnectionLogSeverity.Success);
                        _ = RunGameLaunchedSequenceAsync();
                        return;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            // Process enumeration can briefly fail while FIFA is starting.
            // The one-second timer will simply try again on the next tick.
            if (!processEnumerationWarningLogged)
            {
                processEnumerationWarningLogged = true;
                AddConnectionLog(
                    "FIFA process check was temporarily unavailable",
                    $"Step: enumerate FIFA21.exe processes\nException: {exception.GetType().Name}\nMessage: {exception.Message}\nAurora 21 will retry automatically every second.",
                    ConnectionLogSeverity.Warning);
            }
        }
    }

    private async Task RunGameLaunchedSequenceAsync()
    {
        await RunExitOverlaySequenceAsync(
            title: "Game launched, have fun",
            status: "FIFA 21 is ready");
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        banStatusTimer.Stop();

        // Build smoke tests and the end of an already-running exit animation must
        // be able to close immediately. Normal user closes get the goodbye screen.
        bool smokeTest = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase));

        if (allowImmediateClose || smokeTest)
        {
            return;
        }

        // The successful FIFA launch sequence already owns the close animation.
        // Ignore extra Alt+F4 / X presses until that sequence finishes.
        if (gameLaunchSequenceStarted)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        if (manualExitSequenceStarted)
        {
            return;
        }

        manualExitSequenceStarted = true;
        _ = RunManualExitSequenceAsync();
    }

    private async Task RunManualExitSequenceAsync()
    {
        gameLaunchDetectionArmed = false;
        AddConnectionLog(
            "Launcher closing",
            "Aurora 21 is showing the goodbye screen and fading the music before it exits.",
            ConnectionLogSeverity.Info);

        await RunExitOverlaySequenceAsync(
            title: "Bye!",
            status: "Aurora 21 is closing");
    }

    private async Task RunExitOverlaySequenceAsync(string title, string status)
    {
        // If the user closes while a restore animation is still settling, return the
        // launcher shell to its normal geometry before showing the full-window exit UI.
        ResetLauncherShellVisuals();

        SetStartControlsEnabled(false);
        StopButton.IsEnabled = false;
        SetupWireGuardButton.IsEnabled = false;
        CheckWireGuardButton.IsEnabled = false;

        GameLaunchedTitleText.Text = title;
        GameLaunchedStatusText.Text = status;
        GameLaunchedOverlay.Visibility = Visibility.Visible;
        PlayLauncherSound(CreateExitElectronicStinger());
        GameLaunchedOverlay.Opacity = 0;
        GameLaunchedContent.Opacity = 0;
        GameLaunchedScale.ScaleX = 0.94;
        GameLaunchedScale.ScaleY = 0.94;

        // Use the exact same music fade for a successful game launch and for a
        // normal user close, so both exits feel identical.
        Task musicFade = FadeOutMusicAsync(TimeSpan.FromMilliseconds(850));

        CubicEase easeOut = new() { EasingMode = EasingMode.EaseOut };
        DoubleAnimation overlayFade = new(0, 1, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        DoubleAnimation contentFade = new(0, 1, TimeSpan.FromMilliseconds(460))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        DoubleAnimation scaleX = new(0.94, 1, TimeSpan.FromMilliseconds(560))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };
        DoubleAnimation scaleY = new(0.94, 1, TimeSpan.FromMilliseconds(560))
        {
            EasingFunction = easeOut,
            FillBehavior = FillBehavior.HoldEnd
        };

        GameLaunchedOverlay.BeginAnimation(UIElement.OpacityProperty, overlayFade);
        GameLaunchedContent.BeginAnimation(UIElement.OpacityProperty, contentFade);
        GameLaunchedScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        GameLaunchedScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);

        await musicFade;
        await Task.Delay(1250);

        QuadraticEase easeIn = new() { EasingMode = EasingMode.EaseIn };
        DoubleAnimation windowFade = new(Opacity, 0, TimeSpan.FromMilliseconds(650))
        {
            EasingFunction = easeIn,
            FillBehavior = FillBehavior.HoldEnd
        };
        TaskCompletionSource<bool> completion = new();
        windowFade.Completed += (_, _) => completion.TrySetResult(true);
        BeginAnimation(UIElement.OpacityProperty, windowFade);
        await completion.Task;

        Opacity = 0;
        allowImmediateClose = true;
        Close();
    }

    private async Task FadeOutMusicAsync(TimeSpan duration)
    {
        startupMusicFadeCancellation?.Cancel();
        startupMusicFadePending = false;
        double startVolume = Math.Clamp(musicPlayer.Volume, 0, 1);

        // Paused, muted, zero-volume, or unavailable music needs no audible fade.
        // We still set the player to zero so a pending MediaOpened cannot become
        // audible while the success screen is visible.
        if (!musicIsPlaying || musicMuted || startVolume <= 0.001)
        {
            musicPlayer.Volume = 0;
            musicStartRequested = false;
            return;
        }

        const int steps = 28;
        int delayMs = Math.Max(1, (int)(duration.TotalMilliseconds / steps));
        for (int i = 1; i <= steps; i++)
        {
            double progress = (double)i / steps;
            double eased = progress * progress;
            musicPlayer.Volume = Math.Max(0, startVolume * (1 - eased));
            await Task.Delay(delayMs);
        }

        musicPlayer.Volume = 0;
        musicPlayer.Pause();
        musicIsPlaying = false;
        musicStartRequested = false;
        UpdateMusicControls();
    }

    private void AttachButtonParticleHoverHandlers(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is WpfButton button)
            {
                button.MouseEnter -= Button_MouseEnter;
                button.MouseEnter += Button_MouseEnter;
            }

            AttachButtonParticleHoverHandlers(child);
        }
    }

    private void Button_MouseEnter(object sender, WpfMouseEventArgs e)
    {
        if (sender is not WpfButton button || !button.IsEnabled || button.ActualWidth < 8 || button.ActualHeight < 8)
        {
            return;
        }

        SpawnButtonHoverParticles(button);
    }

    private void SpawnButtonHoverParticles(WpfButton button)
    {
        try
        {
            GeneralTransform transform = button.TransformToVisual(ButtonParticleLayer);
            Point topLeft = transform.Transform(new Point(0, 0));
            double width = button.ActualWidth;
            double height = button.ActualHeight;

            // A restrained one-shot shimmer: tiny vector particles drift away from
            // the edge once per hover entry instead of continuously spraying.
            const int particleCount = 6;
            for (int i = 0; i < particleCount; i++)
            {
                double size = 1.2 + Random.Shared.NextDouble() * 1.4;
                double edgePosition = Random.Shared.NextDouble();
                bool horizontalEdge = i % 2 == 0;
                bool farEdge = i % 3 == 0;

                double x;
                double y;
                if (horizontalEdge)
                {
                    x = topLeft.X + 8 + edgePosition * Math.Max(1, width - 16);
                    y = topLeft.Y + (farEdge ? height - 2 : 1);
                }
                else
                {
                    x = topLeft.X + (farEdge ? width - 2 : 1);
                    y = topLeft.Y + 7 + edgePosition * Math.Max(1, height - 14);
                }

                Ellipse particle = new()
                {
                    Width = size,
                    Height = size,
                    Opacity = 0,
                    IsHitTestVisible = false,
                    Fill = new SolidColorBrush(Color.FromArgb(220, 202, 239, 255)),
                    Effect = new DropShadowEffect
                    {
                        BlurRadius = 3.5,
                        ShadowDepth = 0,
                        Opacity = 0.55,
                        Color = Color.FromRgb(170, 229, 255)
                    }
                };

                TranslateTransform drift = new();
                particle.RenderTransform = drift;
                Canvas.SetLeft(particle, x - size / 2);
                Canvas.SetTop(particle, y - size / 2);
                ButtonParticleLayer.Children.Add(particle);

                double outwardX = horizontalEdge
                    ? (Random.Shared.NextDouble() - 0.5) * 8
                    : (farEdge ? 6 : -6) + (Random.Shared.NextDouble() - 0.5) * 3;
                double outwardY = horizontalEdge
                    ? (farEdge ? 6 : -6) + (Random.Shared.NextDouble() - 0.5) * 3
                    : (Random.Shared.NextDouble() - 0.5) * 8;
                int durationMs = 520 + Random.Shared.Next(0, 180);

                DoubleAnimation driftX = new(0, outwardX, TimeSpan.FromMilliseconds(durationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                DoubleAnimation driftY = new(0, outwardY, TimeSpan.FromMilliseconds(durationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                DoubleAnimationUsingKeyFrames opacity = new();
                opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.78, KeyTime.FromPercent(0.18), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
                opacity.Duration = TimeSpan.FromMilliseconds(durationMs);
                opacity.Completed += (_, _) => ButtonParticleLayer.Children.Remove(particle);

                drift.BeginAnimation(TranslateTransform.XProperty, driftX);
                drift.BeginAnimation(TranslateTransform.YProperty, driftY);
                particle.BeginAnimation(UIElement.OpacityProperty, opacity);
            }
        }
        catch (InvalidOperationException)
        {
            // Ignore transient visual-tree changes while switching launcher pages.
        }
    }

    private void AddConnectionLog(string title, string? detail = null, ConnectionLogSeverity severity = ConnectionLogSeverity.Info)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AddConnectionLog(title, detail, severity));
            return;
        }

        if (!connectionLogReady || ConnectionLogPanel is null) return;

        ConnectionLogEmptyState.Visibility = Visibility.Collapsed;

        (Color accentColor, Color backgroundColor, Color borderColor, string symbol) = severity switch
        {
            ConnectionLogSeverity.Success => (
                Color.FromRgb(108, 228, 171),
                Color.FromArgb(24, 66, 150, 111),
                Color.FromArgb(44, 108, 228, 171),
                "✓"),
            ConnectionLogSeverity.Warning => (
                Color.FromRgb(244, 199, 106),
                Color.FromArgb(23, 171, 126, 42),
                Color.FromArgb(42, 244, 199, 106),
                "!"),
            ConnectionLogSeverity.Error => (
                Color.FromRgb(255, 117, 128),
                Color.FromArgb(28, 170, 58, 70),
                Color.FromArgb(58, 255, 117, 128),
                "×"),
            _ => (
                Color.FromRgb(184, 232, 250),
                Color.FromArgb(15, 110, 158, 181),
                Color.FromArgb(30, 184, 232, 250),
                "•")
        };

        Border entry = new()
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(backgroundColor),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(1),
            Opacity = 0
        };

        Grid layout = new();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Border icon = new()
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(24, accentColor.R, accentColor.G, accentColor.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top
        };
        icon.Child = new TextBlock
        {
            Text = symbol,
            Foreground = new SolidColorBrush(accentColor),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        StackPanel text = new() { Margin = new Thickness(7, 0, 12, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromRgb(235, 242, 248)),
            FontSize = severity == ConnectionLogSeverity.Error ? 13 : 9.5,
            FontWeight = severity == ConnectionLogSeverity.Error ? FontWeights.Bold : FontWeights.SemiBold,
            LineHeight = severity == ConnectionLogSeverity.Error ? 18 : double.NaN,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(detail))
        {
            text.Children.Add(new TextBlock
            {
                Text = detail.Trim(),
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(severity == ConnectionLogSeverity.Error
                    ? Color.FromRgb(219, 185, 190)
                    : Color.FromRgb(145, 158, 171)),
                FontSize = severity == ConnectionLogSeverity.Error ? 11 : 8.4,
                LineHeight = severity == ConnectionLogSeverity.Error ? 17 : 13.5,
                FontWeight = severity == ConnectionLogSeverity.Error ? FontWeights.Medium : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap
            });
        }

        TextBlock timestamp = new()
        {
            Text = DateTime.Now.ToString("HH:mm:ss"),
            Foreground = new SolidColorBrush(Color.FromRgb(98, 111, 124)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 7.4,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 2, 0, 0)
        };

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(timestamp, 2);
        layout.Children.Add(icon);
        layout.Children.Add(text);
        layout.Children.Add(timestamp);
        entry.Child = layout;

        ConnectionLogPanel.Children.Add(entry);
        while (ConnectionLogPanel.Children.Count > 100)
        {
            ConnectionLogPanel.Children.RemoveAt(0);
        }

        ConnectionLogCountText.Text = $"LIVE • {ConnectionLogPanel.Children.Count} EVENTS";

        DoubleAnimation fade = new(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) => entry.Opacity = 1;
        entry.BeginAnimation(UIElement.OpacityProperty, fade);

        Dispatcher.BeginInvoke(() => ConnectionLogScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void LogRuntimeSnapshotChanges(RuntimeSnapshot snapshot, bool force = false)
    {
        if (!connectionLogReady) return;

        string gameState = CondenseState(snapshot.GameState);
        bool runtimeStatusFresh = RuntimeStatusWasUpdatedThisSession();
        string connectionState = secureConnectionVerified
            ? "Connected securely"
            : !snapshot.IsCompatible
                ? "Required player connection files are missing."
                : wireGuardProcess is { HasExited: false }
                    ? "Checking secure route…"
                    : runtimeStatusFresh
                        ? CondenseState(snapshot.ConnectionState)
                        : "Not verified this session";

        ConnectionPageGameText.Text = gameState;
        ConnectionPageNetworkText.Text = CondenseHeroState(connectionState);

        if (force || !string.Equals(lastLoggedGameState, gameState, StringComparison.Ordinal))
        {
            lastLoggedGameState = gameState;
            AddConnectionLog(
                IsRunningState(snapshot.GameState) ? "FIFA 21 is running" : "FIFA 21 is not running",
                IsRunningState(snapshot.GameState)
                    ? gameState
                    : "No live FIFA21.exe process is currently detected.",
                IsRunningState(snapshot.GameState) ? ConnectionLogSeverity.Success : ConnectionLogSeverity.Info);
        }

        if (force || !string.Equals(lastLoggedConnectionState, connectionState, StringComparison.Ordinal))
        {
            lastLoggedConnectionState = connectionState;

            if (secureConnectionVerified)
            {
                AddConnectionLog(
                    "Secure connection is ready",
                    "Discord authorization, WireGuard and the authenticated LocalFUT gateway passed the current-session check.",
                    ConnectionLogSeverity.Success);
            }
            else if (runtimeStatusFresh)
            {
                AddConnectionLog(
                    IsConnectedState(snapshot.ConnectionState) ? "Runtime route is active" : "Runtime route state changed",
                    connectionState,
                    IsConnectedState(snapshot.ConnectionState) ? ConnectionLogSeverity.Success : ConnectionLogSeverity.Info);
            }
            else if (force)
            {
                AddConnectionLog(
                    "Secure connection not verified yet",
                    "Persisted runtime status from an earlier session is being ignored until Aurora 21 completes a fresh connection check.",
                    ConnectionLogSeverity.Info);
            }
        }

        if (force || lastLoggedCompatibility != snapshot.IsCompatible)
        {
            lastLoggedCompatibility = snapshot.IsCompatible;
            if (!snapshot.IsCompatible)
            {
                AddConnectionLog(
                    "Launcher files are incomplete",
                    "RuntimeCompatibility reported that required player connection files are missing from the launcher folder.",
                    ConnectionLogSeverity.Error);
            }
        }
    }

    private string BuildProcessFailureDetail(ProcessDiagnosticCapture capture, int exitCode, bool includeRuntimeDiagnostics = false)
    {
        StringBuilder detail = new();
        detail.AppendLine($"Step: {capture.StageName}");
        if (!string.IsNullOrWhiteSpace(capture.ScriptPath))
        {
            detail.AppendLine($"Script: {capture.ScriptPath}");
        }

        detail.AppendLine($"Observed result: process exited with code {exitCode}.");

        string[] errors = capture.GetErrors();
        string[] output = capture.GetOutput();

        if (errors.Length > 0)
        {
            detail.AppendLine();
            detail.AppendLine("Why Aurora 21 marked this as failed: the step returned a non-zero exit code. Recent stderr:");
            foreach (string line in errors.TakeLast(12))
            {
                detail.AppendLine("  " + line);
            }
        }
        else
        {
            detail.AppendLine();
            detail.AppendLine("Why Aurora 21 marked this as failed: the step returned a non-zero exit code but did not provide a specific stderr message.");
        }

        if (output.Length > 0)
        {
            detail.AppendLine();
            detail.AppendLine("Recent output:");
            foreach (string line in output.TakeLast(10))
            {
                detail.AppendLine("  " + line);
            }
        }

        if (includeRuntimeDiagnostics)
        {
            string runtimeDiagnostics = CollectRuntimeDiagnostics();
            if (!string.IsNullOrWhiteSpace(runtimeDiagnostics))
            {
                detail.AppendLine();
                detail.Append(runtimeDiagnostics);
            }
        }

        return detail.ToString().Trim();
    }

    private string CollectRuntimeDiagnostics()
    {
        string runtimeFolder = IOPath.Combine(runtimeSnapshot.Root, "runtime");
        if (!Directory.Exists(runtimeFolder)) return string.Empty;

        string[] diagnosticFiles =
        {
            "status.json",
            "ea-app-launch.log",
            "trust-patch.log",
            "auth-bridge.log",
            "redirector.error.log",
            "server.error.log"
        };

        StringBuilder builder = new();
        foreach (string fileName in diagnosticFiles)
        {
            string path = IOPath.Combine(runtimeFolder, fileName);
            if (!File.Exists(path)) continue;

            try
            {
                string[] lines = File.ReadLines(path)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .TakeLast(fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? 8 : 6)
                    .ToArray();

                if (lines.Length == 0) continue;

                builder.AppendLine($"Recent {fileName}:");
                foreach (string line in lines)
                {
                    builder.AppendLine("  " + line.Trim());
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                builder.AppendLine($"Could not read {fileName}: {exception.Message}");
            }
        }

        return builder.ToString().Trim();
    }

    private static bool LooksLikeErrorLine(string line)
    {
        string normalized = line.ToLowerInvariant();
        return normalized.Contains("error:") ||
               normalized.Contains("exception") ||
               normalized.Contains("failed") ||
               normalized.Contains("failure") ||
               normalized.Contains("could not") ||
               normalized.Contains("cannot") ||
               normalized.Contains("denied") ||
               normalized.Contains("timed out") ||
               normalized.Contains("timeout");
    }

    private static bool ShouldSurfaceWorkflowLine(string line)
    {
        string normalized = line.ToLowerInvariant();
        return normalized.Contains("game:") ||
               normalized.Contains("connection:") ||
               normalized.Contains("server:") ||
               normalized.Contains("trust bridge:") ||
               normalized.Contains("wireguard") ||
               normalized.Contains("discord") ||
               normalized.Contains("auth") ||
               normalized.Contains("redirect") ||
               normalized.Contains("port preflight") ||
               normalized.Contains("port cleanup") ||
               normalized.Contains("launch") ||
               normalized.Contains("fifa21") ||
               normalized.Contains("fifa 21");
    }

    private void LoadSettings()
    {
        LauncherSettingsSnapshot settings = RuntimeCompatibility.LoadSettings(runtimeSnapshot.Root);
        GamePathTextBox.Text = settings.GamePath;
        EaActivationCheckBox.IsChecked = settings.EaActivation;
        AlwaysOnTopCheckBox.IsChecked = settings.AlwaysOnTop;
        Topmost = settings.AlwaysOnTop;
        MusicVolumeSlider.Value = Math.Clamp(settings.MusicVolume, 0, 1);
        musicMuted = settings.MusicMuted;
        SoundEffectsVolumeSlider.Value = Math.Clamp(settings.SoundEffectsVolume, 0, 1);
        soundEffectsMuted = settings.SoundEffectsMuted;
        SoundEffectsMuteCheckBox.IsChecked = soundEffectsMuted;
    }

    private void ShowView(FrameworkElement view, WpfButton activeButton)
    {
        if (view.Visibility == Visibility.Visible) return;

        OverviewView.Visibility = Visibility.Collapsed;
        ClubView.Visibility = Visibility.Collapsed;
        DailyDropView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        ConnectionView.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
        OverviewNavButton.Style = (Style)FindResource("NavButtonStyle");
        ClubNavButton.Style = (Style)FindResource("NavButtonStyle");
        DailyDropNavButton.Style = (Style)FindResource("NavButtonStyle");
        SettingsNavButton.Style = (Style)FindResource("NavButtonStyle");
        ConnectionNavButton.Style = (Style)FindResource("NavButtonStyle");
        activeButton.Style = (Style)FindResource("ActiveNavButtonStyle");
        AnimateViewEntrance(view);
        PlayLauncherSound(CreateTabDigitalSound(NextTabSoundVariant()), volumeScale: 0.55);
    }

    private static void AnimateViewEntrance(FrameworkElement view)
    {
        view.Opacity = 0;
        TranslateTransform translate = new(14, 0);
        view.RenderTransform = translate;

        CubicEase easing = new() { EasingMode = EasingMode.EaseOut };
        DoubleAnimation fade = new(0, 1, TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        DoubleAnimation slide = new(14, 0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

        fade.Completed += (_, _) => view.Opacity = 1;
        slide.Completed += (_, _) => translate.X = 0;
        view.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void OverviewNavButton_Click(object sender, RoutedEventArgs e) => ShowView(OverviewView, OverviewNavButton);

    private async void ClubNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowView(ClubView, ClubNavButton);
        await RefreshClubAsync();
    }

    private async void RefreshClubButton_Click(object sender, RoutedEventArgs e) => await RefreshClubAsync();

    private async void SaveClubButton_Click(object sender, RoutedEventArgs e)
    {
        string clubName = ClubNameTextBox.Text.Trim();
        string clubAbbr = ClubAbbrTextBox.Text.Trim().ToUpperInvariant();
        if (clubName.Length is < 1 or > 32 || clubAbbr.Length is < 1 or > 3)
        {
            ClubStatusText.Text = "Enter a 1–32 character club name and a 1–3 character abbreviation.";
            return;
        }

        await RunClubRequestAsync("Rename", clubName, clubAbbr);
    }

    private async Task RefreshClubAsync() => await RunClubRequestAsync("Get", null, null);

    private async Task RunClubRequestAsync(string action, string? clubName, string? clubAbbr)
    {
        if (clubRequestRunning) return;
        clubRequestRunning = true;
        SetClubControlsEnabled(false);
        ClubStatusText.Text = action == "Rename" ? "Saving club identity…" : "Loading your club from the server…";

        try
        {
            string scriptPath = IOPath.Combine(runtimeSnapshot.Root, "Manage-Aurora21Club.ps1");
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException("The club helper is missing from this launcher package.", scriptPath);
            }

            List<string> arguments = ["-Action", action];
            if (action == "Rename")
            {
                arguments.AddRange(["-ClubName", clubName ?? string.Empty, "-ClubAbbr", clubAbbr ?? string.Empty]);
            }

            using Process process = CreatePowerShellProcess(scriptPath, arguments.ToArray());
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string output = (await outputTask).Trim();
            string error = (await errorTask).Trim();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "The club request failed." : error);
            }

            ClubCommandResult? result = JsonSerializer.Deserialize<ClubCommandResult>(
                output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (result is null)
            {
                throw new InvalidDataException("The server returned an invalid club response.");
            }

            ClubNameTextBox.Text = result.ClubName ?? string.Empty;
            ClubAbbrTextBox.Text = result.ClubAbbr ?? string.Empty;
            CoinBalanceText.Text = result.Credits.ToString("N0");
            ClubFifaPointsText.Text = result.FifaPoints.ToString("N0");
            DailyPointsBalanceText.Text = $"{result.FifaPoints:N0} FP";
            RecordText.Text = $"{result.Wins:N0} – {result.Draws:N0} – {result.Losses:N0}";
            StreakText.Text = FormatClubStreak(result.StreakType, result.StreakCount);
            ClubStatusText.Text = action == "Rename"
                ? "Club name saved."
                : $"Club loaded • {result.Credits:N0} coins • {result.FifaPoints:N0} FIFA Points";
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException or System.ComponentModel.Win32Exception)
        {
            ClubStatusText.Text = exception.Message;
        }
        finally
        {
            clubRequestRunning = false;
            SetClubControlsEnabled(true);
        }
    }

    private void SetClubControlsEnabled(bool enabled)
    {
        ClubNameTextBox.IsEnabled = enabled;
        ClubAbbrTextBox.IsEnabled = enabled;
        SaveClubButton.IsEnabled = enabled;
        RefreshClubButton.IsEnabled = enabled;
    }

    private sealed class ClubCommandResult
    {
        public string? ClubName { get; set; }
        public string? ClubAbbr { get; set; }
        public long Credits { get; set; }
        public long FifaPoints { get; set; }
        public int Wins { get; set; }
        public int Draws { get; set; }
        public int Losses { get; set; }
        public string? StreakType { get; set; }
        public int StreakCount { get; set; }
    }

    private static string FormatClubStreak(string? streakType, int streakCount)
    {
        string type = (streakType ?? string.Empty).Trim().ToUpperInvariant();
        int count = Math.Max(0, streakCount);
        return type switch
        {
            "W" or "WIN" when count > 0 => $"{count}W",
            "D" or "DRAW" when count > 0 => $"{count}D",
            "L" or "LOSS" when count > 0 => $"{count}L",
            _ => "No streak"
        };
    }

    private async void DailyDropNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowView(DailyDropView, DailyDropNavButton);
        StartDailyCaseIdleAnimation();
        await RunDailyDropRequestAsync(claim: false);
    }

    private void StartDailyCaseIdleAnimation()
    {
        DailyCaseTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(-3, 4, TimeSpan.FromMilliseconds(1450))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    private async void DailyDropClaimButton_Click(object sender, RoutedEventArgs e) =>
        await RunDailyDropRequestAsync(claim: true);

    private async Task RunDailyDropRequestAsync(bool claim)
    {
        if (dailyDropRequestRunning) return;
        dailyDropRequestRunning = true;
        DailyDropClaimButton.IsEnabled = false;
        DailyDropClaimButton.Content = claim ? "OPENING…" : "CHECKING…";
        DailyDropStatusText.Text = claim ? "Opening your daily drop…" : "Loading your FIFA Points balance…";
        if (!claim)
            DailyRewardResultBorder.Visibility = Visibility.Collapsed;

        try
        {
            string scriptPath = IOPath.Combine(runtimeSnapshot.Root, "Manage-Aurora21Club.ps1");
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("The daily-drop helper is missing from this launcher package.", scriptPath);

            using Process process = CreatePowerShellProcess(
                scriptPath,
                "-Action",
                claim ? "ClaimDaily" : "DailyStatus");
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string output = (await outputTask).Trim();
            string error = (await errorTask).Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "The daily-drop request failed." : error);

            DailyDropCommandResult? result = JsonSerializer.Deserialize<DailyDropCommandResult>(
                output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (result is null || result.NextAvailableAt <= 0)
                throw new InvalidDataException("The server returned an invalid daily-drop response.");

            DailyPointsBalanceText.Text = $"{result.FifaPoints:N0} FP";
            dailyDropAvailable = result.Available;
            dailyDropNextAvailableUtc = DateTimeOffset.FromUnixTimeSeconds(result.NextAvailableAt);

            if (claim && result.Claimed && result.Reward > 0)
            {
                await PlayDailyDropAnimationAsync(result.Reward);
            DailyRewardText.Text = $"YOU WON {result.Reward:N0} FIFA POINTS";
            DailyRewardResultBorder.Visibility = Visibility.Visible;
            AnimateDailyRewardReveal();
                DailyDropStatusText.Text = $"Daily reward claimed • new balance {result.FifaPoints:N0} FP";
            }
            else if (claim)
            {
                DailyRewardResultBorder.Visibility = Visibility.Collapsed;
                DailyDropStatusText.Text = "Today's drop has already been claimed.";
            }
            else
            {
                DailyDropStatusText.Text = result.Available
                    ? "Your free daily drop is ready."
                    : "Today's drop has been claimed.";
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException or System.ComponentModel.Win32Exception or ArgumentOutOfRangeException)
        {
            dailyDropAvailable = false;
            dailyDropNextAvailableUtc = null;
            DailyDropStatusText.Text = exception.Message;
            DailyDropCountdownText.Text = "Unable to load daily drop";
        }
        finally
        {
            dailyDropRequestRunning = false;
            UpdateDailyDropCountdown();
        }
    }

    private void PopulateDailyDropReel(int? landingReward)
    {
        DailyDropReelScroller.ScrollToHorizontalOffset(0);
        DailyDropReelPanel.Children.Clear();
        DailyDropReelPanel.Width = DailyDropTileCount * (DailyDropTileWidth + (DailyDropTileMargin * 2));

        for (int index = 0; index < DailyDropTileCount; index++)
        {
            int reward = landingReward.HasValue && index == DailyDropLandingIndex
                ? landingReward.Value
                : DailyDropRewardValues[Random.Shared.Next(DailyDropRewardValues.Length)];
            DailyDropReelPanel.Children.Add(CreateDailyDropTile(reward, landingReward.HasValue && index == DailyDropLandingIndex));
        }
    }

    private static Border CreateDailyDropTile(int reward, bool isLandingTile)
    {
        (string rarity, string background, string border, string foreground) = reward switch
        {
            >= 5000 => ("JACKPOT", "#59441411", "#FFC247", "#FFC247"),
            >= 2500 => ("MYTHIC", "#4D352A16", "#E1A85A", "#E1A85A"),
            >= 1000 => ("LEGENDARY", "#53331A68", "#CF72FF", "#CF72FF"),
            >= 500 => ("RARE", "#49212855", "#A36EFF", "#A36EFF"),
            >= 250 => ("UNCOMMON", "#3E1A3441", "#B684C2", "#B684C2"),
            _ => ("COMMON", "#33222831", "#78818B", "#8D97A1")
        };

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = $"{reward:N0}\nFP",
            TextAlignment = TextAlignment.Center,
            FontSize = reward >= 5000 ? 15 : 16,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = rarity,
            Margin = new Thickness(0, 7, 0, 0),
            Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!,
            FontSize = 6.5,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var tile = new Border
        {
            Width = DailyDropTileWidth,
            Height = 112,
            Margin = new Thickness(DailyDropTileMargin, 10, DailyDropTileMargin, 10),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)new BrushConverter().ConvertFromString(background)!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString(border)!,
            BorderThickness = new Thickness(isLandingTile ? 2 : 1),
            Child = content
        };
        if (isLandingTile)
        {
            tile.Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 0,
                Opacity = 0.55,
                Color = Color.FromRgb(202, 112, 255)
            };
        }
        return tile;
    }

    private async Task PlayDailyDropAnimationAsync(int authoritativeReward)
    {
        PopulateDailyDropReel(authoritativeReward);
        DailyRewardResultBorder.Visibility = Visibility.Collapsed;
        DailyDropStatusText.Text = "Opening case… the server has selected your reward.";
        DailyDropReelPanel.UpdateLayout();
        DailyDropReelScroller.UpdateLayout();
        PlayLauncherSound(CreateDailyDropUnlockSound());

        if (SystemParameters.ClientAreaAnimation)
        {
            var caseShake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(620) };
            caseShake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            caseShake.KeyFrames.Add(new LinearDoubleKeyFrame(-8, KeyTime.FromPercent(0.18)));
            caseShake.KeyFrames.Add(new LinearDoubleKeyFrame(7, KeyTime.FromPercent(0.36)));
            caseShake.KeyFrames.Add(new LinearDoubleKeyFrame(-5, KeyTime.FromPercent(0.54)));
            caseShake.KeyFrames.Add(new LinearDoubleKeyFrame(3, KeyTime.FromPercent(0.72)));
            caseShake.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            DailyCaseTranslate.BeginAnimation(TranslateTransform.XProperty, caseShake);
            DailyCaseLock.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260))
                {
                    BeginTime = TimeSpan.FromMilliseconds(340),
                    FillBehavior = FillBehavior.HoldEnd
                });
            await Task.Delay(660);
        }
        else
        {
            DailyCaseLock.Opacity = 0;
        }

        double tilePitch = DailyDropTileWidth + (DailyDropTileMargin * 2);
        double viewportWidth = Math.Max(1, DailyDropReelScroller.ViewportWidth);
        double startOffset = Math.Max(0, (5 * tilePitch) + (tilePitch / 2) - (viewportWidth / 2));
        double selectedTileCenter = (DailyDropLandingIndex * tilePitch) + (tilePitch / 2);
        double targetOffset = Math.Clamp(
            selectedTileCenter - (viewportWidth / 2),
            0,
            Math.Max(0, DailyDropReelScroller.ScrollableWidth));
        DailyDropReelScroller.ScrollToHorizontalOffset(startOffset);

        if (!SystemParameters.ClientAreaAnimation)
        {
            DailyDropReelScroller.ScrollToHorizontalOffset(targetOffset);
            PlayLauncherSound(CreateDailyDropLandingSound());
            await Task.Delay(150);
            return;
        }

        var selectorPulse = new DoubleAnimation(0.18, 0.78, TimeSpan.FromMilliseconds(360))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        DailyDropSelectorBay.BeginAnimation(UIElement.OpacityProperty, selectorPulse);
        TimeSpan reelDuration = TimeSpan.FromSeconds(5.6);
        int reelSteps = Math.Max(1, (int)Math.Round(Math.Abs(targetOffset - startOffset) / tilePitch));
        PlayLauncherSound(CreateDailyDropReelSound(reelDuration, reelSteps));
        await AnimateDailyDropReelAtDisplayRefreshAsync(startOffset, targetOffset, reelDuration);
        DailyDropSelectorBay.BeginAnimation(UIElement.OpacityProperty, null);
        DailyDropSelectorBay.Opacity = 1;
        PlayLauncherSound(CreateDailyDropLandingSound());
    }

    private void PlayLauncherSound(byte[] pcmBytes, double volumeScale = 1.0, int latencyMilliseconds = 45)
    {
        if (soundEffectsMuted || pcmBytes.Length == 0) return;

        try
        {
            var provider = new BufferedWaveProvider(new WaveFormat(48000, 16, 1))
            {
                ReadFully = false,
                DiscardOnBufferOverflow = true
            };
            provider.AddSamples(pcmBytes, 0, pcmBytes.Length);

            var output = new WaveOutEvent
            {
                DesiredLatency = Math.Clamp(latencyMilliseconds, 45, 140),
                NumberOfBuffers = latencyMilliseconds >= 80 ? 4 : 3,
                Volume = (float)Math.Clamp(SoundEffectsVolumeSlider.Value * volumeScale * 0.85, 0.0, 0.62)
            };
            output.Init(provider);
            output.PlaybackStopped += (_, _) =>
            {
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        launcherSoundOutputs.Remove(output);
                        output.Dispose();
                    });
                }
                catch
                {
                    output.Dispose();
                }
            };
            launcherSoundOutputs.Add(output);
            output.Play();
        }
        catch
        {
            // Sound effects are decorative and must never interrupt a reward claim.
        }
    }

    private void StopLauncherSounds()
    {
        foreach (WaveOutEvent output in launcherSoundOutputs.ToArray())
        {
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
        }
        launcherSoundOutputs.Clear();
    }

    private static byte[] CreateDailyDropUnlockSound()
    {
        const int sampleRate = 48000;
        double[] samples = new double[(int)(sampleRate * 0.64)];
        AddSoundBurst(samples, sampleRate, 0.015, 0.055, 2050, 0.52, 0.30, 211);
        AddSoundBurst(samples, sampleRate, 0.105, 0.075, 1180, 0.42, 0.22, 212);
        AddSoundBurst(samples, sampleRate, 0.245, 0.100, 520, 0.38, 0.17, 213);
        AddSoundBurst(samples, sampleRate, 0.385, 0.210, 115, 0.58, 0.13, 214);
        AddSoundBurst(samples, sampleRate, 0.405, 0.095, 1720, 0.28, 0.16, 215);
        return ConvertSamplesToPcm16(samples);
    }

    private static byte[] CreateDailyDropReelSound(TimeSpan duration, int stepCount)
    {
        const int sampleRate = 48000;
        double seconds = Math.Max(0.25, duration.TotalSeconds);
        double[] samples = new double[(int)Math.Ceiling(sampleRate * seconds)];

        // A quiet mechanical bed gives the individual tile clicks some weight.
        for (int index = 0; index < samples.Length; index++)
        {
            double time = index / (double)sampleRate;
            double progress = time / seconds;
            double fade = Math.Min(1, time / 0.10) * Math.Min(1, (seconds - time) / 0.22);
            double frequency = 82 + (34 * (1 - progress));
            samples[index] += Math.Sin(2 * Math.PI * frequency * time) * 0.025 * Math.Max(0, fade);
        }

        for (int step = 1; step <= stepCount; step++)
        {
            double distanceProgress = step / (double)(stepCount + 1);
            // Inverse of the reel's quartic ease-out keeps every click aligned
            // with an item crossing the selector, including the long slowdown.
            double timeProgress = 1 - Math.Pow(1 - distanceProgress, 0.25);
            double time = timeProgress * seconds;
            double frequency = 1420 - (520 * timeProgress);
            double amplitude = 0.24 + (0.12 * timeProgress);
            AddSoundBurst(samples, sampleRate, time, 0.020, frequency, amplitude, 0.16, 4000 + step);
        }

        return ConvertSamplesToPcm16(samples);
    }

    private static byte[] CreateDailyDropLandingSound()
    {
        const int sampleRate = 48000;
        double[] samples = new double[(int)(sampleRate * 0.72)];
        AddSoundBurst(samples, sampleRate, 0.000, 0.150, 165, 0.62, 0.12, 921);
        AddSoundBurst(samples, sampleRate, 0.055, 0.260, 660, 0.34, 0.03, 922);
        AddSoundBurst(samples, sampleRate, 0.115, 0.360, 990, 0.29, 0.02, 923);
        AddSoundBurst(samples, sampleRate, 0.175, 0.430, 1320, 0.25, 0.01, 924);
        return ConvertSamplesToPcm16(samples);
    }

    private int NextTabSoundVariant()
    {
        int variant = launcherSoundRandom.Next(14);
        if (variant >= lastTabSoundVariant) variant++;
        lastTabSoundVariant = variant;
        return variant;
    }

    private static byte[] CreateTabDigitalSound(int variant)
    {
        const int sampleRate = 48000;
        variant = Math.Abs(variant) % 15;
        double duration = 0.115 + ((variant % 5) * 0.006) + ((variant / 5) * 0.004);
        double[] samples = new double[(int)(sampleRate * duration)];
        double phase = 0;
        double baseFrequency = 102 + ((variant % 5) * 5) + ((variant / 5) * 3);
        double direction = variant % 2 == 0 ? 1 : -1;
        for (int index = 0; index < samples.Length; index++)
        {
            double progress = index / (double)samples.Length;
            double attack = Math.Min(1, progress / 0.10);
            double release = Math.Pow(Math.Max(0, 1 - progress), 2.8);
            double frequency = baseFrequency + (direction * 24 * progress);

            phase += 2 * Math.PI * frequency / sampleRate;
            double pulse = 0.78 + (0.22 * Math.Max(0, Math.Sin(2 * Math.PI * (13 + (variant % 4)) * index / sampleRate)));
            double tone = Math.Sin(phase) + (0.14 * Math.Sin((phase * 2) + 0.35));
            samples[index] = tone * attack * release * pulse * 0.13;
        }
        return ConvertSamplesToPcm16(samples);
    }

    private static byte[] CreateStartupElectronicStinger()
    {
        return CreateSoftSystemMotif(pitchScale: 1.0, timeScale: 1.0, amplitude: 0.23);
    }

    private static byte[] CreateExitElectronicStinger()
    {
        // Reuse the welcome motif at a lower pitch and slower pace so closing
        // and successful game launch feel related without playing the same cue.
        return CreateSoftSystemMotif(pitchScale: 0.62, timeScale: 1.48, amplitude: 0.22);
    }

    private static byte[] CreateSoftSystemMotif(double pitchScale, double timeScale, double amplitude)
    {
        const int sampleRate = 48000;
        timeScale = Math.Max(0.25, timeScale);
        double[] samples = new double[(int)(sampleRate * 1.18 * timeScale)];

        // An original, rounded system-notification motif: smooth sine voices,
        // soft attacks, and restrained upper harmonics rather than bell strikes.
        AddSoftSystemTone(samples, sampleRate, 0.015 * timeScale, 0.72 * timeScale, 329.63 * pitchScale, amplitude);
        AddSoftSystemTone(samples, sampleRate, 0.145 * timeScale, 0.75 * timeScale, 493.88 * pitchScale, amplitude * 0.78);
        AddSoftSystemTone(samples, sampleRate, 0.305 * timeScale, 0.78 * timeScale, 659.25 * pitchScale, amplitude * 0.60);
        return ConvertSamplesToPcm16(samples);
    }

    private static void AddSoftSystemTone(
        double[] destination,
        int sampleRate,
        double startTime,
        double duration,
        double frequency,
        double amplitude)
    {
        int start = Math.Max(0, (int)(startTime * sampleRate));
        int count = Math.Min(destination.Length - start, Math.Max(1, (int)(duration * sampleRate)));
        if (count <= 0) return;

        double phase = 0;
        for (int offset = 0; offset < count; offset++)
        {
            double progress = offset / (double)Math.Max(1, count - 1);
            double pitchDrift = 0.992 + (0.008 * Math.Sin(Math.PI * progress));
            phase += 2 * Math.PI * frequency * pitchDrift / sampleRate;
            double attack = Math.Sin(Math.PI * Math.Min(0.5, progress / 0.22));
            double release = Math.Pow(Math.Max(0, 1 - progress), 2.15);
            double voice = Math.Sin(phase) + (0.16 * Math.Sin((phase * 2) + 0.30)) + (0.045 * Math.Sin((phase * 0.5) - 0.20));
            destination[start + offset] += voice * amplitude * attack * release;
        }
    }

    private static byte[] CreateBanRumbleSound()
    {
        const int sampleRate = 48000;
        const double duration = 3.6;
        double[] samples = new double[(int)(sampleRate * duration)];
        var random = new Random(761);
        for (int index = 0; index < samples.Length; index++)
        {
            double time = index / (double)sampleRate;
            double progress = time / duration;
            double attack = Math.Min(1, time / 0.16);
            double release = Math.Min(1, (duration - time) / 0.65);
            double envelope = attack * Math.Max(0, release) * (0.78 + (0.22 * Math.Sin(2 * Math.PI * 1.7 * time)));
            double sub = Math.Sin(2 * Math.PI * 38 * time) * 0.42;
            double body = Math.Sin(2 * Math.PI * (54 + (4 * Math.Sin(time * 2.1))) * time) * 0.29;
            double grit = ((random.NextDouble() * 2) - 1) * 0.07;
            samples[index] = (sub + body + grit) * envelope;
        }
        AddSoundBurst(samples, sampleRate, 0.04, 0.48, 74, 0.72, 0.18, 762);
        AddSoundBurst(samples, sampleRate, 0.28, 0.34, 46, 0.55, 0.12, 763);
        return ConvertSamplesToPcm16(samples);
    }

    private static void AddSoundBurst(
        double[] destination,
        int sampleRate,
        double startSeconds,
        double durationSeconds,
        double frequency,
        double amplitude,
        double noiseAmount,
        int seed)
    {
        int start = Math.Max(0, (int)(startSeconds * sampleRate));
        int length = Math.Max(1, (int)(durationSeconds * sampleRate));
        var random = new Random(seed);
        for (int offset = 0; offset < length && start + offset < destination.Length; offset++)
        {
            double local = offset / (double)sampleRate;
            double normalized = offset / (double)length;
            double envelope = Math.Exp(-5.4 * normalized) * Math.Min(1, offset / (sampleRate * 0.0025));
            double tone = Math.Sin(2 * Math.PI * frequency * local)
                + (0.30 * Math.Sin(2 * Math.PI * frequency * 2.37 * local));
            double noise = ((random.NextDouble() * 2) - 1) * noiseAmount;
            destination[start + offset] += (tone + noise) * amplitude * envelope;
        }
    }

    private static byte[] ConvertSamplesToPcm16(double[] samples)
    {
        byte[] bytes = new byte[samples.Length * sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            short value = (short)Math.Round(Math.Clamp(samples[index], -1, 1) * short.MaxValue);
            bytes[index * 2] = (byte)(value & 0xff);
            bytes[(index * 2) + 1] = (byte)((value >> 8) & 0xff);
        }
        return bytes;
    }

    private Task AnimateDailyDropReelAtDisplayRefreshAsync(double startOffset, double targetOffset, TimeSpan duration)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        EventHandler? renderingHandler = null;
        renderingHandler = (_, _) =>
        {
            double progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 4);
            DailyDropReelScroller.ScrollToHorizontalOffset(startOffset + ((targetOffset - startOffset) * eased));
            if (progress < 1)
            {
                return;
            }

            CompositionTarget.Rendering -= renderingHandler;
            DailyDropReelScroller.ScrollToHorizontalOffset(targetOffset);
            completion.TrySetResult(true);
        };
        CompositionTarget.Rendering += renderingHandler;
        return completion.Task;
    }

    private void AnimateDailyRewardReveal()
    {
        DailyRewardResultBorder.BeginAnimation(UIElement.OpacityProperty, null);
        DailyRewardResultBorder.Opacity = 0;
        var scale = new ScaleTransform(0.72, 0.72);
        DailyRewardResultBorder.RenderTransformOrigin = new Point(0.5, 0.5);
        DailyRewardResultBorder.RenderTransform = scale;

        var overshoot = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.42 };
        DailyRewardResultBorder.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { FillBehavior = FillBehavior.HoldEnd });
        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(540)) { EasingFunction = overshoot, FillBehavior = FillBehavior.HoldEnd });
        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(540)) { EasingFunction = overshoot, FillBehavior = FillBehavior.HoldEnd });
    }

    private void UpdateDailyDropCountdown()
    {
        if (dailyDropRequestRunning)
        {
            DailyDropClaimButton.IsEnabled = false;
            return;
        }

        if (dailyDropNextAvailableUtc is null)
        {
            DailyDropClaimButton.IsEnabled = false;
            DailyDropClaimButton.Content = "UNLOCK DAILY CASE";
            return;
        }

        TimeSpan remaining = dailyDropNextAvailableUtc.Value - DateTimeOffset.UtcNow;
        if (dailyDropAvailable || remaining <= TimeSpan.Zero)
        {
            dailyDropAvailable = true;
            DailyDropClaimButton.IsEnabled = true;
            DailyDropClaimButton.Content = "UNLOCK DAILY CASE";
            DailyDropCountdownText.Text = "Available now";
            return;
        }

        DailyDropClaimButton.IsEnabled = false;
        DailyDropClaimButton.Content = "CLAIMED TODAY";
        int totalHours = Math.Max(0, (int)Math.Floor(remaining.TotalHours));
        DailyDropCountdownText.Text = $"Next in {totalHours}h {remaining.Minutes}m {remaining.Seconds}s";
    }

    private sealed class DailyDropCommandResult
    {
        public bool Available { get; set; }
        public bool Claimed { get; set; }
        public int Reward { get; set; }
        public long FifaPoints { get; set; }
        public long NextAvailableAt { get; set; }
        public int SecondsRemaining { get; set; }
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        SetSettingsStatus("The game location is saved for this Windows user.", false);
        ShowView(SettingsView, SettingsNavButton);
    }

    private void ConnectionNavButton_Click(object sender, RoutedEventArgs e)
    {
        RuntimeSnapshot latest = RuntimeCompatibility.Discover();
        ConnectionPageGameText.Text = CondenseState(latest.GameState);
        ConnectionPageNetworkText.Text = secureConnectionVerified
            ? "Connected securely"
            : wireGuardProcess is { HasExited: false }
                ? "Checking secure route…"
                : RuntimeStatusWasUpdatedThisSession()
                    ? CondenseHeroState(latest.IsCompatible ? latest.ConnectionState : "Files missing")
                    : "Not verified this session";
        ConnectionPagePythonText.Text = pythonReady ? "Python 3.12 ready" : pythonSetupProcess is { HasExited: false } ? "Checking Python…" : "Python not ready";
        ShowView(ConnectionView, ConnectionNavButton);
        Dispatcher.BeginInvoke(() => ConnectionLogScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        AddConnectionLog(
            "Launch requested",
            "Validating Python, FIFA21.exe, Aurora 21 runtime files and the secure route before starting the game.",
            ConnectionLogSeverity.Info);

        if (!pythonReady)
        {
            AddConnectionLog(
                "Launch paused for prerequisites",
                "Python 3.12 is not ready yet. Aurora 21 will run the prerequisite setup first.",
                ConnectionLogSeverity.Warning);
            RunPythonSetup();
            return;
        }

        if (ValidateLaunchPrerequisites())
        {
            RunWireGuardWorkflow(firstRun: true, onSuccess: StartLaunchWorkflow);
        }
    }

    private void ResetConnectionStateForStartup()
    {
        secureConnectionVerified = false;
        startupConnectionCheckStarted = false;
        lastLoggedGameState = null;
        lastLoggedConnectionState = null;
        lastLoggedCompatibility = null;

        ConnectionPageGameText.Text = "Checking live process…";
        ConnectionPageNetworkText.Text = "Not checked yet";
        ConnectionPagePythonText.Text = "Checking Python…";

        GameStatusText.Text = "Checking…";
        HeroGameStatusText.Text = "Checking…";
        GameStatusDot.Fill = (Brush)FindResource("MutedDotBrush");
        ConnectionStatusText.Text = "Checking secure connection…";
        HeroConnectionStatusText.Text = "Checking…";
        ConnectionStatusDot.Fill = (Brush)FindResource("MutedDotBrush");
    }

    private void RunStartupStaticChecks()
    {
        RuntimeSnapshot fresh = RuntimeCompatibility.Discover();
        lastLoggedCompatibility = fresh.IsCompatible;
        lastLoggedGameState = CondenseState(fresh.GameState);

        if (fresh.IsCompatible)
        {
            AddConnectionLog(
                "Launcher files are ready",
                "Launch/stop scripts, LocalFUT server files and the TLS redirector are present.",
                ConnectionLogSeverity.Success);
        }
        else
        {
            string source = IOPath.Combine(fresh.Root, "runtime-source");
            string launch = IOPath.Combine(fresh.Root, "Launch-LocalFUT.ps1");
            string stop = IOPath.Combine(fresh.Root, "Stop-LocalFUT.ps1");
            string server = IOPath.Combine(source, "localfut21", "server.py");
            string redirector = IOPath.Combine(source, "redirector_tls.py");
            AddConnectionLog(
                "Launcher files check failed",
                $"Aurora 21 (Alpha v1) is missing one or more required files.\nLaunch script: {File.Exists(launch)} ({launch})\nStop script: {File.Exists(stop)} ({stop})\nserver.py: {File.Exists(server)} ({server})\nredirector_tls.py: {File.Exists(redirector)} ({redirector})",
                ConnectionLogSeverity.Error);
        }

        LauncherSettingsSnapshot settings = RuntimeCompatibility.LoadSettings(fresh.Root);
        if (!string.IsNullOrWhiteSpace(settings.GamePath) && File.Exists(settings.GamePath) &&
            string.Equals(IOPath.GetFileName(settings.GamePath), "FIFA21.exe", StringComparison.OrdinalIgnoreCase))
        {
            AddConnectionLog(
                "FIFA 21 installation found",
                $"Configured executable exists: {settings.GamePath}",
                ConnectionLogSeverity.Success);
        }
        else
        {
            AddConnectionLog(
                "FIFA 21 installation needs attention",
                $"Saved path: {(string.IsNullOrWhiteSpace(settings.GamePath) ? "<not configured>" : settings.GamePath)}\nAurora 21 (Alpha v1) could not verify an existing FIFA21.exe at that location. Choose the correct executable on Game Installation before launching.",
                ConnectionLogSeverity.Warning);
        }

        string gameState = CondenseState(fresh.GameState);
        ConnectionPageGameText.Text = gameState;
        GameStatusText.Text = gameState;
        HeroGameStatusText.Text = gameState;
        if (IsRunningState(fresh.GameState))
        {
            GameStatusDot.Fill = (Brush)FindResource("SuccessBrush");
            AddConnectionLog(
                "FIFA 21 is already running",
                $"A live FIFA21.exe process was confirmed by Windows. {fresh.GameState}",
                ConnectionLogSeverity.Success);
        }
        else
        {
            GameStatusDot.Fill = (Brush)FindResource("MutedDotBrush");
            AddConnectionLog(
                "FIFA 21 is not running",
                "Windows process enumeration found no live FIFA21.exe process. This is normal before you press Play FIFA 21.",
                ConnectionLogSeverity.Info);
        }
    }

    private void StartStartupConnectionCheck(bool pythonCheckSucceeded)
    {
        if (startupConnectionCheckStarted) return;
        startupConnectionCheckStarted = true;

        if (!pythonCheckSucceeded)
        {
            ConnectionPageNetworkText.Text = "Waiting for prerequisites";
            ConnectionStatusText.Text = "Secure connection check skipped until prerequisites are ready.";
            HeroConnectionStatusText.Text = "Waiting";
            AddConnectionLog(
                "Secure connection check skipped",
                "The Python/components prerequisite check failed, so Aurora 21 (Alpha v1) did not continue into the authenticated gateway check. Fix the prerequisite error above, then use Check Again.",
                ConnectionLogSeverity.Warning);
            return;
        }

        AddConnectionLog(
            "Checking secure connection",
            "Prerequisites passed. Aurora 21 (Alpha v1) is now verifying Discord authorization, WireGuard and the authenticated LocalFUT gateway for this session.",
            ConnectionLogSeverity.Info);
        RunWireGuardWorkflow(firstRun: false);
    }

    private DateTime? GetRuntimeStatusWriteTimeUtc()
    {
        string statusPath = IOPath.Combine(runtimeSnapshot.Root, "runtime", "status.json");
        try
        {
            return File.Exists(statusPath) ? File.GetLastWriteTimeUtc(statusPath) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool RuntimeStatusWasUpdatedThisSession()
    {
        DateTime? currentWriteUtc = GetRuntimeStatusWriteTimeUtc();
        if (currentWriteUtc is null) return false;

        // Compare against the exact status file timestamp observed when this
        // launcher instance was constructed. This avoids trusting a stale
        // status.json even when ZIP extraction happened moments ago.
        if (runtimeStatusBaselineWriteUtc is DateTime baseline)
        {
            return currentWriteUtc.Value > baseline;
        }

        return currentWriteUtc.Value >= launcherSessionStartedUtc;
    }

    private void RunPythonSetup(Action<bool>? onCompleted = null)
    {
        if (pythonSetupProcess is { HasExited: false })
        {
            AddConnectionLog(
                "Python check already running",
                "Aurora 21 is already waiting for the prerequisite process to finish.",
                ConnectionLogSeverity.Warning);
            return;
        }

        string scriptPath = IOPath.Combine(runtimeSnapshot.Root, "Ensure-Python.ps1");
        if (!File.Exists(scriptPath))
        {
            PythonStatusText.Text = "Required Python setup file is missing.";
            ConnectionPagePythonText.Text = "Setup file missing";
            WorkflowStatusText.Text = "Re-download the complete player launcher.";
            AddConnectionLog(
                "Python setup file is missing",
                $"Expected file: {scriptPath}\nFile.Exists returned false, so Aurora 21 cannot verify or install Python 3.12.",
                ConnectionLogSeverity.Error);
            onCompleted?.Invoke(false);
            return;
        }

        pythonReady = false;
        LaunchButton.IsEnabled = false;
        PythonStatusText.Text = "Checking Python 3.12 and required components…";
        ConnectionPagePythonText.Text = "Checking Python…";
        AddConnectionLog(
            "Checking prerequisites",
            "Starting Ensure-Python.ps1 to verify Python 3.12 and the required Aurora 21 components.",
            ConnectionLogSeverity.Info);

        try
        {
            Process process = CreatePowerShellProcess(scriptPath);
            pythonSetupProcess = process;
            ProcessDiagnosticCapture capture = AttachProcessOutput(process, "Python prerequisite setup", scriptPath);

            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    Dispatcher.BeginInvoke(() => PythonStatusText.Text = args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data) && LooksLikeErrorLine(args.Data))
                {
                    Dispatcher.BeginInvoke(() => PythonStatusText.Text = "Python setup reported an error.");
                }
            };

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.Invoke(() =>
            {
                int exitCode = SafeExitCode(process);
                pythonReady = exitCode == 0;
                LaunchButton.IsEnabled = pythonReady;

                if (pythonReady)
                {
                    PythonStatusText.Text = "Python 3.12 is ready.";
                    ConnectionPagePythonText.Text = "Python 3.12 ready";
                    AddConnectionLog(
                        "Prerequisites ready",
                        "Python 3.12 and the required launcher components passed the setup check.",
                        ConnectionLogSeverity.Success);
                }
                else
                {
                    PythonStatusText.Text = "Python setup failed. Check your internet connection and try again.";
                    ConnectionPagePythonText.Text = "Python setup failed";
                    WorkflowStatusText.Text = "Launch is unavailable until Python setup completes.";
                    AddConnectionLog(
                        "Python setup failed",
                        BuildProcessFailureDetail(capture, exitCode),
                        ConnectionLogSeverity.Error);
                }

                onCompleted?.Invoke(pythonReady);
            });

            process.Start();
            AddConnectionLog(
                "Python setup process started",
                $"PowerShell started successfully.\nScript: {scriptPath}",
                ConnectionLogSeverity.Info);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            PythonStatusText.Text = "Could not start Python setup: " + exception.Message;
            ConnectionPagePythonText.Text = "Could not start";
            AddConnectionLog(
                "Could not start Python setup",
                $"Step: Python prerequisite setup\nScript: {scriptPath}\nException: {exception.GetType().Name}\nMessage: {exception.Message}",
                ConnectionLogSeverity.Error);
            onCompleted?.Invoke(false);
        }
    }

    private async void StartLaunchWorkflow()
    {
        if (banWarningShown) return;

        if (startProcess is { HasExited: false })
        {
            WorkflowStatusText.Text = "A launch is already running.";
            AddConnectionLog(
                "Launch already in progress",
                "The existing Launch-LocalFUT.ps1 process has not exited yet, so a second launch was not started.",
                ConnectionLogSeverity.Warning);
            return;
        }

        WorkflowStatusText.Text = "Preparing cloud media…";
        if (cloudMediaSyncCancellation is not null)
        {
            try { await SyncCloudMediaAsync(cloudMediaSyncCancellation.Token); } catch (OperationCanceledException) { }
        }
        string launchScript = IOPath.Combine(runtimeSnapshot.Root, "Launch-LocalFUT.ps1");
        LauncherSettingsSnapshot launchSettings = RuntimeCompatibility.LoadSettings(runtimeSnapshot.Root);
        WorkflowStatusText.Text = "Starting FIFA 21…";
        SetStartControlsEnabled(false);
        ArmGameLaunchDetection();

        AddConnectionLog(
            "Starting Aurora 21 runtime",
            $"Launching the LocalFUT runtime.\nScript: {launchScript}\nExecutable: {launchSettings.GamePath}",
            ConnectionLogSeverity.Info);
        if (launchSettings.EaActivation)
        {
            AddConnectionLog(
                "EA activation selected",
                "Aurora 21 will ask EA App to activate and launch FIFA 21 using the legacy origin2 launch path.",
                ConnectionLogSeverity.Info);
        }
        else
        {
            AddConnectionLog(
                "Direct game launch selected",
                $"Aurora 21 will start this executable directly:\n{launchSettings.GamePath}\n\nNo EA App/origin2 launch URI will be used.",
                ConnectionLogSeverity.Info);
        }
        AddConnectionLog(
            "Checking required local ports",
            "Before services start, Aurora 21 will check every local TCP/UDP port it needs. If an unrelated non-system process owns one, that process will be closed automatically and the port release will be verified. EA App is preserved when it legitimately owns LSX port 3216.",
            ConnectionLogSeverity.Info);

        try
        {
            Process process = CreatePowerShellProcess(launchScript, "-GamePath", launchSettings.GamePath);
            startProcess = process;
            ProcessDiagnosticCapture capture = AttachProcessOutput(process, "FIFA 21 launch", launchScript);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.Invoke(() => CompleteStartWorkflow(process, capture));
            process.Start();

            AddConnectionLog(
                "Launch process started",
                "PowerShell started successfully. Aurora 21 is now waiting for runtime services and a new FIFA21.exe window.",
                ConnectionLogSeverity.Info);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            gameLaunchDetectionArmed = false;
            SetStartControlsEnabled(true);
            WorkflowStatusText.Text = "Could not start: " + exception.Message;
            AddConnectionLog(
                "Could not start FIFA 21 launch",
                $"Step: FIFA 21 launch\nScript: {launchScript}\nException: {exception.GetType().Name}\nMessage: {exception.Message}",
                ConnectionLogSeverity.Error);
        }
    }

    private void SetupWireGuardButton_Click(object sender, RoutedEventArgs e) => RunWireGuardWorkflow(firstRun: true);
    private void CheckWireGuardButton_Click(object sender, RoutedEventArgs e) => RunWireGuardWorkflow(firstRun: false);

    private void RunWireGuardWorkflow(bool firstRun, Action? onSuccess = null)
    {
        string stageName = firstRun ? "Discord / secure connection setup" : "secure connection check";

        if (wireGuardProcess is { HasExited: false })
        {
            WorkflowStatusText.Text = "A connection check is already running.";
            AddConnectionLog(
                "Connection check already running",
                "Aurora 21 did not start a second connection process because the existing one has not exited yet.",
                ConnectionLogSeverity.Warning);
            return;
        }

        string scriptName = firstRun ? "First-Run-WireGuard.ps1" : "Ensure-WireGuard-Connection.ps1";
        string scriptPath = IOPath.Combine(runtimeSnapshot.Root, scriptName);
        if (!File.Exists(scriptPath))
        {
            ConnectionStatusText.Text = "Connection files are missing.";
            HeroConnectionStatusText.Text = "Files missing";
            ConnectionStatusDot.Fill = (Brush)FindResource("DangerBrush");
            AddConnectionLog(
                "Secure connection script is missing",
                $"Step: {stageName}\nExpected file: {scriptPath}\nFile.Exists returned false. The launcher package is incomplete or the file was removed.",
                ConnectionLogSeverity.Error);
            return;
        }

        ConnectionStatusText.Text = firstRun ? "Verifying Discord and preparing the secure connection…" : "Checking the secure connection…";
        HeroConnectionStatusText.Text = "Checking…";
        ConnectionStatusDot.Fill = (Brush)FindResource("Accent");
        WorkflowStatusText.Text = "Please follow any setup window that appears.";
        SetWireGuardControlsEnabled(false);
        SetStartControlsEnabled(false);

        AddConnectionLog(
            firstRun ? "Preparing secure player access" : "Checking secure player access",
            $"Starting {scriptName}. Aurora 21 will capture the script result and diagnostic output.",
            ConnectionLogSeverity.Info);

        try
        {
            Process process = CreatePowerShellProcess(scriptPath);
            wireGuardProcess = process;
            ProcessDiagnosticCapture capture = AttachProcessOutput(process, stageName, scriptPath);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.Invoke(() =>
            {
                int exitCode = SafeExitCode(process);
                SetWireGuardControlsEnabled(true);
                SetStartControlsEnabled(true);

                if (exitCode == 0)
                {
                    secureConnectionVerified = true;
                    ConnectionStatusText.Text = "Connected securely.";
                    HeroConnectionStatusText.Text = "Connected";
                    ConnectionStatusDot.Fill = (Brush)FindResource("SuccessBrush");
                    WorkflowStatusText.Text = "Your access is ready.";
                    ConnectionPageNetworkText.Text = "Connected securely";
                AddConnectionLog(
                    "Secure connection is ready",
                    $"{scriptName} exited successfully with code 0. Discord/player authorization and the secure route check completed.",
                    ConnectionLogSeverity.Success);
                if (onSuccess is null && cloudMediaSyncCancellation is not null)
                {
                    WorkflowStatusText.Text = "Downloading launcher media…";
                    _ = SyncCloudMediaAsync(cloudMediaSyncCancellation.Token);
                }
                onSuccess?.Invoke();
                }
                else
                {
                    secureConnectionVerified = false;
                    ConnectionStatusText.Text = firstRun
                        ? "Setup did not complete. Try Connect Discord again."
                        : "Connection unavailable.";
                    HeroConnectionStatusText.Text = "Unavailable";
                    ConnectionStatusDot.Fill = (Brush)FindResource("DangerBrush");
                    WorkflowStatusText.Text = "If this continues, ask an Aurora 21 administrator for help.";
                    ConnectionPageNetworkText.Text = "Connection failed";
                    AddConnectionLog(
                        "Secure connection failed",
                        BuildProcessFailureDetail(capture, exitCode, includeRuntimeDiagnostics: true),
                        ConnectionLogSeverity.Error);
                }
            });
            process.Start();
            AddConnectionLog(
                "Connection process started",
                $"PowerShell started successfully.\nScript: {scriptPath}",
                ConnectionLogSeverity.Info);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            SetWireGuardControlsEnabled(true);
            SetStartControlsEnabled(true);
            secureConnectionVerified = false;
            ConnectionStatusText.Text = "Could not start connection setup.";
            HeroConnectionStatusText.Text = "Unavailable";
            ConnectionStatusDot.Fill = (Brush)FindResource("DangerBrush");
            WorkflowStatusText.Text = exception.Message;
            ConnectionPageNetworkText.Text = "Could not start";
            AddConnectionLog(
                "Could not start secure connection setup",
                $"Step: {stageName}\nScript: {scriptPath}\nException: {exception.GetType().Name}\nMessage: {exception.Message}",
                ConnectionLogSeverity.Error);
        }
    }

    private void SetWireGuardControlsEnabled(bool enabled)
    {
        SetupWireGuardButton.IsEnabled = enabled && !banWarningShown;
        CheckWireGuardButton.IsEnabled = enabled && !banWarningShown;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        gameLaunchDetectionArmed = false;

        if (stopProcess is { HasExited: false })
        {
            WorkflowStatusText.Text = "Stop is already running.";
            AddConnectionLog("Stop already in progress", "The existing stop process has not exited yet.", ConnectionLogSeverity.Warning);
            return;
        }

        string stopScript = IOPath.Combine(runtimeSnapshot.Root, "Stop-LocalFUT.ps1");
        if (!File.Exists(stopScript))
        {
            WorkflowStatusText.Text = "Required stop files are missing.";
            AddConnectionLog(
                "Stop script is missing",
                $"Step: stop Aurora 21 support services\nExpected file: {stopScript}\nFile.Exists returned false.",
                ConnectionLogSeverity.Error);
            return;
        }

        WorkflowStatusText.Text = "Stopping FIFA 21 support services…";
        StopButton.IsEnabled = false;
        AddConnectionLog("Stopping Aurora 21 services", $"Starting {stopScript}.", ConnectionLogSeverity.Info);
        try
        {
            Process process = CreatePowerShellProcess(stopScript);
            stopProcess = process;
            ProcessDiagnosticCapture capture = AttachProcessOutput(process, "stop Aurora 21 support services", stopScript);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.Invoke(() => CompleteStopWorkflow(process, capture));
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            StopButton.IsEnabled = true;
            WorkflowStatusText.Text = "Could not stop: " + exception.Message;
            AddConnectionLog(
                "Could not start the stop process",
                $"Step: stop Aurora 21 support services\nScript: {stopScript}\nException: {exception.GetType().Name}\nMessage: {exception.Message}",
                ConnectionLogSeverity.Error);
        }
    }

    private Process CreatePowerShellProcess(string scriptPath, params string[] extraArguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe", WorkingDirectory = runtimeSnapshot.Root,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (string argument in extraArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return new Process { StartInfo = startInfo };
    }

    private void AttachProcessOutput(Process process)
    {
        process.OutputDataReceived += (_, args) => QueueWorkflowLine(args.Data, false);
        process.ErrorDataReceived += (_, args) => QueueWorkflowLine(args.Data, true);
    }

    private ProcessDiagnosticCapture AttachProcessOutput(Process process, string stageName, string? scriptPath)
    {
        ProcessDiagnosticCapture capture = new(stageName, scriptPath);

        process.OutputDataReceived += (_, args) =>
        {
            string? line = args.Data;
            if (string.IsNullOrWhiteSpace(line)) return;

            capture.Add(line, isError: false);
            QueueWorkflowLine(line, false);

            if (ShouldSurfaceWorkflowLine(line))
            {
                Dispatcher.BeginInvoke(() => AddConnectionLog(
                    "Runtime update",
                    line.Trim(),
                    ConnectionLogSeverity.Info));
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            string? line = args.Data;
            if (string.IsNullOrWhiteSpace(line)) return;

            capture.Add(line, isError: true);
            QueueWorkflowLine(line, true);

            // stderr is not automatically a failure (some tools log informational
            // messages there), so surface obvious error text as a warning while the
            // process is running. The final exit code decides success/failure.
            if (LooksLikeErrorLine(line))
            {
                Dispatcher.BeginInvoke(() => AddConnectionLog(
                    $"{stageName} reported a diagnostic error",
                    $"The process is still running; final success is determined by its exit code.\nMessage: {line.Trim()}",
                    ConnectionLogSeverity.Warning));
            }
        };

        return capture;
    }

    private void QueueWorkflowLine(string? line, bool isError)
{
    if (string.IsNullOrWhiteSpace(line)) return;

    if (TryDecodeBanMarker(line, out string banReason))
    {
        Dispatcher.BeginInvoke(() => ShowBanWarningOnAllScreens(banReason));
        return;
    }

    Dispatcher.BeginInvoke(() =>
    {
        string normalized = line.ToLowerInvariant();

        // Python logging writes INFO messages to stderr by default.
        // Do not treat every stderr line as a setup failure; the process
        // exit code is the authoritative success/failure result.
        if (isError)
        {
            if (normalized.Contains("error:") ||
                normalized.Contains("exception") ||
                normalized.Contains("failed") ||
                normalized.Contains("could not") ||
                normalized.Contains("cannot"))
            {
                WorkflowStatusText.Text =
                    "Setup reported an error. Retry or contact an administrator.";
            }

            return;
        }

        if (normalized.Contains("game:"))
            WorkflowStatusText.Text = "FIFA 21 detected.";
        else if (normalized.Contains("connection:"))
            WorkflowStatusText.Text = "Connection status updated.";
        else if (normalized.Contains("port cleanup"))
            WorkflowStatusText.Text = "Clearing a local port conflict…";
        else if (normalized.Contains("port preflight"))
            WorkflowStatusText.Text = "Checking required local ports…";
        else if (normalized.Contains("server:") ||
                 normalized.Contains("trust bridge:"))
            WorkflowStatusText.Text = "Preparing FIFA 21…";
    });
}

    private void RunBanStatusCheck()
    {
        if (banWarningShown || banCheckProcess is { HasExited: false }) return;

        string scriptPath = IOPath.Combine(runtimeSnapshot.Root, "Ensure-Player-Access.ps1");
        if (!File.Exists(scriptPath)) return;

        try
        {
            Process process = CreatePowerShellProcess(scriptPath, "-BanCheckOnly");
            banCheckProcess = process;
            AttachProcessOutput(process);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                try { process.WaitForExit(); } catch { }
                Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(banCheckProcess, process)) banCheckProcess = null;
                    process.Dispose();
                });
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            banCheckProcess?.Dispose();
            banCheckProcess = null;
        }
    }

    private static bool TryDecodeBanMarker(string line, out string reason)
    {
        reason = string.Empty;
        int marker = line.IndexOf(BanMarkerPrefix, StringComparison.Ordinal);
        if (marker < 0) return false;

        try
        {
            string encoded = line[(marker + BanMarkerPrefix.Length)..].Trim();
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            reason = new string(decoded.Where(character => !char.IsControl(character)).ToArray()).Trim();
            if (reason.Length > 300) reason = reason[..300];
            if (string.IsNullOrWhiteSpace(reason)) reason = "Banned by administrator";
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void ShowBanWarningOnAllScreens(string reason)
    {
        if (banWarningShown) return;
        banWarningShown = true;
        banStatusTimer.Stop();
        SetStartControlsEnabled(false);
        SetWireGuardControlsEnabled(false);
        WorkflowStatusText.Text = "Access banned.";
        StopLauncherSounds();
        PlayLauncherSound(CreateBanRumbleSound());

        foreach (WinForms.Screen screen in WinForms.Screen.AllScreens)
        {
            Window warning = CreateBanWarningWindow(reason);
            banWarningWindows.Add(warning);
            warning.Show();

            IntPtr handle = new WindowInteropHelper(warning).Handle;
            System.Drawing.Rectangle bounds = screen.Bounds;
            SetWindowPos(handle, HwndTopmost, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SwpShowWindow);
        }
    }

    private Window CreateBanWarningWindow(string reason)
    {
        Image logo = new()
        {
            Source = new BitmapImage(new Uri(
                "pack://application:,,,/assets/Aurora21-logo.png",
                UriKind.Absolute)),
            Width = 132,
            Height = 132,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 0, 22),
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(198, 92, 255),
                BlurRadius = 34,
                ShadowDepth = 0,
                Opacity = 0.95
            }
        };
        TextBlock heading = new()
        {
            Text = "YOU HAVE BEEN BANNED. REASON:",
            Foreground = Brushes.White,
            FontSize = 46,
            FontWeight = FontWeights.Black,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(30, 0, 30, 24),
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(198, 92, 255),
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.7
            }
        };
        TextBlock reasonText = new()
        {
            Text = reason,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 134, 226)),
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 1000,
            Margin = new Thickness(30, 0, 30, 42)
        };
        WpfButton closeButton = new()
        {
            Content = "Close Aurora 21",
            Width = 240,
            Height = 56,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Background = new SolidColorBrush(Color.FromRgb(133, 47, 191)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 139, 255)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(198, 92, 255),
                BlurRadius = 20,
                ShadowDepth = 0,
                Opacity = 0.75
            }
        };
        closeButton.Click += (_, _) =>
        {
            allowImmediateClose = true;
            foreach (Window openWarning in banWarningWindows.ToArray()) openWarning.Close();
            banWarningWindows.Clear();
            Close();
        };

        StackPanel content = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { logo, heading, reasonText, closeButton }
        };
        Border panel = new()
        {
            Margin = new Thickness(48),
            Padding = new Thickness(32),
            BorderBrush = new SolidColorBrush(Color.FromRgb(198, 92, 255)),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromRgb(22, 8, 30)),
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(198, 92, 255),
                BlurRadius = 42,
                ShadowDepth = 0,
                Opacity = 0.7
            },
            Child = content
        };

        return new Window
        {
            Title = "Aurora 21 access banned",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Topmost = true,
            ShowInTaskbar = true,
            Background = new SolidColorBrush(Color.FromRgb(8, 2, 12)),
            Content = panel
        };
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private void CompleteStartWorkflow(Process process, ProcessDiagnosticCapture capture)
    {
        int exitCode = SafeExitCode(process);
        if (exitCode != 0)
        {
            gameLaunchDetectionArmed = false;
            AddConnectionLog(
                "FIFA 21 launch workflow failed",
                BuildProcessFailureDetail(capture, exitCode, includeRuntimeDiagnostics: true),
                ConnectionLogSeverity.Error);
        }
        else
        {
            AddConnectionLog(
                "Launch script completed",
                "Launch-LocalFUT.ps1 exited with code 0. Aurora 21 is waiting for the runtime to report FIFA 21 running and for the new game window to appear.",
                ConnectionLogSeverity.Success);
        }

        if (!gameLaunchSequenceStarted)
        {
            SetStartControlsEnabled(true);
            WorkflowStatusText.Text = exitCode == 0 ? "Launch completed." : "Launch failed. Open Connection for details.";
            RefreshRuntimeStatus();
        }
    }

    private void CompleteStopWorkflow(Process process, ProcessDiagnosticCapture capture)
    {
        int exitCode = SafeExitCode(process);
        StopButton.IsEnabled = true;
        SetStartControlsEnabled(true);
        WorkflowStatusText.Text = exitCode == 0 ? "Stopped." : "Stop failed. Open Connection for details.";

        if (exitCode == 0)
        {
            AddConnectionLog(
                "Aurora 21 services stopped",
                "Stop-LocalFUT.ps1 exited successfully with code 0.",
                ConnectionLogSeverity.Success);
        }
        else
        {
            AddConnectionLog(
                "Stopping Aurora 21 services failed",
                BuildProcessFailureDetail(capture, exitCode, includeRuntimeDiagnostics: true),
                ConnectionLogSeverity.Error);
        }

        RefreshRuntimeStatus();
    }

    private static int SafeExitCode(Process process)
    {
        try { process.WaitForExit(); return process.ExitCode; }
        catch { return -1; }
    }

    private void SetStartControlsEnabled(bool enabled) => LaunchButton.IsEnabled = enabled && pythonReady && !banWarningShown;

    private bool ValidateLaunchPrerequisites()
    {
        LauncherSettingsSnapshot settings = RuntimeCompatibility.LoadSettings(runtimeSnapshot.Root);
        string launchScript = IOPath.Combine(runtimeSnapshot.Root, "Launch-LocalFUT.ps1");
        if (!File.Exists(launchScript))
        {
            AddConnectionLog(
                "Launch validation failed: launcher script missing",
                $"Expected file: {launchScript}\nFile.Exists returned false. Re-download the complete player launcher folder.",
                ConnectionLogSeverity.Error);
            ShowLaunchConfigurationError("Required launcher files are missing. Re-download the complete player launcher folder.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.GamePath) || !File.Exists(settings.GamePath))
        {
            AddConnectionLog(
                "Launch validation failed: FIFA21.exe not found",
                $"Configured game path: {(string.IsNullOrWhiteSpace(settings.GamePath) ? "<empty>" : settings.GamePath)}\nFile.Exists returned false. Select the installed FIFA21.exe on the Game Installation tab.",
                ConnectionLogSeverity.Error);
            ShowLaunchConfigurationError("Choose a valid FIFA21.exe location before launching.");
            return false;
        }

        string serverPath = IOPath.Combine(settings.SourcePath, "localfut21", "server.py");
        string redirectorPath = IOPath.Combine(settings.SourcePath, "redirector_tls.py");
        if (!Directory.Exists(settings.SourcePath) || !File.Exists(serverPath) || !File.Exists(redirectorPath))
        {
            AddConnectionLog(
                "Launch validation failed: runtime files missing",
                $"Runtime source folder: {settings.SourcePath}\nFolder exists: {Directory.Exists(settings.SourcePath)}\nserver.py exists: {File.Exists(serverPath)} ({serverPath})\nredirector_tls.py exists: {File.Exists(redirectorPath)} ({redirectorPath})\nRe-download the complete launcher folder.",
                ConnectionLogSeverity.Error);
            ShowLaunchConfigurationError("Required player connection files are missing. Re-download the complete launcher folder.");
            return false;
        }

        AddConnectionLog(
            "Launch validation passed",
            $"FIFA executable: {settings.GamePath}\nRuntime source: {settings.SourcePath}\nRequired launch/runtime files are present.",
            ConnectionLogSeverity.Success);
        return true;
    }

    private void ShowLaunchConfigurationError(string message)
    {
        LoadSettings();
        SetSettingsStatus(message, true);
        ShowView(SettingsView, SettingsNavButton);
    }

    private void BrowseGameButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Title = "Select FIFA 21 executable", Filter = "FIFA 21 executable (FIFA21.exe)|FIFA21.exe|Executable files (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
        string current = GamePathTextBox.Text.Trim();
        if (File.Exists(current))
        {
            dialog.InitialDirectory = IOPath.GetDirectoryName(current);
            dialog.FileName = IOPath.GetFileName(current);
        }
        if (dialog.ShowDialog(this) == true)
        {
            GamePathTextBox.Text = dialog.FileName;
            SetSettingsStatus("Game location selected. Choose Save.", false);
        }
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        string gamePath = GamePathTextBox.Text.Trim().Trim('"');
        if (!File.Exists(gamePath))
        {
            SetSettingsStatus("Game location must point to an existing FIFA21.exe file.", true);
            GamePathTextBox.Focus();
            return;
        }
        if (!string.Equals(IOPath.GetFileName(gamePath), "FIFA21.exe", StringComparison.OrdinalIgnoreCase))
        {
            SetSettingsStatus("Select FIFA21.exe, not another executable.", true);
            GamePathTextBox.Focus();
            return;
        }
        try
        {
            gamePath = IOPath.GetFullPath(gamePath);
            bool alwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;
            RuntimeCompatibility.SavePlayerSettings(
                runtimeSnapshot.Root,
                gamePath,
                EaActivationCheckBox.IsChecked == true,
                alwaysOnTop);
            soundEffectsMuted = SoundEffectsMuteCheckBox.IsChecked == true;
            PersistSoundEffectsSettings(SoundEffectsVolumeSlider.Value, soundEffectsMuted);
            Topmost = alwaysOnTop;
            GamePathTextBox.Text = gamePath;
            SetSettingsStatus("Launcher settings saved.", false, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetSettingsStatus("Could not save settings: " + exception.Message, true);
        }
    }

    private async void CreateDesktopShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        await CreateSettingsShortcutAsync(
            IOPath.Combine(desktop, "Aurora 21 (Alpha v1).lnk"),
            "Desktop");
    }

    private async void CreateStartMenuShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        await CreateSettingsShortcutAsync(
            IOPath.Combine(programs, "Aurora 21 (Alpha v1).lnk"),
            "Start Menu");
    }

    private async Task CreateSettingsShortcutAsync(string shortcutPath, string locationName)
    {
        try
        {
            SetSettingsStatus($"Creating {locationName} shortcut…", false);
            string launcherExecutable = ResolveLauncherExecutablePath();
            await CreateWindowsShortcutAsync(shortcutPath, launcherExecutable);
            SetSettingsStatus($"{locationName} shortcut added.", false, true);
            AddConnectionLog(
                $"{locationName} shortcut created",
                $"Shortcut: {shortcutPath}\nTarget: {launcherExecutable}",
                ConnectionLogSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetSettingsStatus($"Could not create the {locationName} shortcut: {exception.Message}", true);
            AddConnectionLog(
                $"{locationName} shortcut creation failed",
                $"Shortcut: {shortcutPath}\n{exception.GetType().Name}: {exception.Message}",
                ConnectionLogSeverity.Error);
        }
    }

    private void SetSettingsStatus(string message, bool isError, bool isSuccess = false)
    {
        SettingsStatusText.Text = message;
        SettingsStatusText.Foreground = isError ? new SolidColorBrush(Color.FromRgb(255, 111, 120)) : isSuccess ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(112, 112, 119));
        SettingsStatusText.FontSize = isError ? 12 : 9;
        SettingsStatusText.FontWeight = isError ? FontWeights.SemiBold : FontWeights.Normal;
        SettingsStatusText.LineHeight = isError ? 17 : double.NaN;
    }

    private void ApplyRuntimeSnapshot(RuntimeSnapshot snapshot)
    {
        string gameState = CondenseState(snapshot.GameState);
        GameStatusText.Text = gameState;
        HeroGameStatusText.Text = gameState;
        GameStatusDot.Fill = IsRunningState(snapshot.GameState)
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("MutedDotBrush");

        // The secure-route UI is only allowed to turn green after a check in
        // this launcher session. status.json is persisted for diagnostics and
        // may otherwise contain yesterday's successful launch state.
        if (secureConnectionVerified)
        {
            ConnectionStatusText.Text = "Connected securely.";
            HeroConnectionStatusText.Text = "Connected";
            ConnectionStatusDot.Fill = (Brush)FindResource("SuccessBrush");
            return;
        }

        if (wireGuardProcess is { HasExited: false })
        {
            return;
        }

        if (!snapshot.IsCompatible)
        {
            ConnectionStatusText.Text = "Required player connection files are missing.";
            HeroConnectionStatusText.Text = "Files missing";
            ConnectionStatusDot.Fill = (Brush)FindResource("DangerBrush");
            return;
        }

        if (!RuntimeStatusWasUpdatedThisSession())
        {
            ConnectionStatusText.Text = "Secure connection has not been verified this session.";
            HeroConnectionStatusText.Text = "Not checked";
            ConnectionStatusDot.Fill = (Brush)FindResource("MutedDotBrush");
            return;
        }

        string connectionState = CondenseState(snapshot.ConnectionState);
        ConnectionStatusText.Text = connectionState;
        HeroConnectionStatusText.Text = CondenseHeroState(connectionState);
        ConnectionStatusDot.Fill = IsConnectedState(snapshot.ConnectionState)
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("MutedDotBrush");
    }

    private static bool IsRunningState(string value)
    {
        string normalized = value.ToLowerInvariant();
        return normalized.Contains("running") && !normalized.Contains("not running");
    }

    private static bool IsConnectedState(string value)
    {
        string normalized = value.ToLowerInvariant();
        return (normalized.Contains("connected") && !normalized.Contains("not connected")) || normalized.Contains("ready");
    }

    private static string CondenseHeroState(string value) => value.Length <= 24 ? value : value[..23] + "…";

    private void RefreshRuntimeStatus()
    {
        RuntimeSnapshot latest = RuntimeCompatibility.Discover();
        ApplyRuntimeSnapshot(latest);
        LogRuntimeSnapshotChanges(latest);
    }
    private static string CondenseState(string value) => value.Length <= 70 ? value : value[..69] + "…";

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private async void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (minimizeAnimationInProgress || restoreAnimationInProgress || manualExitSequenceStarted || gameLaunchSequenceStarted)
        {
            return;
        }

        minimizeAnimationInProgress = true;
        restoreAnimationInProgress = false;
        launcherWasMinimized = true;

        try
        {
            // Visually tuck the launcher inward before Windows sends it to the taskbar.
            // The surrounding Window background remains visible for a fraction of a
            // second, which makes the UI feel as though it physically compacts away.
            LauncherShell.BeginAnimation(UIElement.OpacityProperty, null);
            LauncherShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            LauncherShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            LauncherShellTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            LauncherShell.Opacity = 1;
            LauncherShellScale.ScaleX = 1;
            LauncherShellScale.ScaleY = 1;
            LauncherShellTranslate.Y = 0;

            CubicEase easeIn = new() { EasingMode = EasingMode.EaseIn };
            TimeSpan duration = TimeSpan.FromMilliseconds(260);

            DoubleAnimation opacity = new(1, 0.28, duration)
            {
                EasingFunction = easeIn,
                FillBehavior = FillBehavior.HoldEnd
            };
            DoubleAnimation scaleX = new(1, 0.86, duration)
            {
                EasingFunction = easeIn,
                FillBehavior = FillBehavior.HoldEnd
            };
            DoubleAnimation scaleY = new(1, 0.86, duration)
            {
                EasingFunction = easeIn,
                FillBehavior = FillBehavior.HoldEnd
            };
            DoubleAnimation shiftDown = new(0, 16, duration)
            {
                EasingFunction = easeIn,
                FillBehavior = FillBehavior.HoldEnd
            };

            TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            scaleY.Completed += (_, _) => completion.TrySetResult(true);

            LauncherShell.BeginAnimation(UIElement.OpacityProperty, opacity);
            LauncherShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            LauncherShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            LauncherShellTranslate.BeginAnimation(TranslateTransform.YProperty, shiftDown);

            await completion.Task;

            // Keep the compact values as the restore animation's starting pose.
            LauncherShell.BeginAnimation(UIElement.OpacityProperty, null);
            LauncherShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            LauncherShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            LauncherShellTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            LauncherShell.Opacity = 0.28;
            LauncherShellScale.ScaleX = 0.86;
            LauncherShellScale.ScaleY = 0.86;
            LauncherShellTranslate.Y = 16;

            WindowState = WindowState.Minimized;
        }
        finally
        {
            minimizeAnimationInProgress = false;
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            launcherWasMinimized = true;
            return;
        }

        if (!launcherWasMinimized || manualExitSequenceStarted || gameLaunchSequenceStarted)
        {
            return;
        }

        launcherWasMinimized = false;
        _ = RunRestoreAnimationAsync();
    }

    private async Task RunRestoreAnimationAsync()
    {
        if (restoreAnimationInProgress)
        {
            return;
        }

        restoreAnimationInProgress = true;
        try
        {
            LauncherShell.BeginAnimation(UIElement.OpacityProperty, null);
            LauncherShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            LauncherShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            LauncherShellTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            // Always begin from the same tucked-away pose, including if Windows was
            // minimized through a keyboard/system command rather than our button.
            LauncherShell.Opacity = 0.28;
            LauncherShellScale.ScaleX = 0.86;
            LauncherShellScale.ScaleY = 0.86;
            LauncherShellTranslate.Y = 16;

            // Let WPF present the restored window for one dispatcher turn before
            // expanding it, so the first visible frame is intentionally compact.
            await Dispatcher.Yield(DispatcherPriority.Render);

            CubicEase easeOut = new() { EasingMode = EasingMode.EaseOut };
            TimeSpan duration = TimeSpan.FromMilliseconds(380);

            DoubleAnimation opacity = new(0.28, 1, duration)
            {
                EasingFunction = easeOut,
                FillBehavior = FillBehavior.HoldEnd
            };
            DoubleAnimation scaleX = new(0.86, 1, duration)
            {
                EasingFunction = easeOut,
                FillBehavior = FillBehavior.HoldEnd
            };
            DoubleAnimation scaleY = new(0.86, 1, duration)
            {
                EasingFunction = easeOut,
                FillBehavior = FillBehavior.HoldEnd
            };
            DoubleAnimation shiftUp = new(16, 0, duration)
            {
                EasingFunction = easeOut,
                FillBehavior = FillBehavior.HoldEnd
            };

            TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            scaleY.Completed += (_, _) => completion.TrySetResult(true);

            LauncherShell.BeginAnimation(UIElement.OpacityProperty, opacity);
            LauncherShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            LauncherShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            LauncherShellTranslate.BeginAnimation(TranslateTransform.YProperty, shiftUp);

            await completion.Task;
            ResetLauncherShellVisuals();
        }
        finally
        {
            restoreAnimationInProgress = false;
        }
    }

    private void ResetLauncherShellVisuals()
    {
        LauncherShell.BeginAnimation(UIElement.OpacityProperty, null);
        LauncherShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        LauncherShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        LauncherShellTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        LauncherShell.Opacity = 1;
        LauncherShellScale.ScaleX = 1;
        LauncherShellScale.ScaleY = 1;
        LauncherShellTranslate.Y = 0;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
