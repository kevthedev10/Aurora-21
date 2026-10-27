using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>
/// Squad Battles opponents as in FIFA 21 retail (Python server f354894; squad_battles.json <c>sets</c>): four squads drawn
/// from the other real clubs on the server and frozen when drawn, refreshed on request, with generated squads filling in.
/// </summary>
public sealed partial class SquadBattlesService
{
    private static readonly string[] KitStates = ["activeHomeKit", "activeAwayKit"];

    private SquadBattlesSetRules Sets => Rules.Sets;

    /// <summary>How long after a draw the player may refresh, for the home tile's query.</summary>
    internal long RefreshSeconds => Sets.RefreshSeconds;

    /// <summary>
    /// When the player may refresh this set: its draw plus refreshSeconds, or the time stored with it when sooner, so a set
    /// drawn under a longer timer (6 h before 10-07) follows the current rule.
    /// </summary>
    private long RefreshAt(SquadBattlesSet set) => Math.Min(set.NextRefreshAt, set.CreatedAt + Sets.RefreshSeconds);

    /// <summary>This club's four opponents this week; the first set is drawn on demand.</summary>
    private async ValueTask<SquadBattlesSet> CurrentSetAsync(IUnitOfWork uow, long week)
    {
        if (await uow.SquadBattlesSetAsync() is { } set && set.Week == week)
        {
            return await RepairAsync(uow, set);
        }
        var drawn = await NewSetAsync(uow, week, 0, []);
        await uow.SaveSquadBattlesSetAsync(drawn);
        return drawn;
    }

    private async ValueTask<SquadBattlesSet> NewSetAsync(IUnitOfWork uow, long week, int index, long[] faced)
    {
        var now = Game.NowSeconds;
        var squads = await DrawSetAsync(uow, week, index, faced);
        var sources = squads.OfType<JsonObject>().Select(squad => squad.Int("source")).Where(source => source > 0);
        _log.LogWarning("SQBT SET club={Club} week={Week} set={Set} opponents={Opponents}", uow.ClubId, week, index,
            string.Join("; ", squads.OfType<JsonObject>().Select(squad => $"{squad.Str("bucket")} {squad.Obj("info")?.Str("clubName")} {squad.Obj("info")?.Int("rating")} {squad.Int("source")}")));
        return new SquadBattlesSet(week, index, now, Math.Min(Competition.WeekEnd(week), now + Sets.RefreshSeconds),
            faced.Concat(sources).TakeLast(Sets.FacedMemory).ToArray(), squads);
    }

    /// <summary>
    /// Four frozen opponents: Easy, Medium and Hard and one more Easy or Medium, seeded by club, week and set. The draw never
    /// picks this club, prefers clubs not faced this week, and falls back to a generated squad when a bucket has no usable club.
    /// </summary>
    private async ValueTask<JsonArray> DrawSetAsync(IUnitOfWork uow, long week, int index, long[] faced)
    {
        var random = GameRandom.Seeded("squad-battles-set", uow.ClubId, week, index);
        var buckets = new List<string>(SquadBattlesSetRules.Buckets) { random.Choice(["EASY", "MEDIUM"]) }
            .OrderBy(bucket => Array.IndexOf(SquadBattlesSetRules.Buckets, bucket)).Take(Sets.SquadsPerSet).ToList();
        // A squad holding a Pro, custom or grant-only card is never an opponent (PLAN 7.1).
        var split = Split(await uow.SquadBattlesPoolAsync(_fut.Matches.RestrictedResourceIds()));
        var callerKits = (await _fut.Club.ActiveClubItemsAsync(uow))
            .Where(item => KitStates.Contains(item.Str("itemState"))).Select(item => item.Int("resourceId")).ToHashSet();
        var taken = new HashSet<long>();
        var squads = new JsonArray();
        for (var slot = 0; slot < buckets.Count; slot++)
        {
            squads.Add(await DrawSlotAsync(uow, split[buckets[slot]], buckets[slot], slot, index, faced, taken, callerKits, random));
        }
        return squads;
    }

