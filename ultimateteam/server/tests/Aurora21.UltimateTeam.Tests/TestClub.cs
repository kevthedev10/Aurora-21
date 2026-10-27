using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data.Repositories;

namespace Aurora21.UltimateTeam.Tests;

/// <summary>
/// The club state a scenario test arranges or asserts, read and written through one pooled unit of work per
/// call. It keeps the tests readable now that production passes the unit of work explicitly; it is a test
/// convenience, not a production API.
/// </summary>
public sealed class TestClub : IDisposable
{
    private readonly ClubUnitOfWorkRunner _runner;
    private readonly ClubAccount _account;
    private readonly NpgsqlDataSource? _owned;
    private readonly GameContext? _game;

    public TestClub(GameContext game) : this(game.UnitOfWork, game.Account, owned: null)
    {
        Identity = new ClubIdentity(game.Config.PersonaId, game.Config.NucleusId);
        _game = game;
    }

    private TestClub(ClubUnitOfWorkRunner runner, ClubAccount account, NpgsqlDataSource? owned)
    {
        _runner = runner;
        _account = account;
        _owned = owned;
    }

    /// <summary>Deletes a persona's club on its own pool, as the reset command does.</summary>
    public static bool DeleteClub(string connectionString, long personaId)
    {
        using var source = NpgsqlDataSource.Create(connectionString);
        var runner = new ClubUnitOfWorkRunner(source, TimeProvider.System, deadlockRetries: 0, NullLogger.Instance);
        return ClubOpen.DeleteClubAsync(runner, personaId).GetAwaiter().GetResult();
    }

    public long ClubId => _account.ClubId;

    public ClubIdentity Identity { get; private init; } = new(0, 0);

    public bool CreatedClub => _account.Created;

    public long Established() => Run(uow => uow.EstablishedAsync());

    public string Sid() => Run(uow => uow.SidAsync());

    public string Token() => Run(uow => uow.TokenAsync());

    public void Dispose() => _owned?.Dispose();

    /// <summary>Runs arbitrary repository work in one committed unit, as a service would.</summary>
    public T Run<T>(Func<IUnitOfWork, ValueTask<T>> work) =>
        _runner.RunAsync(ClubId, "test", uow => work(uow), CancellationToken.None).GetAwaiter().GetResult();

    public void Run(Func<IUnitOfWork, ValueTask> work) =>
        _runner.RunAsync(ClubId, "test", uow => work(uow), CancellationToken.None).GetAwaiter().GetResult();

    // ---- account ------------------------------------------------------------

    public long Credits() => Run(uow => uow.CreditsAsync());

    public long AddCredits(long delta) => Run(uow => uow.AddCreditsToAsync(uow.ClubId, delta));

    public long FifaPoints() => Run(uow => uow.FifaPointsAsync());

    public long AddFifaPoints(long delta) => Run(uow => uow.AddFifaPointsAsync(delta));

    public long NextItemId() => Run(uow => uow.NextItemIdAsync());

    public long? SelectedStadiumId() => Run(uow => uow.SelectedStadiumIdAsync());

    public long LastPurchaseTransactionId() => Run(uow => uow.LastPurchaseTransactionIdAsync());

    public bool HasFlag(string flag) => Run(uow => uow.HasFlagAsync(flag));

    public void SetLastMatchCreateIsDraft(bool value) => Run(uow => uow.SetLastMatchCreateIsDraftAsync(value));

    public void SetAccount(long credits, long nextItemId, string sid, string token, long established, long? selectedStadium, long lastTransaction) =>
        Run(uow => uow.SetAccountAsync(credits, nextItemId, sid, token, established, selectedStadium, lastTransaction));

    public void ClearClub(long initialCredits) => Run(uow => uow.ClearClubAsync(initialCredits));

    // ---- items and squads ---------------------------------------------------

    public List<JsonObject> ListItems(string? pile = null) => Run(uow => uow.ListItemsAsync(pile));

    public JsonObject? GetItem(long id) => Run(uow => uow.GetItemAsync(id));

    public bool ItemExists(long id) => Run(uow => uow.ItemExistsAsync(id));

    public void SaveItem(JsonObject item) => Run(uow => uow.SaveItemAsync(item));

    public void SaveItem(long id, JsonObject item) => Run(uow => uow.SaveItemAsync(id, item));

    public int DeletePile(string pile) => Run(uow => uow.DeletePileAsync(pile));

