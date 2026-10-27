using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// rules/tradeability.json (owner decision D14): each grant source's tradeability is a rules lever, first set to PLAN 3.1,
/// and the reward grants that used to skip the untradeable flag (SBC, objectives) now go through it. The SBC menu's pack
/// tiles say the tradeability their payout grants, so a tradeable pack no longer shows as untradeable.
/// </summary>
public sealed class TradeabilitySwitchTests
{
    [Fact]
    public void TheFirstSettingIsPlanThreePointOne()
    {
        var rules = GameRules.Load(TestRuntime.DataRoot).Tradeability;
        foreach (var source in new[] { RewardSources.Store, RewardSources.Sbc, RewardSources.Objective, RewardSources.Season, RewardSources.RivalsOptionA,
                     RewardSources.RivalsOptionB, RewardSources.RivalsFixed, RewardSources.RivalsPromotion, RewardSources.WeekendLeague,
                     RewardSources.SquadBattles, RewardSources.DraftOnline, RewardSources.Operator })
        {
            Assert.False(rules.Untradeable(source, 400), source);
        }
        foreach (var source in new[] { RewardSources.RivalsOptionC, RewardSources.DraftSingle, RewardSources.Gift, RewardSources.MatchPack,
                     RewardSources.Pick, RewardSources.Pity, RewardSources.GuaranteedInform })
        {
            Assert.True(rules.Untradeable(source, 400), source);
        }
        // Every pick and red pick is untradeable whoever grants it.
        foreach (var pack in new long[] { 513, 514, 515, 516, 550, 551, 552, 553 })
        {
            Assert.All(RewardSources.All, source => Assert.True(rules.Untradeable(source, pack), $"{source} {pack}"));
        }
        // The guaranteed in-form is untradeable from objectives and Season Progress, a tradeable competition pack elsewhere.
        Assert.True(rules.Untradeable(RewardSources.Objective, 542));
        Assert.True(rules.Untradeable(RewardSources.Season, 542));
        Assert.False(rules.Untradeable(RewardSources.WeekendLeague, 542));
        Assert.False(rules.Untradeable(RewardSources.SquadBattles, 542));
        Assert.False(rules.Untradeable(RewardSources.DraftOnline, 542));

        // Rivals options by position: A, B, C for a choice of three; a rank's single reward is fixed.
        Assert.Equal(new[] { RewardSources.RivalsOptionA, RewardSources.RivalsOptionB, RewardSources.RivalsOptionC },
            Enumerable.Range(0, 3).Select(index => RewardSources.RivalsOption(index, 3)));
        Assert.Equal(RewardSources.RivalsFixed, RewardSources.RivalsOption(0, 1));

        Assert.Throws<InvalidOperationException>(() => rules.Untradeable("rivals"));
    }

