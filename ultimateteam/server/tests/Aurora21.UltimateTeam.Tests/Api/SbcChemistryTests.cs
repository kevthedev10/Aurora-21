using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// SBC chemistry is computed by the server (ChemistryModel) from the slots, formation and manager FIFA saves, plus the
/// challenge's locked-slot cards. The chemistry number FIFA sends with the save (CardsDLL builder 0x2fbe40) is never
/// trusted: a client that lies about it, or skips the in-game submit check, is refused.
/// </summary>
public sealed class SbcChemistryTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>The Second Step: three free slots (LM, RS, LS of f442) and a chemistry rule.</summary>
    private static SbcChallenge SecondStep(TestRuntime rt) => rt.Game.Sbc.Challenges.Single(c => c.Key == "CH_2");

    private static List<JsonObject> OwnedPlayers(TestRuntime rt)
    {
        var store = rt.Game.Store();
        var seen = new HashSet<long>();
        return store.ListItems("club").Concat(store.ListItems("squad"))
            .Where(item => ClubInventoryService.IsPlayer(item) && !item.Bool("dream") && item.Int("loans") == 0)
            .Where(item => rt.Game.Players.Find(item.Int("resourceId")) is { } meta && seen.Add(meta.Int("assetId")))
            .ToList();
    }

    /// <summary>FIFA's save body; the client's own chemistry and rating claim to be perfect.</summary>
    private static JsonObject LyingSave(IReadOnlyDictionary<int, long> slots) => new()
    {
        ["chemistry"] = 100,
        ["rating"] = 99,
        ["formation"] = "f442",
        ["manager"] = new JsonArray(new JsonObject { ["id"] = 0, ["dream"] = false }),
        ["players"] = new JsonArray(Enumerable.Range(0, 23).Select(index => (JsonNode)new JsonObject
        {
            ["index"] = index,
            ["itemData"] = new JsonObject { ["id"] = slots.GetValueOrDefault(index), ["dream"] = false },
        }).ToArray()),
    };

    private static void SetChemistryTarget(SbcChallenge challenge, long minimum)
    {
        var rule = challenge.Source.Obj("requirements")!.Arr("rules")!.OfType<JsonObject>().Single(r => r.Text("type") == "CHEMISTRY");
        rule["attributes"]!["min"] = minimum;
    }

    [Fact]
    public async Task TheServerComputesChemistryAndIgnoresTheClientsClaim()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var challenge = SecondStep(rt);
        Assert.DoesNotContain("CHEMISTRY", rt.Game.Rules.Sbc.IgnoredRequirements);
        var free = SbcFixtures.FreeSlots(rt, challenge);
        Assert.Equal(new[] { 8, 9, 10 }, free);
        var players = OwnedPlayers(rt).Take(3).ToList();
        var slots = free.Zip(players).ToDictionary(pair => pair.First, pair => pair.Second.Int("id"));

        // What FIFA's chemistry gives this squad in the challenge's formation, with no manager.
        var starters = new JsonObject?[11];
        foreach (var (index, item) in free.Zip(players)) starters[index] = rt.Fut.Native.PlayerItem(item);
        var expected = rt.Game.Chemistry.SquadChemistry(rt.Fut.Sbc.Formation(challenge), starters, null)!.Value;
        Assert.True(expected < 100, "the squad must fall short of the 100 the client claims");

        // One point short is refused, whatever the client claims.
        SetChemistryTarget(challenge, expected + 1);
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", LyingSave(slots)).Status);
        var refused = await rt.Fut.Sbc.SubmitAsync(challenge.Id);
        Assert.Equal("INVALID", refused.Body!["status"]!.S());
        Assert.Contains("Chemistry", refused.Body!["message"]!.S());
        Assert.Empty(rt.Game.Store().SbcCompleted());

        // Exactly enough is accepted.
        SetChemistryTarget(challenge, expected);
        var accepted = await rt.Fut.Sbc.SubmitAsync(challenge.Id);
        Assert.NotEqual("INVALID", accepted.Body!["status"]?.S());
        Assert.Equal(new[] { "CH_2" }, rt.Game.Store().SbcCompleted());
    }

    /// <summary>
    /// A live Around the World squad (2026-10-05), slot by slot as saved: the client showed Rating 81 and
    /// Chemistry 100 with every requirement green, and the server refused it as Min Squad Rating: 81 (80.8 floored).
    /// </summary>
    [Fact]
    public async Task AroundTheWorldAcceptsTheSquadTheClientShowsAs81()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var challenge = rt.Game.Sbc.Challenges.Single(c => c.Key == "CH_14");
        Assert.Equal(900304, challenge.Id);
        long[] resources = [163264, 210972, 197061, 179944, 17017106, 17022495, 205498, 216393, 227796, 16962638, 207807];
        var slots = resources.Select((resource, index) => (index, id: rt.Fut.Items.MakePlayer(resource, "club").Int("id")))
            .ToDictionary(pair => pair.index, pair => pair.id);
        var save = LyingSave(slots);
        save["formation"] = "f5221";
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", save).Status);

        var result = await rt.Fut.Sbc.SubmitAsync(challenge.Id);
        Assert.NotEqual("INVALID", result.Body!["status"]?.S());
        Assert.Equal(new[] { "CH_14" }, rt.Game.Store().SbcCompleted());
    }

    /// <summary>
    /// A live Around the World squad (2026-10-05, club 90): Minamino was bought on the market (two owners) but carries a
    /// loyalty bonus, so FIFA shows him loyal and the squad at 100. The server dropped the bonus and refused it as
    /// Min Squad Chemistry: 100 (99).
    /// </summary>
    [Fact]
    public async Task AroundTheWorldCountsTheLoyaltyBonusOfABoughtCard()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var challenge = rt.Game.Sbc.Challenges.Single(c => c.Key == "CH_14");
        long[] resources = [206075, 16993482, 221660, 220793, 17017034, 212188, 214947, 219809, 226627, 188567, 226766];
        var items = resources.Select(resource => rt.Fut.Items.MakePlayer(resource, "club")).ToList();
        var bought = items[8];
        bought["owners"] = 2;
        bought["loyaltyBonus"] = 1;
        rt.Game.Store().SaveItem(bought);
        var save = LyingSave(items.Select((item, index) => (index, id: item.Int("id"))).ToDictionary(pair => pair.index, pair => pair.id));
        save["formation"] = "f5221";
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", save).Status);

        var result = await rt.Fut.Sbc.SubmitAsync(challenge.Id);
        Assert.NotEqual("INVALID", result.Body!["status"]?.S());
        Assert.Equal(new[] { "CH_14" }, rt.Game.Store().SbcCompleted());
    }

    [Fact]
    public void ABrickWallRatesOnlyThePlayersItTakes()
    {
        // Retail's Multi League & Nation asks a Min 55 rating of six players; FUTBIN's solutions field these six.
        Assert.Equal(74, SbcSquadRating.CalculateSbcSquadRating(new[] { 77, 76, 75, 75, 58, 57 }));
        // Eleven players rate exactly as before.
        Assert.Equal(75, SbcSquadRating.CalculateSbcSquadRating(Enumerable.Repeat(75, 11)));
    }
}
