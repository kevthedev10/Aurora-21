using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FrostySdk;
using FrostySdk.Frostbite;
using FrostySdk.Interfaces;
using FrostySdk.Managers;

internal static class CompiledMod
{
    private static (string Name, Guid? Guid, string Type)[] nativeCollectors = [];

    internal static void CaptureNativeCollectors()
    {
        nativeCollectors = AssetManager.Instance.EnumerateEbx("ChunkFileCollector")
            .Select(e => (e.Name, e.Guid, e.Type)).ToArray();
        Console.WriteLine("Original collector identities: " + string.Join(", ", nativeCollectors.Select(e => e.Name + " (" + e.Type + ")")));
    }
    internal static async Task BuildAndVerify(string game, string output, JsonObject manifest, ILogger logger)
    {
        if (Process.GetProcessesByName("FIFA21").Length != 0)
            throw new InvalidOperationException("Close FIFA 21 before preparing client mods.");
        var staging = Path.Combine(output, "compiler");
        if (Directory.Exists(staging)) throw new InvalidDataException("Compiler directory already exists; use a fresh output directory.");
        Directory.CreateDirectory(staging);
        var mod = Path.Combine(output, "aurora21-leaderboards-" + manifest["revision"]!.GetValue<string>() + ".fbmod");
        using (var package = new FrostbiteSdk.FrostbiteMod(mod))
        {
            var resources = package.Resources.Where(r => r.Name is not null && !r.Name.StartsWith("Icon;") && !r.Name.StartsWith("Screenshot;")).ToArray();
            var expected = manifest["languages"]!.AsArray().Select(l => "data/loc/" + l!.GetValue<string>() + ".db")
                .Concat(CurrencyArt.AssetNames(manifest)).Order(StringComparer.Ordinal).ToArray();
            if (!resources.Select(r => r.Name).Order(StringComparer.Ordinal).SequenceEqual(expected))
                throw new InvalidDataException("Client UI package contains unexpected assets.");
        }
        // The compiler reads Data/Patch through source junctions and writes independent copies under ModData.
        // Never hard-link writable CAS files to the installed game.
        foreach (var folder in new[] { "Data", "Patch" })
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", Path.Combine(staging, folder), Path.Combine(game, folder) }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            await child.WaitForExitAsync();
            if (child.ExitCode != 0) throw new IOException("Could not create source junction: " + folder);
        }
        File.Copy(Path.Combine(game, "FIFA21.exe"), Path.Combine(staging, "FIFA21.exe"));
        typeof(FileSystem).GetField("basePath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(FileSystem.Instance, staging.TrimEnd('\\') + "\\");
        Console.WriteLine("Preparing isolated ModData; this copies the installed game data on the first build.");
        var executor = new ModdingSupport.ModExecutor { fs = FileSystem.Instance };
        if (!await executor.BuildModData(logger, mod)) throw new InvalidDataException("FIFA21 mod compilation failed.");

        Verify(game, output, manifest, logger);
    }

