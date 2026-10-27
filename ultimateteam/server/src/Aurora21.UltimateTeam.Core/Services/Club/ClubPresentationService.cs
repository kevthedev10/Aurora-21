using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Core.Services.Club;

/// <summary>The club documents FIFA 21 reads at start-up: actives, My Stadium, squad, user and stats.</summary>
public sealed class ClubPresentationService
{
    /// <summary>The card subtypes of badges and balls, which the customisation counts require.</summary>
    private const long BadgeCardSubType = 11;
    private const long BallCardSubType = 30;

    /// <summary>The name of the unsaved squad FIFA opens when creating one (squad id 0).</summary>
    private const string NewSquadName = "New Squad";

    /// <summary>The largest consumables page one request returns.</summary>
    private const int MaxConsumablePage = 100;

    private readonly FutServices _fut;

    public ClubPresentationService(FutServices fut) => _fut = fut;

    internal GameContext Game => _fut.Game;
    private ClubRules Rules => Game.Rules.Club;

    public async ValueTask<long> SelectedStadiumIdAsync(IUnitOfWork uow) => ResolveStadium(await uow.SelectedStadiumIdAsync());

    /// <summary>
    /// The venue on the club's default stadium card: the stored selection (PUT /stadium or a selector pack) when a
    /// match can be played there, otherwise the tier 1 FUT Stadium, the retail default (Python server e2e098f).
    /// </summary>
    private long ResolveStadium(long? stored) => Rules.Venues.MatchVenue(stored is { } id && IsKnownVenue(id) ? id : null);

    /// <summary>A venue a club may select: one in stadiums.json, or a FUT Stadium tier.</summary>
    internal bool IsKnownVenue(long venue) => Game.Stadiums.Find(venue) is not null || Rules.Venues.FutStadiums.Contains(venue);

    /// <summary>
    /// The venue the club plays its home matches in, from its active items: the venue of the card in My Stadium slot 5,
    /// which is what FIFA loads (CardsDLL 0x1803c4000). An equipped owned stadium card wins when its venue can be played;
    /// otherwise the default card's selected venue (Python server _club_stadium_venue_id).
    /// </summary>
    public async ValueTask<long> HomeVenueAsync(IUnitOfWork uow, ClubSnapshot? snapshot = null) =>
        await HomeVenueAsync(uow, await ActiveClubItemsAsync(uow, snapshot));

