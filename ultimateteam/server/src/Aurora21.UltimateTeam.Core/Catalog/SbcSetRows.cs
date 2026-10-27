using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>
/// One operator-edited SBC set (migration 136, content area <c>sbc_set</c>): the set's sbc.json definition and its
/// creation order. A row replaces the bundled set with the same key, or adds a new set to the catalogue.
/// </summary>
public sealed record SbcSetRow(string Key, JsonObject Definition, long Ordinal);

/// <summary>One set on the live list (migration 142, content area <c>sbc_live</c>): the game shows it while it is switched on.</summary>
public readonly record struct SbcLiveEntry(string Key, bool Enabled);

/// <summary>The SBC content the database holds: the panel's set edits and the live list.</summary>
public sealed record SbcContentRows(IReadOnlyList<SbcSetRow> Sets, IReadOnlyList<SbcLiveEntry> Live)
{
    public static SbcContentRows Empty { get; } = new([], []);

    public static async ValueTask<SbcContentRows> ReadAsync(IUnitOfWork work) =>
        new(await SbcSetRows.ReadAsync(work), await SbcLiveRows.ReadAsync(work));
}

public static class SbcSetRows
{
    public static async ValueTask<IReadOnlyList<SbcSetRow>> ReadAsync(IUnitOfWork work) =>
        await work.QueryAsync("SELECT set_key, definition::text, ordinal FROM ultimateteam.sbc_sets ORDER BY ordinal",
            row => new SbcSetRow(row.GetString(0), (JsonObject)JsonNode.Parse(row.GetString(1))!, row.GetInt64(2)));

    public static async ValueTask<SbcSetRow?> ReadAsync(IUnitOfWork work, string key) =>
        (await work.QueryAsync("SELECT set_key, definition::text, ordinal FROM ultimateteam.sbc_sets WHERE set_key = @key",
            row => new SbcSetRow(row.GetString(0), (JsonObject)JsonNode.Parse(row.GetString(1))!, row.GetInt64(2)),
            ("key", key))).SingleOrDefault();

    public static ValueTask<int> WriteAsync(IUnitOfWork work, string key, JsonObject definition) =>
        work.ExecuteAsync("""
            INSERT INTO ultimateteam.sbc_sets(set_key, definition) VALUES(@key, @definition::jsonb)
            ON CONFLICT(set_key) DO UPDATE SET definition = excluded.definition, updated_at = now()
            """, ("key", key), ("definition", definition.ToJsonString()));

    public static ValueTask<int> DeleteAsync(IUnitOfWork work, string key) =>
        work.ExecuteAsync("DELETE FROM ultimateteam.sbc_sets WHERE set_key = @key", ("key", key));
}

public static class SbcLiveRows
{
    public static async ValueTask<IReadOnlyList<SbcLiveEntry>> ReadAsync(IUnitOfWork work) =>
        await work.QueryAsync("SELECT set_key, enabled FROM ultimateteam.sbc_live ORDER BY set_key",
            row => new SbcLiveEntry(row.GetString(0), row.GetBoolean(1)));

    /// <summary>Replaces the live list.</summary>
    public static async ValueTask WriteAsync(IUnitOfWork work, IReadOnlyList<SbcLiveEntry> live)
    {
        await work.ExecuteAsync("DELETE FROM ultimateteam.sbc_live WHERE NOT (set_key = ANY(@keys))", ("keys", live.Select(entry => entry.Key).ToArray()));
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.sbc_live(set_key, enabled) SELECT * FROM unnest(@keys, @enabled)
            ON CONFLICT(set_key) DO UPDATE SET enabled = excluded.enabled,
                updated_at = CASE WHEN ultimateteam.sbc_live.enabled = excluded.enabled THEN ultimateteam.sbc_live.updated_at ELSE now() END
            """, ("keys", live.Select(entry => entry.Key).ToArray()), ("enabled", live.Select(entry => entry.Enabled).ToArray()));
    }
}
