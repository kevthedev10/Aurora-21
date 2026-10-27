using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Catalog;

namespace Aurora21.UltimateTeam.Server.Panel;

public sealed partial class OperatorPanel
{
    public static ContentSchedule CreateContentSchedule(GameContext game, IEnumerable<IContentArea>? areas = null) => new(game, PanelContentAreas(game, areas ?? Core.Content.ContentAreas.Default));
    private static IEnumerable<IContentArea> PanelContentAreas(GameContext game, IEnumerable<IContentArea> areas) => areas.Concat<IContentArea>(
        [new PanelPackArea(game),new PanelLineupArea(),new PanelTokenLineupArea(),new PanelPackDeleteArea(game,false),new PanelPackDeleteArea(game,true),
         new SbcSetArea(game),new SbcLiveArea(game),new ScheduledAvailabilityArea(game,new PanelLineupArea(),new PanelTokenLineupArea())]);
    private ContentSchedule Schedule => options.Schedule ?? CreateContentSchedule(game, options.ContentAreas);
    private async Task<ContentChangesets> RequestChangesetsAsync(ApiRequest request)
    {
        var text = request.Query("dropId");
        if (string.IsNullOrEmpty(text)) return request.Query("apply") == "1" ? _changesets.ForImmediate() : _changesets;
        if (!long.TryParse(text, out var id) || id < 1) throw new PanelError(400, "dropId needs a positive id.");
        return Schedule.Draft(await Schedule.DraftIdAsync(id, request.Aborted));
    }
    private static DateTimeOffset? ScheduleDate(ApiRequest request, string field, bool required = false)
    {
        var text = TextField(request.Body, field);
        if (text is null && !required && (Field(request.Body, field) is null || Field(request.Body, field) is { ValueKind: System.Text.Json.JsonValueKind.Null })) return null;
        if (text is null || !System.Text.RegularExpressions.Regex.IsMatch(text, @"(Z|[+-]\d{2}:\d{2})$") || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new PanelError(400, $"{field} needs an ISO date with a UTC offset.");
        return date.ToUniversalTime();
    }
    private static object CampaignJson(ContentCampaign campaign) => new { id = campaign.Id, name = campaign.Name, startsAt = campaign.StartsAt?.ToString("O"), endsAt = campaign.EndsAt?.ToString("O"), version = campaign.Version };
    private async Task<object> DropJsonAsync(ContentDrop drop, CancellationToken token)
    {
        return await game.UnitOfWork.RunReadAsync(0, async work =>
        {
            var set = (await ContentChangesets.ReadAsync(work, drop.ChangesetId))!;
            var ends = await work.QueryAsync("SELECT e.change_id,e.ends_at FROM ultimateteam.content_drop_item_ends e JOIN ultimateteam.content_changes c USING(change_id) WHERE c.changeset_id=@id",
                r => (Id: r.GetInt64(0), End: r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1)), ("id", drop.ChangesetId));
            return (object)new
            {
                id = drop.Id,
                campaignId = drop.CampaignId,
                name = drop.Name,
                startsAt = drop.StartsAt.ToString("O"),
                endsAt = drop.EndsAt?.ToString("O"),
                inheritEnd = drop.InheritEnd,
                status = drop.Status,
                version = drop.Version,
                changesetId = drop.ChangesetId,
                error = drop.Error,
                publishedAt = drop.PublishedAt?.ToString("O"),
                changes = set.Changes.Count,
                items = set.Changes.Select(c => new
                {
                    id = c.Id,
                    area = c.Area,
                    key = c.Key,
                    operation = ContentChangesets.Text(c.Operation),
                    inheritEnd = !ends.Any(e => e.Id == c.Id),
                    endsAt = (ends.Any(e => e.Id == c.Id) ? ends.Single(e => e.Id == c.Id).End : drop.EndsAt)?.ToString("O")
                }).ToArray()
            };
        }, token);
    }
    private async Task<(int, object)?> ScheduleApiAsync(ApiRequest request)
    {
        var parts = request.Parts;
        if (parts.Length < 2 || parts[1] is not ("schedule" or "campaigns" or "drops")) return null;
        var token = request.Aborted;
        if (parts is [_, "schedule"] && request.Method == "GET")
        {
            var (campaigns, drops) = await game.UnitOfWork.RunReadAsync(0, async work => (await ContentSchedule.CampaignsAsync(work), await ContentSchedule.DropsAsync(work)), token);
            var rows = new List<object>(); foreach (var drop in drops) rows.Add(await DropJsonAsync(drop, token));
            var linked = await game.UnitOfWork.RunReadAsync(0, async work => await work.QueryAsync("SELECT c.area,c.target_key->>'code' FROM ultimateteam.content_changes c JOIN ultimateteam.content_drops d USING(changeset_id) WHERE c.operation='insert' AND c.area IN ('promo_event','icon_rotation')", r => (Area: r.GetString(0), Code: r.GetString(1))), token);
            var content = game.Live.Current;
            var legacy = content.Catalog.PromoEvents.Where(e => !linked.Contains(("promo_event", e.Code))).Select(e => (object)new { kind = "promo_event", code = e.Code, name = e.Name, startsAt = Iso(e.StartsAt), endsAt = Iso(e.EndsAt), enabled = e.Enabled, rotationType = content.Catalog.PromoEventPromos.Where(link => link.EventId == e.EventId && link.Enabled).Any(link => content.Catalog.Promos.Any(p => p.PromoId == link.PromoId && (p.OddsCategory == "totw" || p.Code == "silver_totw" || System.Text.RegularExpressions.Regex.IsMatch(p.Code, @"^totw_\d+$")))) ? "totw" : "promo" })
                .Concat(content.Catalog.IconRotations.Where(e => !linked.Contains(("icon_rotation", e.Code))).Select(e => (object)new { kind = "icon_rotation", code = e.Code, name = e.Name, startsAt = Iso(e.StartsAt), endsAt = Iso(e.EndsAt), enabled = e.Enabled }))
                .Concat(content.Rules.Objectives.Seasons.Select(e => (object)new { kind = "season", code = e.Id.ToString(), name = e.Name, startsAt = e.StartsAt.ToString("O"), endsAt = e.EndsAt.ToString("O"), enabled = e.Enabled }));
            return (200, new { now = game.Time.GetUtcNow().ToString("O"), campaigns = campaigns.Select(CampaignJson), drops = rows, legacy });
        }
        if (parts is [_, "schedule", "preview"] && request.Method == "GET")
        {
            var atText = request.Query("at") ?? throw new PanelError(400, "Choose a preview date.");
            if (!DateTimeOffset.TryParse(atText, out var at)) throw new PanelError(400, "Invalid preview date.");
            var preview = await Schedule.ProjectAsync(at, null, token);
            var active = preview.PromoAvailability.ActivePromoIds(at);
            return (200, new
            {
                at = at.ToString("O"),
                revision = preview.Revision,
                promos = preview.Catalog.Promos.Select(p => new { code = p.Code, name = p.Name, generallyAvailable = active.Contains(p.PromoId), cards = preview.Catalog.PromoCards.Count(c => c.PromoId == p.PromoId) }),
                storePackIds = preview.Rules.Packs.StorePacks.Where(p => p.Value is JsonObject obj && obj["enabled"]?.GetValue<bool>() != false && obj["store_listed"]?.GetValue<bool>() == true).Select(p => long.Parse(p.Key)),
                sbcKeys = preview.Sbc.Sets.Where(s => preview.Sbc.IsLive(s.Key)).Select(s => s.Key),
                objectiveGroupIds = preview.Rules.Objectives.LiveGroupIds,
                seasonId = preview.Rules.Objectives.SeasonAt(at.ToUnixTimeSeconds()).Id
            });
        }
        long? id = parts.Length >= 3 && long.TryParse(parts[2], out var parsed) ? parsed : null;
        var version = WholeField(request.Body, "version") ?? 0;
        if (parts[1] == "campaigns" && (parts.Length == 2 && request.Method == "POST" || parts.Length == 3 && id is not null && request.Method == "PUT"))
        {
            var result = await Schedule.SaveCampaignAsync(id, TextField(request.Body, "name") ?? "", ScheduleDate(request, "endsAt"), version, token, ScheduleDate(request, "startsAt"));
            return (200, CampaignJson(result));
        }
        if (parts is [_, "campaigns", _] && id is { } campaignIdToDelete && request.Method == "DELETE")
        {
            await Schedule.DeleteCampaignAsync(campaignIdToDelete, version, token);
            return (200, new { deleted = campaignIdToDelete });
        }
        if (parts[1] == "drops" && (parts.Length == 2 && request.Method == "POST" || parts.Length == 3 && id is not null && request.Method == "PUT"))
        {
            var campaignId = Field(request.Body, "campaignId") is { ValueKind: System.Text.Json.JsonValueKind.Null } ? null : WholeField(request.Body, "campaignId");
            var result = await Schedule.SaveDropAsync(id, campaignId, TextField(request.Body, "name") ?? "", ScheduleDate(request, "startsAt", true)!.Value,
                ScheduleDate(request, "endsAt"), FlagField(request.Body, "inheritEnd") ?? true, version, token);
            return (200, await DropJsonAsync(result, token));
        }
        if (parts[1] == "drops" && id is { } dropId && parts.Length == 4 && request.Method == "POST")
        {
            switch (parts[3])
            {
                case "validate": return (200, ValidationView(await Schedule.ValidateAsync(dropId, version, token)));
                case "schedule": return (200, await DropJsonAsync(await Schedule.ScheduleAsync(dropId, version, token), token));
                case "end": return (200, await DropJsonAsync(await Schedule.EndAsync(dropId, version, token), token));
                case "cancel": return (200, await DropJsonAsync(await Schedule.CancelAsync(dropId, version, token), token));
                case "publish":
                    await Schedule.RunOnceAsync(token, dropId, version);
                    return (200, await DropJsonAsync(await Schedule.GetDropAsync(dropId, token), token));
            }
        }
        if (parts is [_, "drops", _, "items", var item, "end"] && id is { } owner && long.TryParse(item, out var changeId) && request.Method == "PUT")
            return (200, await DropJsonAsync(await Schedule.SetItemEndAsync(owner, changeId, version, ScheduleDate(request, "endsAt"), token, FlagField(request.Body, "inheritEnd") ?? false), token));
        throw new PanelError(405, "method not allowed");
    }
}
