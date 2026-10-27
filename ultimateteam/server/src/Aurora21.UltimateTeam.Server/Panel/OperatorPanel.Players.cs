using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Server.Access;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Players (Aurora16 PANEL-CONTRACTS 6.1): everyone with a club or a verified launch, one player's club, inventory,
/// sessions and panel edits, and every change to a club through <c>PATCH players/&lt;personaId&gt;</c>. A player's id is
/// their persona id, which Aurora 21 derives from their Discord id; the Discord id and its access (blocked, maintenance
/// bypass) ride along, and the <c>access</c> op edits it. Who may play at all is decided by Discord roles (Maintenance).
/// </summary>
public sealed partial class OperatorPanel
{
    private const long MaximumBalance = 99_999_999;
    private const int MaximumUnopenedPacks = 100;
    private const int MaximumInventoryRows = 2000;

    /// <summary>The club edits this server applies (Aurora16 fut.config <c>edits</c>): the page disables the others.</summary>
    private static readonly object EditOps = new { ops = new[] { "grantPacks", "grantCard", "grantItem", "removeItems", "takeItem", "grantSeasonXp", "access", "resetClub" }, deltas = true };

    private sealed record ClubSummary(long ClubId, long PersonaId, string? DisplayName, long Coins, long Points, long ClubItems,
        long NewItems, long TradePile, long UnopenedPacks, DateTime CreatedAt);

    private sealed record SessionSummary(long Logins, DateTime? LastLogin, string? LastRemote);

