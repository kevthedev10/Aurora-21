using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Aurora21.UltimateTeam.Core.Services.Store;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Market;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Protocol.Fire2;
using Aurora21.UltimateTeam.Server.Blaze;

namespace Aurora21.UltimateTeam.Tests;

/// <summary>
/// Synchronous test conveniences over the async router, player registry and services, so scenario tests read top
/// to bottom. Waiting is safe here: xUnit runs tests without a synchronization context, and a failure rethrows the
/// original exception rather than an AggregateException.
/// </summary>
public static class SyncTestExtensions
{
    public static FutResponse Handle(this FutRouter router, FutRequest request) =>
        router.HandleAsync(request).AsTask().GetAwaiter().GetResult();

    public static PlayerLease Acquire(this PlayerRegistry players, PlayerIdentity player) =>
        players.AcquireAsync(player).GetAwaiter().GetResult();

    /// <summary>Runs service work in one pooled unit of work of the context's club and commits it.</summary>
    public static T InUnit<T>(this GameContext game, Func<UnitOfWork, ValueTask<T>> work) =>
        game.RunAsync("test", work, CancellationToken.None).GetAwaiter().GetResult();

    public static void InUnit(this GameContext game, Func<UnitOfWork, ValueTask> work) =>
        game.RunAsync("test", work, CancellationToken.None).GetAwaiter().GetResult();

    public static JsonArray Redeem(this PlayerPickService picks, long tokenId) =>
        picks.RedeemAsync(tokenId).GetAwaiter().GetResult();

    public static JsonObject Select(this PlayerPickService picks, long tokenId, JsonNode? selection) =>
        picks.SelectAsync(tokenId, selection).GetAwaiter().GetResult();

    public static JsonObject Campaign(this ObjectivesService objectives) =>
        objectives.CampaignAsync().GetAwaiter().GetResult();

    public static JsonObject CategoryDetails(this ObjectivesService objectives, long categoryId) =>
        objectives.CategoryDetailsAsync(categoryId).GetAwaiter().GetResult();

    public static JsonObject ClaimLevel(this ObjectivesService objectives, long level) =>
        objectives.ClaimLevelAsync(level).GetAwaiter().GetResult();

    public static JsonObject Claim(this ObjectivesService objectives, long? groupId, long? objectiveId) =>
        (JsonObject)objectives.ClaimAsync(groupId, objectiveId).GetAwaiter().GetResult().Body!;

    public static JsonObject SetsResponse(this SbcService sbc) =>
        sbc.SetsResponseAsync().GetAwaiter().GetResult();

    public static JsonObject ChallengesResponse(this SbcService sbc, int setId) =>
        sbc.ChallengesResponseAsync(setId).GetAwaiter().GetResult();

    public static List<JsonObject> GrantChallengeRewards(this SbcService sbc, SbcChallenge challenge) =>
        sbc.GrantChallengeRewardsAsync(challenge).GetAwaiter().GetResult();

    public static JsonArray GrantAward(this DraftService draft, DraftSlot slot) =>
        draft.GrantAwardAsync(slot).GetAwaiter().GetResult();

    public static JsonObject? EndMatch(this DraftService draft, DraftSlot slot, JsonNode? payload) =>
        draft.EndMatchAsync(slot, payload).GetAwaiter().GetResult();

    public static JsonObject SaveSquad(this ClubInventoryService inventory, JsonNode? payload) =>
        inventory.SaveSquadAsync(payload).GetAwaiter().GetResult();

    public static JsonObject Hub(this ClubPresentationService club) =>
        club.HubAsync().GetAwaiter().GetResult();

    public static IReadOnlyList<ClubRanking> Rankings(this LeaderboardService leaderboards, GameContext game, int? limit = null) =>
        game.InUnit(uow => leaderboards.RankingsAsync(uow, limit));

    public static List<JsonObject> MatchActives(this SquadBattlesService squadBattles, GameContext game, bool opponent) =>
        game.InUnit(uow => squadBattles.MatchActivesAsync(uow, opponent));

