using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data.Records;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Cards (Aurora16 PANEL-CONTRACTS 6.4): the grantable catalogue, searched and filtered, and a summary of the special
/// types. Aurora 21's list is its live player catalogue (every card it can grant), so it is always current. A card key
/// is <c>resourceId:rating:rareflag</c>: Aurora 21's resource id already tells cards apart.
/// </summary>
public sealed partial class OperatorPanel
{
    // InvariantGlobalization disables runtime Unicode decomposition. Keep Latin name folding
    // explicit so Windows and the hosted image match accented names without requiring ICU.
    private static readonly Dictionary<int, string> SearchLatinLetters = new (string Plain, string Letters)[]
    {
        ("a", "ÀÁÂÃÄÅĀĂĄàáâãäåāăą"), ("c", "ÇĆĈĊČçćĉċč"), ("d", "ÐĎĐðďđ"),
        ("e", "ÈÉÊËĒĔĖĘĚèéêëēĕėęě"), ("g", "ĜĞĠĢĝğġģ"), ("h", "ĤĦĥħ"),
        ("i", "ÌÍÎÏĨĪĬĮİìíîïĩīĭįı"), ("j", "Ĵĵ"), ("k", "Ķķ"), ("l", "ĹĻĽĿŁĺļľŀł"),
        ("n", "ÑŃŅŇñńņň"), ("o", "ÒÓÔÕÖØŌŎŐòóôõöøōŏő"), ("r", "ŔŖŘŕŗř"),
        ("s", "ŚŜŞŠśŝşš"), ("t", "ŢŤŦţťŧ"), ("u", "ÙÚÛÜŨŪŬŮŰŲùúûüũūŭůűų"),
        ("w", "ŴẀẂẄŵẁẃẅ"), ("y", "ÝŸŶýÿŷ"), ("z", "ŹŻŽźżž"),
        ("ae", "Ææ"), ("oe", "Œœ"), ("ss", "ẞß"),
    }.SelectMany(group => group.Letters.EnumerateRunes().Select(rune => (rune.Value, group.Plain)))
        .ToDictionary(pair => pair.Value, pair => pair.Plain);

    private static string FoldSearchName(string text)
    {
        var folded = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                folded.Append(SearchLatinLetters.GetValueOrDefault(rune.Value) ?? Rune.ToLowerInvariant(rune).ToString());
        return folded.ToString();
    }

    private static readonly string[] OutfieldAttributes = ["PAC", "SHO", "PAS", "DRI", "DEF", "PHY"];
    private static readonly string[] GoalkeeperAttributes = ["DIV", "HAN", "KIC", "REF", "SPD", "POS"];

    private async Task<(int, object)?> CardsAsync(ApiRequest request)
    {
        if (request.Parts.Length == 1)
        {
            if (request.Method != "GET") throw new PanelError(405, "method not allowed");
            return (200, CardList(request));
        }
        if (request.Parts.Length >= 2 && request.Parts[1] is "editor" or "new" or "images") return await CardEditorAsync(request);
        if (request.Parts is ["cards", "filters"])
        {
            if (request.Method != "GET") throw new PanelError(405, "method not allowed");
            var catalog = game.Live.Snapshot.Catalog;
            var players = game.Players.Entries.Select(entry => entry.Value).ToArray();
            var teams = catalog.Teams.ToDictionary(team => team.NativeTeamId, team => team.Name);
            var leagues = catalog.Leagues.ToDictionary(league => league.NativeLeagueId, league => league.Name);
            return (200, new
            {
                nations = catalog.Nations.OrderBy(n => n.Name).Select(n => new { id = n.NativeNationId, name = n.Name }),
                leagues = catalog.Leagues.OrderBy(l => l.Name).Select(l => new { id = l.NativeLeagueId, name = l.Name }),
                clubs = players.Where(p => p.Int("teamid") > 0).GroupBy(p => (Team: p.Int("teamid"), League: p.Int("leagueId")))
                    .Select(g => new { id = g.Key.Team, leagueId = g.Key.League,
                        name = teams.GetValueOrDefault(g.Key.Team, $"Club {g.Key.Team}"),
                        leagueName = leagues.GetValueOrDefault(g.Key.League, $"League {g.Key.League}") })
                    .OrderBy(c => c.name).ThenBy(c => c.leagueName).ToArray(),
            });
        }
        if (request.Parts is ["cards", "items"])
        {
            if (request.Method != "GET") throw new PanelError(405, "method not allowed");
            return (200, ItemList(request));
        }
        if (request.Parts.Length == 2 && request.Parts[1] == "specials")
        {
            if (request.Method != "GET") throw new PanelError(405, "method not allowed");
            return (200, Specials());
        }
        return null;
    }

