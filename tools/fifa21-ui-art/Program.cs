using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using FrostySdk.Frostbite;
using FrostySdk.Interfaces;
using FrostySdk.Managers;

// FMT dynamically loads libraries and its profile relative to its installation directory.
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
Export(args);

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static void Export(string[] args)
{
    if (args.Length is not (3 or 4))
        throw new ArgumentException("Usage: Aurora21.UiArtExport <game directory> <raw output> <selection.json> [limit]");
    var game = Path.GetFullPath(args[0]);
    var output = Path.GetFullPath(args[1]);
    var selection = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.GetFullPath(args[2])))!;
    var limit = args.Length == 4 ? int.Parse(args[3]) : int.MaxValue;
    if (limit <= 0) throw new ArgumentException("Limit must be positive.");
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "_GameCaches", "fifa21.cache")))
        throw new InvalidDataException("Existing FIFA21 cache required; this exporter does not initiate a cold scan.");
    var expected = new Dictionary<string, string>
    {
        ["FIFA21.exe"] = "44d0acc62a214c6f4dab2b8a4f4bdd068869c636f15cc262035b4edc862a1494",
        ["CardsDLL_Win64_retail.dll"] = "37133be975c0552c919e2901b7da203eb458792b5bf43764821ad687de548a49",
    };
    foreach (var (file, hash) in expected)
        if (HashFile(Path.Combine(game, file)) != hash) throw new InvalidDataException("Unexpected game build: " + file);
    Directory.CreateDirectory(output);
    Console.WriteLine("Reusing the existing FIFA21 asset cache; game build verified.");
    if (!new CacheManager().LoadData("FIFA21", game, new Logger(), false, false))
        throw new InvalidDataException("Could not load the cached asset index.");
    if (!FrostySdk.TypeLibrary.Initialize(true)) throw new InvalidDataException("SDK initialization failed.");
    var manager = AssetManager.Instance;
    foreach (var entry in manager.EnumerateEbx().Where(e => e.Name == "core/chunkfiles/chunkfilecollector" && string.IsNullOrWhiteSpace(e.Type)))
        entry.Type = "ChunkFileCollector";
    var assets = manager.EnumerateCustomAssets("legacy").ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    var reports = new List<object>();
    var errors = new List<object>();
    var requested = selection.Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
    Console.WriteLine($"Indexed {assets.Count:N0} legacy assets; exporting {requested.Length:N0} selected assets.");
    foreach (var name in requested)
    {
        try
        {
            if (!assets.TryGetValue(name, out var entry)) throw new InvalidDataException("Selected asset is absent from current index.");
            var relative = name.Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(output, "raw", relative));
            var allowedRoot = Path.GetFullPath(Path.Combine(output, "raw")) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe asset path.");
            using var input = manager.GetCustomAsset("legacy", entry);
            if (input is null) throw new InvalidDataException("Asset stream is missing.");
            using var bytes = new MemoryStream();
            input.CopyTo(bytes);
            var data = bytes.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Resume only when the existing output is byte-for-byte identical to the current native source.
            if (!File.Exists(destination) || HashFile(destination) != hash) File.WriteAllBytes(destination, data);
            reports.Add(new { name, type = entry.Type, bytes = data.Length, sha256 = hash });
        }
        catch (Exception error)
        {
            errors.Add(new { name, error = error.Message });
            Console.WriteLine($"FAILED {name}: {error.Message}");
        }
        if ((reports.Count + errors.Count) % 100 == 0) Console.WriteLine($"Exported {reports.Count:N0}/{requested.Length:N0}; failures {errors.Count}.");
    }
    foreach (var (file, hash) in expected)
        if (HashFile(Path.Combine(game, file)) != hash) throw new InvalidDataException("Game file changed during extraction: " + file);
    File.WriteAllText(Path.Combine(output, "native-manifest.json"), JsonSerializer.Serialize(new
    {
        schema = 1, game = "FIFA21", extractedAt = DateTimeOffset.UtcNow,
        gameProfile = expected, fmtRevision = "67b35957725fc961e4262afb8be08e9cd1df239e",
        scope = "Selected native FUT and supporting shared UI assets; excludes the general player/kit/crest collections.",
        requested = requested.Length, assets = reports, failures = errors,
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Finished: {reports.Count:N0} assets, {errors.Count} failures. Game executable and CardsDLL hashes unchanged.");
    if (errors.Count != 0) Environment.ExitCode = 1;
}

static string HashFile(string path)
{
    using var file = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
}
sealed class Logger : ILogger
{
    public void Log(string value, params object[] vars)
    {
        // The reviewed AssetManager logs this immediately before its fallback archive scan.
        // Fail closed if the existing cache is invalid instead of rebuilding it implicitly.
        if (value.Contains("Cache Needs to Built/Updated", StringComparison.Ordinal))
            throw new InvalidDataException("Cached index rejected; refusing an automatic game archive rescan.");
    }
    public void LogWarning(string value, params object[] vars) => Console.WriteLine(value);
    public void LogError(string value, params object[] vars) => Console.WriteLine(value);
}
