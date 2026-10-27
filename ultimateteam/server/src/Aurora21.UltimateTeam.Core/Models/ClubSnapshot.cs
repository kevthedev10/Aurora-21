using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Models;

/// <summary>
/// The club's items and squads read once for one response, so assembling the user, squad and
/// stadium documents does not query the whole club repeatedly. Every read returns copies.
/// </summary>
public sealed class ClubSnapshot
{
    internal ClubPresentationView Presentation { get; }

    private ClubSnapshot(ClubPresentationView presentation)
    {
        Presentation = presentation;
    }

    internal static ClubSnapshot FromPresentation(ClubPresentationView view) => new(view);

    public static async ValueTask<ClubSnapshot> LoadAsync(IUnitOfWork uow) =>
        new(await ClubPresentationView.LoadFullAsync(uow));

    public List<JsonObject> Items() => Presentation.Items();

    public List<JsonObject> Pile(string pile) => Presentation.Pile(pile);

    public int CountPile(string pile) => Presentation.CountPile(pile);

    public List<JsonObject> Squads() => Presentation.Squads();
}