    private object CardList(ApiRequest request)
    {
        var snapshot = game.Live.Current;
        var promoCode = request.Query("promo").Trim();
        var grouped = request.Query("group") == "buckets";
        if (grouped && promoCode.Length == 0) throw new PanelError(400, "Bucket grouping requires a promo.");
        PromoRecord? selectedPromo = null;
        HashSet<long>? promoResources = null;
        if (promoCode.Length > 0)
        {
            selectedPromo = snapshot.Catalog.Promos.SingleOrDefault(p => p.Code == promoCode)
                ?? throw new PanelError(404, "No such promo.");
            promoResources = snapshot.Catalog.PromoCards.Where(m => m.PromoId == selectedPromo.PromoId)
                .Select(m => snapshot.Catalog.DefinitionsById[m.PlayerCardDefinitionId].NativeResourceId!.Value.Value).ToHashSet();
        }
        var query = request.Query("q").Trim();
        var foldedQuery = FoldSearchName(query);
        var resourceText = request.Query("resourceId").Trim();
        long? exactResource = null;
        if (resourceText.Length > 0)
        {
            if (!long.TryParse(resourceText, NumberStyles.None, CultureInfo.InvariantCulture, out var resource) || resource < 1)
                throw new PanelError(400, "resourceId must be a positive card resource id.");
            exactResource = resource;
        }
        HashSet<long>? resourceIds = null;
        var resourcesText = request.Query("resourceIds");
        if (resourcesText.Length > 0)
        {
            var values = resourcesText.Split(',');
            if (values.Length > 200 || values.Any(value => !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1))
                throw new PanelError(400, "resourceIds needs 1–200 positive native resource ids.");
            resourceIds = values.Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToHashSet();
        }
        var position = request.Query("pos").Trim().ToUpperInvariant();
        var positions = position.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var nation = QueryInt(request, "nation", 0, 0, int.MaxValue);
        var league = QueryInt(request, "league", 0, 0, int.MaxValue);
        var team = QueryInt(request, "team", 0, 0, int.MaxValue);
        var special = request.Query("special").Trim();
        var rare = request.Query("rare").Trim();
        if (special.Length > 0 && special is not ("any" or "base") && !(special.Length <= 3 && special.All(char.IsAsciiDigit)))
            throw new PanelError(400, "special must be any, base or a rareflag.");
        if (rare.Length > 0 && rare is not ("0" or "1")) throw new PanelError(400, "rare must be 0 or 1.");
        var minimum = QueryInt(request, "min", 0, 0, 99);
        var maximum = QueryInt(request, "max", 99, 0, 99);
        var offset = QueryInt(request, "offset", 0, 0, 1_000_000);
        var limit = QueryInt(request, "limit", 50, 1, 200);
        var sort = request.Query("sort").Trim();
        var direction = request.Query("direction").Trim();
        if (sort.Length > 0 && sort is not ("name" or "rating" or "pos" or "skill" or "weak" or "work" or "PAC" or "SHO" or "PAS" or "DRI" or "DEF" or "PHY" or "type" or "nation" or "packs"))
            throw new PanelError(400, "Unknown card sort column.");
        if (direction.Length > 0 && direction is not ("asc" or "desc")) throw new PanelError(400, "direction must be asc or desc.");
        var keyed = ResolveCardKey(query);
        var matches = snapshot.Players.Entries.Where(entry =>
        {
            var card = entry.Value;
            if (promoResources is not null && !promoResources.Contains(entry.Key)) return false;
            if (resourceIds is not null && !resourceIds.Contains(entry.Key)) return false;
            if (exactResource.HasValue) return entry.Key == exactResource.Value;
            if (keyed is not null) return entry.Key == keyed;
            if (query.Length > 0 && !FoldSearchName(card.Text("name")).Contains(foldedQuery, StringComparison.Ordinal) &&
                !entry.Key.ToString(CultureInfo.InvariantCulture).Equals(query, StringComparison.Ordinal) &&
                !card.Int("assetId").ToString(CultureInfo.InvariantCulture).Equals(query, StringComparison.Ordinal)) return false;
            if (positions.Count > 0 && !positions.Contains(card.Position)) return false;
            if (nation > 0 && card.Int("nation") != nation) return false;
            if (league > 0 && card.Int("leagueId") != league) return false;
            if (team > 0 && card.Int("teamid") != team) return false;
            if (card.Rating < minimum || card.Rating > maximum) return false;
            if (rare.Length > 0 && card.Rareflag != long.Parse(rare, CultureInfo.InvariantCulture)) return false;
            return special switch
            {
                "" => true,
                "any" => card.Rareflag >= 3,
                "base" => card.Rareflag is 0 or 1,
                _ => card.Rareflag == long.Parse(special, CultureInfo.InvariantCulture),
            };
        }).ToList();
        IComparable SortValue(long resourceId, PlayerMeta card) => sort switch
        {
            "name" => card.Text("name").ToUpperInvariant(),
            "pos" => card.Position,
            "skill" => card.Int("skillMoves"),
            "weak" => card.Int("weakFoot"),
            "work" => card.Text("workRateAttack") + "/" + card.Text("workRateDefense"),
            "type" => card.Rareflag >= 3 ? card.Text("rarityName") : card.Rareflag == 1 ? "Rare" : "Common",
            "nation" => game.Live.Snapshot.Catalog.Nations.FirstOrDefault(n => n.NativeNationId == card.Int("nation"))?.Name ?? card.Text("nationName"),
            "packs" => game.Cards.IsGrantOnly(resourceId) ? 0 : 1,
            "PAC" or "SHO" or "PAS" or "DRI" or "DEF" or "PHY" => card.Get("face") is JsonArray face
                && Array.IndexOf(OutfieldAttributes, sort) is var index && index < face.Count ? face[index]?.GetValue<long>() ?? 0 : 0L,
            _ => card.Rating,
        };
        var ordered = direction == "asc"
            ? matches.OrderBy(entry => SortValue(entry.Key, entry.Value))
            : matches.OrderByDescending(entry => SortValue(entry.Key, entry.Value));
        matches = ordered.ThenBy(entry => entry.Value.Text("name"), StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Key).ToList();
        var sections = new List<object>();
        if (grouped)
        {
            var memberships = snapshot.Catalog.PromoCards.Where(m => m.PromoId == selectedPromo!.PromoId).ToArray();
            var byResource = memberships.ToDictionary(m => snapshot.Catalog.DefinitionsById[m.PlayerCardDefinitionId].NativeResourceId!.Value.Value, m => m.BucketId);
            var buckets = snapshot.Catalog.PromoBuckets.Where(b => b.PromoId == selectedPromo!.PromoId && (b.Enabled || memberships.Any(m => m.BucketId == b.Id)))
                .OrderBy(b => b.Code).ToArray();
            foreach (var bucket in buckets)
            {
                var bucketMatches = matches.Where(e => byResource.GetValueOrDefault(e.Key) == bucket.Id).ToArray();
                var page = QueryInt(request, $"bucketPage.{bucket.Code}", 1, 1, 1_000_000 / limit + 1);
                var bucketOffset = (page - 1) * limit;
                sections.Add(new { code = bucket.Code, name = bucket.Name, weight = bucket.DrawWeight, enabled = bucket.Enabled,
                    count = memberships.Count(m => m.BucketId == bucket.Id), total = bucketMatches.Length, offset = bucketOffset, limit,
                    cards = bucketMatches.Skip(bucketOffset).Take(limit).Select(e => CardView(e.Key, e.Value)).ToArray() });
            }
            var bucketIds = buckets.Select(b => b.Id).ToHashSet();
            var unassigned = matches.Where(e => !bucketIds.Contains(byResource.GetValueOrDefault(e.Key))).ToArray();
            if (unassigned.Length > 0)
            {
                var page = QueryInt(request, "bucketPage.unassigned", 1, 1, 1_000_000 / limit + 1);
                sections.Add(new { code = "unassigned", name = "Unassigned cards", weight = 0, enabled = false, count = unassigned.Length,
                    total = unassigned.Length, offset = (page - 1) * limit, limit, cards = unassigned.Skip((page - 1) * limit).Take(limit).Select(e => CardView(e.Key, e.Value)).ToArray() });
            }
        }
        return new
        {
            source = "fut",
            stale = false,
            snapshotAt = Iso(Started),
            snapshotStatus = "current",
            snapshotProblem = (string?)null,
            specialsMode = "all",
            flags = SpecialFlags(),
            grant = new { available = true, reason = (string?)null, countMax = 1 },
            poolSize = promoResources?.Count ?? snapshot.Players.Count,
            total = matches.Count,
            offset,
            limit,
            positions = game.Players.Entries.Select(entry => entry.Value.Position).Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            attrs = OutfieldAttributes,
            gkAttrs = GoalkeeperAttributes,
            cards = grouped ? [] : matches.Skip(offset).Take(limit).Select(entry => CardView(entry.Key, entry.Value)).ToArray(),
            buckets = grouped ? sections.ToArray() : null,
        };
    }

