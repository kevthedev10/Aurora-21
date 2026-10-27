using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>
/// The seasonal CDN document (FIFA 21's "Seasonal" framework). Division Rivals is <see cref="RivalsService"/> and the
/// Weekend League <see cref="WeekendLeagueService"/>.
/// </summary>
public sealed class SeasonsService
{
    private readonly FutServices _fut;

    public SeasonsService(FutServices fut) => _fut = fut;

    private SeasonRules Rules => _fut.Game.Rules.Seasons;

    /// <summary>The seasonal CDN document, with the current weekly competition (Thursday 07:00 UTC turnover) open.</summary>
    public JsonObject SeasonalConfig()
    {
        var now = _fut.Game.NowSeconds;
        var week = Rules.Rivals.WeekIndex(now);
        var start = Rules.Rivals.WeekStart(week);
        var end = Rules.Rivals.WeekStart(week + 1);
        var schedule = TemplateLibrary.Core.Array("seasons/week-schedule", new TemplateArgs
        {
            ["weekIndex"] = week,
            ["start"] = start,
            ["end"] = end,
            ["rewardsTime"] = end + Rules.Champions.RewardsDelaySeconds,
        });
        return TemplateLibrary.Core.Object("seasons/seasonal-config", new TemplateArgs
        {
            ["start"] = start,
            ["end"] = end,
            ["current"] = now,
            ["weeklyMatches"] = Rules.Rivals.WeeklyMatches,
            ["placementMatches"] = Rules.Rivals.PlacementMatches,
            ["playoffMatches"] = Rules.Champions.PlayoffMatches,
            // The Weekend League's match count: one number for this document and the league documents (maxMatches).
            ["finalsMatches"] = Rules.WeekendLeague.MaxMatches,
            ["qualificationPoints"] = Rules.WeekendLeague.QualificationPoints,
            ["scheduleViewMinutes"] = Rules.Champions.ScheduleViewPeriodMinutes,
            ["schedule"] = schedule,
        });
    }
}
