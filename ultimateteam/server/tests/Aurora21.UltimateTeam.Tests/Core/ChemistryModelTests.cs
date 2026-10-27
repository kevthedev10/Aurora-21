using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>FIFA 21's squad chemistry (chemistry.json), on squads whose result follows from the formula alone.</summary>
public sealed class ChemistryModelTests
{
    private static readonly ChemistryModel Model = ChemistryModel.Load(TestRuntime.DataRoot);

    /// <summary>Eleven starters placed in their formation's exact positions.</summary>
    private static List<JsonObject?> Squad(string formation, Func<int, (long Club, long League, long Nation)> identity, long owners)
    {
        var slots = Model.Formations[formation];
        return slots.Select((slot, index) =>
        {
            var (club, league, nation) = identity(index);
            return (JsonObject?)new JsonObject
            {
                ["preferredPosition"] = Model.Positions[slot],
                ["teamid"] = club, ["leagueId"] = league, ["nation"] = nation,
                ["itemState"] = "free", ["owners"] = owners, ["loyaltyBonus"] = 0,
            };
        }).ToList();
    }

    [Fact]
    public void ElevenLinkedLoyalPlayersInPositionReachTheCap()
    {
        // Each: link 3.5 x position 3.0 rounds to 11, +1 loyal, capped at 10 per player; the squad caps at 100.
        Assert.Equal(100, Model.SquadChemistry("f442", Squad("f442", _ => (1, 13, 14), owners: 1), manager: null));
    }

    [Fact]
    public void UnlinkedPlayersInPositionScoreThreeEach()
    {
        // No shared club, league or nation: link 0.9 x position 3.0 = 2.7, rounded to 3; not loyal (two owners), no manager.
        Assert.Equal(33, Model.SquadChemistry("f442", Squad("f442", index => (100 + index, 200 + index, 300 + index), owners: 2), manager: null));
    }

    [Fact]
    public void AManagerOfTheSameNationAddsOnePerPlayerAndAnEmptySlotScoresNothing()
    {
        var squad = Squad("f442", index => (100 + index, 200 + index, 300 + index), owners: 2);
        var manager = new JsonObject { ["leagueId"] = 999, ["nation"] = 300 };
        // Only the goalkeeper (nation 300) shares the manager's nation: 33 + 1.
        Assert.Equal(34, Model.SquadChemistry("f442", squad, manager));
        squad[0] = null;
        Assert.Equal(30, Model.SquadChemistry("f442", squad, manager));
    }

    [Fact]
    public void APlayerOutOfPositionScoresLessAndAnUnknownFormationHasNoChemistry()
    {
        var squad = Squad("f442", index => (100 + index, 200 + index, 300 + index), owners: 2);
        var inPosition = Model.SquadChemistry("f442", squad, null)!.Value;
        squad[0]!["preferredPosition"] = "ST";
        Assert.True(Model.SquadChemistry("f442", squad, null) < inPosition);
        Assert.Null(Model.SquadChemistry("f999", squad, null));
    }
}
