// Frozen pre-repair oracle from 3a40319; only namespace and type names changed.
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Tests.Api.Reference;

/// <summary>The club documents FIFA 21 reads at start-up: actives, My Stadium, squad, user and stats.</summary>
public sealed class ClubPresentationBaseline
{
    /// <summary>The card subtypes of badges and balls, which the customisation counts require.</summary>
    private const long BadgeCardSubType = 11;
    private const long BallCardSubType = 30;

    /// <summary>The name of the unsaved squad FIFA opens when creating one (squad id 0).</summary>
    private const string NewSquadName = "New Squad";

    /// <summary>The largest consumables page one request returns.</summary>
    private const int MaxConsumablePage = 100;

    private readonly FutServices _fut;

    public ClubPresentationBaseline(FutServices fut) => _fut = fut;

    internal GameContext Game => _fut.Game;
    private ClubRules Rules => Game.Rules.Club;

    public async ValueTask<long> SelectedStadiumIdAsync(IUnitOfWork uow)
    {
        // Updated with the Match Day venue port: the default card plays in the selected venue when a match can be played
        // there, otherwise in the tier 1 FUT Stadium (was stadiums.json default 261, a bare pitch).
        var stored = await uow.SelectedStadiumIdAsync();
        return Rules.Venues.MatchVenue(stored is { } id && (Game.Stadiums.Find(id) is not null || Rules.Venues.FutStadiums.Contains(id)) ? id : null);
    }

    public static async ValueTask<JsonObject> CreditsPayloadAsync(IUnitOfWork uow)
    {
        var summary = await uow.FinancialSummaryAsync();
        return TemplateLibrary.Core.Object("club/credits", new TemplateArgs
        {
            ["credits"] = summary.Credits,
            ["points"] = summary.FifaPoints,
            ["unopenedPacks"] = summary.UnopenedPacks,
        });
    }

    public static async ValueTask<string> PowSidAsync(IUnitOfWork uow) =>
        "pow." + Sha256Hex("POW-" + await uow.TokenAsync())[..32];

    public static async ValueTask<string> UtSidAsync(IUnitOfWork uow) =>
        "ut." + Sha256Hex("UT-" + await uow.SidAsync())[..32];

    /// <summary>
    /// The active club items (stadium, kits, badge, ball). FIFA's /user and /squad/active payloads
    /// carry full item objects here, not ids. Persisted customisation choices replace the
    /// virtual defaults from rules/club.json.
    /// </summary>
    public async ValueTask<List<JsonObject>> ActiveClubItemsAsync(IUnitOfWork uow, ClubSnapshotBaseline? snapshot = null)
    {
        var stadium = await SelectedStadiumIdAsync(uow);
        var established = Game.Established;
        var capacity = Game.Stadiums.Find(stadium)?.Capacity is { } known and not 0 ? known : Rules.DefaultStadiumCapacity;
        var items = Rules.DefaultActiveItemsTemplate.RenderArray(Rules.VirtualItemIdArgs(new TemplateArgs
        {
            ["timestamp"] = established,
            ["stadiumId"] = stadium,
            ["capacity"] = capacity,
        }, Game.ClubId)).OfType<JsonObject>().ToList();
        // The default stadium card wears its tier's own card (6200000 + venue, venues.futStadiumCards; PR #62); only the
        // FUT Stadium tiers have one.
        if (Rules.Venues.FutStadiumCards.GetValueOrDefault(stadium) is { } tierCard)
        {
            foreach (var item in items.Where(item => item.Text("itemState") == "activeStadium"))
            {
                item["resourceId"] = 6200000 + stadium;
                item["carddbid"] = 6200000 + stadium;
                item["rating"] = tierCard.Rating;
                item["value"] = tierCard.Rating;
                item["rareflag"] = tierCard.Rareflag;
                item["weightrare"] = tierCard.Rareflag * 10;
            }
        }

        var customisable = Rules.MyStadiumSlots.Select(slot => slot.ItemState).Where(state => state != "activeStadium").ToHashSet();
        var clubItems = snapshot?.Pile("club") ?? await uow.ListItemsAsync("club");
        foreach (var owned in clubItems.Where(item => customisable.Contains(item.Text("itemState"))))
        {
            var state = owned.Text("itemState");
            items.RemoveAll(item => item.Text("itemState") == state);
            items.Add(owned);
        }
        foreach (var item in items)
        {
            item["itemId"] = item.Int("id");
            if (item.Text("itemState") == "activeStadium")
            {
                var stadiumValue = JsonValues.Int(item.GetOr("stadiumid", item.GetOr("assetId", 0)));
                item["stadiumid"] = stadiumValue;
                item["StadiumId"] = stadiumValue;
                // Updated with the localisation-key names port (Python server ca228bc).
                if (!item.ContainsKey("name")) item["name"] = $"StadiumName_{JsonValues.Int(item.GetOr("assetId", stadiumValue))}";
            }
        }
        return items;
    }

