using System.Globalization;
using System.Text.Json;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Cards' non-player tabs: consumables, club customisation and managers, listed for granting only (none of them is edited
/// here). Every item but a My Stadium one comes from the non-player drop table, so the panel grants exactly what packs can
/// build. An item key is <c>&lt;pool&gt;:&lt;resourceId&gt;</c>, or <c>vanity:&lt;subtype&gt;:&lt;asset&gt;</c> for a My Stadium item.
/// </summary>
public sealed partial class OperatorPanel
{
    private const int MaximumItemGrant = 24;
    private const int MaximumNewItems = 100;
    private const string VanityPrefix = "vanity";

    /// <summary>The panel tab of each drop pool and what its items are called there, in the order the tabs list them.</summary>
    private static readonly (string Pool, string Tab, string Label)[] ItemPools =
    [
        (NonPlayerDropTable.ChemistryStyle, "consumables", "Chemistry style"),
        (NonPlayerDropTable.PositionModifier, "consumables", "Position change"),
        (NonPlayerDropTable.ManagerLeague, "consumables", "Manager league"),
        (NonPlayerDropTable.Kit, "customisation", "Kit"),
        (NonPlayerDropTable.Badge, "customisation", "Badge"),
        (NonPlayerDropTable.Ball, "customisation", "Ball"),
        (NonPlayerDropTable.Stadium, "customisation", "Stadium"),
        (NonPlayerDropTable.Manager, "managers", "Manager"),
    ];

    private static readonly string[] ItemTabs = ["consumables", "customisation", "managers"];

    /// <summary>GET cards/items?tab=consumables|customisation|managers: every grantable item of one tab.</summary>
    private object ItemList(ApiRequest request)
    {
        var tab = request.Query("tab");
        if (!ItemTabs.Contains(tab)) throw new PanelError(400, "tab must be consumables, customisation or managers.");
        var snapshot = game.Live.Snapshot;
        var catalog = snapshot.Catalog;
        var teams = catalog.Teams.GroupBy(t => t.NativeTeamId).ToDictionary(g => g.Key, g => g.First().Name);
        var leagues = catalog.Leagues.GroupBy(l => l.NativeLeagueId).ToDictionary(g => g.Key, g => g.First().Name);
        var nations = catalog.Nations.GroupBy(n => n.NativeNationId).ToDictionary(g => g.Key, g => g.First().Name);
        var types = new List<object>();
        var items = new List<object>();
        foreach (var (pool, _, label) in ItemPools.Where(entry => entry.Tab == tab))
        {
            var drops = snapshot.Drops.Pool(pool);
            types.Add(new { value = pool, label, count = drops.Count });
            foreach (var drop in drops.OrderByDescending(d => d.Rating).ThenBy(d => DisplayName(d, teams), StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Resource))
            {
                var manager = pool == NonPlayerDropTable.Manager ? snapshot.Managers.Find(drop.Resource) : null;
                var league = manager?.Int("leagueId") ?? 0;
                var nation = manager?.Int("nation") ?? 0;
                items.Add(new
                {
                    key = $"{pool}:{drop.Resource.ToString(CultureInfo.InvariantCulture)}",
                    type = pool, typeName = label, name = DisplayName(drop, teams), rating = drop.Rating, rareflag = drop.Rareflag, resourceId = drop.Resource,
                    team = drop.Team > 0 ? teams.GetValueOrDefault(drop.Team, $"Club {drop.Team}") : null,
                    league = league > 0 ? leagues.GetValueOrDefault(league, $"League {league}") : null,
                    nation = nation > 0 ? nations.GetValueOrDefault(nation, $"Nation {nation}") : null,
                    forcedUntradeable = false,
                    countMax = OneCopy(pool) ? 1 : MaximumItemGrant,
                });
            }
        }
        if (tab == "customisation")
        {
            // My Stadium items are club unlocks, not owned items: always untradeable, one each, and the starter items every club has are left out.
            var vanity = game.Rules.Club.Vanity;
            var starters = vanity.StarterItems.Select(item => (item.Subtype, item.Asset)).ToHashSet();
            foreach (var group in vanity.Items().Where(item => !starters.Contains((item.Subtype, item.Asset))).GroupBy(item => item.Subtype).OrderBy(g => g.Key))
            {
                var label = vanity.SubtypeName(group.Key);
                types.Add(new { value = $"{VanityPrefix}:{group.Key.ToString(CultureInfo.InvariantCulture)}", label = $"My Stadium: {label}", count = group.Count() });
                foreach (var (subtype, asset, _) in group)
                {
                    var (rating, rareflag) = vanity.RarityOf(subtype, asset);
                    var nameKey = vanity.NameKey(subtype, asset);
                    items.Add(new
                    {
                        key = $"{VanityPrefix}:{subtype.ToString(CultureInfo.InvariantCulture)}:{asset.ToString(CultureInfo.InvariantCulture)}",
                        type = $"{VanityPrefix}:{subtype.ToString(CultureInfo.InvariantCulture)}", typeName = $"My Stadium: {label}",
                        name = $"{label} {asset.ToString(CultureInfo.InvariantCulture)}", nameKey = nameKey.Length > 0 ? nameKey : null,
                        rating, rareflag, resourceId = vanity.ResourceId(subtype, asset),
                        team = (string?)null, league = (string?)null, nation = (string?)null,
                        forcedUntradeable = true, countMax = 1,
                    });
                }
            }
        }
        return new { tab, types, items, total = items.Count };
    }

