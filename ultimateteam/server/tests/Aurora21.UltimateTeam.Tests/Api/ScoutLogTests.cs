using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class ScoutLogTests
{
    private const string G = "/ut/game/fifa21";

    [Fact]
    public void OptInCaptureRecordsCustomTacticSavesWithoutChangingTheReply()
    {
        var provider = new RecordingProvider();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(provider));
        using var rt = TestRuntime.Create(loggers: loggers, prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "user-config"));
            File.WriteAllText(Path.Combine(root, "user-config", "config.json"), """{"diagnostics":{"capture":true}}""");
        });
        Assert.True(rt.Game.Config.CaptureDiagnostics);
        var body = new JsonObject { ["tactics"] = new JsonArray(new JsonObject { ["tactic"] = "BALANCED", ["width"] = 50 }) };
        Assert.True(JsonNode.DeepEquals(new JsonObject(), rt.Call("POST", $"{G}/tactics?squadId=1", body)));
        var captured = Assert.Single(provider.Messages, message => message.StartsWith("TACTICS-SCOUT REQUEST", StringComparison.Ordinal));
        Assert.Contains("path=/ut/game/fifa21/tactics?squadId=1", captured);
        Assert.Contains("\"width\":50", captured);
    }

    [Fact]
    public void OptInCaptureRecordsWholeDraftSquadRearrangements()
    {
        var provider = new RecordingProvider();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(provider));
        using var rt = TestRuntime.Create(loggers: loggers, prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "user-config"));
            File.WriteAllText(Path.Combine(root, "user-config", "config.json"), """{"diagnostics":{"capture":true}}""");
        });
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        var squad = rt.CallObject("GET", $"{G}/squad/{rt.Game.Rules.Draft.SquadId}");
        var body = new JsonObject { ["formation"] = squad.Str("formation"), ["players"] = squad["players"]!.DeepClone() };
        Assert.True(body.ToJsonString().Length > 2000);
        rt.Call("PUT", $"{G}/squad/{rt.Game.Rules.Draft.SquadId}", body);
        var captured = Assert.Single(provider.Messages, message => message.StartsWith("DRAFT-SQUAD-SCOUT REQUEST method=PUT", StringComparison.Ordinal));
        Assert.EndsWith("body=" + body.ToJsonString(), captured);
        Assert.DoesNotContain(provider.Messages, message => message.StartsWith("DRAFT-SQUAD-SCOUT REQUEST method=GET", StringComparison.Ordinal));
    }

    private sealed class RecordingProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);
        public void Dispose() { }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
