using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

public sealed partial class SbcService
{
    private static long SbcItemId(JsonObject slot) => JsonValues.Int(slot.Obj("itemData")?.Get("id") ?? slot.Get("itemId") ?? slot.Get("id"));

    private async ValueTask<List<JsonObject>> LoadSbcPlayersAsync(IUnitOfWork work, IReadOnlyList<long> ids)
    {
        if(ids.Count>Rules.SquadSize||ids.Distinct().Count()!=ids.Count) throw new ClubRuleException("SBC_INVALID_SQUAD");
        var players=new List<JsonObject>();var footballers=new HashSet<long>();
        foreach(var id in ids)
        {
            var (item,problem)=await SbcPlayerAsync(work,id,footballers);
            if(problem is not null) throw new ClubRuleException(problem);
            players.Add(item!);
        }
        return players;
    }

    /// <summary>
    /// One saved squad player and why it cannot go into an SBC (null when it can). The footballer is added to
    /// <paramref name="footballers"/> once accepted, so a second copy of the same footballer is refused.
    /// </summary>
    private async ValueTask<(JsonObject? Item,string? Problem)> SbcPlayerAsync(IUnitOfWork work,long id,HashSet<long> footballers)
    {
        var item=await work.GetItemAsync(id);
        if(item is null) return (null,"SBC_PLAYER_NOT_OWNED");
        if(item.Text("itemType")!="player"||item.Bool("dream")||item.Int("loans")>0||item.Bool("expiredLoan")) return (item,"SBC_INELIGIBLE_PLAYER");
        if(item.Text("pile") is not ("club" or "squad")) return (item,"SBC_PLAYER_NOT_IN_CLUB");
        if(await work.LiveMarketListingForItemAsync(id) is not null) return (item,"SBC_PLAYER_MARKET_LISTED");
        var meta=Game.Players.Find(item.Int("resourceId"));
        if(meta is null) return (item,"SBC_UNKNOWN_CARD_DEFINITION");
        if(!footballers.Add(meta.Int("assetId"))) return (item,"SBC_DUPLICATE_FOOTBALLER");
        return (item,null);
    }

    /// <summary>The saved manager when the club still owns it; FIFA's manager bonus counts only for an owned manager.</summary>
    private async ValueTask<JsonObject?> SbcManagerAsync(IUnitOfWork work,long? managerItemId)
    {
        if(managerItemId is not { } id) return null;
        var item=await work.GetItemAsync(id);
        return item is not null&&item.Text("itemType")=="manager"&&item.Text("pile") is "club" or "squad" ? _fut.Native.ManagerItem(item) : null;
    }

    /// <summary>
    /// The squad's chemistry as FIFA computes it (ChemistryModel), from the saved slots, the challenge's locked-slot cards
    /// exactly as EmptySquad serves them (BrickItem) and the saved manager. The chemistry FIFA itself sends with the save
    /// is never read.
    /// </summary>
    private int SbcChemistry(SbcChallenge challenge,IReadOnlyList<JsonObject> players,SbcSavedSquad saved,JsonObject? manager)
    {
        var bricks=Bricks(challenge);
        var formation=SavedFormation(challenge,saved.Formation??string.Empty);
        var starters=new JsonObject?[Rules.SquadSize];
        for(var i=0;i<saved.Slots.Count&&i<players.Count;i++) starters[saved.Slots[i].Index]=_fut.Native.PlayerItem(players[i]);
        if(bricks is not null)
            for(var index=0;index<Rules.SquadSize;index++)
                if(BrickItem(bricks,formation,index) is { } brick) starters[index]=brick;
        return Game.Chemistry.SquadChemistry(formation,starters,manager)??0;
    }

