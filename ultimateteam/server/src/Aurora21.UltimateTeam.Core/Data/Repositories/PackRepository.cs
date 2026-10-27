using Aurora21.UltimateTeam.Core.Data.Records;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>How an owned pack was granted: untradeable or not, and its grant source.</summary>
public readonly record struct OwnedPackGrant(bool Untradeable, string Source);

/// <summary>Unopened relational rewards, scoped to the transaction's club.</summary>
public static class PackRepository
{
    public static async ValueTask<UnopenedPackRecord> AddRelationalUnopenedPackAsync(this IUnitOfWork work,
        long packId, string source, long? created = null, bool untradeable = false)
    {
        await work.LockRelationalClubAsync();
        return (await work.QueryAsync("""
            INSERT INTO ultimateteam.unopened_packs(club_id,pack_id,source,created,untradeable)
            VALUES(@club,@pack,@source,@created,@untradeable) RETURNING id,club_id,pack_id,source,created
            """, RelationalItemRepository.ReadUnopenedPackRecord,
            ("pack", packId), ("source", source), ("created", created ?? work.NowSeconds), ("untradeable", untradeable))).Single();
    }

    public static ValueTask<List<UnopenedPackRecord>> RelationalUnopenedPacksAsync(this IUnitOfWork work) =>
        work.QueryAsync("SELECT id,club_id,pack_id,source,created FROM ultimateteam.unopened_packs WHERE club_id=@club ORDER BY id",
            RelationalItemRepository.ReadUnopenedPackRecord);

    /// <summary>
    /// Takes one owned copy of a pack: null when the club owns none, else how it was granted: whether it is untradeable (its
    /// items open untradeable; rules/tradeability.json decided at the grant) and its grant source, which its items keep as
    /// their reward source. Tradeable copies go first.
    /// </summary>
    public static async ValueTask<OwnedPackGrant?> TakeOwnedPackAsync(this IUnitOfWork work, long packId)
    {
        await work.LockRelationalClubAsync();
        var taken = await work.QueryAsync("""
            DELETE FROM ultimateteam.unopened_packs WHERE club_id=@club AND id=(
                SELECT id FROM ultimateteam.unopened_packs WHERE club_id=@club AND pack_id=@pack ORDER BY untradeable, id LIMIT 1)
            RETURNING untradeable, source
            """, r => new OwnedPackGrant(r.GetBoolean(0), r.GetString(1)), ("pack", packId));
        return taken.Count == 0 ? null : taken[0];
    }

    public static async ValueTask<UnopenedPackRecord?> TakeRelationalUnopenedPackAsync(this IUnitOfWork work, long packId)
    {
        await work.LockRelationalClubAsync();
        return (await work.QueryAsync("""
            DELETE FROM ultimateteam.unopened_packs WHERE club_id=@club AND id=(
                SELECT id FROM ultimateteam.unopened_packs WHERE club_id=@club AND pack_id=@pack ORDER BY id LIMIT 1)
            RETURNING id,club_id,pack_id,source,created
            """, RelationalItemRepository.ReadUnopenedPackRecord, ("pack", packId))).SingleOrDefault();
    }
}
