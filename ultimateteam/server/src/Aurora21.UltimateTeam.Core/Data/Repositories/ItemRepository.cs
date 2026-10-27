using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>Item persistence backed by the relational schema.</summary>
public static class ItemRepository
{
    /// <summary>Owned player cards (club before squad); <paramref name="permanentOnly"/> leaves out loan copies.</summary>
    public static ValueTask<List<(long ResourceId, long ItemId)>> OwnedPlayerIdentitiesAsync(this IUnitOfWork work,
        long[]? resourceIds = null, bool permanentOnly = false) =>
        work.RelationalItems.OwnedAsync(work, resourceIds, permanentOnly);

    /// <summary>
    /// Owned manager and club item cards (club pile before squad pile) of these resources, as (item type, resource, native item
    /// id). The item type is the card's wire itemType: manager, kit, custom (badge), ball or stadium.
    /// </summary>
    public static ValueTask<List<(string Kind, long ResourceId, long ItemId)>> OwnedNonPlayerIdentitiesAsync(this IUnitOfWork work, long[] resourceIds) =>
        work.QueryAsync("""
            -- query: inventory.owned-non-player-identities
            SELECT d.native_item_type, d.native_resource_id, i.native_item_id FROM ultimateteam.items i
            JOIN ultimateteam.item_definitions d ON d.definition_id = i.definition_id
            WHERE i.club_id = @club AND i.family_id IN (2, 4) AND i.pile IN ('club', 'squad') AND d.native_resource_id = ANY(@resources)
            ORDER BY CASE WHEN i.pile = 'club' THEN 0 ELSE 1 END, i.native_item_id
            """, r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ("resources", resourceIds));

    /// <summary>Which of these definitions (native resource ids) the club owns a copy of, in any pile.</summary>
    public static async ValueTask<HashSet<long>> OwnedDefinitionsAsync(this IUnitOfWork work, long[] resourceIds) =>
        (await work.QueryAsync("""
            -- query: inventory.owned-definitions
            SELECT DISTINCT d.native_resource_id FROM ultimateteam.items i
            JOIN ultimateteam.item_definitions d ON d.definition_id = i.definition_id
            WHERE i.club_id = @club AND d.native_resource_id = ANY(@resources)
            """, r => r.GetInt64(0), ("resources", resourceIds))).ToHashSet();

    public static ValueTask<List<JsonObject>> ListItemsAsync(this IUnitOfWork work, string? pile = null) =>
        work.RelationalItems.ListAsync(work, pile);

    public static ValueTask<List<(long Id, JsonObject Data)>> ListItemRowsAsync(this IUnitOfWork work, string? pile = null) =>
        work.RelationalItems.RowsAsync(work, pile);

    public static ValueTask<JsonObject?> GetItemAsync(this IUnitOfWork work, long id) =>
        work.RelationalItems.GetAsync(work, id);

    public static ValueTask<List<JsonObject>> GetItemsAsync(this IUnitOfWork work, IReadOnlyCollection<long> ids) =>
        work.RelationalItems.GetManyAsync(work, ids);

    public static ValueTask<JsonObject?> ItemOfAsync(this IUnitOfWork work, long clubId, long itemId, bool forUpdate = false) =>
        work.RelationalItems.ItemOfAsync(work, clubId, itemId, forUpdate);

    public static ValueTask<bool> ItemExistsAsync(this IUnitOfWork work, long id) =>
        work.RelationalItems.ExistsAsync(work, id);

    public static ValueTask<bool> AnyItemsAsync(this IUnitOfWork work) =>
        work.RelationalItems.AnyAsync(work);

    public static ValueTask<int> CountItemsAsync(this IUnitOfWork work, string pile) =>
        work.RelationalItems.CountAsync(work, pile);

    public static ValueTask<int> SaveItemAsync(this IUnitOfWork work, JsonObject item) =>
        work.RelationalItems.SaveAsync(work, item.IntKeep("id"), item);

    public static ValueTask<int> SaveItemAsync(this IUnitOfWork work, long id, JsonObject item) =>
        work.RelationalItems.SaveAsync(work, id, item);

    public static ValueTask<int> SaveItemOfAsync(this IUnitOfWork work, long clubId, long id, JsonObject item) =>
        work.RelationalItems.SaveOfAsync(work, clubId, id, item);

    public static ValueTask<int> SaveItemsAsync(this IUnitOfWork work, IEnumerable<JsonObject> items) =>
        work.RelationalItems.SaveManyAsync(work, items);

    public static ValueTask<bool> DeleteItemAsync(this IUnitOfWork work, long id) =>
        work.RelationalItems.DeleteAsync(work, id);

    public static ValueTask<bool> DeleteItemOfAsync(this IUnitOfWork work, long clubId, long itemId) =>
        work.RelationalItems.DeleteOfAsync(work, clubId, itemId);

    public static ValueTask<int> DeletePileAsync(this IUnitOfWork work, string pile) =>
        work.RelationalItems.DeletePileAsync(work, pile);

    /// <summary>How many of the club's items are in a pile.</summary>
    public static ValueTask<long> CountPileAsync(this IUnitOfWork work, string pile) =>
        work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.items WHERE club_id = @club AND pile = @pile", ("pile", pile));

    public static ValueTask<List<(long Id, string? Pile)>> ItemsWithResourceAsync(this IUnitOfWork work, long resourceId) =>
        work.RelationalItems.WithResourceAsync(work, resourceId);

    public static ValueTask<int> DeleteItemsWithResourceAsync(this IUnitOfWork work, long resourceId) =>
        work.RelationalItems.DeleteResourceAsync(work, resourceId);

    public static async ValueTask<long> AddUnopenedPackAsync(this IUnitOfWork work, long packId, string source, long? created = null,
        bool untradeable = false) =>
        (await work.AddRelationalUnopenedPackAsync(packId, source, created, untradeable)).Id;

    /// <summary>
    /// Grants one unopened reward pack from a grant source (rules/tradeability.json, <see cref="Rules.RewardSources"/>): the
    /// switch decides whether it opens untradeable, and its items keep the source as their reward source. Every reward grant
    /// goes through here, so changing a source's tradeability needs no code change.
    /// </summary>
    public static ValueTask<long> AddRewardPackAsync(this IUnitOfWork work, Rules.TradeabilityRules tradeability, long packId, string source,
        long? created = null) =>
        work.AddUnopenedPackAsync(packId, source, created, tradeability.Untradeable(source, packId));

    public static ValueTask<List<long>> UnopenedPackIdsAsync(this IUnitOfWork work) =>
        work.RelationalItems.PackIdsAsync(work);

    public static ValueTask<bool> TakeUnopenedPackAsync(this IUnitOfWork work, long packId) =>
        work.RelationalItems.TakePackAsync(work, packId);

}
