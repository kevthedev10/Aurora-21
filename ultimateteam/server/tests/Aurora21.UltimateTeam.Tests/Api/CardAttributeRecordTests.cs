using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>Attribute records (GET /attributes/metadata) after migration 047 and the ported record rules.</summary>
public sealed class CardAttributeRecordTests
{
    private const string G = "/ut/game/fifa21";

    private static JsonObject Record(TestRuntime rt, long resourceId) =>
        rt.Call("GET", $"{G}/attributes/metadata?defIds={resourceId}").AsArray().Single()!.AsObject();

    private static long[] InGame(JsonObject record) => record["ingameattribs"]!.AsArray().Select(node => node!.GetValue<long>()).ToArray();

    /// <summary>The version a record's own content gives it (Python server 4736f1a).</summary>
    internal static long ContentVersion(JsonObject record) => Cards.RecordVersion(InGame(record),
        record["attributeArray"]!.AsArray().Select(node => node!.GetValue<long>()).ToArray(), record.Int("rating"), record.Int("trait1"), record.Int("trait2"));

    [Fact]
    public void CreateMatchListsTheCardsWhoseRecordTheClientHoldsStale()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        // Three owned cards whose records are complete (a record with version 0 is one FIFA estimates, and 0 is current for it).
        var squad = rt.Game.Store().ListItems().Where(item => item.Text("itemType") == "player").Select(item => item.Int("resourceId"))
            .Distinct().Where(id => Record(rt, id).Int("version") > 3).Take(3).ToList();
        Assert.Equal(3, squad.Count);
        var current = Record(rt, squad[0]).Int("version");

        var response = rt.CallObject("POST", $"{G}/match", new JsonObject
        {
            ["squadId"] = 1,
            ["playersAttributesValidation"] = new JsonArray(
                new JsonObject { ["defId"] = squad[0], ["version"] = current },
                new JsonObject { ["defId"] = squad[1], ["version"] = 3 },
                new JsonObject { ["defId"] = squad[2], ["version"] = 0 }),
        });

        // Only the up-to-date record is left out; CardsDLL downloads the others before the match (Python server 4736f1a).
        Assert.Equal(new[] { squad[1], squad[2] }, response["mismatches"]!.AsArray().Select(node => node!.GetValue<long>()));
    }

    [Fact]
    public void ARecordVersionIsTheSameContentHashAsThePythonServers()
    {
        // SHA-1 of the compact JSON [[80,81,82],[70,71,72,73,74,75],84,17007562,1], computed independently (Node crypto).
        Assert.Equal(920444, Cards.RecordVersion(new long[] { 80, 81, 82 }, new long[] { 70, 71, 72, 73, 74, 75 }, 84, 17007562, 1));
        Assert.NotEqual(920444, Cards.RecordVersion(new long[] { 80, 81, 82 }, new long[] { 70, 71, 72, 73, 74, 75 }, 85, 17007562, 1));
    }

    [Fact]
    public void AGoalkeeperRecordIsCompleteInFifasSlotOrderAndVersioned()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var order = rt.Game.Rules.Cards.InGameAttributeOrder;
        var (resource, meta) = rt.Game.Players.Entries
            .First(entry => entry.Value.Position.Equals("GK", StringComparison.OrdinalIgnoreCase) && entry.Value.Rating >= 85);

        var record = Record(rt, resource);
        var values = InGame(record);

        // Before migration 047 every goalkeeping value was 0 in the catalogue, so FIFA estimated every card.
        Assert.Equal(ContentVersion(record), record.Int("version"));
        Assert.Equal(34, values.Length);
        Assert.All(values, value => Assert.InRange(value, 1, 99));
        var attributes = (JsonObject)meta.Get("attributes")!;
        Assert.Equal(attributes.Int("gkDiving"), values[order.IndexOf("gkdiving")]);
        Assert.Equal(attributes.Int("sprintSpeed"), values[1]);
        Assert.Equal("sprintspeed", order[1]);
    }

    [Fact]
    public void ANativePlayerItemCarriesFifasNativeCardFields()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        const long messi = 158023;
        var meta = rt.Game.Players.Find(messi)!;
        var rates = rt.Game.Rules.Cards.NativeWorkRates;

        // The stored document stays as it is; the fields are added when the native item is rendered for the client.
        var item = rt.Fut.Native.PlayerItem(rt.Fut.Items.BuildPlayer(messi, 123, "club"));

        // FIFA's player table codes: skill moves 0-4, weak foot 1-5, work rates medium 0 / low 1 / high 2.
        Assert.Equal(meta.Int("skillMoves") - 1, item.Int("skillmoves"));
        Assert.Equal(meta.Int("weakFoot"), item.Int("weakfootabilitytypecode"));
        Assert.Equal(rates[meta.Str("workRateAttack").ToLowerInvariant()], item.Int("attackingworkrate"));
        Assert.Equal(rates[meta.Str("workRateDefense").ToLowerInvariant()], item.Int("defensiveworkrate"));
        Assert.Equal(58865920, item.Int("trait1"));
        Assert.Equal(0, item.Int("trait2"));
    }

    [Fact]
    public void EverySquadSlotCarriesItsCardsAttributeRecord()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        foreach (var squad in new[] { rt.CallObject("GET", $"{G}/squad/active"), rt.CallObject("GET", $"{G}/sqbt/user/opponentsquad/0") })
        {
            var slots = squad["players"]!.AsArray().OfType<JsonObject>().Where(slot => slot["itemData"] is JsonObject).ToList();
            Assert.NotEmpty(slots);
            foreach (var slot in slots)
            {
                // The same record /attributes/metadata serves for the card, so both match clients hold identical values.
                var resource = slot["itemData"]!.AsObject().Int("resourceId");
                Assert.Equal(Record(rt, resource).ToJsonString(), slot["playerAttributes"]!.ToJsonString());
            }
        }
    }

    [Fact]
    public void ACorrectedCardServesTheCorrectedAttributes()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var corrections = JsonNode.Parse(File.ReadAllText(Path.Combine(TestRuntime.DataRoot, "card-corrections.json")))!["cards"]!.AsObject();
        var (id, fix) = corrections
            .Select(entry => (Id: long.Parse(entry.Key), Fix: entry.Value!.AsObject()))
            .First(entry => entry.Fix["attributes"] is JsonObject && rt.Game.Players.Contains(entry.Id));
        var order = rt.Game.Rules.Cards.InGameAttributeOrder;

        var values = InGame(Record(rt, id));

        foreach (var (name, value) in fix["attributes"]!.AsObject())
        {
            Assert.Equal(value!.GetValue<long>(), values[order.IndexOf(name)]);
        }
    }
}
