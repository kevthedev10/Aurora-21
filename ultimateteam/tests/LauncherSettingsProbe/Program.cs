using System.Text.Json.Nodes;
using Aurora21Launcher;

if (args.Length != 2)
{
    Console.Error.WriteLine("LauncherSettingsProbe requires RUNTIME_ROOT and TEST_ROOT");
    return 2;
}

var runtimeRoot = Path.GetFullPath(args[0]);
var testRoot = Path.GetFullPath(args[1]);
var previousRoot = Environment.GetEnvironmentVariable("ULTIMATETEAM21_USER_CONFIG_ROOT");
var checks = 0;

void AssertProbe(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}

try
{
    var userA = Path.Combine(testRoot, "User A");
    Environment.SetEnvironmentVariable("ULTIMATETEAM21_USER_CONFIG_ROOT", userA);
    var snapshot = RuntimeCompatibility.LoadSettings(runtimeRoot);
    var settingsPath = Path.Combine(userA, "launcher-settings.json");
    var bundledSource = Path.Combine(runtimeRoot, "runtime-source");
    AssertProbe(snapshot.GamePath == string.Empty, "First-run defaults contain no machine-specific game path");
    AssertProbe(Path.GetFullPath(snapshot.SourcePath) == Path.GetFullPath(bundledSource), "Empty sourcePath resolves to the bundled runtime");
    AssertProbe(File.Exists(settingsPath), "Missing per-user launcher settings are created outside the repository");
    AssertProbe(JsonNode.Parse(File.ReadAllText(settingsPath))?["sourcePath"]?.GetValue<string>() == string.Empty,
        "The bundled runtime is stored portably as an empty sourcePath");

    var gameDirectory = Path.Combine(testRoot, "Game é");
    Directory.CreateDirectory(gameDirectory);
    var gamePath = Path.Combine(gameDirectory, "FIFA21.exe");
    File.WriteAllText(gamePath, "test fixture; never executed");
    File.WriteAllText(settingsPath, "{\"sourcePath\":\"\",\"gamePath\":\"\",\"futureAuthSetting\":\"preserve me\"}");
    var configPath = Path.Combine(userA, "config.json");
    File.WriteAllText(configPath, "{\"futureServerSetting\":\"preserve me\"}");
    RuntimeCompatibility.SaveSettings(runtimeRoot, gamePath, bundledSource, "Unicode FC", "U21");
    var saved = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
    var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
    AssertProbe(saved["gamePath"]?.GetValue<string>() == gamePath, "A FIFA21.exe path with spaces and Unicode round-trips");
    AssertProbe(saved["sourcePath"]?.GetValue<string>() == string.Empty, "Saving the bundled runtime does not persist this checkout path");
    AssertProbe(saved["futureAuthSetting"]?.GetValue<string>() == "preserve me", "Unknown launcher settings survive save");
    AssertProbe(config["futureServerSetting"]?.GetValue<string>() == "preserve me", "Unknown server settings survive save");
    AssertProbe(config["club_name"]?.GetValue<string>() == "Unicode FC" && config["club_abbr"]?.GetValue<string>() == "U21",
        "Club settings are saved in the per-user runtime config");
    AssertProbe(!Directory.EnumerateFiles(userA, "*.tmp").Any(), "Atomic saves leave no temporary files");

    const string bad = "{ broken JSON";
    File.WriteAllText(settingsPath, bad);
    var malformed = RuntimeCompatibility.LoadSettings(runtimeRoot);
    AssertProbe(!string.IsNullOrWhiteSpace(malformed.Problem), "Malformed settings produce an explicit launcher error");
    AssertProbe(File.ReadAllText(settingsPath) == bad, "Malformed settings are not silently overwritten while loading");
    RuntimeCompatibility.SaveSettings(runtimeRoot, gamePath, bundledSource, "Recovered FC", "RFC");
    AssertProbe(JsonNode.Parse(File.ReadAllText(settingsPath))?["gamePath"]?.GetValue<string>() == gamePath,
        "Explicit Save replaces malformed settings with reviewed values");

    var userB = Path.Combine(testRoot, "User B");
    Environment.SetEnvironmentVariable("ULTIMATETEAM21_USER_CONFIG_ROOT", userB);
    var previousDirectory = Environment.CurrentDirectory;
    Environment.CurrentDirectory = testRoot;
    LauncherSettingsSnapshot second;
    try { second = RuntimeCompatibility.LoadSettings(runtimeRoot); }
    finally { Environment.CurrentDirectory = previousDirectory; }
    AssertProbe(second.GamePath == string.Empty, "A second Windows profile does not inherit the first game path");
    AssertProbe(Path.GetFullPath(second.SourcePath) == Path.GetFullPath(bundledSource), "Runtime resolution is independent of the working directory");

    Console.WriteLine($"PASS: {checks} native .NET launcher settings checks");
    return 0;
}
finally
{
    Environment.SetEnvironmentVariable("ULTIMATETEAM21_USER_CONFIG_ROOT", previousRoot);
}
