using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Club;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Builds an SBC submission that the server's own validation accepts.
///
/// Several fixtures used to submit an invented item id, or no squad at all, and were refused with
/// SBC_PLAYER_NOT_OWNED / SBC_WRONG_PLAYER_COUNT before reaching the behaviour they exist to check.
/// This helper only *constructs* a candidate squad from real club cards; the service still validates
/// it independently on submit, so a squad built here that does not actually satisfy the challenge
/// still fails the test rather than passing it.
///
/// Requirement types are handled explicitly and anything unrecognised fails loudly, so a fixture can
/// never quietly end up submitting a squad that skips a rule the server would have enforced.
/// </summary>
internal static class SbcFixtures
{
    private const string G = "/ut/game/fifa21";

    /// <summary>
    /// The PUT .../squad body for a squad this club can field for <paramref name="challenge"/>, its players in the
    /// challenge's free slots (a brick wall refuses a player in a locked slot).
    /// Selection reads current club state, so it must be called immediately before the save.
    ///
    /// Chemistry is switched off for this runtime: these squads are picked by count, nations, leagues and rating only, and a
    /// starter club cannot reach retail chemistry targets. The server's chemistry check has its own tests (SbcChemistryTests).
    /// </summary>
    public static JsonObject SquadBody(TestRuntime rt, SbcChallenge challenge)
    {
        rt.Game.Rules.Sbc.IgnoredRequirements.Add("CHEMISTRY");
        var free = FreeSlots(rt, challenge);
        var players = new JsonArray();
        foreach (var (id, slot) in SelectSquad(rt, challenge).Zip(free))
        {
            players.Add(new JsonObject { ["index"] = slot, ["itemData"] = new JsonObject { ["id"] = id } });
        }
        return new JsonObject { ["players"] = players };
    }

    /// <summary>The slots a player may fill: playerRequirements entries the client is told are "DEFAULT".</summary>
    public static int[] FreeSlots(TestRuntime rt, SbcChallenge challenge) =>
        rt.Fut.Sbc.EmptySquad(challenge.Id)["playerRequirements"]!.AsArray().OfType<JsonObject>()
            .Where(entry => entry.Text("playerType") == "DEFAULT").Select(entry => (int)entry.Int("index")).ToArray();