    /// <summary>One hub slot's opponent from its bucket: a club not taken by another slot, one not faced this week when there is one.</summary>
    private async ValueTask<JsonObject> DrawSlotAsync(IUnitOfWork uow, List<SquadBattlesPoolEntry> bucketPool, string bucket, int slot, int index,
        long[] faced, HashSet<long> taken, HashSet<long> callerKits, GameRandom random)
    {
        var candidates = bucketPool.Where(entry => !taken.Contains(entry.ClubId)).ToList();
        var fresh = candidates.Where(entry => !faced.Contains(entry.ClubId)).ToList();
        var choices = fresh.Count > 0 ? fresh : candidates;
        JsonObject? squad = null;
        foreach (var entry in random.Sample(choices, Math.Min(Sets.SnapshotTries, choices.Count)))
        {
            if ((squad = await SnapshotAsync(uow, entry, slot, index, callerKits)) is not null)
            {
                taken.Add(entry.ClubId);
                break;
            }
        }
        squad ??= await FallbackAsync(uow, slot, index);
        squad["id"] = slot;
        squad["bucket"] = bucket;
        squad["result"] = null;
        return squad;
    }

    /// <summary>
    /// Redraws, in place, each unplayed club snapshot whose starting eleven or bench has an empty slot. Sets drawn before
    /// <see cref="SnapshotAsync"/> refused such squads hold them, and FIFA cannot start a match against one: Chronicc's
    /// two 78s each had a bench hole and failed to start, and no squad with a bench hole was ever played (live 10-05).
    /// </summary>
    private async ValueTask<SquadBattlesSet> RepairAsync(IUnitOfWork uow, SquadBattlesSet set)
    {
        var broken = set.Squads.OfType<JsonObject>()
            .Where(squad => squad.Obj("result") is null && squad.Int("source") > 0 && !MatchdayFull(squad.Obj("doc") ?? new JsonObject()))
            .Select(squad => squad.Int("id")).ToList();
        if (broken.Count == 0)
        {
            return set;
        }
        var split = Split(await uow.SquadBattlesPoolAsync(_fut.Matches.RestrictedResourceIds()));
        var callerKits = (await _fut.Club.ActiveClubItemsAsync(uow))
            .Where(item => KitStates.Contains(item.Str("itemState"))).Select(item => item.Int("resourceId")).ToHashSet();
        var taken = set.Squads.OfType<JsonObject>().Select(squad => squad.Int("source")).Where(source => source > 0).ToHashSet();
        var squads = new JsonArray();
        var drawn = new List<long>();
        foreach (var squad in set.Squads.OfType<JsonObject>())
        {
            var slot = (int)squad.Int("id");
            if (!broken.Contains(slot))
            {
                squads.Add(squad.Copy());
                continue;
            }
            var bucket = split.ContainsKey(squad.Str("bucket")) ? squad.Str("bucket") : "MEDIUM";
            var random = GameRandom.Seeded("squad-battles-repair", uow.ClubId, set.Week, set.Index, slot);
            var redrawn = await DrawSlotAsync(uow, split[bucket], bucket, slot, set.Index, set.Faced, taken, callerKits, random);
            _log.LogWarning("SQBT REPAIR club={Club} set={Set} slot={Slot} was={Was} now={Now} {Rating}", uow.ClubId, set.Index, slot,
                squad.Int("source"), redrawn.Int("source"), redrawn.Obj("info")?.Int("rating"));
            squads.Add(redrawn);
            if (redrawn.Int("source") > 0)
            {
                drawn.Add(redrawn.Int("source"));
            }
        }
        var repaired = set with { Faced = set.Faced.Concat(drawn).TakeLast(Sets.FacedMemory).ToArray(), Squads = squads };
        await uow.SaveSquadBattlesSetAsync(repaired);
        return repaired;
    }

    /// <summary>Whether all eleven starters and seven substitutes are cards (reserves may be empty).</summary>
    private static bool MatchdayFull(JsonObject document) =>
        (document.Arr("players") ?? new JsonArray()).OfType<JsonObject>()
            .Where((slot, ordinal) => JsonValues.TryInt(slot.GetOr("index", ordinal), out var index) && index < MatchService.MatchdaySlots)
            .Count(slot => slot.Obj("itemData") is { } item && item.Int("id") > 0) == MatchService.MatchdaySlots;

