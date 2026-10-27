using System.Diagnostics;
using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;

namespace Aurora21.UltimateTeam.Tests.Performance;

/// <summary>Disposable PostgreSQL projection backfill and item-trigger cost probe. No live club connection is accepted.</summary>
public static class ProjectionBenchmarkScenario
{
    public static async Task<JsonObject> RunAsync(int clubs, int cardsPerClub, int samples, string revision,
        Action<string>? progress = null, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clubs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(clubs, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(cardsPerClub, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cardsPerClub, 5000);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(samples, 100);

        var connectionString = PostgresTestCluster.CreateDatabase();
        try
        {
            await using var source = NpgsqlDataSource.Create(connectionString);
            await RelationalSchemaBootstrap.InstallFreshAsync(source, token);
            async Task<long> Scalar(string sql)
            {
                await using var command = source.CreateCommand(sql);
                command.CommandTimeout = 600;
                return Convert.ToInt64(await command.ExecuteScalarAsync(token));
            }
            async Task<double> Execute(string sql, params (string Name, object Value)[] parameters)
            {
                await using var command = source.CreateCommand(sql);
                command.CommandTimeout = 600;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
                var timer = Stopwatch.StartNew();
                await command.ExecuteNonQueryAsync(token);
                return timer.Elapsed.TotalMilliseconds;
            }

            var definition = await Scalar("""
                SELECT d.definition_id FROM ultimateteam.item_definitions d
                JOIN ultimateteam.player_card_definitions p USING(definition_id)
                WHERE d.family_id=1 ORDER BY d.definition_id LIMIT 1
                """);
            var clubsMs = await Execute("""
                INSERT INTO ultimateteam.clubs(persona_id,nucleus_id,credits,fifa_points,next_item_id,
                    sid,token,established,last_purchase_transaction_id,last_match_create_is_draft,last_draft_slot)
                SELECT 100000000+g,100000000+g,123456,0,@next,'synthetic','synthetic',1,0,false,0
                FROM generate_series(1,@clubs) g
                """, ("clubs", clubs), ("next", cardsPerClub + 1));
            // The fixture writes valid parent/child rows but skips row-by-row invariant checks while
            // generating two million records. This only changes the disposable database.
            await Execute("""
                ALTER TABLE ultimateteam.items DISABLE TRIGGER USER;
                ALTER TABLE ultimateteam.player_items DISABLE TRIGGER USER
                """);
            progress?.Invoke($"Seeding {clubs:N0} synthetic clubs with {cardsPerClub:N0} player cards each");
            var itemsMs = await Execute("""
                INSERT INTO ultimateteam.items(club_id,native_item_id,definition_id,family_id,timestamp,
                    pile,item_state,untradeable,owners,last_sale_price)
                SELECT c.id,g,@definition,1,1700000000,'club','free',false,1,0
                FROM ultimateteam.clubs c CROSS JOIN generate_series(1,@cards) g
                """, ("definition", definition), ("cards", cardsPerClub));
            var childrenMs = await Execute("""
                INSERT INTO ultimateteam.player_items(item_id,contracts,fitness,injury_games,suspension,
                    loan_games,morale,training,assists,lifetime_assists,duplicate_acknowledged)
                SELECT id,7,99,0,0,0,0,0,0,0,false FROM ultimateteam.items
                """);
            await Execute("""
                ALTER TABLE ultimateteam.items ENABLE TRIGGER USER;
                ALTER TABLE ultimateteam.player_items ENABLE TRIGGER USER;
                ANALYZE ultimateteam.items;
                ANALYZE ultimateteam.player_items
                """);
            var expected = (long)clubs * cardsPerClub;
            if (await Scalar("SELECT count(*) FROM ultimateteam.items") != expected ||
                await Scalar("SELECT count(*) FROM ultimateteam.player_items") != expected ||
                await Scalar("SELECT count(*) FROM ultimateteam.items i LEFT JOIN ultimateteam.player_items p ON p.item_id=i.id WHERE p.item_id IS NULL") != 0)
                throw new InvalidOperationException("Synthetic club fixture is incomplete");
            var bytesBefore = await Scalar("SELECT pg_database_size(current_database())");
            progress?.Invoke($"Backfilling the current player-search projection for {expected:N0} owned players");
            var backfillMs = await Execute("""
                INSERT INTO ultimateteam.club_player_search
                    (item_id,club_id,native_item_id,pile,rating,native_rareflag,resource_id)
                SELECT i.id,i.club_id,i.native_item_id,i.pile,p.rating,r.native_rareflag,
                    COALESCE(d.native_resource_id,d.native_asset_id,0)
                FROM ultimateteam.items i
                JOIN ultimateteam.item_definitions d ON d.definition_id=i.definition_id
                JOIN ultimateteam.player_card_definitions p ON p.definition_id=i.definition_id
                JOIN ultimateteam.card_types ct ON ct.card_type_id=p.card_type_id
                JOIN ultimateteam.native_rarities r ON r.native_rarity_id=ct.native_rarity_id
                WHERE i.family_id=1
                """);
            if (await Scalar("SELECT count(*) FROM ultimateteam.club_player_search") != expected ||
                await Scalar("SELECT count(*) FROM ultimateteam.items") != expected)
                throw new InvalidOperationException("Projection backfill did not preserve all synthetic player items");
            var bytesAfter = await Scalar("SELECT pg_database_size(current_database())");
            var projectionBytes = await Scalar("SELECT pg_total_relation_size('ultimateteam.club_player_search')");
            var club = await Scalar("SELECT min(id) FROM ultimateteam.clubs");

            // Same database, same SQL and row count. Each trial rolls back. The only difference is
            // whether the projection's insert/move trigger or delete cascade runs. DDL and rollback
            // are outside the timer; the other item triggers remain the same in each pair.
            const int batch = 200;
            const string insert = """
                INSERT INTO ultimateteam.items(club_id,native_item_id,definition_id,family_id,timestamp,
                    pile,item_state,untradeable,owners,last_sale_price)
                SELECT @club,10000000+g,@definition,1,1700000000,'club','free',false,1,0
                FROM generate_series(1,@batch) g
                """;
            const string move = """
                UPDATE ultimateteam.items SET pile='trade'
                WHERE club_id=@club AND native_item_id BETWEEN 1 AND @batch
                """;
            const string deleteSql = """
                DELETE FROM ultimateteam.items
                WHERE club_id=@club AND native_item_id BETWEEN 1 AND @batch
                """;
            async Task<double> Trial(string sql, bool projectionEnabled)
            {
                await using var connection = await source.OpenConnectionAsync(token);
                await using var transaction = await connection.BeginTransactionAsync(token);
                if (!projectionEnabled)
                {
                    var disableSql = sql == deleteSql
                        ? "ALTER TABLE ultimateteam.club_player_search DROP CONSTRAINT club_player_search_item_id_fkey"
                        : "ALTER TABLE ultimateteam.items DISABLE TRIGGER lfv4_trg_club_player_search_items";
                    await using var disable = new NpgsqlCommand(disableSql, connection, transaction);
                    await disable.ExecuteNonQueryAsync(token);
                }
                await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
                command.Parameters.AddWithValue("club", club);
                command.Parameters.AddWithValue("definition", definition);
                command.Parameters.AddWithValue("batch", batch);
                var watch = Stopwatch.StartNew();
                await command.ExecuteNonQueryAsync(token);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                await transaction.RollbackAsync(token);
                return elapsed;
            }
            async Task<(List<double> Enabled, List<double> Disabled)> Measure(string sql)
            {
                for (var warmup = 0; warmup < 2; warmup++) { _ = await Trial(sql, false); _ = await Trial(sql, true); }
                var enabled = new List<double>();
                var disabled = new List<double>();
                for (var sample = 0; sample < samples; sample++)
                {
                    if (sample % 2 == 0) { disabled.Add(await Trial(sql, false)); enabled.Add(await Trial(sql, true)); }
                    else { enabled.Add(await Trial(sql, true)); disabled.Add(await Trial(sql, false)); }
                }
                return (enabled, disabled);
            }
            var insertTimes = await Measure(insert);
            var moveTimes = await Measure(move);
            var deleteTimes = await Measure(deleteSql);
            if (await Scalar("SELECT count(*) FROM ultimateteam.club_player_search") != expected ||
                await Scalar("SELECT count(*) FROM ultimateteam.items") != expected ||
                await Scalar("SELECT count(*) FROM pg_trigger WHERE tgrelid='ultimateteam.items'::regclass AND tgname='lfv4_trg_club_player_search_items' AND tgenabled='O'") != 1)
                throw new InvalidOperationException("Rollback changed the synthetic club fixture or trigger state");
            progress?.Invoke($"Projection backfill {backfillMs:N0} ms; item insert {insertTimes.Enabled.Average():F2}/{insertTimes.Disabled.Average():F2} ms, move {moveTimes.Enabled.Average():F2}/{moveTimes.Disabled.Average():F2} ms, delete {deleteTimes.Enabled.Average():F2}/{deleteTimes.Disabled.Average():F2} ms per {batch} cards (projection on/off)");
            return new JsonObject
            {
                ["passed"] = true,
                ["recordedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["revision"] = revision,
                ["fixture"] = "Synthetic valid owned-player rows in a throwaway PostgreSQL cluster; invariant triggers skipped during bulk seeding only",
                ["clubs"] = clubs,
                ["cardsPerClub"] = cardsPerClub,
                ["ownedPlayers"] = expected,
                ["seedClubsMs"] = clubsMs,
                ["seedItemsMs"] = itemsMs,
                ["seedPlayerChildrenMs"] = childrenMs,
                ["databaseBytesBeforeBackfill"] = bytesBefore,
                ["databaseBytesAfterBackfill"] = bytesAfter,
                ["projectionTotalBytes"] = projectionBytes,
                ["projectionBackfillMs"] = backfillMs,
                ["writeTrialRows"] = batch,
                ["writeTrialSamplesPerMode"] = samples,
                ["insertTriggerEnabledMeanMs"] = insertTimes.Enabled.Average(),
                ["insertTriggerDisabledMeanMs"] = insertTimes.Disabled.Average(),
                ["insertTriggerDeltaMsPerCard"] = (insertTimes.Enabled.Average() - insertTimes.Disabled.Average()) / batch,
                ["insertTriggerEnabledSamplesMs"] = new JsonArray(insertTimes.Enabled.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["insertTriggerDisabledSamplesMs"] = new JsonArray(insertTimes.Disabled.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["moveTriggerEnabledMeanMs"] = moveTimes.Enabled.Average(),
                ["moveTriggerDisabledMeanMs"] = moveTimes.Disabled.Average(),
                ["moveTriggerDeltaMsPerCard"] = (moveTimes.Enabled.Average() - moveTimes.Disabled.Average()) / batch,
                ["moveTriggerEnabledSamplesMs"] = new JsonArray(moveTimes.Enabled.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["moveTriggerDisabledSamplesMs"] = new JsonArray(moveTimes.Disabled.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["deleteCascadeEnabledMeanMs"] = deleteTimes.Enabled.Average(),
                ["deleteCascadeDisabledMeanMs"] = deleteTimes.Disabled.Average(),
                ["deleteCascadeDeltaMsPerCard"] = (deleteTimes.Enabled.Average() - deleteTimes.Disabled.Average()) / batch,
                ["deleteCascadeEnabledSamplesMs"] = new JsonArray(deleteTimes.Enabled.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["deleteCascadeDisabledSamplesMs"] = new JsonArray(deleteTimes.Disabled.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            };
        }
        finally
        {
            PostgresTestCluster.DropDatabase(connectionString);
        }
    }
}
