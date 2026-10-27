using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using static Aurora21.UltimateTeam.Core.Data.ClubSql;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>
/// Relational SBC progress, season progress, Rivals standings and idempotent match settlement
/// (sbc_completed, sbc_squad_counts, rivals_progress, champions_progress, match_settlements).
/// </summary>
internal static class RelationalProgressionRepository
{
    // Season documents are rebuilt from typed columns; a NULL column is an absent field, as before.
    private static readonly (string Field, string Column)[] RivalsFields =
    {
        ("seasonId", "season_id"), ("division", "division"), ("skillRating", "skill_rating"),
        ("weeklyGames", "weekly_games"), ("weeklyWins", "weekly_wins"), ("weeklyDraws", "weekly_draws"), ("weeklyPoints", "weekly_points"),
        ("wins", "wins"), ("draws", "draws"), ("losses", "losses"), ("titleWins", "title_wins"),
        ("placementMatchesPlayed", "placement_matches_played"), ("matchesPlayed", "matches_played"),
    };

    private static readonly (string Field, string Column)[] ChampionFields =
    {
        ("playoffGames", "playoff_games"), ("playoffWins", "playoff_wins"),
        ("finalsGames", "finals_games"), ("finalsWins", "finals_wins"),
    };

    private static (string Table, (string Field, string Column)[] Fields) Season(string competition) => competition switch
    {
        "rivals" => ("rivals_progress", RivalsFields),
        "champion" => ("champions_progress", ChampionFields),
        _ => throw new NotSupportedException($"Season competition '{competition}' has no relational table; audit required."),
    };

    // ---- SBC ----------------------------------------------------------------

    internal static async ValueTask<HashSet<string>> RelationalSbcCompletedAsync(this IUnitOfWork w) =>
        new(await w.QueryAsync("SELECT challenge_key FROM ultimateteam.sbc_completed WHERE club_id = @club", r => r.GetString(0)), StringComparer.Ordinal);

    internal static async ValueTask<bool> TryMarkRelationalSbcCompletedAsync(this IUnitOfWork w, string challengeKey) =>
        await w.ExecuteAsync("""
            INSERT INTO ultimateteam.sbc_completed(club_id, challenge_key) VALUES(@club, @key)
            ON CONFLICT (club_id, challenge_key) DO NOTHING
            """, ("key", challengeKey)) == 1;

    internal static async ValueTask<long> RelationalSbcSquadCountAsync(this IUnitOfWork w, long challengeId) =>
        await w.ScalarAsync<long?>("SELECT placed FROM ultimateteam.sbc_squad_counts WHERE club_id = @club AND challenge_id = @id", ("id", challengeId)) ?? 0;

    internal static ValueTask<int> SetRelationalSbcSquadCountAsync(this IUnitOfWork w, long challengeId, long placed) =>
        w.ExecuteAsync("""
            INSERT INTO ultimateteam.sbc_squad_counts(club_id, challenge_id, placed) VALUES(@club, @id, @placed)
            ON CONFLICT (club_id, challenge_id) DO UPDATE SET placed = excluded.placed
            """, ("id", challengeId), ("placed", placed));

    // ---- seasons ------------------------------------------------------------

    internal static async ValueTask<JsonObject> RelationalSeasonProgressAsync(this IUnitOfWork w, string competition)
    {
        var (table, fields) = Season(competition);
        var columns = string.Join(", ", fields.Select(f => f.Column));
        var rows = await w.QueryAsync($"SELECT {columns} FROM ultimateteam.{table} WHERE club_id = @club",
            r => fields.Select((_, index) => r.IsDBNull(index) ? (long?)null : r.GetInt64(index)).ToArray());
        return rows.Count == 0 ? new JsonObject() : Document(fields, rows[0]);
    }

