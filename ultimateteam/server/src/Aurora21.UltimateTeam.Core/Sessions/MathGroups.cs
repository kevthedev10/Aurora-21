using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Sessions;

/// <summary>
/// Each player's Windows maths group, so online searchers pair only within one group (seasons.json _mathGroupsComment;
/// Python server 43d3bac, c3992c5): the Windows build from the launcher's health check, and the maths bridge's report of the
/// ucrtbase it switched FIFA to. Shared by every club this process serves and kept in state/windows-builds.json, so a
/// restart still knows each player's group.
/// </summary>
public sealed partial class MathGroups
{
    // BridgeAvx2 is null for a bridge too old to report it, which counts as a CPU with AVX2.
    private sealed record Report(long? Build, long? Revision, long? BridgeBuild, bool BridgeFma3, long Seen, bool? BridgeAvx2 = null);

    private readonly object _gate = new();
    private readonly string _path;
    // Read per call, so a content reload of seasons.json reaches pairing.
    private readonly Func<MathGroupRules> _rules;
    private readonly ILogger _log;
    private Dictionary<string, Report>? _reports;

    public MathGroups(string path, MathGroupRules rules, ILogger log)
        : this(path, () => rules, log)
    {
    }

    /// <param name="rules">The current seasons.json maths-group rules (the live content).</param>
    public MathGroups(string path, Func<MathGroupRules> rules, ILogger log)
    {
        _path = path;
        _rules = rules;
        _log = log;
    }

    /// <summary>
    /// Records a bootstrap request's maths: a WindowsPowerShell/5.1.build.revision User-Agent is the launcher's health check,
    /// which starts a new launch and clears the last bridge report; an X-Aurora21-Math header is the bridge's report.
    /// </summary>
    public void Note(string playerId, string? userAgent, string? bridgeHeader, long now) =>
        Record(playerId, WindowsBuildPattern().Match(userAgent ?? string.Empty), bridgeHeader, now);

    /// <summary>
    /// Records a hosted launch's maths: the launcher asks for its launch code (POST /api/v1/launch/code, after the account
    /// service has verified the player) with an X-Aurora21-Windows header of the OS build and revision ("26100.4652"), and an
    /// X-Aurora21-Math header when it runs the maths bridge. The local launcher's PowerShell health check never reaches a
    /// hosted server, so without this every hosted player's group is unknown and pairs with anyone.
    /// </summary>
    public void NoteLaunch(string playerId, string? windowsHeader, string? bridgeHeader, long now) =>
        Record(playerId, WindowsHeaderPattern().Match(windowsHeader ?? string.Empty), bridgeHeader, now);

    /// <summary>Whether an X-Aurora21-Math value follows the bridge contract; the recorders above ignore any other value.</summary>
    public static bool IsBridgeReport(string? bridgeHeader) => BridgePattern().IsMatch(bridgeHeader ?? string.Empty);

    private void Record(string playerId, Match windows, string? bridgeHeader, long now)
    {
        var bridge = BridgePattern().Match(bridgeHeader ?? string.Empty);
        if (string.IsNullOrEmpty(playerId) || (!windows.Success && !bridge.Success))
        {
            return;
        }
        lock (_gate)
        {
            var reports = Load();
            var before = reports.GetValueOrDefault(playerId);
            var record = before ?? new Report(null, null, null, false, now);
            if (windows.Success)
            {
                record = record with { Build = Parse(windows.Groups[1].Value), Revision = Parse(windows.Groups[2].Value), BridgeBuild = null, BridgeFma3 = false, BridgeAvx2 = null };
            }
            if (bridge.Success)
            {
                var avx2 = Avx2Pattern().Match(bridgeHeader!);
                record = record with { BridgeBuild = Parse(bridge.Groups[3].Value), BridgeFma3 = bridge.Groups[6].Value == "1", BridgeAvx2 = avx2.Success ? avx2.Groups[1].Value == "1" : null };
            }
            if (before is not null && before with { Seen = 0 } == record with { Seen = 0 })
            {
                return;
            }
            reports[playerId] = record with { Seen = now };
            Save(reports);
            _log.LogWarning("MATH GROUP player={Player} windows={Build}.{Revision} bridge={Bridge} -> {Group}", playerId, record.Build, record.Revision,
                record.BridgeBuild, GroupOf(record));
        }
    }

    /// <summary>A player's maths group, or null while it is not known.</summary>
    public string? GroupOf(string playerId)
    {
        lock (_gate)
        {
            return Load().TryGetValue(playerId, out var record) ? GroupOf(record) : null;
        }
    }

    /// <summary>Whether two players' FIFA maths agree: always when the rule is off, and an unknown group pairs with anyone.</summary>
    public bool Compatible(string first, string second)
    {
        if (!_rules().Enabled)
        {
            return true;
        }
        var (a, b) = (GroupOf(first), GroupOf(second));
        return a is null || b is null || a == b;
    }

    private string? GroupOf(Report record) =>
        record.BridgeBuild is { } bridged ? _rules().GroupOfBridge(bridged, record.BridgeFma3, record.BridgeAvx2 ?? true)
        : record.Build is { } build ? _rules().GroupOfWindows(build) : null;

    private static long Parse(string value) => long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private Dictionary<string, Report> Load()
    {
        if (_reports is not null)
        {
            return _reports;
        }
        _reports = new Dictionary<string, Report>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path) && FutJson.ParseObject(File.ReadAllText(_path)) is { } stored)
            {
                foreach (var (player, node) in stored)
                {
                    if (node is JsonObject entry)
                    {
                        _reports[player] = new Report(entry.Get("build") is null ? null : entry.Int("build"), entry.Get("revision") is null ? null : entry.Int("revision"),
                            entry.Get("bridgeBuild") is null ? null : entry.Int("bridgeBuild"), entry.Bool("bridgeFma3"), entry.Int("seen"),
                            entry.Get("bridgeAvx2") is null ? null : entry.Bool("bridgeAvx2"));
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _log.LogWarning("MATH GROUP could not read {Path}: {Error}", _path, error.Message);
        }
        return _reports;
    }

    private void Save(Dictionary<string, Report> reports)
    {
        var document = new JsonObject();
        foreach (var (player, record) in reports.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            document[player] = new JsonObject
            {
                ["build"] = record.Build,
                ["revision"] = record.Revision,
                ["bridgeBuild"] = record.BridgeBuild,
                ["bridgeFma3"] = record.BridgeFma3,
                ["bridgeAvx2"] = record.BridgeAvx2,
                ["seen"] = record.Seen,
            };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, FutJson.Serialize(document));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("MATH GROUP could not save {Path}: {Error}", _path, error.Message);
        }
    }

    [GeneratedRegex(@"WindowsPowerShell/\d+\.\d+\.(\d+)\.(\d+)")]
    private static partial Regex WindowsBuildPattern();

    [GeneratedRegex(@"^\s*(\d{4,6})\.(\d{1,6})\s*$")]
    private static partial Regex WindowsHeaderPattern();

    // The bridge's optional CPU field after fma3 (bridges from 10-07 on); see GroupOfBridge.
    [GeneratedRegex(@"(?:^|;)\s*avx2=([01])\s*(?:;|$)")]
    private static partial Regex Avx2Pattern();

    [GeneratedRegex(@"(?:^|;)\s*ucrt=(\d+)\.(\d+)\.(\d+)\.(\d+)\s*;\s*sha256=([0-9A-Fa-f]{16})\s*;\s*fma3=([01])\s*(?:;|$)")]
    private static partial Regex BridgePattern();
}
