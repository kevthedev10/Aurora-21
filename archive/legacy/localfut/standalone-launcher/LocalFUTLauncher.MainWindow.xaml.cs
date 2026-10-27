using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Project21Launcher;

public partial class MainWindow : Window
{
    private readonly RuntimeSnapshot runtimeSnapshot;
    private readonly DispatcherTimer statusTimer;
    private Process? startProcess;
    private Process? stopProcess;
    private Process? wireGuardProcess;
    private Process? pythonSetupProcess;
    private bool pythonReady;
    private bool secureConnectionVerified;

    public MainWindow()
    {
        InitializeComponent();
        runtimeSnapshot = RuntimeCompatibility.Discover();
        ApplyRuntimeSnapshot(runtimeSnapshot);
        LoadSettings();
        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statusTimer.Tick += (_, _) => RefreshRuntimeStatus();
        statusTimer.Start();
        Loaded += (_, _) => RunPythonSetup();
    }

    private void LoadSettings() => GamePathTextBox.Text = RuntimeCompatibility.LoadSettings(runtimeSnapshot.Root).GamePath;

    private void ShowView(FrameworkElement view, Button activeButton)
    {
        OverviewView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
        OverviewNavButton.Style = (Style)FindResource("NavButtonStyle");
        SettingsNavButton.Style = (Style)FindResource("NavButtonStyle");
        activeButton.Style = (Style)FindResource("ActiveNavButtonStyle");
    }

    private void OverviewNavButton_Click(object sender, RoutedEventArgs e) => ShowView(OverviewView, OverviewNavButton);

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        SetSettingsStatus("The game location is saved for this Windows user.", false);
        ShowView(SettingsView, SettingsNavButton);
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (!pythonReady)
        {
            RunPythonSetup();
            return;
        }

        if (ValidateLaunchPrerequisites())
        {
            RunWireGuardWorkflow(firstRun: true, onSuccess: StartLaunchWorkflow);
        }
    }

    private void RunPythonSetup()
    {
        if (pythonSetupProcess is { HasExited: false }) return;

        string scriptPath = Path.Combine(runtimeSnapshot.Root, "Ensure-Python.ps1");
        if (!File.Exists(scriptPath))
        {
            PythonStatusText.Text = "Required Python setup file is missing.";
            WorkflowStatusText.Text = "Re-download the complete player launcher.";
            return;
        }

        pythonReady = false;
        LaunchButton.IsEnabled = false;
        PythonStatusText.Text = "Checking Python 3.12 and required components…";
        try
        {
            Process process = CreatePowerShellProcess(scriptPath);
            pythonSetupProcess = process;
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    Dispatcher.BeginInvoke(() => PythonStatusText.Text = args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    Dispatcher.BeginInvoke(() => PythonStatusText.Text = "Python setup reported an error.");
                }
            };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.Invoke(() =>
            {
                pythonReady = SafeExitCode(process) == 0;
                LaunchButton.IsEnabled = pythonReady;
                if (pythonReady)
                {
                    PythonStatusText.Text = "Python 3.12 is ready.";
                }
                else
                {
                    PythonStatusText.Text = "Python setup failed. Check your internet connection and try again.";
                    WorkflowStatusText.Text = "Launch is unavailable until Python setup completes.";
                }
            });
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            PythonStatusText.Text = "Could not start Python setup: " + exception.Message;
        }
    }

    private void StartLaunchWorkflow()
    {
        if (startProcess is { HasExited: false })
        {
            WorkflowStatusText.Text = "A launch is already running.";
            return;
        }

        WorkflowStatusText.Text = "Starting FIFA 21…";
        SetStartControlsEnabled(false);
        try
        {
            Process process = CreatePowerShellProcess(Path.Combine(runtimeSnapshot.Root, "Launch-LocalFUT.ps1"));
            startProcess = process;
            AttachProcessOutput(process);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.Invoke(() => CompleteStartWorkflow(process));
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            SetStartControlsEnabled(true);
            WorkflowStatusText.Text = "Could not start: " + exception.Message;
        }
    }

    private void SetupWireGuardButton_Click(object sender, RoutedEventArgs e) => RunWireGuardWorkflow(firstRun: true);
    private void CheckWireGuardButton_Click(object sender, RoutedEventArgs e) => RunWireGuardWorkflow(firstRun: false);

    private void RunWireGuardWorkflow(bool firstRun, Action? onSuccess = null)
    {
        if (wireGuardProcess is { HasExited: false })
        {
            WorkflowStatusText.Text = "A connection check is already running.";
            return;
        }

        string scriptName = firstRun ? "First-Run-WireGuard.ps1" : "Ensure-WireGuard-Connection.ps1";
        string scriptPath = Path.Combine(runtimeSnapshot.Root, scriptName);
        if (!File.Exists(scriptPath))
        {
            ConnectionStatusText.Text = "Connection files are missing.";
            return;
        }

        ConnectionStatusText.Text = firstRun ? "Verifying Discord and preparing the secure connection…" : "Checking the secure connection…";
        WorkflowStatusText.Text = "Please follow any setup window that appears.";
        SetWireGuardControlsEnabled(false);
        SetStartControlsEnabled(false);
        try
        {
            Process process = CreatePowerShellProcess(scriptPath);
            wireGuardProcess = process;
            AttachProcessOutput(process);
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
    WorkflowStatusText.Text = "Your access is ready.";
    onSuccess?.Invoke();
}
else
{
    secureConnectionVerified = false;
    ConnectionStatusText.Text = firstRun
        ? "Setup did not complete. Try Connect Discord again."
        : "Connection unavailable.";

    WorkflowStatusText.Text =
        "If this continues, ask a Project 21 administrator for help.";
}
            });
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            SetWireGuardControlsEnabled(true);
            SetStartControlsEnabled(true);
            secureConnectionVerified = false;
