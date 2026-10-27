using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using static Aurora21.UltimateTeam.Core.Data.ClubSql;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>
/// Draft runs in draft_runs and its four child tables. The service keeps working on its run
/// document; this store captures that document into typed rows and rebuilds it. Draft item
/// documents are never stored: each is rebuilt from definition, Draft wire number and timestamp,
/// and a document that would not rebuild identically is refused rather than stored lossily.
/// </summary>
internal sealed class RelationalDraftStore
{
    private const int WireYear = 2021;
    private const string Player = "player";
    private const string Manager = "manager";

    // The run document's fields in the order of the new-run template; all are typed.
    private static readonly string[] Fields =
    {
        "mode", "state", "difficulty", "formation", "formationChoices", "picked", "manager", "currentChoices",
        "currentChoiceKind", "currentPositionId", "seenAssets", "choiceSerial", "seed", "nextItemId", "captain",
        "wins", "losses", "draftsCompleted", "pendingCoinReward", "pendingOffers", "tactics",
    };

    private readonly ItemFactory _items;
    private readonly NativeItems _native;

    internal RelationalDraftStore(GameContext game)
    {
        _items = new ItemFactory(game);
        _native = new NativeItems(game, _items);
    }

    private sealed record ItemRow(string Kind, long Resource, long Wire, long Timestamp);

    private JsonObject Render(ItemRow row)
    {
        if (row.Kind == Manager)
        {
            var manager = _items.BuildManager(row.Resource, row.Wire, "none");
            manager["timestamp"] = row.Timestamp;
            return _native.ManagerItem(manager);
        }
        var player = _items.BuildPlayer(row.Resource, row.Wire, "none");
        player["timestamp"] = row.Timestamp;
        return _native.DraftPlayerItem(player);
    }

    /// <summary>The identity of a Draft item document, accepted only if it rebuilds identically.</summary>
    private ItemRow Capture(string kind, JsonNode? node)
    {
        if (node is not JsonObject item || !JsonValues.IsStrictInt(item.Get("resourceId"), out var resource)
            || !JsonValues.IsStrictInt(item.Get("id"), out var wire) || !JsonValues.IsStrictInt(item.Get("timestamp"), out var timestamp))
        {
            throw new NotSupportedException("Draft item document lacks integer resourceId/id/timestamp; audit required.");
        }
        var row = new ItemRow(kind, resource, wire, timestamp);
        if (!JsonNode.DeepEquals(Render(row), item))
        {
            throw new NotSupportedException("Draft item document differs from its rebuilt definition; audit required.");
        }
        return row;
    }

    // ---- read ------------------------------------------------------------------

