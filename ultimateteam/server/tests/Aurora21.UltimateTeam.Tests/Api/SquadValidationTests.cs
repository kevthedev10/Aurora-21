using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Server-side squad validation (PLAN 7.1), off by default (owner: developers play their Pro cards everywhere) and switched on
/// here with seasons.json rewardEligibility.restrictGrantOnlyCards: a squad holding a Pro, custom or grant-only card cannot enter Rivals, the Weekend
/// League or Squad Battles, is never a Squad Battles opponent, and a match played with one pays nothing; friendlies may.
/// </summary>
public sealed class SquadValidationTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Tuesday 2026-09-29 12:00 UTC: Squad Battles week 2960.</summary>
    private const long Tuesday = 1_790_683_200;

    /// <summary>Puts a new copy of a card into the club's active squad at a slot; returns its item id.</summary>
    private static long PutInSquad(GameContext game, FutServices fut, long resourceId, int index) =>
        game.InUnit(async uow =>
        {
            var item = await fut.Items.MakePlayerAsync(uow, resourceId, "club");
            var squadId = await uow.ActiveSquadIdAsync();
            var squad = (await uow.GetSquadAsync(squadId))!;
            var slots = squad.Arr("players")!;
            var slot = new JsonObject { ["index"] = index, ["itemId"] = item.Int("id") };
            var existing = slots.OfType<JsonObject>().Select((entry, position) => (entry, position)).FirstOrDefault(pair => pair.entry.Int("index", pair.position) == index);
            if (existing.entry is not null)
            {
                slots[existing.position] = slot;
            }
            else
            {
                slots.Add(slot);
            }
            await uow.SaveSquadAsync(squadId, squad.Text("squadName"), true, squad);
            return item.Int("id");
        });

    private static long ProCard(TestRuntime rt) => rt.Game.Cards.ProCardIds.First();

    /// <summary>Turns the squad restriction on for this runtime: the shipped rules leave it off.</summary>
    private static TestRuntime Restricting(TestRuntime rt)
    {
        Assert.All(rt.Game.Cards.ProCardIds, id => Assert.False(rt.Fut.Matches.IsRestrictedCard(id)));
        rt.Game.Rules.Seasons.RewardEligibility.RestrictGrantOnlyCards = true;
        return rt;
    }

    [Fact]
    public void CompetitiveModesRefuseASquadWithAProCardAndFriendliesTakeIt()
    {
        using var rt = Restricting(TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday)));
        Assert.All(rt.Game.Cards.ProCardIds, id => Assert.True(rt.Fut.Matches.IsRestrictedCard(id)));
        // On the bench, not in the XI: still refused.
        PutInSquad(rt.Game, rt.Fut, ProCard(rt), 12);

        foreach (var create in new[]
        {
            new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 2960, ["sqbtOpponentSquadId"] = 0, ["sqbtMatchDifficulty"] = 3 },
            new JsonObject { ["squadId"] = 1, ["type"] = "ONLINE", ["matchSubType"] = "RIVALS_MATCH", ["rivalsEventId"] = 2960 },
            new JsonObject { ["squadId"] = 1, ["type"] = "ONLINE", ["championId"] = 1 },
        })
        {
            var (status, body, _) = rt.CallRaw("POST", $"{G}/match", create);
            Assert.Equal((400, 471L), (status, body.AsObject().Int("code")));
            Assert.Contains("Pro", body.AsObject().Text("reason"));
        }
        // Refused creates leave no Squad Battles match pending for the next end.
        Assert.False(rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN", ["matchId"] = 1, ["secondsPlayed"] = 5461 }).ContainsKey("squadBattlesScore"));
        // A friendly takes the squad.
        Assert.Equal(200, rt.CallRaw("POST", $"{G}/match", new JsonObject { ["squadId"] = 1 }).Status);
    }

    [Fact]
    public void AMatchPlayedWithAProCardPaysNothing()
    {
        using var rt = Restricting(TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday)));
        var item = PutInSquad(rt.Game, rt.Fut, ProCard(rt), 0);
        var credits = rt.Game.Store().Credits();

        rt.Game.InUnit(uow => rt.Fut.Rivals.NotePairingAsync(uow, new OnlineOpponent(true, "opponent", 1_234_567, 9), 1000));
        var ended = rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = "WIN",
            ["matchId"] = 9,
            ["secondsPlayed"] = 5461,
            ["items"] = new JsonArray(new JsonObject { ["id"] = item }),
            ["myMatchStats"] = new JsonObject { ["goals"] = 2 },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = 0 },
        });

        Assert.Equal(0, ended.Int("matchCoins"));
        Assert.Equal(credits, rt.Game.Store().Credits());
        Assert.Equal(0, rt.Game.Store().Objectives().Stat("matches"));
        Assert.Equal(1L, rt.Game.InUnit(uow => uow.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.match_rewards WHERE club_id=@club AND verdict='RestrictedSquad'")));
    }

    [Fact]
    public void AClubWithAProCardIsNeverASquadBattlesOpponent()
    {
        using var rt = Restricting(TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday), prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "state"));
            File.WriteAllText(Path.Combine(root, "state", "player-names.json"), """{ "222222": "Second Team", "333333": "Third Team" }""");
        }));
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var second = players.Acquire(PlayerIdentity.Of("222222"));
        using var third = players.Acquire(PlayerIdentity.Of("333333"));
        second.Session.Game.FillMatchdaySquad(second.Session.Fut);
        third.Session.Game.FillMatchdaySquad(third.Session.Fut);
        PutInSquad(second.Session.Game, second.Session.Fut, ProCard(rt), 15);

        var names = rt.CallObject("GET", $"{G}/sqbt/user/hub")["sqbtOppSquadList"]!["sqbtOppSquads"]!.AsArray().OfType<JsonObject>()
            .Select(tile => tile.Text("clubName")).ToList();

        // Two real clubs fill the Medium and Hard thirds: the clean one is drawn, the one with the Pro card never is.
        Assert.Contains(names, name => name.Contains("Third", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("Second", StringComparison.Ordinal));
    }
}
