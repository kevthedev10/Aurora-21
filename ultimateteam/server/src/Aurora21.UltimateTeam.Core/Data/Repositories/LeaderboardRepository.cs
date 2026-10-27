namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>A named club as the Leaderboards show it: identity, crest (its active badge's resource id, 0 for none) and Rivals values.</summary>
public sealed record LeaderboardClub(long ClubId, long PersonaId, long NucleusId, string Name, long Established, long BadgeResource,
    long Skill, long RivalsWins);

/// <summary>Reads for the Leaderboards categories (Match Earnings, Transfer Profit, Club Value, Top Squad, Division Rivals).</summary>
public static class LeaderboardRepository
{
    /// <summary>Every named club, with its active badge and its Rivals Skill Rating (0 until placed under the current rules) and wins.</summary>
    public static ValueTask<List<LeaderboardClub>> LeaderboardClubsAsync(this IUnitOfWork work, long ratingVersion) =>
        work.QueryAsync("""
            -- query: leaderboard.clubs
            SELECT c.id, c.persona_id, c.nucleus_id, c.display_name, c.established,
                   coalesce((SELECT d.native_resource_id FROM ultimateteam.items i
                             JOIN ultimateteam.item_definitions d ON d.definition_id = i.definition_id
                             WHERE i.club_id = c.id AND i.item_state = 'activeBadge' ORDER BY i.id LIMIT 1), 0),
                   CASE WHEN p.rating_version >= @version AND p.placed THEN coalesce(p.skill_rating, 0) ELSE 0 END,
                   CASE WHEN p.rating_version >= @version THEN coalesce(p.wins, 0) ELSE 0 END
            FROM ultimateteam.clubs c LEFT JOIN ultimateteam.rivals_progress p ON p.club_id = c.id
            WHERE c.display_name IS NOT NULL AND btrim(c.display_name) <> ''
            ORDER BY c.id
            """,
            reader => new LeaderboardClub(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt64(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)),
            ("version", ratingVersion));

    /// <summary>Each club's paid match coins and paid matches settled since <paramref name="from"/> (unix seconds).</summary>
    public static ValueTask<List<(long ClubId, long Coins, long Matches)>> LeaderboardMatchEarningsAsync(this IUnitOfWork work, long from) =>
        work.QueryAsync("""
            -- query: leaderboard.match-earnings
            SELECT club_id, sum(coins)::bigint, count(*) FROM ultimateteam.match_rewards
            WHERE verdict = 'Paid' AND settled_at >= @from GROUP BY club_id
            """, reader => (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)), ("from", from));

    /// <summary>Every completed transfer market sale since <paramref name="from"/>: seller (null for the market's own stock), buyer and price.</summary>
    public static ValueTask<List<(long? Seller, long Buyer, long Price)>> LeaderboardSalesAsync(this IUnitOfWork work, long from) =>
        work.QueryAsync("""
            -- query: leaderboard.sales
            SELECT seller_club_id, bidder_club_id, sold_price FROM ultimateteam.market_listings
            WHERE state = 'closed' AND sold_price > 0 AND bidder_club_id IS NOT NULL AND end_time >= @from
            """, reader => (reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)), ("from", from));

    /// <summary>How many copies of each player card every club owns.</summary>
    public static ValueTask<List<(long ClubId, long ResourceId, long Copies)>> LeaderboardPlayerCardsAsync(this IUnitOfWork work) =>
        work.QueryAsync("""
            -- query: leaderboard.player-cards
            SELECT i.club_id, d.native_resource_id, count(*) FROM ultimateteam.items i
            JOIN ultimateteam.player_items p ON p.item_id = i.id
            JOIN ultimateteam.item_definitions d ON d.definition_id = i.definition_id
            GROUP BY i.club_id, d.native_resource_id
            """, reader => (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));

    /// <summary>
    /// Each club's active squad starting XI ratings (clubs with all eleven starters owned). Squad Builder concept cards
    /// don't count: a club that saved an XI of concept 96-99s it doesn't own topped Top Squad (live 10-07).
    /// </summary>
    public static ValueTask<List<(long ClubId, int[] Ratings)>> LeaderboardActiveXiAsync(this IUnitOfWork work) =>
        work.QueryAsync("""
            -- query: leaderboard.active-xi
            SELECT s.club_id, array_agg(d.rating::int ORDER BY p.slot_index)
            FROM ultimateteam.squads s
            JOIN ultimateteam.squad_players p ON p.squad_id = s.id AND p.slot_index < 11 AND p.reference_kind = 'owned'
            JOIN ultimateteam.player_card_definitions d ON d.definition_id = p.definition_id
            WHERE s.active
            GROUP BY s.club_id, s.id
            HAVING count(*) = 11
            """, reader => (reader.GetInt64(0), reader.GetFieldValue<int[]>(1)));

    /// <summary>The persona ids of every club this club has played in a rewarded match: its Friends board.</summary>
    public static ValueTask<List<long>> LeaderboardOpponentsAsync(this IUnitOfWork work) =>
        work.QueryAsync("""
            -- query: leaderboard.opponents
            SELECT DISTINCT opponent_persona FROM ultimateteam.match_rewards WHERE club_id = @club AND opponent_persona IS NOT NULL
            """, reader => reader.GetInt64(0));
}
