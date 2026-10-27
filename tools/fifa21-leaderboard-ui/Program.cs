using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FrostySdk.Frostbite;
using FrostySdk.Managers;
using FrostySdk.Interfaces;

if (args is ["--self-test-localisation"])
{
    LocalisationTextEdit.SelfTest();
    ChampionsTextEdit.SelfTest();
    return;
}

if (args is ["--self-test-zip"])
{
    ZipDependencySelfTest.Run();
    return;
}

if (args is ["--self-test-fmt"])
{
    FmtWarningSelfTest.Run();
    return;
}

if (args is ["--self-test-currency-art"])
{
    CurrencyArt.SelfTest();
    return;
}

if (args is ["--merge-currency-art", var profilePath, var artworkPath])
{
    var profile = JsonNode.Parse(File.ReadAllText(profilePath))!.AsObject();
    var artwork = JsonNode.Parse(File.ReadAllText(artworkPath))!.AsArray();
    foreach (var edit in artwork.OfType<JsonObject>()) CurrencyArt.ReadReplacement(edit);
    profile["artwork"] = artwork.DeepClone();
    profile.Remove("revision");
    profile["revision"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToJsonString()))).ToLowerInvariant();
    File.WriteAllText(profilePath, profile.ToJsonString(new() { WriteIndented = true }));
    Console.WriteLine("Updated client profile revision with verified bundled currency artwork.");
    return;
}

if (args is ["--merge-champions-art", var championsProfilePath, var championsArtworkPath])
{
    var profile = JsonNode.Parse(File.ReadAllText(championsProfilePath))!.AsObject();
    var artwork = JsonNode.Parse(File.ReadAllText(championsArtworkPath))!.AsArray();
    var existing = profile["artwork"]!.AsArray();
    foreach (var edit in artwork.OfType<JsonObject>())
    {
        CurrencyArt.ReadReplacement(edit);
        var name = edit["name"]!.GetValue<string>();
        var previous = existing.OfType<JsonObject>().FirstOrDefault(item => item["name"]!.GetValue<string>() == name);
        if (previous is null)
            existing.Add(edit.DeepClone());
        else if (previous["sourceAssetSha256"]!.GetValue<string>() == edit["sourceAssetSha256"]!.GetValue<string>()
                 && previous["replacementFile"]!.GetValue<string>() == edit["replacementFile"]!.GetValue<string>())
        {
            var index = existing.IndexOf(previous);
            existing[index] = edit.DeepClone();
        }
        else
            throw new InvalidDataException("Champions asset conflicts with profile: " + name);
    }
    profile.Remove("revision");
    profile["revision"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToJsonString()))).ToLowerInvariant();
    File.WriteAllText(championsProfilePath, profile.ToJsonString(new() { WriteIndented = true }));
    Console.WriteLine("Updated client profile revision with verified Champions artwork.");
    return;
}

if (args is ["--merge-champions-text", var textProfilePath, var textArtworkPath])
{
    var profile = JsonNode.Parse(File.ReadAllText(textProfilePath))!.AsObject();
    var edits = JsonNode.Parse(File.ReadAllText(textArtworkPath))!.AsArray();
    foreach (var edit in edits.OfType<JsonObject>())
    {
        var key = edit["stringId"]!.GetValue<string>();
        if (profile["textEdits"]!.AsArray().OfType<JsonObject>().Any(row => row["stringId"]!.GetValue<string>() == key))
            throw new InvalidDataException("Champions text overlaps currency edit: " + key);
        foreach (var (language, value) in edit["languages"]!.AsObject())
        {
            if (!profile["languages"]!.AsArray().Any(item => item!.GetValue<string>() == language))
                throw new InvalidDataException("Unknown Champions language: " + language);
            ChampionsTextEdit.Apply(value!["source"]!.GetValue<string>(), value.AsObject(), language + "/" + key);
        }
    }
    profile["championsTextEdits"] = edits.DeepClone();
    profile.Remove("revision");
    profile["revision"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToJsonString()))).ToLowerInvariant();
    File.WriteAllText(textProfilePath, profile.ToJsonString(new() { WriteIndented = true }));
    Console.WriteLine("Updated client profile revision with verified Champions text.");
    return;
}

if (args.Length is not (3 or 4) || (args.Length == 4 && args[3] is not ("--compile" or "--verify-compiled")))
    throw new ArgumentException("Usage: Aurora21-LeaderboardUiTool <game directory> <leaderboard manifest JSON> <output directory> [--compile|--verify-compiled]");
var compile = args.Length == 4 && args[3] == "--compile";
var verify = args.Length == 4 && args[3] == "--verify-compiled";
// FMT resolves its SDK relative to the process working directory. Anchor it to the tool installation,
// after resolving caller-supplied paths so invoking from another directory remains supported.
args = args.Take(3).Select(Path.GetFullPath).ToArray();
if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "SDK", "FIFA21SDK.dll")))
    throw new InvalidDataException("Missing FIFA21 SDK beside the tool.");
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var manifest = JsonNode.Parse(File.ReadAllText(args[1]))!.AsObject();
if (manifest["schema"]?.GetValue<int>() != 1 || manifest["game"]?.GetValue<string>() != "FIFA21") throw new InvalidDataException("Unsupported client manifest");
var revision = manifest["revision"]!.GetValue<string>();
var canonical = (JsonObject)manifest.DeepClone(); canonical.Remove("revision");
if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToJsonString()))).ToLowerInvariant() != revision) throw new InvalidDataException("Manifest revision mismatch");
foreach (var (file, key) in new[] { ("FIFA21.exe", "gameExecutableSha256"), ("CardsDLL_Win64_retail.dll", "cardsLibrarySha256") })
{
    using var stream = File.OpenRead(Path.Combine(args[0], file));
    if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != manifest["profile"]![key]!.GetValue<string>()) throw new InvalidDataException($"Unsupported game file: {file}");
}
// Only legacy localisation assets are needed. Avoid indexing every unrelated EBX object on a cold cache.
if (!new CacheManager().LoadData("FIFA21", args[0], new Logger(), false, false)) throw new InvalidDataException("Native asset index failed");
if (!FrostySdk.TypeLibrary.Initialize(true)) throw new InvalidDataException("FIFA21 SDK could not be loaded.");
// This reviewed FIFA21 cache can omit the collector type; the legacy manager needs it to find the asset bundle.
foreach (var entry in AssetManager.Instance.EnumerateEbx().Where(e => e.Name == "core/chunkfiles/chunkfilecollector" && string.IsNullOrWhiteSpace(e.Type)))
    entry.Type = "ChunkFileCollector";
CompiledMod.CaptureNativeCollectors();
if (!verify) CaptionMod.Build(AssetManager.Instance, AssetManager.Instance.EnumerateCustomAssets("legacy").ToArray(), args[2], manifest);
if (compile) await CompiledMod.BuildAndVerify(args[0], args[2], manifest, new Logger());
if (verify) CompiledMod.Verify(args[0], args[2], manifest, new Logger());

sealed class Logger : ILogger
{
    public void Log(string text, params object[] values) { }
    public void LogWarning(string text, params object[] values) => Console.WriteLine(text);
    public void LogError(string text, params object[] values) => Console.WriteLine(text);
}