    private async Task<(int, object)?> PlayersAsync(ApiRequest request)
    {
        var parts = request.Parts;
        if (parts.Length == 1)
        {
            if (request.Method != "GET") throw new PanelError(405, "method not allowed");
            return (200, await PlayerListAsync(request.Aborted));
        }
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var personaId))
            throw new PanelError(400, "A player is identified by their persona id.");
        if (parts.Length == 2 && request.Method == "GET") return (200, await PlayerDetailAsync(personaId, request.Aborted));
        if (parts.Length == 2 && request.Method == "PATCH") return await EditPlayerAsync(request, personaId);
        if (parts.Length == 3 && parts[2] == "inventory")
        {
            if (request.Method != "GET") throw new PanelError(405, "method not allowed");
            return (200, await InventoryAsync(request, personaId));
        }
        if (parts.Length <= 3) throw new PanelError(405, "method not allowed");
        return null;
    }

    // ------------------------------------------------------------ reads

    private async Task<object> PlayerListAsync(CancellationToken cancellationToken)
    {
        var clubs = await ClubsAsync(null, cancellationToken);
        var sessions = await SessionSummariesAsync(cancellationToken);
        var access = issuer.Access;
        var byPersona = access.DiscordByPersona();
        var rows = new List<object>();
        var seen = new HashSet<long>();
        foreach (var club in clubs)
        {
            seen.Add(club.PersonaId);
            rows.Add(PlayerRow(club.PersonaId, byPersona.GetValueOrDefault(club.PersonaId), club, sessions));
        }
        foreach (var entry in access.Players)
        {
            var persona = PlayerIdentity.Of(entry.DiscordId).PersonaId;
            if (seen.Add(persona)) rows.Add(PlayerRow(persona, entry.DiscordId, null, sessions));
        }
        return new { players = rows, futAlive = true };
    }

    private object PlayerRow(long personaId, string? discordId, ClubSummary? club, IReadOnlyDictionary<string, SessionSummary> sessions)
    {
        var entry = discordId is null ? null : issuer.Access.Find(discordId);
        var session = discordId is null ? null : sessions.GetValueOrDefault(discordId);
        var (name, abbr) = ClubIdentity(personaId, discordId, club?.DisplayName ?? entry?.DisplayName);
        return new
        {
            id = personaId,
            discordId,
            name = PlayerName(personaId, club?.DisplayName ?? entry?.DisplayName),
            club = club is null ? null : new { name, abbr, coins = club.Coins, points = club.Points, items = club.ClubItems },
            lastRemote = session?.LastRemote,
            logins = session?.Logins ?? 0,
            lastLogin = Iso(session?.LastLogin) ?? Iso(entry?.LastLaunchAt),
            online = discordId is not null && (issuer.Presence?.IsOnline(discordId) ?? false),
            sharedAddress = (string?)null,
            access = AccessOf(entry),
        };
    }

    private async Task<object> PlayerDetailAsync(long personaId, CancellationToken cancellationToken)
    {
        var discordId = issuer.Access.DiscordByPersona().GetValueOrDefault(personaId);
        var club = (await ClubsAsync(personaId, cancellationToken)).SingleOrDefault();
        var entry = discordId is null ? null : issuer.Access.Find(discordId);
        if (club is null && entry is null) throw new PanelError(404, "No player or club has that persona id.");
        var sessions = discordId is null ? [] : await game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync("""
            SELECT remote,connected_at,disconnected_at FROM ultimateteam.player_sessions WHERE discord_id=@id
            ORDER BY connected_at DESC LIMIT 50
            """, row => (object)new { remote = row.IsDBNull(0) ? null : row.GetString(0), connected_at = Iso(row.GetDateTime(1)),
                disconnected_at = row.IsDBNull(2) ? null : Iso(row.GetDateTime(2)) }, ("id", discordId)), cancellationToken);
        var summary = discordId is null ? null : (await SessionSummariesAsync(cancellationToken)).GetValueOrDefault(discordId);
        var edits = await game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync("""
            SELECT id,patch::text,requested_by,result,created_at,applied_at FROM ultimateteam.panel_edits
            WHERE persona_id=@persona ORDER BY created_at DESC LIMIT 50
            """, row => (object)new { id = row.GetInt64(0), patch = row.GetString(1), requested_by = row.IsDBNull(2) ? null : row.GetString(2),
                result = row.GetString(3), created_at = Iso(row.GetDateTime(4)), applied_at = row.IsDBNull(5) ? null : Iso(row.GetDateTime(5)) },
            ("persona", personaId)), cancellationToken);
        object? inventory = null, squads = Array.Empty<object>();
        long? seasonXp = null;
        if (club is not null)
        {
            seasonXp = await game.UnitOfWork.RunReadAsync(club.ClubId,
                async work => (await work.ObjectivesAsync(game.Rules.Objectives)).UserXp, cancellationToken);
            inventory = await InventoryCountsAsync(club, cancellationToken);
            squads = await game.UnitOfWork.RunReadAsync(club.ClubId, work => work.QueryAsync("""
                SELECT s.native_squad_id,s.name,s.formation,(SELECT count(*) FROM ultimateteam.squad_players p WHERE p.squad_id=s.id)
                FROM ultimateteam.squads s WHERE s.club_id=@club ORDER BY s.native_squad_id
                """, row => (object)new { id = row.GetInt64(0), name = row.GetString(1), formation = row.GetString(2),
                    rating = (long?)null, chemistry = (long?)null, players = row.GetInt64(3) }), cancellationToken);
        }
        var (name, abbr) = ClubIdentity(personaId, discordId, club?.DisplayName ?? entry?.DisplayName);
        return new
        {
            id = personaId,
            discordId,
            name = PlayerName(personaId, club?.DisplayName ?? entry?.DisplayName),
            online = discordId is not null && (issuer.Presence?.IsOnline(discordId) ?? false),
            sharedAddress = (string?)null,
            lastRemote = summary?.LastRemote,
            logins = summary?.Logins ?? 0,
            lastLogin = Iso(summary?.LastLogin) ?? Iso(entry?.LastLaunchAt),
            futAlive = true,
            club = club is null ? null : new
            {
                name, abbr, coins = club.Coins, points = club.Points, items = club.ClubItems, tradePile = club.TradePile,
                unopenedPacks = club.UnopenedPacks, seasonXp,
            },
            inventory,
            grantablePacks = GrantablePacks(),
            editOps = EditOps,
            identityCollision = (object?)null,
            squadList = squads,
            sessions,
            edits,
            access = AccessOf(entry),
        };
    }

    private object? AccessOf(PlayerAccessEntry? entry) => entry is null ? null : new
    {
        blocked = entry.Blocked,
        maintenanceBypass = entry.MaintenanceBypass,
        note = entry.Note,
        lastLaunchAt = Iso(entry.LastLaunchAt),
        playRolesRequired = issuer.Access.Settings.PlayRoles.Count > 0,
    };

    private async Task<object> InventoryCountsAsync(ClubSummary club, CancellationToken cancellationToken)
    {
        var byKind = await game.UnitOfWork.RunReadAsync(club.ClubId, work => work.QueryAsync("""
            SELECT i.family_id,count(*) FROM ultimateteam.items i WHERE i.club_id=@club GROUP BY i.family_id
            """, row => (Family: row.GetInt16(0), Count: row.GetInt64(1))), cancellationToken);
        var vanity = await game.UnitOfWork.RunReadAsync(club.ClubId, work => work.QueryAsync(
            "SELECT stored, count(*) FROM ultimateteam.club_vanity_items WHERE club_id=@club GROUP BY stored",
            row => (Stored: row.GetBoolean(0), Count: row.GetInt64(1))), cancellationToken);
        long Vanity(bool stored) => vanity.Where(row => row.Stored == stored).Sum(row => row.Count);
        var total = byKind.Sum(kind => kind.Count) + Vanity(true) + Vanity(false);
        return new
        {
            club = club.ClubItems + Vanity(true), @new = club.NewItems + Vanity(false), transfer = club.TradePile, starter = 0, total,
            byKind = new
            {
                player = byKind.Where(kind => kind.Family == 1).Sum(kind => kind.Count),
                manager = byKind.Where(kind => kind.Family == 2).Sum(kind => kind.Count),
                consumable = byKind.Where(kind => kind.Family is 3 or 5).Sum(kind => kind.Count),
                clubItem = byKind.Where(kind => kind.Family == 4).Sum(kind => kind.Count) + Vanity(true) + Vanity(false),
                other = byKind.Where(kind => kind.Family is < 1 or > 5).Sum(kind => kind.Count),
            },
            unopenedPacks = club.UnopenedPacks,
        };
    }

    /// <summary>One pile's items (Aurora16's club / new / transfer / starter piles; Aurora 21 has no starter pile).</summary>
    private async Task<object> InventoryAsync(ApiRequest request, long personaId)
    {
        var pile = request.Query("pile");
        if (pile is not ("all" or "club" or "new" or "transfer" or "starter")) throw new PanelError(400, "pile must be all, club, new, transfer or starter.");
        var club = (await ClubsAsync(personaId, request.Aborted)).SingleOrDefault() ?? throw new PanelError(404, "That persona has no club yet.");
        var piles = pile == "all" ? new[] { "club", "new", "transfer", "starter" } : new[] { pile };
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var truncated = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in piles)
        {
            var stored = name switch { "club" => "club", "new" => "unassigned", "transfer" => "trade", _ => null };
            if (stored is null)
            {
                result[name] = Array.Empty<object>();
                truncated[name] = false;
                continue;
            }
            var rows = await game.UnitOfWork.RunReadAsync(club.ClubId, work => work.QueryAsync("""
                SELECT i.native_item_id,i.family_id,i.untradeable,d.native_resource_id,
                    EXISTS(SELECT 1 FROM ultimateteam.squad_players p JOIN ultimateteam.squads s ON s.id=p.squad_id
                        WHERE s.club_id=i.club_id AND p.item_id=i.id)
                    OR EXISTS(SELECT 1 FROM ultimateteam.squad_managers m JOIN ultimateteam.squads s ON s.id=m.squad_id
                        WHERE s.club_id=i.club_id AND m.item_id=i.id),
                    c.customisation_kind,
                    EXISTS(SELECT 1 FROM ultimateteam.club_slot_bindings b WHERE b.club_id=i.club_id AND b.item_id=i.id),
                    EXISTS(SELECT 1 FROM ultimateteam.market_listings l WHERE l.seller_club_id=i.club_id
                        AND l.seller_item_id=i.native_item_id AND l.state='active')
                FROM ultimateteam.items i LEFT JOIN ultimateteam.item_definitions d ON d.definition_id=i.definition_id
                LEFT JOIN ultimateteam.customisation_definitions c ON c.definition_id=i.definition_id
                WHERE i.club_id=@club AND i.pile=@pile ORDER BY i.native_item_id LIMIT @limit
                """, row => (Item: row.GetInt64(0), Family: row.GetInt16(1), Untradeable: row.GetBoolean(2),
                    Resource: row.IsDBNull(3) ? (long?)null : row.GetInt64(3), InSquad: row.GetBoolean(4),
                    Customisation: row.IsDBNull(5) ? null : row.GetString(5), Equipped: row.GetBoolean(6), Listed: row.GetBoolean(7)),
                ("pile", stored), ("limit", MaximumInventoryRows + 1)), request.Aborted);
            truncated[name] = rows.Count > MaximumInventoryRows;
            var listed = rows.Take(MaximumInventoryRows).Select(row =>
            {
                var card = row.Family == 1 && row.Resource is { } resource ? game.Players.Find(resource) : null;
                var item = row.Resource is { } id ? InventoryItemType(row.Family, row.Customisation, id) : null;
                return (object)new
                {
                    id = row.Item,
                    assetId = card?.Int("assetId") ?? row.Resource,
                    resourceId = row.Resource,
                    name = card?.Text("name") ?? item?.Name ?? (row.Family switch { 2 => "Manager", 3 => "Consumable", 4 => "Club item", 5 => "Player pick", _ => "Item" }),
                    typeName = item?.TypeName ?? (row.Family == 5 ? "Player pick" : null),
                    rating = card?.Rating ?? item?.Rating ?? 0,
                    position = card?.Position,
                    attributes = card is null ? null : FaceStats(card),
                    kind = row.Family switch { 1 => card?.IsSpecial == true ? "special" : "player", 2 => "manager", 3 or 5 => "consumable", 4 => "clubItem", _ => "other" },
                    special = card?.IsSpecial == true ? card.Text("rarityName") : null,
                    untradeable = row.Untradeable,
                    source = "opened",
                    loans = 0,
                    suspendedGames = 0,
                    inSquads = row.InSquad ? new[] { "active" } : Array.Empty<string>(),
                    equipped = row.Equipped,
                    listed = row.Listed,
                    // The removeItems edit takes player cards that are not on the market (ClubInventoryService.RemoveItemsAsync).
                    removable = row.Family == 1 && !row.Listed,
                    art = row.Family == 1 ? CardArt(row.Resource) : null,
                };
            }).ToList();
            // Granted My Stadium items are club unlocks, not owned items: a kept one shows in the club pile, a waiting one in New items.
            if (name is "club" or "new") listed.AddRange(await VanityInventoryAsync(club.ClubId, stored: name == "club", request.Aborted));
            result[name] = listed.ToArray();
        }
        var packs = await game.UnitOfWork.RunReadAsync(club.ClubId, work => work.QueryAsync("""
            SELECT pack_id,count(*) FROM ultimateteam.unopened_packs WHERE club_id=@club GROUP BY pack_id ORDER BY pack_id
            """, row => (object)new { packId = row.GetInt64(0), count = row.GetInt64(1),
                name = PackName(row.GetInt64(0)) }), request.Aborted);
        return new
        {
            id = personaId,
            savedAt = Iso(DateTimeOffset.UtcNow),
            futAlive = true,
            counts = await InventoryCountsAsync(club, request.Aborted),
            piles = result,
            truncated,
            packs,
            squads = Array.Empty<object>(),
            problems = new { },
            sources = new { },
            notes = new[] { "Live from the database: Aurora 21 writes every change at once, so there is no separate last save. Aurora 21 has no starter pile." },
        };
    }

    // ------------------------------------------------------------ edits

    private async Task<(int, object)?> EditPlayerAsync(ApiRequest request, long personaId)
    {
        var body = request.Body;
        if (body.ValueKind != JsonValueKind.Object) throw new PanelError(400, "Send the change as a JSON object.");
        var patch = new JsonObject();
        foreach (var property in body.EnumerateObject().Where(property => property.Name != "confirmOnline"))
            patch[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        var op = TextField(body, "op");
        var discordId = issuer.Access.DiscordByPersona().GetValueOrDefault(personaId);
        // Each edit applies in its own transaction or is refused whole; a refusal changes nothing and is not recorded.
        if (op == "access")
        {
            if (discordId is null) throw new PanelError(404, "That persona belongs to no Discord account, so it has no access to change.");
            await EditAccessAsync(body, discordId, request.Aborted);
        }
        else
        {
            var club = (await ClubsAsync(personaId, request.Aborted)).SingleOrDefault()
                ?? throw new PanelError(404, "That persona has no club yet; the server creates it on their first request.");
            if (op == "resetClub") return await ResetPlayerClubAsync(request, club, discordId);
            switch (op)
            {
                case null: await EditBalancesAsync(body, club, request.Aborted); break;
                case "grantPacks": await GrantPacksAsync(body, club, request.Aborted); break;
                case "grantCard": await GrantCardAsync(body, club, discordId, request.Aborted); break;
                case "grantItem": await GrantItemAsync(body, club, discordId, request.Aborted); break;
                case "removeItems": await RemoveItemsAsync(body, club, discordId, request.Aborted); break;
                case "grantSeasonXp": await GrantSeasonXpAsync(body, club, request.Aborted); break;
                case "takeItem": await TakeItemAsync(body, club, discordId, request.Aborted); break;
                default: throw new PanelError(400, $"Aurora 21 does not apply the {op} edit.");
            }
        }
        await RecordEditAsync(personaId, patch, request.Remote, "ok", request.Aborted);
        _log.LogInformation("PANEL {Remote} applied edit for persona {Persona}: {Patch}", request.Remote, personaId, patch.ToJsonString());
        return (200, await PlayerDetailAsync(personaId, request.Aborted));
    }

    private async Task EditAccessAsync(JsonElement body, string discordId, CancellationToken cancellationToken)
    {
        foreach (var property in body.EnumerateObject())
            if (property.Name is not ("op" or "blocked" or "maintenanceBypass" or "note" or "confirmOnline"))
                throw new PanelError(400, $"The access edit does not take {property.Name}.");
        var note = Field(body, "note") is { } noteNode
            ? noteNode.ValueKind == JsonValueKind.String ? noteNode.GetString() : throw new PanelError(400, "note must be text.")
            : null;
        var wasBlocked = issuer.Access.Find(discordId)?.Blocked == true;
        PlayerAccessEntry entry;
        try
        {
            entry = await issuer.Access.UpdatePlayerAsync(discordId, FlagField(body, "blocked"), FlagField(body, "maintenanceBypass"),
                note, cancellationToken);
        }
        catch (ArgumentException error) { throw new PanelError(400, error.Message); }
        if (entry.Blocked && !wasBlocked)
        {
            var closed = issuer.Revoke(discordId);
            _log.LogInformation("PANEL blocked {Discord}: codes and session revoked, {Closed} Blaze connection(s) closed", discordId, closed);
        }
    }

    /// <summary>
    /// The Club form sets coins and FIFA Points; the balance card adds or takes away (Aurora16 2.3). The club name and
    /// abbreviation are the player's (main's ClubNames), so they are refused rather than silently ignored.
    /// </summary>
    private async Task EditBalancesAsync(JsonElement body, ClubSummary club, CancellationToken cancellationToken)
    {
        if (Field(body, "clubName") is not null || Field(body, "clubAbbr") is not null)
            throw new PanelError(400, "Aurora 21 names each club after its player (their Discord name), so the club name and abbreviation cannot be set here.");
        var coins = WholeField(body, "coins");
        var points = WholeField(body, "points");
        var coinsDelta = WholeField(body, "coinsDelta");
        var pointsDelta = WholeField(body, "pointsDelta");
        if ((coinsDelta is not null || pointsDelta is not null) && (coins is not null || points is not null))
            throw new PanelError(400, "Send a balance change (coinsDelta, pointsDelta) on its own, not with other fields.");
        if (coins is null && points is null && coinsDelta is null && pointsDelta is null) throw new PanelError(400, "Nothing to change.");
        foreach (var (name, value) in new[] { ("coins", coins), ("points", points) })
            if (value is < 0 or > MaximumBalance) throw new PanelError(400, $"{name} must be a whole number from 0 to 99,999,999.");
        foreach (var (name, value) in new[] { ("coinsDelta", coinsDelta), ("pointsDelta", pointsDelta) })
            if (value is 0 or < -MaximumBalance or > MaximumBalance) throw new PanelError(400, $"{name} must be a non-zero whole number from -99,999,999 to 99,999,999.");
        await game.UnitOfWork.RunAsync(club.ClubId, "panel-balance", async work =>
        {
            await work.LockRelationalClubAsync();
            var before = await work.RelationalFinancialSummaryAsync();
            // A grant never lifts past the maximum nor lowers a balance already above it; a take-away stops at 0.
            static long Next(long current, long? set, long? delta) => set ?? (delta is not { } change ? current
                : change > 0 ? Math.Max(current, Math.Min(MaximumBalance, current + change)) : Math.Max(0, current + change));
            await work.ExecuteAsync("UPDATE ultimateteam.clubs SET credits=@coins,fifa_points=@points WHERE id=@club",
                ("coins", Next(before.Credits, coins, coinsDelta)), ("points", Next(before.FifaPoints, points, pointsDelta)));
        }, cancellationToken);
    }

    private async Task GrantSeasonXpAsync(JsonElement body, ClubSummary club, CancellationToken cancellationToken)
    {
        foreach (var property in body.EnumerateObject())
            if (property.Name is not ("op" or "amount" or "confirmOnline"))
                throw new PanelError(400, $"The Season XP grant does not take {property.Name}.");
        var amount = WholeField(body, "amount");
        if (amount is null or < 1 or > MaximumBalance)
            throw new PanelError(400, "amount must be a whole number from 1 to 99,999,999.");
        await game.UnitOfWork.RunAsync(club.ClubId, "panel-grant-season-xp", async work =>
        {
            await work.LockClubAsync();
            var current = await work.ObjectivesAsync(game.Rules.Objectives);
            if (current.UserXp > long.MaxValue - amount.Value)
                throw new PanelError(409, "The Season XP total is too large for this grant.");
            await work.AddObjectivesXpAsync(game.Rules.Objectives, amount.Value);
        }, cancellationToken);
    }

    private async Task GrantPacksAsync(JsonElement body, ClubSummary club, CancellationToken cancellationToken)
    {
        var packId = WholeField(body, "packId") ?? throw new PanelError(400, "packId must identify a catalogue pack.");
        var count = WholeField(body, "count") ?? throw new PanelError(400, "count must be a whole number from 1 to 60.");
        if (count is < 1 or > 60) throw new PanelError(400, "count must be a whole number from 1 to 60.");
        if (game.Packs.GetPackConfig(packId).Count == 0) throw new PanelError(400, $"Pack {packId} is not in this server's pack catalogue.");
        var untradeable = FlagField(body, "untradeable") ?? game.Rules.Tradeability.Untradeable(RewardSources.Operator, packId);
        await game.UnitOfWork.RunAsync(club.ClubId, "panel-gift-packs", async work =>
        {
            await work.LockRelationalClubAsync();
            var held = await work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.unopened_packs WHERE club_id=@club");
            if (held + count > MaximumUnopenedPacks)
                throw new PanelError(409, $"MY PACKS holds {held}; a grant may leave at most {MaximumUnopenedPacks}.");
            // Persist the operator's choice on each unopened pack; opening inherits it.
            for (var i = 0; i < count; i++) await work.AddUnopenedPackAsync(packId, RewardSources.Operator, untradeable: untradeable);
        }, cancellationToken);
    }

    /// <summary>Grants one copy of a catalogue card through the live item factory, into new items (a panel grant is never a bulk grant).</summary>
    private async Task GrantCardAsync(JsonElement body, ClubSummary club, string? discordId, CancellationToken cancellationToken)
    {
        var key = TextField(body, "card") ?? throw new PanelError(400, "card must be a card key: resourceId:rating:rareflag.");
        var count = WholeField(body, "count") ?? 1;
        if (count != 1) throw new PanelError(400, "A player card is granted as one copy.");
        var resourceId = ResolveCardKey(key) ?? throw new PanelError(400, $"No card in this server's catalogue has the key {key}.");
        var requestedUntradeable = FlagField(body, "untradeable") ?? game.Rules.Tradeability.Untradeable(RewardSources.Operator);
        var untradeable = game.Cards.IsDeveloperCard(resourceId) || requestedUntradeable;
        // An exclusive card (a developer's Pro card, or any other grant-only card) exists once on the whole server.
        var exclusive = game.Cards.IsGrantOnly(resourceId);
        using var lease = await players.AcquireAsync(GrantTarget(club, discordId), cancellationToken);
        await lease.Session.Game.RunAsync("panel-grant-card", async work =>
        {
            if (exclusive)
            {
                // Grants of one exclusive card queue on this lock, so two clubs can never both pass the check below.
                await work.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('exclusive-card', @resource))", ("resource", resourceId));
                var holder = await work.ScalarAsync<long?>("""
                    SELECT c.persona_id FROM ultimateteam.items i JOIN ultimateteam.item_definitions d ON d.definition_id = i.definition_id
                    JOIN ultimateteam.clubs c ON c.id = i.club_id WHERE d.family_id = 1 AND d.native_resource_id = @resource LIMIT 1
                    """, ("resource", resourceId));
                if (holder is { } persona)
                {
                    var owner = game.Rules.Cards.AquaProCards.FirstOrDefault(card => card.CustomId == resourceId)?.Owner;
                    throw new PanelError(409, $"This card is exclusive{(owner is null ? "" : $" to {owner}")} and persona {persona} already has it; it exists once on the server.");
                }
            }
            var held = await work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.items WHERE club_id=@club AND pile='unassigned'");
            if (held + count > 100) throw new PanelError(409, $"New items holds {held}; a grant may leave at most 100.");
            for (var i = 0; i < count; i++)
            {
                var item = lease.Session.Fut.Items.BuildPlayer(resourceId, await work.NextItemIdAsync(), "unassigned");
                lease.Session.Fut.Items.MarkReward(item, RewardSources.Operator, untradeable);
                await work.SaveItemAsync(item);
            }
            return 0;
        }, cancellationToken);
    }

    /// <summary>Takes one item out of the club for nothing (ClubInventoryService.TakeAwayItemAsync says what it refuses).</summary>
    private async Task TakeItemAsync(JsonElement body, ClubSummary club, string? discordId, CancellationToken cancellationToken)
    {
        foreach (var property in body.EnumerateObject())
            if (property.Name is not ("op" or "itemId" or "confirmOnline"))
                throw new PanelError(400, $"Taking an item away does not take {property.Name}.");
        var itemId = WholeField(body, "itemId") is { } id and > 0 ? id : throw new PanelError(400, "itemId must identify one of the club's items.");
        var player = discordId is not null ? PlayerIdentity.Of(discordId)
            : club.PersonaId == players.Local.Player.PersonaId ? players.Local.Player
            : throw new PanelError(409, "That club belongs to no known player, so the server cannot open it to take an item.");
        using var lease = await players.AcquireAsync(player, cancellationToken);
        try { await lease.Session.Fut.Inventory.TakeAwayItemAsync(itemId, cancellationToken); }
        catch (ClubRuleException error) { throw new PanelError(409, error.Message); }
    }

    private async Task RecordEditAsync(long personaId, JsonObject patch, string requestedBy, string result, CancellationToken cancellationToken) =>
        await game.UnitOfWork.RunAsync(0, "panel-edit-record", work => work.ExecuteAsync("""
            INSERT INTO ultimateteam.panel_edits(persona_id,patch,requested_by,result,applied_at)
            VALUES(@persona,@patch::jsonb,@by,@result,now())
            """, ("persona", personaId), ("patch", patch.ToJsonString()), ("by", "panel " + requestedBy), ("result", result)), cancellationToken);

    // ------------------------------------------------------------ shared reads

    private async Task<List<ClubSummary>> ClubsAsync(long? personaId, CancellationToken cancellationToken) =>
        await game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync("""
            SELECT c.id,c.persona_id,c.display_name,c.credits,c.fifa_points,
                (SELECT count(*) FROM ultimateteam.items i WHERE i.club_id=c.id AND i.pile='club'),
                (SELECT count(*) FROM ultimateteam.items i WHERE i.club_id=c.id AND i.pile='unassigned'),
                (SELECT count(*) FROM ultimateteam.items i WHERE i.club_id=c.id AND i.pile='trade'),
                (SELECT count(*) FROM ultimateteam.unopened_packs p WHERE p.club_id=c.id),c.created_at
            FROM ultimateteam.clubs c WHERE (@persona::bigint IS NULL OR c.persona_id=@persona::bigint) ORDER BY c.id
            """, row => new ClubSummary(row.GetInt64(0), row.GetInt64(1), row.IsDBNull(2) ? null : row.GetString(2), row.GetInt64(3),
                row.GetInt64(4), row.GetInt64(5), row.GetInt64(6), row.GetInt64(7), row.GetInt64(8), row.GetDateTime(9)),
            ("persona", personaId)), cancellationToken);

    private async Task<IReadOnlyDictionary<string, SessionSummary>> SessionSummariesAsync(CancellationToken cancellationToken) =>
        (await game.UnitOfWork.RunReadAsync(0, work => work.QueryAsync("""
            SELECT discord_id,count(*),max(connected_at),(array_agg(remote ORDER BY connected_at DESC))[1]
            FROM ultimateteam.player_sessions GROUP BY discord_id
            """, row => (Id: row.GetString(0), Summary: new SessionSummary(row.GetInt64(1), row.GetDateTime(2), row.IsDBNull(3) ? null : row.GetString(3)))),
            cancellationToken)).ToDictionary(row => row.Id, row => row.Summary, StringComparer.Ordinal);

    /// <summary>The club name and abbreviation FIFA shows for this persona (a shared-server player's are derived from their name).</summary>
    private (string Name, string Abbr) ClubIdentity(long personaId, string? discordId, string? displayName)
    {
        var config = discordId is not null
            ? game.Config.ForPlayer(PlayerIdentity.Of(discordId), displayName, game.Rules.Club.ClubNames)
            : game.Config;
        return (config.ClubName, config.ClubAbbr);
    }

    private static string PlayerName(long personaId, string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? $"persona {personaId}" : displayName;

    /// <summary>Every pack, sold or not: the panel can grant any of them.</summary>
    private JsonObject GrantablePackCatalog() => game.Packs.Load().Obj("packs") ?? new JsonObject();

    private object[] GrantablePacks() => GrantablePackCatalog()
        .Select(pair => (object)new { id = long.Parse(pair.Key, CultureInfo.InvariantCulture), name = PackName(long.Parse(pair.Key, CultureInfo.InvariantCulture)) }).ToArray();

    private string PackName(long packId) => game.Packs.GetPackConfig(packId) is { Count: > 0 } config && config["name"]?.GetValue<string>() is { Length: > 0 } name
        ? name : $"pack {packId}";

    /// <summary>
    /// A card key <c>resourceId:rating:rareflag</c> (Aurora 21's resource id makes a card unique). A key written with an
    /// asset id in the first place, as Aurora 16's are, is accepted when rating and rareflag pick exactly one card.
    /// </summary>
    private long? ResolveCardKey(string key)
    {
        var parts = key.Trim().Split(':');
        if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rating) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var rareflag)) return null;
        if (game.Players.Find(id) is { } card && card.Rating == rating && card.Rareflag == rareflag) return id;
        var matches = game.Players.Entries.Where(entry => entry.Value.Int("assetId") == id && entry.Value.Rating == rating &&
                                                          entry.Value.Rareflag == rareflag).Select(entry => entry.Key).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