    /// <summary>Saves a squad for <paramref name="challenge"/>.</summary>
    public static void SaveSatisfyingSquad(TestRuntime rt, SbcChallenge challenge) =>
        rt.Call("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SquadBody(rt, challenge));

    /// <summary>One eligible owned card of this tier (First Exchange asks for a bronze), minting one when the club has none.</summary>
    public static long OwnedPlayerItemId(TestRuntime rt, string tier)
    {
        EnsureTier(rt, tier, 1);
        var store = rt.Game.Store();
        var item = store.ListItems("club").Concat(store.ListItems("squad"))
            .First(candidate => ClubInventoryService.IsPlayer(candidate)
                && !candidate.Bool("dream")
                && candidate.Int("loans") == 0
                && rt.Game.Players.Find(candidate.Int("resourceId")) is { } meta && meta.Tier == tier);
        return item.Int("id");
    }

    /// <summary>One eligible owned card, for fixtures that need a real id rather than a whole squad.</summary>
    public static long AnyOwnedPlayerItemId(TestRuntime rt)
    {
        var store = rt.Game.Store();
        var item = store.ListItems("club").Concat(store.ListItems("squad"))
            .First(candidate => ClubInventoryService.IsPlayer(candidate)
                && !candidate.Bool("dream")
                && candidate.Int("loans") == 0
                && rt.Game.Players.Find(candidate.Int("resourceId")) is not null);
        return item.Int("id");
    }

    /// <summary>
    /// Whether this helper can build a squad for <paramref name="challenge"/>. Requirements such as
    /// RARE, TOTW, SAME_CLUB and exact QUALITY cannot be met from a starter club at all, so a fixture
    /// must pick a challenge that is actually satisfiable rather than submit something invalid.
    /// </summary>
    public static bool CanBuildSquad(SbcChallenge challenge) => TryParse(challenge, out _);

    /// <summary>What a challenge asks of the squad, as far as this helper can build it.</summary>
    private sealed record Plan(int Count, int MinimumNations, int ExactNations, int MinimumLeagues, int ExactLeagues, long MinimumRating, string Tier);

    private static bool TryParse(SbcChallenge challenge, out Plan plan)
    {
        var count = 11;
        int minimumNations = 0, exactNations = 0, minimumLeagues = 0, exactLeagues = 0;
        long minimumRating = 0;
        var tier = string.Empty;
        plan = new Plan(count, 0, 0, 0, 0, 0, tier);
        foreach (var rule in challenge.Source.Obj("requirements")?.Arr("rules")?.Objects() ?? Enumerable.Empty<JsonObject>())
        {
            var attributes = rule.Obj("attributes") ?? new JsonObject();
            switch (rule.Text("type").ToUpperInvariant())
            {
                case "PLAYERS":
                    count = checked((int)attributes.Int("max", attributes.Int("exact", 11)));
                    break;
                // SquadBody switches chemistry off for the runtime it builds in (see there).
                case "CHEMISTRY":
                    break;
                case "NATIONS":
                    minimumNations = checked((int)attributes.Int("min"));
                    exactNations = checked((int)attributes.Int("exact"));
                    break;
                case "LEAGUES":
                    minimumLeagues = checked((int)attributes.Int("min"));
                    exactLeagues = checked((int)attributes.Int("exact"));
                    break;
                case "RATING":
                    minimumRating = attributes.Int("min");
                    break;
                case "QUALITY":
                    tier = attributes.Text("value").ToLowerInvariant();
                    break;
                default:
                    return false;
            }
        }
        // One exact grouping is built directly; two at once would need a search this helper does not do.
        if (exactNations > 0 && exactLeagues > 0) return false;
        plan = new Plan(count, minimumNations, exactNations, minimumLeagues, exactLeagues, minimumRating, tier);
        return true;
    }

    /// <summary>
    /// Mints club cards of <paramref name="tier"/> (distinct footballers, plain cards) until the club holds
    /// <paramref name="wanted"/> of them: a starter club owns no bronzes, and First Exchange and Let's Move Forward ask for them.
    /// </summary>
    private static void EnsureTier(TestRuntime rt, string tier, int wanted)
    {
        var store = rt.Game.Store();
        var owned = store.ListItems("club").Concat(store.ListItems("squad"))
            .Where(ClubInventoryService.IsPlayer)
            .Select(item => rt.Game.Players.Find(item.Int("resourceId")))
            .Where(meta => meta is not null && meta.Tier == tier)
            .Select(meta => meta!.Int("assetId")).ToHashSet();
        foreach (var entry in rt.Game.Players.Entries.Where(entry => entry.Value.Tier == tier && !entry.Value.IsSpecial && !rt.Game.Cards.IsGrantOnly(entry.Key)))
        {
            if (owned.Count >= wanted) return;
            if (owned.Add(entry.Value.Int("assetId"))) rt.Fut.Items.MakePlayer(entry.Key, "club");
        }
    }

    private static long[] SelectSquad(TestRuntime rt, SbcChallenge challenge)
    {
        Assert.True(TryParse(challenge, out var plan),
            $"SbcFixtures cannot build a squad for challenge {challenge.Key}; pick one CanBuildSquad accepts or extend the helper.");
        // Enough cards of the tier that exact or minimum nation and league counts can be met from them.
        if (plan.Tier.Length > 0) EnsureTier(rt, plan.Tier, 60);

        var store = rt.Game.Store();
        var candidates = store.ListItems("club").Concat(store.ListItems("squad"))
            .Where(ClubInventoryService.IsPlayer)
            .Where(item => !item.Bool("dream") && item.Int("loans") == 0)
            .Select(item => (Item: item, Meta: rt.Game.Players.Find(item.Int("resourceId"))))
            .Where(pair => pair.Meta is not null && pair.Meta.Rating >= plan.MinimumRating && (plan.Tier.Length == 0 || pair.Meta.Tier == plan.Tier))
            .Select(pair => (pair.Item, Meta: pair.Meta!))
            .ToList();

        // One card per footballer: the service refuses SBC_DUPLICATE_FOOTBALLER.
        var byFootballer = new Dictionary<long, (JsonObject Item, PlayerMeta Meta)>();
        foreach (var entry in candidates)
        {
            byFootballer.TryAdd(entry.Meta.Int("assetId"), entry);
        }
        IEnumerable<(JsonObject Item, PlayerMeta Meta)> pool = byFootballer.Values.ToList();

        // An exact count keeps the pool to that many groups (the largest), one card from each first.
        var chosen = new List<(JsonObject Item, PlayerMeta Meta)>();
        foreach (var (exact, group) in new (int, Func<PlayerMeta, long>)[] { (plan.ExactNations, meta => meta.Int("nation")), (plan.ExactLeagues, meta => meta.Int("leagueId")) })
        {
            if (exact == 0) continue;
            var groups = pool.GroupBy(entry => group(entry.Meta)).OrderByDescending(g => g.Count()).Take(exact).ToList();
            pool = groups.SelectMany(g => g).ToList();
            chosen.AddRange(groups.Select(g => g.First()).Take(plan.Count));
        }
        Take(chosen, pool, plan.MinimumNations, meta => meta.Int("nation"), plan.Count);
        Take(chosen, pool, plan.MinimumLeagues, meta => meta.Int("leagueId"), plan.Count);
        foreach (var entry in pool)
        {
            if (chosen.Count >= plan.Count)
            {
                break;
            }
            if (!chosen.Contains(entry))
            {
                chosen.Add(entry);
            }
        }

        Assert.True(chosen.Count == plan.Count,
            $"the club has only {chosen.Count} eligible cards for challenge {challenge.Key}, which needs {plan.Count}");
        int Distinct(Func<PlayerMeta, long> group) => chosen.Select(e => group(e.Meta)).Distinct().Count();
        Assert.True(plan.MinimumNations == 0 || Distinct(meta => meta.Int("nation")) >= plan.MinimumNations,
            $"could not reach {plan.MinimumNations} nations for challenge {challenge.Key}");
        Assert.True(plan.ExactNations == 0 || Distinct(meta => meta.Int("nation")) == plan.ExactNations,
            $"could not field exactly {plan.ExactNations} nations for challenge {challenge.Key}");
        Assert.True(plan.MinimumLeagues == 0 || Distinct(meta => meta.Int("leagueId")) >= plan.MinimumLeagues,
            $"could not reach {plan.MinimumLeagues} leagues for challenge {challenge.Key}");
        Assert.True(plan.ExactLeagues == 0 || Distinct(meta => meta.Int("leagueId")) == plan.ExactLeagues,
            $"could not field exactly {plan.ExactLeagues} leagues for challenge {challenge.Key}");
        return chosen.Select(entry => entry.Item.Int("id")).ToArray();
    }

    /// <summary>Adds cards with distinct <paramref name="group"/> values until the minimum is reached.</summary>
    private static void Take(
        List<(JsonObject Item, PlayerMeta Meta)> chosen,
        IEnumerable<(JsonObject Item, PlayerMeta Meta)> pool,
        int minimumDistinct,
        Func<PlayerMeta, long> group,
        int count)
    {
        var seen = chosen.Select(entry => group(entry.Meta)).ToHashSet();
        foreach (var entry in pool)
        {
            if (seen.Count >= minimumDistinct || chosen.Count >= count)
            {
                return;
            }
            if (!chosen.Contains(entry) && seen.Add(group(entry.Meta)))
            {
                chosen.Add(entry);
            }
        }
    }
}