    /// <summary>
    public async ValueTask<JsonObject> StadiumResponseAsync(IUnitOfWork uow, ClubSnapshotBaseline? snapshot = null) =>
        StadiumResponse(await ActiveClubItemsAsync(uow, snapshot));

    private JsonObject StadiumResponse(List<JsonObject> actives)
    {
        var activeStadium = actives.FirstOrDefault(item => item.Text("itemState") == "activeStadium") ?? new JsonObject();
        var myStadiumCard = Rules.Venues.DefaultVenueId; // MyStadiumCardAssetId was removed with the Match Day venue port
        var stadiumId = JsonValues.Int(activeStadium.GetOr("stadiumid", activeStadium.GetOr("assetId", myStadiumCard)), myStadiumCard);
        var slots = new JsonArray();
        foreach (var (slot, position) in Rules.MyStadiumSlots.Select((slot, index) => (slot, index + 1)))
        {
            JsonObject? itemData = null;
            if (actives.FirstOrDefault(entry => entry.Text("itemState") == slot.ItemState) is { } source)
            {
                itemData = source.Copy();
                itemData["cardSubTypeId"] = source.Int("cardsubtypeid");
                itemData["teamId"] = JsonValues.Int(source.GetOr("teamId", source.GetOr("teamid", 0)));
                itemData["authenticity"] = source.Int("authenticity");
            }
            // Numbering Match Day slots by list position made each open the wrong picker.
            var slotId = slot.SlotId ?? position;
            slots.Add(TemplateLibrary.Core.Object("club/stadium-slot", new TemplateArgs
            {
                ["slotId"] = slotId,
                ["slotName"] = slot.SlotName,
                ["stadiumArea"] = slot.StadiumArea,
                ["sortOrder"] = position,
                ["vanityTypes"] = new JsonArray(slot.VanityTypes.Select(type => (JsonNode?)type).ToArray()),
                ["emptiable"] = slot.Emptiable,
                ["locked"] = slot.Locked,
                ["itemData"] = itemData,
            }));
        }
        return TemplateLibrary.Core.Object("club/stadium", new TemplateArgs
        {
            ["clubName"] = Game.Config.ClubName,
            ["stadiumId"] = stadiumId,
            ["slots"] = slots,
        });
    }

    /// <summary>The club's first real owned manager (highest rated), or a preview of the best definition.</summary>
    public JsonObject DefaultManager(ClubSnapshotBaseline snapshot)
    {
        var owned = snapshot.Items()
            .Where(item => item.Text("itemType") == "manager")
            .OrderBy(item => -item.Int("rating"))
            .ThenBy(item => item.Int("id"))
            .FirstOrDefault();
        if (owned is not null)
        {
            return _fut.Native.ManagerItem(owned);
        }
        return Game.Managers.BestManagerId() is { } resourceId
            ? _fut.Native.ManagerItem(_fut.Items.BuildManager(resourceId, 0, "none"))
            : new JsonObject();
    }

