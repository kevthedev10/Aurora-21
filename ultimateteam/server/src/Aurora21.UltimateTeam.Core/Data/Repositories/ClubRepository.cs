namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>The club row an open resolves: stable session values and whether this open created it.</summary>
public sealed record ClubAccount(long ClubId, long Established, string Sid, string Token, bool Created);

/// <summary>The balances shown together in the client credits document.</summary>
public sealed record ClubFinancialSummary(long Credits, long FifaPoints, int UnopenedPacks);

/// <summary>Service-facing account operations backed by the relational club table.</summary>
public static partial class ClubRepository
{
    public const long FirstItemId = 900000000000;

    /// <summary>
    /// Each club issues item ids from its own band, so no two clubs share one: FIFA caches items by id alone, and in an
    /// online match a shared id let the opponent's card overwrite the player's own (migration 045).
    /// </summary>
    public const long ItemIdBandSize = 10_000_000;

    /// <summary>The first item id of a club's band; club 1 starts at <see cref="FirstItemId"/>.</summary>
    public static long FirstItemIdOf(long clubId) => FirstItemId + (clubId - 1) * ItemIdBandSize;

    /// <summary>The top ids of each band hold the club's virtual cosmetics; ordinary items are numbered below them.</summary>
    public const long VirtualItemIdReserve = 16;

    /// <summary>A virtual cosmetic's id in a club's band: reserved slot 1 to <see cref="VirtualItemIdReserve"/>.</summary>
    public static long VirtualItemIdOf(long clubId, int slot) =>
        slot is >= 1 and <= (int)VirtualItemIdReserve ? FirstItemIdOf(clubId + 1) - VirtualItemIdReserve + slot
            : throw new ArgumentOutOfRangeException(nameof(slot));

    /// <summary>
    /// The first id of the market range: above the club bands (club 5001 would reach it) and Draft's ids (910e9),
    /// below vanity items (970e9).
    /// </summary>
    public const long FirstMarketItemId = 950000000000;

    /// <summary>
    /// A listing's card as other clubs see it, and the id its buyer's copy keeps. FIFA caches items by id alone: a buy
    /// answered with a new id lost the card it was showing ("NOT FOUND", live 10-05), and the seller's id would come
    /// back after a club reset. One id per trade is unique across clubs.
    /// </summary>
    public static long MarketItemIdOf(long tradeId) => FirstMarketItemId + tradeId;
}
