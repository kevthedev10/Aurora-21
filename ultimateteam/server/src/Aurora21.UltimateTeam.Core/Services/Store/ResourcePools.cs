using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers;

namespace Aurora21.UltimateTeam.Core.Services.Store;

/// <summary>
/// Named card pools a pack or player pick can draw from (store_packs.resource_pool and a pick group's resource_pool;
/// migration 061, Python server 764ef6e): <c>totw_1_3</c>, the authored weeks 1–3 in-forms,
/// and <c>totw_1_3_red</c>, their FUT Champions red copies. These named reward pools
/// stay fixed when the ordinary promo phase changes. An unknown name is an empty pool.
/// </summary>
public sealed class ResourcePools
{
    public const string TotwOneToThree = "totw_1_3";
    public const string TotwOneToThreeRed = "totw_1_3_red";

    private readonly GameContext _game;
    private sealed record Pools(IReadOnlySet<long> Totw, IReadOnlySet<long> Reds);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ContentSnapshot, Lazy<Pools>> _revisions = new();

    public ResourcePools(GameContext game) => _game = game;

    private Pools At(ContentSnapshot snapshot) => _revisions.GetValue(snapshot, revision => new Lazy<Pools>(() => Build(revision))).Value;

    private static Pools Build(ContentSnapshot snapshot)
    {
        var totw = new long[] { 1, 2, 3 }
            .SelectMany(week => (snapshot.Rules.Totw.Week(week) ?? throw new InvalidDataException($"Missing TOTW week {week}"))
                .Starters.Concat(snapshot.Rules.Totw.Week(week)!.Subs))
            .Select(card => card.ResourceId).Where(id => snapshot.Players.Find(id) is not null && !snapshot.Cards.IsGrantOnly(id)).ToHashSet();
        var reds = totw.Select(id => RedCopyOf(snapshot, id)).OfType<long>()
            .Where(id => IsGoldRed(snapshot, id)).ToHashSet();
        return new Pools(totw, reds);
    }

    /// <summary>Champions rewards use the red card's own rating, not its footballer's base-card tier.</summary>
    public static bool IsGoldRed(ContentSnapshot snapshot, long id) => snapshot.Players.Find(id) is { } meta
        && snapshot.Rules.Cards.FutChampionsRedRareflags.Contains(meta.Rareflag)
        && snapshot.Rules.Packs.TierFor(meta.Rating) == "gold";

    public IReadOnlySet<long> TotwOneToThreeCards => At(_game.Live.Snapshot).Totw;
    public IReadOnlySet<long> TotwOneToThreeRedCards => At(_game.Live.Snapshot).Reds;

    private static bool IsBase(ContentSnapshot snapshot, long id) => snapshot.Players.Find(id) is { } meta && meta.Rating > 0 && !meta.Special
        && meta.Rareflag is 0 or 1 && !snapshot.Cards.IsGrantOnly(id) && !snapshot.Cards.IsAquaPro(id);

    public bool IsBase(long id) => IsBase(_game.Live.Snapshot, id);

    private static long? RedCopyOf(ContentSnapshot snapshot, long informId)
    {
        var inform = snapshot.Players.Find(informId)!;
        var asset = inform.IntOr("assetId", informId, informId);
        for (var index = 0; index < snapshot.Rules.PackOdds.RedRewardRevisions.Count; index++)
        {
            var id = asset + snapshot.Rules.PackOdds.RedRewardRevisions[index] * PlayerCatalog.ResourceRevisionSize;
            if (snapshot.Players.Find(id) is not { } red || !snapshot.Cards.Rules.FutChampionsRedRareflags.Contains(red.Rareflag)) continue;
            if (index == 0 || red.Rating == inform.Rating) return id;
        }
        return null;
    }

    /// <summary>The cards a named pool holds; null when no pool is named.</summary>
    public IReadOnlySet<long>? Named(string? name, ContentSnapshot? snapshot = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        return name switch
        {
            TotwOneToThree => At(snapshot ?? _game.Live.Snapshot).Totw,
            TotwOneToThreeRed => At(snapshot ?? _game.Live.Snapshot).Reds,
            _ => new HashSet<long>(),
        };
    }

    /// <summary>
    /// A player pick's pool within its rating range: null when none is named, and ignored (null, <paramref name="ignored"/>
    /// set) when it holds fewer cards than the pick offers.
    /// </summary>
    public IReadOnlySet<long>? ForPick(string? name, long minRating, long maxRating, long choices, out bool ignored)
    {
        ignored = false;
        if (Named(name) is not { } pool)
        {
            return null;
        }
        var inRange = pool.Where(id => _game.Players.Find(id) is { } meta && meta.Rating >= minRating && meta.Rating <= maxRating).ToHashSet();
        ignored = inRange.Count < choices;
        return ignored ? null : inRange;
    }
}
