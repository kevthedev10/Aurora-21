using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;

namespace Aurora21.UltimateTeam.Core.Models;

/// <summary>Response-local metadata plus only the documents needed for squad/customization rendering.</summary>
internal sealed class ClubPresentationView
{
    private readonly List<Item> _items;
    private readonly List<JsonObject> _squads;
    // Counts of ordinary players deliberately omitted from this response-local view.
    private Dictionary<string, int> _omittedPlayers = new();
    internal bool HasPlayerRepresentations { get; private set; }

    private ClubPresentationView(List<Item> items, List<JsonObject> squads)
    {
        _items = items;
        _squads = squads;
    }

    /// <summary>
    /// What a row needs to build its metadata summary, kept instead of the summary itself so a
    /// response that never asks for one never builds it.
    /// </summary>
    internal sealed record MetadataSource(RelationalItemWireMapper Mapper, RelationalItemHeader Header,
        string? Position, long? Style, long Loans, long? ManagerLeague, long PickRating, long PickPreview,
        string? Payload, string? Representation)
    {
        public JsonObject Build() => Mapper.Metadata(Header, Position, Style, Loans, ManagerLeague,
            PickRating, PickPreview, Payload, Representation);
    }

    internal sealed record Item(long StorageId, long DocumentId, string Pile, bool IsManager, string State, long Rating)
    {
        /// <summary>
        /// The native squad card, when the fetch could build it straight from typed state. A row has
        /// this or <see cref="Document"/>, never both: the card is what the squad renderer reads, and
        /// the wire document it would otherwise be projected from has no other reader, so not building
        /// it is the point. Rendered at fetch time so neither the aggregate nor the document stays
        /// alive for the rest of the request.
        /// </summary>
        public JsonObject? NativeCard { get; set; }

        public JsonObject? Document { get; set; }

        /// <summary>Set when the summary had to be built during the read to answer the scalars.</summary>
        public MetadataSource? Source { get; init; }

        private JsonObject? _metadata;

        /// <summary>
        /// The metadata summary, built on first use. Hub asks for counts and a squad and never reads
        /// one, so on that path this stays null and the ~80 summaries it would have built — the
        /// largest single part of a hub response's allocation — are never allocated. The user and
        /// search paths do read it, and get exactly what the eager build produced.
        /// </summary>
        public JsonObject? Metadata
        {
            get => _metadata ??= Source?.Build();
            init => _metadata = value;
        }
    }

