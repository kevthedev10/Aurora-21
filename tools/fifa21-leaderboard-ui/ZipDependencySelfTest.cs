using System.IO.Compression;
using System.Text;
using Ionic.Zip;
using ZipFile = Ionic.Zip.ZipFile;

// Exercises the shipped ZIP assembly without reading game data or launching FIFA.
internal static class ZipDependencySelfTest
{
    public static void Run()
    {
        var assembly = typeof(ZipFile).Assembly.GetName();
        if (assembly.Name != "ProDotNetZip" || assembly.Version is null || assembly.Version < new Version(1, 20, 0))
            throw new InvalidDataException($"Unexpected ZIP dependency: {assembly}");
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "DotNetZip.dll")))
            throw new InvalidDataException("The package still contains the vulnerable DotNetZip.dll.");

        var root = Path.Combine(Path.GetTempPath(), "Aurora21 ZIP test " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Synthetic FIFA21 cache payload.\n", 1000)));
            var cache = Path.Combine(root, "synthetic.cache");
            using (var zip = new ZipFile())
            using (var input = new MemoryStream(payload))
            {
                zip.AddEntry("cacheEntry", input);
                zip.Save(cache);
            }
            // Same read/extract-to-memory operations used by FMT CacheManager.
            using (var zip = ZipFile.Read(cache))
            using (var output = new MemoryStream())
            {
                zip.Entries.Single().Extract(output);
                if (!output.ToArray().SequenceEqual(payload)) throw new InvalidDataException("Cache round trip failed.");
            }
            using (var zip = System.IO.Compression.ZipFile.OpenRead(cache))
            using (var entry = zip.GetEntry("cacheEntry")!.Open())
            using (var output = new MemoryStream())
            {
                entry.CopyTo(output);
                if (!output.ToArray().SequenceEqual(payload)) throw new InvalidDataException("ZIP interoperability failed.");
            }

            // Absolute extended Windows paths are the CVE's relevant extraction case.
            var outside = Path.Combine(root, "escaped.txt");
            var malicious = Path.Combine(root, "absolute-path.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(malicious, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry(@"\\?\" + outside).Open()))
                writer.Write("synthetic traversal fixture");
            var extract = Path.Combine(root, "extract");
            Directory.CreateDirectory(extract);
            var rejected = false;
            try
            {
                using var zip = ZipFile.Read(malicious);
                zip.Entries.Single().Extract(extract);
            }
            catch (IOException) { rejected = true; }
            if (!rejected || File.Exists(outside)) throw new InvalidDataException("Archive traversal was not rejected.");
            Console.WriteLine($"PASS: {assembly}; cache round trip; ZIP interoperability; absolute traversal rejected; no legacy DLL.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
