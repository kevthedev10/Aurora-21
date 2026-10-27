using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Services.Auth;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Services.Market;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Aurora21.UltimateTeam.Core.Services.Store;

namespace Aurora21.UltimateTeam.Core.Services;

/// <summary>Composition root for the FUT feature services (they reference one another through this).</summary>
public sealed class FutServices
{
    private FutServices(GameContext game)
    {
        Game = game;
        Items = new ItemFactory(game);
        Native = new NativeItems(game, Items);
        Awards = new RewardAwardService(game.Packs);
        Pools = new ResourcePools(game);
        Inventory = new ClubInventoryService(this);
        Club = new ClubPresentationService(this);
        Search = new ClubSearchService(this);
        Customisation = new CustomisationService(this);
        Vanity = new VanityService(this);
        Rivals = new RivalsService(this);
        WeekendLeague = new WeekendLeagueService(this);
        Packs = new PackOpeningService(this);
        CompletionPacks = new CompletionPackService(this);
        LegacyMatchPacks = new LegacyMatchPackService(this);
        Picks = new PlayerPickService(this);
        Store = new StoreService(this);
        Market = new SharedMarketService(this);
        Onboarding = new OnboardingService(this);
        Draft = new DraftService(this);
        SquadBattles = new SquadBattlesService(this);
        Matches = new MatchService(this);
        Seasons = new SeasonsService(this);
        Objectives = new ObjectivesService(this);
        Leaderboards = new LeaderboardService(this);
        Sbc = new SbcService(this);
        Auth = new LocalAuthService(this);
    }

    public GameContext Game { get; }

    /// <summary>Every player's club on this server (set once the club is registered).</summary>
    public Sessions.PlayerRegistry? Players { get; internal set; }
    public ItemFactory Items { get; }
    public NativeItems Native { get; }
    public RewardAwardService Awards { get; }
    public ResourcePools Pools { get; }
    public ClubInventoryService Inventory { get; }
    public ClubPresentationService Club { get; }
    public ClubSearchService Search { get; }
    public CustomisationService Customisation { get; }
    public VanityService Vanity { get; }
    public RivalsService Rivals { get; }
    public WeekendLeagueService WeekendLeague { get; }
    public PackOpeningService Packs { get; }
    public CompletionPackService CompletionPacks { get; }
    public LegacyMatchPackService LegacyMatchPacks { get; }
    public PlayerPickService Picks { get; }
    public StoreService Store { get; }
    public SharedMarketService Market { get; }
    public OnboardingService Onboarding { get; }
    public DraftService Draft { get; }
    public SquadBattlesService SquadBattles { get; }
    public MatchService Matches { get; }
    public SeasonsService Seasons { get; }
    public ObjectivesService Objectives { get; }
    public LeaderboardService Leaderboards { get; }
    public SbcService Sbc { get; }
    public LocalAuthService Auth { get; }

    /// <summary>Builds the services and runs the idempotent start-up club maintenance.</summary>
    public static async Task<FutServices> CreateAsync(GameContext game, CancellationToken cancellationToken = default)
    {
        using var content = game.Live.Pin();
        var services = new FutServices(game);
        await new ClubMaintenanceService(services).RunAsync(cancellationToken);
        return services;
    }

    internal static FutServices CreateRelationalSlice(GameContext game) => new(game);

    /// <summary>The isolated relational services with the fresh-club initializer; legacy maintenance repairs are not run.</summary>
    internal static async Task<FutServices> CreateInitializedRelationalSliceAsync(GameContext game, CancellationToken cancellationToken = default)
    {
        using var content = game.Live.Pin();
        var services = CreateRelationalSlice(game);
        await new ClubMaintenanceService(services).InitializeRelationalClubAsync(cancellationToken);
        return services;
    }
}
