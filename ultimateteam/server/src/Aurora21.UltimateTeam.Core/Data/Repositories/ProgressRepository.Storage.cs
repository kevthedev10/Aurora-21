using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

public static partial class ProgressRepository
{
    public static ValueTask<PlayerPickState?> GetPlayerPickAsync(this IUnitOfWork work,long itemId) => RelationalProgressCompatibility.Pick(work,itemId);
    public static ValueTask<int> SavePlayerPickAsync(this IUnitOfWork work,long itemId,PlayerPickState state) => RelationalProgressCompatibility.SavePick(work,itemId,state);
    public static ValueTask<int> DeletePlayerPickAsync(this IUnitOfWork work,long itemId) => RelationalProgressCompatibility.DeletePick(work,itemId);
    public static ValueTask<SortedDictionary<long,long>> ClientDataAsync(this IUnitOfWork work,string kind) => work.RelationalClientDataAsync(kind);
    /// <summary>Replaces every entry of one client-data document, in the caller's transaction.</summary>
    public static ValueTask SaveClientDataAsync(this IUnitOfWork work,string kind,IReadOnlyDictionary<long,long> values) => work.SaveRelationalClientDataAsync(kind,values);
    public static ValueTask<OnboardingState> OnboardingAsync(this IUnitOfWork work) => work.RelationalOnboardingAsync();
    public static ValueTask<int> SaveOnboardingAsync(this IUnitOfWork work,OnboardingState state) => work.SaveRelationalOnboardingAsync(state,work.RelationalItems.Mapper.Game.Rules.Club.RenderedDefaultActiveItems(work.ClubId));
    public static ValueTask<HashSet<string>> SbcCompletedAsync(this IUnitOfWork work) => work.RelationalSbcCompletedAsync();
    /// <summary>Historical completion counts, including prior rounds of repeatable sets.</summary>
    public static async ValueTask<Dictionary<string, long>> SbcCompletionCountsAsync(this IUnitOfWork work) =>
        (await work.QueryAsync("SELECT challenge_key, completion_count FROM ultimateteam.sbc_completed WHERE club_id=@club",
            r => (Key: r.GetString(0), Count: r.GetInt64(1))))
            .ToDictionary(row => row.Key, row => row.Count, StringComparer.Ordinal);

    // Caller holds the club lock. Keep prior completions rather than deleting history between rounds.
    public static ValueTask<int> RecordRepeatableSbcCompletionAsync(this IUnitOfWork work, string challengeKey) =>
        work.ExecuteAsync("""
            INSERT INTO ultimateteam.sbc_completed(club_id,challenge_key,completion_count) VALUES(@club,@key,1)
            ON CONFLICT(club_id,challenge_key) DO UPDATE
            SET completion_count=ultimateteam.sbc_completed.completion_count+1
            """, ("key", challengeKey));

    /// <summary>Claims a non-repeatable completion. False when already completed.</summary>
    public static ValueTask<bool> TryMarkSbcCompletedAsync(this IUnitOfWork work,string challengeKey) => work.TryMarkRelationalSbcCompletedAsync(challengeKey);
    public static ValueTask<long> SbcSquadCountAsync(this IUnitOfWork work,long challengeId) => work.RelationalSbcSquadCountAsync(challengeId);
    public static ValueTask<int> SetSbcSquadCountAsync(this IUnitOfWork work,long challengeId,long placed) => work.SetRelationalSbcSquadCountAsync(challengeId,placed);
    public static ValueTask<System.Text.Json.Nodes.JsonObject> SeasonProgressAsync(this IUnitOfWork work,string competition) => work.RelationalSeasonProgressAsync(competition);
    public static ValueTask<int> SaveSeasonProgressAsync(this IUnitOfWork work,string competition,System.Text.Json.Nodes.JsonObject data) => work.SaveRelationalSeasonProgressAsync(competition,data);
    public static ValueTask<System.Text.Json.Nodes.JsonObject?> DraftRunAsync(this IUnitOfWork work,DraftSlot slot=DraftSlot.SinglePlayer) => work.RelationalItems.Drafts.ReadAsync(work,slot);
    public static ValueTask<int> SaveDraftRunAsync(this IUnitOfWork work,System.Text.Json.Nodes.JsonObject run,DraftSlot slot=DraftSlot.SinglePlayer) => work.RelationalItems.Drafts.SaveAsync(work,run,slot);
    public static ValueTask<int> ClearDraftRunAsync(this IUnitOfWork work,DraftSlot slot=DraftSlot.SinglePlayer) => work.RelationalItems.Drafts.ClearAsync(work,slot);
    /// <summary>The club's objectives, with the Season XP and Season Pass claims of the season in force (migration 166).</summary>
    public static ValueTask<ObjectivesState> ObjectivesAsync(this IUnitOfWork work, ObjectiveRules rules) =>
        RelationalProgressCompatibility.Objectives(work, rules.SeasonAt(work.NowSeconds).Id);
    /// <summary>The club's objectives, with the Season XP and Season Pass claims of the given season.</summary>
    public static ValueTask<ObjectivesState> ObjectivesAsync(this IUnitOfWork work, long season) => RelationalProgressCompatibility.Objectives(work, season);
    public static ValueTask<int> SaveObjectivesAsync(this IUnitOfWork work,ObjectivesState state) => RelationalProgressCompatibility.SaveObjectives(work,state);
}