    /// <summary>A card's six face stats in FIFA's order (the goalkeeper ones for a keeper), or none when the catalogue has no face.</summary>
    private static long[] FaceStats(PlayerMeta card) =>
        card.Get("face") is JsonArray face ? face.Select(value => value?.GetValue<long>() ?? 0).ToArray() : [];

    private object CardView(long resourceId, PlayerMeta card) => new
    {
        cardKey = $"{resourceId}:{card.Rating}:{card.Rareflag}",
        resourceId,
        assetId = card.Int("assetId"),
        name = card.Text("name"),
        rating = card.Rating,
        rareflag = card.Rareflag,
        special = card.Rareflag >= 3 ? card.Text("rarityName") : null,
        position = card.Position,
        skillMoves = card.Int("skillMoves"),
        weakFoot = card.Int("weakFoot"),
        workRateAttack = card.Text("workRateAttack"),
        workRateDefense = card.Text("workRateDefense"),
        attributes = FaceStats(card),
        nation = game.Live.Snapshot.Catalog.Nations.FirstOrDefault(n => n.NativeNationId == card.Int("nation"))?.Name ?? card.Text("nationName"),
        teamId = card.Int("teamid"),
        leagueId = card.Int("leagueId"),
        source = card.Rareflag >= 3 ? "real" : "base",
        inPacks = !game.Cards.IsGrantOnly(resourceId),
        forcedUntradeable = game.Cards.IsDeveloperCard(resourceId),
        art = CardArt(resourceId),
    };

