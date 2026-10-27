namespace Aurora21.UltimateTeam.Core.Data.Repositories;

public static partial class ClubRepository
{
    /// <summary>An operator reset leaves the club empty until FIFA completes CreateUser, including on a local server.</summary>
    public const string NativeCreationRequiredFlag = "native_club_creation_required_v1";
    internal static readonly string[] RelationalClubTables = ["market_watch", "club_slot_bindings", "onboarding", "squads", "draft_runs", "objectives", "items", "unopened_packs", "match_settlements", "rivals_progress", "champions_progress", "sbc_completed", "sbc_squad_counts", "sbc_squad_items", "client_data", "club_flags", "squad_battles_weeks", "squad_battles_pending", "club_vanity_slots", "club_vanity_items", "rivals_pending", "club_records", "rivals_week_results", "weekend_league_records", "weekend_league_entries",
        "champion_registrations", "champion_pending", "champion_results", "squad_battles_sets", "squad_battles_results", "champion_connection_claims", "match_clock", "match_rewards", "club_pack_purchases"];
    public static async ValueTask<ClubAccount> EnsureClubAsync(this IUnitOfWork work, ClubIdentity identity, long initialCredits)
    {
        var result=await work.EnsureRelationalClubAsync(identity,initialCredits);
        var a=result.Account;return new(a.ClubId,a.Established,a.Sid,a.Token,result.Created);
    }
    public static ValueTask<long?> ClubIdOfPersonaAsync(this IUnitOfWork work,long personaId) => work.RelationalClubIdOfPersonaAsync(personaId);
    public static ValueTask<bool> IsPristineAsync(this IUnitOfWork work) =>
        work.ScalarAsync<bool>("SELECT NOT ("+string.Join(" OR ",RelationalClubTables.Select(t=>$"EXISTS(SELECT 1 FROM ultimateteam.{t} WHERE club_id=@club)"))+")");
    public static async ValueTask ClearClubAsync(this IUnitOfWork work,long initialCredits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCredits);
        await work.WithdrawFromMarketAsync();
        await work.ExecuteAsync("UPDATE ultimateteam.market_listings SET seller_cleared=true WHERE seller_club_id=@club");
        await DeleteRelationalOwnedRowsAsync(work);
        await work.SetAccountAsync(initialCredits,FirstItemIdOf(work.ClubId),ClubSql.NewSid(),ClubSql.NewToken(),work.NowSeconds,null,0);
        await work.ExecuteAsync("UPDATE ultimateteam.clubs SET fifa_points=0,daily_drop_day=NULL,last_match_create_is_draft=false,last_draft_slot=1 WHERE id=@club");
    }
    public static async ValueTask<bool> DeleteClubAsync(this IUnitOfWork work)
    {
        await work.WithdrawFromMarketAsync();
        // Published snapshot deletion is allowed only with its parent listing deletion.
        await work.ExecuteAsync("DELETE FROM ultimateteam.market_listings WHERE seller_club_id=@club");
        await work.ExecuteAsync("UPDATE ultimateteam.market_listings SET bidder_club_id=NULL WHERE bidder_club_id=@club");
        await DeleteRelationalOwnedRowsAsync(work);
        var deleted=await work.ExecuteAsync("DELETE FROM ultimateteam.clubs WHERE id=@club")>0;
        // The held-lock record also answers "does this club exist"; the row is gone, so drop the entry
        // rather than let a later caller in this transaction read a stale yes.
        if(deleted)work.HeldClubLocks.Remove(work.ClubId);
        return deleted;
    }
    public static ValueTask<int> SetAccountAsync(this IUnitOfWork work,long credits,long nextItemId,string sid,string token,long established,long? selectedStadium,long lastTransaction) =>
        work.ExecuteAsync("UPDATE ultimateteam.clubs SET credits=@credits,next_item_id=@next,sid=@sid,token=@token,established=@est,selected_stadium_id=@stadium,last_purchase_transaction_id=@txn WHERE id=@club",
            ("credits",credits),("next",nextItemId),("sid",sid),("token",token),("est",established),("stadium",selectedStadium),("txn",lastTransaction));
    private static async ValueTask DeleteRelationalOwnedRowsAsync(IUnitOfWork work)
    {
        // Membership/role FKs intentionally use NO ACTION; never rely on a parent cascade here.
        foreach(var table in new[]{"squad_tactics","squad_roles","squad_players","squad_managers"})
            await work.ExecuteAsync($"DELETE FROM ultimateteam.{table} c USING ultimateteam.squads s WHERE c.squad_id=s.id AND s.club_id=@club");
        foreach(var table in RelationalClubTables)
            await work.ExecuteAsync($"DELETE FROM ultimateteam.{table} WHERE club_id=@club");
    }
}