    /// <summary>
    /// A squad with all native slots (including empty ones): the active squad (or the first) when
    public async ValueTask<JsonObject> NativeSquadAsync(IUnitOfWork uow, ClubSnapshotBaseline? snapshot = null, long? squadId = null)
    {
        snapshot ??= await ClubSnapshotBaseline.LoadAsync(uow);
        var selected = SelectSquad(snapshot, squadId);
        JsonArray? tactics = null;
        if (selected is not null)
        {
            tactics = Tactics(await uow.SquadTacticsAsync(SquadDocuments.Id(selected)));
        }
        return NativeSquad(snapshot, squadId, await ActiveClubItemsAsync(uow, snapshot), tactics);
    }

    private JsonObject NativeSquad(ClubSnapshotBaseline snapshot, long? squadId, List<JsonObject> activeItems, JsonArray? tactics)
    {
        var squadRules = Rules.Squad;
        var selected = SelectSquad(snapshot, squadId);
        var source = selected ?? new JsonObject
        {
            ["id"] = squadId ?? 0,
            ["squadName"] = squadId == 0 ? NewSquadName : squadRules.DefaultName,
            ["formation"] = squadRules.DefaultFormation,
            ["players"] = new JsonArray(),
        };
        var itemsById = new Dictionary<long, JsonObject>();
        foreach (var item in snapshot.Items())
        {
            itemsById[item.IntKeep("id")] = item;
        }

        var cards = Game.Cards;
        var positions = new Dictionary<long, JsonObject>();
        foreach (var position in (source.Arr("players") ?? new JsonArray()).OfType<JsonObject>())
        {
            var raw = position.Get("itemId") ?? position.Obj("itemData")?.Get("id");
            if (!JsonValues.TryInt(raw, out var itemId) || !JsonValues.TryInt(position.GetOr("index", positions.Count), out var index))
            {
                continue;
            }
            var kitNumber = position.IntOr("kitNumber", index + 1);
            if (itemsById.TryGetValue(itemId, out var item))
            {
                positions[index] = SquadDocuments.Slot(index, kitNumber, _fut.Native.PlayerItem(item));
            }
            else if (cards.IsConceptItemId(itemId) && Game.Players.Contains(cards.ConceptResourceId(itemId)))
            {
                positions[index] = SquadDocuments.Slot(index, kitNumber, _fut.Native.ConceptPlayerItem(cards.ConceptResourceId(itemId)));
            }
        }

        var players = new JsonArray();
        var populated = new List<JsonObject>();
        for (var index = 0; index < squadRules.Slots; index++)
        {
            if (positions.TryGetValue(index, out var slot))
            {
                players.Add(slot);
                populated.Add((JsonObject)slot["itemData"]!);
            }
            else
            {
                players.Add(SquadDocuments.EmptySlot(index));
            }
        }

        var managers = new JsonArray((source.Arr("manager") ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(saved => saved.Obj("itemData") ?? saved)
            .Where(item => item.Text("itemType") == "manager")
            .Select(item => (JsonNode?)_fut.Native.ManagerItem(item))
            .ToArray());
        if (managers.Count == 0 && DefaultManager(snapshot) is { Count: > 0 } defaultManager)
        {
            managers.Add(defaultManager);
        }

        var squadItemIds = populated.Select(item => item.Int("id")).ToList();
        var captain = JsonValues.HasValue(source.Get("captain")) ? source.Int("captain") : squadItemIds.FirstOrDefault();
        if (!squadItemIds.Contains(captain) && squadItemIds.Count > 0)
        {
            captain = squadItemIds[0];
        }
        var resolvedId = SquadDocuments.Id(source);
        var squad = TemplateLibrary.Core.Object("club/squad", new TemplateArgs
        {
            ["id"] = resolvedId,
            ["personaId"] = Game.Config.PersonaId,
            ["name"] = SquadDocuments.FirstText(source, squadRules.DefaultName, "squadName", "name"),
            ["formation"] = JsonValues.Str(source.Get("formation"), squadRules.DefaultFormation),
            ["captain"] = captain,
            ["chemistry"] = SquadDocuments.Chemistry(source, squadRules.DefaultChemistry),
            ["starRating"] = source.Int("starRating", squadRules.DefaultStarRating),
            // Updated with the squad-fields port (Python SQUAD_MATCH_PARITY): the template now carries rating and
            // squadType; the stored values, as this oracle has no rating derivation.
            ["rating"] = source.Int("rating", source.Int("starRating", squadRules.DefaultStarRating)),
            ["squadType"] = JsonValues.Str(source.Get("squadType"), squadRules.DefaultSquadType),
            ["custom"] = JsonValues.Str(source.Get("custom"), squadRules.CustomTactics),
            ["players"] = players,
            ["actives"] = ToArray(activeItems),
            // The pre-match validator requires native manager subtype 4.
            ["managers"] = managers,
            ["kickTakers"] = SquadDocuments.KickTakers(KickTakerIds(source, squadItemIds, squadRules.KickTakers), cards.IsConceptItemId),
        });
        // FIFA reads custom tactics back from the squad document; an unsaved placeholder has none.
        if (selected is not null && tactics is { Count: > 0 })
        {
            squad["tactics"] = tactics;
        }
        return squad;
    }

    private static JsonObject? SelectSquad(ClubSnapshotBaseline snapshot, long? squadId)
    {
        var squads = snapshot.Squads();
        return squadId is null
            ? squads.FirstOrDefault(squad => squad.Bool("active")) ?? squads.FirstOrDefault()
            : squads.FirstOrDefault(squad => SquadDocuments.Id(squad) == squadId);
    }

    /// <summary>
    /// Set-piece takers: saved <c>kicktakers[index]</c> choices that are still in the squad, over the
    /// first players, padded with the last taker.
    /// </summary>
    private static List<long> KickTakerIds(JsonObject squad, IReadOnlyList<long> squadItemIds, int count)
    {
        var fallback = squadItemIds.Take(count).ToList();
        var takers = new List<long>(fallback);
        foreach (var entry in (squad.Arr("kicktakers") ?? new JsonArray()).OfType<JsonObject>())
        {
            if (!JsonValues.TryInt(entry.GetOr("index", -1), out var slot) || !JsonValues.TryInt(entry.GetOr("id", 0), out var taker)
                || slot < 0 || slot >= count || !squadItemIds.Contains(taker))
            {
                continue;
            }
            while (takers.Count <= slot)
            {
                takers.Add(fallback.Count > 0 ? fallback[^1] : taker);
            }
            takers[(int)slot] = taker;
        }
        return SquadDocuments.PaddedKickTakerIds(takers, count);
    }

    private JsonArray Tactics(IReadOnlyDictionary<string, JsonObject> saved)
    {
        var order = Rules.Squad.TacticMentalities;
        return new JsonArray(saved
            .OrderBy(entry => order.IndexOf(entry.Key) is var index and >= 0 ? index : order.Count)
            .Select(entry => (JsonNode?)entry.Value)
            .ToArray());
    }

    /// <summary>A saved squad as FIFA 21's native summary decoder reads it.</summary>
    public JsonObject SquadSummary(JsonObject squad)
    {
        var id = SquadDocuments.Id(squad);
        return TemplateLibrary.Core.Object("club/squad-summary", new TemplateArgs
        {
            ["id"] = id,
            ["name"] = SquadDocuments.FirstText(squad, $"Squad {id}", "squadName", "name"),
            ["rating"] = squad.Int("rating"),
            ["chemistry"] = squad.Int("chemistry"),
            ["formation"] = JsonValues.Str(squad.Get("formation"), Rules.Squad.DefaultFormation),
        });
    }
    public async ValueTask<JsonObject> SquadListAsync(IUnitOfWork uow) => TemplateLibrary.Core.Object("club/squad-list", new TemplateArgs
    {
        ["squads"] = new JsonArray((await uow.ListSquadsAsync()).Select(squad => (JsonNode?)SquadSummary(squad)).ToArray()),
    });

    public async ValueTask<List<JsonObject>> ClubItemsWithActivesAsync(IUnitOfWork uow, ClubSnapshotBaseline snapshot) =>
        ClubItemsWithActives(snapshot, await ActiveClubItemsAsync(uow, snapshot));

    private static List<JsonObject> ClubItemsWithActives(ClubSnapshotBaseline snapshot, IEnumerable<JsonObject> activeItems)
    {
        var merged = new Dictionary<long, JsonObject>();
        var order = new List<long>();
        foreach (var item in snapshot.Pile("club").Concat(activeItems))
        {
            var id = item.Int("id");
            if (!merged.ContainsKey(id))
            {
                order.Add(id);
            }
            merged[id] = item;
        }
        return order.Select(id => merged[id]).ToList();
    }

    /// <summary>Owned kits, badges, stadiums and balls, counted from the real inventory.</summary>
    public static TemplateArgs CustomisationCounts(IReadOnlyCollection<JsonObject> items)
    {
        long Count(string type, long? subType = null) => items.Count(item =>
            item.Text("itemType").ToLowerInvariant() == type && (subType is null || item.Int("cardsubtypeid") == subType));
        return new TemplateArgs
        {
            ["kits"] = Count("kit"),
            ["badges"] = Count("custom", BadgeCardSubType),
            ["stadiums"] = Count("stadium"),
            ["balls"] = Count("ball", BallCardSubType),
        };
    }

    public async ValueTask<JsonObject> NativeUserAsync(IUnitOfWork uow, ClubSnapshotBaseline? snapshot = null)
    {
        snapshot ??= await ClubSnapshotBaseline.LoadAsync(uow);
        var clubItems = snapshot.Pile("club");
        var clubPlayers = clubItems.Count(ClubInventoryService.IsPlayer);
        var clubManagers = clubItems.Count(item => item.Text("itemType") == "manager");
        var squad = await NativeSquadAsync(uow, snapshot);
        var squadPlayers = (squad.Arr("players") ?? new JsonArray()).Count(slot => slot is JsonObject s && s["itemData"] is JsonObject);
        var actives = await ActiveClubItemsAsync(uow, snapshot);
        var stadium = StadiumResponse(actives);
        var credits = await CreditsPayloadAsync(uow);
        var summaries = new JsonArray(snapshot.Squads().Select(saved => (JsonNode?)SquadSummary(saved)).ToArray());
        var args = CustomisationCounts(ClubItemsWithActives(snapshot, actives));
        return TemplateLibrary.Core.Object("club/user", new TemplateArgs
        {
            // Added with the club record port (Python server 17172fc); the oracle clubs have played no match.
            ["won"] = 0,
            ["draw"] = 0,
            ["loss"] = 0,
            ["loosePlayers"] = Math.Max(0, clubPlayers - squadPlayers),
            ["squadPlayers"] = squadPlayers,
            ["managers"] = clubManagers,
            ["activeItems"] = actives.Count,
            ["clubTotal"] = clubPlayers + clubManagers + actives.Count,
            ["unassigned"] = snapshot.CountPile("unassigned"),
            ["playerPicksPending"] = snapshot.Items().Any(ItemFactory.IsPlayerPickToken),
            ["clubPlayers"] = clubPlayers,
            ["personaId"] = Game.Config.PersonaId,
            ["clubName"] = Game.Config.ClubName,
            ["clubAbbr"] = Game.Config.ClubAbbr,
            // Added with the club-name screen; the oracle clubs are seeded, never created by FIFA, so they are not offered it.
            ["clubNameChangeAllowed"] = 0,
            ["established"] = Game.Established.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["currencies"] = credits["currencies"]?.DeepClone(),
            ["unopenedPacks"] = credits["unopenedPacks"]?.DeepClone(),
            ["myStadium"] = stadium,
            ["myStadiumTier"] = stadium["lastUsedTierLevel"]?.DeepClone(),
            ["myStadiumSlots"] = stadium["tiers"]![0]!["slots"]?.DeepClone(),
            ["actives"] = ToArray(actives),
            ["squadSummaries"] = summaries,
            ["kits"] = args["kits"],
            ["badges"] = args["badges"],
            ["stadiums"] = args["stadiums"],
            ["balls"] = args["balls"],
        });
    }

    public async ValueTask<JsonObject> ClubStatsAsync(IUnitOfWork uow)
    {
        var clubItems = await uow.ListItemsAsync("club");
        var consumables = clubItems.Concat(await uow.ListItemsAsync("unassigned")).Where(NativeItems.IsConsumable).ToList();
        return ClubStats(clubItems, consumables);
    }

    private JsonObject ClubStats(List<JsonObject> clubItems, List<JsonObject> consumables)
    {
        var counters = ConsumableCounters(consumables);
        long Counter(string name) => counters.GetValueOrDefault(name);
        return TemplateLibrary.Core.Object("club/stats", new TemplateArgs
        {
            ["players"] = clubItems.Count(ClubInventoryService.IsPlayer),
            ["managers"] = clubItems.Count(item => item.Text("itemType") == "manager"),
            ["owned"] = consumables.Sum(item => Math.Max(1, item.Int("stackCount", 1))),
            ["contract"] = Counter("contractPlayer") + Counter("contractManager"),
            ["fitness"] = Counter("fitnessPlayer") + Counter("fitnessTeam"),
            ["healing"] = Counter("healing"),
            ["playstyle"] = Counter("playstylePlayer") + Counter("playstyleGk"),
            ["training"] = Counter("trainingPlayer") + Counter("trainingGk"),
            ["playerPlayStyle"] = Counter("playstylePlayer"),
            ["gkPlayStyle"] = Counter("playstyleGk"),
            ["contractPlayer"] = Counter("contractPlayer"),
            ["contractManager"] = Counter("contractManager"),
            ["fitnessPlayer"] = Counter("fitnessPlayer"),
            ["fitnessTeam"] = Counter("fitnessTeam"),
            ["trainingPlayer"] = Counter("trainingPlayer"),
            ["trainingGk"] = Counter("trainingGk"),
            ["managerLeague"] = Counter("managerLeagueModifier"),
            ["position"] = Counter("position"),
            ["formationManager"] = Counter("formationManager"),
        });
    }

    /// <summary>
    /// Club &gt; Consumables' counters, in FIFA's native stats shape (as the Python server sent it): a category and a stat list. CardsDLL's stats parser
    /// (0x2E4F00) looks the category up among club, year, country, league, newcards and consumables and indexes its stats
    /// table with it; with no category the index stays -1 and the game crashes (the player consumable menus, e.g.
    /// chemistry styles, load this right after club/consumables/development).
    /// </summary>
    public async ValueTask<JsonObject> ConsumableStatsAsync(IUnitOfWork uow) =>
        ConsumableStats((await uow.ListItemsAsync("club")).Concat(await uow.ListItemsAsync("unassigned")).Where(NativeItems.IsConsumable));

    private JsonObject ConsumableStats(IEnumerable<JsonObject> consumables)
    {
        var counters = ConsumableCounters(consumables);
        long Counter(string name) => counters.GetValueOrDefault(name);
        return TemplateLibrary.Core.Object("club/consumable-stats", new TemplateArgs
        {
            ["owned"] = counters.Values.Sum(),
            ["contract"] = Counter("contractPlayer") + Counter("contractManager"),
            ["contractPlayer"] = Counter("contractPlayer"),
            ["contractManager"] = Counter("contractManager"),
            ["fitness"] = Counter("fitnessPlayer") + Counter("fitnessTeam"),
            ["healing"] = Counter("healing"),
            ["training"] = Counter("trainingPlayer") + Counter("trainingGk"),
            ["trainingPlayer"] = Counter("trainingPlayer"),
            ["trainingGk"] = Counter("trainingGk"),
            ["fitnessPlayer"] = Counter("fitnessPlayer"),
            ["fitnessTeam"] = Counter("fitnessTeam"),
            ["playerPlayStyle"] = Counter("playstylePlayer"),
            ["gkPlayStyle"] = Counter("playstyleGk"),
            ["position"] = Counter("position"),
            ["managerLeague"] = Counter("managerLeagueModifier"),
            ["formationManager"] = Counter("formationManager"),
        });
    }

    /// <summary>
    /// Card counts per consumable counter. Chemistry styles count as player or goalkeeper play
    /// styles by definition (they share the position cards' asset id); other cards by their native
    /// card asset id, or their consumableType when they have none.
    /// </summary>
    private Dictionary<string, long> ConsumableCounters(IEnumerable<JsonObject> consumables)
    {
        var rules = Game.Rules.Cards.ConsumableCounters;
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in consumables)
        {
            var stack = item.Int("stackCount", 1);
            string? counter;
            if (Game.Cards.ChemistryStyle(item.Int("resourceId")) is { } style)
            {
                counter = style.Goalkeeper ? "playstyleGk" : "playstylePlayer";
            }
            else if (rules.ByCardAssetId.TryGetValue(item.Int("cardassetid"), out var byAsset))
            {
                counter = byAsset == "fitnessPlayer" && item.Int("cardsubtypeid") == rules.TeamFitnessSubType ? "fitnessTeam" : byAsset;
            }
            else
            {
                counter = rules.ByConsumableType.GetValueOrDefault(item.Int("consumableType"));
            }
            if (counter is not null)
            {
                counters[counter] = counters.GetValueOrDefault(counter) + stack;
            }
        }
        return counters;
    }