    public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork w, DraftSlot slot)
    {
        var headers = await w.QueryAsync("""
            SELECT id, mode, state, difficulty, seed, formation, current_choice_kind, current_position_id, choice_serial,
                   next_wire_item_id, captain_position_id, wins, losses, drafts_completed, pending_coin_reward
            FROM ultimateteam.draft_runs WHERE club_id = @club AND slot = @slot
            """,
            r => (Id: r.GetInt64(0), Mode: r.GetString(1), State: r.GetString(2), Difficulty: r.GetInt64(3), Seed: r.GetInt64(4),
                Formation: r.IsDBNull(5) ? null : r.GetString(5), Kind: r.IsDBNull(6) ? null : r.GetString(6), Position: r.GetInt64(7),
                Serial: r.GetInt64(8), NextWire: r.GetInt64(9), Captain: r.IsDBNull(10) ? (long?)null : r.GetInt64(10),
                Wins: r.GetInt64(11), Losses: r.GetInt64(12), Completed: r.GetInt64(13), Reward: r.GetInt64(14)),
            ("slot", (short)slot));
        if (headers.Count == 0)
        {
            return null;
        }
        var h = headers[0];
        var picks = await w.QueryAsync("""
            SELECT p.pick_kind, p.position_id, d.native_resource_id, p.wire_item_id, p.timestamp
            FROM ultimateteam.draft_picks p JOIN ultimateteam.item_definitions d ON d.definition_id = p.definition_id
            WHERE p.run_id = @run ORDER BY p.pick_kind, p.position_id
            """, r => (Kind: r.GetString(0), Position: r.GetInt64(1), Item: new ItemRow(r.GetString(0), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4))),
            ("run", h.Id));
        var offers = await w.QueryAsync("""
            SELECT c.choice_kind, c.position_id, d.native_resource_id, c.wire_item_id, c.timestamp
            FROM ultimateteam.draft_choices c JOIN ultimateteam.item_definitions d ON d.definition_id = c.definition_id
            WHERE c.run_id = @run ORDER BY c.ordinal
            """, r => (Key: OfferKey(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1)),
                Item: new ItemRow(r.GetString(0) == Manager ? Manager : Player, r.GetInt64(2), r.GetInt64(3), r.GetInt64(4))),
            ("run", h.Id));
        var formations = await w.QueryAsync("SELECT formation FROM ultimateteam.draft_formation_choices WHERE run_id = @run ORDER BY ordinal",
            r => r.GetString(0), ("run", h.Id));
        // Each seen footballer maps back to its one card-art asset (one-to-one in the installed catalog).
        var seen = await w.QueryAsync("""
            SELECT s.footballer_id, min(d.native_asset_id), max(d.native_asset_id)
            FROM ultimateteam.draft_seen_footballers s
            LEFT JOIN ultimateteam.player_card_definitions p ON p.footballer_id = s.footballer_id
            LEFT JOIN ultimateteam.item_definitions d ON d.definition_id = p.definition_id AND d.game_year = @year
            WHERE s.run_id = @run GROUP BY s.footballer_id, s.ordinal ORDER BY s.ordinal
            """, r => (Min: r.IsDBNull(1) ? (long?)null : r.GetInt64(1), Max: r.IsDBNull(2) ? (long?)null : r.GetInt64(2)),
            ("run", h.Id), ("year", WireYear));
        if (seen.Any(s => s.Min is null || s.Min != s.Max))
        {
            throw new NotSupportedException("A seen Draft footballer has no single card asset; audit required.");
        }
        var tacticRows = await w.QueryAsync("""
            SELECT mentality, setting_code, ordinal, integer_value, boolean_value, text_value
            FROM ultimateteam.draft_tactics WHERE run_id = @run ORDER BY mentality, setting_code, ordinal
            """, r => (Mentality: r.GetString(0), Code: r.GetString(1), Ordinal: r.IsDBNull(2) ? (int?)null : r.GetInt32(2),
                Value: !r.IsDBNull(3) ? JsonValue.Create(r.GetInt64(3)) : !r.IsDBNull(4) ? JsonValue.Create(r.GetBoolean(4)) : JsonValue.Create(r.GetString(5))),
            ("run", h.Id));

        var picked = new JsonObject();
        foreach (var pick in picks.Where(p => p.Kind == Player).OrderBy(p => p.Position))
        {
            picked[pick.Position.ToString(CultureInfo.InvariantCulture)] = Render(pick.Item);
        }
        var managerPick = picks.SingleOrDefault(p => p.Kind == Manager);
        var pending = new JsonObject();
        foreach (var group in offers.GroupBy(o => o.Key))
        {
            pending[group.Key] = new JsonArray(group.Select(o => (JsonNode?)Render(o.Item)).ToArray());
        }
        long captain = 0;
        if (h.Captain is { } captainPosition)
        {
            captain = picked.Obj(captainPosition.ToString(CultureInfo.InvariantCulture))?.Int("id")
                ?? throw new InvalidOperationException("Draft captain position has no pick; audit required.");
        }
        var kind = h.Kind ?? string.Empty;
        var run = new JsonObject
        {
            ["mode"] = h.Mode,
            ["state"] = h.State,
            ["difficulty"] = h.Difficulty,
            ["formation"] = h.Formation ?? string.Empty,
            ["formationChoices"] = new JsonArray(formations.Select(f => (JsonNode?)f).ToArray()),
            ["picked"] = picked,
            ["manager"] = managerPick.Item is null ? null : Render(managerPick.Item),
            ["currentChoices"] = CurrentChoices(pending, kind, h.Position),
            ["currentChoiceKind"] = kind,
            ["currentPositionId"] = h.Position,
            ["seenAssets"] = new JsonArray(seen.Select(s => (JsonNode?)JsonValue.Create(s.Min!.Value)).ToArray()),
            ["choiceSerial"] = h.Serial,
            ["seed"] = h.Seed,
            ["nextItemId"] = h.NextWire,
            ["captain"] = captain,
            ["wins"] = h.Wins,
            ["losses"] = h.Losses,
            ["draftsCompleted"] = h.Completed,
            ["pendingCoinReward"] = h.Reward,
            ["pendingOffers"] = pending,
        };
        // A run without custom tactics keeps the new-run document's fields.
        if (tacticRows.Count > 0)
        {
            run["tactics"] = Tactics(tacticRows);
        }
        return run;
    }

    /// <summary>The run's custom tactics, one entry per mentality as FIFA posted it.</summary>
    private static JsonObject Tactics(IEnumerable<(string Mentality, string Code, int? Ordinal, JsonValue Value)> rows)
    {
        var tactics = new JsonObject();
        foreach (var mentality in rows.GroupBy(r => r.Mentality, StringComparer.Ordinal))
        {
            var fields = mentality.Where(r => r.Ordinal is null).Select(r => (r.Code, (JsonNode?)r.Value))
                .Concat(mentality.Where(r => r.Ordinal is not null).GroupBy(r => r.Code, StringComparer.Ordinal)
                    .Select(list => (list.Key, (JsonNode?)new JsonArray(list.Select(e =>
                        (JsonNode?)new JsonObject { ["index"] = e.Ordinal!.Value, ["value"] = e.Value.DeepClone() }).ToArray()))));
            tactics[mentality.Key] = RelationalSquadCompatibilityStore.TacticDocument(fields);
        }
        return tactics;
    }

    /// <summary>The typed rows of the run's tactics (<see cref="Tactics"/> rebuilds them).</summary>
    private static List<object> TacticRows(JsonObject? tactics)
    {
        var rows = new List<object>();
        foreach (var (mentality, node) in tactics ?? new JsonObject())
        {
            if (node is not JsonObject entry)
            {
                throw new NotSupportedException("Draft tactic entry is not an object; audit required.");
            }
            var (values, lists) = RelationalSquadCompatibilityStore.TacticRows(entry);
            rows.AddRange(values.OfType<JsonObject>().Select(v => (object)new
            {
                mentality, code = v.Text("code"), ordinal = (int?)null,
                integer = v.Get("integer")?.GetValue<long>(), boolean = v.Get("boolean")?.GetValue<bool>(), text = v.Get("text")?.GetValue<string>(),
            }));
            rows.AddRange(lists.OfType<JsonObject>().Select(l => (object)new
            {
                mentality, code = l.Text("code"), ordinal = (int?)l.Int("ordinal"),
                integer = (long?)l.Int("value"), boolean = (bool?)null, text = (string?)null,
            }));
        }
        return rows;
    }

    /// <summary>
    /// The schema knows player and manager choices only; a captain offer is a player choice without a
    /// position, a player offer keeps its position and a manager offer has none.
    /// </summary>
    private static string OfferKey(string kind, long? position) => (kind, position) switch
    {
        (Player, null) => "captain:0",
        (Player, { } slot) => $"player:{slot.ToString(CultureInfo.InvariantCulture)}",
        (Manager, null) => "manager:0",
        _ => throw new NotSupportedException("Draft choice kind/position is not a traced shape; audit required."),
    };

    /// <summary>
    /// The service always shows the kept offer of the current captain/player/manager screen, and no
    /// choices otherwise, so the current choices are derived rather than stored twice.
    /// </summary>
    private static JsonArray CurrentChoices(JsonObject pending, string kind, long position)
    {
        if (kind is not ("captain" or Player or Manager))
        {
            return new JsonArray();
        }
        var key = $"{kind}:{(kind == Player ? position : 0).ToString(CultureInfo.InvariantCulture)}";
        return pending.Arr(key)?.DeepClone() as JsonArray ?? new JsonArray();
    }

    // ---- write -----------------------------------------------------------------

    public async ValueTask<int> SaveAsync(IUnitOfWork w, JsonObject run, DraftSlot slot)
    {
        foreach (var (key, _) in run)
        {
            if (Array.IndexOf(Fields, key) < 0)
            {
                throw new NotSupportedException($"Draft run field '{key}' has no relational column; audit required.");
            }
        }
        string Text(string key) => run.Get(key) is { } node && JsonValues.IsString(node) ? node.GetValue<string>()
            : throw new NotSupportedException($"Draft run field '{key}' is not text; audit required.");
        long Number(string key) => JsonValues.IsStrictInt(run.Get(key), out var value) ? value
            : throw new NotSupportedException($"Draft run field '{key}' is not an integer; audit required.");

        var formation = Text("formation");
        var kind = Text("currentChoiceKind");
        var position = Number("currentPositionId");

        var picks = new List<(string Kind, long Position, ItemRow Item)>();
        foreach (var (key, node) in run.Obj("picked") ?? throw new NotSupportedException("Draft picks are not an object; audit required."))
        {
            if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var slotIndex))
            {
                throw new NotSupportedException("Draft pick position is not a number; audit required.");
            }
            picks.Add((Player, slotIndex, Capture(Player, node)));
        }
        if (run.Get("manager") is { } managerNode)
        {
            picks.Add((Manager, 0, Capture(Manager, managerNode)));
        }

        var offers = new List<(string Kind, long? Position, ItemRow Item)>();
        foreach (var (key, node) in run.Obj("pendingOffers") ?? new JsonObject())
        {
            var parts = key.Split(':');
            if (parts.Length != 2 || parts[0] is not ("captain" or Player or Manager)
                || !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offerPosition)
                || parts[0] != Player && offerPosition != 0 || node is not JsonArray items || items.Count == 0)
            {
                throw new NotSupportedException("Draft offer key or list is not a traced shape; audit required.");
            }
            var storedKind = parts[0] == Manager ? Manager : Player;
            long? storedPosition = parts[0] == Player ? offerPosition : null;
            offers.AddRange(items.Select(item => (storedKind, storedPosition, Capture(storedKind, item))));
        }
        var pending = new JsonObject();
        foreach (var group in offers.GroupBy(o => OfferKey(o.Kind, o.Position)))
        {
            pending[group.Key] = new JsonArray(group.Select(o => (JsonNode?)Render(o.Item)).ToArray());
        }
        if (!JsonNode.DeepEquals(CurrentChoices(pending, kind, position), run.Get("currentChoices")))
        {
            throw new NotSupportedException("Draft current choices differ from the kept offer; audit required.");
        }

        var formations = (run.Arr("formationChoices") ?? throw new NotSupportedException("Draft formation choices are not a list; audit required."))
            .Select(node => node is not null && JsonValues.IsString(node) ? node.GetValue<string>()
                : throw new NotSupportedException("Draft formation choice is not text; audit required.")).ToArray();
        var assets = (run.Arr("seenAssets") ?? throw new NotSupportedException("Draft seen assets are not a list; audit required."))
            .Select(node => JsonValues.IsStrictInt(node, out var asset) ? asset
                : throw new NotSupportedException("Draft seen asset is not an integer; audit required.")).ToArray();
        if (assets.Distinct().Count() != assets.Length)
        {
            throw new NotSupportedException("Draft seen assets repeat; audit required.");
        }

        var tactics = TacticRows(run.Obj("tactics"));

        long? captainPosition = null;
        var captain = Number("captain");
        if (captain != 0)
        {
            var holder = picks.Where(p => p.Kind == Player && p.Item.Wire == captain).ToList();
            captainPosition = holder.Count == 1 ? holder[0].Position
                : throw new NotSupportedException("Draft captain is not exactly one current pick; audit required.");
        }

        await w.LockClubAsync();
        var resources = picks.Select(p => p.Item.Resource).Concat(offers.Select(o => o.Item.Resource)).Distinct().ToArray();
        var definitions = (await w.QueryAsync("""
            SELECT d.native_resource_id, d.definition_id, d.family_id, p.footballer_id FROM ultimateteam.item_definitions d
            LEFT JOIN ultimateteam.player_card_definitions p ON p.definition_id = d.definition_id
            WHERE d.game_year = @year AND d.family_id IN (1, 2) AND d.native_resource_id = ANY(@resources)
            """, r => (Resource: r.GetInt64(0), Definition: r.GetInt64(1), Family: r.GetInt64(2), Footballer: r.IsDBNull(3) ? (long?)null : r.GetInt64(3)),
            ("year", WireYear), ("resources", resources)))
            .GroupBy(d => (d.Resource, d.Family)).ToDictionary(g => g.Key, g => g.Single());
        (long Definition, long? Footballer) Resolve(ItemRow item)
        {
            var family = item.Kind == Manager ? 2L : 1L;
            return definitions.TryGetValue((item.Resource, family), out var d) ? (d.Definition, d.Footballer)
                : throw new NotSupportedException("Draft item has no FIFA 21 definition of its family; audit required.");
        }
        var seen = (await w.QueryAsync("""
            SELECT d.native_asset_id, min(p.footballer_id), max(p.footballer_id)
            FROM ultimateteam.item_definitions d JOIN ultimateteam.player_card_definitions p ON p.definition_id = d.definition_id
            WHERE d.game_year = @year AND d.native_asset_id = ANY(@assets) GROUP BY d.native_asset_id
            """, r => (Asset: r.GetInt64(0), Min: r.GetInt64(1), Max: r.GetInt64(2)), ("year", WireYear), ("assets", assets)))
            .ToDictionary(s => s.Asset);
        var seenRows = assets.Select((asset, ordinal) => seen.TryGetValue(asset, out var s) && s.Min == s.Max
            ? new { footballer_id = s.Min, ordinal }
            : throw new NotSupportedException("A seen Draft asset has no single footballer; audit required.")).ToArray();

        var runId = await w.ScalarAsync<long>("""
            INSERT INTO ultimateteam.draft_runs(club_id, slot, run_revision, mode, state, difficulty, seed, formation, current_choice_kind,
                current_position_id, choice_serial, next_wire_item_id, captain_position_id, wins, losses, drafts_completed, pending_coin_reward)
            VALUES(@club, @slot, 1, @mode, @state, @difficulty, @seed, @formation, @kind, @position, @serial, @nextWire, @captain,
                @wins, @losses, @completed, @reward)
            ON CONFLICT (club_id, slot) DO UPDATE SET run_revision = ultimateteam.draft_runs.run_revision + 1, mode = excluded.mode,
                state = excluded.state, difficulty = excluded.difficulty, seed = excluded.seed, formation = excluded.formation,
                current_choice_kind = excluded.current_choice_kind, current_position_id = excluded.current_position_id,
                choice_serial = excluded.choice_serial, next_wire_item_id = excluded.next_wire_item_id,
                captain_position_id = excluded.captain_position_id, wins = excluded.wins, losses = excluded.losses,
                drafts_completed = excluded.drafts_completed, pending_coin_reward = excluded.pending_coin_reward
            RETURNING id
            """,
            ("slot", (short)slot), ("mode", Text("mode")), ("state", Text("state")), ("difficulty", Number("difficulty")),
            ("seed", Number("seed")), ("formation", formation.Length == 0 ? null : formation), ("kind", kind.Length == 0 ? null : kind),
            ("position", position), ("serial", Number("choiceSerial")), ("nextWire", Number("nextItemId")), ("captain", captainPosition),
            ("wins", Number("wins")), ("losses", Number("losses")), ("completed", Number("draftsCompleted")), ("reward", Number("pendingCoinReward")));
        // Child rows are upserted on their natural keys and the rest deleted, in one statement: rows a
        // save leaves unchanged are not rewritten, and their deferred invariant checks do not run again.
        await w.ExecuteAsync("""
            -- query: draft.children.save
            WITH picks AS (
                SELECT * FROM json_to_recordset(@picks) AS x(kind text, position bigint, definition bigint, footballer bigint, wire bigint, timestamp bigint)
            ), removed_picks AS (
                DELETE FROM ultimateteam.draft_picks p WHERE p.run_id = @run
                    AND NOT EXISTS (SELECT 1 FROM picks x WHERE x.kind = p.pick_kind AND x.position = p.position_id)
            ), saved_picks AS (
                INSERT INTO ultimateteam.draft_picks(run_id, pick_kind, position_id, definition_id, footballer_id, wire_item_id, timestamp)
                SELECT @run, x.kind, x.position, x.definition, x.footballer, x.wire, x.timestamp FROM picks x
                ON CONFLICT (run_id, pick_kind, position_id) DO UPDATE SET definition_id = excluded.definition_id,
                    footballer_id = excluded.footballer_id, wire_item_id = excluded.wire_item_id, timestamp = excluded.timestamp
                WHERE (draft_picks.definition_id, draft_picks.footballer_id, draft_picks.wire_item_id, draft_picks.timestamp)
                    IS DISTINCT FROM (excluded.definition_id, excluded.footballer_id, excluded.wire_item_id, excluded.timestamp)
            ), choices AS (
                SELECT * FROM json_to_recordset(@choices) AS x(ordinal integer, kind text, position bigint, definition bigint, footballer bigint, wire bigint, timestamp bigint)
            ), removed_choices AS (
                DELETE FROM ultimateteam.draft_choices c WHERE c.run_id = @run
                    AND NOT EXISTS (SELECT 1 FROM choices x WHERE x.ordinal = c.ordinal)
            ), saved_choices AS (
                INSERT INTO ultimateteam.draft_choices(run_id, ordinal, choice_kind, position_id, definition_id, footballer_id, wire_item_id, timestamp)
                SELECT @run, x.ordinal, x.kind, x.position, x.definition, x.footballer, x.wire, x.timestamp FROM choices x
                ON CONFLICT (run_id, ordinal) DO UPDATE SET choice_kind = excluded.choice_kind, position_id = excluded.position_id,
                    definition_id = excluded.definition_id, footballer_id = excluded.footballer_id, wire_item_id = excluded.wire_item_id,
                    timestamp = excluded.timestamp
                WHERE (draft_choices.choice_kind, draft_choices.position_id, draft_choices.definition_id, draft_choices.footballer_id,
                    draft_choices.wire_item_id, draft_choices.timestamp)
                    IS DISTINCT FROM (excluded.choice_kind, excluded.position_id, excluded.definition_id, excluded.footballer_id,
                    excluded.wire_item_id, excluded.timestamp)
            ), formations AS (
                SELECT ordinality - 1 AS ordinal, formation FROM unnest(@formations) WITH ORDINALITY AS f(formation, ordinality)
            ), removed_formations AS (
                DELETE FROM ultimateteam.draft_formation_choices c WHERE c.run_id = @run
                    AND NOT EXISTS (SELECT 1 FROM formations x WHERE x.ordinal = c.ordinal)
            ), saved_formations AS (
                INSERT INTO ultimateteam.draft_formation_choices(run_id, ordinal, formation)
                SELECT @run, x.ordinal, x.formation FROM formations x
                ON CONFLICT (run_id, ordinal) DO UPDATE SET formation = excluded.formation
                WHERE draft_formation_choices.formation IS DISTINCT FROM excluded.formation
            ), seen AS (
                SELECT * FROM json_to_recordset(@seen) AS x(footballer_id bigint, ordinal integer)
            ), removed_seen AS (
                DELETE FROM ultimateteam.draft_seen_footballers s WHERE s.run_id = @run
                    AND NOT EXISTS (SELECT 1 FROM seen x WHERE x.footballer_id = s.footballer_id)
            )
            INSERT INTO ultimateteam.draft_seen_footballers(run_id, footballer_id, ordinal)
            SELECT @run, x.footballer_id, x.ordinal FROM seen x
            ON CONFLICT (run_id, footballer_id) DO UPDATE SET ordinal = excluded.ordinal
            WHERE draft_seen_footballers.ordinal IS DISTINCT FROM excluded.ordinal
            """, ("run", runId),
            ("picks", RelationalItemRepository.Rows(picks.Select(p =>
            {
                var (definition, footballer) = Resolve(p.Item);
                return new { kind = p.Kind, position = p.Position, definition, footballer, wire = p.Item.Wire, timestamp = p.Item.Timestamp };
            }))),
            ("choices", RelationalItemRepository.Rows(offers.Select((o, ordinal) =>
            {
                var (definition, footballer) = Resolve(o.Item);
                return new { ordinal, kind = o.Kind, position = o.Position, definition, footballer, wire = o.Item.Wire, timestamp = o.Item.Timestamp };
            }))),
            ("formations", formations),
            ("seen", RelationalItemRepository.Rows(seenRows)));
        await w.ExecuteAsync("""
            -- query: draft.tactics.save
            WITH tactics AS (
                SELECT * FROM json_to_recordset(@tactics) AS x(mentality text, code text, ordinal integer, integer bigint, boolean boolean, text text)
            ), removed AS (
                DELETE FROM ultimateteam.draft_tactics t WHERE t.run_id = @run
                    AND NOT EXISTS (SELECT 1 FROM tactics x WHERE x.mentality = t.mentality AND x.code = t.setting_code
                        AND x.ordinal IS NOT DISTINCT FROM t.ordinal)
            )
            INSERT INTO ultimateteam.draft_tactics(run_id, mentality, setting_code, ordinal, integer_value, boolean_value, text_value)
            SELECT @run, x.mentality, x.code, x.ordinal, x.integer, x.boolean, x.text FROM tactics x
            ON CONFLICT ON CONSTRAINT draft_tactics_unique DO UPDATE SET integer_value = excluded.integer_value,
                boolean_value = excluded.boolean_value, text_value = excluded.text_value
            WHERE (draft_tactics.integer_value, draft_tactics.boolean_value, draft_tactics.text_value)
                IS DISTINCT FROM (excluded.integer_value, excluded.boolean_value, excluded.text_value)
            """, ("run", runId), ("tactics", RelationalItemRepository.Rows(tactics)));
        return 1;
    }

    public async ValueTask<int> ClearAsync(IUnitOfWork w, DraftSlot slot)
    {
        await w.LockClubAsync();
        return await w.ExecuteAsync("DELETE FROM ultimateteam.draft_runs WHERE club_id = @club AND slot = @slot", ("slot", (short)slot));
    }
}
