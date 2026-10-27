using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// A pick's reveal names the choices the club already owns (10-07 report: a picked duplicate offered Send to Club, PUT /item
/// kept it in Unassigned and the next pack was refused). FIFA reads duplicates only from duplicateItemIdList, and the select
/// reply is the bare item, so the reveal is where it learns them.
/// </summary>
public sealed class PlayerPickDuplicateTests
{
    private const string G = "/ut/game/fifa21";

    [Fact]
    public async Task ThePickRevealListsTheChoicesTheClubOwns()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var token = rt.Fut.Items.MakePlayerPick(86, 4);
        var first = rt.CallObject("POST", $"{G}/item/nontargeted?itemId={token.Int("id")}");
        var choices = first["choiceData"]!.AsArray().OfType<JsonObject>().ToList();
        var club = rt.Game.Store().ListItems("club").Concat(rt.Game.Store().ListItems("squad")).ToList();
        Assert.Equal(choices.Where(choice => club.Any(item => item.Int("resourceId") == choice.Int("resourceId"))).Select(choice => choice.Int("id")).Order(),
            Duplicates(first).Keys.Order());

        // Without its club copies a choice is new; with a copy back in the club the reveal names it with that copy.
        var picked = choices[1];
        foreach (var copy in club.Where(item => item.Int("resourceId") == picked.Int("resourceId")))
        {
            rt.Game.InUnit(async work =>
            {
                await work.ExecuteAsync("""
                    DELETE FROM ultimateteam.squad_players sp USING ultimateteam.items i
                    WHERE i.club_id = @club AND i.native_item_id = @id AND sp.item_id = i.id
                    """, ("id", copy.IntKeep("id")));
                await work.DeleteItemAsync(copy.IntKeep("id"));
            });
        }
        var cleared = Duplicates(rt.CallObject("POST", $"{G}/item/nontargeted?itemId={token.Int("id")}"));
        Assert.False(cleared.ContainsKey(picked.Int("id")));
        var owned = await rt.Game.RunAsync("own-a-choice", async work =>
        {
            var item = rt.Fut.Items.BuildPlayer(picked.Int("resourceId"), await work.NextItemIdAsync(), "club");
            await work.SaveItemAsync(item);
            return item;
        }, CancellationToken.None);
        var revealed = Duplicates(rt.CallObject("POST", $"{G}/item/nontargeted?itemId={token.Int("id")}"));
        Assert.Equal(owned.IntKeep("id"), revealed[picked.Int("id")]);
        Assert.Equal(cleared.Count + 1, revealed.Count);

        // The pick grants the very item the reveal named, so FIFA's duplicate entry still applies to it.
        var confirmed = rt.CallObject("POST", $"{G}/playerpicks/item/{picked.Int("resourceId")}/select");
        Assert.Equal(picked.Int("id"), confirmed.Int("id"));
        Assert.Equal("unassigned", rt.Game.Store().GetItem(picked.Int("id"))!.Text("pile"));
    }

    private static Dictionary<long, long> Duplicates(JsonObject reveal) =>
        reveal["duplicateItemIdList"]!.AsArray().OfType<JsonObject>().ToDictionary(entry => entry.Int("itemId"), entry => entry.Int("duplicateItemId"));
}
