using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>
/// Leaderboards: the Leaderboards screen options, and the server's clubs ranked by Division Rivals
/// skill rating for the Leaderboards, Squad Battles and FUT Champions boards and Blaze Stats.
/// </summary>
public sealed class LeaderboardService
{
    private readonly FutServices _fut;
    private readonly object _cacheGate = new();
    private IReadOnlyList<ClubRanking>? _cached;
    private long _cachedAt;

    public LeaderboardService(FutServices fut) => _fut = fut;

    private GameContext Game => _fut.Game;
    private LeaderboardRules Rules => Game.Rules.Seasons.Leaderboards;

    /// <summary>
    /// The options document CardsDLL parses: a selection, one item list per group (exactly one
    /// default each) and every permutation as an array. A missing default or permutations left the
    /// screen spinning, and an object in place of the array froze the game.
    /// </summary>
    public JsonObject Options()
    {
        var groups = Rules.Groups;
        var selection = new JsonObject();
        var options = new JsonArray();
        foreach (var group in groups)
        {
            selection[group.Id] = group.Default;
            options.Add(new JsonObject
            {
                ["id"] = group.Id,
                [group.Id] = new JsonArray(group.Options.Select(option => (JsonNode?)new JsonObject
                {
                    ["id"] = option.Id,
                    ["display"] = option.Display,
                    ["description"] = option.Description,
                    ["image"] = option.Image,
                    ["default"] = option.Id == group.Default,
                }).ToArray()),
            });
        }
        var permutations = new JsonArray();
        IEnumerable<Dictionary<string, string>> combinations = new[] { new Dictionary<string, string>() };
        foreach (var group in groups)
        {
            combinations = combinations.SelectMany(combination => group.Options.Select(option =>
                new Dictionary<string, string>(combination) { [group.Id] = option.Id })).ToList();
        }
        foreach (var combination in combinations)
        {
            var permutation = new JsonObject();
            var url = Rules.UrlTemplate;
            foreach (var group in groups)
            {
                permutation[group.Id] = combination[group.Id];
                url = url.Replace($"{{{group.Id}}}", combination[group.Id], StringComparison.Ordinal);
            }
            permutation["url"] = url;
            permutations.Add(permutation);
        }
        return TemplateLibrary.Core.Object("seasons/leaderboard-options", new TemplateArgs
        {
            ["selection"] = selection,
            ["options"] = options,
            ["permutations"] = permutations,
        });
    }

