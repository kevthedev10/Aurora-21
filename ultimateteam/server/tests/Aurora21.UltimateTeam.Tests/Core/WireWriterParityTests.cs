using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// The generated writers are a derived form of the embedded templates, so the only thing that makes
/// them safe is that they emit the same bytes. A template edited without rerunning
/// tools/wire-writers/generate.mjs fails here, which is the point.
/// </summary>
public sealed class WireWriterParityTests
{
    private static IEnumerable<string> EmbeddedTemplates() => typeof(TemplateLibrary).Assembly
        .GetManifestResourceNames()
        .Select(name => name.Replace('\\', '/'))
        .Where(name => name.StartsWith("Templates/", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
        .Select(name => name["Templates/".Length..^".json".Length])
        .OrderBy(name => name, StringComparer.Ordinal);

    // Deterministic values covering every JSON shape a placeholder can carry, including null.
    private static JsonNode? Sample(string placeholder, int offset) => (offset % 7) switch
    {
        0 => JsonValue.Create(1234567890123L),
        1 => JsonValue.Create("text-" + placeholder),
        2 => JsonValue.Create(true),
        3 => null,
        4 => JsonValue.Create(1.5),
        5 => new JsonObject { ["inner"] = 1, ["label"] = placeholder },
        _ => new JsonArray(JsonValue.Create(7), JsonValue.Create("item"), null),
    };

    /// <summary>
    /// The same arguments supplied either way: as built nodes, or as deferred writers that emit
    /// those nodes. Both must reach the wire as the same bytes.
    /// </summary>
    private static TemplateArgs ArgumentsFor(JsonTemplate template, int offset, bool deferred = false)
    {
        var args = new TemplateArgs();
        var index = offset;
        foreach (var placeholder in template.Placeholders.OrderBy(name => name, StringComparer.Ordinal))
        {
            var value = Sample(placeholder, index++);
            if (deferred)
            {
                args.Defer(placeholder, writer => FutJson.WriteValue(writer, value));
            }
            else
            {
                args[placeholder] = value;
            }
        }
        return args;
    }

    private static byte[] Generated(string name, TemplateArgs args)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, FutJson.WriterOptions))
        {
            Assert.True(WireWriters.TryWrite(name, writer, args), $"no generated writer for {name}");
        }
        return stream.ToArray();
    }

    [Fact]
    public void EveryEmbeddedTemplateHasAGeneratedWriter()
    {
        Assert.Equal(EmbeddedTemplates().ToArray(), WireWriters.Templates.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.NotEmpty(WireWriters.Templates);
    }

    [Fact]
    public void GeneratedWritersEmitTheSameBytesAsRenderingTheTemplate()
    {
        var compared = 0;
        foreach (var name in EmbeddedTemplates())
        {
            var template = TemplateLibrary.Core[name];
            // Several offsets so each placeholder is exercised as more than one JSON type.
            for (var offset = 0; offset < 7; offset++)
            {
                var args = ArgumentsFor(template, offset);
                var rendered = FutJson.SerializeToUtf8(template.Render(args));
                var generated = Generated(name, ArgumentsFor(template, offset));
                Assert.True(rendered.AsSpan().SequenceEqual(generated),
                    $"{name} (offset {offset}) differs:\n  rendered  {System.Text.Encoding.UTF8.GetString(rendered)}\n  generated {System.Text.Encoding.UTF8.GetString(generated)}");
                compared++;
            }
        }
        Assert.Equal(EmbeddedTemplates().Count() * 7, compared);
    }

    /// <summary>
    /// Deferring an argument changes when its bytes are produced, not what they are. This is what
    /// makes it safe to stream a placeholder that would otherwise be built as a node tree.
    /// </summary>
    [Fact]
    public void DeferredArgumentsEmitTheSameBytesAsTheNodesTheyReplace()
    {
        var compared = 0;
        foreach (var name in EmbeddedTemplates())
        {
            var template = TemplateLibrary.Core[name];
            for (var offset = 0; offset < 7; offset++)
            {
                var built = Generated(name, ArgumentsFor(template, offset));
                var deferred = Generated(name, ArgumentsFor(template, offset, deferred: true));
                Assert.True(built.AsSpan().SequenceEqual(deferred),
                    $"{name} (offset {offset}) differs:\n  built    {System.Text.Encoding.UTF8.GetString(built)}\n  deferred {System.Text.Encoding.UTF8.GetString(deferred)}");
                compared++;
            }
        }
        Assert.Equal(EmbeddedTemplates().Count() * 7, compared);
    }

    /// <summary>The same through the library's public byte path, which is how responses are written.</summary>
    [Fact]
    public void TemplateLibraryBytesAcceptsDeferredArguments()
    {
        var name = EmbeddedTemplates().First(candidate => TemplateLibrary.Core[candidate].Placeholders.Count > 0);
        var template = TemplateLibrary.Core[name];
        Assert.Equal(
            TemplateLibrary.Core.Bytes(name, ArgumentsFor(template, 0)),
            TemplateLibrary.Core.Bytes(name, ArgumentsFor(template, 0, deferred: true)));
    }

    [Fact]
    public void AnUnknownTemplateIsReportedRatherThanWrittenEmpty()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, FutJson.WriterOptions);
        Assert.False(WireWriters.TryWrite("no/such-template", writer, new TemplateArgs()));
    }
}