    private void ValidateSbcRequirements(SbcChallenge challenge,IReadOnlyList<JsonObject> players,SbcSavedSquad saved,JsonObject? manager)
    {
        var need=RequiredPlayers(challenge);
        if(players.Count!=need) throw new ClubRuleException("SBC_WRONG_PLAYER_COUNT: "+RuleText.Format(Rules.Text.ShortSquad,("need",need),("placed",players.Count)));
        // Slots are re-checked here too: the challenge's locked slots can change after a squad was saved.
        var bricks=Bricks(challenge);
        if(saved.Slots.Any(slot=>slot.Index<0||slot.Index>=Rules.SquadSize||bricks?.IsBrick(slot.Index)==true)) throw new ClubRuleException("SBC_LOCKED_SLOT");
        var metadata=players.Select(p=>Game.Players.Find(p.Int("resourceId"))!).ToArray();
        foreach(var authored in RequirementRules(challenge.Source))
        {
            var type=authored.Text("type").ToUpperInvariant();
            if(type!="PLAYERS"&&!Rules.Requirements.ContainsKey(type)) throw new ClubRuleException("SBC_UNSUPPORTED_REQUIREMENT");
        }
        foreach(var requirement in Requirements(challenge))
        {
            if(requirement.Key is null) continue;
            long actual;long expected=requirement.Value;
            switch(requirement.Key)
            {
                case SbcEligibilityKey.NationId:
                    actual=metadata.Count(m=>m.Int("nation")==requirement.Value);expected=requirement.PlayerCount;break;
                case SbcEligibilityKey.ClubId:
                    actual=metadata.Count(m=>m.Int("teamid")==requirement.Value);expected=requirement.PlayerCount;break;
                case SbcEligibilityKey.LeagueId:
                    actual=metadata.Count(m=>m.Int("leagueId")==requirement.Value);expected=requirement.PlayerCount;break;
                case SbcEligibilityKey.RarityGroup:
                    actual=metadata.Count(m=>Game.Cards.RarityGroups(m.Rareflag).Any(n=>JsonValues.Int(n)==requirement.Value));expected=requirement.PlayerCount;break;
                case SbcEligibilityKey.Rarity:
                    actual=metadata.Count(m=>m.Rareflag==requirement.Value);expected=requirement.PlayerCount;break;
                case SbcEligibilityKey.Quality:
                    var quality=Rules.Requirements["QUALITY"].Qualities;
                    actual=metadata.Count(m=>quality.GetValueOrDefault(m.Str("tier"))==requirement.Value);expected=requirement.PlayerCount;break;
                case SbcEligibilityKey.Chemistry: actual=SbcChemistry(challenge,players,saved,manager);break;
                case SbcEligibilityKey.TeamRating:
                    actual=SbcSquadRating.CalculateSbcSquadRating(metadata.Select(m=>checked((int)m.Rating)));break;
                case SbcEligibilityKey.ClubCount: actual=metadata.Select(m=>m.Int("teamid")).Distinct().Count();break;
                case SbcEligibilityKey.NationCount: actual=metadata.Select(m=>m.Int("nation")).Distinct().Count();break;
                case SbcEligibilityKey.LeagueCount: actual=metadata.Select(m=>m.Int("leagueId")).Distinct().Count();break;
                case SbcEligibilityKey.SameNation: actual=metadata.GroupBy(m=>m.Int("nation")).Max(g=>g.Count());break;
                case SbcEligibilityKey.SameLeague: actual=metadata.GroupBy(m=>m.Int("leagueId")).Max(g=>g.Count());break;
                case SbcEligibilityKey.SameClub: actual=metadata.GroupBy(m=>m.Int("teamid")).Max(g=>g.Count());break;
                default: throw new ClubRuleException("SBC_UNSUPPORTED_REQUIREMENT");
            }
            var valid=requirement.Scope switch {SbcScope.AtLeast=>actual>=expected,SbcScope.AtMost=>actual<=expected,SbcScope.Exact=>actual==expected,_=>false};
            if(!valid) throw new ClubRuleException("SBC_REQUIREMENT_NOT_MET: "+requirement.Label);
        }
    }

    private async ValueTask ConsumeSbcPlayersAsync(IUnitOfWork work,long[] ids)
    {
        var submitted=ids.ToHashSet();
        foreach(var squad in await work.ListSquadsAsync())
        {
            var changed=false;
            foreach(var slot in (squad.Arr("players")??new JsonArray()).OfType<JsonObject>())
                if(submitted.Contains(SbcItemId(slot))) {slot.Remove("itemId");slot.Remove("itemData");slot.Remove("id");changed=true;}
            if(!changed) continue;
            if(submitted.Contains(squad.Int("captain"))) squad.Remove("captain");
            if(squad.Arr("kicktakers") is { } kicks)
                for(var index=kicks.Count-1;index>=0;index--) if(kicks[index] is JsonObject kick&&submitted.Contains(kick.Int("id"))) kicks.RemoveAt(index);
            var squadId=squad.Int("id",squad.Int("squadId"));
            await work.UpdateSquadDataAsync(squadId,await work.SquadActiveAsync(squadId)??false,squad);
        }
        foreach(var id in ids) if(!await work.DeleteItemAsync(id)) throw new ClubRuleException("SBC_PLAYER_NOT_OWNED");
    }
}
