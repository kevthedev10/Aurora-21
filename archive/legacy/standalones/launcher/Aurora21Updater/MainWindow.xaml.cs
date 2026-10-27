using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Aurora21Updater;

public partial class MainWindow : Window
{
    private const long MaximumPackageBytes = 1_500_000_000;
    private readonly Uri manifestUri;
    private readonly DispatcherTimer marqueeTimer;
    private double marqueePosition = -140;
    private string? installedLauncherPath;

    public MainWindow(Uri manifestUri)
    {
        this.manifestUri = manifestUri;
        InitializeComponent();
        marqueeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        marqueeTimer.Tick += MarqueeTimer_Tick;
        Loaded += async (_, _) => await InstallLatestAsync();
        SizeChanged += (_, _) => { if (!marqueeTimer.IsEnabled) SetProgressWidth(currentProgress); };
    }

    private int currentProgress;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(installedLauncherPath) && File.Exists(installedLauncherPath))
        {
            Process.Start(new ProcessStartInfo(installedLauncherPath)
            {
                WorkingDirectory = Path.GetDirectoryName(installedLauncherPath),
                UseShellExecute = true
            });
        }
        Close();
    }

    private async Task InstallLatestAsync()
    {
        string appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aurora21");
        string installRoot = Path.Combine(appRoot, "Launcher");
        string backupRoot = Path.Combine(appRoot, "Launcher.previous");
        string stagingRoot = Path.Combine(appRoot, ".update-" + Guid.NewGuid().ToString("N"));
        string archivePath = Path.Combine(stagingRoot, "launcher.zip");
        string extractRoot = Path.Combine(stagingRoot, "extracted");

        try
        {
            if (Process.GetProcessesByName("Aurora21-Launcher").Any(process => !process.HasExited))
                throw new InvalidOperationException("Close Aurora21-Launcher.exe, then run the updater again.");

            Directory.CreateDirectory(stagingRoot);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Aurora21-Updater/1.1");

            SetStage("Checking the Aurora release channel", "Downloading the current release manifest...", true, "CONNECTING");
            string manifestJson = await DownloadManifestAsync(client);
            ReleaseManifest manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The release manifest was empty.");
            ValidateManifest(manifest);

            Uri packageUri = new(manifestUri, manifest.PackageUrl);
            if (packageUri.Scheme != Uri.UriSchemeHttps || !string.Equals(packageUri.Host, manifestUri.Host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package URL must use HTTPS on the manifest server.");

            if (TryUseInstalledRelease(installRoot, manifest))
            {
                ShowReady(manifest.Version, installRoot, alreadyCurrent: true);
                return;
            }

            SetStage($"Downloading Aurora 21 {manifest.Version}", "Keep this window open while the launcher package downloads.", false, "0%");
            await DownloadPackageAsync(client, packageUri, archivePath, manifest.Size);
            SetStage("Verifying the release", "Checking the complete package against its secure SHA-256 value...", true, "VERIFYING");
            string actualHash = await ComputeSha256Async(archivePath);
            if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package verification failed. Expected {manifest.Sha256}, received {actualHash}.");

            SetStage("Installing the launcher", "Moving the validated release into your Windows AppData folder...", true, "INSTALLING");
            Directory.CreateDirectory(extractRoot);
            ExtractArchiveSafely(archivePath, extractRoot);
            InstallWithRollback(FindPackageRoot(extractRoot), installRoot, backupRoot);
            File.WriteAllText(Path.Combine(installRoot, ".aurora-release.json"), manifestJson);

            installedLauncherPath = Path.Combine(installRoot, "Aurora21-Launcher.exe");
            ShowReady(manifest.Version, installRoot, alreadyCurrent: false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        {
            StopMarquee();
            SetProgress(0);
            EyebrowText.Text = "UPDATE INTERRUPTED";
            HeadingText.Text = "Update could not be installed";
            StatusText.Text = exception.Message;
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 146, 171));
            StageDot.Fill = new SolidColorBrush(Color.FromRgb(255, 107, 145));
            PercentText.Text = "ACTION REQUIRED";
            ActionButton.Content = "CLOSE";
            ActionButton.IsEnabled = true;
        }
        finally
        {
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true); }
            catch { }
        }
    }

    private bool TryUseInstalledRelease(string installRoot, ReleaseManifest remoteManifest)
    {
        string launcherPath = Path.Combine(installRoot, "Aurora21-Launcher.exe");
        string markerPath = Path.Combine(installRoot, ".aurora-release.json");
        if (!File.Exists(launcherPath) || !File.Exists(markerPath)) return false;

        try
        {
            ReleaseManifest? installedManifest = JsonSerializer.Deserialize<ReleaseManifest>(
                File.ReadAllText(markerPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (installedManifest is null) return false;

            bool matches = string.Equals(installedManifest.Version, remoteManifest.Version, StringComparison.OrdinalIgnoreCase)
                && string.Equals(installedManifest.Sha256, remoteManifest.Sha256, StringComparison.OrdinalIgnoreCase)
                && installedManifest.Size == remoteManifest.Size;
            if (matches) installedLauncherPath = launcherPath;
            return matches;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ShowReady(string version, string installRoot, bool alreadyCurrent)
    {
        StopMarquee();
        SetProgress(100);
        EyebrowText.Text = alreadyCurrent ? "ALREADY UP TO DATE" : "INSTALLATION COMPLETE";
        HeadingText.Text = "Aurora 21 is ready";
        StatusText.Text = alreadyCurrent
            ? $"Version {version} is already installed. No download is needed."
            : $"Version {version} is installed in:\n{installRoot}";
        StatusText.Foreground = (Brush)FindResource("TextMuted");
        ActionButton.Content = "LAUNCH AURORA 21";
        ActionButton.IsEnabled = true;
    }

    private async Task<string> DownloadManifestAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(manifestUri, HttpCompletionOption.ResponseContentRead);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new HttpRequestException($"Could not download the release manifest: {manifestUri} returned HTTP 404. The VPS launcher release has not been published yet.");
            throw new HttpRequestException($"Could not download the release manifest: {manifestUri} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }
        return await response.Content.ReadAsStringAsync();
    }

    private async Task DownloadPackageAsync(HttpClient client, Uri packageUri, string destination, long expectedSize)
    {
        using HttpResponseMessage response = await client.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Could not download the launcher package: {packageUri} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

        long total = response.Content.Headers.ContentLength ?? expectedSize;
        if (total <= 0 || total > MaximumPackageBytes) throw new InvalidDataException("The server reported an invalid launcher package size.");
        await using Stream input = await response.Content.ReadAsStreamAsync();
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        byte[] buffer = new byte[128 * 1024];
        long received = 0;
        int count;
        while ((count = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count));
            received += count;
            SetProgress((int)Math.Clamp(received * 100L / total, 0, 100));
            StatusText.Text = $"Downloaded {received / 1048576d:N1} MB of {total / 1048576d:N1} MB";
        }
        if (expectedSize > 0 && received != expectedSize)
            throw new InvalidDataException($"The downloaded package size was {received} bytes; expected {expectedSize} bytes.");
    }

    private void SetStage(string heading, string detail, bool marquee, string progressLabel)
    {
        HeadingText.Text = heading;
        StatusText.Text = detail;
        PercentText.Text = progressLabel;
        if (marquee) StartMarquee(); else { StopMarquee(); SetProgress(0); }
    }

    private void StartMarquee()
    {
        marqueePosition = -140;
        ProgressFill.Width = 140;
        marqueeTimer.Start();
    }

    private void StopMarquee() => marqueeTimer.Stop();

    private void MarqueeTimer_Tick(object? sender, EventArgs e)
    {
        double width = Math.Max(1, ProgressTrack.ActualWidth - 2);
        marqueePosition += Math.Max(3.2, width / 170);
        if (marqueePosition > width) marqueePosition = -140;
        Canvas.SetLeft(ProgressFill, marqueePosition);
    }

    private void SetProgress(int percentage)
    {
        currentProgress = Math.Clamp(percentage, 0, 100);
        SetProgressWidth(currentProgress);
        PercentText.Text = $"{currentProgress}%";
    }

    private void SetProgressWidth(int percentage)
    {
        double width = Math.Max(0, ProgressTrack.ActualWidth - 2);
        ProgressFill.Width = width * percentage / 100d;
        Canvas.SetLeft(ProgressFill, 0);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input));
    }

    private static void ValidateManifest(ReleaseManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.PackageUrl))
            throw new InvalidDataException("The release manifest is missing its version or package URL.");
        if (string.IsNullOrWhiteSpace(manifest.Sha256) || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The release manifest contains an invalid SHA-256 value.");
        if (manifest.Size <= 0 || manifest.Size > MaximumPackageBytes)
            throw new InvalidDataException("The release manifest contains an invalid package size.");
    }

    private static void ExtractArchiveSafely(string archivePath, string destinationRoot)
    {
        string normalizedRoot = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string destination = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The launcher archive contains an unsafe file path.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, false);
        }
    }

    private static string FindPackageRoot(string extractRoot)
    {
        string[] launchers = Directory.GetFiles(extractRoot, "Aurora21-Launcher.exe", SearchOption.AllDirectories);
        if (launchers.Length != 1) throw new InvalidDataException("The package must contain exactly one Aurora21-Launcher.exe.");
        return Path.GetDirectoryName(launchers[0])!;
    }

    private static void InstallWithRollback(string packageRoot, string installRoot, string backupRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(installRoot)!);
        if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
        bool previousMoved = false;
        try
        {
            if (Directory.Exists(installRoot)) { Directory.Move(installRoot, backupRoot); previousMoved = true; }
            Directory.Move(packageRoot, installRoot);
        }
        catch
        {
            if (Directory.Exists(installRoot)) Directory.Delete(installRoot, true);
            if (previousMoved && Directory.Exists(backupRoot)) Directory.Move(backupRoot, installRoot);
            throw;
        }
    }
}

internal sealed class ReleaseManifest
{
    public string Version { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
}
