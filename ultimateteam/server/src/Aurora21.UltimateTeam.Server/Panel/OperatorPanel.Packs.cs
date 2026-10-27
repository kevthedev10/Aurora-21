using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Pack definitions, unopened packs and recent panel gifts from Aurora 21's live database.
/// </summary>
public sealed partial class OperatorPanel
{
    private async Task<(int, object)?> PacksAsync(ApiRequest request)
    {
        if (request.Parts.Length == 1)
        {
            if (request.Method != "GET") throw new PanelError(405, "method not allowed");
            return (200, await PackOverviewAsync(request.Aborted));
        }
        return request.Parts[1] switch
        {
            "odds" when request.Parts.Length == 2 && request.Method == "POST" => (200, await PackOddsAsync(request)),
            "simulate" or "catalog" or "lineup" or "tokens-lineup" => await PackCatalogRouteAsync(request),
            _ => null,
        };
    }

    private async Task<object> PackOverviewAsync(CancellationToken cancellationToken)
    {
        var catalog = GrantablePackCatalog();
        var packs = catalog.Select(pair =>
        {
            var id = long.Parse(pair.Key, CultureInfo.InvariantCulture);
            var config = pair.Value as JsonObject ?? new JsonObject();
            var group = config.Text("store_group", "");
            return (object)new
            {
                id,
                storeId = id,
                name = config.Text("name", $"pack {id}"),
                category = group.Length > 0 ? group : "Store",
                categoryKey = group,
                items = config.Int("size", 12),
                rare = config.Int("rare_slots"),
                ratingMin = config.Get("min_rating") is null ? (long?)null : config.Int("min_rating"),
                ratingMax = config.Get("max_rating") is null ? (long?)null : config.Int("max_rating"),
                coins = config.Int("price"),
                points = config.Int("points_price"),
                blurb = config.Text("bio", ""),
                enabled = game.Packs.IsForSale(id),
            };
        }).ToArray();
        var clubs = await ClubsAsync(null, cancellationToken);
        var byPersona = issuer.Access.DiscordByPersona();
        var owned = (await game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync("""
            SELECT club_id,pack_id,count(*) FROM ultimateteam.unopened_packs GROUP BY club_id,pack_id ORDER BY club_id,pack_id
            """, row => (Club: row.GetInt64(0), Pack: row.GetInt64(1), Count: row.GetInt64(2))), cancellationToken))
            .GroupBy(row => row.Club).ToDictionary(group => group.Key, group => group.ToArray());
        var gifts = await game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync("""
            SELECT id,persona_id,patch->>'packId',patch->>'count',requested_by,created_at,applied_at,result FROM ultimateteam.panel_edits
            WHERE patch->>'op'='grantPacks' ORDER BY created_at DESC LIMIT 30
            """, row => (object)new
            {
                id = row.GetInt64(0), personaId = row.GetInt64(1),
                packId = long.TryParse(row.IsDBNull(2) ? null : row.GetString(2), CultureInfo.InvariantCulture, out var pack) ? pack : 0,
                count = long.TryParse(row.IsDBNull(3) ? null : row.GetString(3), CultureInfo.InvariantCulture, out var count) ? count : 0,
                requestedBy = row.IsDBNull(4) ? null : row.GetString(4), createdAt = Iso(row.GetDateTime(5)),
                appliedAt = row.IsDBNull(6) ? null : Iso(row.GetDateTime(6)), result = row.GetString(7),
            }), cancellationToken);
        return new
        {
            snapshot = new { status = "current", current = true, updatedAt = Iso(DateTimeOffset.UtcNow), ageMs = 0, pid = Environment.ProcessId, writerAlive = true },
            packs = new { version = 1, packs },
            grantable = GrantablePacks(),
            pileMax = new { value = MaximumUnopenedPacks, source = "server" },
            pileCap = new { value = MaximumUnopenedPacks, pileMax = MaximumUnopenedPacks, myPacksShown = MaximumUnopenedPacks, exact = true },
            futAlive = true,
            editOps = new[] { "grantPacks" },
            clubs = clubs.Select(club =>
            {
                var discordId = byPersona.GetValueOrDefault(club.PersonaId);
                var (name, abbr) = ClubIdentity(club.PersonaId, discordId, club.DisplayName);
                var held = owned.GetValueOrDefault(club.ClubId) ?? [];
                return new
                {
                    id = club.PersonaId, name, abbr, unopenedPacks = club.UnopenedPacks,
                    personaName = PlayerName(club.PersonaId, club.DisplayName),
                    online = discordId is not null && (issuer.Presence?.IsOnline(discordId) ?? false),
                    owned = new
                    {
                        total = club.UnopenedPacks, scanned = club.UnopenedPacks,
                        packs = held.Select(row => new { packId = row.Pack, count = row.Count, resolvesTo = row.Pack, name = PackName(row.Pack) }),
                    },
                    pending = (object?)null,
                    savedAt = Iso(DateTimeOffset.UtcNow),
                };
            }),
            grants = gifts,
        };
    }
}
