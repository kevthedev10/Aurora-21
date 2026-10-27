using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Server.Hosting;

namespace Aurora21.UltimateTeam.Server.Tools;

/// <summary>Offline club maintenance commands. The launcher scripts refuse to run them while the game or server is up.</summary>
public static class ClubCommands
{
    public static async Task<int> GrantCardAsync(string? dataRoot, long resourceId, string pile, bool allowDuplicate, long? replaceResourceId)
    {
        var (context, fut) = await OpenAsync(dataRoot);
        using var opened = context;
        if (resourceId <= 0 || !context.Players.Contains(resourceId))
        {
            throw new UsageException("--grant-card must name a known positive player resource ID");
        }
        var replaced = 0;
        if (replaceResourceId is { } oldId)
        {
            if (oldId <= 0 || oldId == resourceId)
            {
                throw new UsageException("--replace-resource-id must be a different positive resource ID");
            }
            try
            {
                replaced = await fut.Inventory.RemoveNonSquadPlayerItemsAsync(oldId);
            }
            catch (ClubRuleException error)
            {
                throw new UsageException(error.Message);
            }
        }
        var name = context.Players.Find(resourceId)!.Text("name");
        var existing = await context.ReadAsync(uow => fut.Inventory.OwnedPlayerItemAsync(uow, resourceId), CancellationToken.None);
        if (existing is not null && !allowDuplicate)
        {
            Console.WriteLine(FutJson.Serialize(new JsonObject
            {
                ["status"] = "already-owned",
                ["resourceId"] = resourceId,
                ["itemId"] = existing.Int("id"),
                ["name"] = name,
                ["replaced"] = replaced,
            }));
            return 0;
        }
        var item = await context.RunAsync("grant-card", uow => fut.Items.MakePlayerAsync(uow, resourceId, pile), CancellationToken.None);
        Console.WriteLine(FutJson.Serialize(new JsonObject
        {
            ["status"] = "granted",
            ["resourceId"] = resourceId,
            ["itemId"] = item.Int("id"),
            ["pile"] = pile,
            ["name"] = name,
            ["replaced"] = replaced,
        }));
        return 0;
    }

    /// <summary>Adds one club copy of every catalog player not already owned or waiting in Unassigned.</summary>
    public static async Task<int> GrantAllCardsAsync(string? dataRoot)
    {
        var (context, fut) = await OpenAsync(dataRoot);
        using var opened = context;
        return await GrantAllCardsAsync(context, fut, Console.Out);
    }

    /// <summary>
    /// Deliberately resumable: each card is its own savepoint, so a card that cannot be built rolls back
    /// completely (including its item id) while the others commit. The command then exits 2, and a rerun
    /// grants only the cards still missing.
    /// </summary>
    internal static async Task<int> GrantAllCardsAsync(GameContext context, FutServices fut, TextWriter output, IEnumerable<long>? catalog = null)
    {
        var owned = await context.ReadAsync(async uow =>
        {
            var known = await fut.Inventory.OwnedPlayerResourceIdsAsync(uow);
            foreach (var item in (await uow.ListItemsAsync("unassigned")).Where(ClubInventoryService.IsPlayer))
            {
                known.Add(ClubInventoryService.ResourceOrAsset(item));
            }
            return known;
        }, CancellationToken.None);
        var missing = (catalog ?? context.Players.Ids).Where(id => !owned.Contains(id)).ToList();
        output.WriteLine(FutJson.Serialize(new JsonObject { ["status"] = "starting", ["catalog"] = context.Players.Count, ["missing"] = missing.Count }));
        var granted = 0;
        var failed = 0;
        await context.RunAsync("grant-all-cards", async uow =>
        {
            await uow.LockClubAsync();
            foreach (var resourceId in missing)
            {
                try
                {
                    await uow.SavepointAsync(async () => await fut.Items.MakePlayerAsync(uow, resourceId, "club"));
                    granted++;
                }
                catch (InvalidOperationException error)
                {
                    failed++;
                    output.WriteLine(FutJson.Serialize(new JsonObject { ["status"] = "failed", ["resourceId"] = resourceId, ["error"] = error.Message }));
                }
            }
        }, CancellationToken.None);
        output.WriteLine(FutJson.Serialize(new JsonObject { ["status"] = "done", ["granted"] = granted, ["failed"] = failed }));
        return failed > 0 ? 2 : 0;
    }

    /// <summary>Deletes this persona's club (items, squads, progress). The next start creates a fresh club.</summary>
    public static async Task<int> ResetClubAsync(string? dataRoot)
    {
        var paths = RuntimePaths.Resolve(dataRoot);
        paths.EnsureDirectories();
        var config = ServerConfig.Load(new UserConfigStore(paths));
        using var database = ClubDatabase.Connect(paths, testMode: false, connectionString: null, config.Database, Loggers.CreateLogger("UltimateTeam.Database"));
        await DatabasePreflight.CheckAsync(database.DataSource, database.Settings, Loggers.CreateLogger("UltimateTeam.Database"));
        using var context = await GameContext.CreateAsync(paths, Loggers, testMode: false, connectionString: database.ConnectionString);
        var deleted = await ClubOpen.DeleteClubAsync(context.UnitOfWork, config.PersonaId);
        Console.WriteLine(FutJson.Serialize(new JsonObject
        {
            ["status"] = deleted ? "reset" : "no-club",
            ["personaId"] = config.PersonaId,
            ["database"] = database.Describe(),
        }));
        return 0;
    }

    /// <summary>Reports relational storage readiness without changing club state.</summary>
    public static async Task<int> RepairDatabaseAsync(string? dataRoot, bool apply)
    {
        var paths = RuntimePaths.Resolve(dataRoot);
        paths.EnsureDirectories();
        var config = ServerConfig.Load(new UserConfigStore(paths));
        using var database = ClubDatabase.Connect(paths, testMode: false, connectionString: null, config.Database, Loggers.CreateLogger("UltimateTeam.Database"));
        await DatabasePreflight.CheckAsync(database.DataSource, database.Settings, Loggers.CreateLogger("UltimateTeam.Database"));
        if (apply) throw new UsageException("repair-db --apply is retired; relational maintenance runs at startup.");
        var status = await RelationalStorageReadiness.InspectForRuntimeAsync(database.DataSource);
        Console.WriteLine(FutJson.Serialize(new JsonObject { ["backend"] = "relational", ["status"] = status.State.ToString(), ["detail"] = status.Detail, ["database"] = database.Describe() }));
        return status.State == RelationalStorageState.RelationalReady ? 0 : 3;
    }

    private static readonly ILoggerFactory Loggers = ConsoleLogging.Quiet();

    private static async Task<(GameContext Context, FutServices Fut)> OpenAsync(string? dataRoot)
    {
        var context = await GameContext.CreateAsync(RuntimePaths.Resolve(dataRoot), Loggers, testMode: false);
        try
        {
            return (context, await FutServices.CreateAsync(context));
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }
}
