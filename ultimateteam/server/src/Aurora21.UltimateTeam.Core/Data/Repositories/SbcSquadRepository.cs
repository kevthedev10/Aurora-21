namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>One saved SBC squad slot: the formation slot index and the native item placed there.</summary>
public readonly record struct SbcSquadSlot(int Index, long ItemId);

/// <summary>A club's saved SBC squad: its filled slots, the formation FIFA saved and the manager item it saved (if any).</summary>
public sealed record SbcSavedSquad(IReadOnlyList<SbcSquadSlot> Slots, string? Formation, long? ManagerItemId);

/// <summary>Club-scoped SBC selections; native item IDs are revalidated against ownership at submission.</summary>
public static class SbcSquadRepository
{
    public static ValueTask<List<long>> SbcSquadItemsAsync(this IUnitOfWork work, long challengeId) =>
        work.QueryAsync("SELECT native_item_id FROM ultimateteam.sbc_squad_items WHERE club_id=@club AND challenge_id=@challenge ORDER BY slot_index", r=>r.GetInt64(0), ("challenge",challengeId));

    public static async ValueTask<SbcSavedSquad> SbcSavedSquadAsync(this IUnitOfWork work, long challengeId)
    {
        var slots = await work.QueryAsync("SELECT slot_index, native_item_id FROM ultimateteam.sbc_squad_items WHERE club_id=@club AND challenge_id=@challenge ORDER BY slot_index",
            r=>new SbcSquadSlot(r.GetInt32(0), r.GetInt64(1)), ("challenge",challengeId));
        var shape = await work.QueryAsync("SELECT formation, manager_item_id FROM ultimateteam.sbc_squad_counts WHERE club_id=@club AND challenge_id=@challenge",
            r=>(Formation: r.IsDBNull(0) ? null : r.GetString(0), Manager: r.IsDBNull(1) ? (long?)null : r.GetInt64(1)), ("challenge",challengeId));
        return new SbcSavedSquad(slots, shape.FirstOrDefault().Formation, shape.FirstOrDefault().Manager);
    }

    /// <summary>Replaces the saved squad. Slot indices are the squad's formation slots, so chemistry can be computed from them.</summary>
    public static async ValueTask SaveSbcSquadAsync(this IUnitOfWork work, long challengeId, IReadOnlyList<SbcSquadSlot> slots, string? formation, long? managerItemId)
    {
        await work.LockClubAsync();
        await work.ExecuteAsync("DELETE FROM ultimateteam.sbc_squad_items WHERE club_id=@club AND challenge_id=@challenge", ("challenge",challengeId));
        await work.ExecuteAsync("INSERT INTO ultimateteam.sbc_squad_items(club_id,challenge_id,slot_index,native_item_id) SELECT @club,@challenge,entries.slot,entries.id FROM unnest(@slots::integer[],@ids::bigint[]) AS entries(slot,id)",
            ("challenge",challengeId),("slots",slots.Select(slot=>slot.Index).ToArray()),("ids",slots.Select(slot=>slot.ItemId).ToArray()));
        await work.SetSbcSquadCountAsync(challengeId,slots.Count);
        await work.ExecuteAsync("UPDATE ultimateteam.sbc_squad_counts SET formation=@formation, manager_item_id=@manager WHERE club_id=@club AND challenge_id=@challenge",
            ("challenge",challengeId),("formation",formation),("manager",managerItemId));
    }

    /// <summary>Saves item ids in slots 0, 1, 2... with no formation or manager (also clears a squad when empty).</summary>
    public static ValueTask SaveSbcSquadItemsAsync(this IUnitOfWork work, long challengeId, long[] itemIds) =>
        work.SaveSbcSquadAsync(challengeId, itemIds.Select((id, index) => new SbcSquadSlot(index, id)).ToList(), null, null);
}
