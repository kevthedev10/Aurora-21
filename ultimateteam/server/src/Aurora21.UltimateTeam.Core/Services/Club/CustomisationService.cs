using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;

namespace Aurora21.UltimateTeam.Core.Services.Club;

/// <summary>Club customisation: granting every locally verified definition (duplicate-safe) and the My Stadium venue.</summary>
public sealed class CustomisationService
{
    // FIFA 21 builds have sent the chosen venue under each of these objects and keys.
    private static readonly string[] VenueContainers = { "myStadium", "stadium" };
    private static readonly string[] VenueKeys = { "StadiumId", "stadiumId", "stadiumid", "selectedStadiumId", "assetId", "id" };

    private readonly FutServices _fut;
    private readonly ILogger _log;

    public CustomisationService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Http");
    }

    /// <summary>Persists the first known stadium named by a PUT/POST /stadium body; unknown ids are ignored.</summary>
    public async ValueTask SelectVenueAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        var game = _fut.Game;
        var sources = VenueContainers.Select(key => request.GetOr(key, new JsonObject())).Prepend(request).OfType<JsonObject>();
        var chosen = sources.SelectMany(source => VenueKeys.Select(key => source.Int(key))).FirstOrDefault(id => id != 0 && _fut.Club.IsKnownVenue(id));
        if (chosen != 0)
        {
            // The FUT Challengers and Champions Stadiums open with the Stadium Development rewards.
            var open = await game.RunAsync("select-stadium", async uow =>
            {
                if (!await _fut.Vanity.VenueOpenAsync(uow, chosen)) return false;
                await uow.SetSelectedStadiumIdAsync(chosen);
                return true;
            }, cancellationToken);
            if (open)
            {
                _log.LogInformation("My Stadium venue set to {Stadium} ({Name})", chosen, game.Stadiums.Find(chosen)?.Name ?? game.Rules.Club.Venues.FutStadiumName);
            }
        }
    }

    /// <summary>Relational venue and slot updates share one owning-club transaction.</summary>
    public async ValueTask SaveStadiumAsync(JsonObject request,CancellationToken cancellationToken=default)
    {
        var game=_fut.Game;
        var sources=VenueContainers.Select(key=>request.GetOr(key,new JsonObject())).Prepend(request).OfType<JsonObject>();
        var chosen=sources.SelectMany(source=>VenueKeys.Select(key=>source.Int(key))).FirstOrDefault(id=>id!=0&&_fut.Club.IsKnownVenue(id));
        var bindings=new JsonObject();
        foreach(var slot in (request.Arr("tiers")??new JsonArray()).OfType<JsonObject>().SelectMany(tier=>(tier.Arr("slots")??new JsonArray()).OfType<JsonObject>()))
            if(slot.Int("slotId")>0)bindings[slot.Int("slotId").ToString(System.Globalization.CultureInfo.InvariantCulture)]=slot.Get("itemId")?.DeepClone()??JsonValue.Create(0);
        // Vanity slots hold virtual items (VanityService); the rest bind owned items or the club's defaults.
        var vanity=bindings.Where(b=>_fut.Vanity.IsVanitySlot(long.Parse(b.Key,System.Globalization.CultureInfo.InvariantCulture))).Select(b=>(Slot:long.Parse(b.Key,System.Globalization.CultureInfo.InvariantCulture),Item:JsonValues.Int(b.Value))).ToList();
        foreach(var (slot,_) in vanity)bindings.Remove(slot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await game.RunAsync("save-stadium",async w=>{await w.LockClubAsync();if(chosen!=0&&await _fut.Vanity.VenueOpenAsync(w,chosen))await w.SetSelectedStadiumIdAsync(chosen);if(bindings.Count>0)await w.SetStadiumSlotBindingsAsync(bindings);foreach(var (slot,item) in vanity)
            {
                // One item that does not fit its slot (live 10-02: a pitch trophy saved into the TIFO slot) must not lose
                // the rest of the save. SetAsync validates before it writes, so a refused binding changes nothing.
                try{await _fut.Vanity.SetAsync(w,slot,item);}
                catch(Models.ClubRuleException error){_log.LogWarning("My Stadium skipped vanity slot {Slot} item {Item}: {Reason}",slot,item,error.Message);}
            }},cancellationToken);
    }

    /// <summary>Stores the <c>tiers[].slots[] {slotId, itemId}</c> bindings a My Stadium save sends.</summary>
    public async ValueTask SaveSlotBindingsAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        var bindings = new JsonObject();
        foreach (var slot in (request.Arr("tiers") ?? new JsonArray()).OfType<JsonObject>().SelectMany(tier => (tier.Arr("slots") ?? new JsonArray()).OfType<JsonObject>()))
        {
            if (slot.Int("slotId") > 0)
            {
                bindings[slot.Int("slotId").ToString(System.Globalization.CultureInfo.InvariantCulture)] = slot.Int("itemId");
            }
        }
        if (bindings.Count > 0)
        {
            await _fut.Game.RunAsync("save-stadium-slots", uow => uow.SetStadiumSlotBindingsAsync(bindings), cancellationToken);
            _log.LogInformation("My Stadium saved {Count} slot binding(s)", bindings.Count);
        }
    }

    /// <summary>
    /// Kits come from kits.json; stadiums are venue selections exposed through the stadium
    /// route. Only the bundled badge and ball are proven by native data, so the vanity
    /// tables that are absent are reported instead of inventing ids.
    /// </summary>
    public async ValueTask<JsonObject> GrantAllAsync(CancellationToken cancellationToken = default)
    {
        var game = _fut.Game;
        return await game.RunAsync("grant-customisation", async uow =>
        {
            await uow.LockClubAsync();
            var templates = new Dictionary<string, JsonObject>();
            foreach (var item in await _fut.Club.ActiveClubItemsAsync(uow))
            {
                templates[item.Text("itemState")] = item.Copy();
            }

            // One owned copy per (item type, definition), preferring the one already in the club.
            var owned = new Dictionary<(string Type, long Resource), JsonObject>();
            foreach (var item in await uow.ListItemsAsync())
            {
                var key = (item.Text("itemType"), item.Int("resourceId"));
                if (!owned.ContainsKey(key) || item.Text("pile") == "club")
                {
                    owned[key] = item;
                }
            }

            var granted = new List<JsonObject>();
            var newRelational = new List<JsonObject>();
            void Grant(JsonObject item)
            {
                var key = (item.Text("itemType"), item.Int("resourceId"));
                if (owned.TryGetValue(key, out var existing))
                {
                    if (existing.Text("pile") != "club")
                    {
                        var moved = existing.Copy();
                        moved["pile"] = "club";
                        owned[key] = moved;
                        granted.Add(moved);
                    }
                    return;
                }
                var created = item.Copy();
                created["id"] = 0;
                created["itemId"] = created["id"]!.DeepClone();
                created["pile"] = "club";
                newRelational.Add(created);
                owned[key] = created;
                granted.Add(created);
            }

            foreach (var kit in game.Kits.Kits)
            {
                var kitType = ItemFactory.KitType(kit);
                var item = ItemFactory.KitItem(0, game.NowSeconds, kit.IntKeep("resourceId"), "free", "club",
                    kit.Int("teamid"), kitType, 2 + Math.Max(0, kitType), kit.Text("name", "FIFA 21 Kit"));
                item.Remove("id");
                item.Remove("pile");
                Grant(item);
            }
            // The default stadium card stays virtual: its venue follows the club's selection, which an owned copy would not.
            foreach (var slot in game.Rules.Club.MyStadiumSlots.Where(slot => !slot.Locked && slot.ItemType is not ("kit" or "stadium")))
            {
                if (templates.TryGetValue(slot.ItemState, out var template))
                {
                    Grant(template);
                }
            }

            if(granted.Count>0) {
                var first=newRelational.Count==0 ? 0 : await uow.ReserveItemIdsAsync(newRelational.Count);
                for(var i=0;i<newRelational.Count;i++) { newRelational[i]["id"]=first+i;newRelational[i]["itemId"]=first+i; }
                await uow.SaveItemsAsync(granted);
            }
            long CountType(string type) => granted.Count(item => item.Text("itemType") == type);
            long HasTemplate(string state) => templates.ContainsKey(state) ? 1 : 0;
            return TemplateLibrary.Core.Object("club/customisation-granted", new TemplateArgs
            {
                ["grantedCount"] = granted.Count,
                ["kits"] = CountType("kit"),
                ["badges"] = CountType("custom"),
                ["balls"] = CountType("ball"),
                ["availableKits"] = game.Kits.Count,
                ["availableStadiums"] = game.Stadiums.Count,
                ["availableBadges"] = HasTemplate("activeBadge"),
                ["availableBalls"] = HasTemplate("activeBall"),
                ["unsupportedVanityCategories"] = new JsonArray(game.Rules.Club.UnsupportedVanityCategories.Select(name => (JsonNode?)name).ToArray()),
            });
        }, cancellationToken);
    }
}
