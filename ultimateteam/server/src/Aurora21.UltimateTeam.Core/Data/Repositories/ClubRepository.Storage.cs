namespace Aurora21.UltimateTeam.Core.Data.Repositories;

// The service-facing club operations use only the relational schema.
public static partial class ClubRepository
{
    public static ValueTask LockClubAsync(this IUnitOfWork work) => work.LockRelationalClubAsync();
    public static ValueTask<long> CreditsAsync(this IUnitOfWork work) =>
        Read<long>(work,"credits");
    public static ValueTask<long> CreditsOfAsync(this IUnitOfWork work, long clubId) =>
        LockedCredits(work,clubId);
    public static ValueTask<long> AddCreditsToAsync(this IUnitOfWork work, long clubId, long delta) =>
        AddCredits(work,clubId,delta);
    public static ValueTask<string> SidAsync(this IUnitOfWork work) =>
        Read<string>(work,"sid");
    public static ValueTask<string> TokenAsync(this IUnitOfWork work) =>
        Read<string>(work,"token");
    public static ValueTask<long> EstablishedAsync(this IUnitOfWork work) =>
        Read<long>(work,"established");
    public static ValueTask<ClubAccount> AccountAsync(this IUnitOfWork work) =>
        Account(work);
    public static ValueTask<long> NextItemIdAsync(this IUnitOfWork work) =>
        Next(work);
    public static ValueTask<long> NextItemIdOfAsync(this IUnitOfWork work, long clubId) =>
        NextOf(work,clubId);
    public static ValueTask<long> ReserveItemIdsAsync(this IUnitOfWork work, int count) =>
        Reserve(work,count);
    public static ValueTask<long?> SelectedStadiumIdAsync(this IUnitOfWork work) =>
        Read<long?>(work,"selected_stadium_id");
    public static ValueTask<int> SetSelectedStadiumIdAsync(this IUnitOfWork work, long stadiumId) =>
        work.SetRelationalSelectedStadiumAsync(stadiumId);
    public static ValueTask<long> LastPurchaseTransactionIdAsync(this IUnitOfWork work) =>
        Read<long>(work,"last_purchase_transaction_id");
    public static ValueTask<int> SetLastPurchaseTransactionIdAsync(this IUnitOfWork work, long value) =>
        work.SetRelationalLastPurchaseTransactionAsync(value);
    public static ValueTask<long> FifaPointsAsync(this IUnitOfWork work) =>
        Points(work);
    public static ValueTask<ClubFinancialSummary> FinancialSummaryAsync(this IUnitOfWork work) =>
        work.RelationalFinancialSummaryAsync();
    public static ValueTask<long> AddFifaPointsAsync(this IUnitOfWork work, long delta) =>
        work.AddRelationalFifaPointsAsync(delta);
    public static ValueTask<string?> DailyDropDayAsync(this IUnitOfWork work) =>
        Read<string?>(work,"daily_drop_day");
    public static ValueTask<int> SetDailyDropDayAsync(this IUnitOfWork work, string? day) =>
        work.SetRelationalDailyDropDayAsync(day);
    public static ValueTask<string?> DisplayNameAsync(this IUnitOfWork work) =>
        Read<string?>(work,"display_name");
    public static ValueTask<int> SetDisplayNameIfMissingAsync(this IUnitOfWork work, string name) =>
        work.SetRelationalDisplayNameIfMissingAsync(name);
    public static ValueTask<int> SetDisplayNameAsync(this IUnitOfWork work, string name) =>
        work.SetRelationalDisplayNameAsync(name);
    public static ValueTask<bool> HasFlagAsync(this IUnitOfWork work, string flag) =>
        work.RelationalFlagAsync(flag);
    public static ValueTask<int> SetFlagAsync(this IUnitOfWork work, string flag) =>
        work.SetRelationalFlagAsync(flag);
    public static ValueTask<HashSet<string>> FlagsAsync(this IUnitOfWork work, params string[] flags) =>
        work.RelationalFlagsAsync(flags);
    public static ValueTask<int> SetFlagsAsync(this IUnitOfWork work, IEnumerable<string> flags) =>
        work.SetRelationalFlagsAsync(flags);
    public static ValueTask<List<string>> FlagsWithPrefixAsync(this IUnitOfWork work, string prefix) =>
        work.RelationalFlagsWithPrefixAsync(prefix);
    public static ValueTask<int> ClearFlagsAsync(this IUnitOfWork work, IEnumerable<string> flags) =>
        work.ClearRelationalFlagsAsync(flags);
    public static ValueTask<System.Text.Json.Nodes.JsonObject> StadiumSlotBindingsAsync(this IUnitOfWork w) => w.RelationalItems.Customisation.WireBindingsAsync(w);
    public static ValueTask<int> SetStadiumSlotBindingsAsync(this IUnitOfWork w,System.Text.Json.Nodes.JsonObject bindings) => w.RelationalItems.Customisation.SetAsync(w,bindings);
    /// <summary>Whether the last POST /match created a Draft match (a PUT carries no squad and follows it).</summary>
    public static ValueTask<bool> LastMatchCreateIsDraftAsync(this IUnitOfWork work) =>
        Read<bool>(work,"last_match_create_is_draft");
    public static ValueTask<int> SetLastMatchCreateIsDraftAsync(this IUnitOfWork work, bool value) =>
        work.SetRelationalLastMatchCreateIsDraftAsync(value);
    public static async ValueTask<DraftSlot> LastDraftSlotAsync(this IUnitOfWork work) =>
        await Read<int>(work,"last_draft_slot") == (int)DraftSlot.Online ? DraftSlot.Online : DraftSlot.SinglePlayer;
    public static ValueTask<int> SetLastDraftSlotAsync(this IUnitOfWork work, DraftSlot slot) =>
        work.SetRelationalLastDraftSlotAsync(slot);
    /// <summary>The last named Draft slot and whether the last created match was a Draft match, in one read; a missing club reads as the defaults.</summary>
    public static async ValueTask<(DraftSlot Slot, bool LastMatchWasDraft)> MatchEndDraftStateAsync(this IUnitOfWork work) =>
        (await work.QueryAsync("-- query: club.match-end-draft-state\nSELECT last_draft_slot, last_match_create_is_draft FROM ultimateteam.clubs WHERE id=@club",
            r => (Convert.ToInt32(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) == (int)DraftSlot.Online ? DraftSlot.Online : DraftSlot.SinglePlayer,
                r.GetBoolean(1)))).SingleOrDefault((DraftSlot.SinglePlayer, false));
    /// <summary>
    /// Claims one match for settlement. A concurrent server blocks on the unique key and then
    /// returns false after the winning transaction has stored its response.
    /// </summary>
    public static ValueTask<bool> TryBeginMatchSettlementAsync(this IUnitOfWork work, string matchKey) =>
        work.TryBeginRelationalMatchSettlementAsync(matchKey);
    /// <summary>The original response for an already settled match.</summary>
    public static ValueTask<System.Text.Json.Nodes.JsonObject?> MatchSettlementAsync(this IUnitOfWork work, string matchKey) =>
        work.RelationalMatchSettlementAsync(matchKey);
    public static ValueTask<System.Text.Json.Nodes.JsonObject?> LatestMatchSettlementAsync(this IUnitOfWork work, string matchKey) =>
        work.LatestRelationalMatchSettlementAsync(matchKey);
    public static ValueTask<int> CompleteMatchSettlementAsync(this IUnitOfWork work, string matchKey, System.Text.Json.Nodes.JsonObject response) =>
        work.CompleteRelationalMatchSettlementAsync(matchKey, response);
    /// <summary>Every named club on the server with its Division Rivals progress document (empty when it has none).</summary>
    public static ValueTask<List<(long PersonaId, string Name, System.Text.Json.Nodes.JsonObject Progress)>> RivalsStandingsAsync(this IUnitOfWork work) =>
        work.RelationalRivalsStandingsAsync();
    private static ValueTask<T> Read<T>(IUnitOfWork work,string column) => work.ScalarAsync<T>($"-- query: club.column.{column}\nSELECT {column} FROM ultimateteam.clubs WHERE id=@club");
    private static async ValueTask<long> LockedCredits(IUnitOfWork work,long owner) { var scoped=ClubScopedUnitOfWork.For(work,owner);await scoped.LockRelationalClubAsync();return await Read<long>(scoped,"credits"); }
    private static ValueTask<long> AddCredits(IUnitOfWork work,long owner,long delta) { return ClubScopedUnitOfWork.For(work,owner).AddRelationalCreditsAsync(delta); }
    private static async ValueTask<long> Next(IUnitOfWork work)=>(await work.NextNativeItemIdAsync()).Value;
    private static ValueTask<long> NextOf(IUnitOfWork work,long owner) { return Next(ClubScopedUnitOfWork.For(work,owner)); }
    private static async ValueTask<long> Reserve(IUnitOfWork work,int count)=>(await work.ReserveNativeItemIdsAsync(count)).First.Value;
    private static async ValueTask<long> Points(IUnitOfWork work)=>Math.Max(0,await Read<long>(work,"fifa_points"));
    private static async ValueTask<ClubAccount> Account(IUnitOfWork work) { var a=await work.RelationalAccountAsync();return new(a.ClubId,a.Established,a.Sid,a.Token,false); }
}