    public static BlazeReply Respond(this BlazeResponses responses, Fire2Frame frame) =>
        responses.RespondAsync(frame, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public static JsonObject MakePlayer(this ItemFactory items, long resourceId, string pile = "club", long loanGames = 0) =>
        items.Game.InUnit(uow => items.MakePlayerAsync(uow, resourceId, pile, loanGames));

    public static JsonObject MakePlayerPick(this ItemFactory items, long minRating, long choiceCount = 4, string pile = "unassigned",
        long sourcePackId = 0, long previewResourceId = 0) =>
        items.Game.InUnit(uow => items.MakePlayerPickAsync(uow, minRating, choiceCount, pile, sourcePackId, previewResourceId));

    public static HashSet<long> OwnedPlayerResourceIds(this ClubInventoryService inventory) =>
        inventory.Game.InUnit(inventory.OwnedPlayerResourceIdsAsync);

    public static bool WouldDuplicateOwnedPlayer(this ClubInventoryService inventory, long itemId) =>
        inventory.Game.InUnit(uow => inventory.WouldDuplicateOwnedPlayerAsync(uow, itemId));

    public static bool UpdatePile(this ClubInventoryService inventory, long itemId, string pile) =>
        inventory.Game.InUnit(uow => inventory.UpdatePileAsync(uow, itemId, pile));

    public static void RepairEmptyActiveSquad(this ClubInventoryService inventory) =>
        inventory.Game.InUnit(inventory.RepairEmptyActiveSquadAsync);

    public static JsonObject CreatePack(this PackOpeningService packs, long packId = 1) =>
        packs.Game.InUnit(uow => packs.CreatePackAsync(uow, packId));

    public static List<JsonObject> ActiveClubItems(this ClubPresentationService club, ClubSnapshot? snapshot = null) =>
        club.Game.InUnit(uow => club.ActiveClubItemsAsync(uow, snapshot));

    public static GameContext ForPlayer(this GameContext game, PlayerIdentity player) =>
        game.ForPlayerAsync(player).GetAwaiter().GetResult();

    /// <summary>Builds the services for a context, running club maintenance, as a session open does.</summary>
    public static FutServices Services(this GameContext game) => FutServices.CreateAsync(game).GetAwaiter().GetResult();

    // Market and onboarding conveniences, moved out of the production services (issues.md M-5).
    public static JsonObject Search(this SharedMarketService market, QueryValues query) => market.SearchAsync(query).AsTask().GetAwaiter().GetResult();
    public static JsonObject TradePile(this SharedMarketService market) => market.TradePileAsync().AsTask().GetAwaiter().GetResult();
    public static JsonObject Watchlist(this SharedMarketService market, QueryValues? query = null) => market.WatchlistAsync(query).AsTask().GetAwaiter().GetResult();
    public static JsonObject Status(this SharedMarketService market, IEnumerable<long> tradeIds) => market.StatusAsync(tradeIds).AsTask().GetAwaiter().GetResult();
    public static MarketCounters Counters(this SharedMarketService market) => market.CountersAsync().AsTask().GetAwaiter().GetResult();
    public static JsonObject List(this SharedMarketService market, long itemId, long startingBid, long buyNow, long duration) =>
        market.ListAsync(itemId, startingBid, buyNow, duration).AsTask().GetAwaiter().GetResult();
    public static JsonObject ClearTrade(this SharedMarketService market, long tradeId) => market.ClearTradeAsync(tradeId).AsTask().GetAwaiter().GetResult();
    public static JsonObject ClearSold(this SharedMarketService market) => market.ClearSoldAsync().AsTask().GetAwaiter().GetResult();
    public static void Watch(this SharedMarketService market, long tradeId) => market.WatchAsync(tradeId).GetAwaiter().GetResult();
    public static void Unwatch(this SharedMarketService market, long tradeId) => market.UnwatchAsync(tradeId).GetAwaiter().GetResult();
    public static void Offer(this SharedMarketService market, long tradeId, long amount) => market.OfferAsync(tradeId, amount).AsTask().GetAwaiter().GetResult();
    public static void PlaceBid(this SharedMarketService market, long tradeId, long amount) => market.PlaceBidAsync(tradeId, amount).AsTask().GetAwaiter().GetResult();
    public static void BuyNow(this SharedMarketService market, long tradeId, long offered = 0) => market.BuyNowAsync(tradeId, offered).AsTask().GetAwaiter().GetResult();
    public static int ExpireDue(this SharedMarketService market) => market.ExpireDueAsync().AsTask().GetAwaiter().GetResult();
    public static int SeedAiListings(this SharedMarketService market) => market.SeedAiListingsAsync().AsTask().GetAwaiter().GetResult();
    internal static MarketListingPage SearchListings(this SharedMarketService market, QueryValues query) =>
        market.Game.ReadAsync(uow => market.SearchListingsAsync(uow, query), CancellationToken.None).GetAwaiter().GetResult();
    public static bool IsComplete(this OnboardingService onboarding) =>
        onboarding.Game.ReadAsync(OnboardingService.IsCompleteAsync, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Fills every empty starter and substitute slot of the club's active squad with a new card, as a played club's squad
    /// is: a squad with an empty matchday slot is never a Squad Battles opponent.
    /// </summary>
    public static void FillMatchdaySquad(this GameContext game, FutServices fut) =>
        game.InUnit(async uow =>
        {
            var squadId = await uow.ActiveSquadIdAsync();
            var squad = (await uow.GetSquadAsync(squadId))!;
            var slots = squad.Arr("players")!;
            var filled = slots.OfType<JsonObject>().Select((entry, position) => (entry, position))
                .Where(pair => (pair.entry.Obj("itemData") ?? pair.entry).Int("id", pair.entry.Int("itemId")) > 0)
                .Select(pair => pair.entry.Int("index", pair.position)).ToHashSet();
            var spare = new ClubMaintenanceService(fut).InitialPlayerResourceIds(60).Skip(MatchService.MatchdaySlots).GetEnumerator();
            for (var index = 0; index < MatchService.MatchdaySlots; index++)
            {
                if (filled.Contains(index) || !spare.MoveNext())
                {
                    continue;
                }
                var item = await fut.Items.MakePlayerAsync(uow, spare.Current, "club");
                var slot = new JsonObject { ["index"] = index, ["itemId"] = item.Int("id") };
                var existing = slots.OfType<JsonObject>().Select((entry, position) => (entry, position)).FirstOrDefault(pair => pair.entry.Int("index", pair.position) == index);
                if (existing.entry is not null)
                {
                    slots[existing.position] = slot;
                }
                else
                {
                    slots.Add(slot);
                }
            }
            await uow.SaveSquadAsync(squadId, squad.Text("squadName"), true, squad);
        });
}
