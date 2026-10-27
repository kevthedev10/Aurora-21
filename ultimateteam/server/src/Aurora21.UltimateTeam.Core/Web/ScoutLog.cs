using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Core.Web;

/// <summary>Opt-in request capture (diagnostics.capture) used while reverse engineering new screens.</summary>
public sealed class ScoutLog
{
    private static readonly (string Label, string[] Tokens)[] Topics =
    {
        ("PLAYER-PICK", new[] { "playerpick", "/item/nontargeted" }),
        ("STORE", new[] { "store", "pack", "purchase", "loc" }),
        ("DRAFT", new[] { "draft" }),
        ("SQUAD-BATTLES", new[] { "squadbattle", "squad/battle", "squad_battle", "sqbt" }),
        ("SCMP", new[] { "scmp", "objective", "campaign", "milestone" }),
        ("SEASONAL", new[] { "seasonal", "rivals", "champion", "champs", "weekendleague", "weekend_league", "/wl/", "seasonalplay", "competition" }),
        ("SBC", new[] { "/sbs/" }),
        // Custom tactic saves: their native field set is untraced, so relational instruction storage stays blocked.
        ("TACTICS", new[] { "/tactics" }),
    };

    private const int BodyPreviewBytes = 2000;

    private readonly ILogger _log;

    public ScoutLog(ILogger log) => _log = log;

    /// <summary>Logs the request once for every topic its path mentions.</summary>
    public void Record(FutRequest request)
    {
        foreach (var (label, _) in Topics.Where(topic => topic.Tokens.Any(token => request.Lower.Contains(token, StringComparison.Ordinal))))
        {
            _log.LogInformation("{Label}-SCOUT REQUEST method={Method} path={Path} contentType={ContentType} body={Body}",
                label, request.Method, request.RawTarget, request.ContentType, request.BodyPreview(BodyPreviewBytes));
        }
    }
}
