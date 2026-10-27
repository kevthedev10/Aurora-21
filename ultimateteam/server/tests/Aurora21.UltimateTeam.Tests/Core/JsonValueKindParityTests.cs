using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// JsonValues.Kind no longer calls JsonValue.GetValueKind, which used to serialize the value to a
/// pooled buffer just to report its type. Every lenient reader funnels through Kind, so the
/// replacement has to answer identically for every shape a value can take.
/// </summary>
public sealed class JsonValueKindParityTests
{
    private static IEnumerable<(string Label, JsonValue Value)> Values()
    {
        // Created from CLR values.
        yield return ("long", JsonValue.Create(42L));
        yield return ("long.min", JsonValue.Create(long.MinValue));
        yield return ("long.max", JsonValue.Create(long.MaxValue));
        yield return ("int", JsonValue.Create(7));
        yield return ("double", JsonValue.Create(1.5));
        yield return ("double.whole", JsonValue.Create(2.0));
        yield return ("float", JsonValue.Create(1.25f));
        yield return ("decimal", JsonValue.Create(3.75m));
        yield return ("string", JsonValue.Create("text"));
        yield return ("string.empty", JsonValue.Create(string.Empty));
        yield return ("string.numeric", JsonValue.Create("85"));
        yield return ("bool.true", JsonValue.Create(true));
        yield return ("bool.false", JsonValue.Create(false));

        // Parsed from documents, so backed by a JsonElement.
        foreach (var text in new[] { "42", "-1", "1.5", "1.0", "1e5", "\"text\"", "\"85\"", "true", "false" })
        {
            yield return ("parsed:" + text, (JsonValue)JsonNode.Parse(text)!);
        }

        // Parsed as part of a larger document, the common server case.
        var document = JsonNode.Parse("""{"a":1,"b":"two","c":true,"d":3.5,"e":null}""")!.AsObject();
        foreach (var key in new[] { "a", "b", "c", "d" })
        {
            yield return ("member:" + key, (JsonValue)document[key]!);
        }
    }

    [Fact]
    public void KindMatchesTheValuesOwnReportedKind()
    {
        var checked_ = 0;
        foreach (var (label, value) in Values())
        {
            Assert.Equal(value.GetValueKind(), JsonValues.Kind(value));
            checked_++;
        }
        Assert.Equal(Values().Count(), checked_);
    }

    [Fact]
    public void ContainerAndNullKindsAreUnchanged()
    {
        Assert.Equal(JsonValueKind.Null, JsonValues.Kind(null));
        Assert.Equal(JsonValueKind.Object, JsonValues.Kind(new JsonObject()));
        Assert.Equal(JsonValueKind.Array, JsonValues.Kind(new JsonArray()));
        Assert.Equal(JsonValueKind.Null, JsonValues.Kind(JsonNode.Parse("null")));
    }

    [Fact]
    public void TheLenientReadersStillAgreeWithSerializedText()
    {
        foreach (var (label, value) in Values())
        {
            // Text must stay the value's JSON text; it is what stored documents were written with.
            var expected = value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                _ => FutJson.Serialize(value),
            };
            Assert.Equal(expected, JsonValues.Text(value));

            // TryInt and HasValue read through Kind, so they must be unaffected too.
            Assert.NotEqual(JsonValueKind.Undefined, JsonValues.Kind(value));
            _ = JsonValues.TryInt(value, out _);
            _ = JsonValues.HasValue(value);
        }
    }
}

/// <summary>
/// The typed StatRows overload skips building {index, value} objects only to parse them back, so it
/// has to produce exactly what the JsonNode overload produces for the same recorded values.
/// </summary>
public sealed class StatRowsParityTests
{
    [Fact]
    public void TypedRowsMatchTheDocumentPath()
    {
        using var runtime = TestRuntime.Create(marketTarget: 0, relationalSlice: false);
        var cards = runtime.Game.Cards;
        var cases = new[]
        {
            Array.Empty<(int, long)>(),
            new[] { (0, 5L) },
            new[] { (0, 1L), (1, 2L), (2, 3L) },
            new[] { (1, -4L) },                       // negatives clamp to zero
            new[] { (999, 7L) },                      // out of range is dropped
            new[] { (-1, 7L) },
            new[] { (0, 1L), (0, 9L) },               // later value wins
            new[] { (2, long.MaxValue) },
        };
        foreach (var recorded in cases)
        {
            var document = new JsonArray(recorded
                .Select(r => (JsonNode?)new JsonObject { ["index"] = r.Item1, ["value"] = r.Item2 }).ToArray());
            var fromDocument = cards.StatRows(document);
            var fromTyped = cards.StatRowsFrom(recorded);
            Assert.Equal(FutJson.Serialize(fromDocument), FutJson.Serialize(fromTyped));
        }
    }
}