    public List<(long Id, string? Pile)> ItemsWithResource(long resourceId) => Run(uow => uow.ItemsWithResourceAsync(resourceId));

    public List<JsonObject> ListSquads() => Run(uow => uow.ListSquadsAsync());

    public JsonObject? GetSquad(long id) => Run(uow => uow.GetSquadAsync(id));

    public long ActiveSquadId() => Run(uow => uow.ActiveSquadIdAsync());

    // ---- packs, progress and picks ------------------------------------------

    public List<long> UnopenedPackIds() => Run(uow => uow.UnopenedPackIdsAsync());

    public long AddUnopenedPack(long packId, string source, long? created = null) => Run(uow => uow.AddUnopenedPackAsync(packId, source, created));

    public bool TakeUnopenedPack(long packId) => Run(uow => uow.TakeUnopenedPackAsync(packId));

    /// <summary>The season in force for the game's rules (the default season without a game).</summary>
    private long Season(IUnitOfWork uow) => _game?.Rules.Objectives.SeasonAt(uow.NowSeconds).Id ?? DefaultSeasonProgress.DefaultSeason;

    public ObjectivesState Objectives() => Run(uow => uow.ObjectivesAsync(Season(uow)));

    public void AddObjectivesXp(long xp) => Run(uow => uow.AddObjectivesXpAsync(Season(uow), xp));

    public HashSet<string> SbcCompleted() => Run(uow => uow.SbcCompletedAsync());

    public bool TryMarkSbcCompleted(string challengeKey) => Run(uow => uow.TryMarkSbcCompletedAsync(challengeKey));

    public long SbcSquadCount(long challengeId) => Run(uow => uow.SbcSquadCountAsync(challengeId));

    public JsonObject SeasonProgress(string competition) => Run(uow => uow.SeasonProgressAsync(competition));

    public void SaveSeasonProgress(string competition, JsonObject data) => Run(uow => uow.SaveSeasonProgressAsync(competition, data));

    public JsonObject? DraftRun(DraftSlot slot = DraftSlot.SinglePlayer) => Run(uow => uow.DraftRunAsync(slot));

    public void SaveDraftRun(JsonObject run, DraftSlot slot = DraftSlot.SinglePlayer) => Run(uow => uow.SaveDraftRunAsync(run, slot));

    public PlayerPickState? GetPlayerPick(long itemId) => Run(uow => uow.GetPlayerPickAsync(itemId));

    public OnboardingState Onboarding() => Run(uow => uow.OnboardingAsync());

    public SortedDictionary<long, long> ClientData(string kind) => Run(uow => uow.ClientDataAsync(kind));

    // ---- market -------------------------------------------------------------

    public MarketListing? MarketListing(long tradeId) => Run(uow => uow.MarketListingAsync(tradeId));

    public long CreditsOf(long clubId) => Run(uow => uow.CreditsOfAsync(clubId));

    public JsonObject? ItemOf(long clubId, long itemId) => Run(uow => uow.ItemOfAsync(clubId, itemId));

    public bool DeleteItem(long id) => Run(uow => uow.DeleteItemAsync(id));

    public MarketListing? LiveMarketListingForItem(long itemId) => Run(uow => uow.LiveMarketListingForItemAsync(itemId));

    public List<MarketListing> ActiveMarketListings() => Run(uow => uow.ActiveMarketListingsAsync());

    public List<MarketListing> OwnMarketListings() => Run(uow => uow.OwnMarketListingsAsync());

    public int CountActiveMarketListings(string? kind = null) => Run(uow => uow.CountActiveMarketListingsAsync(kind));

    public List<long> MarketWatchIds() => Run(uow => uow.MarketWatchIdsAsync());

    public void WatchMarketListing(long tradeId, long? clubId = null) => Run(uow => uow.WatchMarketListingAsync(tradeId, clubId));

    public void SetMarketBid(long tradeId, long amount, long bidderClubId) => Run(uow => uow.SetMarketBidAsync(tradeId, amount, bidderClubId));

    public long InsertMarketListing(string kind, string sellerName, long? sellerItemId, long resourceId, JsonObject item, long rating,
        long startingBid, long buyNow, long createdAt, long endTime) =>
        Run(uow => uow.InsertMarketListingAsync(kind, sellerName, sellerItemId, resourceId, item, rating, startingBid, buyNow, createdAt, endTime));
}

/// <summary>Gives every test context the club helper as <c>game.Store()</c>.</summary>
public static class TestClubExtensions
{
    public static TestClub Store(this GameContext game) => new(game);
}
