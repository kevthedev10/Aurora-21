using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>
/// The Squad Battles Team of the Week (Python server a4fdc85; rules/sqbt_totw.json, squad_battles.json <c>totw</c>): every
/// FIFA 21 TOTW can be browsed through /featuredsquad (featureConsumerId sqbttotw), and the current one played.
/// </summary>
public sealed partial class SquadBattlesService
{
    private const string TotwConsumer = "sqbttotw";

    private SquadBattlesTotwRules Totw => Rules.Totw;

    /// <summary>
    /// One archived TOTW as a CPU squad: EA's XI placed in the formation slots the archive names (in archive order when they
    /// do not fill the formation), then the bench in EA's order. The user is always at home against it (0x1800f0300), so the
    /// CPU wears the opponent strips.
    /// </summary>
    private async ValueTask<JsonObject?> TotwDocumentAsync(IUnitOfWork uow, long week)
    {
        if (Game.Rules.Totw.Week(week) is not { } entry || !Game.Chemistry.Formations.TryGetValue(entry.Formation, out var slots))
        {
            return null;
        }
        var positions = Game.Chemistry.Positions;
        var byPosition = new Dictionary<int, TotwCard>();
        foreach (var card in entry.Starters)
        {
            byPosition.TryAdd(positions.IndexOf((card.Slot ?? string.Empty).ToUpperInvariant()), card);
        }
        var ordered = slots.Select(position => byPosition.GetValueOrDefault(position)).ToList();
        var starters = ordered.Any(card => card is null) || ordered.Distinct().Count() != entry.Starters.Count ? entry.Starters : ordered!;
        var players = new JsonArray(starters.Concat(entry.Subs.Take(Game.Rules.Club.Squad.Slots - starters.Count))
            .Select((card, index) => (JsonNode?)SquadDocuments.Slot(index, index + 1,
                _fut.Native.PlayerItem(_fut.Items.BuildPlayer(card!.ResourceId, Totw.ItemBase + week * 100 + index, "none")), Game.Cards))
            .ToArray());
        var starterItems = players.Take(starters.Count).Select(player => player!["itemData"]!.AsObject()).ToList();
        var view = await ClubPresentationView.LoadActivesAsync(uow, _fut.Club.CustomisationStates(), includeManager: true);
        var actives = (await _fut.Club.ActiveClubItemsAsync(uow, view))
            .Where(item => MatchItemStates.Contains(item.Text("itemState"))).Select(item => item.Copy()).ToList();
        DressInOpponentKits(actives, Rules.ItemIds.OpponentKits + Totw.KitSlot * Rules.ItemIds.OpponentKitsPerSlot);
        ReplaceStadium(actives, Totw.StadiumItemBase + week);
        return TemplateLibrary.Core.Object("squad-battles/totw-squad", new TemplateArgs
        {
            ["squadId"] = Totw.SquadId(week),
            ["personaId"] = Totw.PersonaBase + week,
            ["squadName"] = $"Team of the Week {week}",
            ["formation"] = entry.Formation,
            ["captain"] = starterItems.MaxBy(item => item.Int("rating"))!.Int("id"),
            ["chemistry"] = Game.Chemistry.SquadChemistry(entry.Formation, starterItems, null) ?? 0,
            ["custom"] = Game.Rules.Club.Squad.CustomTactics,
            ["players"] = players,
            ["actives"] = ClubPresentationService.ToArray(actives),
            ["myStadiumInfo"] = _fut.Club.StadiumResponse(actives),
            ["stadiumId"] = StadiumId,
            ["manager"] = _fut.Club.DefaultManager(view),
            // The goalkeeper does not take kicks.
            ["kickTakers"] = SquadDocuments.KickTakers(starterItems.Skip(1).Take(Game.Rules.Club.Squad.KickTakers).Select(item => item.Int("id"))),
        });
    }

