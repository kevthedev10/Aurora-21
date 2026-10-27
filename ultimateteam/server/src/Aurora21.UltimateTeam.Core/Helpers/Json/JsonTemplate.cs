using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aurora21.UltimateTeam.Core.Helpers.Json;

/// <summary>
/// Values substituted into a <see cref="JsonTemplate"/>, written as a collection initializer.
/// An argument is either a built <see cref="JsonNode"/> or, via <see cref="Defer"/>, something the
/// response writer calls back into — so a large placeholder need never exist as a node tree.
/// </summary>
public sealed class TemplateArgs : IEnumerable<KeyValuePair<string, JsonNode?>>
{
    private readonly Dictionary<string, JsonNode?> _values = new(StringComparer.Ordinal);
    // Most arguments are plain nodes, so the deferred table is not allocated until one is added.
    private Dictionary<string, Action<Utf8JsonWriter>>? _writers;

    public JsonNode? this[string name]
    {
        get => _values[name];
        set
        {
            // One name, one argument: assigning a node replaces a deferred writer of the same name,
            // so Names and Contains never report a name twice or disagree about how it is supplied.
            _writers?.Remove(name);
            _values[name] = value;
        }
    }

    /// <summary>
    /// Supplies a placeholder as something to <em>write</em> rather than something built: the callback
    /// emits the value straight into the response's writer when the template is written, and no node
    /// is ever materialised for it. Only a template written to bytes can carry one —
    /// <see cref="JsonTemplate.Render"/> has no node to return and refuses.
    /// </summary>
    public void Defer(string name, Action<Utf8JsonWriter> write)
    {
        _values.Remove(name);
        (_writers ??= new(StringComparer.Ordinal))[name] = write;
    }

    /// <summary>
    /// Set when the caller built every node argument for this one render and does not use them
    /// afterwards: a render may then attach an argument that has no parent instead of copying it.
    /// Leave it unset for a node the caller keeps, mutates later or shares (a cache, a document it
    /// still edits); an argument that already has a parent, or appears twice, is copied regardless.
    /// </summary>
    public bool AdoptNodes { get; init; }

    public bool Contains(string name) => _values.ContainsKey(name) || (_writers?.ContainsKey(name) ?? false);

    // Names whose node has been handed out. A template can use one placeholder twice in the same
    // object, and both entries are rendered before that object assigns their parent, so an unset
    // Parent alone cannot tell a second use from the first.
    private HashSet<string>? _adopted;

    /// <summary>The rendered form of a node argument: the node itself on its first use when it may be adopted, otherwise a copy.</summary>
    internal JsonNode? Take(string name)
    {
        var value = _values[name];
        if (AdoptNodes && value is { Parent: null } && (_adopted ??= new(StringComparer.Ordinal)).Add(name))
        {
            return value;
        }
        return value?.DeepClone();
    }

    /// <summary>Whether any argument is a deferred writer, and so cannot be rendered to a node tree.</summary>
    public bool HasDeferred => _writers is { Count: > 0 };

    public IEnumerable<string> Names => _writers is null ? _values.Keys : _values.Keys.Concat(_writers.Keys);

    /// <summary>
    /// The first argument name that is not in <paramref name="placeholders"/>, or null when every
    /// name is. Iterates the dictionaries by their own struct enumerators, so validating a render
    /// allocates nothing; <see cref="Names"/> would box one enumerator per call.
    /// </summary>
    internal string? FirstNameOutside(HashSet<string> placeholders)
    {
        foreach (var name in _values.Keys)
        {
            if (!placeholders.Contains(name)) return name;
        }
        if (_writers is not null)
        {
            foreach (var name in _writers.Keys)
            {
                if (!placeholders.Contains(name)) return name;
            }
        }
        return null;
    }

    /// <summary>
    /// Writes the named argument's value — the deferred writer's output, or the node's — with the
    /// wire's number and escaping rules. The generated writers call this for every placeholder.
    /// </summary>
    public void WriteTo(Utf8JsonWriter writer, string name)
    {
        if (_writers is not null && _writers.TryGetValue(name, out var write))
        {
            write(writer);
            return;
        }
        FutJson.WriteValue(writer, _values[name]);
    }

    /// <summary>The node arguments only; a deferred writer has no node to enumerate.</summary>
    public IEnumerator<KeyValuePair<string, JsonNode?>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A JSON document in which any string value that is exactly <c>"{{name}}"</c> is replaced by
/// the argument of that name (any JSON value, including objects and null). Everything else is
/// copied as written, preserving key order. Rendering fails when an argument is missing or
/// unused, so a template and its caller cannot drift apart silently.
/// </summary>
public sealed partial class JsonTemplate
{
    private readonly Node _plan;
    private readonly HashSet<string> _placeholders = new(StringComparer.Ordinal);

    private JsonTemplate(string name, JsonNode root)
    {
        Name = name;
        _plan = Compile(root);
    }

    public string Name { get; }