    internal static void Verify(string game, string output, JsonObject manifest, ILogger logger)
    {
        var staging = Path.Combine(output, "compiler");
        var compiled = Path.Combine(staging, "ModData");
        foreach (var layout in new[] { "Data/layout.toc", "Patch/layout.toc" })
            if (!File.Exists(Path.Combine(compiled, layout))) throw new InvalidDataException("Missing compiled layout: " + layout);
        var compiledExecutable = Path.Combine(compiled, "FIFA21.exe");
        if (!File.Exists(compiledExecutable)) File.Copy(Path.Combine(game, "FIFA21.exe"), compiledExecutable);
        using (var executable = File.OpenRead(compiledExecutable))
            if (Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant() != manifest["profile"]!["gameExecutableSha256"]!.GetValue<string>())
                throw new InvalidDataException("Compiled executable belongs to another game build");
        typeof(FileSystem).GetField("basePath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(FileSystem.Instance, compiled.TrimEnd('\\') + "\\");
        typeof(FileSystem).GetField("cacheName", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(FileSystem.Instance, "Aurora21-leaderboards-" + manifest["revision"]!.GetValue<string>());
        FileSystem.Instance.ResetManifest();
        if (!new CacheManager().LoadData("FIFA21", compiled, logger, false, false)) throw new InvalidDataException("Compiled asset index failed.");
        if (!TypeLibrary.Initialize(true)) throw new InvalidDataException("Compiled asset SDK could not be loaded.");
        var collectorReport = new JsonArray();
        foreach (var source in nativeCollectors)
        {
            var entry = AssetManager.Instance.EnumerateEbx().Single(e => e.Name == source.Name);
            if (entry.Guid != source.Guid) throw new InvalidDataException("Compiled collector identity changed: " + source.Name);
            collectorReport.Add(new JsonObject { ["name"] = entry.Name, ["sourceType"] = source.Type, ["compiledType"] = entry.Type });
            if (string.IsNullOrWhiteSpace(entry.Type)) entry.Type = source.Type;
            else if (entry.Type != source.Type) throw new InvalidDataException("Compiled collector type differs: " + source.Name);
        }
        File.WriteAllText(Path.Combine(output, "collector-validation.json"), collectorReport.ToJsonString());
        AssetManager.Instance.GetLegacyAssetManager().Initialize(logger);
        var legacy = AssetManager.Instance.EnumerateCustomAssets("legacy").ToArray();
        File.WriteAllText(Path.Combine(output, "compiled-legacy-index.json"), JsonSerializer.Serialize(legacy.Select(e => new { e.Name, e.Type })));
        Console.WriteLine($"Compiled native index: {legacy.Length} legacy assets.");
        var languages = legacy
            .Where(e => e.Name.StartsWith("data/loc/") && e.Name.EndsWith(".db")).ToArray();
        if (languages.Length != 21) throw new InvalidDataException("Compiled language count differs.");
        foreach (var entry in languages)
        {
            using var stream = AssetManager.Instance.GetCustomAsset("legacy", entry);
            using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            var expected = File.ReadAllBytes(Path.Combine(output, Path.GetFileName(entry.Name)));
            if (!bytes.ToArray().AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Compiled replacement differs: " + entry.Name);
        }
        foreach (var name in CurrencyArt.AssetNames(manifest))
        {
            var entries = legacy
                .Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length != 1) throw new InvalidDataException("Compiled artwork asset missing/ambiguous: " + name);
            var entry = entries[0];
            using var stream = AssetManager.Instance.GetCustomAsset("legacy", entry);
            using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            if (!bytes.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(CurrencyArt.ExpectedPath(output, name))))
                throw new InvalidDataException("Compiled artwork asset differs: " + name);
        }
        // Remove only the two compiler-owned junctions, never their targets or game data.
        foreach (var folder in new[] { "Data", "Patch" })
        {
            var junction = new DirectoryInfo(Path.Combine(staging, folder));
            if (junction.Exists && junction.LinkTarget is { } target &&
                Path.GetFullPath(target).Equals(Path.Combine(game, folder), StringComparison.OrdinalIgnoreCase))
                junction.Delete();
        }
        foreach (var (file, key) in new[] { ("FIFA21.exe", "gameExecutableSha256"), ("CardsDLL_Win64_retail.dll", "cardsLibrarySha256") })
        {
            using var stream = File.OpenRead(Path.Combine(game, file));
            if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != manifest["profile"]![key]!.GetValue<string>())
                throw new InvalidDataException("Game profile changed during compilation: " + file);
        }
        File.WriteAllText(Path.Combine(output, "compiled-profile.json"), JsonSerializer.Serialize(new
        {
            schema = 1, revision = manifest["revision"]!.GetValue<string>(), gameDirectory = game,
            gameExecutableSha256 = manifest["profile"]!["gameExecutableSha256"]!.GetValue<string>(),
            cardsLibrarySha256 = manifest["profile"]!["cardsLibrarySha256"]!.GetValue<string>(),
            modDataRelativePath = "compiler/ModData", verifiedLanguages = languages.Length,
            verifiedArtworkAssets = CurrencyArt.AssetNames(manifest).Length,
        }));
        Console.WriteLine($"COMPILED-MOD verified all 21 languages and {CurrencyArt.AssetNames(manifest).Length} artwork assets against generated replacements; profile ready.");
    }
}