    private static async ValueTask<List<Item>> MetadataAsync(IUnitOfWork uow, bool documents, long[]? playerIds = null, bool sharedLock = true) {
            // Retain this shared lock until the root transaction ends, including selected-child reads.
            if (sharedLock && !await uow.EnsureRelationalClubLockAsync(exclusive: false))
                throw new InvalidOperationException("Relational club does not exist.");
            var rows = await uow.QueryAsync("""
                -- query: club.presentation.metadata
                SELECT i.id, i.club_id, i.native_item_id, i.definition_id, i.family_id, i.timestamp,
                    i.pile, i.item_state, i.untradeable, i.owners, i.last_sale_price, i.reward_source,
                    p.position_override, COALESCE(style.native_effect_id,p.native_play_style), COALESCE(p.loan_games,0),
                    m.league_id, COALESCE(pick.min_rating,0), COALESCE(preview.native_resource_id,0),
                    e.payload::text, e.wire_representation::text
                FROM ultimateteam.items i
                LEFT JOIN ultimateteam.player_items p ON p.item_id=i.id
                LEFT JOIN ultimateteam.consumable_definitions style ON style.definition_id=p.style_definition_id
                LEFT JOIN ultimateteam.manager_items m ON m.item_id=i.id
                LEFT JOIN ultimateteam.player_pick_items pick ON pick.item_id=i.id
                LEFT JOIN ultimateteam.item_definitions preview ON preview.definition_id=pick.preview_definition_id
                LEFT JOIN ultimateteam.item_extensions e ON e.item_id=i.id
                WHERE i.club_id=@club AND (@allPlayers OR i.family_id<>1 OR i.native_item_id=ANY(@playerIds))
                ORDER BY i.native_item_id
                """, reader =>
                {
                    var mapper = uow.RelationalItems.Mapper;
                    var header = RelationalItemRepository.ReadRelationalItemHeader(reader);
                    var source = new MetadataSource(mapper, header,
                        reader.IsDBNull(12) ? null : reader.GetString(12),
                        reader.IsDBNull(13) ? null : reader.GetInt64(13), reader.GetInt64(14),
                        reader.IsDBNull(15) ? null : reader.GetInt64(15), reader.GetInt64(16), reader.GetInt64(17),
                        reader.IsDBNull(18) ? null : reader.GetString(18), reader.IsDBNull(19) ? null : reader.GetString(19));
                    // The common row answers its scalars from the header and catalog, and defers the
                    // summary until something asks for one. A manager, or a row carrying a stored
                    // extension, still builds it here and reads the scalars back out, so those keep
                    // the exact values the summary would have given.
                    if (mapper.SummaryScalars(header, source.Payload, source.Representation) is { } scalars)
                    {
                        return new Item(header.NativeItemId.Value, scalars.Id, scalars.Pile, IsManager: false,
                            scalars.State, scalars.Rating) { Source = source };
                    }
                    var summary = source.Build();
                    return new Item(header.NativeItemId.Value, summary.Int("id"), summary.Text("pile"),
                        summary.Text("itemType") == "manager", summary.Text("itemState"), summary.Int("rating"))
                        { Source = source, Metadata = summary };
                }, ("allPlayers", playerIds is null), ("playerIds", playerIds ?? Array.Empty<long>()));
            var wanted = documents ? rows.Select(row => row.StorageId).ToArray() : Array.Empty<long>();
            var full = (await uow.RelationalItems.GetManyAsync(uow, wanted)).ToDictionary(item => item.Int("id"));
            foreach (var row in rows) if (full.TryGetValue(row.DocumentId, out var document)) row.Document = document;
            return rows;
    }

    public static async ValueTask<ClubPresentationView> LoadFullAsync(IUnitOfWork uow) =>
        new(await MetadataAsync(uow, documents: true), await uow.ListSquadsAsync());

    private static async ValueTask<ClubPresentationView> LoadRelationalSelectionAsync(IUnitOfWork uow,
        List<JsonObject> squads, long? squadId)
    {
        if (!await uow.EnsureRelationalClubLockAsync(exclusive: false))
            throw new InvalidOperationException("Relational club does not exist.");
        var selection = new ClubPresentationView(new(), squads);
        var squad = selection.SelectSquad(squadId);
        var ids = squad is null ? new HashSet<long>() : SquadDocuments.ItemIds(squad).ToHashSet();
        foreach (var manager in (squad?.Arr("manager") ?? new JsonArray()).OfType<JsonObject>())
            ids.Add((manager.Obj("itemData") ?? manager).Int("id"));
        // Every squad's starters and managers as well: each squad summary serves the chemistry computed from them.
        ids.UnionWith(ChemistryIds(squads, uow.RelationalItems.Mapper.Game.Chemistry.Starters));
        // Virtual actives replace a matching owned id in count/search merges, even on unusual fixtures.
        var clubRules = uow.RelationalItems.Mapper.Game.Rules.Club;
        foreach (var item in clubRules.DefaultActiveItemsTemplate.RenderArray(clubRules.VirtualItemIdArgs(new TemplateArgs
            { ["timestamp"] = 0, ["stadiumId"] = 0, ["capacity"] = 0 }, uow.ClubId)).OfType<JsonObject>())
            ids.Add(item.Int("id"));
        var selectedIds = ids.ToArray();
        // The representation check and the omitted-player counts share one statement; the counts are
        // simply unused when the club needs the exact renderer below.
        var (unusual, countsJson) = (await uow.QueryAsync("""
            -- query: club.presentation.shape
            SELECT EXISTS(SELECT 1 FROM ultimateteam.items i JOIN ultimateteam.item_extensions e ON e.item_id=i.id
                    WHERE i.club_id=@club AND i.family_id=1 AND e.wire_representation IS NOT NULL
                        -- an empty representation changes nothing, so it is not an unusual one
                        AND e.wire_representation::jsonb <> '{"overrides":{},"missing":[],"nulls":[],"stringNumbers":[]}'::jsonb),
                COALESCE((SELECT json_agg(json_build_array(c.pile, c.n)) FROM (
                    SELECT pile, count(*) AS n FROM ultimateteam.items
                    WHERE club_id=@club AND family_id=1 AND NOT(native_item_id=ANY(@selectedIds)) GROUP BY pile) c), '[]')::text
            """, reader => (reader.GetBoolean(0), reader.GetString(1)), ("selectedIds", selectedIds))).Single();
        // Exceptional imported representations can alter ids, types and piles. Keep their exact renderer.
        if (unusual)
            return new(await MetadataAsync(uow, false, sharedLock: false), squads) { HasPlayerRepresentations = true };
        var counts = JsonNode.Parse(countsJson)!.AsArray()
            .Select(row => (Pile: row![0] is null ? "" : row[0]!.GetValue<string>(), Count: checked((int)row[1]!.GetValue<long>())));
        var view = new ClubPresentationView(await MetadataAsync(uow, false, playerIds: selectedIds, sharedLock: false), squads);
        view._omittedPlayers = counts.ToDictionary(row => row.Pile, row => row.Count);
        return view;
    }

