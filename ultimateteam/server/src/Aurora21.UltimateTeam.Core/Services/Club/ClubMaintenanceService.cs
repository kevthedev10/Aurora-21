using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;

namespace Aurora21.UltimateTeam.Core.Services.Club;

/// <summary>
/// The starter setup of a new club, run once when the club first opens: the initial players, a manager, the default
/// kits and the first squad. An initialized club is never set up again, so card, rule and deploy changes do not
/// rescan existing clubs. Chemistry styles and position modifiers come from packs, not from this setup.
/// </summary>
public sealed class ClubMaintenanceService
{
    /// <summary>Set once a club has its starting content: seeded at open, or created by FIFA (CreateUser).</summary>
    internal const string RelationalInitializedFlag = "relational_club_initialized_v1";

    /// <summary>
    /// A club FIFA created (its first-run screens): its starter squad came from CreateUser, so maintenance never tops it up
    /// with the configured initial players, manager or kits.
    /// </summary>
    internal const string NativeClubFlag = "native_club_created_v1";

    private readonly FutServices _fut;
    private readonly ILogger _log;

    public ClubMaintenanceService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Maintenance");
    }

    private GameContext Game => _fut.Game;

    /// <summary>Sets the club up when it has never been set up; an initialized club costs one flag read.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (await Game.ReadAsync(w => w.HasFlagAsync(RelationalInitializedFlag), cancellationToken)) return;
        await InitializeRelationalClubAsync(cancellationToken);
    }

    /// <summary>
    /// Initial club, staged and persisted with one bulk item save.
    /// </summary>
    internal async Task InitializeRelationalClubAsync(CancellationToken cancellationToken = default)
    {
        await Game.RunAsync("relational-club-initialize", w => InitializeRelationalAsync(w), cancellationToken);
    }

    /// <summary>
    /// Completes a club FIFA has just created (CreateUser staged its starter squad in this transaction).
    /// </summary>
    internal ValueTask CompleteNativeCreationAsync(IUnitOfWork uow) =>
        InitializeRelationalAsync(uow, creating: true);

    private async ValueTask InitializeRelationalAsync(IUnitOfWork uow, bool creating = false)
    {
        await uow.LockClubAsync();
        // Checked again under the club lock: a concurrent open may have set the club up meanwhile.
        var flags = await uow.FlagsAsync(RelationalInitializedFlag, NativeClubFlag, ClubRepository.NativeCreationRequiredFlag);
        if (flags.Contains(RelationalInitializedFlag))
        {
            return;
        }
        // A club FIFA creates stays empty until its CreateUser: nothing is seeded, and no flag marks it maintained.
        if (!creating && (Game.NativeClubCreation || flags.Contains(ClubRepository.NativeCreationRequiredFlag)))
        {
            return;
        }
        var native = creating || flags.Contains(NativeClubFlag);
        var items = await uow.ListItemsAsync();
        var staged = new List<Func<long, JsonObject>>();
        var changed = new List<JsonObject>();

        // A natively created club's players, manager and kits are its own choices, never topped up from the config.
        if (!native)
        {
            var target = Math.Max(Game.Rules.Club.Squad.Starters, Game.Config.InitialClubPlayerCount);
            var ownedPlayers = await _fut.Inventory.OwnedPlayerResourceIdsAsync(uow);
            foreach (var id in InitialPlayerResourceIds(target).Where(id => !ownedPlayers.Contains(id)).Take(Math.Max(0, target - ownedPlayers.Count)))
            {
                staged.Add(itemId => _fut.Items.BuildPlayer(id, itemId, "club"));
            }

            if (!items.Any(item => item.Text("itemType") == "manager") && Game.Managers.BestManagerId() is { } managerId)
            {
                staged.Add(itemId => _fut.Items.BuildManager(managerId, itemId, "club"));
            }

            var selectedStates = (await uow.RelationalItems.Customisation.ReadAsync(uow)).Select(b => b.State).ToHashSet(StringComparer.Ordinal);
            StageInitialKits(items, staged, changed, selectedStates);
        }

        // Chemistry styles and position modifiers come from packs; a new club starts without them.
        if (staged.Count > 0)
        {
            // Same native order as sequential allocation: players, manager, kits.
            var first = await uow.ReserveItemIdsAsync(staged.Count);
            changed.AddRange(staged.Select((build, index) => build(first + index)));
        }
        await uow.SaveItemsAsync(changed);
        if (!await uow.AnySquadsAsync())
        {
            await _fut.Inventory.SeedSquadAsync(uow);
        }
        else
        {
            await _fut.Inventory.RepairEmptyActiveSquadAsync(uow);
        }
        await uow.SetFlagsAsync(creating
            ? new[] { RelationalInitializedFlag, NativeClubFlag }
            : new[] { RelationalInitializedFlag });
        _log.LogInformation("Initialized relational club with {Count} new item(s)", staged.Count);
    }
    /// <summary>The kit rule of <see cref="EnsureInitialClubKitsAsync"/>, staged: reuse an owned kit or create one per missing slot.</summary>
    private void StageInitialKits(IReadOnlyList<JsonObject> items, List<Func<long, JsonObject>> staged, List<JsonObject> changed, HashSet<string>? selectedStates = null)
    {
        var kits = items.Where(item => item.Text("itemType") == "kit").ToList();
        var activeByState = kits
            .Where(item => item.Text("itemState") is "activeHomeKit" or "activeAwayKit")
            .GroupBy(item => item.Text("itemState"))
            .ToDictionary(group => group.Key, group => group.Last());
        foreach (var kit in Game.Rules.Club.InitialKits)
        {
            if (selectedStates?.Contains(kit.ItemState) == true) continue;
            if (activeByState.TryGetValue(kit.ItemState, out var existing))
            {
                var homeResource = activeByState.TryGetValue("activeHomeKit", out var home) ? home.Int("resourceId") : 0;
                var existingResource = existing.Int("resourceId");
                if (kit.ItemState == "activeHomeKit" || (existingResource != 0 && existingResource != homeResource))
                {
                    continue;
                }
            }
            if (kits.FirstOrDefault(item => item.Int("resourceId") == kit.ResourceId
                && item.Text("itemState") is not ("activeHomeKit" or "activeAwayKit")) is { } owned)
            {
                var equipped = owned.Copy();
                equipped["itemState"] = kit.ItemState;
                equipped["pile"] = "club";
                changed.Add(equipped);
            }
            else
            {
                var rule = kit;
                staged.Add(itemId => ItemFactory.KitItem(itemId, Game.NowSeconds, rule.ResourceId, rule.ItemState, "club", rule.TeamId, rule.KitType, 2 + rule.KitType, rule.Name));
            }
        }
    }

    /// <summary>A deterministic, playable initial club: configured starters first, then strongest base cards.</summary>
    public List<long> InitialPlayerResourceIds(int count)
    {
        var strongest = Game.Players.Entries
            .Where(entry => entry.Value.Rareflag is 0 or 1)
            .OrderBy(entry => -entry.Value.Rating)
            .ThenBy(entry => entry.Key)
            .Select(entry => entry.Key);
        return Game.Config.StarterPlayers.Concat(strongest)
            .Where(Game.Players.Contains)
            .Distinct()
            .Take(count)
            .ToList();
    }

}
