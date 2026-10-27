using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

public sealed partial class SbcService
{
    private static class DailyClock
    {
        // Invariant-globalization Windows builds cannot translate IANA names through ICU.
        internal static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");
    }

    /// <summary>The current 7pm-to-7pm London content day, including GMT/BST changes.</summary>
    public static long DailyResetAt(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, DailyClock.London);
        var date = local.Date;
        if (local.Hour < 19) date = date.AddDays(-1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.AddHours(19), DateTimeKind.Unspecified), DailyClock.London)).ToUnixTimeSeconds();
    }

    private static bool IsDaily(SbcSet set) => set.Source.Bool("repeatable") && set.Source.Bool("repeatableDaily");

    private async ValueTask<Dictionary<string, long>> CurrentCountsAsync(IUnitOfWork work, long? resetAt = null)
    {
        var counts = await work.SbcCompletionCountsAsync();
        var dailySets = Game.Sbc.Sets.Where(IsDaily).ToArray();
        if (dailySets.Length == 0) return counts;
        var daily = (await work.QueryAsync("SELECT challenge_key,completion_count FROM ultimateteam.sbc_daily_completed WHERE club_id=@club AND reset_at=@reset",
            r => (Key: r.GetString(0), Count: r.GetInt64(1)), ("reset", resetAt ?? DailyResetAt(DateTimeOffset.FromUnixTimeSeconds(work.NowSeconds))))).ToDictionary(r => r.Key, r => r.Count, StringComparer.Ordinal);
        foreach (var set in dailySets)
            foreach (var challenge in set.Challenges)
            {
                counts.Remove(challenge.Key);
                if (daily.TryGetValue(challenge.Key, out var count)) counts[challenge.Key] = count;
            }
        return counts;
    }
}
