using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>
/// Per-Windows-user preference files (config.json, pack_settings.json). A missing file is
/// created once from the caller's defaults; an existing file is never overwritten, and a
/// malformed one is reported instead of being silently replaced.
/// </summary>
public sealed class UserConfigStore
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    private readonly RuntimePaths _paths;

    public UserConfigStore(RuntimePaths paths) => _paths = paths;

    public string DataRoot => _paths.DataRoot;

    public string PathFor(string name) => Path.Combine(_paths.UserConfigRoot, name);

    /// <summary>The bundled example for a preference file: <c>config.json</c> has <c>config.example.json</c>.</summary>
    public static string ExampleName(string name) => Path.GetFileNameWithoutExtension(name) + ".example.json";

    /// <summary>The bundled example document, or null when the data root has none.</summary>
    public JsonObject? ReadExample(string name)
    {
        var example = Path.Combine(_paths.DataRoot, ExampleName(name));
        return File.Exists(example) ? ReadObject(example) : null;
    }

    /// <summary>The user's file over <paramref name="defaults"/>; the file is created from the defaults when missing.</summary>

    public JsonObject Read(string name, JsonObject defaults)
    {
        var path = PathFor(name);
        Directory.CreateDirectory(_paths.UserConfigRoot);
        if (!File.Exists(path))
        {
            CreateInitial(path, defaults);
        }
        var result = defaults.Copy();
        result.Update(ReadObject(path));
        return result;
    }

    private static JsonObject ReadObject(string path)
    {
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
            return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Expected a JSON object");
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Cannot read {path}: {error.Message}. The file was not overwritten.", error);
        }
    }

    private static void CreateInitial(string path, JsonObject defaults)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(defaults.ToJsonString(IndentedOptions));
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process created it first; keep theirs.
        }
    }
}