    /// <summary>
    /// Club &gt; Consumables and the apply screen: owned club-pile consumables grouped into one stack
    /// per definition. The training categories list chemistry styles only; every other category
    /// lists all consumables.
    /// </summary>
    public async ValueTask<JsonObject> ConsumableSearchAsync(IUnitOfWork uow, string category, QueryValues query) =>
        ConsumableSearch(await uow.ListItemsAsync("club"), category, query);

    private JsonObject ConsumableSearch(IEnumerable<JsonObject> clubItems, string category, QueryValues query)
    {
        var chemistryOnly = category.ToLowerInvariant() is "training" or "playstyle" or "playstyles" or "chemistrystyle";
        var stacks = clubItems
            .Where(NativeItems.IsConsumable)
            .Where(item => !chemistryOnly || Game.Cards.ChemistryStyle(item.Int("resourceId")) is not null)
            .GroupBy(item => JsonValues.Int(item.GetOr("resourceId", item.GetOr("assetId", 0))))
            .Where(group => group.Key > 0)
            .Select(group => _fut.Native.ConsumableStack(group.OrderBy(item => item.Int("id")).ToList()))
            .OrderBy(stack => stack.Obj("item")!.Int("cardsubtypeid"))
            .ThenBy(stack => stack.Int("resourceId"))
            .ToList();
        var start = (int)Math.Clamp(query.Ids("start").FirstOrDefault(), 0, stacks.Count);
        var countText = query.Last("count") ?? query.Last("num");
        var count = long.TryParse(countText, out var parsed) ? (int)Math.Clamp(parsed, 1, MaxConsumablePage) : MaxConsumablePage;
        var page = stacks.Skip(start).Take(count).ToList();
        return TemplateLibrary.Core.Object("club/consumables", new TemplateArgs
        {
            ["items"] = ToArray(page),
            ["count"] = page.Count,
            ["total"] = stacks.Count,
        });
    }