    /// <summary>
    /// The pool's Easy, Medium and Hard thirds. Retail's bucket bounds are in points no source defines for this server's
    /// squads, so the buckets are the pool's own thirds, by the XI's average rating (see _setsComment); copies of an XI count once.
    /// </summary>
    private static Dictionary<string, List<SquadBattlesPoolEntry>> Split(List<SquadBattlesPoolEntry> pool)
    {
        var ordered = pool.DistinctBy(entry => entry.Signature).OrderBy(entry => entry.Rating).ThenBy(entry => entry.ClubId).ToList();
        int third = ordered.Count / 3, twoThirds = 2 * ordered.Count / 3;
        return new Dictionary<string, List<SquadBattlesPoolEntry>>
        {
            ["EASY"] = ordered[..third],
            ["MEDIUM"] = ordered[third..twoThirds],
            ["HARD"] = ordered[twoThirds..],
        };
    }

    /// <summary>
    /// One real club's active squad, frozen as a CPU opponent for a hub slot: the same document the club's own /squad/active
    /// serves, read as it is now, with its own item ids (each club's band is its own) and its own stadium. It takes a CPU
    /// persona (FIFA keeps a squad's stadium only when the persona is not the user's; squad parser 0x1803248e0), and its
    /// strips are swapped when they would match the user's. Null when its XI and bench are no longer eighteen owned cards:
    /// FIFA cannot start a match against a squad with an empty substitute slot.
    /// </summary>
    private async ValueTask<JsonObject?> SnapshotAsync(IUnitOfWork uow, SquadBattlesPoolEntry entry, int slot, int setIndex, HashSet<long> callerKits)
    {
        var (document, name) = await uow.ReadOtherClubAsync(entry.ClubId, async other =>
            (await _fut.Club.NativeSquadAsync(other), await other.DisplayNameAsync()));
        var starters = Starters(document);
        if (!MatchdayFull(document) || starters.Count != Game.Rules.Club.Squad.Starters || starters.Any(item => item.Bool("dream") || Game.Cards.IsConceptItemId(item.Int("id"))))
        {
            return null;
        }
        // A squad holding a Pro, custom or grant-only card anywhere (substitutes too) is never an opponent (PLAN 7.1).
        if ((document.Arr("players") ?? new JsonArray()).OfType<JsonObject>().Select(slot => slot.Obj("itemData")).OfType<JsonObject>()
            .Any(item => _fut.Matches.IsRestrictedCard(ClubInventoryService.ResourceOrAsset(item))))
        {
            return null;
        }
        var actives = (document.Arr("actives") ?? new JsonArray()).OfType<JsonObject>().Select(item => item.Copy()).ToList();
        var kits = actives.Where(item => KitStates.Contains(item.Str("itemState"))).Select(item => item.Int("resourceId")).ToHashSet();
        if (kits.Count == 0 || kits.Overlaps(callerKits))
        {
            DressInOpponentKits(actives, Rules.ItemIds.OpponentKits + slot * Rules.ItemIds.OpponentKitsPerSlot);
        }
        var crest = (actives.FirstOrDefault(item => item.Str("itemState") == "activeBadge")?.Int("resourceId") ?? 0) - Sets.BadgeResourceBase;
        if (crest is <= 0 or >= 1_000_000)
        {
            crest = Rules.HomeAway.OpponentBadgeAssetId;
        }
        var clubName = PlayerNames.Readable(name) ?? entry.Name;
        clubName = clubName[..Math.Min(24, clubName.Length)];
        var formation = document.Str("formation") is { Length: > 0 } saved ? saved : Rules.Formation;
        var chemistry = document.Int("chemistry");
        var rating = DocumentRating(document);
        var venue = actives.FirstOrDefault(item => item.Str("itemState") == "activeStadium")?.Int("assetId") ?? 0;
        var stadium = venue > 0 ? venue : StadiumId;
        var userHome = (setIndex + slot) % 2 == 0;
        var estDate = ParityEstDate(crest, rating, chemistry, formation, stadium, userHome, 0);
        document["id"] = slot;
        if (document.ContainsKey("squadId"))
        {
            document["squadId"] = slot;
        }
        document["personaId"] = Sets.SetPersonaBase + slot;
        document["squadName"] = clubName;
        document["actives"] = ClubPresentationService.ToArray(actives);
        document["club"] = ClubPresentationService.ToArray(actives);
        document["myStadiumInfo"] = _fut.Club.StadiumResponse(actives);
        document["stadiumAssetId"] = stadium;
        document["stadiumId"] = stadium;
        document["isHomeTeam"] = true;
        document["estDate"] = estDate;
        return new JsonObject
        {
            ["source"] = entry.ClubId,
            ["info"] = Info(clubName, Abbreviation(clubName), crest, rating, chemistry, formation, stadium, estDate, userHome, document),
            ["doc"] = document,
        };
    }

