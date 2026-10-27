using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>Current pack, bucket and quick-sell operator settings survive runtime restart.</summary>
public sealed class PackOddsPersistenceTests
{
    private const string Weights = """
        SELECT b.code, b.draw_weight::text FROM ultimateteam.promo_buckets b JOIN ultimateteam.promos p USING (promo_id)
        WHERE p.code = 'gold_rare' ORDER BY b.code
        """;

    private static async Task<Dictionary<string, string>> ReadAsync(NpgsqlDataSource source, string sql)
    {
        var result = new Dictionary<string, string>();
        await using var command = source.CreateCommand(sql);
        await using var rows = await command.ExecuteReaderAsync();
        while (await rows.ReadAsync()) result[rows.GetValue(0).ToString()!] = rows.GetString(1);
        return result;
    }

    [Fact]
    public async Task RestartKeepsOperatorOddsAndQuickSellEdits()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);

        await using (var prepare = source.CreateCommand("""
            SELECT set_config('ultimateteam.content_publish', 'on', true);
            UPDATE ultimateteam.promo_buckets b SET draw_weight = CASE b.code WHEN 'rating_86_87' THEN 0.35 ELSE 0.2 END
            FROM ultimateteam.promos p WHERE p.promo_id = b.promo_id AND p.code = 'gold_rare' AND b.code IN ('rating_86_87', 'rating_88_89');
            UPDATE ultimateteam.store_packs SET category_chances = CASE pack_id WHEN 74 THEN '{"totw": 0.02}'::jsonb ELSE '{}'::jsonb END
            WHERE pack_id IN (74, 75);
            UPDATE ultimateteam.content_rules SET value = '90' WHERE file = 'cards' AND path = '["quickSell","tiers","silver","rareMaximum"]';
            SELECT set_config('ultimateteam.content_publish', '', true);
            """)) await prepare.ExecuteNonQueryAsync();
        using var reopened = TestRuntime.Create(connectionString: rt.Detach(), marketTarget: 0);

        var weights = await ReadAsync(source, Weights);
        Assert.Equal(0.35m, decimal.Parse(weights["rating_86_87"]));
        Assert.Equal(0.2m, decimal.Parse(weights["rating_88_89"]));
        var chances = await ReadAsync(source, "SELECT pack_id, category_chances::text FROM ultimateteam.store_packs WHERE pack_id IN (74,75)");
        Assert.Equal("{\"totw\": 0.02}", chances["74"]);
        Assert.Equal("{}", chances["75"]);
        await using (var cap = source.CreateCommand("SELECT value::text FROM ultimateteam.content_rules WHERE file='cards' AND path='[\"quickSell\",\"tiers\",\"silver\",\"rareMaximum\"]'"))
            Assert.Equal("90", await cap.ExecuteScalarAsync());
    }
}