    public static async ValueTask<ClubPresentationView> LoadSquadAsync(IUnitOfWork uow, long? squadId, IReadOnlySet<string> states)
    {
        var view = await LoadRelationalSelectionAsync(uow, await uow.ListSquadsAsync(), squadId);
        var starters = uow.RelationalItems.Mapper.Game.Chemistry.Starters;
        var selected = view.RequiredSquadRows(squadId, states, starters);
        return await view.FetchAsync(uow, selected, view.NativeOnlySquadRows(squadId, states, starters));
    }

    private static IEnumerable<long> ChemistryIds(IEnumerable<JsonObject> squads, int starters) =>
        squads.SelectMany(squad => SquadDocuments.ChemistryItemIds(squad, starters));

    /// <summary>
    /// The squad slots whose only reader is the native squad renderer, so the fetch can build the
    /// native card and never the wire document. A row any other reader also needs keeps its document:
    /// a saved manager, the club's manager row, and a customisation row selected by state, all of
    /// which are handed out as documents by <see cref="Manager(long)"/>, <see cref="ItemDocument"/>
    /// and <see cref="CustomisationItems"/>. A club with imported player representations keeps the
    /// document path whole, as <see cref="LoadRelationalSelectionAsync"/> intends for it.
    /// </summary>
    private HashSet<long> NativeOnlySquadRows(long? squadId, IReadOnlySet<string> states, int starters)
    {
        var squad = HasPlayerRepresentations ? null : SelectSquad(squadId);
        if (squad is null)
        {
            return new HashSet<long>();
        }
        // Other squads' starters are read only for their chemistry, from the native card too. Saved managers of every
        // squad keep their documents (Manager(long) hands them out).
        var ids = SquadDocuments.ItemIds(squad).Concat(ChemistryIds(_squads, starters)).ToHashSet();
        foreach (var saved in _squads.SelectMany(other => (other.Arr("manager") ?? new JsonArray()).OfType<JsonObject>()))
        {
            ids.Remove((saved.Obj("itemData") ?? saved).Int("id"));
        }
        foreach (var row in CustomisationRows(states))
        {
            ids.Remove(row.DocumentId);
        }
        if (ManagerRow() is { } manager)
        {
            ids.Remove(manager.DocumentId);
        }
        return ids;
    }