    /// <inheritdoc cref="HomeVenueAsync(IUnitOfWork, ClubSnapshot?)"/>
    public async ValueTask<long> HomeVenueAsync(IUnitOfWork uow, IEnumerable<JsonObject> actives)
    {
        var equipped = actives.FirstOrDefault(item => item.Text("itemState") == "activeStadium") is { } stadium
            ? JsonValues.Int(stadium.GetOr("stadiumid", stadium.GetOr("assetId", 0)))
            : 0;
        return Rules.Venues.IsMatchVenue(equipped) ? equipped : await SelectedStadiumIdAsync(uow);
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

    public static async ValueTask<string> UtSidAsync(IUnitOfWork uow) => UtSid(await uow.SidAsync());

    /// <summary>The <c>X-UT-SID</c> value for a club SID.</summary>
    public static string UtSid(string sid) => "ut." + Sha256Hex("UT-" + sid)[..32];

    /// <summary>
    /// The active club items (stadium, kits, badge, ball). FIFA's /user and /squad/active payloads
    /// carry full item objects here, not ids. Persisted customisation choices replace the
    /// virtual defaults from rules/club.json.
    /// </summary>
    public async ValueTask<List<JsonObject>> ActiveClubItemsAsync(IUnitOfWork uow, ClubSnapshot? snapshot = null)
    {
        var view = snapshot?.Presentation ?? await ClubPresentationView.LoadActivesAsync(uow, CustomisationStates());
        return await ActiveClubItemsAsync(uow, view);
    }

    internal HashSet<string> CustomisationStates() => Rules.MyStadiumSlots.Select(slot => slot.ItemState).ToHashSet();

    internal async ValueTask<List<JsonObject>> ActiveClubItemsAsync(IUnitOfWork uow, ClubPresentationView view) =>
        (await ClubItemsAsync(uow, view)).Actives;

    /// <summary>
    /// The active club items, and the club's own default badge, ball and stadium while an owned one replaces them, as
    /// free items: the Match Day picker lists those so the player can go back to the default (Python server e2e098f).
    /// Starter kits are owned items, so they are listed anyway.
    /// </summary>
    internal async ValueTask<(List<JsonObject> Actives, List<JsonObject> FreeDefaults)> ClubItemsAsync(IUnitOfWork uow, ClubPresentationView view)
    {
        var (bindings, storedStadium) = await uow.RelationalItems.Customisation.ReadWithStadiumAsync(uow);
        var stadium = ResolveStadium(storedStadium);
        var established = Game.Established;
        var capacity = Game.Stadiums.Find(stadium)?.Capacity is { } known and not 0 ? known : Rules.DefaultStadiumCapacity;
        var items = Rules.DefaultActiveItemsTemplate.RenderArray(Rules.VirtualItemIdArgs(new TemplateArgs
        {
            ["timestamp"] = established,
            ["stadiumId"] = stadium,
            ["capacity"] = capacity,
        }, Game.ClubId)).OfType<JsonObject>().ToList();

        // The default stadium card wears the tier's own face (retail): 6200000 + venue, with the tier's rating and
        // rare flag (venues.futStadiumCards). The shared default card 6200058 showed every tier, the starter stadium
        // included, as the same 83-rated rare card (10-03 audit). Before the free-defaults pass below, so an active
        // default and a replaced one listed as free both carry the tier's face; a virtual selection still binds to
        // 6200058's definition, which migration 053 keeps.
        // Only the FUT Stadium tiers have such a card: any other selected venue (a stadium pack's) keeps 6200058, as
        // 6200000 + that venue is some other stadium's card.
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

        var equipped = bindings
            .Where(b => b.Item is not null).Select(b => view.ItemDocument(b.Native)).ToList();
        var freeDefaults = new List<JsonObject>();
        foreach (var owned in equipped)
        {
            var state = owned.Text("itemState");
            foreach (var replaced in items.Where(item => item.Text("itemState") == state && item.Text("itemType") != "kit"))
            {
                replaced["itemState"] = "free";
                freeDefaults.Add(replaced);
            }
            items.RemoveAll(item => item.Text("itemState") == state || item.Text("itemState") == "free");
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
                // FIFA looks the name up: the venue's localisation key.
                if (!item.ContainsKey("name")) item["name"] = $"StadiumName_{JsonValues.Int(item.GetOr("assetId", stadiumValue))}";
            }
        }
        foreach (var item in freeDefaults)
        {
            item["itemId"] = item.Int("id");
            if (item.Text("itemType") == "stadium" && !item.ContainsKey("name")) item["name"] = $"StadiumName_{item.Int("assetId")}";
        }
        return (items, freeDefaults);
    }

    /// <summary>
    public async ValueTask<JsonObject> StadiumResponseAsync(IUnitOfWork uow, ClubSnapshot? snapshot = null) =>
        StadiumResponse(await ActiveClubItemsAsync(uow, snapshot), await _fut.Vanity.SlotItemsAsync(uow), await _fut.Vanity.UnlockedSlotsAsync(uow));

    /// <summary>
    /// The club's My Stadium document from these actives, as its match squads carry it: its vanity choices in the slots
    /// Stadium Development opened for it. Sent with the rules' locks, the goal song slot (38) and the other earned slots read
    /// locked, and the game played no goal song in Rivals although one was equipped.
    /// </summary>
    public async ValueTask<JsonObject> ClubStadiumAsync(IUnitOfWork uow, List<JsonObject> actives) =>
        StadiumResponse(actives, await _fut.Vanity.SlotItemsAsync(uow), await _fut.Vanity.UnlockedSlotsAsync(uow));

