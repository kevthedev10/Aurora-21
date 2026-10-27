using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Server.Blaze;

public sealed class BlazeQosSettings
{
    public required long ProbeCount { get; init; }
    public required long ServiceId { get; init; }
    public required long TimeoutMs { get; init; }
    public required string PingSite { get; init; }
}

public sealed class BlazePreAuthSettings
{
    public required List<long> ComponentIds { get; init; }
    public required JsonObject ClientConfig { get; init; }
    public required BlazeQosSettings Qos { get; init; }
}

public sealed class BlazeTelemetrySettings
{
    public required long Port { get; init; }
    public required string Disabled { get; init; }
}

public sealed class BlazeTickerSettings
{
    public required long Port { get; init; }
    public required string SettingId { get; init; }
    public required string GroupId { get; init; }
}

public sealed class BlazeSessionSettings
{
    public required string TitleSessionKey { get; init; }
    public required string TitleEmail { get; init; }
    public required string LocalEmail { get; init; }
}

public sealed class BlazeEntitlementSettings
{
    public required List<string> Names { get; init; }
    public required string Tag { get; init; }
}

/// <summary>Stats component leaderboards: the period counters and the single ranked stat.</summary>
public sealed class BlazeStatsSettings
{
    public required int EpochYear { get; init; }
    public required long DailyRetention { get; init; }
    public required long WeeklyRetention { get; init; }
    public required long MonthlyRetention { get; init; }
    public required int LeaderboardSize { get; init; }
    public required string StatName { get; init; }
    public required string StatShortName { get; init; }
    public required string StatLongName { get; init; }
}

/// <summary>Names and defaults of matchmade Rivals games.</summary>
public sealed class BlazeMatchmakingSettings
{
    public required string DefaultProtocolVersion { get; init; }
    public required string GameNamePrefix { get; init; }

    /// <summary>The name prefix of a game of another paired mode (Online Draft), followed by the mode.</summary>
    public required string ModeGameNamePrefix { get; init; }

    public required string GameUuidPrefix { get; init; }
}

/// <summary>Limits on the OSDK pregame attribute maps a client may store and relay.</summary>
public sealed class BlazeOsdkAttributeLimits
{
    public required int MaxPairs { get; init; }
    public required int MaxBytes { get; init; }
    public required int MaxStoredKeys { get; init; }
}

/// <summary>Limits on the Blaze Messaging ClientMessages the relay forwards.</summary>
public sealed class BlazeMessagingLimits
{
    public required int MaxTargetIds { get; init; }
    public required int MaxAttributePairs { get; init; }
    public required int MaxAttributeBytes { get; init; }
}

/// <summary>The online-match wire values the production Python server ran with (see blaze.json _onlineComment).</summary>
public sealed class BlazeOnlineSettings
{
    public required long UserPlatform { get; init; }
    public required long NetworkTopology { get; init; }
    public required long InitialGameSettings { get; init; }
    public required string OsdkRosterVersion { get; init; }
    public required long GameSetupTelemetryMicroseconds { get; init; }
    public required long GameSetupJoinerResult { get; init; }

    /// <summary>GAME.ATTR fifaGameSpeed at start-up (0 Slow, 1 Normal, 2 Fast); null sends no key (FIFA plays Normal).</summary>
    public long? GameSpeed { get; init; }

    public required BlazeOsdkAttributeLimits OsdkAttributes { get; init; }
    public required BlazeMessagingLimits Messaging { get; init; }
}

/// <summary>Resources/blaze.json: the local Blaze server's identity and client configuration.</summary>
public sealed class BlazeSettings
{
    public required string ServerVersion { get; init; }
    public required string BuildSource { get; init; }
    public required string InstanceName { get; init; }
    public required string PersonaNamespace { get; init; }
    public required string Platform { get; init; }
    public required long UserLocation { get; init; }
    public required BlazePreAuthSettings PreAuth { get; init; }
    public required Dictionary<string, JsonObject> ClientConfigs { get; init; }
    public required JsonObject UserOptions { get; init; }
    public required BlazeTelemetrySettings Telemetry { get; init; }
    public required BlazeTickerSettings Ticker { get; init; }
    public required BlazeSessionSettings Session { get; init; }
    public required BlazeEntitlementSettings Entitlements { get; init; }
    public required BlazeStatsSettings Stats { get; init; }
    public required BlazeMatchmakingSettings Matchmaking { get; init; }
    public required BlazeOnlineSettings Online { get; init; }
    public required List<int[]> EmptyReplies { get; init; }

    private static readonly Lazy<BlazeSettings> Loaded = new(() =>
        RulesJson.Deserialize<BlazeSettings>(EmbeddedResources.ReadText(typeof(BlazeSettings).Assembly, "blaze.json"), "blaze.json"));

    /// <summary>The embedded settings, read once.</summary>
    public static BlazeSettings Load() => Loaded.Value;

    /// <summary>A string map in document order, with <c>{name}</c> placeholders filled from <paramref name="values"/>.</summary>
    public static List<KeyValuePair<string, string>> Pairs(JsonObject map, params (string Name, object Value)[] values) =>
        map.Select(entry => KeyValuePair.Create(entry.Key, RuleText.Format(entry.Value!.GetValue<string>(), values))).ToList();
}
