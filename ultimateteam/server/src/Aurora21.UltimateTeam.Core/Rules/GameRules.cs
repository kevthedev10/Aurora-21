namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>
/// The tunable game rules: pack tables, mode settings, rewards and default club content. The server reads them from
/// PostgreSQL rows (migration 073, <see cref="RuleRows"/>), first filled from the bundled
/// <c>runtime-source/ultimateteam21/rules/*.json</c> files, and validates them strictly, so a typo stops a load with a
/// clear message instead of silently falling back to a default. Top-level keys starting with "_" are comments.
/// </summary>
public sealed class GameRules
{
    /// <summary>
    /// Loaded from rules/packs.json, then replaced at start-up with database-sourced rules on the
    /// relational backend, where migration 041 made store_packs and its rule tables authoritative.
    /// Nothing else reassigns this; it is settable only so that swap can happen before first use.
    /// </summary>
    public required PackRules Packs { get; set; }
    public required DraftRules Draft { get; init; }
    public required CardRules Cards { get; init; }
    public required ClubRules Club { get; init; }
    public required SquadBattlesRules SquadBattles { get; init; }
    public required SeasonRules Seasons { get; init; }
    public required ObjectiveRules Objectives { get; init; }
    public required SbcRules Sbc { get; init; }
    public required OnboardingRules Onboarding { get; init; }
    public required MarketRules Market { get; init; }
    public required RivalsRewardRules RivalsRewards { get; init; }
    public required ChampionsRewardRules ChampionsRewards { get; init; }
    public required SquadBattlesRewardRules SquadBattlesRewards { get; init; }
    public required StaffRules Staff { get; init; }
    public required TotwArchive Totw { get; init; }
    public required PackOddsRules PackOdds { get; init; }

    /// <summary>rules/tradeability.json: which grant sources hand out untradeable packs and items (D14).</summary>
    public required TradeabilityRules Tradeability { get; init; }

    /// <summary>
    /// The rules files PostgreSQL holds as rows (migration 073), by file name without ".json": every bundled rules file
    /// except packs.json, whose packs have their own tables since migration 041.
    /// </summary>
    public static IReadOnlyList<string> DatabaseFiles { get; } =
    [
        "draft", "cards", "club", "squad_battles", "seasons", "objectives", "sbc", "onboarding", "market",
        "rivals_rewards", "champions_rewards", "sqbt_rewards", "sqbt_totw", "staff", "pack_odds", "tradeability",
    ];

    /// <summary>
    /// The bundled rules files: the defaults the database is first filled from, and what "Import from files" compares. The
    /// mode reward tables come from the bundled seeds (rewards/*.json), as a fresh database is filled.
    /// </summary>
    public static GameRules Load(string dataRoot)
    {
        var directory = Path.Combine(dataRoot, "rules");
        var packs = RulesJson.Deserialize<PackRules>(ReadFile(directory, "packs"), Path.Combine(directory, "packs.json"));
        return FromDocuments(ReadFiles(dataRoot), packs, Data.Repositories.ModeRewardRepository.Seeds(dataRoot),
            file => Path.Combine(directory, file + ".json"));
    }

    /// <summary>The text of every <see cref="DatabaseFiles"/> file of the bundled rules folder.</summary>
    public static IReadOnlyDictionary<string, string> ReadFiles(string dataRoot)
    {
        var directory = Path.Combine(dataRoot, "rules");
        return DatabaseFiles.ToDictionary(file => file, file => ReadFile(directory, file), StringComparer.Ordinal);
    }

