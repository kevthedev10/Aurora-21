using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class PotmOneImportTests
{
    [Fact]
    public async Task SetIsDisabledExactCardsAreGrantedAndLeagueAndTotwRulesAreEnforced()
    {
        using var rt = TestRuntime.Create();
        var set = rt.Game.Sbc.FindSet("POTM_1")!;
        Assert.NotNull(set);
        Assert.False(rt.Game.Sbc.IsLive(set.Key));
        Assert.False(set.Source.Bool("repeatable"));
        Assert.Null(new SbcSetArea(rt.Game).Problem(set.Key,set.Source));
        Assert.Equal([50553127L,50548002L,67347889L],set.Challenges.Select(c=>rt.Fut.Sbc.PlayerResourceId(c.Source["rewards"]!["entries"]![0]!.AsObject())));
        Assert.Equal(84139084,rt.Fut.Sbc.PlayerResourceId(set.Source["rewards"]!["entries"]![0]!.AsObject()));
        Assert.Equal([70L,75L,60L],set.Challenges.Select(c=>SbcText.Requirements(rt.Game.Rules.Sbc,c.Source).Single(r=>r.Key==SbcEligibilityKey.Chemistry).Value));
        foreach(var challenge in set.Challenges)
        {
            var paid=await rt.Fut.Sbc.GrantChallengeRewardsAsync(challenge);
            Assert.Single(paid);
            Assert.True(paid[0].Bool("untradeable"));
            Assert.Equal("unassigned",paid[0].Text("pile"));
            var award=rt.Fut.Sbc.AwardJson(challenge.Source["rewards"]!["entries"]![0]!.AsObject(),set.Key)!;
            Assert.Equal(paid[0].Int("resourceId"),award.Int("halId"));
            Assert.Equal(paid[0].Int("rating"),award.Int("rating"));
            Assert.Equal(paid[0].Text("preferredPosition"),award.Text("position"));
        }
        // Isolated service fixture only: make it available in this test and ignore chemistry to test league/TOTW independently.
        // The stored set remains off; its original rating and chemistry requirements are asserted above.
        rt.Game.Sbc.LiveKeys.Add(set.Key);
        rt.Game.Rules.Sbc.IgnoredRequirements.Add("CHEMISTRY");
        var first=set.Challenges[0];
        var wanted=13L;
        var used=new HashSet<long>();
        List<JsonObject> Squad(bool league,bool totw)
        {
            used.Clear();
            var cards=new List<JsonObject>();
            void Add(Func<global::Aurora21.UltimateTeam.Core.Catalog.PlayerMeta,bool> predicate)
            {
                var picked=rt.Game.Players.Entries.First(e=>e.Value.Rating>=87&&!rt.Game.Cards.IsGrantOnly(e.Key)
                    &&!used.Contains(e.Value.Int("assetId"))&&predicate(e.Value));
                used.Add(picked.Value.Int("assetId"));cards.Add(rt.Fut.Items.MakePlayer(picked.Key,"club"));
            }
            Add(m=>m.Int("leagueId")!=wanted&&m.Rareflag==(totw?3:1));
            if(league) Add(m=>m.Int("leagueId")==wanted&&m.Rareflag==1);
            while(cards.Count<11) Add(m=>m.Int("leagueId")!=wanted&&m.Rareflag==1);
            return cards;
        }
        async Task<JsonObject> Submit(bool league,bool totw)
        {
            var cards=Squad(league,totw);
            var body=new JsonObject { ["formation"]="f442", ["players"]=new JsonArray(cards.Select((card,i)=>(JsonNode)new JsonObject { ["index"]=i,["itemData"]=new JsonObject { ["id"]=card.Int("id") } }).ToArray()) };
            await rt.Fut.Sbc.SaveSquadAsync(first.Id,body);
            return (await rt.Fut.Sbc.SubmitAsync(first.Id)).Body!.AsObject();
        }
        Assert.Equal("INVALID",(await Submit(false,true)).Text("status"));
        Assert.Equal("INVALID",(await Submit(true,false)).Text("status"));
        Assert.NotEqual("INVALID",(await Submit(true,true)).Text("status"));
    }

    [Fact]
    public void MigrationReplayPreservesOperatorEditsAndLiveChoice()
    {
        using var rt=TestRuntime.Create();
        rt.Game.InUnit(async work=>
        {
            await work.ExecuteAsync("""
                SELECT set_config('ultimateteam.content_publish','on',true);
                UPDATE ultimateteam.sbc_sets SET definition=jsonb_set(definition,'{description}','"Operator edit"') WHERE set_key='POTM_1';
                UPDATE ultimateteam.sbc_live SET enabled=true WHERE set_key='POTM_1';
                DELETE FROM ultimateteam.schema_migrations WHERE version=155;
                """);
            using var stream=typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/migrations/155-potm-one-sbc.sql")!;
            using var reader=new StreamReader(stream);
            await work.ExecuteAsync(await reader.ReadToEndAsync());
            Assert.Equal("Operator edit",await work.ScalarAsync<string>("SELECT definition->>'description' FROM ultimateteam.sbc_sets WHERE set_key='POTM_1'"));
            Assert.True(await work.ScalarAsync<bool>("SELECT enabled FROM ultimateteam.sbc_live WHERE set_key='POTM_1'"));
            return 0;
        });
    }
}
