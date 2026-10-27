using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>
/// Squad Battles: generated CPU squads (Draft, and opponents the real clubs cannot fill) and the forced match venue. The
/// opponent sets, the weekly competition and the Team of the Week are in the other parts of this class.
/// </summary>
public sealed partial class SquadBattlesService
{
    private const long SecondsPerDay = 24 * 60 * 60;
    private static readonly string[] MatchItemStates = { "activeHomeKit", "activeAwayKit", "activeStadium" };

    private readonly FutServices _fut;
    private readonly ILogger _log;

    public SquadBattlesService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.SquadBattles");
    }

    private GameContext Game => _fut.Game;
    private SquadBattlesRules Rules => Game.Rules.SquadBattles;

    /// <summary>The venue every generated match is played at.</summary>
    public long StadiumId => Rules.Venue.StadiumId;

    /// <summary>Consistent local/opponent cosmetic items for a match handoff.</summary>
    public async ValueTask<List<JsonObject>> MatchActivesAsync(IUnitOfWork uow, bool opponent)
    {
        var actives = (await _fut.Club.ActiveClubItemsAsync(uow))
            .Where(item => MatchItemStates.Contains(item.Text("itemState")))
            .Select(item => item.Copy())
            .ToList();
        PrepareMatchActives(actives, opponent);
        return actives;
    }

    /// <summary>
    /// The club's match items with the stadium card moved to the match venue: Draft is always played there, so its squad
    /// carries the venue card, not the club's own stadium (home Squad Battles fixtures keep theirs, dbda9bc4).
    /// </summary>
    public async ValueTask<List<JsonObject>> VenueActivesAsync(IUnitOfWork uow)
    {
        var actives = await MatchActivesAsync(uow, opponent: false);
        ReplaceStadium(actives, null);
        return actives;
    }

    private void PrepareMatchActives(List<JsonObject> actives, bool opponent)
    {
        // The home side keeps its own stadium card: a home fixture loads the club's venue from it (the online shape).
        // Only the CPU opponent is moved into the forced venue (local 10-03: every home game sat in the Bernabéu).
        if (opponent)
        {
            ReplaceStadium(actives, Rules.ItemIds.MatchOpponentStadium);
            DressInOpponentKits(actives, Rules.ItemIds.OpponentKits);
        }
    }

    /// <summary>Stable daily generation key shared by hub summaries and squad fetches.</summary>
    public long CycleId() => Game.NowSeconds / SecondsPerDay;

    /// <summary>A randomized, positionally valid matchday squad of base cards, stable for the day.</summary>
    public List<long> PlayerIds(long targetRating, long slot = 0)
    {
        var players = Game.Players;
        var usedResources = new HashSet<long>();
        var usedAssets = new HashSet<long>();
        var selected = new List<long>();
        var random = GameRandom.Seeded("squad-battles", CycleId(), slot, targetRating);
        var allPositions = players.Entries.Select(entry => entry.Value.Position.ToUpperInvariant()).ToHashSet();

        List<(long Distance, long NegRating, long Id)> Candidates(IReadOnlySet<string> positions)
        {
            var result = new List<(long, long, long)>();
            foreach (var (id, meta) in players.Entries)
            {
                // Only base resource ids: promo ids make the pre-match flow wait forever on unavailable player heads.
                if (!positions.Contains(meta.Position.ToUpperInvariant()) || meta.Rating < Rules.CandidateMinRating
                    || meta.IntOr("rareflag", 1) is not (0 or 1) || id > 0x00FFFFFF || Game.Cards.IsGrantOnly(id)
                    || usedResources.Contains(id) || usedAssets.Contains(meta.IntOr("assetId", id, id)))
                {
                    continue;
                }
                result.Add((Math.Abs(meta.Rating - targetRating), -meta.Rating, id));
            }
            result.Sort();
            return result;
        }

        foreach (var positions in Rules.SlotPositions)
        {
            var pool = Candidates(positions.ToHashSet());
            if (pool.Count == 0)
            {
                pool = Candidates(allPositions);
            }
            if (pool.Count == 0)
            {
                throw new InvalidOperationException("FIFA 21 player catalog cannot fill a Squad Battles squad");
            }
            var shortlist = pool.Take(Math.Min(Rules.ShortlistSize, pool.Count)).ToList();
            var weights = shortlist.Select(candidate => 1.0 / Math.Pow(1.0 + candidate.Distance, Rules.RatingDistanceExponent)).ToList();
            var resourceId = random.WeightedChoice(shortlist, weights).Id;
            selected.Add(resourceId);
            usedResources.Add(resourceId);
            usedAssets.Add(players.Find(resourceId)?.IntOr("assetId", resourceId, resourceId) ?? resourceId);
        }
        return selected;
    }

    /// <summary>One complete playable opponent squad for a hub slot.</summary>
    public async ValueTask<JsonObject> OpponentAsync(IUnitOfWork uow, long slot)
    {
        var view = await ClubPresentationView.LoadActivesAsync(uow, _fut.Club.CustomisationStates(),
            includeManager: true);
        var actives = (await _fut.Club.ActiveClubItemsAsync(uow, view))
            .Where(item => MatchItemStates.Contains(item.Text("itemState")))
            .ToList();
        return Opponent(slot, actives, _fut.Club.DefaultManager(view));
    }

    private JsonObject Opponent(long slot, List<JsonObject> actives, JsonObject manager)
    {
        slot = Math.Clamp(slot, 0, Rules.Opponents.Count - 1);
        var opponent = Rules.Opponents[(int)slot];
        var players = new JsonArray(PlayerIds(opponent.Rating, slot)
            .Select((resourceId, index) => (JsonNode?)SquadDocuments.Slot(index, index + 1, _fut.Native.PlayerItem(
                _fut.Items.BuildPlayer(resourceId, OpponentPlayerItemId(slot, index), "none")), Game.Cards))
            .ToArray());
        var starterIds = players.Take(Game.Rules.Club.Squad.Starters).Select(player => player!["itemData"]!.AsObject().Int("id")).ToList();
        var rating = StarterRating(PlayerIds(opponent.Rating, slot));

        DressInOpponentKits(actives, Rules.ItemIds.OpponentKits + slot * Rules.ItemIds.OpponentKitsPerSlot);
        // CardsDLL caches active items by id: never reuse the local club's stadium instance id.
        ReplaceStadium(actives, Rules.ItemIds.OpponentStadiums + slot);

        return TemplateLibrary.Core.Object("squad-battles/opponent-squad", new TemplateArgs
        {
            ["slot"] = slot,
            // Not the user's persona, or FIFA takes this for the user's squad and drops its myStadiumInfo.
            ["personaId"] = Rules.HomeAway.CpuPersonaBase + slot,
            ["clubName"] = opponent.ClubName,
            ["formation"] = Rules.Formation,
            ["captain"] = starterIds[1],
            ["chemistry"] = opponent.Chemistry,
            ["starRating"] = opponent.Rating >= Rules.FiveStarFromRating ? 5 : 4,
            ["custom"] = Game.Rules.Club.Squad.CustomTactics,
            ["players"] = players,
            ["actives"] = ClubPresentationService.ToArray(actives),
            ["stadiumId"] = StadiumId,
            // The CPU's venue when it hosts: its My Stadium document holding the match stadium card.
            ["myStadiumInfo"] = _fut.Club.StadiumResponse(actives),
            ["estDate"] = EstDate(slot, rating, opponent.Chemistry),
            ["manager"] = manager,
            // The goalkeeper does not take kicks.
            ["kickTakers"] = SquadDocuments.KickTakers(starterIds.Skip(1).Take(Game.Rules.Club.Squad.KickTakers)),
        });
    }

    /// <summary>Legacy generic opponent feed for any other Squad Battles path: complete squads built from owned players.</summary>
    public async ValueTask<JsonObject> LegacyOpponentFeedAsync(IUnitOfWork uow)
    {
        var feed = Rules.LegacyFeed;
        var slots = Game.Rules.Club.Squad.Slots;
        var snapshot = await ClubSnapshot.LoadAsync(uow);
        var baseSquad = await _fut.Club.NativeSquadAsync(uow, snapshot);
        // Never a Pro, custom or grant-only card, even from the club's own players (PLAN 7.1).
        var sourceItems = snapshot.Items().Where(item => ClubInventoryService.IsPlayer(item) && !_fut.Matches.IsRestrictedCard(ClubInventoryService.ResourceOrAsset(item))).ToList();
        if (sourceItems.Count == 0)
        {
            // Never a grant-only card (the developers' Pro cards), even as a display-only opponent.
            sourceItems = Game.Players.Entries
                .Where(entry => !Game.Cards.IsGrantOnly(entry.Key))
                .OrderByDescending(entry => entry.Value.IntKeep("rating"))
                .Take(slots)
                .Select((entry, index) => _fut.Items.BuildPlayer(entry.Key, Rules.ItemIds.LegacyPlayers + index, "none"))
                .ToList();
        }
        // Fewer owned players than slots are repeated in order to fill the squad.
        var lineup = sourceItems.Count == 0
            ? new List<JsonObject>()
            : Enumerable.Range(0, slots).Select(index => sourceItems[index % sourceItems.Count]).ToList();

        var squads = new JsonArray();
        for (var index = 0; index < feed.Squads; index++)
        {
            var name = RuleText.Format(feed.NameFormat, ("n", index + 1));
            var opponent = baseSquad.Copy();
            opponent["id"] = feed.SquadIdBase + index;
            opponent["squadId"] = feed.SquadIdBase + index;
            opponent["squadName"] = name;
            opponent["name"] = name;
            opponent["difficulty"] = 0;
            opponent["rating"] = feed.RatingBase + index * feed.RatingStep;
            opponent["chemistry"] = feed.ChemistryBase + index * feed.ChemistryStep;
            opponent["players"] = new JsonArray(lineup.Select((item, position) =>
                (JsonNode?)SquadDocuments.Slot(position, position + 1, _fut.Native.PlayerItem(item), Game.Cards)).ToArray());
            squads.Add(opponent);
        }
        return TemplateLibrary.Core.Object("squad-battles/legacy-feed", new TemplateArgs
        {
            ["squads"] = squads,
            ["count"] = squads.Count,
        });
    }

    /// <summary>The rounded mean rating of a generated squad's starters, as the hub summary shows it.</summary>
    private long StarterRating(List<long> generated)
    {
        var starters = Game.Rules.Club.Squad.Starters;
        return (long)Math.Round(generated.Take(starters).Sum(id => Game.Players.Find(id)?.Rating ?? 0) / (double)starters, MidpointRounding.ToEven);
    }

    /// <summary>Whether the user hosts this cycle's fixture against a hub slot: two of the four opponents come to the user.</summary>
    public bool UserAtHome(long slot) => (CycleId() + slot) % 2 == 0;

    /// <summary>
    /// The opponent's estDate, chosen so FIFA derives the intended home side: CardsDLL sums the summary's badge,
    /// rating, chemistry, formation enum, estDate and stadium asset (kit team ids are not sent), even meaning the user
    /// is at home. A decimal string, as the summary decoder reads it.
    /// </summary>
    public string EstDate(long slot, long rating, long chemistry)
    {
        var homeAway = Rules.HomeAway;
        var total = homeAway.OpponentBadgeAssetId + rating + chemistry + homeAway.FormationEnum + StadiumId;
        var nudge = (total % 2 == 0) != UserAtHome(slot) ? 1 : 0;
        return (homeAway.EstDateBase + nudge).ToString(CultureInfo.InvariantCulture);
    }

    private long OpponentPlayerItemId(long slot, long index) =>
        Rules.ItemIds.OpponentPlayers + slot * Rules.ItemIds.OpponentPlayersPerSlot + index;

    /// <summary>Replaces the active stadium with the match venue (card and physical venue ids), optionally under a new item id.</summary>
    private void ReplaceStadium(List<JsonObject> actives, long? itemId)
    {
        var stadium = (actives.FirstOrDefault(item => item.Text("itemState") == "activeStadium") ?? new JsonObject()).Copy();
        actives.RemoveAll(item => item.Text("itemState") == "activeStadium");
        var venue = Rules.Venue;
        stadium.Update(new JsonObject
        {
            ["resourceId"] = venue.ResourceId,
            ["carddbid"] = venue.ResourceId,
            // The venue FIFA loads is the card's assetId.
            ["assetId"] = venue.StadiumId,
            ["stadiumid"] = venue.StadiumId,
            ["StadiumId"] = venue.StadiumId,
            ["name"] = venue.Name,
            ["itemType"] = "stadium",
            ["itemState"] = "activeStadium",
        });
        if (itemId is not null)
        {
            stadium["id"] = itemId.Value;
        }
        actives.Add(stadium);
    }

    /// <summary>Replaces the home/away kits with the opponent kits, based on the club's kit items.</summary>
    private void DressInOpponentKits(List<JsonObject> actives, long kitIdBase)
    {
        foreach (var kit in Rules.OpponentKits)
        {
            var item = actives.FirstOrDefault(active => active.Text("itemState") == kit.ItemState && active.Text("itemType") == "kit")?.Copy() ?? new JsonObject();
            item.Update(new JsonObject
            {
                ["id"] = kitIdBase + kit.KitType,
                ["resourceId"] = kit.ResourceId,
                ["carddbid"] = kit.ResourceId,
                ["assetId"] = kit.ResourceId,
                ["teamid"] = kit.TeamId,
                ["kitType"] = kit.KitType,
                ["teamkittypetechid"] = kit.KitType,
                ["category"] = 2 + kit.KitType,
                ["name"] = kit.Name,
                ["itemType"] = "kit",
                ["itemState"] = kit.ItemState,
            });
            actives.RemoveAll(active => active.Text("itemState") == kit.ItemState);
            actives.Add(item);
        }
    }

}