    /// <summary>
    /// One board: the Leaderboards categories as FIFA 21 ships them (Match Earnings, Transfer Profit, Club Value, Top Squad)
    /// and Division Rivals, over the Top 100 or Friends (the clubs this club has played, and itself) and a period. The header
    /// names the column labels (string keys FIFA localises) and the info panel's rows, which FutLeaderboards.swf draws from
    /// the category options by id (publishTitles(value, arrCategoryItems), up to five; any other id draws an empty row).
    /// Each row's insetUrl is the details request FIFA makes for the highlighted club (appended to /leaderboards).
    /// </summary>
    public async ValueTask<JsonObject> EntriesAsync(IUnitOfWork uow, string? category = null, string? view = null, string? period = null)
    {
        var board = Category(category);
        var span = Period(period);
        var scores = await ScoresAsync(uow, span);
        var standings = Standings(scores, board);
        if (string.Equals(view, "FRIENDS", StringComparison.OrdinalIgnoreCase))
        {
            var friends = (await uow.LeaderboardOpponentsAsync()).ToHashSet();
            friends.Add(Game.Config.PersonaId);
            standings = standings.Where(standing => friends.Contains(standing.Club.PersonaId)).ToList();
        }
        var row = Rules.Row;
        var categories = Categories;
        return new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["rank"] = "LOC_LB_RANK",
                ["persona"] = "LOC_LB_PERSONA_PC",
                ["badge"] = "LOC_LB_BADGE",
                ["score"] = categories.FirstOrDefault(option => option.Id == board)?.ScoreLabel ?? "FUT_RIVALS_RATING",
                ["inset"] = new JsonArray(categories.Take(MaxInsets).Select(option => (JsonNode?)new JsonObject { ["id"] = option.Id }).ToArray()),
            },
            ["entries"] = new JsonArray(standings.Take(Rules.MaxRows).Select((standing, index) =>
            {
                var crest = Crest(standing.Club.BadgeResource);
                var entry = TemplateLibrary.Core.Object("seasons/leaderboard-row", new TemplateArgs
                {
                    ["rank"] = index + 1,
                    ["name"] = standing.Club.Name,
                    ["badge"] = crest,
                    ["est"] = EstablishedYear(standing.Club.Established),
                    ["sku"] = row.Sku,
                    ["scoreIcon"] = board,
                    ["score"] = standing.Score,
                    ["tiebreakIcon"] = row.TiebreakIcon,
                    ["wins"] = standing.Tiebreak,
                });
                // The row's crest: the entry parser (0x180306a20) stores "badge" but never shows it; the list's BADGE_ID
                // comes from "teamId" (0x180147e14), so without it every other club wears the grey default shield.
                entry["teamId"] = crest;
                entry["insetUrl"] = $"/period/{span.Id}/user/{standing.Club.PersonaId}";
                return (JsonNode?)entry;
            }).ToArray()),
        };
    }

    /// <summary>
    /// GET leaderboards/period/{period}/user/{id} (CardsDLL parser 0x180309aa0): the club's score in each category for the
    /// period, and its clubInfo (credits, trophies, won, draw, loss). FIFA asks with a board row's persona id, or with its
    /// own user id (the nucleus id) for the viewer's own row; an unknown id is the caller's club. Credits stay 0: another
    /// club's balance is not shown to other players.
    /// </summary>
    public async ValueTask<JsonObject> DetailsAsync(IUnitOfWork uow, long id, string? period = null)
    {
        var scores = await ScoresAsync(uow, Period(period));
        var club = scores.Clubs.FirstOrDefault(entry => entry.PersonaId == id)
            ?? scores.Clubs.FirstOrDefault(entry => entry.NucleusId == id)
            ?? scores.Clubs.FirstOrDefault(entry => entry.PersonaId == Game.Config.PersonaId);
        var personaId = club?.PersonaId ?? Game.Config.PersonaId;
        var info = await uow.LeaderboardClubInfoAsync(personaId);
        return new JsonObject
        {
            ["category"] = new JsonArray(Categories.Take(MaxInsets).Select(option => (JsonNode?)new JsonObject
            {
                ["id"] = option.Id,
                ["score"] = new JsonObject { ["value"] = club is null ? 0 : scores.Score(option.Id, club).Score },
            }).ToArray()),
            ["clubInfo"] = new JsonObject
            {
                ["credits"] = 0,
                ["trophies"] = info?.Titles ?? 0,
                ["won"] = info?.Won ?? 0,
                ["draw"] = info?.Draw ?? 0,
                ["loss"] = info?.Loss ?? 0,
            },
        };
    }

    /// <summary>The info panel holds at most five rows (header "inset", CardsDLL 0x180306e70).</summary>
    private const int MaxInsets = 5;

    private IReadOnlyList<LeaderboardOption> Categories =>
        Rules.Groups.FirstOrDefault(group => group.Id == "category")?.Options ?? [];

    /// <summary>The board's category: a known option id (any case), else the group's default.</summary>
    private string Category(string? id)
    {
        var group = Rules.Groups.FirstOrDefault(entry => entry.Id == "category");
        return group?.Options.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase))?.Id
            ?? group?.Default ?? "rivals";
    }

    /// <summary>A period and the unix second it starts: the week from Monday 00:00 UTC, the calendar month (UTC), or all time.</summary>
    private LeaderboardPeriod Period(string? id)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(Game.NowSeconds);
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        return id?.ToLowerInvariant() switch
        {
            "monthly" => new LeaderboardPeriod("Monthly", new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()),
            "alltime" => new LeaderboardPeriod("AllTime", 0),
            _ => new LeaderboardPeriod("Weekly", today.AddDays(-(((int)today.DayOfWeek + 6) % 7)).ToUnixTimeSeconds()),
        };
    }

    /// <summary>
    /// A category's standings: every club with a score, best first (ties by tiebreak, then name). Match Earnings and Transfer
    /// Profit list only clubs active in the period; the others list every named club.
    /// </summary>
    private static List<LeaderboardStanding> Standings(LeaderboardScores scores, string category) =>
        scores.Clubs
            .Select(club => (Club: club, Value: scores.Score(category, club)))
            .Where(entry => category is not ("competitor" or "trader") || entry.Value.Active)
            .Select(entry => new LeaderboardStanding(entry.Club, entry.Value.Score, entry.Value.Tiebreak))
            .OrderByDescending(standing => standing.Score)
            .ThenByDescending(standing => standing.Tiebreak)
            .ThenBy(standing => standing.Club.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();

    /// <summary>Every club's values for a period. Cached briefly per period: it reads every club's matches, sales and cards.</summary>
    private async ValueTask<LeaderboardScores> ScoresAsync(IUnitOfWork uow, LeaderboardPeriod period)
    {
        var now = Game.NowSeconds;
        lock (_cacheGate)
        {
            if (_scores.TryGetValue(period.Id, out var cached) && cached.Start == period.Start && now - cached.At < Rules.CacheSeconds)
            {
                return cached;
            }
        }
        var clubs = (await uow.LeaderboardClubsAsync(Game.Rules.Seasons.Rivals.RatingVersion))
            .Select(club => club with { Name = Trim(club.Name) })
            .ToList();
        if (clubs.Count == 0)
        {
            clubs.Add(new LeaderboardClub(0, Game.Config.PersonaId, 0, Trim(Game.Config.DisplayName), Game.Established, 0, 0, 0));
        }
        var earnings = (await uow.LeaderboardMatchEarningsAsync(period.Start)).ToDictionary(row => row.ClubId, row => (row.Coins, row.Matches));
        var profit = new Dictionary<long, (long Profit, long Trades)>();
        var market = Game.Rules.Market;
        void Trade(long club, long amount)
        {
            var (sum, trades) = profit.GetValueOrDefault(club);
            profit[club] = (sum + amount, trades + 1);
        }
        foreach (var (seller, buyer, price) in await uow.LeaderboardSalesAsync(period.Start))
        {
            if (seller is { } sold)
            {
                Trade(sold, market.SellerProceeds(price));
            }
            Trade(buyer, -price);
        }
        var values = new Dictionary<long, (long Total, long Best)>();
        foreach (var (club, resource, copies) in await uow.LeaderboardPlayerCardsAsync())
        {
            var value = Game.Cards.DiscardValue(resource);
            var (total, best) = values.GetValueOrDefault(club);
            values[club] = (total + value * copies, Math.Max(best, value));
        }
        var squads = new Dictionary<long, long>();
        foreach (var (club, ratings) in await uow.LeaderboardActiveXiAsync())
        {
            squads[club] = SbcSquadRating.CalculateSbcSquadRating(ratings);
        }
        var scores = new LeaderboardScores(period.Start, now, clubs, earnings, profit, values, squads);
        lock (_cacheGate)
        {
            _scores[period.Id] = scores;
        }
        return scores;
    }

    /// <summary>The crest FIFA draws for a club: its active badge's crest (the badge definition is 6000000 + crest), else the default.</summary>
    private long Crest(long badgeResource)
    {
        var crest = badgeResource - Game.Rules.SquadBattles.Sets.BadgeResourceBase;
        return crest is > 0 and < 1_000_000 ? crest : Rules.Row.Badge;
    }

    private long EstablishedYear(long established) =>
        established <= 0 ? Rules.Row.Est
        : DateTimeOffset.FromUnixTimeSeconds(established > 100_000_000_000 ? established / 1000 : established).Year;

    private readonly Dictionary<string, LeaderboardScores> _scores = new(StringComparer.Ordinal);

    /// <summary>
    /// Every named club on the server ranked by Rivals skill, wins and matches, then name, for Blaze Stats and matchmaking;
    /// this club alone when no club has a name yet. Cached briefly: it reads every club's progress.
    /// </summary>
    public async ValueTask<IReadOnlyList<ClubRanking>> RankingsAsync(IUnitOfWork uow, int? limit = null) =>
        (await AllRankingsAsync(uow)).Take(Math.Min(limit ?? Rules.MaxRows, Rules.MaxRows)).ToList();

    /// <summary>Every ranked club, past the board's maxRows (a details request can name any club).</summary>
    private async ValueTask<IReadOnlyList<ClubRanking>> AllRankingsAsync(IUnitOfWork uow)
    {
        var now = Game.NowSeconds;
        if (Cached(now) is not { } ranked)
        {
            // The ranked Skill Rating is 0 until a club has placed under the current rules (Python server 56145ed).
            var defaultRegion = Game.Rules.Seasons.WeekendLeague.DefaultRegion;
            var rows = (await uow.RivalsBoardAsync(Game.Rules.Seasons.Rivals.RatingVersion, defaultRegion))
                .Select(club => (club.PersonaId, Name: Trim(club.Name), Skill: club.Rating, club.Wins, club.Matches, club.Losses, club.NucleusId, club.Region))
                .ToList();
            if (rows.Count == 0)
            {
                rows.Add((Game.Config.PersonaId, Trim(Game.Config.DisplayName), 0, 0, 0, 0, 0, defaultRegion));
            }
            ranked = rows
                .OrderByDescending(row => row.Skill)
                .ThenByDescending(row => row.Wins)
                .ThenByDescending(row => row.Matches)
                .ThenBy(row => row.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .Select((row, index) => new ClubRanking(index + 1, row.PersonaId, row.Name, row.Skill, row.Wins, row.Matches, row.Losses, row.NucleusId, row.Region))
                .ToList();
            lock (_cacheGate)
            {
                _cached = ranked;
                _cachedAt = now;
            }
        }
        return ranked;
    }

    /// <summary>The cached ranking while it is fresh. Two concurrent misses both read; the cache only saves repeated work.</summary>
    private IReadOnlyList<ClubRanking>? Cached(long now)
    {
        lock (_cacheGate)
        {
            return _cached is not null && now - _cachedAt < Rules.CacheSeconds ? _cached : null;
        }
    }

    private string Trim(string? name)
    {
        // Plain ASCII only (Python server 81c770e): any other character hung the Leaderboards screen.
        var trimmed = PlayerNames.Readable(name) ?? Rules.DefaultName;
        return trimmed[..Math.Min(trimmed.Length, Rules.MaxNameLength)];
    }
}

/// <summary>A Leaderboards period: its option id and the unix second it starts.</summary>
public sealed record LeaderboardPeriod(string Id, long Start);

/// <summary>A club's place on one category board.</summary>
public sealed record LeaderboardStanding(LeaderboardClub Club, long Score, long Tiebreak);

/// <summary>Every club's Leaderboards values for one period, and how each category scores a club.</summary>
public sealed record LeaderboardScores(long Start, long At, IReadOnlyList<LeaderboardClub> Clubs,
    IReadOnlyDictionary<long, (long Coins, long Matches)> Earnings, IReadOnlyDictionary<long, (long Profit, long Trades)> Profit,
    IReadOnlyDictionary<long, (long Total, long Best)> Values, IReadOnlyDictionary<long, long> Squads)
{
    /// <summary>
    /// A club's score and tiebreak in a category, and whether it was active in the period: Match Earnings (coins, matches),
    /// Transfer Profit (proceeds less purchases, club value), Club Value (players' quick-sell value, best player), Top Squad
    /// (active XI squad rating, club value) and Division Rivals (Skill Rating, wins).
    /// </summary>
    public (long Score, long Tiebreak, bool Active) Score(string category, LeaderboardClub club)
    {
        var value = Values.GetValueOrDefault(club.ClubId);
        return category switch
        {
            "competitor" => Earnings.TryGetValue(club.ClubId, out var earned) ? (earned.Coins, earned.Matches, true) : (0, 0, false),
            "trader" => Profit.TryGetValue(club.ClubId, out var traded) ? (traded.Profit, value.Total, true) : (0, value.Total, false),
            "collector" => (value.Total, value.Best, true),
            "builder" => (Squads.GetValueOrDefault(club.ClubId), value.Total, true),
            _ => (club.Skill, club.RivalsWins, true),
        };
    }
}

/// <summary>One club's place on the server leaderboards.</summary>
public sealed record ClubRanking(long Rank, long PersonaId, string Name, long Skill, long Wins, long Matches, long Losses = 0,
    long NucleusId = 0, string Region = "");