    public IReadOnlySet<string> Placeholders => _placeholders;

    public static JsonTemplate Parse(string name, string json) =>
        new(name, JsonMaterializer.Materialize(JsonNode.Parse(json)) ?? throw new InvalidDataException($"template {name} is empty"));

    /// <summary>
    /// Fails when the arguments and placeholders do not match exactly, so a template and its caller
    /// cannot drift apart silently. Writing a template straight to bytes must run this too.
    /// </summary>
    public void Validate(TemplateArgs args)
    {
        // Hand-written loops, not LINQ: this runs on every render, and FirstOrDefault(lambda) over
        // IEnumerable allocated a closure and a boxed enumerator each time — 4.8% of a hub response's
        // allocation between them. Iterating the HashSet and the dictionaries directly allocates
        // nothing.
        foreach (var name in _placeholders)
        {
            if (!args.Contains(name))
            {
                throw new InvalidOperationException($"template {Name} needs a value for {{{{{name}}}}}");
            }
        }
        if (args.FirstNameOutside(_placeholders) is { } unused)
        {
            throw new InvalidOperationException($"template {Name} has no placeholder {{{{{unused}}}}}");
        }
    }

    public JsonNode Render(TemplateArgs? args = null)
    {
        args ??= new TemplateArgs();
        Validate(args);
        // A deferred writer produces bytes into a writer and has no node form, which is the whole
        // point of it. Rendering one would have to materialise it back into the tree the caller
        // deferred to avoid, so this path says so instead of quietly building it.
        if (args.HasDeferred)
        {
            throw new InvalidOperationException(
                $"template {Name} has a deferred writer argument and cannot be rendered to a node; write it with TemplateLibrary.Bytes");
        }
        return _plan.Render(args)!;
    }

    public JsonObject RenderObject(TemplateArgs? args = null) =>
        Render(args) as JsonObject ?? throw new InvalidOperationException($"template {Name} is not a JSON object");

    public JsonArray RenderArray(TemplateArgs? args = null) =>
        Render(args) as JsonArray ?? throw new InvalidOperationException($"template {Name} is not a JSON array");

    /// <summary>
    /// Resolves the template tree once, so rendering never re-inspects it. Which leaves are placeholders
    /// is fixed when the template is parsed; the previous renderer rediscovered it with a regular
    /// expression on every string leaf of every render.
    /// </summary>
    private Node Compile(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var keys = new string[obj.Count];
                var values = new Node[obj.Count];
                var literal = true;
                var index = 0;
                foreach (var (key, value) in obj)
                {
                    keys[index] = key;
                    values[index] = Compile(value);
                    literal &= values[index] is LiteralNode;
                    index++;
                }
                // A subtree with no placeholder renders to a copy of itself, so clone it whole.
                return literal ? new LiteralNode(obj) : new ObjectNode(keys, values);
            }
            case JsonArray array:
            {
                var items = new Node[array.Count];
                var literal = true;
                for (var index = 0; index < array.Count; index++)
                {
                    items[index] = Compile(array[index]);
                    literal &= items[index] is LiteralNode;
                }
                return literal ? new LiteralNode(array) : new ArrayNode(items);
            }
            case JsonValue value when PlaceholderName(value) is { } name:
                _placeholders.Add(name);
                return new PlaceholderNode(name);
            default:
                return new LiteralNode(node);
        }
    }

    private abstract class Node
    {
        public abstract JsonNode? Render(TemplateArgs args);
    }

    /// <summary>
    /// Template content, copied out on every render. The clone is required, not an optimisation to
    /// remove later: a <see cref="JsonNode"/> has a single parent and callers mutate what they receive,
    /// so handing out the cached template itself would corrupt every later render.
    /// </summary>
    private sealed class LiteralNode(JsonNode? value) : Node
    {
        public override JsonNode? Render(TemplateArgs args) => value?.DeepClone();
    }

    private sealed class PlaceholderNode(string name) : Node
    {
        public override JsonNode? Render(TemplateArgs args) => args.Take(name);
    }

    private sealed class ObjectNode(string[] keys, Node[] values) : Node
    {
        public override JsonNode? Render(TemplateArgs args)
        {
            // Exactly sized, in template key order: JsonObject sizes its dictionary from this collection.
            var entries = new KeyValuePair<string, JsonNode?>[keys.Length];
            for (var index = 0; index < keys.Length; index++)
            {
                entries[index] = new KeyValuePair<string, JsonNode?>(keys[index], values[index].Render(args));
            }
            return new JsonObject(entries);
        }
    }

    private sealed class ArrayNode(Node[] items) : Node
    {
        public override JsonNode? Render(TemplateArgs args)
        {
            var values = new JsonNode?[items.Length];
            for (var index = 0; index < items.Length; index++)
            {
                values[index] = items[index].Render(args);
            }
            return new JsonArray(values);
        }
    }

    private static string? PlaceholderName(JsonValue value)
    {
        if (!value.TryGetValue(out string? text))
        {
            return null;
        }
        var match = PlaceholderPattern().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^\{\{([A-Za-z][A-Za-z0-9_]*)\}\}$")]
    private static partial Regex PlaceholderPattern();
}

