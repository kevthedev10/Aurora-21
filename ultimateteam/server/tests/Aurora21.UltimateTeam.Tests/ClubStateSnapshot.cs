using Npgsql;
using Aurora21.UltimateTeam.Core.Data;

namespace Aurora21.UltimateTeam.Tests;

/// <summary>
/// Every committed row a club owns (its account row, each club-owned table and the market listings it
/// sells or bids on), read on a separate connection so an uncommitted transaction cannot hide a change.
/// </summary>
public static class ClubStateSnapshot
{
    public static string Capture(string connectionString, long clubId)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        var parts = new List<string>
        {
            "clubs=" + Rows(connection, "ultimateteam.clubs", "id = @club", clubId),
            "market_listings=" + Rows(connection, "ultimateteam.market_listings", "seller_club_id = @club OR bidder_club_id = @club", clubId),
        };
        using var tables = new NpgsqlCommand("""
            SELECT DISTINCT table_name FROM information_schema.columns
            WHERE table_schema='ultimateteam' AND column_name='club_id' AND table_name NOT IN ('clubs','market_listings')
            ORDER BY table_name
            """, connection);
        using var reader = tables.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        reader.Close();
        parts.AddRange(names.Select(table => table + "=" + Rows(connection, "ultimateteam." + table, "club_id = @club", clubId)));
        return string.Join('\n', parts);
    }

    /// <summary>
    /// A table a later migration introduces does not exist on an older schema. Recording it as absent
    /// keeps the snapshot comparable across an attempted upgrade without hiding a change: a table that
    /// gains rows, or appears where it was absent, still alters the snapshot.
    /// </summary>
    private const string AbsentTable = "<absent>";

    private static string Rows(NpgsqlConnection connection, string table, string filter, long clubId)
    {
        if (!TableExists(connection, table))
        {
            return AbsentTable;
        }
        using var command = new NpgsqlCommand(
            $"SELECT COALESCE(json_agg(row_to_json(t)::text ORDER BY row_to_json(t)::text), '[]')::text FROM {table} t WHERE {filter}",
            connection);
        command.Parameters.AddWithValue("club", clubId);
        return (string)command.ExecuteScalar()!;
    }

    private static bool TableExists(NpgsqlConnection connection, string table)
    {
        using var command = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection);
        command.Parameters.AddWithValue("table", table);
        return (bool)command.ExecuteScalar()!;
    }
}
