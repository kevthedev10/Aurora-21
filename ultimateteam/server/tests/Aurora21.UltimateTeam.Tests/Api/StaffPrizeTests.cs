using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Staff prize exclusion (pack-odds-2026-09 PLAN.md 5.2 and 7.2, rules/staff.json), off in the shipped rules (owner: staff count) and
/// switched on here for Rivals, the Weekend League and Squad Battles: a staff club is left out when the
/// Weekend League, a Squad Battles week or a Division Rivals week settles, and its place passes to the next player.
/// </summary>
public sealed class StaffPrizeTests
{
    private const string G = "/ut/game/fifa21";
    private const string StaffDiscordId = "222222";

    /// <summary>Friday 2026-10-02 08:00 UTC: Weekend League 2961, Squad Battles week 2960 and Rivals week 2961 are all live.</summary>
    private const long TestWeekend = 1_790_928_000;

    /// <summary>Monday 2026-10-05 16:00 UTC: Weekend League 2961's rewards time, after Squad Battles week 2960 settled.</summary>
    private const long Monday = 1_791_216_000;

    /// <summary>Thursday 2026-10-08 07:10 UTC: Rivals week 2961 has closed and settles.</summary>
    private const long RivalsSettles = 1_791_443_400;

    /// <summary>Puts a Discord account on the panel's maintenance bypass list, which rules/staff.json counts as staff.</summary>
    private static void MarkStaff(TestRuntime rt, string discordId) =>
        rt.Game.InUnit(uow => uow.ExecuteAsync(
            "INSERT INTO ultimateteam.player_access(discord_id, maintenance_bypass) VALUES (@id, true)", ("id", discordId)));

    /// <summary>Excludes staff from every competition's prizes for this runtime: the shipped rules exclude them from none.</summary>
    private static TestRuntime Excluding(TestRuntime rt)
    {
        Assert.Empty(rt.Game.Rules.Staff.PrizesIneligibleIn);
        rt.Game.Rules.Staff.PrizesIneligibleIn.AddRange(new[] { StaffRules.Rivals, StaffRules.WeekendLeague, StaffRules.SquadBattles });
        return rt;
    }

    private static RivalsRecord Placed(TestRuntime rt, long weeklyScore) => new()
    {
        RatingVersion = rt.Game.Rules.Seasons.Rivals.RatingVersion,
        WeekId = 2961,
        SkillRating = 1100,
        Placed = true,
        PlacementPlayed = 5,
        WeekDivision = 5,
        BestDivision = 5,
        Division = 5,
        WeeklyScore = weeklyScore,
        WeeklyCounted = 1,
        WeeklyGames = 1,
    };

