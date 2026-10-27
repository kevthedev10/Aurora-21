namespace Aurora21.UltimateTeam.Core.Data.Records;

public sealed record RelationalAccount(long ClubId, long PersonaId, long NucleusId, string? DisplayName,
    long Credits, long FifaPoints, NativeItemId LastAllocatedNativeItemId, string Sid, string Token,
    long Established, long? SelectedStadiumId, long LastPurchaseTransactionId, string? DailyDropDay,
    bool LastMatchCreateIsDraft, DraftSlot LastDraftSlot, DateTime CreatedAt);

public sealed record RelationalClubOpenResult(RelationalAccount Account, bool Created);

/// <summary>Native item numbers reserved atomically; PostgreSQL allocates internal item keys separately.</summary>
public readonly record struct NativeItemIdRange(NativeItemId First, int Count)
{
    public NativeItemId Last => new(checked(First.Value + Count - 1));
}