ConnectionStatusText.Text = "Could not start connection setup.";
            WorkflowStatusText.Text = exception.Message;
        }
    }

    private void SetWireGuardControlsEnabled(bool enabled)
    {
        SetupWireGuardButton.IsEnabled = enabled;
        CheckWireGuardButton.IsEnabled = enabled;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (stopProcess is { HasExited: false })
        {
            WorkflowStatusText.Text = "Stop is already running.";
            return;
        }

        string stopScript = Path.Combine(runtimeSnapshot.Root, "Stop-LocalFUT.ps1");
        if (!File.Exists(stopScript))
        {
            WorkflowStatusText.Text = "Required stop files are missing.";
            return;
        }

        WorkflowStatusText.Text = "Stopping FIFA 21 support services…";
        StopButton.IsEnabled = false;
        try
        {
            Process process = CreatePowerShellProcess(stopScript);
            stopProcess = process;
            AttachProcessOutput(process);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.Invoke(() => CompleteStopWorkflow(process));
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            StopButton.IsEnabled = true;
            WorkflowStatusText.Text = "Could not stop: " + exception.Message;
        }
    }

    private Process CreatePowerShellProcess(string scriptPath)
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
        return new Process { StartInfo = startInfo };
    }

    private void AttachProcessOutput(Process process)
    {
        process.OutputDataReceived += (_, args) => QueueWorkflowLine(args.Data, false);
        process.ErrorDataReceived += (_, args) => QueueWorkflowLine(args.Data, true);
    }

    private void QueueWorkflowLine(string? line, bool isError)
{
    if (string.IsNullOrWhiteSpace(line)) return;

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
        else if (normalized.Contains("server:") ||
                 normalized.Contains("trust bridge:"))
            WorkflowStatusText.Text = "Preparing FIFA 21…";
    });
}

    private void CompleteStartWorkflow(Process process)
    {
        int exitCode = SafeExitCode(process);
        SetStartControlsEnabled(true);
        WorkflowStatusText.Text = exitCode == 0 ? "Launch completed." : "Launch failed. Try again or contact an administrator.";
        RefreshRuntimeStatus();
    }

    private void CompleteStopWorkflow(Process process)
    {
        int exitCode = SafeExitCode(process);
        StopButton.IsEnabled = true;
        SetStartControlsEnabled(true);
        WorkflowStatusText.Text = exitCode == 0 ? "Stopped." : "Stop failed.";
        RefreshRuntimeStatus();
    }

    private static int SafeExitCode(Process process)
    {
        try { process.WaitForExit(); return process.ExitCode; }
        catch { return -1; }
    }

    private void SetStartControlsEnabled(bool enabled) => LaunchButton.IsEnabled = enabled && pythonReady;

    private bool ValidateLaunchPrerequisites()
    {
        LauncherSettingsSnapshot settings = RuntimeCompatibility.LoadSettings(runtimeSnapshot.Root);
        if (!File.Exists(Path.Combine(runtimeSnapshot.Root, "Launch-LocalFUT.ps1")))
        {
            ShowLaunchConfigurationError("Required launcher files are missing. Re-download the complete player launcher folder.");
            return false;
        }
        if (!File.Exists(settings.GamePath))
        {
            ShowLaunchConfigurationError("Choose a valid FIFA21.exe location before launching.");
            return false;
        }
        if (!Directory.Exists(settings.SourcePath) || !File.Exists(Path.Combine(settings.SourcePath, "localfut21", "server.py")) || !File.Exists(Path.Combine(settings.SourcePath, "redirector_tls.py")))
        {
            ShowLaunchConfigurationError("Required player connection files are missing. Re-download the complete launcher folder.");
            return false;
        }
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
            dialog.InitialDirectory = Path.GetDirectoryName(current);
            dialog.FileName = Path.GetFileName(current);
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
        if (!string.Equals(Path.GetFileName(gamePath), "FIFA21.exe", StringComparison.OrdinalIgnoreCase))
        {
            SetSettingsStatus("Select FIFA21.exe, not another executable.", true);
            GamePathTextBox.Focus();
            return;
        }
        try
        {
            gamePath = Path.GetFullPath(gamePath);
            RuntimeCompatibility.SavePlayerSettings(runtimeSnapshot.Root, gamePath);
            GamePathTextBox.Text = gamePath;
            SetSettingsStatus("Game location saved.", false, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetSettingsStatus("Could not save settings: " + exception.Message, true);
        }
    }

    private void SetSettingsStatus(string message, bool isError, bool isSuccess = false)
    {
        SettingsStatusText.Text = message;
        SettingsStatusText.Foreground = isError ? new SolidColorBrush(Color.FromRgb(255, 111, 120)) : isSuccess ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(112, 112, 119));
    }

    private void ApplyRuntimeSnapshot(RuntimeSnapshot snapshot)
{
    GameStatusText.Text = CondenseState(snapshot.GameState);

    // The runtime snapshot describes the FIFA/LocalFUT runtime, not the
    // authoritative Discord + WireGuard connection result.
    // Do not overwrite a connection that has already been verified.
    if (secureConnectionVerified)
    {
        ConnectionStatusText.Text = "Connected securely.";
        return;
    }

    // Do not overwrite the temporary message while a connection check is running.
    if (wireGuardProcess is { HasExited: false })
    {
        return;
    }

    ConnectionStatusText.Text = snapshot.IsCompatible
        ? CondenseState(snapshot.ConnectionState)
        : "Required player connection files are missing.";
}

    private void RefreshRuntimeStatus() => ApplyRuntimeSnapshot(RuntimeCompatibility.Discover());
    private static string CondenseState(string value) => value.Length <= 70 ? value : value[..69] + "…";

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