    /// <summary>
    /// Rules from their JSON documents (bundled files, or the database rows rebuilt) and the mode reward tables (migrations 126, 131, 133, 135),
    /// validated as strictly as the files: a missing document, unknown key or wrong value type throws
    /// <see cref="InvalidDataException"/> naming its source.
    /// </summary>
    public static GameRules FromDocuments(IReadOnlyDictionary<string, string> documents, PackRules packs,
        ModeRewardTables tables, Func<string, string>? source = null)
    {
        T Parse<T>(string file) => RulesJson.Deserialize<T>(
            documents.TryGetValue(file, out var text) ? text : throw new InvalidDataException($"The {file} rules are missing."),
            source?.Invoke(file) ?? $"{file} rules");
        var rules = new GameRules
        {
            Packs = packs,
            Draft = WithLevels(Parse<DraftRules>("draft"), tables.Draft),
            Cards = Parse<CardRules>("cards"),
            Club = Parse<ClubRules>("club"),
            SquadBattles = Parse<SquadBattlesRules>("squad_battles"),
            Seasons = Parse<SeasonRules>("seasons"),
            Objectives = Parse<ObjectiveRules>("objectives"),
            Sbc = Parse<SbcRules>("sbc"),
            Onboarding = Parse<OnboardingRules>("onboarding"),
            Market = Parse<MarketRules>("market"),
            RivalsRewards = WithRewards(Parse<RivalsRewardRules>("rivals_rewards"), tables.Rivals),
            ChampionsRewards = WithRanks(Parse<ChampionsRewardRules>("champions_rewards"), tables.Champions),
            SquadBattlesRewards = WithTiers(Parse<SquadBattlesRewardRules>("sqbt_rewards"), tables.SquadBattles),
            Totw = Parse<TotwArchive>("sqbt_totw"),
            Staff = Parse<StaffRules>("staff"),
            PackOdds = Parse<PackOddsRules>("pack_odds"),
            Tradeability = Parse<TradeabilityRules>("tradeability"),
        };
        if (rules.ChampionsRewards.MaxWins != rules.Seasons.WeekendLeague.MaxMatches)
        {
            throw new InvalidDataException($"Invalid game rules: champions_rewards.json win ranks cover 0-{rules.ChampionsRewards.MaxWins} wins, "
                + $"but seasons.json weekendLeague.maxMatches is {rules.Seasons.WeekendLeague.MaxMatches}");
        }
        return rules;
    }

    private static DraftRules WithLevels(DraftRules settings, IEnumerable<DraftRewardLevel> levels)
    {
        try
        {
            return settings.WithRewards(levels);
        }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException($"Invalid Draft reward levels: {error.Message}", error);
        }
    }

    private static SquadBattlesRewardRules WithTiers(SquadBattlesRewardRules settings, IEnumerable<SquadBattlesRewardTier> tiers)
    {
        try
        {
            return settings.WithTiers(tiers);
        }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException($"Invalid Squad Battles reward tiers: {error.Message}", error);
        }
    }

    private static RivalsRewardRules WithRewards(RivalsRewardRules settings, RivalsRewardTables tables)
    {
        try
        {
            return settings.WithRewards(tables);
        }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException($"Invalid Division Rivals rewards: {error.Message}", error);
        }
    }

    private static ChampionsRewardRules WithRanks(ChampionsRewardRules settings, IEnumerable<ChampionsRank> ranks)
    {
        try
        {
            return settings.WithRanks(ranks);
        }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException($"Invalid Champions reward ranks: {error.Message}", error);
        }
    }

    /// <summary>Checks one rules document against its file's model; throws <see cref="InvalidDataException"/> when it does not read.</summary>
    public static void ValidateDocument(string file, string text)
    {
        var source = $"{file} rules";
        _ = file switch
        {
            "draft" => (object)RulesJson.Deserialize<DraftRules>(text, source),
            "cards" => RulesJson.Deserialize<CardRules>(text, source),
            "club" => RulesJson.Deserialize<ClubRules>(text, source),
            "squad_battles" => RulesJson.Deserialize<SquadBattlesRules>(text, source),
            "seasons" => RulesJson.Deserialize<SeasonRules>(text, source),
            "objectives" => RulesJson.Deserialize<ObjectiveRules>(text, source),
            "sbc" => RulesJson.Deserialize<SbcRules>(text, source).Checked(),
            "onboarding" => RulesJson.Deserialize<OnboardingRules>(text, source),
            "market" => RulesJson.Deserialize<MarketRules>(text, source),
            "rivals_rewards" => RulesJson.Deserialize<RivalsRewardRules>(text, source),
            "champions_rewards" => RulesJson.Deserialize<ChampionsRewardRules>(text, source),
            "sqbt_rewards" => RulesJson.Deserialize<SquadBattlesRewardRules>(text, source),
            "sqbt_totw" => RulesJson.Deserialize<TotwArchive>(text, source),
            "staff" => RulesJson.Deserialize<StaffRules>(text, source),
            "pack_odds" => RulesJson.Deserialize<PackOddsRules>(text, source),
            "tradeability" => RulesJson.Deserialize<TradeabilityRules>(text, source),
            _ => throw new InvalidDataException($"There are no {file} rules."),
        };
    }

    private static string ReadFile(string directory, string file)
    {
        var path = Path.Combine(directory, file + ".json");
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Invalid game rules file {path}: {error.Message}", error);
        }
    }
}
