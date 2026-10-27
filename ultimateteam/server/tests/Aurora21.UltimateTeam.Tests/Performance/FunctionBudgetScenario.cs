using System.Diagnostics;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Services;

namespace Aurora21.UltimateTeam.Tests.Performance;

/// <summary>Sequential direct-call attribution on the existing throwaway fixture; never a capacity test.</summary>
internal static class FunctionBudgetScenario
{
    public static async Task<JsonObject> RunAsync(GameContext game, FutServices fut, int samples,
        CancellationToken cancellationToken, Action<string>? progress, Func<long> commands)
    {
        var report = new JsonObject
        {
            ["boundary"] = "Fresh snapshots per call unless labelled warm; warm means CountPile has initialized all item dictionaries. Snapshot preparation and pool/begin/commit excluded from direct-call measurement; called child functions and SQL are included. Synchronous helper deltas use current-thread counters; async helper deltas use whole-process counters and can include driver/background runtime work. No soak producer or maintenance job runs. Rows overlap; do not sum nested function budgets. The connection stays leased through each measured function.",
        };
        var shape = await game.ReadAsync(async uow =>
        {
            var snapshot = await ClubSnapshot.LoadAsync(uow);
            return new JsonObject
            {
                ["items"] = snapshot.Items().Count,
                ["clubPile"] = snapshot.CountPile("club"),
                ["squads"] = snapshot.Squads().Count,
                ["playerCatalogEntries"] = game.Players.Entries.Count(),
            };
        }, cancellationToken);
        report["fixtureShape"] = shape;
        var budgets = new JsonObject();
        foreach (var name in new[] { "ClubSnapshot.LoadAsync", "ClubSnapshot.Items", "ClubSnapshot.Items.warm", "ClubSnapshot.Pile",
            "ClubSnapshot.CountPile.cold", "ClubSnapshot.CountPile.warm", "ClubSnapshot.Squads",
            "ClubPresentationService.DefaultManager", "ClubPresentationService.ActiveClubItemsAsync", "ClubPresentationService.ActiveClubItemsAsync.warm",
            "ClubPresentationService.NativeSquadAsync", "ClubPresentationService.HubAsync",
            "ClubInventoryService.OwnedPlayerResourceIdsAsync", "SquadBattlesService.OpponentAsync" })
        {
            var timings = new PerformanceScenario.LatencyHistogram();
            long allocated = 0, sql = 0;
            var synchronous = name.StartsWith("ClubSnapshot.", StringComparison.Ordinal) && name != "ClubSnapshot.LoadAsync"
                || name == "ClubPresentationService.DefaultManager";
            for (var sample = -5; sample < samples; sample++)
            {
                await game.ReadAsync(async uow =>
                {
                    var snapshot = name is "ClubSnapshot.LoadAsync" or "ClubPresentationService.HubAsync"
                        or "ClubInventoryService.OwnedPlayerResourceIdsAsync" or "SquadBattlesService.OpponentAsync"
                        ? null : await ClubSnapshot.LoadAsync(uow);
                    if (name.EndsWith(".warm", StringComparison.Ordinal)) { _ = snapshot!.CountPile("club"); }
                    var before = synchronous ? GC.GetAllocatedBytesForCurrentThread() : GC.GetTotalAllocatedBytes(true);
                    var beforeSql = commands();
                    var timer = Stopwatch.StartNew();
                    switch (name)
                    {
                        case "ClubSnapshot.LoadAsync": _ = await ClubSnapshot.LoadAsync(uow); break;
                        case "ClubSnapshot.Items" or "ClubSnapshot.Items.warm": _ = snapshot!.Items(); break;
                        case "ClubSnapshot.Pile": _ = snapshot!.Pile("club"); break;
                        case "ClubSnapshot.CountPile.cold" or "ClubSnapshot.CountPile.warm": _ = snapshot!.CountPile("club"); break;
                        case "ClubSnapshot.Squads": _ = snapshot!.Squads(); break;
                        case "ClubPresentationService.DefaultManager": _ = fut.Club.DefaultManager(snapshot!); break;
                        case "ClubPresentationService.ActiveClubItemsAsync" or "ClubPresentationService.ActiveClubItemsAsync.warm":
                            _ = await fut.Club.ActiveClubItemsAsync(uow, snapshot); break;
                        case "ClubPresentationService.NativeSquadAsync": _ = await fut.Club.NativeSquadAsync(uow, snapshot); break;
                        case "ClubPresentationService.HubAsync": _ = await fut.Club.HubAsync(uow); break;
                        case "ClubInventoryService.OwnedPlayerResourceIdsAsync": _ = await fut.Inventory.OwnedPlayerResourceIdsAsync(uow); break;
                        case "SquadBattlesService.OpponentAsync": _ = await fut.SquadBattles.OpponentAsync(uow, 0); break;
                        default: throw new InvalidOperationException("Unknown direct-call budget");
                    }
                    timer.Stop();
                    var delta = (synchronous ? GC.GetAllocatedBytesForCurrentThread() : GC.GetTotalAllocatedBytes(true)) - before;
                    if (sample >= 0)
                    {
                        allocated += delta;
                        sql += commands() - beforeSql;
                        timings.Add(timer.Elapsed.TotalMilliseconds);
                    }
                    return true;
                }, cancellationToken);
            }
            var row = timings.Report();
            row["allocatedBytesPerCall"] = allocated / samples;
            row["sqlCommandsPerCall"] = (double)sql / samples;
            row["allocationCounter"] = synchronous ? "current-thread synchronous" : "whole-process asynchronous";
            budgets[name] = row;
            progress?.Invoke($"Function {name}: mean={row["meanMs"]} ms, bytes/call={allocated / samples}");
        }
        report["budgets"] = budgets;
        return report;
    }
}
