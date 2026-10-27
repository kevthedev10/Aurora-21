using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>The operator's pause for new matches (MatchGate, migration 108): online pairing and single-player starts.</summary>
public sealed class MatchPauseTests
{
    private const string G = "/ut/game/fifa21";
    private const string HostId = "441100000000000001";
    private const string GuestId = "441100000000000002";

    [Fact]
    public void PausedSearchesNeitherQueueNorPairAndResumedSearchesPair()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        var host = new BlazeClient(hostLease.Session, new BlazeResponses(hostLease.Session.Fut), new Pipe().Writer, "10.0.0.5", "10.0.0.1");
        var guest = new BlazeClient(guestLease.Session, new BlazeResponses(guestLease.Session.Fut), new Pipe().Writer, "10.0.0.6", "10.0.0.1");
        var gate = new MatchGate { Open = false };
        var registry = new MatchmakingRegistry(null, NullLogger.Instance, pairing: () => gate.Open);

        foreach (var mode in new[] { "FUT_Rivals", "fut_champions", "fut_draft" })
        {
            Assert.Equal(0, registry.Start(HostId, mode, [], host));
            Assert.Equal(0, registry.Start(GuestId, mode, [], guest));
        }
        Assert.Null(registry.GameIdFor(HostId));
        Assert.False(registry.IsSearching(HostId) || registry.IsSearching(GuestId));
        // Not shutting down: the panel shows the pause separately.
        Assert.True(registry.Accepting);

        gate.Open = true;
        Assert.NotEqual(0, registry.Start(HostId, "FUT_Rivals", [], host));
        Assert.NotEqual(0, registry.Start(GuestId, "FUT_Rivals", [], guest));
        var gameId = registry.GameIdFor(HostId);
        Assert.NotNull(gameId);
        Assert.Equal(gameId, registry.GameIdFor(GuestId));

        // Pausing again does not end a paired game.
        gate.Open = false;
        Assert.Equal(gameId, registry.GameIdFor(HostId));
    }

    /// <summary>
    /// A player serving a leaver penalty (LeaverCooldown, owner 2026-10-09) is answered like a paused search: never queued nor
    /// paired, in every online mode, while everyone else pairs as usual; once the cooldown ends they pair again.
    /// </summary>
    [Fact]
    public void ALeaverOnCooldownNeitherQueuesNorPairs()
    {
        using var rt = TestRuntime.Create();
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions(), shared: true);
        using var hostLease = players.Acquire(PlayerIdentity.Of(HostId));
        using var guestLease = players.Acquire(PlayerIdentity.Of(GuestId));
        var host = new BlazeClient(hostLease.Session, new BlazeResponses(hostLease.Session.Fut), new Pipe().Writer, "10.0.0.5", "10.0.0.1");
        var guest = new BlazeClient(guestLease.Session, new BlazeResponses(guestLease.Session.Fut), new Pipe().Writer, "10.0.0.6", "10.0.0.1");
        long now = 10_000;
        var cooldown = new LeaverCooldown();
        cooldown.Set(GuestId, new LeaverCooldown.Penalty(1, now + 3600));
        var registry = new MatchmakingRegistry(null, NullLogger.Instance, barredUntil: player => cooldown.Until(player, now));

        foreach (var mode in new[] { "FUT_Rivals", "fut_champions", "fut_draft", "FUT_HouseRules" })
        {
            Assert.NotEqual(0, registry.Start(HostId, mode, [], host));
            Assert.Equal(0, registry.Start(GuestId, mode, [], guest));
        }
        Assert.Null(registry.GameIdFor(HostId));
        Assert.True(registry.IsSearching(HostId));
        Assert.False(registry.IsSearching(GuestId));

        now += 3600;
        Assert.NotEqual(0, registry.Start(HostId, "FUT_Rivals", [], host));
        Assert.NotEqual(0, registry.Start(GuestId, "FUT_Rivals", [], guest));
        Assert.NotNull(registry.GameIdFor(GuestId));
    }

    [Fact]
    public void PausedMatchesRefuseSquadBattlesUntilResumed()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        rt.Game.MatchGate.Open = false;

        var (status, body, _) = rt.CallRaw("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1, ["sqbtMatchDifficulty"] = 3 });
        Assert.Equal(400, status);
        Assert.Equal("Matches are paused by the server", body["reason"]!.GetValue<string>());

        rt.Game.MatchGate.Open = true;
        Assert.Equal(200, rt.CallRaw("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1, ["sqbtMatchDifficulty"] = 3 }).Status);
    }
}
