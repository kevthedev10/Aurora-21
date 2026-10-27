using System.Net;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Anti-cheat (owner, 2026-10-08): Leavers lists who leaves online matches and how early (match_leaves, migration 179;
/// the serial-leaver rule is rewardEligibility.opponentLeft.serialLeaver), and Market log lists completed transfer market
/// sales (market_trades) with the signs of a coin transfer between players flagged.
/// </summary>
public sealed partial class OperatorPanel
{
    /// <summary>A sale at least this many times the card's median price (or this fraction of it) is flagged.</summary>
    private const double MarketPriceFactor = 5;

    /// <summary>A flagged price gap only matters for real coins.</summary>
    private const long MarketFlagMinimumCoins = 10_000;

    /// <summary>A Buy Now this soon after listing, from another player's club, is flagged as arranged.</summary>
    private const long MarketInstantBuySeconds = 15;

    /// <summary>Sales from one seller to one buyer within the period before they are flagged as a pair.</summary>
    private const int MarketRepeatPairTrades = 3;

    private async Task<(int, object)?> LeaversAsync(ApiRequest request)
    {
        // DELETE leavers/{discordId}/penalty: lift a leaver's cooldown or ban (an appeal upheld on Discord).
        if (request.Parts is [_, var penalised, "penalty"])
        {
            if (request.Method != "DELETE") throw new PanelError(405, "method not allowed");
            var lifted = await game.UnitOfWork.RunAsync(0, "panel-lift-leaver-penalty", work => work.LiftPenaltiesAsync(penalised));
            game.LeaverCooldown.Clear(penalised);
            _log.LogWarning("PANEL LEAVER PENALTY lifted player={Player} rows={Lifted}", penalised, lifted);
            return (200, new { ok = true, lifted });
        }
        if (request.Method != "GET") throw new PanelError(405, "method not allowed");
        if (request.Parts.Length != 1) return null;
        var hours = QueryInt(request, "hours", 168, 1, 24 * 90);
        var limit = QueryInt(request, "limit", 200, 1, 1000);
        var rules = game.Rules.Seasons.RewardEligibility.OpponentLeft;
        var now = game.NowSeconds;
        var since = now - hours * 3600L;
        var windowSince = now - (long)(rules.SerialLeaver.WindowHours * 3600);
        var (leavers, recent) = await game.UnitOfWork.RunReadAsync(0, async work =>
        {
            var leavers = await work.QueryAsync("""
                SELECT leaver_player, max(leaver_persona), count(*), count(*) FILTER (WHERE early),
                    count(*) FILTER (WHERE early AND left_at > @window), count(*) FILTER (WHERE serial AND stayer_outcome = 'Win'), max(left_at),
                    count(DISTINCT stayer_player) FILTER (WHERE early), array_agg(DISTINCT mode)
                FROM ultimateteam.match_leaves WHERE left_at > @since
                GROUP BY leaver_player ORDER BY count(*) FILTER (WHERE early) DESC, count(*) DESC LIMIT 500
                """, row => (Player: row.GetString(0), Persona: row.IsDBNull(1) ? (long?)null : row.GetInt64(1), Leaves: row.GetInt64(2),
                    Early: row.GetInt64(3), EarlyInWindow: row.GetInt64(4), Serial: row.GetInt64(5), Last: row.GetInt64(6),
                    Opponents: row.GetInt64(7), Modes: row.GetFieldValue<string[]>(8)),
                ("since", since), ("window", windowSince));
            var recent = await work.QueryAsync("""
                SELECT leaver_player, stayer_player, mode, match_key, minute, stayer_goals, leaver_goals, early, serial, stayer_outcome, left_at
                FROM ultimateteam.match_leaves WHERE left_at > @since ORDER BY left_at DESC LIMIT @limit
                """, row => (Leaver: row.GetString(0), Stayer: row.GetString(1), Mode: row.GetString(2), Match: row.GetString(3),
                    Minute: row.GetInt32(4), StayerGoals: row.GetInt32(5), LeaverGoals: row.GetInt32(6), Early: row.GetBoolean(7),
                    Serial: row.GetBoolean(8), Outcome: row.GetString(9), At: row.GetInt64(10)),
                ("since", since), ("limit", limit));
            return (leavers, recent);
        }, request.Aborted);
        var cooldowns = game.LeaverCooldown.Active(now);
        var penaltyOf = cooldowns.ToDictionary(row => row.Player, row => row.Penalty, StringComparer.Ordinal);
        return (200, new
        {
            rules = new { winFromMinute = rules.WinFromMinute, earlyLeaves = rules.SerialLeaver.EarlyLeaves, windowHours = rules.SerialLeaver.WindowHours,
                fromMinute = rules.SerialLeaver.FromMinute },
            queueCooldown = new { earlyLeaves = rules.QueueCooldown.EarlyLeaves, windowSeconds = rules.QueueCooldown.WindowSeconds,
                ladderSeconds = rules.QueueCooldown.LadderSeconds, offenceMemoryDays = rules.QueueCooldown.OffenceMemoryDays },
            penalties = cooldowns.Select(row => new { discordId = row.Player, name = LeaverName(row.Player), offence = row.Penalty.Offence,
                banned = row.Penalty.Until is null, until = row.Penalty.Until is { } ends ? IsoSeconds(ends) : null }),
            hours,
            leavers = leavers.Select(row => new
            {
                discordId = row.Player, name = LeaverName(row.Player), personaId = row.Persona ?? PersonaOf(row.Player),
                leaves = row.Leaves, earlyLeaves = row.Early, earlyInWindow = row.EarlyInWindow, serialLeaves = row.Serial,
                serialNow = rules.SerialLeaver.EarlyLeaves > 0 && row.EarlyInWindow >= rules.SerialLeaver.EarlyLeaves,
                earlyOpponents = row.Opponents, modes = row.Modes, lastLeftAt = IsoSeconds(row.Last),
                penalty = penaltyOf.TryGetValue(row.Player, out var penalty) ? (penalty.Until is null ? "banned" : "cooldown") : null,
            }),
            recent = recent.Select(row => new
            {
                leaver = new { discordId = row.Leaver, name = LeaverName(row.Leaver) },
                stayer = new { discordId = row.Stayer, name = LeaverName(row.Stayer) },
                mode = row.Mode, match = row.Match, minute = row.Minute, score = $"{row.LeaverGoals}-{row.StayerGoals}",
                early = row.Early, serial = row.Serial, stayerOutcome = row.Outcome, at = IsoSeconds(row.At),
            }),
        });
    }

    private async Task<(int, object)?> MarketLogAsync(ApiRequest request)
    {
        if (request.Method != "GET") throw new PanelError(405, "method not allowed");
        if (request.Parts.Length != 1) return null;
        var hours = QueryInt(request, "hours", 24, 1, 24 * 90);
        var limit = QueryInt(request, "limit", 300, 1, 2000);
        var flaggedOnly = request.Query("flagged") is "1" or "true";
        var clubText = request.Query("club").Trim();
        long? club = clubText.Length == 0 ? null
            : long.TryParse(clubText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id
            : throw new PanelError(400, "club must be a club id.");
        var since = game.NowSeconds - hours * 3600L;
        var (trades, clubs, addresses) = await game.UnitOfWork.RunReadAsync(0, async work =>
        {
            // Each sale with the card's median price over the week before it (other sales of the same card), and how many
            // sales went from this seller to this buyer in the period.
            var trades = await work.QueryAsync("""
                SELECT t.trade_id, t.sold_at, t.sale, t.seller_club_id, t.seller_name, t.buyer_club_id, t.resource_id, t.rating, t.rareflag,
                    t.price, t.seller_proceeds, t.starting_bid, t.buy_now, t.listed_at, ref.median, ref.sales,
                    count(*) OVER (PARTITION BY t.seller_club_id, t.buyer_club_id)
                FROM ultimateteam.market_trades t
                LEFT JOIN LATERAL (
                    SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY o.price) AS median, count(*) AS sales
                    FROM ultimateteam.market_trades o
                    WHERE o.resource_id = t.resource_id AND o.id <> t.id AND o.sold_at > t.sold_at - 604800 AND o.sold_at <= t.sold_at + 604800
                ) ref ON true
                WHERE t.sold_at > @since AND (@side::bigint IS NULL OR t.seller_club_id = @side::bigint OR t.buyer_club_id = @side::bigint)
                ORDER BY t.sold_at DESC
                """, row => new MarketTradeRow(row.GetInt64(0), row.GetInt64(1), row.GetString(2), row.IsDBNull(3) ? null : row.GetInt64(3),
                    row.GetString(4), row.GetInt64(5), row.GetInt64(6), row.GetInt64(7), row.IsDBNull(8) ? 0 : row.GetInt64(8), row.GetInt64(9),
                    row.GetInt64(10), row.GetInt64(11), row.GetInt64(12), row.GetInt64(13), row.IsDBNull(14) ? null : row.GetDouble(14),
                    row.IsDBNull(15) ? 0 : row.GetInt64(15), row.GetInt64(16)),
                ("since", since), ("side", club));
            var clubs = (await work.QueryAsync("SELECT id, persona_id, display_name FROM ultimateteam.clubs",
                    row => (Id: row.GetInt64(0), Persona: row.GetInt64(1), Name: row.IsDBNull(2) ? null : row.GetString(2))))
                .ToDictionary(row => row.Id);
            var addresses = await work.QueryAsync("""
                SELECT DISTINCT discord_id, remote FROM ultimateteam.player_sessions WHERE remote IS NOT NULL AND connected_at > now() - interval '30 days'
                """, row => (Id: row.GetString(0), Remote: row.GetString(1)));
            return (trades, clubs, addresses);
        }, request.Aborted);
        var byPersona = issuer.Access.DiscordByPersona();
        var ips = addresses.GroupBy(row => row.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => AddressOf(row.Remote)).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        string? DiscordOf(long? clubId) => clubId is { } key && clubs.TryGetValue(key, out var row) ? byPersona.GetValueOrDefault(row.Persona) : null;
        object? Club(long? clubId, string fallback) => clubId is not { } key ? null : new
        {
            id = key, discordId = DiscordOf(key), personaId = clubs.TryGetValue(key, out var owner) ? owner.Persona : (long?)null,
            name = clubs.TryGetValue(key, out var row) ? PlayerName(row.Persona, row.Name ?? issuer.Access.Find(DiscordOf(key) ?? "")?.DisplayName) : fallback,
        };
        var snapshot = game.Live.Current;
        var rows = trades.Select(trade =>
        {
            var flags = new List<string>();
            var player = trade.SellerClubId is not null;
            if (player && trade.Median is { } median && trade.ReferenceSales >= 3)
            {
                if (trade.Price >= MarketFlagMinimumCoins && trade.Price >= median * MarketPriceFactor) flags.Add("overpriced");
                if (median >= MarketFlagMinimumCoins && trade.Price * MarketPriceFactor <= median) flags.Add("underpriced");
            }
            if (player && trade.Sale == "buyNow" && trade.SoldAt - trade.ListedAt <= MarketInstantBuySeconds) flags.Add("instantBuy");
            if (player && trade.PairTrades >= MarketRepeatPairTrades) flags.Add("repeatPair");
            var seller = DiscordOf(trade.SellerClubId);
            var buyer = DiscordOf(trade.BuyerClubId);
            if (seller is not null && buyer is not null && ips.TryGetValue(seller, out var a) && ips.TryGetValue(buyer, out var b) && a.Overlaps(b))
                flags.Add("sameIp");
            var card = snapshot.Players.Find(trade.ResourceId);
            return new
            {
                tradeId = trade.TradeId, soldAt = IsoSeconds(trade.SoldAt), sale = trade.Sale, listedForSeconds = trade.SoldAt - trade.ListedAt,
                seller = Club(trade.SellerClubId, trade.SellerName) ?? new { id = 0L, discordId = (string?)null, personaId = (long?)null, name = trade.SellerName + " (AI)" },
                buyer = Club(trade.BuyerClubId, $"club {trade.BuyerClubId}"),
                card = new { resourceId = trade.ResourceId, name = card?.Text("name") is { Length: > 0 } name ? name : $"item {trade.ResourceId}",
                    rating = trade.Rating, rareflag = trade.Rareflag },
                price = trade.Price, sellerProceeds = trade.SellerProceeds, startingBid = trade.StartingBid, buyNow = trade.BuyNow,
                medianPrice = trade.Median is { } m ? (long?)Math.Round(m) : null, referenceSales = trade.ReferenceSales, pairTrades = trade.PairTrades,
                flags,
            };
        }).ToList();
        var shown = (flaggedOnly ? rows.Where(row => row.flags.Count > 0) : rows).Take(limit).ToList();
        return (200, new
        {
            hours,
            totals = new { trades = rows.Count, flagged = rows.Count(row => row.flags.Count > 0), coins = rows.Sum(row => row.price) },
            trades = shown,
        });
    }

    private sealed record MarketTradeRow(long TradeId, long SoldAt, string Sale, long? SellerClubId, string SellerName, long BuyerClubId,
        long ResourceId, long Rating, long Rareflag, long Price, long SellerProceeds, long StartingBid, long BuyNow, long ListedAt,
        double? Median, long ReferenceSales, long PairTrades);

    private string LeaverName(string playerId) =>
        issuer.Access.Find(playerId)?.DisplayName is { Length: > 0 } name ? name : $"persona {PersonaOf(playerId)}";

    private static long PersonaOf(string playerId) => PlayerIdentity.Of(playerId).PersonaId;

    private static string IsoSeconds(long unixSeconds) => Iso(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))!;

    /// <summary>The address of a session's remote endpoint, without its port.</summary>
    private static string AddressOf(string remote) =>
        IPEndPoint.TryParse(remote, out var endpoint) ? endpoint.Address.ToString() : remote;
}