    /// <summary>Each special rareflag (3 and up) with its name from the catalogue.</summary>
    private Dictionary<string, string> SpecialFlags() => game.Players.Entries
        .Where(entry => entry.Value.Rareflag >= 3)
        .GroupBy(entry => entry.Value.Rareflag)
        .OrderBy(group => group.Key)
        .ToDictionary(group => group.Key.ToString(CultureInfo.InvariantCulture),
            group => group.Select(entry => entry.Value.Text("rarityName")).FirstOrDefault(name => name.Length > 0) ?? $"Type {group.Key}");

    private object Specials()
    {
        var types = game.Players.Entries.Where(entry => entry.Value.Rareflag >= 3).GroupBy(entry => entry.Value.Rareflag).OrderBy(group => group.Key)
            .Select(group => new
            {
                flag = group.Key,
                name = group.Select(entry => entry.Value.Text("rarityName")).FirstOrDefault(name => name.Length > 0) ?? $"Type {group.Key}",
                available = group.LongCount(),
                inPacks = group.LongCount(entry => !game.Cards.IsGrantOnly(entry.Key)),
                boost = (long?)null,
                anyTier = false,
            }).ToArray();
        return new
        {
            status = "current",
            current = true,
            mode = "all",
            modeRaw = "all",
            source = "real",
            week = (long?)null,
            skipped = Array.Empty<long>(),
            rules = new { rate = 0 },
            types,
            snapshotAt = Iso(Started),
            problem = (string?)null,
            flags = SpecialFlags(),
            poolSize = game.Players.Count,
            duplicateKeys = 0,
            builtAt = Iso(Started),
            catalogueSize = game.Players.Count,
            grant = new { available = true, reason = (string?)null, countMax = 1 },
        };
    }
}
