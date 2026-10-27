using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data.Records;
using static Aurora21.UltimateTeam.Core.Data.ClubSql;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>Club-scoped relational aggregates. No source documents or per-item child queries.</summary>
public static class RelationalItemRepository
{
    // ultimateteam.item_families, seeded by database-handoff/normalized-seeds/010-sources-and-families.json.
    // Each family owns exactly one state table; RelationalItemWireMapper dispatches on the same numbers.
    private const short PlayerFamily = 1;
    private const short ManagerFamily = 2;
    private const short ConsumableFamily = 3;
    private const short CustomisationFamily = 4;
    private const short PlayerPickFamily = 5;

    internal static RelationalItemHeader ReadRelationalItemHeader(NpgsqlDataReader r, int o = 0) => new(new ItemId(r.GetInt64(o)), r.GetInt64(o+1), new NativeItemId(r.GetInt64(o+2)), new DefinitionId(r.GetInt64(o+3)), r.GetInt16(o+4), r.GetInt64(o+5), r.IsDBNull(o+6) ? null : r.GetString(o+6), r.IsDBNull(o+7) ? null : r.GetString(o+7), r.GetBoolean(o+8), r.GetInt64(o+9), r.GetInt64(o+10), r.IsDBNull(o+11) ? null : r.GetString(o+11));

    internal static PlayerItemState ReadPlayerItemState(NpgsqlDataReader r, int o = 0) => new(new ItemId(r.GetInt64(o)), r.GetInt64(o+1), r.GetInt64(o+2), r.IsDBNull(o+3) ? null : r.GetString(o+3), r.GetInt64(o+4), r.GetInt64(o+5), r.GetInt64(o+6), r.GetInt64(o+7), r.GetInt64(o+8), r.IsDBNull(o+9) ? null : r.GetString(o+9), r.IsDBNull(o+10) ? null : new DefinitionId(r.GetInt64(o+10)), r.IsDBNull(o+11) ? null : r.GetInt64(o+11), r.GetInt64(o+12), r.GetInt64(o+13), r.IsDBNull(o+14) ? null : r.GetInt64(o+14), r.GetBoolean(o+15));



    internal static ManagerItemState ReadManagerItemState(NpgsqlDataReader r, int o = 0) => new(new ItemId(r.GetInt64(o)), r.GetInt64(o+1), r.IsDBNull(o+2) ? null : r.GetInt64(o+2));

    internal static ConsumableItemState ReadConsumableItemState(NpgsqlDataReader r, int o = 0) => new(new ItemId(r.GetInt64(o)), r.GetInt64(o+1), r.GetInt64(o+2));

    internal static CustomisationItemState ReadCustomisationItemState(NpgsqlDataReader r, int o = 0) => new(new ItemId(r.GetInt64(o)), r.IsDBNull(o+1) ? null : r.GetBoolean(o+1));

    internal static PlayerPickItemState ReadPlayerPickItemState(NpgsqlDataReader r, int o = 0) => new(new ItemId(r.GetInt64(o)), r.IsDBNull(o+1) ? null : new DefinitionId(r.GetInt64(o+1)), r.GetInt64(o+2), r.GetInt64(o+3), r.GetInt64(o+4), r.GetString(o+5), r.IsDBNull(o+6) ? null : r.GetInt64(o+6), r.GetBoolean(o+7), r.GetFieldValue<long[]>(o+8), r.IsDBNull(o+9) ? null : r.GetString(o+9), r.GetInt32(o+10), r.IsDBNull(o+11) ? null : r.GetString(o+11), r.IsDBNull(o+12) ? null : r.GetString(o+12));

    internal static ItemExtensionState ReadItemExtensionState(NpgsqlDataReader r, int o = 0) => new(new ItemId(r.GetInt64(o)), r.GetInt64(o+1), new CatalogJson(r.GetString(o+2)), r.IsDBNull(o+3) ? null : new CatalogJson(r.GetString(o+3)));