/// <summary>JSON response templates embedded in an assembly under <c>Resources/Templates</c>, parsed once.</summary>
public sealed class TemplateLibrary
{
    private readonly Assembly _assembly;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonTemplate> _cache = new(StringComparer.Ordinal);

    public TemplateLibrary(Assembly assembly) => _assembly = assembly;

    /// <summary>The Core assembly's templates.</summary>
    public static TemplateLibrary Core { get; } = new(typeof(TemplateLibrary).Assembly);

    /// <summary>Loads <c>Resources/Templates/{name}.json</c>, e.g. <c>store/pack-tile</c>.</summary>
    public JsonTemplate this[string name] => _cache.GetOrAdd(name, key => JsonTemplate.Parse(key, EmbeddedResources.ReadText(_assembly, $"Templates/{key}.json")));

    public JsonObject Object(string name, TemplateArgs? args = null) => this[name].RenderObject(args);

    public JsonArray Array(string name, TemplateArgs? args = null) => this[name].RenderArray(args);

    /// <summary>
    /// The rendered template as wire bytes. Uses the generated writer when this library owns the
    /// generated code, which skips building the node tree; the tree path renders the same bytes for
    /// any template without one. Only for a result that is sent as-is: bytes cannot be edited
    /// afterwards, so a caller that still has to change fields must use <see cref="Object"/>.
    /// </summary>
    public byte[] Bytes(string name, TemplateArgs? args = null)
    {
        var template = this[name];
        args ??= new TemplateArgs();
        template.Validate(args);
        if (!ReferenceEquals(_assembly, typeof(TemplateLibrary).Assembly))
        {
            return FutJson.SerializeToUtf8(template.Render(args));
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, FutJson.WriterOptions))
        {
            if (!WireWriters.TryWrite(name, writer, args))
            {
                // Only a generated writer can call a deferred argument back; the tree path would have
                // to materialise it. Every embedded template has a writer, so this means the template
                // is new and generate.mjs has not been rerun.
                if (args.HasDeferred)
                {
                    throw new InvalidOperationException(
                        $"template {name} has a deferred writer argument but no generated writer; rerun tools/wire-writers/generate.mjs");
                }
                FutJson.WriteValue(writer, template.Render(args));
            }
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Writes one rendered template into a writer that is already producing a larger body, such as
    /// one element of an array a deferred argument emits. The bytes are those <see cref="Bytes"/>
    /// would produce for the same template and arguments.
    /// </summary>
    public void Write(Utf8JsonWriter writer, string name, TemplateArgs args)
    {
        var template = this[name];
        template.Validate(args);
        if (ReferenceEquals(_assembly, typeof(TemplateLibrary).Assembly) && WireWriters.TryWrite(name, writer, args))
        {
            return;
        }
        if (args.HasDeferred)
        {
            throw new InvalidOperationException(
                $"template {name} has a deferred writer argument but no generated writer; rerun tools/wire-writers/generate.mjs");
        }
        FutJson.WriteValue(writer, template.Render(args));
    }
}

/// <summary>Reads files embedded with <c>LogicalName="%(RecursiveDir)%(Filename)%(Extension)"</c> below <c>Resources/</c>.</summary>
public static class EmbeddedResources
{
    /// <summary>The resource as text with "\n" line endings, whatever the checkout's line-ending conversion.</summary>
    public static string ReadText(Assembly assembly, string path)
    {
        var name = path.Replace('\\', '/');
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? assembly.GetManifestResourceStream(name.Replace('/', '\\'))
                           ?? throw new FileNotFoundException($"embedded resource {name} was not found in {assembly.GetName().Name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    public static JsonNode ReadJson(Assembly assembly, string path) =>
        JsonMaterializer.Materialize(JsonNode.Parse(ReadText(assembly, path)))
        ?? throw new InvalidDataException($"embedded resource {path} is empty");
}

/// <summary>
/// Converts parsed JSON into CLR-backed values (long, double, string, bool) so that numbers read
/// from files are written by <see cref="FutJson"/> like computed ones (0.10 becomes 0.1).
/// </summary>
public static class JsonMaterializer
{
    public static JsonNode? Materialize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(entry => KeyValuePair.Create(entry.Key, Materialize(entry.Value)))),
        JsonArray array => new JsonArray(array.Select(Materialize).ToArray()),
        JsonValue value when value.TryGetValue(out JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => JsonValue.Create(element.GetString()),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            JsonValueKind.Number when element.TryGetInt64(out var integer) && !element.GetRawText().Contains('.') && !element.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase)
                => JsonValue.Create(integer),
            JsonValueKind.Number => JsonValue.Create(element.GetDouble()),
            _ => null,
        },
        _ => node?.DeepClone(),
    };
}