    /// <summary>
    /// Replaces a season document. Only the typed fields current readers use are accepted; any other
    /// field (for example an imported <c>matchesPlayed</c>) has no column and is rejected, not dropped.
    /// </summary>
    internal static ValueTask<int> SaveRelationalSeasonProgressAsync(this IUnitOfWork w, string competition, JsonObject data)
    {
        var (table, fields) = Season(competition);
        var values = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var (key, node) in data)
        {
            var field = Array.Find(fields, f => f.Field == key);
            if (field.Column is null)
            {
                throw new NotSupportedException($"Season field '{key}' has no relational column; audit required.");
            }
            if (node is null)
            {
                values[field.Column] = null;
            }
            else if (JsonValues.IsStrictInt(node, out var value))
            {
                values[field.Column] = value;
            }
            else
            {
                throw new NotSupportedException($"Season field '{key}' is not an integer; audit required.");
            }
        }
        var columns = fields.Select(f => f.Column).ToArray();
        var parameters = columns.Select(c => (c, (object?)values.GetValueOrDefault(c))).ToArray();
        return w.ExecuteAsync($"""
            INSERT INTO ultimateteam.{table}(club_id, {string.Join(", ", columns)})
            VALUES(@club, {string.Join(", ", columns.Select(c => "@" + c + "::bigint"))})
            ON CONFLICT (club_id) DO UPDATE SET {string.Join(", ", columns.Select(c => $"{c} = excluded.{c}"))}
            """, parameters);
    }

    /// <summary>Every named club with its Rivals document, in club order, as the legacy standings query.</summary>
    internal static async ValueTask<List<(long PersonaId, string Name, JsonObject Progress)>> RelationalRivalsStandingsAsync(this IUnitOfWork w)
    {
        var columns = string.Join(", ", RivalsFields.Select(f => "p." + f.Column));
        return (await w.QueryAsync($"""
            SELECT c.persona_id, c.display_name, {columns}
            FROM ultimateteam.clubs c LEFT JOIN ultimateteam.rivals_progress p ON p.club_id = c.id
            WHERE c.display_name IS NOT NULL AND btrim(c.display_name) <> ''
            ORDER BY c.id
            """,
            r => (r.GetInt64(0), r.GetString(1),
                RivalsFields.Select((_, index) => r.IsDBNull(index + 2) ? (long?)null : r.GetInt64(index + 2)).ToArray())))
            .Select(row => (row.Item1, row.Item2, Document(RivalsFields, row.Item3)))
            .ToList();
    }

    private static JsonObject Document((string Field, string Column)[] fields, long?[] values)
    {
        var document = new JsonObject();
        for (var index = 0; index < fields.Length; index++)
        {
            if (values[index] is { } value)
            {
                document[fields[index].Field] = value;
            }
        }
        return document;
    }

    // ---- match settlement ---------------------------------------------------

    /// <summary>Claims one match; a concurrent claimant waits on the unique key and then sees the stored response.</summary>
    internal static async ValueTask<bool> TryBeginRelationalMatchSettlementAsync(this IUnitOfWork w, string matchKey) =>
        await w.ExecuteAsync("""
            INSERT INTO ultimateteam.match_settlements(club_id, match_key, response) VALUES(@club, @key, NULL)
            ON CONFLICT (club_id, match_key) DO NOTHING
            """, ("key", matchKey)) == 1;

    /// <summary>The stored response is the schema's explicit idempotency exception: replay returns it unchanged.</summary>
    internal static async ValueTask<JsonObject?> RelationalMatchSettlementAsync(this IUnitOfWork w, string matchKey) =>
        (await w.QueryDocumentsAsync("""
            SELECT response FROM ultimateteam.match_settlements
            WHERE club_id = @club AND match_key = @key AND response IS NOT NULL
            """, ("key", matchKey))).FirstOrDefault();

    /// <summary>
    /// The newest stored response for <paramref name="matchKey"/> or a later match that reused its id
    /// (<c>{matchKey}@{created}</c>): a replay of that later match's end returns its own response.
    /// </summary>
    internal static async ValueTask<JsonObject?> LatestRelationalMatchSettlementAsync(this IUnitOfWork w, string matchKey) =>
        (await w.QueryDocumentsAsync("""
            SELECT response FROM ultimateteam.match_settlements
            WHERE club_id = @club AND (match_key = @key OR left(match_key, length(@key) + 1) = @key || '@') AND response IS NOT NULL
            ORDER BY id DESC LIMIT 1
            """, ("key", matchKey))).FirstOrDefault();

    internal static ValueTask<int> CompleteRelationalMatchSettlementAsync(this IUnitOfWork w, string matchKey, JsonObject response) =>
        w.ExecuteAsync("UPDATE ultimateteam.match_settlements SET response = @response WHERE club_id = @club AND match_key = @key",
            ("key", matchKey), ("response", Json(response)));
}