    /// <summary>The My Stadium document: Match Day slots from the actives, vanity slots from the club's vanity choices.</summary>
    /// <param name="unlocked">The slots open to the club; without it a slot is locked as the rules say (CPU squads).</param>
    internal JsonObject StadiumResponse(List<JsonObject> actives, IReadOnlyDictionary<long, JsonObject>? vanity = null,
        IReadOnlySet<long>? unlocked = null)
    {
        var activeStadium = actives.FirstOrDefault(item => item.Text("itemState") == "activeStadium") ?? new JsonObject();
        var defaultVenue = Rules.Venues.DefaultVenueId;
        var stadiumId = JsonValues.Int(activeStadium.GetOr("stadiumid", activeStadium.GetOr("assetId", defaultVenue)), defaultVenue);
        var slots = new JsonArray();
        foreach (var (slot, position) in Rules.MyStadiumSlots.Select((slot, index) => (slot, index + 1)))
        {
            // Numbering Match Day slots by list position made each open the wrong picker.
            var slotId = slot.SlotId ?? position;
            JsonObject? itemData = null;
            if ((actives.FirstOrDefault(entry => entry.Text("itemState") == slot.ItemState) ?? vanity?.GetValueOrDefault(slotId)) is { } source)
            {
                itemData = source.Copy();
                itemData["cardSubTypeId"] = source.Int("cardsubtypeid");
                itemData["teamId"] = JsonValues.Int(source.GetOr("teamId", source.GetOr("teamid", 0)));
                itemData["authenticity"] = source.Int("authenticity");
            }
            slots.Add(TemplateLibrary.Core.Object("club/stadium-slot", new TemplateArgs
            {
                ["slotId"] = slotId,
                ["slotName"] = slot.SlotName,
                ["stadiumArea"] = slot.StadiumArea,
                ["sortOrder"] = position,
                ["vanityTypes"] = new JsonArray(slot.VanityTypes.Select(type => (JsonNode?)type).ToArray()),
                ["emptiable"] = slot.Emptiable,
                ["locked"] = unlocked is null ? slot.Locked : !unlocked.Contains(slotId),
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
    public JsonObject DefaultManager(ClubSnapshot snapshot)
        => DefaultManager(snapshot.Presentation);

    internal JsonObject DefaultManager(ClubPresentationView view)
    {
        var owned = view.Manager();
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
    public async ValueTask<JsonObject> NativeSquadAsync(IUnitOfWork uow, ClubSnapshot? snapshot = null, long? squadId = null)
    {
        var view = snapshot?.Presentation ?? await ClubPresentationView.LoadSquadAsync(uow, squadId, CustomisationStates());
        return await NativeSquadAsync(uow, view, squadId);
    }

    private async ValueTask<JsonObject> NativeSquadAsync(IUnitOfWork uow, ClubPresentationView view, long? squadId = null)
    {
        var selected = view.SelectSquad(squadId);
        JsonArray? tactics = null;
        if (selected is not null)
        {
            tactics = Tactics(await uow.SquadTacticsAsync(SquadDocuments.Id(selected)));
        }
        return NativeSquad(view, selected, squadId, await ActiveClubItemsAsync(uow, view), tactics);
    }

    /// <summary>
    /// A squad's content, resolved once and rendered either as a node tree or straight into a
    /// response writer. Cards are the view's own and are read, never inserted into a tree, so the
    /// writing path hands them out uncopied; <see cref="NativeSquad"/> copies them on the way in.
    /// </summary>
    private readonly record struct SquadContent(string Template, long Id, string Name, string Formation,
        long Captain, JsonNode Chemistry, long StarRating, long Rating, string SquadType, string Custom,
        List<(long Index, long KitNumber, JsonObject Card)> Slots, List<JsonObject> Actives,
        List<JsonObject> Managers, List<long> KickTakerIds, JsonArray? Tactics);

    /// <summary>The squad rendered as a document, for callers that go on to read or change it.</summary>
    private JsonObject NativeSquad(ClubPresentationView view, JsonObject? selected, long? squadId, List<JsonObject> activeItems, JsonArray? tactics)
    {
        var content = SquadContentOf(view, selected, squadId, activeItems, tactics);
        var squadRules = Rules.Squad;
        var players = new JsonArray();
        var slots = content.Slots.ToDictionary(slot => slot.Index);
        for (var index = 0; index < squadRules.Slots; index++)
        {
            players.Add(slots.TryGetValue(index, out var slot)
                ? SquadDocuments.Slot(index, slot.KitNumber, slot.Card.Copy(), Game.Cards)
                : SquadDocuments.EmptySlot(index));
        }
        var args = new TemplateArgs
        {
            ["id"] = content.Id,
            ["personaId"] = Game.Config.PersonaId,
            ["name"] = content.Name,
            ["formation"] = content.Formation,
            ["captain"] = content.Captain,
            ["chemistry"] = content.Chemistry,
            ["starRating"] = content.StarRating,
            ["rating"] = content.Rating,
            ["squadType"] = content.SquadType,
            ["custom"] = content.Custom,
            ["players"] = players,
            ["actives"] = ToArray(content.Actives),
            // The pre-match validator requires native manager subtype 4.
            ["managers"] = new JsonArray(content.Managers.Select(item => (JsonNode?)item.Copy()).ToArray()),
            ["kickTakers"] = SquadDocuments.KickTakers(content.KickTakerIds, Game.Cards.IsConceptItemId),
        };
        if (content.Tactics is { } saved)
        {
            args["tactics"] = saved;
        }
        return TemplateLibrary.Core.Object(content.Template, args);
    }

    /// <summary>
    /// The same squad written straight into the response, for a caller that sends it unchanged. The
    /// cards, actives, managers and kick takers are emitted as the writer reaches them, so none of
    /// the arrays and none of the copies the document form needs are ever built. Byte-identical to
    /// <see cref="NativeSquad"/> by <c>NativeSquadStreamParityTests</c>.
    /// </summary>
    private Action<Utf8JsonWriter> NativeSquadWriter(ClubPresentationView view, JsonObject? selected, long? squadId,
        List<JsonObject> activeItems, JsonArray? tactics)
    {
        var content = SquadContentOf(view, selected, squadId, activeItems, tactics);
        var slotCount = Rules.Squad.Slots;
        var isConcept = Game.Cards.IsConceptItemId;
        var cards = Game.Cards;
        var args = new TemplateArgs
        {
            ["id"] = content.Id,
            ["personaId"] = Game.Config.PersonaId,
            ["name"] = content.Name,
            ["formation"] = content.Formation,
            ["captain"] = content.Captain,
            ["chemistry"] = content.Chemistry,
            ["starRating"] = content.StarRating,
            ["rating"] = content.Rating,
            ["squadType"] = content.SquadType,
            ["custom"] = content.Custom,
        };
        args.Defer("players", writer =>
        {
            var slots = content.Slots;
            var next = 0;
            writer.WriteStartArray();
            for (var index = 0; index < slotCount; index++)
            {
                if (next < slots.Count && slots[next].Index == index)
                {
                    var slot = slots[next++];
                    writer.WriteStartObject();
                    writer.WriteNumber("index", index);
                    writer.WriteNumber("kitNumber", slot.KitNumber);
                    writer.WritePropertyName("itemData");
                    FutJson.WriteValue(writer, slot.Card);
                    if (cards.SquadSlotAttributes(slot.Card) is { } attributes)
                    {
                        writer.WritePropertyName("playerAttributes");
                        FutJson.WriteValue(writer, attributes);
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("index", index);
                    writer.WriteNumber("kitNumber", 0);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
        });
        // The template spends this one on both "actives" and "club", so the document form clones the
        // array twice and this form simply writes it twice.
        args.Defer("actives", writer => WriteAll(writer, content.Actives));
        args.Defer("managers", writer => WriteAll(writer, content.Managers));
        args.Defer("kickTakers", writer =>
        {
            writer.WriteStartArray();
            for (var index = 0; index < content.KickTakerIds.Count; index++)
            {
                var id = content.KickTakerIds[index];
                writer.WriteStartObject();
                writer.WriteNumber("index", index);
                writer.WriteNumber("id", id);
                writer.WriteBoolean("dream", isConcept(id));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        if (content.Tactics is { } saved)
        {
            args.Defer("tactics", writer => FutJson.WriteValue(writer, saved));
        }
        return writer =>
        {
            TemplateLibrary.Core[content.Template].Validate(args);
            if (!WireWriters.TryWrite(content.Template, writer, args))
            {
                throw new InvalidOperationException($"template {content.Template} has no generated writer; rerun tools/wire-writers/generate.mjs");
            }
        };
    }

    private static void WriteAll(Utf8JsonWriter writer, List<JsonObject> items)
    {
        writer.WriteStartArray();
        foreach (var item in items)
        {
            FutJson.WriteValue(writer, item);
        }
        writer.WriteEndArray();
    }

    /// <summary>A saved squad's cards by slot index, and its native managers (the club's default when it saved none).</summary>
    private (Dictionary<long, (long KitNumber, JsonObject Card)> Positions, List<JsonObject> Managers) SquadCards(
        ClubPresentationView view, JsonObject source)
    {
        var itemsById = view.SquadItems(source);
        var cards = Game.Cards;
        var positions = new Dictionary<long, (long KitNumber, JsonObject Card)>();
        foreach (var position in (source.Arr("players") ?? new JsonArray()).OfType<JsonObject>())
        {
            var raw = position.Get("itemId") ?? position.Obj("itemData")?.Get("id");
            if (!JsonValues.TryInt(raw, out var itemId) || !JsonValues.TryInt(position.GetOr("index", positions.Count), out var index))
            {
                continue;
            }
            var kitNumber = position.IntOr("kitNumber", index + 1);
            if (itemsById.TryGetValue(itemId, out var entry))
            {
                // Typed state renders the native card directly; the full wire document is only built
                // when the short path cannot serve it (imported representations and special cards).
                positions[index] = (kitNumber, entry.NativeForWriting(_fut.Native));
            }
            else if (cards.IsConceptItemId(itemId) && Game.Players.Contains(cards.ConceptResourceId(itemId)))
            {
                positions[index] = (kitNumber, _fut.Native.ConceptPlayerItem(cards.ConceptResourceId(itemId)));
            }
        }

        var managers = (source.Arr("manager") ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(saved => saved.Obj("itemData") ?? saved)
            .Select(item => item.Text("itemType") != "manager" ? view.Manager(item.Int("id")) ?? item : item)
            .Where(item => item.Text("itemType") == "manager")
            .Select(item => _fut.Native.ManagerItem(item))
            .ToList();
        if (managers.Count == 0 && DefaultManager(view) is { Count: > 0 } defaultManager)
        {
            managers.Add(defaultManager);
        }
        return (positions, managers);
    }

    /// <summary>
    /// FIFA's own chemistry for a squad's starters and manager, as its squad screen and swap previews compute it; the
    /// <paramref name="saved"/> number is kept only for a formation FIFA has no slots for (Python server e7fc5e1).
    /// </summary>
    private long SquadChemistry(JsonObject source, IReadOnlyDictionary<long, (long KitNumber, JsonObject Card)> positions,
        List<JsonObject> managers, long saved) =>
        Game.Chemistry.SquadChemistry(JsonValues.Str(source.Get("formation"), Rules.Squad.DefaultFormation),
            Enumerable.Range(0, Game.Chemistry.Starters).Select(index => positions.TryGetValue(index, out var starter) ? starter.Card : null).ToList(),
            managers.FirstOrDefault()) ?? saved;

    private SquadContent SquadContentOf(ClubPresentationView view, JsonObject? selected, long? squadId, List<JsonObject> activeItems, JsonArray? tactics)
    {
        var squadRules = Rules.Squad;
        var source = selected ?? new JsonObject
        {
            ["id"] = squadId ?? 0,
            ["squadName"] = squadId == 0 ? NewSquadName : squadRules.DefaultName,
            ["formation"] = squadRules.DefaultFormation,
            ["players"] = new JsonArray(),
        };
        var (positions, managers) = SquadCards(view, source);

        // Slot order, so both render paths walk them once alongside the empty slots.
        var slots = positions.OrderBy(entry => entry.Key)
            .Select(entry => (Index: entry.Key, entry.Value.KitNumber, entry.Value.Card)).ToList();

        var squadItemIds = slots.Select(slot => slot.Card.Int("id")).ToList();
        var captain = JsonValues.HasValue(source.Get("captain")) ? source.Int("captain") : squadItemIds.FirstOrDefault();
        if (!squadItemIds.Contains(captain) && squadItemIds.Count > 0)
        {
            captain = squadItemIds[0];
        }
        // FIFA reads custom tactics back from the squad document; an unsaved placeholder has none.
        // The key is the last one either way, so the two templates differ only by its presence and
        // the shape stays in Resources rather than being appended to a rendered document.
        var saved = selected is not null && tactics is { Count: > 0 } ? tactics : null;
        var ratings = SquadRatings(source, positions);
        return new SquadContent(saved is null ? "club/squad" : "club/squad-tactics",
            SquadDocuments.Id(source),
            SquadDocuments.FirstText(source, squadRules.DefaultName, "squadName", "name"),
            JsonValues.Str(source.Get("formation"), squadRules.DefaultFormation),
            captain,
            SquadChemistry(source, positions, managers, ratings.Chemistry),
            ratings.StarRating,
            ratings.Rating,
            JsonValues.Str(source.Get("squadType"), squadRules.DefaultSquadType),
            JsonValues.Str(source.Get("custom"), squadRules.CustomTactics),
            slots, activeItems, managers,
            KickTakerIds(source, squadItemIds, squadRules.KickTakers), saved);
    }

    /// <summary>
    /// A squad's star rating, rating and saved chemistry. A squad the client has saved keeps the numbers it sent (its PUT
    /// carries both <c>rating</c> and <c>starRating</c>). One it never saved stores 0 for both, which FIFA drew as a
    /// one-star, rating 5 team (every new member's first match; Python server SQUAD_IDENTITY_RATE_UNSAVED): it is rated
    /// from its starting XI instead, and a saved chemistry of 0 reads as the default, as a client save always carries one.
    /// </summary>
    private (long StarRating, long Rating, long Chemistry, bool Derived) SquadRatings(JsonObject source,
        IReadOnlyDictionary<long, (long KitNumber, JsonObject Card)> positions)
    {
        var squadRules = Rules.Squad;
        var chemistry = SquadDocuments.Chemistry(source, squadRules.DefaultChemistry);
        if (source.Int("rating") == 0 && source.Int("starRating") == 0)
        {
            var starters = Enumerable.Range(0, squadRules.Starters)
                .Select(index => positions.TryGetValue(index, out var starter) ? starter.Card.Int("rating") : 0);
            if (SquadDocuments.Overall(starters, squadRules.Starters) is var overall and > 0)
            {
                return (overall, overall, chemistry == 0 ? squadRules.DefaultChemistry : chemistry, true);
            }
        }
        var starRating = source.Int("starRating", squadRules.DefaultStarRating);
        return (starRating, source.Int("rating", starRating), chemistry, false);
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

    /// <summary>
    /// A saved squad as FIFA 21's native summary decoder reads it, with the chemistry computed from its cards now, so
    /// the list matches the squad screen after a card changed outside it (Python server _summary_chemistry).
    /// </summary>
    private JsonObject SquadSummary(ClubPresentationView view, JsonObject squad)
    {
        var id = SquadDocuments.Id(squad);
        var (positions, managers) = SquadCards(view, squad);
        // An unsaved squad is listed with the rating its own document derives from its XI.
        var ratings = SquadRatings(squad, positions);
        return TemplateLibrary.Core.Object("club/squad-summary", new TemplateArgs
        {
            ["id"] = id,
            ["name"] = SquadDocuments.FirstText(squad, $"Squad {id}", "squadName", "name"),
            ["rating"] = ratings.Derived ? ratings.Rating : squad.Int("rating"),
            ["chemistry"] = SquadChemistry(squad, positions, managers, ratings.Derived ? ratings.Chemistry : squad.Int("chemistry")),
            ["formation"] = JsonValues.Str(squad.Get("formation"), Rules.Squad.DefaultFormation),
        });
    }
    public async ValueTask<JsonObject> SquadListAsync(IUnitOfWork uow)
    {
        // The squad view holds every squad's starters and managers; no customisation rows are needed here.
        var view = await ClubPresentationView.LoadSquadAsync(uow, null, new HashSet<string>());
        return TemplateLibrary.Core.Object("club/squad-list", new TemplateArgs
        {
            ["squads"] = new JsonArray(view.Squads().Select(squad => (JsonNode?)SquadSummary(view, squad)).ToArray()),
        });
    }

    public async ValueTask<List<JsonObject>> ClubItemsWithActivesAsync(IUnitOfWork uow, ClubSnapshot snapshot) =>
        ClubItemsWithActives(snapshot, await ActiveClubItemsAsync(uow, snapshot));

    private static List<JsonObject> ClubItemsWithActives(ClubSnapshot snapshot, IEnumerable<JsonObject> activeItems)
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

    public async ValueTask<JsonObject> NativeUserAsync(IUnitOfWork uow, ClubSnapshot? snapshot = null)
    {
        var view = snapshot?.Presentation ?? await ClubPresentationView.LoadSquadAsync(uow, null, CustomisationStates());
        var metadata = view.MetadataItems();
        var clubItems = metadata.Where(item => item.Text("pile") == "club").ToList();
        var clubPlayers = view.CountPlayers("club");
        var clubManagers = clubItems.Count(item => item.Text("itemType") == "manager");
        var squad = await NativeSquadAsync(uow, view);
        var squadPlayers = (squad.Arr("players") ?? new JsonArray()).Count(slot => slot is JsonObject s && s["itemData"] is JsonObject);
        var actives = await ActiveClubItemsAsync(uow, view);
        var stadium = await ClubStadiumAsync(uow, actives);
        var credits = await CreditsPayloadAsync(uow);
        var summaries = new JsonArray(view.Squads().Select(saved => (JsonNode?)SquadSummary(view, saved)).ToArray());
        var args = CustomisationCounts(clubItems.Concat(actives).GroupBy(item => item.Int("id")).Select(group => group.Last()).ToList());
        var record = await uow.QueryAsync("SELECT won, draw, loss FROM ultimateteam.club_records WHERE club_id=@club",
            r => (Won: r.GetInt64(0), Draw: r.GetInt64(1), Loss: r.GetInt64(2)));
        var (won, draw, loss) = record.Count == 0 ? (0L, 0L, 0L) : record[0];
        return TemplateLibrary.Core.Object("club/user", new TemplateArgs
        {
            // The club header's record of settled non-Draft matches (Python server 17172fc).
            ["won"] = won,
            ["draw"] = draw,
            ["loss"] = loss,
            ["loosePlayers"] = Math.Max(0, clubPlayers - squadPlayers),
            ["squadPlayers"] = squadPlayers,
            ["managers"] = clubManagers,
            ["activeItems"] = actives.Count,
            ["clubTotal"] = clubPlayers + clubManagers + actives.Count,
            ["unassigned"] = view.CountPile("unassigned") + await _fut.Vanity.UnassignedCountAsync(uow),
            ["playerPicksPending"] = metadata.Any(ItemFactory.IsPlayerPickToken),
            ["clubPlayers"] = clubPlayers,
            ["personaId"] = Game.Config.PersonaId,
            ["clubName"] = Game.Config.ClubName,
            ["clubAbbr"] = Game.Config.ClubAbbr,
            // 1 opens FIFA's club-name screen after onboarding; only a club FIFA created that has not been named yet gets it.
            ["clubNameChangeAllowed"] = await _fut.Onboarding.ClubNameChangeAllowedAsync(uow) ? 1 : 0,
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

    /// <summary>
    /// The home hub's competition tiles: Squad Battles, Division Rivals and the Weekend League. Their due weeks and events are
    /// settled first (no query once known settled), then one query reads all of this club's competition rows; the live
    /// standings behind the ranks come from the shared cache.
    /// </summary>
    public async ValueTask<(JsonObject SquadBattles, JsonObject Rivals, JsonObject WeekendLeague)> CompetitionTilesAsync(IUnitOfWork uow)
    {
        await _fut.Rivals.EnsureSettledAsync(uow);
        await _fut.WeekendLeague.EnsureSettledAsync(uow);
        await _fut.SquadBattles.EnsureSettledAsync(uow);
        var (rivalsFrom, rivalsTo) = _fut.Rivals.PrizeWindow();
        var (leagueFrom, leagueTo) = _fut.WeekendLeague.PrizeWindow();
        var (battlesFrom, battlesTo) = _fut.SquadBattles.PrizeWindow();
        var state = await uow.HubCompetitionStateAsync(new HubCompetitionWindows(rivalsFrom, rivalsTo, _fut.WeekendLeague.SlotId(), leagueFrom, leagueTo,
            _fut.SquadBattles.CurrentWeekId, battlesFrom, battlesTo, _fut.SquadBattles.RefreshSeconds));
        return (await _fut.SquadBattles.HomeEventAsync(uow, state), await _fut.Rivals.HomeTileAsync(uow, state), await _fut.WeekendLeague.HomeEventAsync(uow, state));
    }

    /// <summary>The FUT hub summary: club, squad, transfer-list and watchlist counts.</summary>
    /// <summary>The hub template and its values, for a caller that sends the body unchanged.</summary>
    public ValueTask<(string Template, TemplateArgs Args)> HubBodyAsync(IUnitOfWork uow) => HubBodyAsync(uow, stream: true);

    /// <param name="stream">
    /// True writes the squad straight into the response and never builds it as a document, which is
    /// what the route wants. False renders it as a node, for <see cref="HubAsync(IUnitOfWork)"/> and
    /// the callers that read the hub back.
    /// </param>
    private async ValueTask<(string Template, TemplateArgs Args)> HubBodyAsync(IUnitOfWork uow, bool stream)
    {
        var market = await Game.ActiveListingCount.CountersAsync(uow, Game.Rules.Market.Listings.ActiveCountCacheMilliseconds);
        var view = await ClubPresentationView.LoadSquadAsync(uow, null, CustomisationStates());
        var selected = view.SelectSquad(null);
        var tactics = selected is not null ? Tactics(await uow.SquadTacticsAsync(SquadDocuments.Id(selected))) : null;
        var actives = await ActiveClubItemsAsync(uow, view);
        var tiles = await CompetitionTilesAsync(uow);
        var args = new TemplateArgs
        {
            // The Squad Battles tile (sqbtEvent, parser 0x18032d260), the Rivals tile (0x18032d480) and the Weekend League
            // calendar (championEvent, 0x1803379f0).
            ["sqbtEvent"] = tiles.SquadBattles,
            ["rivals"] = tiles.Rivals,
            ["championEvent"] = tiles.WeekendLeague,
            ["liveMessages"] = Rules.News.Enabled,
            ["auctionCount"] = market.ActiveListings,
            ["clubPlayers"] = view.CountPile("club") + view.CountPile("squad"),
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
        };
        if (stream)
        {
            args.Defer("squad", NativeSquadWriter(view, selected, null, actives, tactics));
        }
        else
        {
            args["squad"] = NativeSquad(view, selected, null, actives, tactics);
        }
        return ("club/hub", args);
    }

    public async ValueTask<JsonObject> HubAsync(IUnitOfWork uow)
    {
        var (template, args) = await HubBodyAsync(uow, stream: false);
        return TemplateLibrary.Core.Object(template, args);
    }

    public async ValueTask<JsonObject> HubAsync(CancellationToken cancellationToken = default) =>
        await Game.ReadAsync(uow => HubAsync(uow), cancellationToken);

    /// <summary>CardsDLL's bulk start-up document; its sections match the standalone routes.</summary>
    public async ValueTask<JsonObject> UserMassInfoAsync(IUnitOfWork uow)
    {
        var snapshot = ClubSnapshot.FromPresentation(await ClubPresentationView.LoadSquadAsync(uow, null, CustomisationStates()));
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
        // The FUT News flag CardsDLL reads before its /livemessage request (Python server ab6b0eb), just before credits.
        var balance = massInfo.Pop("credits");
        massInfo["liveMessagesAvailable"] = Rules.News.Enabled;
        massInfo["credits"] = balance;
        return massInfo;
    }

    public static JsonArray ToArray(IEnumerable<JsonObject> items) => new(items.Select(item => (JsonNode?)item.DeepClone()).ToArray());

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