    /// <summary>A generated CPU squad for a slot the real clubs cannot fill; its cards carry the set number, so a refresh never reuses an id.</summary>
    private async ValueTask<JsonObject> FallbackAsync(IUnitOfWork uow, int slot, int setIndex)
    {
        var document = await OpponentAsync(uow, slot);
        var ids = new Dictionary<long, long>();
        foreach (var (player, index) in (document.Arr("players") ?? new JsonArray()).OfType<JsonObject>().Select((player, index) => (player, index)))
        {
            if (player.Obj("itemData") is { } item)
            {
                var id = Sets.FallbackItemBase + setIndex % Sets.FallbackSetsCycled * 1000 + slot * 100 + index;
                ids[item.Int("id")] = id;
                item["id"] = id;
            }
        }
        document["captain"] = ids.GetValueOrDefault(document.Int("captain"), document.Int("captain"));
        foreach (var taker in (document.Arr("kicktakers") ?? new JsonArray()).OfType<JsonObject>())
        {
            taker["id"] = ids.GetValueOrDefault(taker.Int("id"), taker.Int("id"));
        }
        var preset = Rules.Opponents[Math.Min(slot, Rules.Opponents.Count - 1)];
        var rating = DocumentRating(document);
        var badge = Rules.HomeAway.OpponentBadgeAssetId;
        var userHome = (setIndex + slot) % 2 == 0;
        var estDate = ParityEstDate(badge, rating, preset.Chemistry, Rules.Formation, StadiumId, userHome, 0);
        document["personaId"] = Sets.SetPersonaBase + slot;
        document["estDate"] = estDate;
        return new JsonObject
        {
            ["source"] = 0,
            ["info"] = Info(preset.ClubName, preset.ClubAbbr, badge, rating, preset.Chemistry, Rules.Formation, StadiumId, estDate, userHome, document),
            ["doc"] = document,
        };
    }

    private JsonObject Info(string clubName, string clubAbbr, long badge, long rating, long chemistry, string formation, long stadium, string estDate,
        bool userHome, JsonObject document) => new()
    {
        ["clubName"] = clubName,
        ["clubAbbr"] = clubAbbr,
        ["badge"] = badge,
        ["rating"] = rating,
        ["chemistry"] = chemistry,
        ["formation"] = formation,
        ["stadium"] = stadium,
        ["estDate"] = estDate,
        ["userHome"] = userHome,
        ["topPlayers"] = TopPlayers(document),
    };

    /// <summary>A stored number that counts even when it is 0 (<c>Int(key, fallback)</c> reads 0 as empty); <paramref name="missing"/> when absent.</summary>
    private static long Present(JsonObject source, string key, long missing) => JsonValues.TryInt(source.Get(key), out var value) ? value : missing;

    /// <summary>Three letters from the club name, as a generated squad's abbreviation is.</summary>
    private static string Abbreviation(string clubName) => (new string(clubName.Where(char.IsAsciiLetter).ToArray()).ToUpperInvariant() + "SQB")[..3];

    private List<JsonObject> Starters(JsonObject document) =>
        (document.Arr("players") ?? new JsonArray()).Take(Game.Rules.Club.Squad.Starters).OfType<JsonObject>()
            .Select(slot => slot.Obj("itemData")).OfType<JsonObject>().ToList();

