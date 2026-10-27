using Aurora21.UltimateTeam.Core.Data;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class StoreTradeabilityUpgradeTests
{
    [Fact]
    public async Task UpgradePreservesInheritedGlobalPackAndExplicitListingChoices()
    {
        var connectionString = PostgresTestCluster.CreateDatabase();
        try
        {
            await using var source = NpgsqlDataSource.Create(connectionString);
            await Migration140Database.InstallAsync(source);
            await RelationalSchemaBootstrap.InstallFreshAsync(source, async (file, connection, transaction, token) =>
            {
                if (file != "migrations/148-token-store-lineup.sql") return;
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO ultimateteam.content_rules(file,path,value,ordinal)
                    VALUES('tradeability','["sources","store"]','true',99900),
                          ('tradeability','["packs","74"]','false',99901),
                          ('tradeability','["sourcePacks","store","74"]','true',99902),
                          ('tradeability','["packs","75"]','false',99903)
                    ON CONFLICT(file,path) DO UPDATE SET value=EXCLUDED.value;
                    UPDATE ultimateteam.store_packs SET store_untradeable=NULL WHERE pack_id IN(74,75,76);
                    UPDATE ultimateteam.store_packs SET store_untradeable=false WHERE pack_id=77;
                    INSERT INTO ultimateteam.token_store_listings(pack_id,offer_id,price,enabled,untradeable)
                    VALUES(74,1074,25,true,NULL),(75,1075,25,true,NULL),(76,1076,25,true,NULL),(77,1077,25,true,false)
                    ON CONFLICT(pack_id) DO UPDATE SET untradeable=EXCLUDED.untradeable;
                    """, connection, transaction);
                await seed.ExecuteNonQueryAsync(token);
            }, CancellationToken.None);
            await using var check = source.CreateCommand("""
                SELECT p.pack_id,p.store_untradeable,t.untradeable
                FROM ultimateteam.store_packs p JOIN ultimateteam.token_store_listings t USING(pack_id)
                WHERE p.pack_id IN(74,75,76,77) ORDER BY p.pack_id
                """);
            await using var rows = await check.ExecuteReaderAsync();
            foreach (var expected in new[] { true, false, true, false })
            {
                Assert.True(await rows.ReadAsync());
                Assert.Equal(expected, rows.GetBoolean(1));
                Assert.Equal(expected, rows.GetBoolean(2));
            }
            Assert.False(await rows.ReadAsync());
        }
        finally { PostgresTestCluster.DropDatabase(connectionString); }
    }
}
