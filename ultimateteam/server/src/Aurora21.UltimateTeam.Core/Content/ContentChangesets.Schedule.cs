using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;

namespace Aurora21.UltimateTeam.Core.Content;

public sealed partial class ContentChangesets
{
    private async ValueTask SelectDraftAsync(IUnitOfWork work, bool editing)
    {
        await work.ExecuteAsync("SELECT set_config('ultimateteam.draft_id', @id, true)", ("id", _selectedId?.ToString() ?? ""));
        if (_selectedId is not { } id) return;
        var states = await work.QueryAsync("SELECT status FROM ultimateteam.content_drops WHERE changeset_id=@id FOR UPDATE", r => r.GetString(0), ("id", id));
        if (states.Count != 1 || states[0] is not ("draft" or "scheduled" or "failed"))
            throw new ContentChangeException(409, "Select an editable draft drop.");
        if (!editing) return;
        await work.ExecuteAsync("UPDATE ultimateteam.content_drops SET status='draft',version=version+1,error=NULL WHERE changeset_id=@id", ("id", id));
        await InvalidateDependentsAsync(work, id);
    }

    internal static async ValueTask InvalidateDependentsAsync(IUnitOfWork work, long id)
    {
        await work.ExecuteAsync("""
            WITH RECURSIVE dependencies(source,target) AS (
              SELECT DISTINCT a.changeset_id,b.changeset_id
              FROM ultimateteam.content_changes a JOIN ultimateteam.content_changes b ON a.changeset_id<>b.changeset_id
              LEFT JOIN ultimateteam.content_drops da ON da.changeset_id=a.changeset_id
              JOIN ultimateteam.content_drops db ON db.changeset_id=b.changeset_id AND db.starts_at>=coalesce(da.starts_at,'-infinity'::timestamptz)
              WHERE (a.area=b.area AND a.target_key=b.target_key)
                 OR (a.area='promo' AND b.area IN ('promo_event','store_pack') AND
                     jsonb_path_exists(b.after_value,'$.** ? (@ == $code)',jsonb_build_object('code',a.target_key->'code')))
                 OR (a.area='store_pack' AND ((b.area='content_window' AND b.target_key->>'kind' IN ('store','token') AND b.target_key->>'target'=a.target_key->>'packId')
                     OR (b.area IN ('store_lineup','token_store_lineup','rules','sbc_set') AND jsonb_path_exists(b.after_value,'$.** ? (@ == $id)',jsonb_build_object('id',a.target_key->'packId')))))
                 OR (a.area='sbc_set' AND ((b.area='content_window' AND b.target_key->>'kind'='sbc' AND b.target_key->>'target'=a.target_key->>'setKey')
                     OR (b.area='sbc_live' AND jsonb_path_exists(b.after_value,'$.** ? (@ == $id)',jsonb_build_object('id',a.target_key->'setKey')))))
                 OR (a.area='rules' AND a.target_key->>'file'='objectives' AND
                     ((b.area='rules' AND b.target_key->>'file'='objectives') OR (b.area='content_window' AND b.target_key->>'kind'='objective')))
            ), affected(id) AS (
              SELECT target FROM dependencies WHERE source=@id
              UNION SELECT d.target FROM dependencies d JOIN affected a ON d.source=a.id
            )
            UPDATE ultimateteam.content_drops SET status='draft',version=version+1,error='Related content changed. Validate and schedule again.'
            WHERE status='scheduled' AND changeset_id<>@id AND changeset_id IN (SELECT id FROM affected)
            """, ("id", id));
    }

    /// <summary>Apply a selected drop inside the scheduler's transaction, preserving unrelated intervening edits.</summary>
    internal async Task<ContentChangeset> ApplyDraftAsync(IUnitOfWork work, long id, bool history, Func<ContentChange, JsonObject?, JsonObject?, JsonObject?>? transform = null)
    {
        await work.ExecuteAsync("SELECT set_config('ultimateteam.draft_id', @id, true)", ("id", id.ToString()));
        var draft = await ReadAsync(work, id) ?? throw new ContentChangeException(404, "No such content draft.");
        if (draft.Status != "draft") throw new ContentChangeException(409, "This drop was already published or discarded.");
        await work.ExecuteAsync("SELECT set_config('ultimateteam.content_publish','on',true)");
        foreach (var change in draft.Changes.OrderBy(c => Area(c.Area).ApplyOrder).ThenBy(c => c.Id))
        {
            var area = Area(change.Area);
            var live = await area.ReadAsync(work, change.Key);
            // Catalogue-wide revisions are editor concurrency tokens, not scheduled payload fields.
            var before = change.Before?.DeepClone().AsObject();
            var after = change.After?.DeepClone().AsObject();
            if (before?.ContainsKey("revision") == true) before["revision"] = live?["revision"]?.DeepClone();
            if (after?.ContainsKey("revision") == true && live is not null) after["revision"] = live["revision"]?.DeepClone();
            var merged = change.Area == "content_window" ? after : ScheduledContentPatch.Rebase(change.Area, change.Key, change.Operation, before, after, live);
            if (transform is not null) merged = transform(change, merged, live);
            if (area.Name == "content_window" && JsonNode.DeepEquals(merged, live))
            {
                if (history) await work.ExecuteAsync("UPDATE ultimateteam.content_changes SET before_value=@before::jsonb,after_value=@after::jsonb WHERE change_id=@id", ("before", live?.ToJsonString()), ("after", merged?.ToJsonString()), ("id", change.Id));
                continue;
            }
            await area.ValidateAsync(work, change.Operation, change.Key, merged);
            await area.ApplyAsync(work, change.Operation, change.Key, merged);
            if (history)
                await work.ExecuteAsync("UPDATE ultimateteam.content_changes SET before_value=@before::jsonb,after_value=@after::jsonb WHERE change_id=@id",
                    ("before", live?.ToJsonString()), ("after", merged?.ToJsonString()), ("id", change.Id));
        }
        await work.ExecuteAsync("SELECT set_config('ultimateteam.content_publish','off',true)");
        if (history)
            await work.ExecuteAsync("UPDATE ultimateteam.content_changesets SET status='published',published_at=now() WHERE changeset_id=@id", ("id", id));
        return draft;
    }
}