    [Fact]
    public void StaffAreTheBypassListAndAreLeftOutUntilTheTestWeekEnds()
    {
        using var rt = Excluding(TestRuntime.Create(time: new TestRuntime.FixedTime(TestWeekend)));
        var staff = rt.Game.Rules.Staff;
        Assert.True(staff.MaintenanceBypassIsStaff);
        var until = DateTimeOffset.Parse("2026-10-12T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds();
        foreach (var competition in new[] { StaffRules.Rivals, StaffRules.WeekendLeague, StaffRules.SquadBattles })
        {
            Assert.True(staff.Excludes(competition, until));
            Assert.False(staff.Excludes(competition, until + 1));
        }
        var rules = rt.Game.Rules.Seasons;
        // The test week: Weekend League 2961, Rivals week 2961 and the Squad Battles weeks ending 5 and 12 October; not Weekend League 2962.
        Assert.True(staff.Excludes(StaffRules.WeekendLeague, rules.WeekendLeague.Event(2961).End));
        Assert.True(staff.Excludes(StaffRules.Rivals, rules.Rivals.WeekStart(2962)));
        Assert.True(staff.Excludes(StaffRules.SquadBattles, rt.Game.Rules.SquadBattles.Competition.WeekEnd(2961)));
        Assert.False(staff.Excludes(StaffRules.WeekendLeague, rules.WeekendLeague.Event(2962).End));

        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var member = players.Acquire(PlayerIdentity.Of(StaffDiscordId));
        Assert.Empty(rt.Game.InUnit(uow => uow.StaffClubIdsAsync(staff)));
        MarkStaff(rt, StaffDiscordId);
        Assert.Equal(new[] { member.Session.Game.InUnit(uow => new ValueTask<long>(uow.ClubId)) }, rt.Game.InUnit(uow => uow.StaffClubIdsAsync(staff)));
    }

    [Fact]
    public void AStaffClubWinsNoWeekendLeaguePrizeAndRankOnePassesOn()
    {
        var clock = new TestRuntime.MovableTime(TestWeekend);
        using var rt = Excluding(TestRuntime.Create(time: clock));
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var member = players.Acquire(PlayerIdentity.Of(StaffDiscordId));
        MarkStaff(rt, StaffDiscordId);
        // The staff club went 20-0; this club 19-1.
        member.Session.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 20, 20, 0, 0, 5)));
        rt.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 20, 19, 0, 1, 5)));
        clock.Now = Monday;
        var prize = Assert.Single(rt.CallObject("POST", $"{G}/champion/user/prize")["awardedPrizes"]!.AsArray().OfType<JsonObject>());
        Assert.Equal((2961L, 1L, 1L), (prize.Int("eventId"), prize.Int("rank"), prize.Int("tierLevel")));
        Assert.Empty(member.Session.Game.InUnit(uow => uow.UnclaimedChampionResultsAsync(2961, 2961)));
    }

    [Fact]
    public void AStaffClubWinsNoSquadBattlesPrizeAndItsPlacePassesOn()
    {
        var clock = new TestRuntime.MovableTime(TestWeekend);
        using var rt = Excluding(TestRuntime.Create(time: clock));
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var member = players.Acquire(PlayerIdentity.Of(StaffDiscordId));
        using var third = players.Acquire(PlayerIdentity.Of("333333"));
        MarkStaff(rt, StaffDiscordId);
        SquadBattlesWeek Week(long points) => SquadBattlesWeek.Blank(2960) with { Points = points, Games = 10, Wins = 6, Losses = 4 };
        member.Session.Game.InUnit(uow => uow.SaveSquadBattlesWeekAsync(Week(3000)));
        rt.Game.InUnit(uow => uow.SaveSquadBattlesWeekAsync(Week(2000)));
        third.Session.Game.InUnit(uow => uow.SaveSquadBattlesWeekAsync(Week(1000)));
        clock.Now = Monday;
        rt.Call("GET", $"{G}/sqbt/user/hub");
        var mine = Assert.Single(rt.Game.InUnit(uow => uow.UnclaimedSquadBattlesResultsAsync(2960, 2960)));
        Assert.Equal(("rank1", 1), (mine.TierId, mine.Rank));
        Assert.Empty(member.Session.Game.InUnit(uow => uow.UnclaimedSquadBattlesResultsAsync(2960, 2960)));
        Assert.Equal(2, Assert.Single(third.Session.Game.InUnit(uow => uow.UnclaimedSquadBattlesResultsAsync(2960, 2960))).Position);
    }

    [Fact]
    public void AStaffClubIsLeftOutOfTheRivalsPercentiles()
    {
        var clock = new TestRuntime.MovableTime(TestWeekend);
        using var rt = Excluding(TestRuntime.Create(time: clock));
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 8 }, shared: true);
        var member = players.Acquire(PlayerIdentity.Of(StaffDiscordId));
        var others = new[] { "333333", "444444", "555555", "666666" }.Select(id => players.Acquire(PlayerIdentity.Of(id))).ToList();
        try
        {
            MarkStaff(rt, StaffDiscordId);
            // Six clubs with the staff club on top: counted, this club would be second of six (34th percentile, Rank II); without it,
            // first of five (20th percentile, Rank I).
            member.Session.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 9000)));
            rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 5000)));
            foreach (var (other, index) in others.Select((other, index) => (other, index)))
            {
                other.Session.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 1000 + index * 500)));
            }
            clock.Now = RivalsSettles;
            Assert.True(rt.CallObject("GET", $"{G}/rivals/user/hub").Bool("isPrizeAvailable"));
            Assert.Equal(1, rt.Game.InUnit(uow => uow.RivalsResultAsync(2961))!.Rank);
            Assert.Null(member.Session.Game.InUnit(uow => uow.RivalsResultAsync(2961)));
        }
        finally
        {
            member.Dispose();
            others.ForEach(other => other.Dispose());
        }
    }
}
