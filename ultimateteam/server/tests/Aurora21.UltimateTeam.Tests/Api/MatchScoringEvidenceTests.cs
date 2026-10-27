using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class MatchScoringEvidenceTests
{
    private static JsonObject Attribute(string key, JsonNode? value) => new() { ["key"] = key, ["value"] = value };

    [Fact]
    public void EvidencePreservesUnknownNumericFlagsButNeverCopiesArbitraryStringsOrFields()
    {
        var body = new JsonObject
        {
            ["token"] = "secret-token", ["personaId"] = 999,
            ["items"] = new JsonArray(new JsonObject { ["id"] = 123, ["goals"] = 1, ["assists"] = 0, ["name"] = "secret-name", ["leagueId"] = 999 }),
            ["events"] = new JsonArray(new JsonObject { ["attributes"] = new JsonArray(
                Attribute("name","goal"), Attribute("player_id","456"), Attribute("flag",900),Attribute("flag",900),
                Attribute("authCode","secret-code"), Attribute("flag","secret-flag")) }),
        };
        var evidence = MatchScoringEvidence.Summarize(body,new Dictionary<long,JsonObject>
        {
            [123] = new() { ["resourceId"] = 789, ["assetId"] = 456, ["leagueId"] = 39, ["nation"] = 25 },
        });
        var text = evidence.ToJsonString();
        Assert.DoesNotContain("secret",text);
        Assert.DoesNotContain("personaId",text);
        Assert.Equal(39,evidence["players"]![0]!["leagueId"]!.GetValue<long>());
        Assert.Equal(456,evidence["events"]![0]!["playerId"]!.GetValue<long>());
        Assert.Equal("goal",evidence["events"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(3,evidence["events"]![0]!["flags"]!.AsArray().Count);
        Assert.Equal(900,evidence["events"]![0]!["flags"]![0]!.GetValue<long>());
        Assert.Null(evidence["events"]![0]!["flags"]![2]);
    }

    [Fact]
    public void EvidenceBoundsPlayersEventsAndAttributesAndMarksMissingEvents()
    {
        var body = new JsonObject
        {
            ["items"] = new JsonArray(Enumerable.Range(1,30).Select(i=>(JsonNode)new JsonObject { ["id"] = i }).ToArray()),
            ["events"] = new JsonArray(Enumerable.Range(1,140).Select(i=>(JsonNode)new JsonObject
            {
                ["attributes"] = new JsonArray(Enumerable.Range(1,80).Select(flag=>(JsonNode)Attribute("flag",flag)).ToArray()),
            }).ToArray()),
        };
        Assert.Equal(23,MatchScoringEvidence.ItemIds(body).Length);
        var evidence = MatchScoringEvidence.Summarize(body,new Dictionary<long,JsonObject>());
        Assert.Equal(23,evidence["players"]!.AsArray().Count);
        Assert.Equal(128,evidence["events"]!.AsArray().Count);
        Assert.Equal(64,evidence["events"]![0]!["flags"]!.AsArray().Count);
        Assert.True(evidence.Bool("eventsTruncated"));
        Assert.True(evidence.Bool("itemsTruncated"));
        Assert.True(evidence["events"]![0]!.AsObject().Bool("attributesTruncated"));
        body.Remove("events");
        Assert.False(MatchScoringEvidence.Summarize(body,new Dictionary<long,JsonObject>()).Bool("eventsPresent"));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public void NativeMatchEndRouteCapturesEventsEvenWithObjectivesOffAndNoProgress(string method)
    {
        var provider = new RecordingProvider();
        using var loggers = LoggerFactory.Create(builder=>builder.AddProvider(provider).SetMinimumLevel(LogLevel.Information));
        using var rt = TestRuntime.Create(loggers:loggers);
        Assert.All(rt.Game.Rules.Objectives.GrindGroups.Where(g=>g.SourceKey is not null),g=>Assert.False(rt.Game.Rules.Objectives.IsLive(g.Id)));
        var item = rt.Fut.Items.MakePlayer(50520590,"club");
        var asset = rt.Game.Players.Find(50520590)!.Int("assetId");
        var before = rt.Game.Store().Objectives().Stat("matches");
        rt.Call(method,"/ut/game/fifa21/match/end",new JsonObject
        {
            ["endReason"] = "NO_CONTEST", ["gid"] = 0, ["authCode"] = "secret-test-code",
            ["myMatchStats"] = new JsonObject { ["goals"] = 1 },
            ["items"] = new JsonArray(new JsonObject { ["id"] = item.Int("id"), ["goals"] = 1, ["assists"] = 0 }),
            ["events"] = new JsonArray(new JsonObject { ["attributes"] = new JsonArray(
                Attribute("name","goal"),Attribute("player_id",asset.ToString()),Attribute("flag",900)) }),
        });
        var line = Assert.Single(provider.Messages,m=>m.StartsWith("SCORING EVIDENCE ",StringComparison.Ordinal));
        Assert.DoesNotContain("secret-test-code",line);
        var evidence = JsonNode.Parse(line["SCORING EVIDENCE ".Length..])!;
        Assert.Equal(item.Int("id"),evidence["players"]![0]!["itemId"]!.GetValue<long>());
        Assert.Equal(asset,evidence["players"]![0]!["assetId"]!.GetValue<long>());
        Assert.Equal(asset,evidence["events"]![0]!["playerId"]!.GetValue<long>());
        Assert.Equal(before,rt.Game.Store().Objectives().Stat("matches"));
    }

    private sealed class RecordingProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);
        public void Dispose() { }
        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState:notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
                => messages.Enqueue(formatter(state,exception));
        }
    }
}
