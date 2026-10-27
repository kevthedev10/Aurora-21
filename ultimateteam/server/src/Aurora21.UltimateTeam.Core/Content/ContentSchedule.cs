using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Content;

public sealed record ContentCampaign(long Id, string Name, DateTimeOffset? EndsAt, long Version, DateTimeOffset? StartsAt = null);
public sealed record ContentDrop(long Id, long? CampaignId, string Name, DateTimeOffset StartsAt, DateTimeOffset? EndsAt,
    bool InheritEnd, string Status, long Version, long ChangesetId, string? Error, DateTimeOffset? PublishedAt, JsonArray Expires);

/// <summary>Durable scheduled publication, shared by panel requests and the server-owned worker.</summary>
public sealed class ContentSchedule(GameContext game, IEnumerable<IContentArea> areas)
{
    private readonly IContentArea[] _areas = areas.ToArray();
    private ILogger Log => game.Logger("UltimateTeam.ContentSchedule");
    public ContentChangesets Draft(long changesetId) => new(game, _areas, changesetId);
    public static async Task<List<ContentCampaign>> CampaignsAsync(IUnitOfWork work) => await work.QueryAsync(
        "SELECT campaign_id,name,ends_at,version,starts_at FROM ultimateteam.content_campaigns ORDER BY campaign_id",
        r => new ContentCampaign(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4)));
    public static async Task<List<ContentDrop>> DropsAsync(IUnitOfWork work) => await work.QueryAsync("""
        SELECT drop_id,campaign_id,name,starts_at,ends_at,inherit_end,status,version,changeset_id,error,published_at,expires::text
        FROM ultimateteam.content_drops ORDER BY starts_at,drop_id
        """, r => new ContentDrop(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
            r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4), r.GetBoolean(5), r.GetString(6), r.GetInt64(7), r.GetInt64(8),
            r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), JsonNode.Parse(r.GetString(11))!.AsArray()));
    public Task<ContentDrop> GetDropAsync(long id, CancellationToken token = default) => game.UnitOfWork.RunReadAsync(0,
        async work => (await DropsAsync(work)).SingleOrDefault(d => d.Id == id) ?? throw new ContentChangeException(404, "No such drop."), token);
    public async Task<long> DraftIdAsync(long dropId, CancellationToken token = default) => (await GetDropAsync(dropId, token)).ChangesetId;
    private static void Name(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length > 120) throw new ContentChangeException(400, "Name needs 1–120 characters."); }
    private static void Version(long actual, long expected) { if (actual != expected) throw new ContentChangeException(409, "This content changed. Refresh before editing."); }
    public Task<ContentCampaign> SaveCampaignAsync(long? id, string name, DateTimeOffset? endsAt, long version, CancellationToken token = default, DateTimeOffset? startsAt = null)
    {
        Name(name);
        return game.UnitOfWork.RunAsync(0, "content-campaign", async work =>
        {
            await ContentChangesets.LockAsync(work);
            if (startsAt is { } start && (endsAt is null || endsAt <= start)) throw new ContentChangeException(400, "Campaign end must follow its start.");
            if (id is null)
            {
                var created = await work.ScalarAsync<long>("INSERT INTO ultimateteam.content_campaigns(name,starts_at,ends_at) VALUES(@name,@start,@end) RETURNING campaign_id", ("name", name), ("start", startsAt), ("end", endsAt));
                return new ContentCampaign(created, name, endsAt, 1, startsAt);
            }
            var old = (await CampaignsAsync(work)).SingleOrDefault(c => c.Id == id) ?? throw new ContentChangeException(404, "No such campaign.");
            Version(old.Version, version);
            startsAt ??= old.StartsAt;
            var allChildren = (await DropsAsync(work)).Where(d => d.CampaignId == id && d.Status != "cancelled").ToArray();
            if (startsAt is { } campaignStart)
            {
                if (endsAt is null || endsAt <= campaignStart) throw new ContentChangeException(400, "Campaign end must follow its start.");
                if (allChildren.Any(d => d.StartsAt < campaignStart || d.StartsAt >= endsAt || (d.InheritEnd ? endsAt : d.EndsAt) is null || (d.InheritEnd ? endsAt : d.EndsAt) > endsAt))
                    throw new ContentChangeException(400, "Campaign dates must contain every existing drop. Adjust those drops first.");
                if (old.StartsAt is null)
                    foreach (var child in allChildren)
                        await ValidateCampaignTimingAsync(work, (await ContentChangesets.ReadAsync(work, child.ChangesetId))!);
            }
            var children = (await DropsAsync(work)).Where(d => d.CampaignId == id && d.InheritEnd).ToArray();
            if (old.EndsAt != endsAt && children.Any(d => d.Status is "live" or "ended")) throw new ContentChangeException(409, "Change active content expiry on its drop rather than the campaign default.");
            if (endsAt is { } end && children.Any(d => d.StartsAt >= end && d.Status is not ("cancelled" or "ended"))) throw new ContentChangeException(400, "Campaign end must follow its drops.");
            await work.ExecuteAsync("UPDATE ultimateteam.content_campaigns SET name=@name,starts_at=@start,ends_at=@end,version=version+1 WHERE campaign_id=@id", ("name", name), ("start", startsAt), ("end", endsAt), ("id", id));
            if (old.EndsAt != endsAt)
            {
                await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET ends_at=@end,status='draft',error=NULL,version=version+1 WHERE campaign_id=@id AND inherit_end AND status IN ('draft','scheduled','failed')", ("end", endsAt), ("id", id));
                foreach (var child in children.Where(d => d.Status is "draft" or "scheduled" or "failed"))
                    await ContentChangesets.InvalidateDependentsAsync(work, child.ChangesetId);
            }
            return new ContentCampaign(id.Value, name, endsAt, version + 1, startsAt);
        }, token);
    }
    public Task DeleteCampaignAsync(long id, long version, CancellationToken token = default) => game.UnitOfWork.RunAsync(0, "content-campaign-delete", async work =>
    {
        await ContentChangesets.LockAsync(work);
        var campaign = (await CampaignsAsync(work)).SingleOrDefault(c => c.Id == id) ?? throw new ContentChangeException(404, "No such campaign.");
        Version(campaign.Version, version);
        var children = (await DropsAsync(work)).Where(d => d.CampaignId == id).ToArray();
        var now = game.Time.GetUtcNow();
        if (children.Any(d => d.Status == "live") || campaign.StartsAt <= now && now < campaign.EndsAt)
            throw new ContentChangeException(409, "Live campaigns cannot be deleted.");
        foreach (var child in children.Where(d => d.PublishedAt is null && d.Status != "ended"))
        {
            await ContentChangesets.InvalidateDependentsAsync(work, child.ChangesetId);
            await work.ExecuteAsync("UPDATE ultimateteam.content_changesets SET status='discarded' WHERE changeset_id=@id AND status IN ('draft','open')", ("id", child.ChangesetId));
            await work.ExecuteAsync("DELETE FROM ultimateteam.content_drops WHERE drop_id=@id", ("id", child.Id));
        }
        // Completed releases keep their publication/expiry history and no longer belong to a campaign.
        await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET campaign_id=NULL,inherit_end=false,version=version+1 WHERE campaign_id=@id", ("id", id));
        await work.ExecuteAsync("DELETE FROM ultimateteam.content_campaigns WHERE campaign_id=@id", ("id", id));
        return true;
    }, token);

    public Task<ContentDrop> SaveDropAsync(long? id, long? campaignId, string name, DateTimeOffset startsAt, DateTimeOffset? endsAt, bool inheritEnd, long version, CancellationToken token = default)
    {
        Name(name);
        return game.UnitOfWork.RunAsync(0, "content-drop", async work =>
        {
            await ContentChangesets.LockAsync(work);
            var campaign = campaignId is null ? null : (await CampaignsAsync(work)).SingleOrDefault(c => c.Id == campaignId) ?? throw new ContentChangeException(404, "No such campaign.");
            if (inheritEnd) endsAt = campaign?.EndsAt;
            if (campaign?.StartsAt is { } campaignStart && (startsAt < campaignStart || endsAt is null || endsAt > campaign.EndsAt))
                throw new ContentChangeException(400, "Drop dates must stay within its campaign.");
            if (endsAt is { } end && end <= startsAt) throw new ContentChangeException(400, "End must follow release time.");
            long dropId;
            if (id is null)
            {
                var setId = await work.ScalarAsync<long>("INSERT INTO ultimateteam.content_changesets(status,note) VALUES('draft',@name) RETURNING changeset_id", ("name", name));
                dropId = await work.ScalarAsync<long>("""
                    INSERT INTO ultimateteam.content_drops(campaign_id,name,starts_at,ends_at,inherit_end,changeset_id)
                    VALUES(@campaign,@name,@start,@end,@inherit,@set) RETURNING drop_id
                    """, ("campaign", campaignId), ("name", name), ("start", startsAt), ("end", endsAt), ("inherit", inheritEnd), ("set", setId));
            }
            else
            {
                var old = (await DropsAsync(work)).SingleOrDefault(d => d.Id == id) ?? throw new ContentChangeException(404, "No such drop.");
                Version(old.Version, version);
                if (old.Status is not ("draft" or "scheduled" or "failed")) throw new ContentChangeException(409, "Only unpublished drops can be edited.");
                if (campaign?.StartsAt is not null)
                    await ValidateCampaignTimingAsync(work, (await ContentChangesets.ReadAsync(work, old.ChangesetId))!);
                await ContentChangesets.InvalidateDependentsAsync(work, old.ChangesetId);
                await work.ExecuteAsync("""
                    UPDATE ultimateteam.content_drops SET campaign_id=@campaign,name=@name,starts_at=@start,ends_at=@end,
                    inherit_end=@inherit,status='draft',error=NULL,version=version+1 WHERE drop_id=@id
                    """, ("campaign", campaignId), ("name", name), ("start", startsAt), ("end", endsAt), ("inherit", inheritEnd), ("id", id));
                dropId = id.Value;
                await ContentChangesets.InvalidateDependentsAsync(work, old.ChangesetId);
            }
            return (await DropsAsync(work)).Single(d => d.Id == dropId);
        }, token);
    }

    public Task<ContentDrop> SetItemEndAsync(long id, long changeId, long version, DateTimeOffset? end, CancellationToken token = default, bool inheritEnd = false) =>
        game.UnitOfWork.RunAsync(0, "content-drop-item-end", async work =>
        {
            await ContentChangesets.LockAsync(work);
            var drop = (await DropsAsync(work)).SingleOrDefault(d => d.Id == id) ?? throw new ContentChangeException(404, "No such drop.");
            Version(drop.Version, version);
            if (drop.Status is not ("draft" or "scheduled" or "failed")) throw new ContentChangeException(409, "Edit an unpublished drop.");
            if (end is { } instant && instant <= drop.StartsAt) throw new ContentChangeException(400, "Item end must follow release time.");
            if (!await work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.content_changes WHERE change_id=@change AND changeset_id=@set)", ("change", changeId), ("set", drop.ChangesetId))) throw new ContentChangeException(404, "No such drop item.");
            if (inheritEnd) await work.ExecuteAsync("DELETE FROM ultimateteam.content_drop_item_ends WHERE change_id=@id", ("id", changeId));
            else await work.ExecuteAsync("INSERT INTO ultimateteam.content_drop_item_ends(change_id,ends_at) VALUES(@id,@end) ON CONFLICT(change_id) DO UPDATE SET ends_at=excluded.ends_at", ("id", changeId), ("end", end));
            await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET status='draft',version=version+1,error=NULL WHERE drop_id=@id", ("id", id));
            await ContentChangesets.InvalidateDependentsAsync(work, drop.ChangesetId);
            return (await DropsAsync(work)).Single(d => d.Id == id);
        }, token);

    public Task<ContentDrop> CancelAsync(long id, long version, CancellationToken token = default) => game.UnitOfWork.RunAsync(0, "content-drop-cancel", async work =>
    {
        await ContentChangesets.LockAsync(work);
        var drops = await DropsAsync(work); var drop = drops.SingleOrDefault(d => d.Id == id) ?? throw new ContentChangeException(404, "No such drop.");
        Version(drop.Version, version);
        if (drop.Status is "ended" or "cancelled") throw new ContentChangeException(409, "This drop has already ended.");
        if (drop.Status == "live") throw new ContentChangeException(409, "Use End now to remove active availability.");
        await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET status='cancelled',version=version+1 WHERE drop_id=@id", ("id", id));
        await ContentChangesets.InvalidateDependentsAsync(work, drop.ChangesetId);
        return (await DropsAsync(work)).Single(d => d.Id == id);
    }, token);

    public async Task<ContentValidation> ValidateAsync(long id, long version, CancellationToken token = default)
    {
        var drop = await GetDropAsync(id, token); Version(drop.Version, version);
        try
        {
            await game.UnitOfWork.RunReadAsync(0, async work =>
            {
                await ValidateDropTimingAsync(work, drop, (await ContentChangesets.ReadAsync(work, drop.ChangesetId))!);
                return 0;
            }, token);
            _ = await ProjectAsync(drop.StartsAt, id, token);
        }
        catch (ContentChangeException e) { return new(false, 0, e.Conflicts, [e.Message]); }
        catch (Exception e) when (e is InvalidDataException or Npgsql.PostgresException) { return new(false, 0, [], [e.Message]); }
        return new(true, (await game.UnitOfWork.RunReadAsync(0, w => ContentChangesets.ReadAsync(w, drop.ChangesetId), token))!.Changes.Count, [], []);
    }

    public async Task<ContentDrop> ScheduleAsync(long id, long version, CancellationToken token = default)
    {
        var initial = await GetDropAsync(id, token); Version(initial.Version, version);
        if (initial.Status is not ("draft" or "failed")) throw new ContentChangeException(409, "Select a draft drop.");
        // Validate once under the content lock; the projection rolls back to its savepoint.
        return await game.UnitOfWork.RunAsync(0, "content-drop-schedule", async work =>
        {
            await ContentChangesets.LockAsync(work);
            var drop = (await DropsAsync(work)).Single(d => d.Id == id); Version(drop.Version, version);
            if (drop.Status is not ("draft" or "failed")) throw new ContentChangeException(409, "Select a draft drop.");
            if (drop.StartsAt <= game.Time.GetUtcNow()) throw new ContentChangeException(400, "Choose a future release time or Publish now.");
            var changes = (await ContentChangesets.ReadAsync(work, drop.ChangesetId))!;
            if (changes.Changes.Count == 0) throw new ContentChangeException(400, "Add content before scheduling.");
            CheckAllowed(changes);
            await ValidateDropTimingAsync(work, drop, changes);
            try { await work.SavepointAsync(async () => await ProjectWithinAsync(work, drop.StartsAt, drop.Id, token)); }
            catch (Projection) { }
            catch (ContentChangeException error) { throw new ContentChangeException(422, error.Message, error.Conflicts); }
            catch (Exception error) when (error is InvalidDataException or Npgsql.PostgresException) { throw new ContentChangeException(422, error.Message); }
            var expires = await ExpiriesAsync(work, drop, changes);
            await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET status='scheduled',version=version+1,error=NULL,expires=@expires::jsonb WHERE drop_id=@id", ("id", id), ("expires", expires.ToJsonString()));
            return (await DropsAsync(work)).Single(d => d.Id == id);
        }, token);
    }
    public static bool IsPlayerContent(string area, JsonObject key) => area is "promo" or "promo_event" or "icon_rotation" or "player_card" or "store_pack" or "store_lineup" or "token_store_lineup" or "sbc_set" or "sbc_live" or "content_window"
        || area == "rules" && key["file"]?.ToString() == "objectives";
    private static void CheckAllowed(ContentChangeset changes)
    {
        foreach (var c in changes.Changes)
            if (!IsPlayerContent(c.Area, c.Key)) throw new ContentChangeException(400, $"{c.Area} is not player content and cannot be scheduled.");
    }

    private static async Task ValidateDropTimingAsync(IUnitOfWork work, ContentDrop drop, ContentChangeset changes)
    {
        if ((await CampaignsAsync(work)).Any(c => c.Id == drop.CampaignId && c.StartsAt is not null))
            await ValidateCampaignTimingAsync(work, changes);
    }

    private static async Task ValidateCampaignTimingAsync(IUnitOfWork work, ContentChangeset changes)
    {
        if (changes.Changes.Any(c => c.Area == "icon_rotation"))
            throw new ContentChangeException(400, "Icon rotations are managed independently of campaigns.");
        var events = changes.Changes.Where(c => c.Area == "promo_event").ToArray();
        if (events.Length == 0) return;
        var promos = await PromoEdits.PromosAsync(work, pending: false);
        var totw = promos.Where(p => PromoEdits.IsTotw(p.Key, p.Value.Category)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var promo in changes.Changes.Where(c => c.Area == "promo" && PromoEdits.IsTotw(c.Key["code"]!.ToString(), c.After?["odds_category"]?.ToString())))
            totw.Add(promo.Key["code"]!.ToString());
        foreach (var change in events)
        {
            var current = await new PromoTimingArea(false).ReadAsync(work, change.Key);
            foreach (var code in new[] { current, change.After }.Where(row => row is not null).SelectMany(row => (row!["promos"] as JsonArray)?.Select(n => n?.ToString() ?? "") ?? []))
                if (PromoEdits.IsTotw(code) || totw.Contains(code))
                    throw new ContentChangeException(400, "TOTW events are managed independently of campaigns.");
        }
    }

    private async Task<JsonArray> ExpiriesAsync(IUnitOfWork work, ContentDrop drop, ContentChangeset changes)
    {
        var overrides = await work.QueryAsync("SELECT e.change_id,e.ends_at FROM ultimateteam.content_drop_item_ends e JOIN ultimateteam.content_changes c USING(change_id) WHERE c.changeset_id=@id", r => (Id: r.GetInt64(0), End: r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1)), ("id", drop.ChangesetId));
        var result = new JsonArray();
        foreach (var c in changes.Changes)
        {
            var itemOverride = overrides.Where(o => o.Id == c.Id).ToArray();
            var end = itemOverride.Length > 0 ? itemOverride[0].End : drop.EndsAt;
            void Add(string kind, string target) => result.Add(new JsonObject { ["kind"] = kind, ["target"] = target, ["endsAt"] = end?.ToString("O"), ["done"] = false });
            if (c.Area is "promo_event" or "icon_rotation" && c.After?["enabled"]?.GetValue<bool>() == true)
            {
                if (itemOverride.Length == 0 && !c.Key["code"]!.ToString().StartsWith($"drop_{drop.Id}_", StringComparison.Ordinal)) end = DateTimeOffset.Parse(c.After["ends_at"]!.ToString());
                Add(c.Area, c.Key["code"]!.ToString());
            }
            else if (c.Area == "rules" && c.Key["file"]?.ToString() == "objectives" && c.Key["path"]?.ToJsonString() == "[\"seasons\"]")
            {
                var previous = (c.Before?["value"] as JsonArray)?.OfType<JsonObject>().ToDictionary(r => r["id"]!.ToString()) ?? [];
                foreach (var season in (c.After?["value"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    if (season["enabled"]?.GetValue<bool>() != false && (!previous.TryGetValue(season["id"]!.ToString(), out var old) || !JsonNode.DeepEquals(old, season)))
                    {
                        if (itemOverride.Length == 0) end = DateTimeOffset.Parse(season["endsAt"]!.ToString());
                        Add("season", season["id"]!.ToString());
                    }
            }
            else if (c.Area == "content_window" && c.After?["enabled"]?.GetValue<bool>() == true)
            {
                if (end is null || end <= drop.StartsAt) throw new ContentChangeException(422, "Each content release needs an end date after its start date.");
                Add(c.Key["kind"]!.ToString(), c.Key["target"]!.ToString());
            }
            else if (c.Area is "store_lineup" or "token_store_lineup" or "sbc_live" || c.Area == "rules" && c.Key["file"]?.ToString() == "objectives" && c.Key["path"]?.ToJsonString() == "[\"live\"]")
            {
                var field = c.Area == "rules" ? "value" : "listing"; var idField = c.Area is "store_lineup" or "token_store_lineup" ? "packId" : c.Area == "sbc_live" ? "setKey" : "group";
                var before = (c.Before?[field] as JsonArray)?.OfType<JsonObject>().Where(r => r["enabled"]?.GetValue<bool>() == true).Select(r => r[idField]!.ToString()).ToHashSet() ?? [];
                foreach (var row in (c.After?[field] as JsonArray)?.OfType<JsonObject>() ?? [])
                    if (row["enabled"]?.GetValue<bool>() == true && !before.Contains(row[idField]!.ToString())) Add(c.Area == "store_lineup" ? "store" : c.Area == "token_store_lineup" ? "token" : c.Area == "sbc_live" ? "sbc" : "objective", row[idField]!.ToString());
            }
        }
        return result;
    }

    private static JsonObject? RemoveExpiredAvailability(ContentChange change, JsonObject? value, JsonObject? live, JsonArray expiries, DateTimeOffset now, ContentDrop drop)
    {
        if (value is null) return null;
        if (change.Area is "promo_event" or "icon_rotation")
        {
            if (change.Key["code"]!.ToString().StartsWith($"drop_{drop.Id}_", StringComparison.Ordinal)) value["starts_at"] = drop.StartsAt.ToString("O");
            var window = expiries.OfType<JsonObject>().SingleOrDefault(e => e["kind"]?.ToString() == change.Area && e["target"]?.ToString() == change.Key["code"]?.ToString());
            if (window is not null) value["ends_at"] = window["endsAt"]?.DeepClone() ?? JsonValue.Create("9999-12-31T23:59:59Z");
        }
        if (change.Area == "rules" && change.Key["file"]?.ToString() == "objectives" && change.Key["path"]?.ToJsonString() == "[\"seasons\"]")
            foreach (var season in (value["value"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var window = expiries.OfType<JsonObject>().SingleOrDefault(e => e["kind"]?.ToString() == "season" && e["target"]?.ToString() == season["id"]?.ToString());
                if (window is not null) season["endsAt"] = window["endsAt"]?.DeepClone() ?? JsonValue.Create("9999-12-31T23:59:59Z");
            }
        var expired = expiries.OfType<JsonObject>().Where(e => e["endsAt"] is not null && DateTimeOffset.Parse(e["endsAt"]!.ToString()) <= now).ToArray();
        if (change.Area == "content_window" && expired.Any(e => e["kind"]?.ToString() == change.Key["kind"]?.ToString() && e["target"]?.ToString() == change.Key["target"]?.ToString()))
            return live?.DeepClone().AsObject() ?? new JsonObject { ["enabled"] = false };
        var kind = change.Area == "store_lineup" ? "store" : change.Area == "token_store_lineup" ? "token" : change.Area == "sbc_live" ? "sbc" : change.Area == "rules" && change.Key["file"]?.ToString() == "objectives" && change.Key["path"]?.ToJsonString() == "[\"live\"]" ? "objective" : null;
        if (kind is not null)
        {
            var field = kind == "objective" ? "value" : "listing"; var key = kind is "store" or "token" ? "packId" : kind == "sbc" ? "setKey" : "group";
            foreach (var row in (value[field] as JsonArray)?.OfType<JsonObject>() ?? [])
                if (expired.Any(e => e["kind"]?.ToString() == kind && e["target"]?.ToString() == row[key]?.ToString())) row["enabled"] = (live?[field] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(r => r[key]?.ToString() == row[key]?.ToString())?["enabled"]?.DeepClone() ?? JsonValue.Create(false);
        }
        return value;
    }

    private sealed class ReadProjection<T>(T value) : Exception { public T Value => value; }
    /// <summary>Read after preceding queued drops; the savepoint always rolls the projection back.</summary>
    public async Task<T> DraftReadAsync<T>(IUnitOfWork work, long changesetId, Func<IUnitOfWork, Task<T>> read)
    {
        await ContentChangesets.LockAsync(work);
        try
        {
            await work.SavepointAsync(async () =>
            {
                var drops = await DropsAsync(work);
                var selected = drops.Single(d => d.ChangesetId == changesetId);
                foreach (var earlier in drops.Where(d => d.Status == "scheduled" && d.Id != selected.Id && (d.StartsAt < selected.StartsAt || d.StartsAt == selected.StartsAt && d.Id < selected.Id)).OrderBy(d => d.StartsAt).ThenBy(d => d.Id))
                    {
                    var set=(await ContentChangesets.ReadAsync(work,earlier.ChangesetId))!;
                    var expiry=await ExpiriesAsync(work,earlier,set);
                    earlier.Expires.Clear();
                    foreach(var item in expiry) earlier.Expires.Add(item?.DeepClone());
                    await Draft(earlier.ChangesetId).ApplyDraftAsync(work, earlier.ChangesetId, false,
                        (change,value,live)=>RemoveExpiredAvailability(change,value,live,earlier.Expires,selected.StartsAt,earlier));
                }
                await ApplyExpiryAsync(work, drops.Where(d => d.Id != selected.Id && d.Status is "live" or "scheduled").ToArray(), selected.StartsAt, false);
                await work.ExecuteAsync("SELECT set_config('ultimateteam.draft_id',@id,true)", ("id", changesetId.ToString()));
                throw new ReadProjection<T>(await read(work));
            });
        }
        catch (ReadProjection<T> projected) { return projected.Value; }
        throw new InvalidOperationException("An editor projection committed.");
    }
    public Task<JsonObject?> DraftBaselineAsync(IUnitOfWork work, long changesetId, IContentArea area, JsonObject key,
        ContentOperation? operation = null, JsonObject? after = null) => DraftReadAsync(work, changesetId, async projected =>
        {
            var row = await area.ReadAsync(projected, key);
            if (operation is { } op) await area.ValidateAsync(projected, op, key, after);
            return row;
        });

    private sealed class Projection(ContentSnapshot snapshot) : Exception { public ContentSnapshot Snapshot => snapshot; }
    public async Task<ContentSnapshot> ProjectAsync(DateTimeOffset at, long? candidate = null, CancellationToken token = default)
    {
        try
        {
            await game.UnitOfWork.RunAsync(0, "content-schedule-preview", async work =>
            {
                await ProjectWithinAsync(work, at, candidate, token);
            }, token);
        }
        catch (Projection projection) { return projection.Snapshot; }
        throw new InvalidOperationException("A schedule preview committed.");
    }
    private async Task ProjectWithinAsync(IUnitOfWork work, DateTimeOffset at, long? candidate, CancellationToken token)
    {
        await ContentChangesets.LockAsync(work);
        var drops = await DropsAsync(work);
        var selected = candidate is { } id ? drops.SingleOrDefault(d => d.Id == id) ?? throw new ContentChangeException(404, "No such drop.") : null;
        if (selected is not null && selected.Status is not ("draft" or "scheduled" or "failed")) throw new ContentChangeException(409, "Preview an unpublished drop.");
        var queued = drops.Where(d => d.Status == "scheduled" || d.Id == candidate).ToArray();
        foreach (var d in queued)
        {
            var set = (await ContentChangesets.ReadAsync(work, d.ChangesetId))!; CheckAllowed(set);
            d.Expires.Clear(); foreach (var item in await ExpiriesAsync(work, d, set)) d.Expires.Add(item?.DeepClone());
        }
        var simulated = drops.Where(d => d.Status == "live" || queued.Any(q => q.Id == d.Id)).ToArray();
        var horizon = candidate is null ? at : queued.Select(d => d.StartsAt).Concat(queued.SelectMany(ExpiryTimes)).Append(at).Max();
        ContentSnapshot? selectedSnapshot = null;
        foreach (var instant in queued.Select(d => d.StartsAt).Concat(simulated.SelectMany(ExpiryTimes)).Where(t => t <= horizon).Append(at).Distinct().Order())
        {
            foreach (var d in queued.Where(d => d.StartsAt == instant))
            {
                // A selected draft can reference promos supplied by preceding queued releases.
                if (d.Id == candidate)
                    await ValidateDropTimingAsync(work, d, (await ContentChangesets.ReadAsync(work, d.ChangesetId))!);
                await Draft(d.ChangesetId).ApplyDraftAsync(work, d.ChangesetId, false, (change, value, live) => RemoveExpiredAvailability(change, value, live, d.Expires, instant, d));
                _ = await ContentSources.LoadAsync(work, game.Paths, Log, token);
            }
            await ApplyExpiryAsync(work, simulated, instant, false);
            var sources = await ContentSources.LoadAsync(work, game.Paths, Log, token);
            var snapshot = ContentSnapshot.Create(sources, game.Live.Current.Revision + 1, () => sources.Packs, instant);
            if (instant == at) selectedSnapshot = snapshot;
        }
        throw new Projection(selectedSnapshot ?? game.Live.Current);
    }
    private static IEnumerable<DateTimeOffset> ExpiryTimes(ContentDrop drop) => drop.Expires.OfType<JsonObject>()
        .Where(e => e["done"]?.GetValue<bool>() != true && e["endsAt"] is not null).Select(e => DateTimeOffset.Parse(e["endsAt"]!.ToString()));

    private async Task ApplyExpiryAsync(IUnitOfWork work, IReadOnlyList<ContentDrop> drops, DateTimeOffset now, bool history)
    {
        var area = new ContentChangesets(game, _areas).Area("content_window");
        await work.ExecuteAsync("SELECT set_config('ultimateteam.content_publish','on',true)");
        foreach (var drop in drops.Where(d => d.Status == "live" || !history && d.Status is "scheduled" or "draft" or "failed"))
        {
            var changed = false;
            foreach (var e in drop.Expires.OfType<JsonObject>())
            {
                if (e["done"]?.GetValue<bool>() == true || e["endsAt"] is null || DateTimeOffset.Parse(e["endsAt"]!.ToString()) > now) continue;
                var kind = e["kind"]!.ToString(); var target = e["target"]!.ToString();
                var covered = drops.Any(other => other.Id != drop.Id && other.StartsAt <= now && (other.Status == "live" || !history && other.Status is "scheduled" or "draft")
                    && other.Expires.OfType<JsonObject>().Any(x => x["kind"]?.ToString() == kind && x["target"]?.ToString() == target && (x["endsAt"] is null || DateTimeOffset.Parse(x["endsAt"]!.ToString()) > now)));
                if (!covered && kind is "store" or "token" or "sbc" or "objective")
                    await area.ApplyAsync(work, ContentOperation.Update, new JsonObject { ["kind"] = kind, ["target"] = target }, new JsonObject { ["enabled"] = false });
                e["done"] = true; changed = true;
            }
            if (history && changed)
                await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET expires=@expires::jsonb,version=version+1 WHERE drop_id=@id", ("expires", drop.Expires.ToJsonString()), ("id", drop.Id));
            if (history && (drop.EndsAt is { } end && end <= now || drop.Expires.Count > 0) && !drop.Expires.OfType<JsonObject>().Any(e => e["endsAt"] is null || e["done"]?.GetValue<bool>() != true))
                await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET status='ended',version=version+1 WHERE drop_id=@id AND status='live'", ("id", drop.Id));
        }
        await work.ExecuteAsync("SELECT set_config('ultimateteam.content_publish','off',true)");
    }

    public async Task RunOnceAsync(CancellationToken token = default, long? publishNow = null, long? expectedVersion = null)
    {
        var now = game.Time.GetUtcNow();
        var needed = await game.UnitOfWork.RunReadAsync(0, async work =>
        {
            var drops = await DropsAsync(work);
            return publishNow is not null || drops.Any(d => d.Status == "scheduled" && d.StartsAt <= now || d.Status == "live" && (ExpiryTimes(d).Any(t => t <= now) || d.EndsAt <= now && !d.Expires.OfType<JsonObject>().Any(e => e["endsAt"] is null || e["done"]?.GetValue<bool>() != true)));
        }, token);
        if (!needed) return;
        await game.Live.ReloadAsync(async (revision, cancellation) =>
        {
            var sources = await game.UnitOfWork.RunAsync(0, "content-scheduled-publish", async work =>
            {
                await ContentChangesets.LockAsync(work);
                await work.ExecuteAsync("SELECT set_config('ultimateteam.defer_objective_start','on',true)");
                var drops = await DropsAsync(work);
                if (publishNow is { } requested)
                {
                    var selected = drops.SingleOrDefault(d => d.Id == requested) ?? throw new ContentChangeException(404, "No such drop.");
                    Version(selected.Version, expectedVersion ?? 0);
                    if (selected.Status is not ("draft" or "failed" or "scheduled")) throw new ContentChangeException(409, "Select an unpublished drop.");
                    if ((await CampaignsAsync(work)).Any(c => c.Id == selected.CampaignId && c.StartsAt is not null))
                        throw new ContentChangeException(400, "Campaign drops release at 7pm London time. Schedule this drop instead.");
                }
                foreach (var drop in drops.Where(d => d.Status == "scheduled" && d.StartsAt <= now || d.Id == publishNow).OrderBy(d => d.StartsAt).ThenBy(d => d.Id))
                {
                    var current = (await DropsAsync(work)).Single(d => d.Id == drop.Id);
                    if (drop.Id != publishNow && current.Status != "scheduled") continue;
                    try
                    {
                        await work.SavepointAsync(async () =>
                        {
                            var set = (await ContentChangesets.ReadAsync(work, drop.ChangesetId))!; CheckAllowed(set);
                            if (set.Changes.Count == 0) throw new ContentChangeException(400, "Add content before publication.");
                            if (drop.Id == publishNow && drop.StartsAt > now && set.Changes.Any(c => c.Area == "content_window"))
                                throw new ContentChangeException(422, "This release starts in the future. Schedule it instead.");
                            var expiry = await ExpiriesAsync(work, drop, set);
                            // Expired windows are removed within this transaction; never visible to a request.
                            await Draft(drop.ChangesetId).ApplyDraftAsync(work, drop.ChangesetId, true, (change, value, live) =>
                            {
                                var result = RemoveExpiredAvailability(change, value, live, expiry, now, drop);
                                if (drop.Id == publishNow && result is not null)
                                {
                                    if (change.Area is "promo_event" or "icon_rotation" && DateTimeOffset.Parse(result["ends_at"]!.ToString()) > now) result["starts_at"] = now.ToString("O");
                                    if (change.Area == "rules" && change.Key["file"]?.ToString() == "objectives" && change.Key["path"]?.ToJsonString() == "[\"seasons\"]")
                                        foreach (var season in (result["value"] as JsonArray)?.OfType<JsonObject>() ?? [])
                                            if (expiry.OfType<JsonObject>().Any(e => e["kind"]?.ToString() == "season" && e["target"]?.ToString() == season["id"]?.ToString()) && DateTimeOffset.Parse(season["startsAt"]!.ToString()) > now && DateTimeOffset.Parse(season["endsAt"]!.ToString()) > now) season["startsAt"] = now.ToString("O");
                                }
                                return result;
                            });
                            await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET status='live',published_at=now(),error=NULL,version=version+1,expires=@expiry::jsonb WHERE drop_id=@id", ("id", drop.Id), ("expiry", expiry.ToJsonString()));
                            if (drop.Id == publishNow && drop.StartsAt > now && (drop.EndsAt is null || drop.EndsAt > now)) await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET starts_at=@now WHERE drop_id=@id", ("now", now), ("id", drop.Id));
                            await ApplyExpiryAsync(work, await DropsAsync(work), now, true);
                            var loaded = await ContentSources.LoadAsync(work, game.Paths, Log, cancellation);
                            _ = ContentSnapshot.Create(loaded, revision, () => loaded.Packs, now);
                            if (drop.Id == publishNow) await ContentChangesets.InvalidateDependentsAsync(work, drop.ChangesetId);
                        });
                    }
                    catch (Exception e) when (e is ContentChangeException or InvalidDataException or Npgsql.PostgresException)
                    {
                        if (drop.Id == publishNow) throw new ContentChangeException(422, $"Nothing was published: {e.Message}");
                        await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET status='failed',error=@error,version=version+1 WHERE drop_id=@id", ("id", drop.Id), ("error", e.Message));
                        await ContentChangesets.InvalidateDependentsAsync(work, drop.ChangesetId);
                        Log.LogError("CONTENT DROP FAILED drop={Drop}: {Error}", drop.Id, e.Message);
                    }
                }
                await ApplyExpiryAsync(work, await DropsAsync(work), now, true);
                var final = await ContentSources.LoadAsync(work, game.Paths, Log, cancellation);
                await ObjectiveStartRepository.StartLiveAsync(work, final.Rules.Objectives);
                var snapshot = ContentSnapshot.Create(final, revision, () => game.Packs.Rules, now);
                return (Sources: final, Snapshot: snapshot);
            }, cancellation);
            game.Packs.Publish(sources.Sources.Packs);
            return sources.Snapshot;
        }, token);
    }
    public async Task<ContentDrop> EndAsync(long id, long version, CancellationToken token = default)
    {
        var now = game.Time.GetUtcNow();
        await game.Live.ReloadAsync(async (revision, cancellation) =>
        {
            var sources = await game.UnitOfWork.RunAsync(0, "content-drop-end", async work =>
            {
                await ContentChangesets.LockAsync(work);
                var drops = await DropsAsync(work); var drop = drops.SingleOrDefault(d => d.Id == id) ?? throw new ContentChangeException(404, "No such drop.");
                Version(drop.Version, version);
                if (drop.Status != "live") throw new ContentChangeException(409, "Only live drops can end now.");
                await work.ExecuteAsync("SELECT set_config('ultimateteam.content_publish','on',true)");
                foreach (var window in drop.Expires.OfType<JsonObject>())
                {
                    window["endsAt"] = now.ToString("O"); window["done"] = false;
                    var kind = window["kind"]!.ToString(); var target = window["target"]!.ToString();
                    if (kind is "promo_event" or "icon_rotation")
                    {
                        var area = new ContentChangesets(game, _areas).Area(kind); var key = new JsonObject { ["code"] = target };
                        var row = await area.ReadAsync(work, key);
                        if (row is not null) { row["enabled"] = false; await area.ApplyAsync(work, ContentOperation.Update, key, row); }
                    }
                    else if (kind == "season")
                    {
                        var area = new RulesArea(); var key = new JsonObject { ["file"] = "objectives", ["path"] = new JsonArray("seasons") };
                        var row = await area.ReadAsync(work, key);
                        var season = (row?["value"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(s => s["id"]?.ToString() == target);
                        if (season is not null) { season["enabled"] = false; await area.ApplyAsync(work, ContentOperation.Update, key, row); }
                    }
                }
                await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET ends_at=@now,expires=@expires::jsonb,inherit_end=false,version=version+1 WHERE drop_id=@id", ("id", id), ("now", now), ("expires", drop.Expires.ToJsonString()));
                await ApplyExpiryAsync(work, await DropsAsync(work), now, true);
                await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET status='ended' WHERE drop_id=@id", ("id", id));
                await ContentChangesets.InvalidateDependentsAsync(work, drop.ChangesetId);
                var final = await ContentSources.LoadAsync(work, game.Paths, Log, cancellation);
                return (Sources: final, Snapshot: ContentSnapshot.Create(final, revision, () => game.Packs.Rules, now));
            }, cancellation);
            game.Packs.Publish(sources.Sources.Packs);
            return sources.Snapshot;
        }, token);
        return await GetDropAsync(id, token);
    }

    public async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), game.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                try { await RunOnceAsync(token); }
                catch (Exception e) when (e is not OperationCanceledException) { Log.LogError(e, "Scheduled content worker failed"); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
