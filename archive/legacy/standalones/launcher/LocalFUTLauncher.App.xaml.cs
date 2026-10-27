using System.Windows;

namespace Project21Launcher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        MainWindow window = new();
        MainWindow = window;
        window.Show();

        if (args.Args.Any(argument => string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(Shutdown));
        }
    }
}