    /// <summary>A squad document's rating: the mean of its starters' ratings, rounded as Python's round() does.</summary>
    private long DocumentRating(JsonObject document)
    {
        var ratings = Starters(document).Select(item => item.Int("rating")).ToList();
        return ratings.Count == 0 ? 0 : (long)Math.Round(ratings.Average(), MidpointRounding.ToEven);
    }

    /// <summary>The summary's three top starters, with the squad's own item ids (CardsDLL decodes topPlayers as {itemData} objects).</summary>
    private JsonArray TopPlayers(JsonObject document) =>
        new(Starters(document).OrderByDescending(item => item.Int("rating")).Take(Rules.TopPlayersShown)
            .Select(item => (JsonNode?)new JsonObject { ["itemData"] = item.Copy() }).ToArray());

    /// <summary>
    /// estDate for a summary, chosen so FIFA picks the intended home side: CardsDLL 0x1800f0300 sums the badge, rating,
    /// chemistry, formation enum, estDate, stadium and both kit team ids, an even sum putting the user at home.
    /// </summary>
    private string ParityEstDate(long badge, long rating, long chemistry, string formation, long stadium, bool userHome, long kitTeams)
    {
        var formationId = Game.Chemistry.FormationIds.TryGetValue(formation, out var id) ? id : Rules.HomeAway.FormationEnum;
        var total = badge + rating + chemistry + formationId + stadium + kitTeams;
        return (Rules.HomeAway.EstDateBase + ((total % 2 == 0) != userHome ? 1 : 0)).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A kit item as sqbt2data {teamId, category, year}: kitType 0/1/3 is category 2/3/5 (home, away, goalkeeper); year 0 is the packaged art.</summary>
    private static JsonObject? KitData(JsonObject? item) => item is not null && item.Int("teamid") > 0
        ? new JsonObject { ["teamId"] = item.Int("teamid"), ["category"] = item.Int("kitType") switch { 1 => 3, 3 => 5, _ => 2 }, ["year"] = 0 }
        : null;

    /// <summary>
    /// One hub tile (decoder 0x18033bc50), which also fills the opponent panel (0x1800f6410): nation, kits, tactics, formation,
    /// founding date, stadium and top players. A played tile shows its result and the difficulty played; pointsWon of 0 or
    /// more marks it played.
    /// </summary>
    private JsonObject Summary(JsonObject squad)
    {
        var info = squad.Obj("info") ?? new JsonObject();
        var document = squad.Obj("doc") ?? new JsonObject();
        var actives = (document.Arr("actives") ?? new JsonArray()).OfType<JsonObject>().ToList();
        var homeKit = KitData(actives.FirstOrDefault(item => item.Str("itemState") == "activeHomeKit"));
        var awayKit = KitData(actives.FirstOrDefault(item => item.Str("itemState") == "activeAwayKit"));
        var (rating, chemistry) = (info.Int("rating"), info.Int("chemistry"));
        var label = Sets.BucketLabels.GetValueOrDefault(squad.Str("bucket"), Sets.BucketLabels["MEDIUM"]);
        var summary = TemplateLibrary.Core.Object("squad-battles/opponent-summary", new TemplateArgs
        {
            // id and squadId are the same fixed-map key and both the zero-based slot.
            ["slot"] = squad.Int("id"),
            ["clubName"] = info.Str("clubName"),
            ["clubAbbr"] = info.Str("clubAbbr"),
            ["badge"] = Text(info.Int("badge")),
            ["rating"] = rating,
            ["chemistry"] = chemistry,
            ["formation"] = info.Str("formation"),
            ["difficulty"] = label,
            ["estDate"] = ParityEstDate(info.Int("badge"), rating, chemistry, info.Str("formation"), info.Int("stadium"), info.Bool("userHome"),
                (homeKit?.Int("teamId") ?? 0) + (awayKit?.Int("teamId") ?? 0)),
            ["nation"] = Sets.NationCode(Starters(document).Select(item => item.Int("nation"))),
            ["custom"] = Sets.TacticsCustom(document.Get("custom") is JsonValue custom && custom.TryGetValue(out string? text) ? text : null),
            ["basePointsWin"] = Competition.BasePoints(rating, chemistry, true),
            ["basePointsLoss"] = Competition.BasePoints(rating, chemistry, false),
            ["stadiumId"] = info.Int("stadium"),
            ["topPlayers"] = info.Arr("topPlayers")?.DeepClone() ?? new JsonArray(),
        });
        var data = summary.Obj("sqbt2data")!;
        if (homeKit is not null)
        {
            data["homeKitData"] = homeKit;
        }
        if (awayKit is not null)
        {
            data["awayKitData"] = awayKit;
        }
        if (squad.Obj("result") is { } result)
        {
            var played = Competition.DifficultyNames[Competition.ClampDifficulty(result.Int("difficulty"))];
            summary["pointsWon"] = result.Int("pointsWon");
            summary["score"] = result.Int("score");
            summary["oppScore"] = result.Int("oppScore");
            summary["penaltyScore"] = Present(result, "penaltyScore", -1);
            summary["oppPenaltyScore"] = Present(result, "oppPenaltyScore", -1);
            summary["difficulty"] = played;
            summary["difficultyName"] = played;
        }
        return summary;
    }

    private JsonArray Summaries(SquadBattlesSet set) => new(set.Squads.OfType<JsonObject>().Select(squad => (JsonNode?)Summary(squad)).ToArray());

    /// <summary>GET /sqbt/user/opponentsquad/{slot}: the frozen squad behind a hub tile (the generated one when the slot has none).</summary>
    public async ValueTask<JsonObject> SetDocumentAsync(IUnitOfWork uow, long slot)
    {
        var set = await CurrentSetAsync(uow, CurrentWeek);
        return set.Squads.OfType<JsonObject>().FirstOrDefault(squad => Present(squad, "id", -1) == slot)?.Obj("doc")?.Copy()
            ?? await OpponentAsync(uow, slot);
    }

    /// <summary>
    /// PUT /sqbt/v2/user/opponents/refresh: four new opponents (FIFA sends it only when the player asks, after
    /// nextSquadRefreshTimeStamp, 0x1800f4dd0, and replaces all four tiles with the reply, 0x1802992f0). That time is the draw
    /// itself (refreshSeconds 0): FIFA 21 retail let the player refresh whenever they wanted. Unplayed squads count toward the
    /// week's matches for no points (EA). A refresh not allowed (the week over or its matches used) returns the set unchanged.
    /// </summary>
    public async ValueTask<JsonObject> RefreshAsync(IUnitOfWork uow)
    {
        await uow.LockClubAsync();
        var week = await uow.SquadBattlesWeekAsync(CurrentWeek);
        var set = await CurrentSetAsync(uow, week.Week);
        var now = Game.NowSeconds;
        var counted = week.Counted(Competition.MaxRegularGames);
        if (now >= RefreshAt(set) && now < Competition.WeekEnd(week.Week) && counted < Competition.MaxRegularGames)
        {
            var unplayed = set.Squads.OfType<JsonObject>().Count(squad => squad.Obj("result") is null);
            var skipped = Math.Min(Competition.MaxRegularGames - counted, unplayed);
            week = week with { Skipped = week.Skipped + skipped };
            await uow.SaveSquadBattlesWeekAsync(week);
            set = await NewSetAsync(uow, week.Week, set.Index + 1, set.Faced);
            await uow.SaveSquadBattlesSetAsync(set);
            _log.LogWarning("SQBT REFRESH club={Club} set={Set} skipped={Skipped} counted={Counted}/{Max}", uow.ClubId, set.Index, skipped,
                week.Counted(Competition.MaxRegularGames), Competition.MaxRegularGames);
        }
        else
        {
            _log.LogWarning("SQBT REFRESH refused club={Club} next={Next} now={Now} counted={Counted}/{Max}", uow.ClubId, RefreshAt(set), now,
                counted, Competition.MaxRegularGames);
        }
        return TemplateLibrary.Core.Object("squad-battles/refresh", new TemplateArgs
        {
            ["counted"] = week.Counted(Competition.MaxRegularGames),
            ["refreshAt"] = RefreshAt(set),
            ["opponents"] = Summaries(set),
        });
    }
}
