using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services.Store;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Two kinds of player pick (owner, 2026-10-04): a random pick draws its choices by rating, specials and sources; a fixed
/// pick's group lists the cards (choices) and offers exactly those, for example two versions of one player.
/// </summary>
public sealed class FixedPlayerPickTests
{
    private static JsonObject Pack(params long[] choices) => new()
    {
        ["name"] = "Two versions pick", ["size"] = 1,
        ["player_picks"] = new JsonArray(new JsonObject
        {
            ["count"] = 1, ["choice_count"] = choices.Length, ["label"] = "Pick one",
            ["choices"] = new JsonArray(choices.Select(id => (JsonNode)id).ToArray()),
        }),
    };

    [Fact]
    public async Task AFixedPickOffersExactlyItsCardsInOrder()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var cards = rt.Game.Players.Entries.Where(entry => entry.Value.Rating >= 85 && !rt.Game.Cards.IsGrantOnly(entry.Key))
            .Select(entry => entry.Key).Take(2).ToArray();
        PlayerPickDraw.Validate(rt.Game, Pack(cards));
        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("SELECT set_config('ultimateteam.content_publish','on',true)");
            return await work.ExecuteAsync("""
                INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,price,size,gold_weight,rare_slots,player_picks)
                VALUES(91,'store',true,'Two versions pick',100,1,100,0,@picks::jsonb)
                """, ("picks", Pack(cards)["player_picks"]!.ToJsonString()));
        });
        await using (var source = NpgsqlDataSource.Create(rt.ConnectionString))
            rt.Game.Packs.Publish((await StorePackCatalog.LoadAsync(source)).Rules);
        rt.Game.Store().AddCredits(1000);
        Assert.Equal(200, rt.CallRaw("POST", "/ut/game/fifa21/purchased/items", new JsonObject { ["packId"] = 91, ["currency"] = "COINS" }).Status);
        var token = rt.Game.Store().ListItems("unassigned").Single();
        var choices = await rt.Fut.Picks.RedeemAsync(token.Int("id"));
        Assert.Equal(cards, choices.OfType<JsonObject>().Select(choice => choice.Int("resourceId")));
    }

    [Fact]
    public void AFixedPickNeedsRealDifferentCardsAndItsOwnChoiceCount()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var cards = rt.Game.Players.Entries.Where(entry => entry.Value.Rating >= 85).Select(entry => entry.Key).Take(2).ToArray();
        Assert.Throws<InvalidOperationException>(() => PlayerPickDraw.Validate(rt.Game, Pack(cards[0])));
        Assert.Throws<InvalidOperationException>(() => PlayerPickDraw.Validate(rt.Game, Pack(cards[0], cards[0])));
        Assert.Throws<InvalidOperationException>(() => PlayerPickDraw.Validate(rt.Game, Pack(cards[0], 999_999_999)));
        var mismatched = Pack(cards);
        mismatched["player_picks"]![0]!["choice_count"] = 3;
        Assert.Throws<InvalidOperationException>(() => PlayerPickDraw.Validate(rt.Game, mismatched));
    }
}
