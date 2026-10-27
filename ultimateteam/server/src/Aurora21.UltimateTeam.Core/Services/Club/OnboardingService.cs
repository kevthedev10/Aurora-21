using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Club;

/// <summary>
/// FIFA 21's first-run onboarding (ultimateteam/onboarding.md): the country and club screens that create a club
/// (hosted clubs and deliberately reset local clubs), the starter loan, client-data progress markers, kits and badge.
/// </summary>
public sealed class OnboardingService
{
    // Client-data documents are key/value pairs; these are the keys the onboarding flow reads.
    private const long OnboardingStepKey = 0;
    private const long OnboardingCompletedStep = 3;
    private const long HomeKitKey = 1;
    private const long AwayKitKey = 2;
    private const long BadgeKey = 3;
    private const long LoanRewardKey = 0;
    private const long SquadTutorialKey = 5;

    private const string HomeKitState = "activeHomeKit";
    private const string AwayKitState = "activeAwayKit";
    private const string BadgeState = "activeBadge";

    /// <summary>The longest club name and abbreviation kept (the onboarding table's limits); longer typed text is cut.</summary>
    private const int ClubNameMaxLength = 30;
    private const int ClubAbbrMaxLength = 5;

    /// <summary>Set once the player has named a club FIFA created (its club-name screen); FIFA is not offered the screen again.</summary>
    internal const string ClubNamedFlag = "native_club_named_v1";

    private readonly FutServices _fut;
    private readonly ILogger _log;

    private IReadOnlyList<IReadOnlyList<(long ResourceId, long Footballer)>>? _loanPools;

