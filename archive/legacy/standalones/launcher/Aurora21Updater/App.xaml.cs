using System.Windows;

namespace Aurora21Updater;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new MainWindow(ResolveManifestUri(e.Args)).Show();
    }

    private static Uri ResolveManifestUri(string[] args)
    {
        const string defaultUrl = "https://project21.duckdns.org/downloads/launcher/latest.json";
        string value = defaultUrl;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--manifest-url=", StringComparison.OrdinalIgnoreCase))
                value = args[index]["--manifest-url=".Length..];
            else if (string.Equals(args[index], "--manifest-url", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                value = args[++index];
        }

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : new Uri(defaultUrl);
    }
}
