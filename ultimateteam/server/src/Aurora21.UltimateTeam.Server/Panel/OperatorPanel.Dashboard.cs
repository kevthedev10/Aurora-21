using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Server.Access;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Dashboard (Aurora16 PANEL-CONTRACTS 6.7): services, who is connected, totals, recent logins, clubs and the last 14
/// days, plus the access state the status strip shows. Aurora 21 runs every game service in one process, so FUT and
/// Blaze are up whenever this answers; the TLS redirector is its own container and is probed.
/// </summary>
public sealed partial class OperatorPanel
{
    private async Task<object> DashboardAsync(ApiRequest request)
    {
        var cancellationToken = request.Aborted;
        var services = new List<object>();
        foreach (var service in options.Services)
        {
            bool up;
            try { up = await service.Up(cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException) { up = false; }
            services.Add(new { key = service.Key, label = service.Label, port = service.Port, up });
        }
        var access = issuer.Access;
        var online = issuer.Presence?.Online() ?? [];
        var byPersona = access.DiscordByPersona();
        var clubs = await ClubsAsync(null, cancellationToken);
        var clubByPersona = clubs.ToDictionary(club => club.PersonaId);
        var (totals, recent, days) = await game.UnitOfWork.RunReadAsync(0, async work =>
        {
            var summary = (await work.QueryAsync("""
                SELECT (SELECT count(DISTINCT discord_id) FROM ultimateteam.player_sessions WHERE connected_at >= date_trunc('day', now())),
                       (SELECT count(*) FROM ultimateteam.player_sessions WHERE connected_at >= date_trunc('day', now())),
                       (SELECT count(DISTINCT discord_id) FROM ultimateteam.player_sessions WHERE connected_at >= now() - interval '7 days'),
                       (SELECT COALESCE(sum(credits),0) FROM ultimateteam.clubs), (SELECT COALESCE(sum(fifa_points),0) FROM ultimateteam.clubs),
                       (SELECT count(*) FROM ultimateteam.items WHERE pile='club')
                """, row => (PlayersToday: row.GetInt64(0), LoginsToday: row.GetInt64(1), PlayersWeek: row.GetInt64(2),
                    Coins: Convert.ToInt64(row.GetValue(3)), Points: Convert.ToInt64(row.GetValue(4)), Items: row.GetInt64(5)))).Single();
            var logins = await work.QueryAsync("""
                SELECT discord_id,remote,connected_at,disconnected_at FROM ultimateteam.player_sessions ORDER BY connected_at DESC LIMIT 20
                """, row => (Discord: row.GetString(0), Remote: row.IsDBNull(1) ? null : row.GetString(1), At: row.GetDateTime(2),
                    Ended: row.IsDBNull(3) ? (DateTime?)null : row.GetDateTime(3)));
            var history = await work.QueryAsync("""
                SELECT to_char(date_trunc('day', connected_at), 'YYYY-MM-DD'),count(DISTINCT discord_id),count(*)
                FROM ultimateteam.player_sessions WHERE connected_at >= date_trunc('day', now()) - interval '13 days'
                GROUP BY 1 ORDER BY 1 DESC
                """, row => (object)new { day = row.GetString(0), players = row.GetInt64(1), logins = row.GetInt64(2) });
            return (summary, logins, history);
        }, cancellationToken);
        string? Club(string discordId)
        {
            var persona = PlayerIdentity.Of(discordId).PersonaId;
            return clubByPersona.TryGetValue(persona, out var club) ? ClubIdentity(persona, discordId, club.DisplayName).Name : null;
        }
        string Name(string discordId) => PlayerName(PlayerIdentity.Of(discordId).PersonaId,
            clubByPersona.GetValueOrDefault(PlayerIdentity.Of(discordId).PersonaId)?.DisplayName ?? access.Find(discordId)?.DisplayName);
        var settings = access.Settings;
        return new
        {
            services = new
            {
                host = options.AdvertisedHost,
                list = services,
                blaze = true,
                fut = true,
                futAlive = true,
                supervised = false,
                database = "PostgreSQL (ultimateteam)",
                blazeAccess = new
                {
                    maintenance = settings.Maintenance,
                    bypassCount = access.Players.Count(entry => entry.MaintenanceBypass),
                    maxPlayers = settings.MaxPlayers,
                    blockedCount = access.Players.Count(entry => entry.Blocked),
                    playRoles = settings.PlayRoles.Count,
                    controls = new { known = true, loaded = true },
                },
            },
            online = new
            {
                count = online.Count,
                players = online.OrderBy(player => player.Since).Select(player => new
                {
                    id = PlayerIdentity.Of(player.PlayerId).PersonaId,
                    discordId = player.PlayerId,
                    name = Name(player.PlayerId),
                    club = Club(player.PlayerId),
                    remote = player.Remote,
                    since = Iso(player.Since),
                }),
            },
            totals = new
            {
                players = Math.Max(clubs.Count, access.Players.Count),
                clubs = clubs.Count,
                players_today = totals.PlayersToday,
                logins_today = totals.LoginsToday,
                players_week = totals.PlayersWeek,
                coins = totals.Coins,
                points = totals.Points,
                items = totals.Items,
            },
            recent = recent.Select(row => new
            {
                id = PlayerIdentity.Of(row.Discord).PersonaId,
                name = Name(row.Discord),
                club = Club(row.Discord),
                remote = row.Remote,
                at = Iso(row.At),
                ended = Iso(row.Ended),
            }),
            clubs = clubs.Select(club =>
            {
                var discordId = byPersona.GetValueOrDefault(club.PersonaId);
                var (name, abbr) = ClubIdentity(club.PersonaId, discordId, club.DisplayName);
                return new
                {
                    persona_id = club.PersonaId, club_name = name, club_abbr = abbr, coins = club.Coins, points = club.Points,
                    items = club.ClubItems, new_items = club.NewItems, squads = (long?)null, updated_at = Iso(club.CreatedAt),
                };
            }),
            days,
            launch = new
            {
                identity = issuer.Configured,
                playRoles = settings.PlayRoles.Count,
                discordBot = issuer.Roles.TokenSet,
            },
            startedAt = Iso(Started),
            supervisor = Array.Empty<string>(),
            auth = new { mode = "loopback" },
        };
    }
}