    internal static UnopenedPackRecord ReadUnopenedPackRecord(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4));

    internal static PlayerPickChoiceRecord ReadPlayerPickChoiceRecord(NpgsqlDataReader r) => new(r.GetInt64(0), new ItemId(r.GetInt64(1)), r.GetInt32(2), new DefinitionId(r.GetInt64(3)), new NativeItemId(r.GetInt64(4)), r.GetInt64(5), r.GetBoolean(6));

    public static async ValueTask<List<RelationalItemAggregate>> ListRelationalItemsAsync(this IUnitOfWork work,
        string? pile = null, NativeItemId[]? nativeIds = null)
    {
        // The runner uses read committed. Hold a shared club lock across all child queries:
        // cooperating writes lock this same club FOR UPDATE before changing any aggregate.
        // Concurrent readers remain compatible; no partly old/partly new instance can be returned.
        if (!work.InTransaction) throw new InvalidOperationException("Inventory reads require a root transaction.");
        if (!await work.EnsureRelationalClubLockAsync(exclusive: false))
            throw new InvalidOperationException("Relational club does not exist.");
        // Headers, player state and extensions come from one statement: player_items and item_extensions
        // are keyed by item_id, so the LEFT JOINs add at most one row each and every item stays one row.
        // The rarer family tables below remain separate lookups. An earlier six-way merge was reverted
        // on measurements taken while workstation GC paused the benchmark for over half its run; at
        // 1,000 clubs PostgreSQL CPU per request now limits throughput (measured 2026-09-24).
        const int PlayerColumn = 12, ExtensionColumn = 29;
        var playerStats = new Dictionary<ItemId, IReadOnlyList<PlayerStatValue>>();
        var playerMap = new Dictionary<ItemId, PlayerItemState>();
        var extensionMap = new Dictionary<ItemId, ItemExtensionState>();
        var headers = await work.QueryAsync("""
            -- query: inventory.headers
            SELECT i.id, i.club_id, i.native_item_id, i.definition_id, i.family_id, i.timestamp, i.pile, i.item_state,
                i.untradeable, i.owners, i.last_sale_price, i.reward_source,
                p.item_id, p.contracts, p.fitness, p.injury_type, p.injury_games, p.suspension, p.loan_games, p.morale,
                p.training, p.position_override, p.style_definition_id, p.native_play_style, p.assists, p.lifetime_assists,
                p.loyalty_bonus, p.duplicate_acknowledged, p.match_stats::text,
                e.item_id, e.schema_revision, e.payload, e.wire_representation
            FROM ultimateteam.items i
            LEFT JOIN ultimateteam.player_items p ON p.item_id = i.id
            LEFT JOIN ultimateteam.item_extensions e ON e.item_id = i.id
            WHERE i.club_id = @club
            """ + (pile is null ? "" : " AND i.pile = @pile") + (nativeIds is null ? "" : " AND i.native_item_id = ANY(@native)")
            + " ORDER BY i.native_item_id", r =>
            {
                var header = ReadRelationalItemHeader(r);
                if (!r.IsDBNull(PlayerColumn))
                {
                    var state = ReadPlayerItemState(r, PlayerColumn);
                    playerMap[state.ItemId] = state;
                    playerStats[state.ItemId] = PlayerMatchStatsJson.Decode(r.GetString(PlayerColumn + 16), state.ItemId);
                }
                if (!r.IsDBNull(ExtensionColumn))
                {
                    var extension = ReadItemExtensionState(r, ExtensionColumn);
                    extensionMap[extension.ItemId] = extension;
                }
                return header;
            },
            ("pile", pile), ("native", nativeIds?.Select(i => i.Value).ToArray()));
        if (headers.Count == 0) return new();
        // The headers are authoritative about each item's family, and items.family_id is constrained against
        // the definition's family on every write. Each family table is asked only about its own items (a
        // club's few managers need not be probed with every player id), and not at all when it has none.
        var byFamily = headers.GroupBy(h => h.FamilyId).ToDictionary(g => g.Key, g => g.Select(h => h.Id.Value).ToArray());
        var manager = byFamily.TryGetValue(ManagerFamily, out var managerIds) ? await work.QueryAsync("-- query: inventory.manager-state\nSELECT item_id, contracts, league_id FROM ultimateteam.manager_items WHERE item_id = ANY(@ids) ORDER BY item_id", r => ReadManagerItemState(r), ("ids", managerIds)) : new();
        var managerMap = manager.ToDictionary(s => s.ItemId);
        var consumable = byFamily.TryGetValue(ConsumableFamily, out var consumableIds) ? await work.QueryAsync("-- query: inventory.consumable-state\nSELECT item_id, quantity, untradeable_quantity FROM ultimateteam.consumable_items WHERE item_id = ANY(@ids) ORDER BY item_id", r => ReadConsumableItemState(r), ("ids", consumableIds)) : new();
        var consumableMap = consumable.ToDictionary(s => s.ItemId);
        var customisation = byFamily.TryGetValue(CustomisationFamily, out var customisationIds) ? await work.QueryAsync("-- query: inventory.customisation-state\nSELECT item_id, is_locked_override FROM ultimateteam.customisation_items WHERE item_id = ANY(@ids) ORDER BY item_id", r => ReadCustomisationItemState(r), ("ids", customisationIds)) : new();
        var customisationMap = customisation.ToDictionary(s => s.ItemId);
        var pick = byFamily.TryGetValue(PlayerPickFamily, out var pickIds) ? await work.QueryAsync("-- query: inventory.player-pick-state\nSELECT item_id, preview_definition_id, min_rating, choice_count, source_pack_id, state, selected_choice_id, promo_only, rareflags, label, max_rating, resource_pool, draw_config::text FROM ultimateteam.player_pick_items WHERE item_id = ANY(@ids) ORDER BY item_id", r => ReadPlayerPickItemState(r), ("ids", pickIds)) : new();
        var pickMap = pick.ToDictionary(s => s.ItemId);
        return headers.Select(h => new RelationalItemAggregate(h, playerMap.GetValueOrDefault(h.Id),
            managerMap.GetValueOrDefault(h.Id), consumableMap.GetValueOrDefault(h.Id),
            customisationMap.GetValueOrDefault(h.Id), pickMap.GetValueOrDefault(h.Id),
            extensionMap.GetValueOrDefault(h.Id), playerStats.GetValueOrDefault(h.Id) ?? Array.Empty<PlayerStatValue>())).ToList();
    }

    public static async ValueTask<RelationalItemAggregate?> GetRelationalItemAsync(this IUnitOfWork work, NativeItemId nativeId) =>
        (await work.ListRelationalItemsAsync(nativeIds: new[] { nativeId })).SingleOrDefault();

    /// <summary>
    /// Owned player identities, club pile before squad pile. <paramref name="resourceIds"/> narrows the scan to
    /// the resources a caller actually asks about; null keeps the whole club. The order is part of the contract:
    /// callers index it with TryAdd to resolve the first owned card of a resource. <paramref name="permanentOnly"/> leaves
    /// out loan copies (matches left or expired): retail lets a club own a card next to its loan, so a loan is never the
    /// copy a bought or packed card duplicates.
    /// </summary>
    public static ValueTask<List<RelationalOwnedPlayerIdentity>> RelationalOwnedPlayerIdentitiesAsync(this IUnitOfWork work,
        long[]? resourceIds = null, bool permanentOnly = false) =>
        work.QueryAsync("""
            -- query: inventory.owned-player-identities
            SELECT i.definition_id, p.footballer_id, d.native_resource_id, i.id, i.native_item_id
            FROM ultimateteam.items i JOIN ultimateteam.player_card_definitions p ON p.definition_id = i.definition_id
            JOIN ultimateteam.item_definitions d ON d.definition_id = i.definition_id
            LEFT JOIN ultimateteam.player_items pi ON pi.item_id = i.id
            LEFT JOIN ultimateteam.item_extensions e ON e.item_id = i.id
            WHERE i.club_id = @club AND i.family_id = 1 AND i.pile IN ('club', 'squad')
                AND (@allResources OR d.native_resource_id = ANY(@resources))
                AND (NOT @permanentOnly OR (COALESCE(pi.loan_games, 0) = 0 AND COALESCE(e.payload->>'expiredLoan', 'false') <> 'true'))
            ORDER BY CASE WHEN i.pile = 'club' THEN 0 ELSE 1 END, i.native_item_id
            """, r => new RelationalOwnedPlayerIdentity(new DefinitionId(r.GetInt64(0)), new FootballerId(r.GetInt64(1)),
                r.IsDBNull(2) ? null : new NativeResourceId(r.GetInt64(2)), new ItemId(r.GetInt64(3)), new NativeItemId(r.GetInt64(4))),
            ("allResources", resourceIds is null), ("resources", resourceIds ?? Array.Empty<long>()), ("permanentOnly", permanentOnly));

    /// <summary>Insert/update complete aggregates in a fixed query budget. Identity changes are rejected.</summary>
    public static async ValueTask<List<RelationalItemHeader>> SaveRelationalItemsAsync(this IUnitOfWork work,
        IReadOnlyList<RelationalItemAggregate> items)
    {
        if (items.Count == 0) return new();
        foreach (var item in items) Validate(item, work.ClubId);
        if (items.Select(i => i.Header.NativeItemId).Distinct().Count() != items.Count)
            throw new ArgumentException("Duplicate native IDs in item batch.", nameof(items));
        await work.LockRelationalClubAsync();
        // Diagnose family mismatches before writing; the package also checks this
        // relation at commit, including direct SQL callers.
        var invalidDefinitions = await work.ScalarAsync<long>("""
            SELECT count(*) FROM json_to_recordset(@rows) AS x(definition_id bigint, family_id smallint)
            LEFT JOIN ultimateteam.item_definitions d ON d.definition_id=x.definition_id
            WHERE d.definition_id IS NULL OR d.family_id <> x.family_id
            """, ("rows", Rows(items.Select(i => new { definition_id = i.Header.DefinitionId.Value, family_id = i.Header.FamilyId }))));
        if (invalidDefinitions != 0) throw new ArgumentException("Item definition is missing or belongs to another family.", nameof(items));
        var headers = await work.QueryAsync("""
            INSERT INTO ultimateteam.items(club_id, native_item_id, definition_id, family_id, timestamp, pile, item_state, untradeable, owners, last_sale_price, reward_source)
            SELECT @club, native_item_id, definition_id, family_id, timestamp, pile, item_state, untradeable, owners, last_sale_price, reward_source
            FROM json_to_recordset(@rows) AS x(native_item_id bigint, definition_id bigint, family_id smallint, timestamp bigint,
                pile text, item_state text, untradeable boolean, owners bigint, last_sale_price bigint, reward_source text)
            ON CONFLICT (club_id, native_item_id) DO UPDATE SET
                definition_id = excluded.definition_id, family_id = excluded.family_id, timestamp = excluded.timestamp,
                pile = excluded.pile, item_state = excluded.item_state, untradeable = excluded.untradeable,
                owners = excluded.owners, last_sale_price = excluded.last_sale_price, reward_source = excluded.reward_source
            RETURNING id, club_id, native_item_id, definition_id, family_id, timestamp, pile, item_state, untradeable, owners, last_sale_price, reward_source
            """, r => ReadRelationalItemHeader(r), ("rows", Rows(items.Select(i => new {
                native_item_id = i.Header.NativeItemId.Value, definition_id = i.Header.DefinitionId.Value,
                family_id = i.Header.FamilyId, timestamp = i.Header.Timestamp, pile = i.Header.Pile,
                item_state = i.Header.ItemState, untradeable = i.Header.Untradeable,
                owners = i.Header.Owners, last_sale_price = i.Header.LastSalePrice, reward_source = i.Header.RewardSource }))));
        var keys = headers.ToDictionary(h => h.NativeItemId, h => h.Id);
        foreach (var item in items)
            if (item.Header.Id.Value != 0 && item.Header.Id != keys[item.Header.NativeItemId])
                throw new InvalidOperationException("Internal item ID does not match its club-scoped native ID.");
        // Only the families present in this batch are written. A family's statement with no rows inserts and
        // updates nothing and fires no trigger (every trigger on these tables is row-level), so skipping it only
        // saves the round trip; the statement count stays bounded by the families present, never by the rows.
        if (items.Any(i => i.Player is not null))
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.player_items(item_id, contracts, fitness, injury_type, injury_games, suspension, loan_games, morale, training, position_override, style_definition_id, native_play_style, assists, lifetime_assists, loyalty_bonus, duplicate_acknowledged, match_stats)
            SELECT item_id, contracts, fitness, injury_type, injury_games, suspension, loan_games, morale, training, position_override, style_definition_id, native_play_style, assists, lifetime_assists, loyalty_bonus, duplicate_acknowledged, match_stats FROM json_to_recordset(@rows) AS x(item_id bigint, contracts bigint, fitness bigint, injury_type text, injury_games bigint, suspension bigint, loan_games bigint, morale bigint, training bigint, position_override text, style_definition_id bigint, native_play_style bigint, assists bigint, lifetime_assists bigint, loyalty_bonus bigint, duplicate_acknowledged boolean, match_stats jsonb)
            ON CONFLICT (item_id) DO UPDATE SET contracts = excluded.contracts, fitness = excluded.fitness, injury_type = excluded.injury_type, injury_games = excluded.injury_games, suspension = excluded.suspension, loan_games = excluded.loan_games, morale = excluded.morale, training = excluded.training, position_override = excluded.position_override, style_definition_id = excluded.style_definition_id, native_play_style = excluded.native_play_style, assists = excluded.assists, lifetime_assists = excluded.lifetime_assists, loyalty_bonus = excluded.loyalty_bonus, duplicate_acknowledged = excluded.duplicate_acknowledged, match_stats = excluded.match_stats
            """, ("rows", Rows(items.Where(i => i.Player is not null).Select(i => new { item_id = keys[i.Header.NativeItemId].Value, contracts = i.Player!.Contracts, fitness = i.Player!.Fitness, injury_type = i.Player!.InjuryType, injury_games = i.Player!.InjuryGames, suspension = i.Player!.Suspension, loan_games = i.Player!.LoanGames, morale = i.Player!.Morale, training = i.Player!.Training, position_override = i.Player!.PositionOverride, style_definition_id = i.Player!.StyleDefinitionId?.Value, native_play_style = i.Player!.NativePlayStyle, assists = i.Player!.Assists, lifetime_assists = i.Player!.LifetimeAssists, loyalty_bonus = i.Player!.LoyaltyBonus, duplicate_acknowledged = i.Player!.DuplicateAcknowledged, match_stats = PlayerMatchStatsJson.Encode(i.Stats) }))));
        if (items.Any(i => i.Manager is not null))
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.manager_items(item_id, contracts, league_id)
            SELECT item_id, contracts, league_id FROM json_to_recordset(@rows) AS x(item_id bigint, contracts bigint, league_id bigint)
            ON CONFLICT (item_id) DO UPDATE SET contracts = excluded.contracts, league_id = excluded.league_id
            """, ("rows", Rows(items.Where(i => i.Manager is not null).Select(i => new { item_id = keys[i.Header.NativeItemId].Value, contracts = i.Manager!.Contracts, league_id = i.Manager!.LeagueId }))));
        if (items.Any(i => i.Consumable is not null))
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.consumable_items(item_id, quantity, untradeable_quantity)
            SELECT item_id, quantity, untradeable_quantity FROM json_to_recordset(@rows) AS x(item_id bigint, quantity bigint, untradeable_quantity bigint)
            ON CONFLICT (item_id) DO UPDATE SET quantity = excluded.quantity, untradeable_quantity = excluded.untradeable_quantity
            """, ("rows", Rows(items.Where(i => i.Consumable is not null).Select(i => new { item_id = keys[i.Header.NativeItemId].Value, quantity = i.Consumable!.Quantity, untradeable_quantity = i.Consumable!.UntradeableQuantity }))));
        if (items.Any(i => i.Customisation is not null))
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.customisation_items(item_id, is_locked_override)
            SELECT item_id, is_locked_override FROM json_to_recordset(@rows) AS x(item_id bigint, is_locked_override boolean)
            ON CONFLICT (item_id) DO UPDATE SET is_locked_override = excluded.is_locked_override
            """, ("rows", Rows(items.Where(i => i.Customisation is not null).Select(i => new { item_id = keys[i.Header.NativeItemId].Value, is_locked_override = i.Customisation!.IsLockedOverride }))));
        if (items.Any(i => i.Pick is not null))
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.player_pick_items(item_id, preview_definition_id, min_rating, choice_count, source_pack_id, state, selected_choice_id, label)
            SELECT item_id, preview_definition_id, min_rating, choice_count, source_pack_id, state, selected_choice_id, label FROM json_to_recordset(@rows) AS x(item_id bigint, preview_definition_id bigint, min_rating bigint, choice_count bigint, source_pack_id bigint, state text, selected_choice_id bigint, label text)
            ON CONFLICT (item_id) DO UPDATE SET preview_definition_id = excluded.preview_definition_id, min_rating = excluded.min_rating, choice_count = excluded.choice_count, source_pack_id = excluded.source_pack_id, state = excluded.state, selected_choice_id = excluded.selected_choice_id, label = excluded.label
            """, ("rows", Rows(items.Where(i => i.Pick is not null).Select(i => new { item_id = keys[i.Header.NativeItemId].Value, preview_definition_id = i.Pick!.PreviewDefinitionId?.Value, min_rating = i.Pick!.MinRating, choice_count = i.Pick!.ChoiceCount, source_pack_id = i.Pick!.SourcePackId, state = i.Pick!.State, selected_choice_id = i.Pick!.SelectedChoiceId, label = i.Pick!.Label }))));
        if (items.Any(i => i.Extension is not null))
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.item_extensions(item_id, schema_revision, payload, wire_representation)
            SELECT item_id, schema_revision, payload, wire_representation FROM json_to_recordset(@rows) AS x(item_id bigint, schema_revision bigint, payload json, wire_representation json)
            ON CONFLICT (item_id) DO UPDATE SET schema_revision = excluded.schema_revision, payload = excluded.payload, wire_representation = excluded.wire_representation
            """, ("rows", Rows(items.Where(i => i.Extension is not null).Select(i => new { item_id = keys[i.Header.NativeItemId].Value, schema_revision = i.Extension!.SchemaRevision, payload = JsonSerializer.Deserialize<JsonElement>(i.Extension!.Payload.Text), wire_representation = i.Extension!.WireRepresentation is { } j3 ? JsonSerializer.Deserialize<JsonElement>(j3.Text) : (JsonElement?)null }))));
        // Items saved without an extension drop any extension they had before.
        if (items.Any(i => i.Extension is null))
        await work.ExecuteAsync("DELETE FROM ultimateteam.item_extensions WHERE item_id = ANY(@ids)",
            ("ids", items.Where(i => i.Extension is null).Select(i => keys[i.Header.NativeItemId].Value).ToArray()));
        var byNativeId = headers.ToDictionary(h => h.NativeItemId);
        return items.Select(i => byNativeId[i.Header.NativeItemId]).ToList();
    }

    /// <summary>Read all quick-sell guards, and each item's reward source, under the caller's exclusive club lock.</summary>
    internal static ValueTask<List<(long Id, bool InSquad, bool InSlot, bool Listed, string? RewardSource)>> RelationalDiscardGuardsAsync(
        this IUnitOfWork work, long[] nativeIds) => work.QueryAsync("""
            -- query: inventory.discard-guards
            SELECT i.native_item_id,
                EXISTS(SELECT 1 FROM ultimateteam.squad_players p JOIN ultimateteam.squads s ON s.id=p.squad_id WHERE s.club_id=@club AND p.item_id=i.id)
                OR EXISTS(SELECT 1 FROM ultimateteam.squad_managers m JOIN ultimateteam.squads s ON s.id=m.squad_id WHERE s.club_id=@club AND m.item_id=i.id),
                EXISTS(SELECT 1 FROM ultimateteam.club_slot_bindings b WHERE b.club_id=@club AND b.item_id=i.id),
                EXISTS(SELECT 1 FROM ultimateteam.market_listings l WHERE l.kind='player' AND l.seller_club_id=@club AND l.seller_item_id=i.native_item_id AND l.state='active'),
                i.reward_source
            FROM ultimateteam.items i WHERE i.club_id=@club AND i.native_item_id=ANY(@native)
            ORDER BY i.native_item_id
            """, r => (r.GetInt64(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetString(4)), ("native", nativeIds));

    /// <summary>Delete a club-scoped batch and return only IDs actually removed.</summary>
    internal static async ValueTask<List<long>> DeleteRelationalItemsAsync(this IUnitOfWork work, long[] nativeIds)
    {
        if (nativeIds.Length == 0) return new();
        await work.LockRelationalClubAsync();
        await work.DetachOnboardingKitsAsync("i.native_item_id = ANY(@native)", ("native", nativeIds));
        return await work.QueryAsync("""
            -- query: inventory.delete-batch
            DELETE FROM ultimateteam.items WHERE club_id=@club AND native_item_id=ANY(@native)
            RETURNING native_item_id
            """, r => r.GetInt64(0), ("native", nativeIds));
    }

    public static async ValueTask<bool> DeleteRelationalItemAsync(this IUnitOfWork work, NativeItemId nativeId)
    {
        await work.LockRelationalClubAsync();
        await work.DetachOnboardingKitsAsync("i.native_item_id = @native", ("native", nativeId.Value));
        return await work.ExecuteAsync("DELETE FROM ultimateteam.items WHERE club_id = @club AND native_item_id = @native", ("native", nativeId.Value)) == 1;
    }

    public static async ValueTask<int> DeleteRelationalPileAsync(this IUnitOfWork work, string pile)
    {
        await work.LockRelationalClubAsync();
        await work.DetachOnboardingKitsAsync("i.pile = @pile", ("pile", pile));
        return await work.ExecuteAsync("DELETE FROM ultimateteam.items WHERE club_id = @club AND pile = @pile", ("pile", pile));
    }

    public static ValueTask<long> CountRelationalItemsAsync(this IUnitOfWork work, string pile) =>
        work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.items WHERE club_id = @club AND pile = @pile", ("pile", pile));

    private static void Validate(RelationalItemAggregate i, long club)
    {
        if (i.Header.ClubId != club) throw new ArgumentException("Item owner differs from transaction club.");
        var children = new[] { i.Player is not null, i.Manager is not null, i.Consumable is not null,
            i.Customisation is not null, i.Pick is not null };
        if (i.Header.FamilyId < 1 || i.Header.FamilyId > 5 || children.Count(b => b) != 1 || !children[i.Header.FamilyId - 1])
            throw new ArgumentException("Item must have exactly its matching family state.");
        if (i.Header.FamilyId != 1 && (i.Stats?.Count ?? 0) > 0)
            throw new ArgumentException("Only player items have statistics.");
        if (i.Consumable is { } stack && (stack.Quantity <= 0 || stack.UntradeableQuantity < 0 || stack.UntradeableQuantity > stack.Quantity))
            throw new ArgumentException("Consumable quantities must be positive with a valid untradeable subset.");
    }

    internal static JsonParameter Rows<T>(IEnumerable<T> rows) => JsonText(JsonSerializer.Serialize(rows));
}