    public OnboardingService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Onboarding");
    }

    internal GameContext Game => _fut.Game;
    private OnboardingRules Rules => Game.Rules.Onboarding;
    private LoanRules Loans => Rules.Loans;

    /// <summary>A persisted badge selection also counts as completion for older profiles.</summary>
    public static async ValueTask<bool> IsCompleteAsync(IUnitOfWork uow) => IsComplete(await uow.OnboardingAsync());

    private static bool IsComplete(OnboardingState state) => state.Complete || state.BadgeChoice != 0;

    /// <summary>
    /// Read-modify-write operations take the club lock before reading, so concurrent
    /// onboarding writes cannot lose each other's fields.
    /// </summary>
    private static ValueTask LockRelationalAsync(IUnitOfWork uow) => uow.LockClubAsync();

    // ---- club creation: FIFA's country and club screens ---------------------

    /// <summary>
    /// Whether this club waits for FIFA to create it (the native profile or a deliberate panel reset, until CreateUser):
    /// FIFA answers a 465 on its bulk start-up document with its country and club screens (CardsDLL 0x37E0A0: 465 is
    /// native result 1, which raises GetHistorical and then createClub).
    /// </summary>
    public async ValueTask<bool> AwaitingCreationAsync(IUnitOfWork uow)
    {
        // Read persisted state so a panel reset reaches every already-open context.
        var flags = await uow.FlagsAsync(ClubMaintenanceService.RelationalInitializedFlag, ClubRepository.NativeCreationRequiredFlag);
        return !flags.Contains(ClubMaintenanceService.RelationalInitializedFlag)
            && (Game.NativeClubCreation || flags.Contains(ClubRepository.NativeCreationRequiredFlag));
    }

    public async Task<bool> IsAwaitingCreationAsync(CancellationToken cancellationToken = default) =>
        await Game.ReadAsync(uow => AwaitingCreationAsync(uow), cancellationToken);

    /// <summary>Includes local clubs reset into native onboarding, and their idempotent CreateUser replies afterward.</summary>
    public async Task<bool> UsesNativeCreationAsync(CancellationToken cancellationToken = default) =>
        Game.NativeClubCreation || await Game.ReadAsync(async uow =>
            (await uow.FlagsAsync(ClubMaintenanceService.NativeClubFlag, ClubRepository.NativeCreationRequiredFlag)).Count > 0,
            cancellationToken);

    /// <summary>
    /// GET user/historical (GetHistorical, CardsDLL 0x365520): the club name and abbreviation the club screen starts
    /// from, and no returning-user rewards.
    /// </summary>
    public JsonObject Historical() => TemplateLibrary.Core.Object("onboarding/historical", new TemplateArgs
    {
        ["clubName"] = Game.Config.ClubName,
        ["clubAbbr"] = Game.Config.ClubAbbr,
    });

    /// <summary>
    /// user/action/ONBOARDING_COUNTRY_&lt;n&gt;_SELECTED: the country screen's choice, a 1-based position in its list of
    /// nations (rules/onboarding.json countryNations). The club it later creates draws its starter squad from that nation.
    /// </summary>
    public async ValueTask<FutResult> SelectCountryAsync(long position, CancellationToken cancellationToken = default)
    {
        if (Rules.CountryNation(position) is not { } nation)
        {
            return FutResult.Error(404, "unknown onboarding country");
        }
        return await Game.RunAsync("select-onboarding-country", async uow =>
        {
            await LockRelationalAsync(uow);
            if (!await AwaitingCreationAsync(uow))
            {
                // The country only shapes a new club's starter squad; once the club exists it changes nothing.
                _log.LogInformation("ONBOARDING country {Position} after the club was created; nothing to change", position);
                return FutResult.Ok(new JsonObject());
            }
            await uow.SaveOnboardingAsync((await uow.OnboardingAsync()) with { NationId = nation });
            _log.LogInformation("ONBOARDING country {Position} nation={Nation}", position, nation);
            return FutResult.Ok(new JsonObject());
        }, cancellationToken);
    }

    /// <summary>
    /// CreateUser (POST user, CardsDLL 0x310D60): creates the club with the name and abbreviation the player typed, a starter
    /// squad from the chosen country (rules/onboarding.json starter) and the starter consumables, in one transaction. The
    /// reply (parser 0x3111D0) carries the new user, its squad and the granted starter pack. A repeated CreateUser gets
    /// the club it created again; nothing is granted twice.
    /// </summary>
    public async ValueTask<FutResult> CreateClubAsync(JsonNode? payload, CancellationToken cancellationToken = default)
    {
        if (payload is not JsonObject request)
        {
            return FutResult.Error(400, "expected a create-user object");
        }
        var name = ClubText(request.Get("clubName"), Game.Config.ClubName, ClubNameMaxLength);
        var abbreviation = ClubText(request.Get("clubAbbr"), Game.Config.ClubAbbr, ClubAbbrMaxLength);
        var result = await Game.RunAsync("create-club", async uow =>
        {
            await uow.LockClubAsync();
            if (!await AwaitingCreationAsync(uow))
            {
                return FutResult.Ok(await CreationResponseAsync(uow, await StarterItemsAsync(uow)));
            }
            var state = await uow.OnboardingAsync();
            var nation = state.NationId;
            if (nation == 0)
            {
                // The country screen comes first; without its choice the screen's default (its first nation) applies.
                nation = Rules.CountryNations[0];
                _log.LogWarning("ONBOARDING CreateUser before a country was chosen; using nation {Nation}", nation);
            }
            var players = StarterPlayerIds(nation);
            var manager = StarterManagerId(nation);
            var items = new List<JsonObject>();
            var first = await uow.ReserveItemIdsAsync(players.Count + (manager is null ? 0 : 1));
            items.AddRange(players.Select((resourceId, index) => Untradeable(_fut.Items.BuildPlayer(resourceId, first + index, "club"))));
            if (manager is { } managerId)
            {
                items.Add(Untradeable(_fut.Items.BuildManager(managerId, first + players.Count, "club")));
            }
            await uow.SaveItemsAsync(items);
            var squad = new JsonObject
            {
                ["id"] = 1,
                ["squadId"] = 1,
                ["name"] = name,
                ["squadName"] = name,
                ["formation"] = Rules.Starter.Formation,
                ["rating"] = 0,
                ["chemistry"] = 0,
                ["active"] = true,
                ["players"] = SquadDocuments.Lineup(items.Take(players.Count)),
                ["kicktakers"] = new JsonArray(),
            };
            await uow.SaveSquadAsync(1, name, true, squad);
            await uow.SaveOnboardingAsync(state with { NationId = nation, ClubName = name, ClubAbbr = abbreviation });
            await new ClubMaintenanceService(_fut).CompleteNativeCreationAsync(uow);
            await uow.ClearRelationalFlagsAsync([ClubRepository.NativeCreationRequiredFlag]);
            // The reply's user already carries the chosen name; the persona stays the player's recorded name.
            Game.UseChosenClubIdentity(name, abbreviation);
            _log.LogInformation("ONBOARDING club created: nation={Nation} players={Players} manager={Manager}", nation, players.Count, manager ?? 0);
            return FutResult.Ok(await CreationResponseAsync(uow, items));
        }, cancellationToken);
        return result;
    }

    private async ValueTask<JsonObject> CreationResponseAsync(IUnitOfWork uow, IEnumerable<JsonObject> starterPack) =>
        TemplateLibrary.Core.Object("onboarding/create-user", new TemplateArgs
        {
            ["login"] = await _fut.Club.NativeUserAsync(uow),
            ["squad"] = await _fut.Club.NativeSquadAsync(uow),
            ["starterPack"] = new JsonArray(starterPack.Select(item => (JsonNode?)(item.Text("itemType") == "manager"
                ? _fut.Native.ManagerItem(item) : _fut.Native.PlayerItem(item))).ToArray()),
        });

    /// <summary>
    /// Whether FIFA may show this club its club-name screen: the user document's clubNameChangeAllowed. FIFA sends CreateUser
    /// straight after the country screen, with GetHistorical's default name, so a club FIFA created is named here, after
    /// onboarding. The user parser (CardsDLL 0x329190) stores the flag at user +0x64, and the onboarding navigator
    /// (0x40D10, at 0x41190) opens GoToChangeClubName when it is 1. Offered until the club has been named once.
    /// </summary>
    public async ValueTask<bool> ClubNameChangeAllowedAsync(IUnitOfWork uow)
    {
        var flags = await uow.FlagsAsync(ClubMaintenanceService.NativeClubFlag, ClubNamedFlag);
        return flags.Contains(ClubMaintenanceService.NativeClubFlag) && !flags.Contains(ClubNamedFlag);
    }

    /// <summary>
    /// ChangeClubName, the club-name screen's Apply: <c>/user/club?profaneNameChange=true|false</c> with
    /// <c>{clubName, clubAbbr}</c> (CardsDLL URI builder 0x31E5A0, serializer 0x31E660). Keeps the typed name and
    /// abbreviation as CreateUser does, once. The reply parser (0x31E9F0) reads only an optional
    /// dynamicObjectivesUpdates, so the answer is an empty object.
    /// </summary>
    public async ValueTask<FutResult> RenameClubAsync(JsonNode? payload, string? profaneNameChange, CancellationToken cancellationToken = default)
    {
        if (payload is not JsonObject request)
        {
            return FutResult.Error(400, "expected a club name");
        }
        var name = ClubText(request.Get("clubName"), string.Empty, ClubNameMaxLength);
        var abbreviation = ClubText(request.Get("clubAbbr"), string.Empty, ClubAbbrMaxLength);
        if (name.Length == 0 || abbreviation.Length == 0)
        {
            return FutResult.Error(400, "expected a club name and abbreviation");
        }
        var renamed = false;
        var result = await Game.RunAsync("rename-club", async uow =>
        {
            await uow.LockClubAsync();
            if (!await ClubNameChangeAllowedAsync(uow))
            {
                _log.LogWarning("ONBOARDING club rename refused: this club has no rename left");
                return FutResult.Error(403, "club name change not allowed");
            }
            await uow.SaveOnboardingAsync((await uow.OnboardingAsync()) with { ClubName = name, ClubAbbr = abbreviation });
            await uow.SetFlagAsync(ClubNamedFlag);
            renamed = true;
            return FutResult.Ok(new JsonObject());
        }, cancellationToken);
        if (renamed)
        {
            Game.UseChosenClubIdentity(name, abbreviation);
            _log.LogInformation("ONBOARDING club named: length={Length} abbreviation length={AbbreviationLength} profaneNameChange={Profane}",
                name.Length, abbreviation.Length, profaneNameChange ?? "(none)");
        }
        return result;
    }

    /// <summary>The created club's starter squad as it stands (the reply to a repeated CreateUser).</summary>
    private static async ValueTask<List<JsonObject>> StarterItemsAsync(IUnitOfWork uow) =>
        await uow.GetSquadAsync(1) is { } squad ? await uow.GetItemsAsync(SquadDocuments.ItemIds(squad)) : new List<JsonObject>();

    /// <summary>A typed club name or abbreviation without control characters and cut to <paramref name="maxLength"/>; empty or missing keeps the fallback.</summary>
    private static string ClubText(JsonNode? value, string fallback, int maxLength)
    {
        var text = value is JsonValue node && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
        var cleaned = new string((text ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = fallback.Trim();
        }
        return cleaned[..Math.Min(cleaned.Length, maxLength)].Trim();
    }

    private JsonObject Untradeable(JsonObject item)
    {
        _fut.Items.Restrict(item);
        return item;
    }

    /// <summary>
    /// The starter squad's cards (rules/onboarding.json starter, local policy): one per position, each drawn at random, seeded
    /// by the club and nation, from the eligible base cards rated closest to the target, every other slot from the nation.
    /// No footballer appears twice.
    /// </summary>
    internal List<long> StarterPlayerIds(long nation)
    {
        var rules = Rules.Starter;
        var random = GameRandom.Seeded("onboarding-starter", Game.ClubId, nation);
        var eligible = Game.Players.Entries
            .Where(entry => entry.Key < PlayerCatalog.ResourceRevisionSize && !entry.Value.IsSpecial && entry.Value.Rareflag <= rules.MaxRareflag
                && entry.Value.Rating >= rules.MinRating && entry.Value.Rating <= rules.MaxRating && !Game.Cards.IsGrantOnly(entry.Key))
            .Select(entry => (Id: entry.Key, Position: entry.Value.Text("preferredPosition"), Nation: entry.Value.Int("nation"),
                entry.Value.Rating, Footballer: entry.Value.IntKeep("assetId", entry.Key)))
            .ToList();
        var footballers = new HashSet<long>();
        var selected = new List<long>();
        foreach (var (position, slot) in rules.Positions.Select((position, slot) => (position, slot)))
        {
            var pool = eligible.Where(card => card.Position == position && !footballers.Contains(card.Footballer)).ToList();
            if (pool.Count == 0)
            {
                pool = eligible.Where(card => !footballers.Contains(card.Footballer)).ToList();
            }
            if (pool.Count == 0)
            {
                break;
            }
            if (slot % 2 == 0 && pool.Where(card => card.Nation == nation).ToList() is { Count: > 0 } national)
            {
                pool = national;
            }
            var closest = pool.OrderBy(card => Math.Abs(card.Rating - rules.TargetRating)).ThenBy(card => card.Id).Take(rules.Candidates).ToList();
            var pick = random.Choice(closest);
            footballers.Add(pick.Footballer);
            selected.Add(pick.Id);
        }
        return selected;
    }

    /// <summary>The starter manager: one of the nation's managers (any when it has none) rated at most the starter limit.</summary>
    internal long? StarterManagerId(long nation)
    {
        var managers = Game.Managers.Entries.Where(entry => entry.Value.Int("rating") <= Rules.Starter.ManagerMaxRating).ToList();
        var national = managers.Where(entry => entry.Value.Int("nation") == nation).ToList();
        var pool = (national.Count > 0 ? national : managers).Select(entry => entry.Key).Order().ToList();
        return pool.Count == 0 ? null : GameRandom.Seeded("onboarding-manager", Game.ClubId, nation).Choice(pool);
    }

    // ---- client data --------------------------------------------------------

    public static JsonObject ClientDataPayload(IReadOnlyDictionary<long, long> values) => new()
    {
        ["entries"] = new JsonArray(values.OrderBy(v => v.Key)
            .Select(v => (JsonNode?)new JsonObject { ["key"] = v.Key, ["value"] = v.Value }).ToArray()),
    };

    public async ValueTask<FutResult> SaveClientDataAsync(string kind, JsonNode? payload, CancellationToken cancellationToken = default)
    {
        if (payload is not JsonObject document || document.Arr("entries") is not { } entries)
        {
            return FutResult.Error(400, "expected client-data entries");
        }
        if (entries.Count > ClientDataLimits.MaxEntries)
        {
            return FutResult.Error(400, "too many client-data entries");
        }
        return await Game.RunAsync("save-client-data", async uow =>
        {
            await LockRelationalAsync(uow);
            var values = new SortedDictionary<long, long>(await uow.ClientDataAsync(kind));
            var seen = new HashSet<long>();
            foreach (var node in entries)
            {
                if (node is not JsonObject entry)
                {
                    return FutResult.Error(400, "invalid client-data entry");
                }
                if (!JsonValues.IsStrictInt(entry.Get("key"), out var key) || !JsonValues.IsStrictInt(entry.Get("value"), out var value)
                    || !ClientDataLimits.IsValid(key, value) || !seen.Add(key))
                {
                    return FutResult.Error(400, "invalid client-data key/value");
                }
                values[key] = value;
            }
            await uow.SaveClientDataAsync(kind, values);
            return FutResult.Ok(ClientDataPayload(values));
        }, cancellationToken);
    }

    public async ValueTask<JsonObject> CompletedOnboardingClientDataAsync(IUnitOfWork uow)
    {
        var values = new SortedDictionary<long, long>(await uow.ClientDataAsync("onboarding"));
        if (await IsCompleteAsync(uow))
        {
            var state = await uow.OnboardingAsync();
            var actives = await _fut.Club.ActiveClubItemsAsync(uow);
            values[OnboardingStepKey] = OnboardingCompletedStep;
            if (state.KitChoices.Count >= 2)
            {
                values[HomeKitKey] = state.KitChoices[0];
                values[AwayKitKey] = state.KitChoices[1];
            }
            else
            {
                SetIfPresent(values, HomeKitKey, actives.LastOrDefault(item => item.Text("itemState") == HomeKitState)?.Int("resourceId") ?? 0);
                SetIfPresent(values, AwayKitKey, actives.LastOrDefault(item => item.Text("itemState") == AwayKitState)?.Int("resourceId") ?? 0);
            }
            var badge = state.BadgeChoice != 0
                ? state.BadgeChoice
                : actives.FirstOrDefault(item => item.Text("itemState") == BadgeState)?.Int("resourceId") ?? 0;
            SetIfPresent(values, BadgeKey, badge);
        }
        return ClientDataPayload(values);
    }

    public async ValueTask<JsonObject> CompletedLoanClientDataAsync(IUnitOfWork uow)
    {
        var values = new SortedDictionary<long, long>(await uow.ClientDataAsync("loanplayerreward"));
        if (await IsCompleteAsync(uow))
        {
            var resourceId = (await uow.OnboardingAsync()).LoanResourceId;
            if (resourceId != 0)
            {
                // The client stores the base definition id (low 24 bits).
                values[LoanRewardKey] = resourceId & 0xFFFFFF;
            }
        }
        return ClientDataPayload(values);
    }

    /// <summary>A stored client-data document as uploaded, for the kinds the game only reads back at login.</summary>
    public async ValueTask<JsonObject> StoredClientDataAsync(IUnitOfWork uow, string kind) =>
        ClientDataPayload(await uow.ClientDataAsync(kind));

    /// <summary>User hub markers: the defaults FIFA expects, overridden by what the client stored.</summary>
    public async ValueTask<JsonObject> UserHubClientDataAsync(IUnitOfWork uow)
    {
        var values = new SortedDictionary<long, long>();
        foreach (var entry in TemplateLibrary.Core.Object("client-data/user-hub-data").Arr("entries")!.OfType<JsonObject>())
        {
            values[entry.Int("key")] = entry.Int("value");
        }
        foreach (var (key, value) in await uow.ClientDataAsync("userhubdata"))
        {
            values[key] = value;
        }
        return ClientDataPayload(values);
    }

    public async ValueTask<JsonObject> SquadClientDataAsync(IUnitOfWork uow)
    {
        var values = new SortedDictionary<long, long>(await uow.ClientDataAsync("squad"));
        if (await IsCompleteAsync(uow))
        {
            values.TryAdd(SquadTutorialKey, 0);
        }
        return ClientDataPayload(values);
    }

    private static void SetIfPresent(IDictionary<long, long> values, long key, long value)
    {
        if (value != 0)
        {
            values[key] = value;
        }
    }

    /// <summary>Merges server-owned progress markers into a client-data document.</summary>
    private static async ValueTask MarkAsync(IUnitOfWork uow, string kind, params (long Key, long Value)[] markers)
    {
        var values = new SortedDictionary<long, long>(await uow.ClientDataAsync(kind));
        foreach (var (key, value) in markers)
        {
            values[key] = value;
        }
        await uow.SaveClientDataAsync(kind, values);
    }

    // ---- loans --------------------------------------------------------------

    /// <param name="OfferId">The local offer id FIFA sends back to sign it: firstOfferId + position × offerIdsPerPosition + rank.</param>
    /// <param name="Footballer">The card's footballer (its asset id).</param>
    private sealed record LoanOffer(long OfferId, long ResourceId, long Footballer, int Ranking);

    /// <summary>
    /// Every position's loan cards, best first (rules/onboarding.json loans, retail-like): the base cards (not special,
    /// rareflag at most maxRareflag, not grant-only) preferring that position, by rating then lowest id. A card's place in
    /// its list is part of its offer id.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<(long ResourceId, long Footballer)>> LoanPools() => _loanPools ??= Loans.Positions
        .Select(position => (IReadOnlyList<(long, long)>)Game.Players.Entries
            .Where(entry => entry.Value.Text("preferredPosition") == position && entry.Key < PlayerCatalog.ResourceRevisionSize
                && !entry.Value.IsSpecial && entry.Value.Rareflag <= Loans.MaxRareflag && !Game.Cards.IsGrantOnly(entry.Key))
            .OrderByDescending(entry => entry.Value.Rating)
            .ThenBy(entry => entry.Key)
            .Take(Loans.OfferIdsPerPosition)
            .Select(entry => (entry.Key, entry.Value.IntKeep("assetId", entry.Key)))
            .ToList())
        .ToList();

    /// <summary>
    /// The offers for a club owning the given footballers: per position, the best playersPerPosition cards of footballers
    /// it does not own, ranked from 1. A club FIFA created owns only its starters, so it gets each position's best cards;
    /// a seeded club (which already owns the best base cards) gets the next best, never a duplicate of its own.
    /// </summary>
    private List<LoanOffer> Offers(IReadOnlySet<long> ownedFootballers) =>
        LoanPools().SelectMany((pool, position) => pool
                .Select((card, rank) => (card, rank))
                .Where(entry => !ownedFootballers.Contains(entry.card.Footballer))
                .Take(Loans.PlayersPerPosition)
                .Select((entry, index) => new LoanOffer(Loans.FirstOfferId + (long)position * Loans.OfferIdsPerPosition + entry.rank,
                    entry.card.ResourceId, entry.card.Footballer, index + 1)))
            .ToList();

    /// <summary>The footballers a club owns in its club and squad, apart from its onboarding loans.</summary>
    private async ValueTask<HashSet<long>> OwnedFootballersAsync(IUnitOfWork uow, OnboardingState state)
    {
        var loans = state.StaleLoanItemIds.Append(state.LoanItemId).Where(id => id != 0).ToHashSet();
        return (await uow.OwnedPlayerIdentitiesAsync())
            .Where(identity => !loans.Contains(identity.ItemId))
            .Select(identity => Game.Players.Find(identity.ResourceId)?.IntKeep("assetId", identity.ResourceId) ?? identity.ResourceId)
            .ToHashSet();
    }

    /// <summary>The loan cards a club owning none of them is offered, in offer order (playersPerPosition per position).</summary>
    public IReadOnlyList<long> LoanPlayerResourceIds() => Offers(new HashSet<long>()).Select(offer => offer.ResourceId).ToList();

    /// <summary>The starter loan offers; none once onboarding is complete, or FIFA re-enters first-run onboarding.</summary>
    public async ValueTask<JsonObject> LoanOffersAsync(IUnitOfWork uow)
    {
        var state = await uow.OnboardingAsync();
        return IsComplete(state) ? TemplateLibrary.Core.Object("onboarding/no-loans") : LoanPlayerChoices(Offers(await OwnedFootballersAsync(uow, state)));
    }

    /// <summary>
    /// Offer definition ids are local emulator ids, separate from resource and item ids. FIFA's picker shows five: offers
    /// for the position being improved ranked 1..10 first, then other positions' number ones (CardsDLL 0x10A7E0).
    /// </summary>
    private JsonObject LoanPlayerChoices(IEnumerable<LoanOffer> offers) => new()
    {
        ["loans"] = new JsonArray(offers.Select(offer => (JsonNode?)TemplateLibrary.Core.Object("onboarding/loan-offer", new TemplateArgs
        {
            ["offerId"] = offer.OfferId,
            ["ranking"] = offer.Ranking,
            ["games"] = Loans.Games,
            ["item"] = _fut.Native.PlayerItem(_fut.Items.BuildPlayer(offer.ResourceId, offer.ResourceId, "none", Loans.Games)),
        })).ToArray()),
    };

    public async ValueTask<FutResult> SignLoanAsync(long offerId, CancellationToken cancellationToken = default)
    {
        return await Game.RunAsync("sign-loan", async uow =>
        {
            await uow.LockClubAsync();
            var state = await uow.OnboardingAsync();
            var granted = state.LoanItemId != 0 ? await uow.GetItemAsync(state.LoanItemId) : null;
            if (granted is not null && state.LoanOfferId == offerId)
            {
                // A repeated signature (a lost reply) gets the same loan.
                return FutResult.Ok(_fut.Native.PlayerItem(granted));
            }
            if (Offers(await OwnedFootballersAsync(uow, state)).FirstOrDefault(offer => offer.OfferId == offerId) is not { } offer)
            {
                return FutResult.Error(404, "unknown local loan offer");
            }
            var resourceId = offer.ResourceId;
            if (state.LoanItemId != 0 && state.LoanOfferId != 0 && state.LoanOfferId != offerId)
            {
                // An interrupted onboarding can pick another offer. Keep the old loan until a
                // later squad save binds the new one so the squad never points at a deleted item.
                var stale = state.StaleLoanItemIds.Where(id => id > 0).ToList();
                if (granted is not null && !stale.Contains(state.LoanItemId))
                {
                    stale.Add(state.LoanItemId);
                }
                state = state with { StaleLoanItemIds = stale };
                await uow.SaveOnboardingAsync(state);
                granted = null;
            }

            if (granted is null)
            {
                granted = await _fut.Items.MakePlayerAsync(uow, resourceId, "unassigned", Loans.Games);
                // A changed loan reopens the following onboarding selections.
                await uow.SaveOnboardingAsync(state with
                {
                    LoanOfferId = offerId,
                    LoanItemId = granted.Int("id"),
                    LoanResourceId = resourceId,
                    KitChoices = Array.Empty<long>(),
                    KitItemIds = new JsonObject(),
                    BadgeChoice = 0,
                    BadgeItemId = 0,
                });
            }
            return FutResult.Ok(_fut.Native.PlayerItem(granted));
        }, cancellationToken);
    }

    /// <summary>
    /// The loan-explore squad save (PUT squad/{id}?onboarding=true, CardsDLL 0x1097A0): when the saved starting eleven
    /// holds the signed loan, the loan marker FIFA resumes from (kit selection next) is recorded. FIFA's own callbacks
    /// write no marker (ultimateteam/onboarding.md).
    /// </summary>
    public async ValueTask RecordLoanSquadAsync(JsonObject savedSquad, CancellationToken cancellationToken = default)
    {
        await Game.RunAsync("record-onboarding-loan", async uow =>
        {
            await LockRelationalAsync(uow);
            var state = await uow.OnboardingAsync();
            var starters = SquadDocuments.ChemistryItemIds(savedSquad, Game.Rules.Club.Squad.Starters).ToHashSet();
            if (state.LoanItemId == 0 || state.LoanResourceId == 0 || !starters.Contains(state.LoanItemId))
            {
                _log.LogInformation("ONBOARDING squad save without the signed loan in the starting eleven; no loan marker");
                return;
            }
            await MarkAsync(uow, "loanplayerreward", (LoanRewardKey, state.LoanResourceId & 0xFFFFFF));
        }, cancellationToken);
    }

    /// <summary>After a squad save that references the current loan, deletes stale earlier loans.</summary>
    public async ValueTask CleanupStaleLoansAsync(JsonObject savedSquad, CancellationToken cancellationToken = default)
    {
        await Game.RunAsync("cleanup-stale-loans", async uow =>
        {
            await LockRelationalAsync(uow);
            var state = await uow.OnboardingAsync();
            var referenced = SquadDocuments.ItemIds(savedSquad).ToHashSet();
            if (state.LoanItemId == 0 || !referenced.Contains(state.LoanItemId))
            {
                return;
            }
            foreach (var staleId in state.StaleLoanItemIds.Where(id => !referenced.Contains(id)))
            {
                await uow.DeleteItemAsync(staleId);
            }
            await uow.SaveOnboardingAsync(state with { StaleLoanItemIds = state.StaleLoanItemIds.Where(referenced.Contains).ToList() });
        }, cancellationToken);
    }

    // ---- kits and badge -----------------------------------------------------

    /// <summary>
    /// The club's kit choices (rules/onboarding.json kits): home and away kits drawn from the catalogue's pack kits at
    /// random, seeded by the club so every visit shows the same ones. Previews use definition ids, not owned ids.
    /// </summary>
    private (List<JsonObject> Home, List<JsonObject> Away) KitPool()
    {
        var kits = Game.Kits.Kits;
        List<JsonObject> Draw(long kitType, int count, string stream)
        {
            var pool = kits.Where(kit => ItemFactory.KitType(kit) == kitType && kit.IntKeep("resourceId") > 0)
                .OrderBy(kit => kit.IntKeep("resourceId")).ToList();
            return GameRandom.Seeded(stream, Game.ClubId).Sample(pool, Math.Min(count, pool.Count));
        }
        return (Draw(0, Rules.Kits.HomeChoices, "onboarding-home-kits"), Draw(1, Rules.Kits.AwayChoices, "onboarding-away-kits"));
    }

    private JsonObject KitItem(JsonObject kit, long itemId, string itemState, string pile) =>
        ItemFactory.KitItem(itemId, Game.NowSeconds, kit.IntKeep("resourceId"), itemState, pile, kit.Int("teamid"), ItemFactory.KitType(kit),
            2 + kit.Int("kitType"), kit.Text("name", "FIFA 21 Kit"));

    /// <summary>Native kit preview lists (parser 0x372050) using definition ids, not owned ids.</summary>
    public ValueTask<JsonObject> KitChoicesAsync(IUnitOfWork uow)
    {
        var (home, away) = KitPool();
        return ValueTask.FromResult(new JsonObject
        {
            ["homeItemDataList"] = new JsonArray(home.Select(kit => (JsonNode?)KitItem(kit, kit.IntKeep("resourceId"), HomeKitState, "club")).ToArray()),
            ["awayItemDataList"] = new JsonArray(away.Select(kit => (JsonNode?)KitItem(kit, kit.IntKeep("resourceId"), AwayKitState, "club")).ToArray()),
        });
    }

    /// <summary>
    /// The paired kit choice (serializer 0x315AC0): grants the two chosen kits untradeable, equips them in the home and
    /// away slots and answers their owned ids (parser 0x315BC0). Records the kit markers FIFA resumes from (badge next).
    /// </summary>
    public async ValueTask<FutResult> SelectKitsAsync(JsonNode? payload, CancellationToken cancellationToken = default)
    {
        if (payload is not JsonObject request)
        {
            return FutResult.Error(400, "expected paired kit choices");
        }
        var homeDefinition = request.Int("homeKitId");
        var awayDefinition = request.Int("awayKitId");
        var (homes, aways) = KitPool();
        var home = homes.FirstOrDefault(kit => kit.IntKeep("resourceId") == homeDefinition);
        var away = aways.FirstOrDefault(kit => kit.IntKeep("resourceId") == awayDefinition);
        if (home is null || away is null)
        {
            return FutResult.Error(404, "unknown local kit choice");
        }
        return await Game.RunAsync("select-onboarding-kits", async uow =>
        {
            await LockRelationalAsync(uow);
            var state = await uow.OnboardingAsync();
            if (state.KitChoices.SequenceEqual(new[] { homeDefinition, awayDefinition })
                && state.KitItemIds.Int("homeKitId") is var ownedHome and > 0 && state.KitItemIds.Int("awayKitId") is var ownedAway and > 0
                && await uow.ItemExistsAsync(ownedHome) && await uow.ItemExistsAsync(ownedAway))
            {
                return FutResult.Ok(new JsonObject { ["homeKitId"] = ownedHome, ["awayKitId"] = ownedAway });
            }
            var first = await uow.ReserveItemIdsAsync(2);
            await uow.SaveItemsAsync(new[] { Untradeable(KitItem(home, first, "free", "club")), Untradeable(KitItem(away, first + 1, "free", "club")) });
            await uow.RelationalItems.Customisation.EquipAsync(uow, first, HomeKitState);
            await uow.RelationalItems.Customisation.EquipAsync(uow, first + 1, AwayKitState);
            var result = new JsonObject { ["homeKitId"] = first, ["awayKitId"] = first + 1 };
            await uow.SaveOnboardingAsync(state with
            {
                KitChoices = new[] { homeDefinition, awayDefinition },
                KitItemIds = result.Copy(),
            });
            await MarkAsync(uow, "onboarding", (HomeKitKey, homeDefinition), (AwayKitKey, awayDefinition));
            return FutResult.Ok(result);
        }, cancellationToken);
    }

    /// <summary>
    /// The club's badge choices (rules/onboarding.json badges): club badges from the catalogue at random, seeded by the club.
    /// </summary>
    private async ValueTask<List<(long Resource, long Asset, string Name, long Rating, long Rareflag)>> BadgePoolAsync(IUnitOfWork uow)
    {
        var badges = await uow.QueryAsync("""
            -- query: onboarding.badges
            SELECT d.native_resource_id, coalesce(d.native_asset_id, 0), c.name, coalesce(c.rating, 0), coalesce(c.native_rareflag, 0)
            FROM ultimateteam.item_definitions d JOIN ultimateteam.customisation_definitions c ON c.definition_id = d.definition_id
            WHERE d.game_year = 2021 AND d.family_id = 4 AND c.customisation_kind = 'custom' AND d.native_resource_id IS NOT NULL
            ORDER BY d.native_resource_id
            """, reader => (Resource: reader.GetInt64(0), Asset: reader.GetInt64(1), Name: reader.GetString(2), Rating: reader.GetInt64(3),
                Rareflag: reader.GetInt64(4)));
        return GameRandom.Seeded("onboarding-badges", Game.ClubId).Sample(badges, Math.Min(Rules.Badges.Choices, badges.Count));
    }

    private JsonObject BadgeItem((long Resource, long Asset, string Name, long Rating, long Rareflag) badge, long itemId, string itemState) =>
        ItemFactory.ClubCosmeticItem("custom", itemId, Game.NowSeconds, badge.Resource, badge.Asset, itemState, "club", badge.Name,
            badge.Rating, badge.Rareflag, Game.Rules.Club.DefaultStadiumCapacity)!;

    /// <summary>Native badge previews (parser 0x35BCC0) using definition ids.</summary>
    public async ValueTask<JsonObject> BadgeChoicesAsync(IUnitOfWork uow) => new()
    {
        ["badgeItemDataList"] = new JsonArray((await BadgePoolAsync(uow))
            .Select(badge => (JsonNode?)BadgeItem(badge, badge.Resource, BadgeState)).ToArray()),
    };

    /// <summary>
    /// The badge choice (POST onboarding/badge/{resourceId}, no body): grants the badge untradeable, equips it and answers
    /// its owned id (parser 0x2FCDB0). This completes onboarding.
    /// </summary>
    public async ValueTask<FutResult> SelectBadgeAsync(long resourceId, CancellationToken cancellationToken = default)
    {
        return await Game.RunAsync("select-onboarding-badge", async uow =>
        {
            await LockRelationalAsync(uow);
            var state = await uow.OnboardingAsync();
            if (state.BadgeChoice == resourceId && state.BadgeItemId != 0 && await uow.ItemExistsAsync(state.BadgeItemId))
            {
                return FutResult.Ok(new JsonObject { ["badgeId"] = state.BadgeItemId });
            }
            var choices = await BadgePoolAsync(uow);
            var index = choices.FindIndex(badge => badge.Resource == resourceId);
            if (index < 0)
            {
                return FutResult.Error(404, "unknown local badge choice");
            }
            var itemId = await uow.ReserveItemIdsAsync(1);
            await uow.SaveItemsAsync(new[] { Untradeable(BadgeItem(choices[index], itemId, "free")) });
            await uow.RelationalItems.Customisation.EquipAsync(uow, itemId, BadgeState);
            await uow.SaveOnboardingAsync(state with
            {
                BadgeChoice = resourceId,
                BadgeItemId = itemId,
                Complete = true,
            });
            _log.LogInformation("ONBOARDING complete: badge={Badge}", resourceId);
            return FutResult.Ok(new JsonObject { ["badgeId"] = itemId });
        }, cancellationToken);
    }
}