    /// <summary>
    /// GET /featuredsquad/user/stats: the TOTW tile (consumer sqbttotw, parser 0x1802f6400), an opponent summary plus the
    /// end time, name, banner and the points per difficulty. It is playable while endTimeStamp is ahead; the current TOTW
    /// stays playable every week until the archive names another. The Featured Squad (consumer sqbt) is off.
    /// </summary>
    public async ValueTask<JsonObject> FeaturedStatsAsync(IUnitOfWork uow, string consumer)
    {
        var week = Game.Rules.Totw.Current;
        if (consumer != TotwConsumer || week == 0 || await TotwDocumentAsync(uow, week) is not { } document)
        {
            return TemplateLibrary.Core.Object("squad-battles/featured-off");
        }
        var record = await uow.SquadBattlesWeekAsync(CurrentWeek);
        var rating = DocumentRating(document);
        var chemistry = document.Int("chemistry");
        var (winPoints, lossPoints) = Competition.DifficultyPoints(rating, chemistry);
        var squadId = Totw.SquadId(week);
        var tile = TemplateLibrary.Core.Object("squad-battles/totw-tile", new TemplateArgs
        {
            ["squadId"] = squadId,
            ["clubName"] = $"TOTW {week}",
            ["crest"] = Text(Totw.Crest),
            ["rating"] = rating,
            ["chemistry"] = chemistry,
            ["formation"] = document.Str("formation"),
            ["difficulty"] = Totw.Difficulty,
            ["estDate"] = Text(Rules.HomeAway.EstDateBase),
            ["stadiumId"] = StadiumId,
            ["basePointsWin"] = Competition.BasePoints(rating, chemistry, true),
            ["basePointsLoss"] = Competition.BasePoints(rating, chemistry, false),
            ["topPlayers"] = TopPlayers(document),
            ["end"] = Competition.WeekEnd(record.Week),
            ["squadName"] = document.Str("squadName"),
            // The panel's picture, fetched as squadBattle/cmn/<bannerImageName> and cached by name.
            ["banner"] = Totw.BannerName(week),
            ["winPoints"] = new JsonArray(winPoints.Select(points => (JsonNode?)points).ToArray()),
            ["lossPoints"] = new JsonArray(lossPoints.Select(points => (JsonNode?)points).ToArray()),
        });
        if (record.Totw is { } played && played.Int("id") == squadId)
        {
            var name = Competition.DifficultyNames[Competition.ClampDifficulty(played.Int("difficulty"))];
            tile["pointsWon"] = played.Int("pointsWon");
            tile["score"] = played.Int("score");
            tile["oppScore"] = played.Int("oppScore");
            tile["penaltyScore"] = Present(played, "penaltyScore", -1);
            tile["oppPenaltyScore"] = Present(played, "oppPenaltyScore", -1);
            tile["difficulty"] = name;
            tile["difficultyName"] = name;
        }
        return tile;
    }

    /// <summary>GET /featuredsquad/history: every TOTW id newest first, a bare array (the TOTW viewer); empty for any other consumer.</summary>
    public JsonArray FeaturedHistory(string consumer) => consumer != TotwConsumer ? new JsonArray()
        : new JsonArray(Game.Rules.Totw.Weeks.Select(entry => entry.Week).OrderDescending().Select(week => (JsonNode?)Totw.SquadId(week)).ToArray());

    /// <summary>GET /featuredsquad/{id}: one archived TOTW squad, or a 404 for any other id or consumer.</summary>
    public async ValueTask<FutResult> FeaturedSquadAsync(IUnitOfWork uow, long squadId, string consumer) =>
        consumer == TotwConsumer && Totw.WeekOf(squadId) is { } week && await TotwDocumentAsync(uow, week) is { } document
            ? FutResult.Ok(document)
            : FutResult.Error(404, "no such featured squad");

    /// <summary>A TOTW banner (CardsDLL 0x1800f92d0 fetches squadBattle/cmn/&lt;bannerImageName&gt;): the archived week's picture, or null.</summary>
    public byte[]? Banner(string name)
    {
        var week = Game.Rules.Totw.Weeks.FirstOrDefault(entry => Totw.BannerName(entry.Week) == name);
        var path = week is null ? null : Path.Combine(Game.Paths.DataRoot, Totw.ArtFolder, name);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
