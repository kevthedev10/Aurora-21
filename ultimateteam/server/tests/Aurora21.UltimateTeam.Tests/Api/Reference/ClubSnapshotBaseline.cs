// Frozen pre-repair oracle from 3a40319; only namespace and type names changed.
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Tests.Api.Reference;

/// <summary>
/// The club's items and squads read once for one response, so assembling the user, squad and
/// stadium documents does not query the whole club repeatedly. Every read returns copies.
/// </summary>
public sealed class ClubSnapshotBaseline
{
    private readonly List<JsonObject> _items;
    private readonly List<JsonObject> _squads;

    private ClubSnapshotBaseline(List<JsonObject> items, List<JsonObject> squads)
    {
        _items = items;
        _squads = squads;
    }

    public static async ValueTask<ClubSnapshotBaseline> LoadAsync(IUnitOfWork uow) =>
        new(await uow.ListItemsAsync(), await uow.ListSquadsAsync());

    public List<JsonObject> Items() => _items.Select(item => item.Copy()).ToList();

    public List<JsonObject> Pile(string pile) => _items.Where(item => item.Text("pile") == pile).Select(item => item.Copy()).ToList();

    public int CountPile(string pile) => _items.Count(item => item.Text("pile") == pile);

    public List<JsonObject> Squads() => _squads.Select(squad => squad.Copy()).ToList();
}