    [Fact]
    public void ARulesFileMustSetEverySourceAndNoOther()
    {
        var path = Path.Combine(TestRuntime.DataRoot, "rules", "tradeability.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var missing = document.DeepClone().AsObject();
        missing["sources"]!.AsObject().Remove(RewardSources.MatchPack);
        Assert.Contains(RewardSources.MatchPack, Assert.Throws<InvalidDataException>(() => RulesJson.Deserialize<TradeabilityRules>(missing.ToJsonString(), path)).Message);
        var unknown = document.DeepClone().AsObject();
        unknown["sources"]!["rivals"] = true;
        Assert.Contains("rivals", Assert.Throws<InvalidDataException>(() => RulesJson.Deserialize<TradeabilityRules>(unknown.ToJsonString(), path)).Message);
    }

    [Fact]
    public void SbcAndObjectivePacksAreGrantedThroughTheSwitch()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        List<(long Pack, string Source, bool Untradeable)> Packs() => rt.Game.InUnit(uow => uow.QueryAsync(
            "SELECT pack_id, source, untradeable FROM ultimateteam.unopened_packs WHERE club_id=@club ORDER BY id",
            r => (r.GetInt64(0), r.GetString(1), r.GetBoolean(2))));

        // The Welcome reward (earned with one match): pick packs from objectives, untradeable by the pack override.
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));
        rt.Fut.Objectives.Claim(null, null);
        var welcome = Packs().Where(pack => pack.Source == RewardSources.Objective).ToList();
        Assert.NotEmpty(welcome);
        Assert.All(welcome, pack => Assert.Equal(rt.Game.Rules.Tradeability.Untradeable(RewardSources.Objective, pack.Pack), pack.Untradeable));

        // An SBC pack: tradeable in the first setting, untradeable once the lever is pulled (a rules edit, no code).
        var challenge = rt.Game.Sbc.Challenges.First(challenge => rt.Fut.Sbc.GrantChallengeRewards(challenge).Any(award => award.Text("itemType") == "pack"));
        var granted = Packs().Where(pack => pack.Source == RewardSources.Sbc).ToList();
        Assert.NotEmpty(granted);
        Assert.All(granted, pack => Assert.Equal(rt.Game.Rules.Tradeability.Untradeable(RewardSources.Sbc, pack.Pack), pack.Untradeable));
        rt.Game.Rules.Tradeability.Sources[RewardSources.Sbc] = true;
        rt.Fut.Sbc.GrantChallengeRewards(challenge);
        Assert.All(Packs().Where(pack => pack.Source == RewardSources.Sbc).Skip(granted.Count), pack => Assert.True(pack.Untradeable));
    }

    [Fact]
    public void AnSbcPackAwardSaysTheTradeabilityItsPayoutGrants()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        JsonObject PackAwardOf(string setKey)
        {
            var set = rt.Game.Sbc.Sets.Single(set => set.Key == setKey);
            var entry = set.Challenges
                .SelectMany(challenge => challenge.Source.Obj("rewards")?.Arr("entries")?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                .First(reward => reward.Text("type") == "PACK");
            return rt.Fut.Sbc.AwardJson(entry, setKey)!;
        }

        // A non-repeatable set's pack is granted tradeable (the sbc source), so the SBC menu's tile stops saying untradeable.
        var foundations = PackAwardOf("FND_LEAGUE_NATION_BASICS");
        Assert.False(foundations.Bool("untradeable"));
        Assert.False(foundations.Bool("isUntradeable"));
        // A repeatable set pays untradeable (rules/sbc.json) and its tile says so.
        foreach (var upgrade in new[] { "UPG_BRONZE", "UPG_SILVER", "UPG_GOLD" })
        {
            var tile = PackAwardOf(upgrade);
            Assert.True(tile.Bool("untradeable"), upgrade);
            Assert.True(tile.Bool("isUntradeable"), upgrade);
        }

        // The lever moves the label with the payout: the same Foundations tile once the sbc source is untradeable.
        rt.Game.Rules.Tradeability.Sources[RewardSources.Sbc] = true;
        Assert.True(PackAwardOf("FND_LEAGUE_NATION_BASICS").Bool("untradeable"));
        Assert.True(PackAwardOf("FND_LEAGUE_NATION_BASICS").Bool("isUntradeable"));
    }

    [Fact]
    public void SeasonAndWelcomePackAwardsSayTheTradeabilityTheirPayoutsGrant()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        rt.Game.Store().AddObjectivesXp(12 * rt.Game.Rules.Objectives.SeasonProgress.XpPerLevel);
        JsonObject Campaign() => rt.CallObject("GET", "/ut/game/fifa21/scmp/campaign/active");

        // Every Season Progress level's pack tile states what claiming it pays (the season source of the lever).
        JsonObject? TileOf(JsonObject level) =>
            (level.Arr("rewardOptionList")?[0] as JsonObject)?.Arr("awards")?[0] as JsonObject;
        var levels = Campaign()["levels"].A().OfType<JsonObject>()
            .Select(level => (Level: level, Tile: TileOf(level)))
            .Where(entry => JsonValues.HasValue(entry.Tile?.Get("packId")))
            .ToList();
        Assert.NotEmpty(levels);
        foreach (var entry in levels)
        {
            var tile = entry.Tile!;
            var untradeable = rt.Game.Rules.Tradeability.Untradeable(RewardSources.Season, tile.Int("packId"));
            Assert.Equal(untradeable, tile.Bool("untradeable"));
            Assert.Equal(untradeable, tile.Bool("isUntradeable"));
        }
        // A plain pack level (not the 542 in-form) is tradeable, so its tile stops saying untradeable.
        var plain = levels.First(entry => rt.Game.Rules.Tradeability.Untradeable(RewardSources.Season, entry.Tile!.Int("packId")) is false);
        Assert.False(plain.Tile!.Bool("untradeable"));

        // The welcome group's pick pack is untradeable (the packs override) and its tile says so.
        var welcome = Campaign()["categories"]![0]!["groups"].Objects().Single(group => group["id"].L() == 1);
        var welcomeTile = (welcome.Arr("awardsList")?[0] as JsonObject)!;
        Assert.True(welcomeTile.Bool("untradeable"));
        Assert.True(welcomeTile.Bool("isUntradeable"));

        // The lever moves the labels with the payouts: the same plain level once the season source is untradeable.
        rt.Game.Rules.Tradeability.Sources[RewardSources.Season] = true;
        var flipped = Campaign()["levels"].A().OfType<JsonObject>()
            .First(level => TileOf(level)?.Int("packId") == plain.Tile!.Int("packId"));
        Assert.True(TileOf(flipped)!.Bool("untradeable"));
        Assert.True(TileOf(flipped)!.Bool("isUntradeable"));
    }
}
