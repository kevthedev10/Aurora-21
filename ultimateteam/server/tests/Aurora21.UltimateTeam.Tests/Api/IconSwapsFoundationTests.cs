using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Store;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Icon Swaps foundations (ultimateteam/icon-swaps.md): the tokens are rewards only, a token pack opens a token the club
/// does not hold yet, and the SBC pieces a swap needs (the token rules, a multi-part set paid as a set reward) work. The
/// Icon Swaps objectives and swap SBCs themselves are not switched on, so these tests author their own challenges.
/// </summary>
public sealed class IconSwapsFoundationTests
{
    private const string G = "/ut/game/fifa21";
    private static readonly long[] TokenRareflags = [89, 92, 93, 94];
    // The FGS Swaps 1-2 tokens: reward-only too, though nothing hands them out yet.
    private static readonly long[] FgsTokenRareflags = [76, 77];
    private static readonly (long PackId, long Rareflag)[] TokenPacks = [(560, 89), (561, 92), (562, 93), (563, 94)];

    [Fact]
    public void TokensAreRewardOnlyAndQuickSellForNothing()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        foreach (var rareflag in TokenRareflags)
        {
            var tokens = rt.Game.Players.Entries.Where(entry => entry.Value.Rareflag == rareflag).Select(entry => entry.Key).ToList();
            Assert.Equal(18, tokens.Count);
            Assert.False(rt.Game.Cards.RarityAllowed(rareflag, null));
            Assert.True(rt.Game.Cards.RarityAllowed(rareflag, new[] { rareflag }));
            Assert.All(tokens, token => Assert.Equal(0, rt.Game.Cards.DiscardValue(token)));
        }
    }

    [Fact]
    public void FgsSwapsTokensAreRewardOnlyToo()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        foreach (var rareflag in FgsTokenRareflags)
        {
            Assert.Equal(10, rt.Game.Players.Entries.Count(entry => entry.Value.Rareflag == rareflag));
            Assert.False(rt.Game.Cards.RarityAllowed(rareflag, null));
            Assert.True(rt.Game.Cards.RarityAllowed(rareflag, new[] { rareflag }));
        }
    }

    /// <summary>Ordinary pack draws exclude reward-only tokens; explicit token packs can grant them.</summary>
    [Fact]
    public async Task StandardPacksNeverDrawATokenAndDedicatedTokenPacksCanGrantOne()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var bronzePack = new JsonObject
        {
            ["name"] = "Bronze player", ["size"] = 1, ["rare_slots"] = 1,
            ["gold_weight"] = 0, ["silver_weight"] = 0, ["bronze_weight"] = 100,
        };
        for (var pack = 0; pack < 150; pack++)
        {
            var opened = await rt.Game.RunAsync("bronze-pack", uow => rt.Fut.Packs.CreatePackAsync(uow, 1, bronzePack.DeepClone().AsObject()), CancellationToken.None);
            var card = rt.Game.Players.Find(opened["items"]!.AsArray().OfType<JsonObject>().Single().Int("resourceId"))!;
            Assert.DoesNotContain(card.Rareflag, TokenRareflags.Concat(FgsTokenRareflags));
        }
        var named = new JsonObject { ["swap_token_rareflag"] = 89 };
        var token = await rt.Game.RunAsync("named-token", uow => rt.Fut.Packs.CreatePackAsync(uow, 560, named), CancellationToken.None);
        Assert.Equal(89, rt.Game.Players.Find(token["items"]!.AsArray().OfType<JsonObject>().Single().Int("resourceId"))!.Rareflag);
    }

    /// <summary>Reward-only used to double as the red FUT Champions pool's rarity list; a token must never join it.</summary>
    [Fact]
    public void TheRedChampionsPoolNeverTakesAToken()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        Assert.Equal(new long[] { 18 }, rt.Game.Rules.Cards.FutChampionsRedRareflags);
        var players = rt.Game.Players;
        // A token whose footballer also has an ordinary card, put in the live Team of the Week.
        var (token, sibling) = players.Entries.Where(entry => TokenRareflags.Contains(entry.Value.Rareflag))
            .Select(entry => (Token: entry.Key, Sibling: players.Entries
                .FirstOrDefault(other => other.Value.Rareflag is 0 or 1 && other.Value.Int("assetId") == entry.Value.Int("assetId")).Key))
            .First(pair => pair.Sibling != 0);
        var week = rt.Game.Rules.Totw.Week(rt.Game.Rules.Totw.Current)!;
        week.Subs.Add(new TotwCard { ResourceId = sibling, Name = players.Find(sibling)!.Str("name"), Rating = players.Find(sibling)!.Rating });

        var red = rt.Fut.Pools.Named(ResourcePools.TotwOneToThreeRed)!;
        Assert.DoesNotContain(token, red);
        Assert.All(red, id => Assert.Equal(18, players.Find(id)!.Rareflag));
    }

    [Fact]
    public async Task TokenPacksOpenEachTokenOfTheirRoundOnceThenAny()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var onSale = rt.CallObject("GET", $"{G}/store/purchasegroup/all")["purchase"].Objects().Select(pack => pack["id"].L()).ToHashSet();
        foreach (var (packId, rareflag) in TokenPacks)
        {
            Assert.Equal(rareflag, rt.Game.Packs.GetPackConfig(packId).Int("swap_token_rareflag"));
            Assert.DoesNotContain(packId, onSale);
        }

        // Claimed into My Packs, a token pack opens one token.
        store.AddUnopenedPack(560, "objective");
        store.DeletePile("unassigned");
        var opened = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 560, ["useCredits"] = 0, ["usePreOrder"] = 1, ["currency"] = "PREORDER" });
        Assert.Single(opened["itemList"].A());

        // Each later one opens a token the club does not hold, in any pile, until it holds all 18.
        for (var pack = 1; pack < 18; pack++)
        {
            await rt.Game.RunAsync("token-pack", uow => rt.Fut.Packs.CreatePackAsync(uow, 560), CancellationToken.None);
        }
        List<JsonObject> Tokens() => store.ListItems().Where(item => rt.Game.Players.Find(item.Int("resourceId"))?.Rareflag == 89).ToList();
        Assert.Equal(18, Tokens().Count);
        Assert.Equal(18, Tokens().Select(item => item.Int("resourceId")).Distinct().Count());
        Assert.All(Tokens(), item => Assert.True(item.Bool("untradeable")));

        // Holding all 18, it still opens one of them.
        await rt.Game.RunAsync("token-pack", uow => rt.Fut.Packs.CreatePackAsync(uow, 560), CancellationToken.None);
        Assert.Equal(19, Tokens().Count);

        // The operator panel's contents preview rolls player packs only; a token pack is not one.
        Assert.Throws<NotSupportedException>(() => rt.Fut.Packs.PreviewPlayerPack(rt.Game.Packs.GetPackConfig(560)));
    }

    [Fact]
    public async Task ATokenRuleCountsOnlyItsOwnRound()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        // An open, listed challenge: a brick wall would refuse players in its locked slots.
        var challenge = rt.Game.Sbc.Sets.Single(set => set.Key == "FND_HYBRID_NATIONS").Challenges[0];
        challenge.Source["requirements"] = Requirements(3, 1);
        challenge.Source["rewards"] = new JsonObject { ["entries"] = new JsonArray() };
        Assert.Contains(rt.Fut.Sbc.EligibilityRequirements(challenge).OfType<JsonObject>(),
            slot => slot["eligibilityKey"].L() == (long)SbcEligibilityKey.Rarity && slot["eligibilityValue"].L() == 89);
        Assert.Equal("Icon Swaps 1 tokens: Min 3", rt.Fut.Sbc.Describe(challenge));

        var (roundOne, roundTwo) = (TokensOf(rt, 89, 3), TokensOf(rt, 92, 1, TokensOf(rt, 89, 3)));
        var items = roundOne.Take(2).Concat(roundTwo).Select(id => rt.Fut.Items.MakePlayer(id, "club")).ToList();
        await rt.Fut.Sbc.SaveSquadAsync(challenge.Id, Squad(items));
        var refused = await rt.Fut.Sbc.SubmitAsync(checked((int)challenge.Id));
        Assert.Equal(200, refused.Status);
        Assert.Equal("INVALID", refused.Body!["status"]!.S());

        items[2] = rt.Fut.Items.MakePlayer(roundOne[2], "club");
        await rt.Fut.Sbc.SaveSquadAsync(challenge.Id, Squad(items));
        Assert.Equal(200, (await rt.Fut.Sbc.SubmitAsync(checked((int)challenge.Id))).Status);
        Assert.All(items, item => Assert.Null(rt.Game.Store().ListItems().FirstOrDefault(owned => owned.Int("id") == item.Int("id"))));
    }

    /// <summary>
    /// A swap dearer than an 11-player squad is a two-part set whose Icon is the set reward, so no single part can claim
    /// it; the set tile previews that reward.
    /// </summary>
    [Fact]
    public async Task ATwoPartSwapPaysItsIconOnlyOnceBothPartsAreIn()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        const long henry = 237069; // Thierry Henry, Mid Icon 90: 17 Icon Swaps 1 tokens in FIFA 21
        var meta = rt.Game.Players.Find(henry)!;
        // No bundled two-challenge set is on the live SBC list in phase 1; this runtime shows them all.
        rt.Game.Sbc.LiveKeys.UnionWith(rt.Game.Sbc.Sets.Select(set => set.Key));
        var set = rt.Game.Sbc.Sets.First(candidate => candidate.Challenges.Count == 2);
        set.Source["rewards"] = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["type"] = "PLAYER",
                ["attributes"] = new JsonObject { ["resourceId"] = henry, ["name"] = meta.Str("name"), ["rating"] = meta.Rating, ["position"] = meta.Position },
            }),
        };
        foreach (var challenge in set.Challenges)
        {
            challenge.Source["requirements"] = Requirements(2, 1);
            challenge.Source["rewards"] = new JsonObject { ["entries"] = new JsonArray() };
        }
        var tile = (await rt.Fut.Sbc.SetsResponseAsync())["categories"]!.AsArray().OfType<JsonObject>()
            .SelectMany(category => category["sets"]!.AsArray().OfType<JsonObject>()).Single(candidate => candidate["setId"].L() == set.Id);
        Assert.Equal(henry, tile["awards"]!.AsArray().OfType<JsonObject>().Single()["value"].L());

        var tokens = TokensOf(rt, 89, 4);
        long HenryCopies() => rt.Game.Store().ListItems().Count(item => item.Int("resourceId") == henry);
        async Task Submit(int challengeIndex, IEnumerable<long> cards)
        {
            var challenge = set.Challenges[challengeIndex];
            await rt.Fut.Sbc.SaveSquadAsync(challenge.Id, Squad(cards.Select(id => rt.Fut.Items.MakePlayer(id, "club")).ToList()));
            Assert.Equal(200, (await rt.Fut.Sbc.SubmitAsync(checked((int)challenge.Id))).Status);
        }
        await Submit(0, tokens.Take(2));
        Assert.Equal(0, HenryCopies());
        await Submit(1, tokens.Skip(2));
        Assert.Equal(1, HenryCopies());
        Assert.True(rt.Game.Store().ListItems().Single(item => item.Int("resourceId") == henry).Bool("untradeable"));
    }

    private static JsonObject Requirements(int players, int round) => new()
    {
        ["rules"] = new JsonArray(
            new JsonObject { ["type"] = "PLAYERS", ["attributes"] = new JsonObject { ["min"] = players, ["max"] = players } },
            new JsonObject { ["type"] = $"ICON_SWAPS_{round}", ["attributes"] = new JsonObject { ["min"] = players } }),
    };

    /// <summary>Tokens of one round, one per footballer and none sharing a footballer with <paramref name="avoid"/>.</summary>
    private static List<long> TokensOf(TestRuntime rt, long rareflag, int count, IEnumerable<long>? avoid = null)
    {
        var footballers = (avoid ?? Array.Empty<long>()).Select(id => rt.Game.Players.Find(id)!.Int("assetId")).ToHashSet();
        return rt.Game.Players.Entries.Where(entry => entry.Value.Rareflag == rareflag).OrderBy(entry => entry.Key)
            .Where(entry => footballers.Add(entry.Value.Int("assetId"))).Take(count).Select(entry => entry.Key).ToList();
    }

    private static JsonObject Squad(IEnumerable<JsonObject> items) => new()
    {
        ["players"] = new JsonArray(items.Select(item => (JsonNode)new JsonObject { ["itemData"] = new JsonObject { ["id"] = item.Int("id") } }).ToArray()),
    };
}