    /// <summary>The FUT hub summary: club, squad, transfer-list and watchlist counts.</summary>
    public async ValueTask<JsonObject> HubAsync(IUnitOfWork uow)
    {
        var market = await uow.MarketCountersAsync();
        var snapshot = await ClubSnapshotBaseline.LoadAsync(uow);
        // Added with the Division Rivals, Weekend League and Squad Battles ports (Python server 56145ed, f354894): the same
        // services' tiles.
        var tiles = await _fut.Club.CompetitionTilesAsync(uow);
        return TemplateLibrary.Core.Object("club/hub", new TemplateArgs
        {
            ["sqbtEvent"] = tiles.SquadBattles,
            ["rivals"] = tiles.Rivals,
            ["championEvent"] = tiles.WeekendLeague,
            // Added with the FUT News port (Python server ab6b0eb).
            ["liveMessages"] = Rules.News.Enabled,
            ["auctionCount"] = market.ActiveListings,
            ["clubPlayers"] = snapshot.CountPile("club") + snapshot.CountPile("squad"),
            ["tradePile"] = new JsonObject
            {
                ["count"] = market.TradePileCount,
                ["selling"] = market.Selling,
                ["sold"] = market.Sold,
            },
            ["watchlist"] = new JsonObject
            {
                ["count"] = market.WatchCount,
                ["outbid"] = market.Outbid,
                ["winning"] = market.Winning,
            },
            ["squad"] = await NativeSquadAsync(uow, snapshot),
        });
    }