    /// <summary>
    /// The grantItem edit: <c>count</c> copies of one non-player item into the club's New items, tradeable unless asked
    /// otherwise. A club cosmetic and a My Stadium item are granted once and refused when the club already has them; a My
    /// Stadium item is always untradeable.
    /// </summary>
    private async Task GrantItemAsync(JsonElement body, ClubSummary club, string? discordId, CancellationToken cancellationToken)
    {
        foreach (var property in body.EnumerateObject())
            if (property.Name is not ("op" or "item" or "count" or "untradeable" or "confirmOnline"))
                throw new PanelError(400, $"The item grant does not take {property.Name}.");
        var key = TextField(body, "item") ?? throw new PanelError(400, "item must be an item key such as chemistry_style:5003.");
        var parts = key.Split(':');
        var count = WholeField(body, "count") ?? 1;
        var untradeable = FlagField(body, "untradeable") ?? game.Rules.Tradeability.Untradeable(RewardSources.Operator);
        using var lease = await players.AcquireAsync(GrantTarget(club, discordId), cancellationToken);

        if (parts.Length == 3 && parts[0] == VanityPrefix)
        {
            var vanity = game.Rules.Club.Vanity;
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var subtype)
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var asset)
                || vanity.ItemIdOf(subtype, asset) is null)
                throw new PanelError(400, $"No My Stadium item in this server's catalogue has the key {key}.");
            if (count != 1) throw new PanelError(400, "A My Stadium item is granted as one copy.");
            if (vanity.StarterItems.Any(item => item.Subtype == subtype && item.Asset == asset))
                throw new PanelError(409, "Every club starts with this My Stadium item.");
            await lease.Session.Game.RunAsync("panel-grant-vanity", async work =>
            {
                await work.LockClubAsync();
                var granted = await lease.Session.Fut.Vanity.GrantAsync(work, [subtype], 1, [asset]);
                if (granted.Count == 0) throw new PanelError(409, "The club already has this My Stadium item.");
                return 0;
            }, cancellationToken);
            return;
        }

        if (parts.Length != 2 || !ItemPools.Any(entry => entry.Pool == parts[0])
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var resource)
            || game.Live.Snapshot.Drops.Pool(parts[0]).FirstOrDefault(drop => drop.Resource == resource) is not { } item)
            throw new PanelError(400, $"No item in this server's catalogue has the key {key}.");
        var cosmetic = NonPlayerDropTable.Cosmetics.Contains(item.Pool);
        var manager = item.Pool == NonPlayerDropTable.Manager;
        if (OneCopy(item.Pool) ? count != 1 : count is < 1 or > MaximumItemGrant)
            throw new PanelError(400, OneCopy(item.Pool) ? $"A {(manager ? "manager" : "club customisation item")} is granted as one copy." : $"count must be a whole number from 1 to {MaximumItemGrant}.");
        await lease.Session.Game.RunAsync("panel-grant-item", async work =>
        {
            await work.LockClubAsync();
            if (cosmetic)
            {
                var defaults = game.Rules.Club.RenderedDefaultActiveItems(club.ClubId).Any(active => active.Text("itemType") == item.Kind && active.Int("resourceId") == item.Resource);
                var owned = defaults || await work.ScalarAsync<bool>("""
                    SELECT EXISTS(SELECT 1 FROM ultimateteam.items i JOIN ultimateteam.item_definitions d ON d.definition_id = i.definition_id
                    JOIN ultimateteam.customisation_definitions c ON c.definition_id = d.definition_id
                    WHERE i.club_id = @club AND d.native_resource_id = @resource AND c.customisation_kind = @kind)
                    """, ("resource", item.Resource), ("kind", item.Kind));
                if (owned) throw new PanelError(409, $"The club already has {item.Name}.");
            }
            // A club holds each manager once, in any pile.
            if (manager && await work.ScalarAsync<bool>("""
                    SELECT EXISTS(SELECT 1 FROM ultimateteam.items i JOIN ultimateteam.item_definitions d ON d.definition_id = i.definition_id
                    WHERE i.club_id = @club AND i.family_id = 2 AND d.native_resource_id = @resource)
                    """, ("resource", item.Resource)))
                throw new PanelError(409, $"The club already has {item.Name}; a club holds each manager once.");
            var held = await work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.items WHERE club_id=@club AND pile='unassigned'");
            if (held + count > MaximumNewItems) throw new PanelError(409, $"New items holds {held}; a grant may leave at most {MaximumNewItems}.");
            var first = await work.ReserveItemIdsAsync((int)count);
            var built = Enumerable.Range(0, (int)count)
                .Select(index => lease.Session.Fut.Items.MarkReward(lease.Session.Fut.Items.BuildDrop(item, first + index, "unassigned"), RewardSources.Operator, untradeable))
                .ToList();
            await work.SaveItemsAsync(built);
            return 0;
        }, cancellationToken);
    }

    /// <summary>Club customisation items and managers: a club holds each once, so a grant gives one copy.</summary>
    private static bool OneCopy(string pool) => pool == NonPlayerDropTable.Manager || NonPlayerDropTable.Cosmetics.Contains(pool);

    /// <summary>
    /// The removeItems edit: takes these player cards (native item ids) out of the club and pays nothing. A card in a squad
    /// leaves its squads. Consumables, managers and club customisation stay (a kit or badge slot can never be left empty),
    /// and any such item or a market-listed card refuses the whole removal.
    /// </summary>
    private async Task RemoveItemsAsync(JsonElement body, ClubSummary club, string? discordId, CancellationToken cancellationToken)
    {
        foreach (var property in body.EnumerateObject())
            if (property.Name is not ("op" or "items" or "confirmOnline"))
                throw new PanelError(400, $"The item removal does not take {property.Name}.");
        if (Field(body, "items") is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() is < 1 or > MaximumNewItems)
            throw new PanelError(400, $"items must list 1 to {MaximumNewItems} item ids.");
        var ids = new List<long>();
        foreach (var entry in list.EnumerateArray())
            ids.Add(entry.ValueKind == JsonValueKind.Number && entry.TryGetInt64(out var id) && id > 0 ? id : throw new PanelError(400, "items must list positive item ids."));
        using var lease = await players.AcquireAsync(GrantTarget(club, discordId), cancellationToken);
        try { await lease.Session.Fut.Inventory.RemoveItemsAsync(ids, cancellationToken); }
        catch (ClubRuleException refused) { throw new PanelError(409, refused.Message); }
    }

    /// <summary>
    /// What the panel calls an item. Badges, balls and stadium cards store FIFA's localisation keys (TeamName_Abbr15_*,
    /// BallName_*, StadiumName_*, migration 053), so they are named after their club, ball number and venue instead.
    /// </summary>
    private string DisplayName(NonPlayerDrop drop, IReadOnlyDictionary<long, string> teams) => drop.Pool switch
    {
        NonPlayerDropTable.Badge => $"{(drop.Team > 0 ? teams.GetValueOrDefault(drop.Team, $"Club {drop.Team}") : $"Club {drop.Asset}")} badge",
        NonPlayerDropTable.Ball => $"Ball {drop.Asset.ToString(CultureInfo.InvariantCulture)}",
        NonPlayerDropTable.Stadium => game.Live.Snapshot.Stadiums.Find(drop.Asset)?.Name ?? $"Stadium {drop.Asset.ToString(CultureInfo.InvariantCulture)}",
        _ => drop.Name,
    };

    /// <summary>The drop table's non-player items by pool and resource, with their tab label and panel name (inventory rows).</summary>
    private (object Snapshot, Dictionary<(string Pool, long Resource), (NonPlayerDrop Drop, string Label, string Name)> Index)? _itemIndex;

    /// <summary>A non-player inventory item's name, type and rating from the drop table, or null when it lists no such item.</summary>
    private (string Name, string TypeName, long Rating)? InventoryItemType(short family, string? customisation, long resource)
    {
        var snapshot = game.Live.Snapshot;
        var cached = _itemIndex;
        if (cached is not { } current || !ReferenceEquals(current.Snapshot, snapshot))
        {
            var teams = snapshot.Catalog.Teams.GroupBy(t => t.NativeTeamId).ToDictionary(g => g.Key, g => g.First().Name);
            var index = new Dictionary<(string Pool, long Resource), (NonPlayerDrop Drop, string Label, string Name)>();
            foreach (var (pool, _, label) in ItemPools)
                foreach (var drop in snapshot.Drops.Pool(pool))
                    index.TryAdd((pool, drop.Resource), (drop, label, DisplayName(drop, teams)));
            cached = (snapshot, index);
            _itemIndex = cached;
        }
        string[] pools = family switch
        {
            2 => [NonPlayerDropTable.Manager],
            3 => NonPlayerDropTable.Consumables,
            4 => customisation switch
            {
                "kit" => [NonPlayerDropTable.Kit], "custom" => [NonPlayerDropTable.Badge],
                "ball" => [NonPlayerDropTable.Ball], "stadium" => [NonPlayerDropTable.Stadium], _ => [],
            },
            _ => [],
        };
        foreach (var pool in pools)
            if (cached.Value.Index.TryGetValue((pool, resource), out var found))
                return (found.Name, found.Label, found.Drop.Rating);
        // A manager without a league is not in the drop table, but the catalogue still names it.
        return family == 2 && game.Live.Snapshot.Managers.Find(resource) is { } manager
            ? (manager.Text("name"), "Manager", manager.Int("rating")) : null;
    }

    /// <summary>The club's granted My Stadium items, kept (<paramref name="stored"/>) or still waiting in New items, as inventory rows.</summary>
    private async Task<List<object>> VanityInventoryAsync(long clubId, bool stored, CancellationToken cancellationToken)
    {
        var vanity = game.Rules.Club.Vanity;
        var rows = await game.UnitOfWork.RunReadAsync(clubId, work => work.QueryAsync("""
            SELECT v.native_subtype, v.native_asset_id, EXISTS(SELECT 1 FROM ultimateteam.club_vanity_slots s WHERE s.club_id = v.club_id
                AND s.native_subtype = v.native_subtype AND s.native_asset_id = v.native_asset_id)
            FROM ultimateteam.club_vanity_items v WHERE v.club_id = @club AND v.stored = @stored ORDER BY v.native_subtype, v.native_asset_id
            """, row => (Subtype: (long)row.GetInt32(0), Asset: row.GetInt64(1), Equipped: row.GetBoolean(2)), ("stored", stored)), cancellationToken);
        return rows.Where(row => vanity.ItemIdOf(row.Subtype, row.Asset) is not null).Select(row =>
        {
            var label = vanity.SubtypeName(row.Subtype);
            return (object)new
            {
                id = vanity.ItemIdOf(row.Subtype, row.Asset)!.Value,
                assetId = row.Asset,
                resourceId = vanity.ResourceId(row.Subtype, row.Asset),
                name = $"{label} {row.Asset.ToString(CultureInfo.InvariantCulture)}",
                typeName = $"My Stadium: {label}",
                rating = vanity.RarityOf(row.Subtype, row.Asset).Rating,
                position = (string?)null,
                kind = "clubItem",
                special = (string?)null,
                untradeable = true,
                source = "granted",
                loans = 0,
                suspendedGames = 0,
                inSquads = Array.Empty<string>(),
                equipped = row.Equipped,
                listed = false,
                removable = false,
                art = (object?)null,
            };
        }).ToList();
    }

    /// <summary>The player whose club a grant opens: the Discord account's, or the server's own local club.</summary>
    private PlayerIdentity GrantTarget(ClubSummary club, string? discordId) =>
        discordId is not null ? PlayerIdentity.Of(discordId)
        : club.PersonaId == players.Local.Player.PersonaId ? players.Local.Player
        : throw new PanelError(409, "That club belongs to no known player, so the server cannot open it to grant an item.");
}