    private IEnumerable<Item> RequiredSquadRows(long? squadId, IReadOnlySet<string> states, int starters)
    {
        var squad = SelectSquad(squadId);
        var ids = squad is null ? new HashSet<long>() : SquadDocuments.ItemIds(squad).ToHashSet();
        if (squad is not null)
            foreach (var saved in (squad.Arr("manager") ?? new JsonArray()).OfType<JsonObject>())
                ids.Add((saved.Obj("itemData") ?? saved).Int("id"));
        ids.UnionWith(ChemistryIds(_squads, starters));
        // A club with imported player representations loads metadata for every player, and the user response
        // renders every saved squad slot (subs and reserves too), so every slot of every squad needs its document.
        if (HasPlayerRepresentations)
            foreach (var other in _squads)
                ids.UnionWith(SquadDocuments.ItemIds(other));
        // Last document with a matching JSON id wins, as in the previous full-inventory dictionary.
        var players = _items.Where(item => ids.Contains(item.DocumentId))
            .GroupBy(item => item.DocumentId).Select(group => group.Last());
        var selected = players.Concat(CustomisationRows(states));
        if (ManagerRow() is { } manager) { selected = selected.Append(manager); }
        return selected;
    }

    public static async ValueTask<ClubPresentationView> LoadActivesAsync(IUnitOfWork uow, IReadOnlySet<string> states,
        bool allMetadata = false, bool includeManager = false)
    {
        var view = !allMetadata
            ? await LoadRelationalSelectionAsync(uow, new(), null)
            : new ClubPresentationView(await MetadataAsync(uow, documents: false), new());
        var selected = view.CustomisationRows(states);
        if (includeManager && view.ManagerRow() is { } manager) selected = selected.Append(manager);
        return await view.FetchAsync(uow, selected);
    }

    private async ValueTask<ClubPresentationView> FetchAsync(IUnitOfWork uow, IEnumerable<Item> selected,
        IReadOnlySet<long>? nativeOnly = null)
    {
            // One read of the same aggregates GetManyAsync would have read, then the shortest form
            // each row actually has a reader for. A squad slot with no other reader becomes a native
            // card; everything else is rendered to its wire document as before.
            // RenderNativePlayer is held byte-identical to rendering then projecting by
            // NativePlayerRenderParityTests, so this changes what is built, not what is sent.
            var mapper = uow.RelationalItems.Mapper;
            var aggregates = await uow.RelationalItems.AggregatesAsync(uow, selected.Select(item => item.StorageId).Distinct().ToArray());
            var cards = new Dictionary<long, JsonObject>();
            var byId = new Dictionary<long, JsonObject>();
            foreach (var aggregate in aggregates)
            {
                var id = aggregate.Header.NativeItemId.Value;
                // CanRenderNativePlayer is the second guard and an independent one: it already
                // refuses anything that is not a plain 2021 player with no extension.
                if (nativeOnly is not null && nativeOnly.Contains(id) && mapper.CanRenderNativePlayer(aggregate))
                {
                    cards[id] = mapper.RenderNativePlayer(aggregate);
                }
                else
                {
                    byId[id] = mapper.Render(aggregate);
                }
            }
            foreach (var row in _items)
            {
                if (cards.TryGetValue(row.DocumentId, out var card)) row.NativeCard = card;
                else if (byId.TryGetValue(row.DocumentId, out var document)) row.Document = document;
            }
            return this;
    }

    private IEnumerable<Item> CustomisationRows(IReadOnlySet<string> states) =>
        _items.Where(item => item.Pile == "club" && states.Contains(item.State));

    // Preserve the existing -rating sort, JSON-id tie break, and stable storage ordering.
    private Item? ManagerRow() => _items.Where(item => item.IsManager)
        .OrderBy(item => -item.Rating).ThenBy(item => item.DocumentId).FirstOrDefault();

    private static JsonObject Copy(Item item) => item.Document?.Copy()
        ?? throw new InvalidOperationException("Required presentation document was not fetched");