    public async ValueTask<JsonObject> HubAsync(CancellationToken cancellationToken = default) =>
        await Game.ReadAsync(uow => HubAsync(uow), cancellationToken);

    /// <summary>CardsDLL's bulk start-up document; its sections match the standalone routes.</summary>
    public async ValueTask<JsonObject> UserMassInfoAsync(IUnitOfWork uow)
    {
        var snapshot = await ClubSnapshotBaseline.LoadAsync(uow);
        var onboarding = _fut.Onboarding;
        var massInfo = TemplateLibrary.Core.Object("club/mass-info", new TemplateArgs
        {
            ["user"] = await NativeUserAsync(uow, snapshot),
            ["squad"] = await NativeSquadAsync(uow, snapshot),
            ["loanPlayers"] = await onboarding.LoanOffersAsync(uow),
            ["loanClientData"] = await onboarding.CompletedLoanClientDataAsync(uow),
            ["onboardingClientData"] = await onboarding.CompletedOnboardingClientDataAsync(uow),
            // Intro and confirmation "seen" markers: without them every login replays the intros.
            ["tutorialClientData"] = await onboarding.StoredClientDataAsync(uow, "tutorialpopups"),
            ["userHubClientData"] = await onboarding.UserHubClientDataAsync(uow),
            ["squadBuildingSetsClientData"] = await onboarding.StoredClientDataAsync(uow, "squadbuildingsets"),
            ["worldEventClientData"] = await onboarding.StoredClientDataAsync(uow, "worldevents"),
            ["squadClientData"] = await onboarding.SquadClientDataAsync(uow),
            ["credits"] = await uow.CreditsAsync(),
        });
        if (Game.Config.RivalsClientData)
        {
            // Stored Rivals and Champions intro markers, just before credits.
            var credits = massInfo.Pop("credits");
            massInfo["rivalsClientData"] = await onboarding.StoredClientDataAsync(uow, "rivals");
            massInfo["credits"] = credits;
        }
        return massInfo;
    }

    public static JsonArray ToArray(IEnumerable<JsonObject> items) => new(items.Select(item => (JsonNode?)item.DeepClone()).ToArray());

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
