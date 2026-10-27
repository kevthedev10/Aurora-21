using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Custom seasons (owner, 2026-10-07): rules/objectives.json schedules named seasons, each with its own Season Pass. A club's
/// Season XP and level claims are kept per season (migration 166): a new season starts from zero, and a season that returns
/// (the default season after a custom one) gives back what the club had, so no level pays twice.
/// </summary>
public sealed class CustomSeasonTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Thu 1 Oct 2026 12:00 UTC, before the custom season.</summary>
    private const long BeforeSeason = 1790856000;

    private static readonly DateTimeOffset SeasonStart = DateTimeOffset.FromUnixTimeSeconds(BeforeSeason + 86400);
    private static readonly DateTimeOffset SeasonEnd = SeasonStart.AddDays(28);

    private sealed class Clock(long unixSeconds) : TimeProvider
    {
        public long Now { get; set; } = unixSeconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Now);
    }

    private static JsonObject Key(params JsonNode[] path) => new() { ["file"] = "objectives", ["path"] = new JsonArray(path) };

    /// <summary>"Winter Season", id 2: five levels of 1,000 XP, level 1 paying 777 coins.</summary>
    private static JsonObject Winter() => new()
    {
        ["id"] = 2, ["name"] = "Winter Season", ["startsAt"] = SeasonStart.ToString("O"), ["endsAt"] = SeasonEnd.ToString("O"),
        ["levelCount"] = 5, ["xpPerLevel"] = 1000, ["rewardLevelCap"] = null,
        ["levelRewards"] = new JsonArray(Enumerable.Range(1, 5).Select(level => (JsonNode?)new JsonObject { ["coins"] = level == 1 ? 777 : 100 * level }).ToArray()),
    };

    private static async Task PublishSeasonsAsync(TestRuntime rt, params JsonObject[] seasons)
    {
        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        await changes.SetChangeAsync(RulesArea.AreaName, Key("seasons"), ContentOperation.Update,
            new JsonObject { ["value"] = new JsonArray(seasons.Select(season => (JsonNode?)season).ToArray()) }, null);
        await changes.PublishAsync("custom seasons", "test");
    }

    [Fact]
    public async Task ACustomSeasonHasItsOwnNameDatesLevelsAndXpAndTheDefaultSeasonResumesAfterIt()
    {
        var clock = new Clock(BeforeSeason);
        using var rt = TestRuntime.Create(time: clock);
        await PublishSeasonsAsync(rt, Winter());
        var defaults = rt.Game.Rules.Objectives.SeasonProgress;

        // The default season: the club earns two levels and claims level 1 (10,000 coins).
        rt.Game.Store().AddObjectivesXp(2 * defaults.XpPerLevel);
        var credits = rt.Game.Store().Credits();
        rt.Call("POST", $"{G}/scmp/campaign/level/1/reward/0");
        Assert.Equal(credits + 10000, rt.Game.Store().Credits());

        // The custom season: its name, dates and five levels, and the club starts at 0 XP with nothing claimed.
        clock.Now = SeasonStart.ToUnixTimeSeconds();
        var campaign = rt.CallObject("GET", $"{G}/scmp/campaign/active");
        Assert.Equal((2L, "Winter Season"), (campaign.Int("id"), campaign.Text("name")));
        Assert.Equal((SeasonStart.ToUnixTimeSeconds(), SeasonEnd.ToUnixTimeSeconds()), (campaign.Int("startTime"), campaign.Int("endTime")));
        Assert.Equal(0, campaign.Int("userXp"));
        var levels = campaign["levels"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(5, levels.Count);
        Assert.All(levels, level => Assert.NotEqual(2, level.Int("state")));
        rt.CallRaw("POST", $"{G}/scmp/campaign/level/1/reward/0");
        Assert.Equal(credits + 10000, rt.Game.Store().Credits());

        // Season XP earned now counts for the custom season, whose level 1 pays its own reward.
        rt.Game.Store().AddObjectivesXp(1500);
        rt.Call("POST", $"{G}/scmp/campaign/level/1/reward/0");
        Assert.Equal(credits + 10000 + 777, rt.Game.Store().Credits());
        var winter = rt.Game.Store().Objectives();
        Assert.Equal(1500, winter.UserXp);
        Assert.Equal([1L], winter.ClaimedLevels);

        // Back in the default season after it: the club's XP and claimed level 1 are as it left them, so level 1 never pays twice.
        clock.Now = SeasonEnd.ToUnixTimeSeconds();
        var back = rt.CallObject("GET", $"{G}/scmp/campaign/active");
        Assert.Equal(1L, back.Int("id"));
        Assert.Equal(2 * defaults.XpPerLevel, back.Int("userXp"));
        Assert.Equal(2, back["levels"]![0]!.AsObject().Int("state"));
        rt.Call("POST", $"{G}/scmp/campaign/level/1/reward/0");
        Assert.Equal(credits + 10000 + 777, rt.Game.Store().Credits());
        rt.Call("POST", $"{G}/scmp/campaign/level/2/reward/0");
        Assert.Contains(2L, rt.Game.Store().Objectives().ClaimedLevels);

        // And the custom season keeps its own progress too.
        clock.Now = SeasonStart.ToUnixTimeSeconds() + 60;
        Assert.Equal(1500, rt.CallObject("GET", $"{G}/scmp/campaign/active").Int("userXp"));
    }

    [Fact]
    public async Task StartingTheDefaultSeasonFreshUnderANewIdClearsXpAndClaims()
    {
        // The panel's "Start fresh" (owner, 2026-10-08) publishes campaign.id = the next free season id.
        using var rt = TestRuntime.Create(time: new Clock(BeforeSeason));
        var defaults = rt.Game.Rules.Objectives.SeasonProgress;
        rt.Game.Store().AddObjectivesXp(2 * defaults.XpPerLevel);
        rt.Call("POST", $"{G}/scmp/campaign/level/1/reward/0");
        var credits = rt.Game.Store().Credits();

        var changes = new ContentChangesets(rt.Game, ContentAreas.Default);
        await changes.SetChangeAsync(RulesArea.AreaName, Key("campaign", "id"), ContentOperation.Update, new JsonObject { ["value"] = 2 }, null);
        await changes.PublishAsync("start fresh", "test");

        var fresh = rt.CallObject("GET", $"{G}/scmp/campaign/active");
        Assert.Equal((2L, 0L), (fresh.Int("id"), fresh.Int("userXp")));
        Assert.All(fresh["levels"]!.AsArray().OfType<JsonObject>(), level => Assert.NotEqual(2, level.Int("state")));

        // Level 1 can be earned and claimed again in the new season.
        rt.Game.Store().AddObjectivesXp(defaults.XpPerLevel);
        rt.Call("POST", $"{G}/scmp/campaign/level/1/reward/0");
        Assert.Equal(credits + 10000, rt.Game.Store().Credits());
        Assert.Equal([1L], rt.Game.Store().Objectives().ClaimedLevels);
    }

    [Fact]
    public void SeasonsMustNotOverlapOrReuseAnId()
    {
        var text = File.ReadAllText(Path.Combine(TestRuntime.DataRoot, "rules", "objectives.json"));
        string With(params JsonObject[] seasons)
        {
            var document = JsonNode.Parse(text)!.AsObject();
            document["seasons"] = new JsonArray(seasons.Select(season => (JsonNode?)season).ToArray());
            return document.ToJsonString();
        }
        var rules = RulesJson.Deserialize<ObjectiveRules>(With(Winter()), "seasons");
        Assert.Equal("Winter Season", rules.SeasonAt(SeasonStart.ToUnixTimeSeconds()).Name);
        Assert.True(rules.SeasonAt(BeforeSeason).IsDefault);

        var overlapping = Winter();
        overlapping["id"] = 3;
        overlapping["startsAt"] = SeasonStart.AddDays(7).ToString("O");
        overlapping["endsAt"] = SeasonEnd.AddDays(7).ToString("O");
        Assert.Throws<InvalidDataException>(() => RulesJson.Deserialize<ObjectiveRules>(With(Winter(), overlapping), "seasons"));
        var defaultId = Winter();
        defaultId["id"] = 1;
        Assert.Throws<InvalidDataException>(() => RulesJson.Deserialize<ObjectiveRules>(With(defaultId), "seasons"));
        var backwards = Winter();
        backwards["endsAt"] = SeasonStart.AddDays(-1).ToString("O");
        Assert.Throws<InvalidDataException>(() => RulesJson.Deserialize<ObjectiveRules>(With(backwards), "seasons"));
        var short_ = Winter();
        short_["levelCount"] = 6;
        Assert.Throws<InvalidDataException>(() => RulesJson.Deserialize<ObjectiveRules>(With(short_), "seasons"));
    }
}