    internal List<JsonObject> MetadataItems() => _items.Select(item => item.Metadata?.Copy() ?? Copy(item)).ToList();

    /// <summary>
    /// Test seam: which form the fetch materialised each selected row as. A byte comparison alone
    /// cannot tell whether the native short path was taken or every row quietly rendered a document,
    /// so the test that asserts the live-set win reads it from here.
    /// </summary>
    internal IEnumerable<(long Id, bool NativeCard, bool Document)> FetchedRows() =>
        _items.Where(item => item.NativeCard is not null || item.Document is not null)
            .Select(item => (item.DocumentId, item.NativeCard is not null, item.Document is not null));

    internal int OmittedPlayers(string pile) => _omittedPlayers.GetValueOrDefault(pile);
    internal int CountPlayers(string pile) => OmittedPlayers(pile) + _items.Count(item => item.Pile == pile
        && Services.Club.ClubInventoryService.IsPlayer(item.Metadata ?? item.Document!));
    public int CountPile(string pile) => OmittedPlayers(pile) + _items.Count(item => item.Pile == pile);
    public List<JsonObject> Items() => _items.Select(Copy).ToList();
    public List<JsonObject> Pile(string pile) => _items.Where(item => item.Pile == pile).Select(Copy).ToList();
    public List<JsonObject> Squads() => _squads.Select(squad => squad.Copy()).ToList();
    public List<JsonObject> CustomisationItems(IReadOnlySet<string> states) => CustomisationRows(states).Select(Copy).ToList();
    public JsonObject ItemDocument(long id) => _items.FirstOrDefault(item=>item.DocumentId==id) is { } item ? Copy(item) : throw new InvalidOperationException("Bound customisation item was not fetched.");
    public JsonObject? Manager(long id) => _items.FirstOrDefault(item => item.IsManager && item.DocumentId == id) is { } manager ? Copy(manager) : null;
    public JsonObject? Manager() => ManagerRow() is { } manager ? Copy(manager) : null;
    /// <summary>
    /// One squad card, able to produce its native form by the shortest route available. Any document
    /// it holds is the view's own, so it is read and never changed.
    /// </summary>
    internal readonly record struct SquadCard(JsonObject? NativeCard, JsonObject? Document)
    {
        /// <summary>
        /// A fresh card every call, like the projection it replaces. The stored card is the view's
        /// own: a JsonNode has one parent, so handing the same instance to two slots of one squad
        /// would throw, and a caller that mutates what it received would change the next render.
        /// </summary>
        public JsonObject Native(Items.NativeItems native) => NativeCard?.Copy() ?? Build(native);

        /// <summary>
        /// The card for a caller that will only write it. Writing never reparents or changes a node,
        /// so the stored card is handed out as it is; anything that puts the result into a document
        /// must use <see cref="Native"/> instead.
        /// </summary>
        public JsonObject NativeForWriting(Items.NativeItems native) => NativeCard ?? Build(native);

        private JsonObject Build(Items.NativeItems native) =>
            native.PlayerItem(Document ?? throw new InvalidOperationException("Required presentation document was not fetched"));
    }

    /// <summary>A squad's cards by document id, carrying typed state when the backend supplied it.</summary>
    public Dictionary<long, SquadCard> SquadItems(JsonObject squad)
    {
        var ids = SquadDocuments.ItemIds(squad).ToHashSet();
        var cards = new Dictionary<long, SquadCard>();
        foreach (var group in _items.Where(item => ids.Contains(item.DocumentId)).GroupBy(item => item.DocumentId))
        {
            var item = group.Last();
            cards[group.Key] = new SquadCard(item.NativeCard, item.NativeCard is null ? item.Document : null);
        }
        return cards;
    }

    public JsonObject? SelectSquad(long? squadId)
    {
        var squad = squadId is null ? _squads.FirstOrDefault(item => item.Bool("active")) ?? _squads.FirstOrDefault()
            : _squads.FirstOrDefault(item => SquadDocuments.Id(item) == squadId);
        return squad?.Copy();
    }
}
