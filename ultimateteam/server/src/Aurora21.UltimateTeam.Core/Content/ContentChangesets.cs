using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Content;

/// <summary>What a content change does to its row.</summary>
public enum ContentOperation { Insert, Update, Hide }

/// <summary>
/// One kind of editable content (for example player cards, or one rules file): how its rows are keyed, read, checked
/// and written. The changeset pipeline calls it; nothing else writes content rows.
/// </summary>
public interface IContentArea
{
    /// <summary>The area name stored with each change, for example <c>player_card</c>.</summary>
    string Name { get; }

    /// <summary>Areas apply in ascending order within one publish, so a row is written before rows that refer to it.</summary>
    int ApplyOrder { get; }

    /// <summary>The canonical key of a row (for example <c>{"definitionId": 22507}</c>); throws <see cref="ContentChangeException"/> when malformed.</summary>
    JsonObject NormalizeKey(JsonObject key);

    /// <summary>The live row as the editor shows it, or null when it does not exist.</summary>
    ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key);

    /// <summary>Refuses an operation or value this area does not allow, with <see cref="ContentChangeException"/>.</summary>
    ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after);

    /// <summary>Writes one change. Runs only inside a publish transaction.</summary>
    ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after);
}

/// <summary>Optional catalogue-wide baseline check for a new row whose missing before value cannot carry a revision.</summary>
public interface IContentBaselineArea
{
    ValueTask CheckBaselineAsync(IUnitOfWork work, ContentChange change);
}

/// <summary>The content areas this server edits. Areas are added one at a time (live-content-editing-plan.md, steps 5-8).</summary>
public static class ContentAreas
{
    public static IReadOnlyList<IContentArea> Default { get; } = [new PlayerCardArea(), new PromoArea(), new PromoTimingArea(false), new PromoTimingArea(true),
        new ChampionsRankArea(), new RivalsRankArea(), new RivalsPromotionArea(),
        new SquadBattlesTierArea(), new DraftLevelArea(), new RulesArea()];
}

/// <summary>A refused content edit, validation or publish: an HTTP-like status, a message and any conflicting rows.</summary>
public sealed class ContentChangeException(int status, string message, IReadOnlyList<ContentConflict>? conflicts = null) : Exception(message)
{
    public int Status { get; } = status;
    public IReadOnlyList<ContentConflict> Conflicts { get; } = conflicts ?? Array.Empty<ContentConflict>();
}

public sealed record ContentChange(long Id, string Area, JsonObject Key, ContentOperation Operation, JsonObject? Before, JsonObject? After,
    DateTimeOffset UpdatedAt);

public sealed record ContentChangeset(long Id, string Status, string? Note, string? CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt, long? RevertsChangesetId, IReadOnlyList<ContentChange> Changes);

/// <summary>A pending change whose row no longer matches the value it was edited from.</summary>
public sealed record ContentConflict(long ChangeId, string Area, JsonObject Key, JsonObject? Expected, JsonObject? Live);

/// <summary>A dry run of the open changeset: would it publish, and if not, why.</summary>
public sealed record ContentValidation(bool Ok, int Changes, IReadOnlyList<ContentConflict> Conflicts, IReadOnlyList<string> Errors);

public sealed record ContentPublishResult(ContentChangeset Changeset, ContentSnapshot Snapshot);

/// <summary>
/// Reviewed content editing (live-content-editing-plan.md, step 3). Edits collect as changes of one open changeset;
/// validate applies them in a transaction, rebuilds the content from that transaction with the start-up loader and rolls
/// back; publish does the same, commits, and makes the new content current for every club. Each change keeps the row as
/// it was first edited, so a publish refuses rows that changed since, and a published changeset can be reverted.
/// </summary>
public sealed partial class ContentChangesets
{
    // One advisory lock for every changeset write, across server processes too.
    private const long ChangesetLock = 210_064;
    private const int MaxNoteLength = 500;
    private const int MaxByLength = 64;

    private readonly GameContext _game;
    private readonly IReadOnlyDictionary<string, IContentArea> _areas;
    private readonly ILogger _log;
    private readonly long? _selectedId;
    private readonly bool _immediate;

    public ContentChangesets(GameContext game, IEnumerable<IContentArea> areas, long? selectedId = null, bool immediate = false)
    {
        _game = game;
        _selectedId = selectedId;
        _immediate = immediate;
        _areas = areas.ToDictionary(area => area.Name, StringComparer.Ordinal);
        _log = game.Logger("UltimateTeam.ContentChangesets");
    }

    public IReadOnlyCollection<string> AreaNames => _areas.Keys.Order(StringComparer.Ordinal).ToArray();
    public ContentChangesets ForImmediate() => new(_game, _areas.Values, immediate: true);

    /// <summary>The open changeset, or null when no edit is pending.</summary>
    public Task<ContentChangeset?> OpenAsync(CancellationToken cancellationToken = default) =>
        _immediate ? Task.FromResult<ContentChangeset?>(null) : _game.UnitOfWork.RunReadAsync(0, work => ReadOpenAsync(work), cancellationToken);

    /// <summary>
    /// Adds or replaces the pending change of one row. The first edit of a row records its live value; later edits keep
    /// it, and an edit back to that value removes the change.
    /// </summary>
    public Task<ContentChangeset?> SetChangeAsync(string areaName, JsonObject key, ContentOperation operation, JsonObject? after, string? by,
        CancellationToken cancellationToken = default)
    {
        if (_immediate) return SaveLiveAsync(areaName, key, operation, after, by, cancellationToken);
        if (_selectedId is not null && !ContentSchedule.IsPlayerContent(areaName,key)) throw new ContentChangeException(400,"Only player content can be staged in a drop.");
        var area = Area(areaName);
        var canonical = area.NormalizeKey(key);
        CheckLabel(by, "by", MaxByLength);
        if ((operation == ContentOperation.Hide) != (after is null))
            throw new ContentChangeException(400, operation == ContentOperation.Hide ? "A hide change has no value." : "The change needs its new value.");
        return _game.UnitOfWork.RunAsync(0, "content-change", async work =>
        {
            await LockAsync(work);
            await SelectDraftAsync(work, editing: true);
            JsonObject? projected = null;
            if (_selectedId is { } draftId)
                projected = await new ContentSchedule(_game,_areas.Values).DraftBaselineAsync(work,draftId,area,canonical,operation,after);
            else await area.ValidateAsync(work, operation, canonical, after);
            await StageAsync(work, area, canonical, operation, after, await OpenIdAsync(work, by), (projected, _selectedId is not null));
            if (_selectedId is { } selected) await InvalidateDependentsAsync(work,selected);
            return await ReadOpenAsync(work);
        }, cancellationToken);
    }

    /// <summary>Publish one edit in its own transaction and history entry, preserving other open edits and scheduled drops.</summary>
    public async Task<ContentChangeset?> SaveLiveAsync(string areaName, JsonObject key, ContentOperation operation, JsonObject? after, string? by,
        CancellationToken cancellationToken = default)
    {
        var area = Area(areaName);
        var canonical = area.NormalizeKey(key);
        CheckLabel(by, "by", MaxByLength);
        if ((operation == ContentOperation.Hide) != (after is null))
            throw new ContentChangeException(400, operation == ContentOperation.Hide ? "A hide change has no value." : "The change needs its new value.");
        ContentChangeset? published = null;
        ContentSnapshot snapshot;
        try
        {
            snapshot = await _game.Live.ReloadAsync(async (revision, token) =>
            {
                var result = await _game.UnitOfWork.RunAsync(0, "content-save-live", async work =>
                {
                    await LockAsync(work);
                    var id = await work.ScalarAsync<long>("INSERT INTO ultimateteam.content_changesets(status,created_by) VALUES('draft',@by) RETURNING changeset_id", ("by", by));
                    await work.ExecuteAsync("SELECT set_config('ultimateteam.draft_id',@id,true)", ("id", id.ToString()));
                    await area.ValidateAsync(work, operation, canonical, after);
                    await StageAsync(work, area, canonical, operation, after, id, (null, false));
                    return await ApplyWithinAsync(work, (await ReadAsync(work, id))!, true, null, by, revision, token);
                }, token);
                published = result.Changeset;
                _game.Packs.Publish(result.Packs);
                return result.Snapshot;
            }, cancellationToken);
        }
        catch (Exception error) when (IsContentFailure(error) && error is not ContentChangeException)
        {
            throw new ContentChangeException(422, $"Nothing was saved: {error.Message}");
        }

        _log.LogInformation("CONTENT SAVED changeset={Changeset} changes={Changes} revision={Revision}", published!.Id,
            published.Changes.Count, snapshot.Revision);
        return published;
    }

    /// <summary>
    /// Stages one change in the open changeset (the caller holds the lock). <paramref name="known"/> is the live row when
    /// the caller has already read it (an import compares whole documents), so it is not read again.
    /// </summary>
    private static async ValueTask StageAsync(IUnitOfWork work, IContentArea area, JsonObject canonical, ContentOperation operation,
        JsonObject? after, long changesetId, (JsonObject? Live, bool Read) known)
    {
        var existing = (await ChangesAsync(work, changesetId, area.Name, canonical)).SingleOrDefault();
        var live = existing is not null ? null : known.Read ? known.Live : await area.ReadAsync(work, canonical);
        var (op, before) = existing is null ? (operation, live) : (Merge(existing.Operation, operation), existing.Before);
        if (area.Name == PlayerCardArea.AreaName && before is not null && !before.ContainsKey("cardTypeId"))
        {
            var current = await area.ReadAsync(work, canonical);
            if (current?["cardTypeId"] is { } type)
            {
                before = (JsonObject)before.DeepClone();
                before["cardTypeId"] = type.DeepClone();
            }
        }
        if (existing is null)
        {
            if (operation == ContentOperation.Insert && live is not null) throw new ContentChangeException(409, "That row already exists; edit it instead.");
            if (operation != ContentOperation.Insert && live is null) throw new ContentChangeException(404, "That row does not exist.");
        }
        // Editing back to the published value, or hiding a row that is only pending insertion, leaves nothing to publish.
        if (op is null || (op == ContentOperation.Update && area.Name != "content_window" && JsonNode.DeepEquals(before, after)))
        {
            if (existing is not null) await work.ExecuteAsync("DELETE FROM ultimateteam.content_changes WHERE change_id=@id", ("id", existing.Id));
        }
        else if (existing is not null)
        {
            await work.ExecuteAsync("""
                UPDATE ultimateteam.content_changes SET operation=@op, before_value=@before::jsonb, after_value=@after::jsonb, updated_at=now() WHERE change_id=@id
                """, ("op", Text(op.Value)), ("before", before?.ToJsonString()), ("after", after?.ToJsonString()), ("id", existing.Id));
        }
        else
        {
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.content_changes(changeset_id, area, target_key, operation, before_value, after_value)
                VALUES(@changeset, @area, @key::jsonb, @op, @before::jsonb, @after::jsonb)
                """, ("changeset", changesetId), ("area", area.Name), ("key", canonical.ToJsonString()), ("op", Text(op.Value)),
                ("before", before?.ToJsonString()), ("after", after?.ToJsonString()));
        }
    }

    /// <summary>Drops one pending change.</summary>
    public Task<ContentChangeset?> RemoveChangeAsync(long changeId, CancellationToken cancellationToken = default) =>
        _game.UnitOfWork.RunAsync(0, "content-change-remove", async work =>
        {
            await LockAsync(work);
            await SelectDraftAsync(work, editing: true);
            var removed = await work.ExecuteAsync("""
                DELETE FROM ultimateteam.content_changes c USING ultimateteam.content_changesets s
                WHERE c.change_id=@id AND s.changeset_id=c.changeset_id AND ((@selected::bigint IS NULL AND s.status='open') OR (s.changeset_id=@selected AND s.status='draft'))
                """, ("id", changeId), ("selected", _selectedId));
            if (removed == 0) throw new ContentChangeException(404, "No pending change has that id.");
            return await ReadOpenAsync(work);
        }, cancellationToken);

    /// <summary>Discards the open changeset; its changes stay recorded under the discarded changeset.</summary>
    public Task DiscardAsync(CancellationToken cancellationToken = default) =>
        _game.UnitOfWork.RunAsync(0, "content-discard", async work =>
        {
            await LockAsync(work);
            await SelectDraftAsync(work, editing: true);
            if (_selectedId is { } selected)
            {
                await work.ExecuteAsync("DELETE FROM ultimateteam.content_changes WHERE changeset_id=@id",("id",selected));
                return 0;
            }
            if (await work.ExecuteAsync("UPDATE ultimateteam.content_changesets SET status='discarded' WHERE (@selected::bigint IS NULL AND status='open') OR (changeset_id=@selected AND status='draft')", ("selected", _selectedId)) == 0)
                throw new ContentChangeException(409, "There are no pending content changes.");
            return 0;
        }, cancellationToken);

    /// <summary>A dry run of the open changeset: applied, loaded and checked in a transaction that is rolled back.</summary>
    public async Task<ContentValidation> ValidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ApplyAsync(commit: false, note: null, by: null, _game.Live.Current.Revision + 1, cancellationToken);
        }
        catch (DryRun dryRun)
        {
            return dryRun.Result;
        }
        catch (ContentChangeException refused) when (refused.Conflicts.Count > 0)
        {
            return new ContentValidation(false, await PendingCountAsync(cancellationToken), refused.Conflicts, [refused.Message]);
        }
        catch (Exception error) when (IsContentFailure(error))
        {
            return new ContentValidation(false, await PendingCountAsync(cancellationToken), [], [error.Message]);
        }
        throw new InvalidOperationException("A content validation committed.");
    }

    /// <summary>The validated pending revision, read from a transaction that is always rolled back.</summary>
    public async Task<ContentSnapshot> PreviewAsync(CancellationToken cancellationToken = default)
    {
        try { await ApplyAsync(false, null, null, _game.Live.Current.Revision + 1, cancellationToken); }
        catch (DryRun result) { return result.Snapshot; }
        catch (Exception error) when (IsContentFailure(error) && error is not ContentChangeException)
        { throw new ContentChangeException(422, $"The pending content cannot be previewed: {error.Message}"); }
        throw new InvalidOperationException("A preview committed.");
    }

    /// <summary>
    /// Publishes the open changeset: every change or none, validated as start-up validates, then current for every club.
    /// Units of work already running finish on the content they started with.
    /// </summary>
    public async Task<ContentPublishResult> PublishAsync(string? note, string? by, CancellationToken cancellationToken = default)
    {
        CheckLabel(note, "note", MaxNoteLength);
        CheckLabel(by, "by", MaxByLength);
        ContentChangeset? published = null;
        ContentSnapshot snapshot;
        try
        {
            snapshot = await _game.Live.ReloadAsync(async (revision, token) =>
            {
                var (changeset, next, packs) = await ApplyAsync(commit: true, note, by, revision, token);
                published = changeset;
                _game.Packs.Publish(packs);
                return next;
            }, cancellationToken);
        }
        catch (Exception error) when (IsContentFailure(error) && error is not ContentChangeException { Status: 409 })
        {
            // Conflicts and "nothing pending" keep their 409 and rows; anything else that stopped the publish is 422.
            throw new ContentChangeException(422, $"Nothing was published: {error.Message}");
        }
        _log.LogInformation("CONTENT PUBLISHED changeset={Changeset} changes={Changes} revision={Revision}", published!.Id,
            published.Changes.Count, snapshot.Revision);
        return new ContentPublishResult(published, snapshot);
    }

    /// <summary>Published changesets, newest first, with their changes.</summary>
    public Task<IReadOnlyList<ContentChangeset>> HistoryAsync(int limit, CancellationToken cancellationToken = default) =>
        _game.UnitOfWork.RunReadAsync(0, async work =>
        {
            var ids = await work.QueryAsync("""
                SELECT changeset_id FROM ultimateteam.content_changesets WHERE status='published' ORDER BY published_at DESC, changeset_id DESC LIMIT @limit
                """, row => row.GetInt64(0), ("limit", Math.Clamp(limit, 1, 200)));
            var list = new List<ContentChangeset>();
            foreach (var id in ids) list.Add((await ReadAsync(work, id))!);
            return (IReadOnlyList<ContentChangeset>)list;
        }, cancellationToken);

    /// <summary>
    /// Opens a changeset that puts back what a published changeset changed: its before values, published through the
    /// normal review. An inserted row is hidden, except a player card: owned card definitions must remain available.
    /// Needs no other pending edit.
    /// </summary>
    public Task<ContentChangeset?> RevertAsync(long changesetId, string? by, CancellationToken cancellationToken = default)
    {
        CheckLabel(by, "by", MaxByLength);
        return _game.UnitOfWork.RunAsync(0, "content-revert", async work =>
        {
            await LockAsync(work);
            await SelectDraftAsync(work, editing: true);
            var target = await ReadAsync(work, changesetId);
            if (_selectedId is not null && target is not null && target.Changes.Any(c=>!ContentSchedule.IsPlayerContent(c.Area,c.Key))) throw new ContentChangeException(400,"This history entry includes operational changes and cannot be put in a drop.");
            if (target is not { Status: "published" }) throw new ContentChangeException(404, "No published changeset has that id.");
            if (await ReadOpenAsync(work) is { Changes.Count: > 0 }) throw new ContentChangeException(409, "Publish or discard the pending changes first.");
            if (target.Changes.Any(change => change.Area == PlayerCardArea.AreaName && change.Operation == ContentOperation.Insert))
                throw new ContentChangeException(409, "A newly added player card cannot be reverted: clubs may already own it. Make it grant-only in the cards rules instead.");
            var openId = await OpenIdAsync(work, by);
            await work.ExecuteAsync("UPDATE ultimateteam.content_changesets SET reverts_changeset_id=@target WHERE changeset_id=@id",
                ("target", changesetId), ("id", openId));
            foreach (var change in target.Changes.Reverse())
            {
                var area = Area(change.Area);
                var live = await area.ReadAsync(work, change.Key);
                var (op, after) = change.Operation == ContentOperation.Insert ? (ContentOperation.Hide, (JsonObject?)null) : (ContentOperation.Update, change.Before);
                if (live is null || (op == ContentOperation.Update && JsonNode.DeepEquals(live, after))) continue;
                await work.ExecuteAsync("""
                    INSERT INTO ultimateteam.content_changes(changeset_id, area, target_key, operation, before_value, after_value)
                    VALUES(@changeset, @area, @key::jsonb, @op, @before::jsonb, @after::jsonb)
                    """, ("changeset", openId), ("area", change.Area), ("key", change.Key.ToJsonString()), ("op", Text(op)),
                    ("before", live.ToJsonString()), ("after", after?.ToJsonString()));
            }
            return await ReadOpenAsync(work);
        }, cancellationToken);
    }

    /// <summary>
    /// Stages the differences between the stored rules and the bundled rules files (every file, or one) as pending changes,
    /// reviewed and published like any other edit: how a changed file reaches a server whose rules live in the database.
    /// Returns the open changeset and how many changes the import staged.
    /// </summary>
    public Task<(ContentChangeset? Open, int Staged)> ImportRulesAsync(string? file, string? by, CancellationToken cancellationToken = default)
    {
        CheckLabel(by, "by", MaxByLength);
        if (file is not null && !GameRules.DatabaseFiles.Contains(file))
            throw new ContentChangeException(400, $"file is one of {string.Join(", ", GameRules.DatabaseFiles)}.");
        var area = Area(RulesArea.AreaName);
        IReadOnlyDictionary<string, string> bundled;
        try
        {
            bundled = GameRules.ReadFiles(_game.Paths.DataRoot);
            foreach (var (name, text) in bundled) GameRules.ValidateDocument(name, text);
        }
        catch (InvalidDataException error)
        {
            throw new ContentChangeException(422, $"The bundled rules files do not read, so nothing was imported: {error.Message}");
        }
        return _game.UnitOfWork.RunAsync(0, "content-import-rules", async work =>
        {
            await LockAsync(work);
            var stored = await RuleRows.DocumentsAsync(work);
            var differences = RuleRows.Differences(stored, bundled, file);
            if (differences.Count == 0) return (await ReadOpenAsync(work), 0);
            var open = await ReadOpenAsync(work);
            var affectedFiles = differences.Select(d => d.File).ToHashSet(StringComparer.Ordinal);
            if (open?.Changes.Any(c => c.Area == RulesArea.AreaName && c.Key["file"] is JsonValue name
                && name.TryGetValue<string>(out var pendingFile) && affectedFiles.Contains(pendingFile)) == true)
                throw new ContentChangeException(409, "Review, publish or discard pending rule edits for the affected files before importing. Nothing was imported.");
            var changesetId = await OpenIdAsync(work, by);
            var documents = stored.ToDictionary(pair => pair.Key, pair => JsonNode.Parse(pair.Value)!, StringComparer.Ordinal);
            foreach (var (name, path, kind, value) in differences)
            {
                var key = area.NormalizeKey(new JsonObject { ["file"] = name, ["path"] = path });
                JsonObject? live = null;
                if (documents.TryGetValue(name, out var document) && RuleRows.Find(document, path, out var found) is var current && found)
                    live = new JsonObject { ["value"] = current?.DeepClone() };
                var (operation, after) = kind switch
                {
                    ContentDifference.Update => (ContentOperation.Update, new JsonObject { ["value"] = value?.DeepClone() }),
                    ContentDifference.Insert => (ContentOperation.Insert, new JsonObject { ["value"] = value?.DeepClone() }),
                    _ => (ContentOperation.Hide, (JsonObject?)null),
                };
                await StageAsync(work, area, key, operation, after, changesetId, (live, true));
            }
            return (await ReadOpenAsync(work), differences.Count);
        }, cancellationToken);
    }

    private async Task<(ContentChangeset Changeset, ContentSnapshot Snapshot, PackRules Packs)> ApplyAsync(bool commit, string? note, string? by,
        long revision, CancellationToken cancellationToken) =>
        await _game.UnitOfWork.RunAsync(0, commit ? "content-publish" : "content-validate", async work =>
        {
            await LockAsync(work);
            await SelectDraftAsync(work, editing: false);
            var open = await ReadOpenAsync(work);
            if (open is not { Changes.Count: > 0 }) throw new ContentChangeException(409, "There are no pending content changes.");
            return await ApplyWithinAsync(work, open, commit, note, by, revision, cancellationToken);
        }, cancellationToken);

    private async Task<(ContentChangeset Changeset, ContentSnapshot Snapshot, PackRules Packs)> ApplyWithinAsync(IUnitOfWork work,
        ContentChangeset open, bool commit, string? note, string? by, long revision, CancellationToken cancellationToken)
    {
        var conflicts = new List<ContentConflict>();
        foreach (var change in open.Changes)
        {
            var area = Area(change.Area);
            if (area is IContentBaselineArea baseline) await baseline.CheckBaselineAsync(work, change);
            var live = await area.ReadAsync(work, change.Key);
            // Existing card drafts predate card-type snapshots. Compare their original recorded fields;
            // new drafts include the type so concurrent type edits and History restores are covered.
            var comparison = live;
            if (change.Area == PlayerCardArea.AreaName && change.Before is not null && !change.Before.ContainsKey("cardTypeId") && live is not null)
            {
                comparison = (JsonObject)live.DeepClone();
                comparison.Remove("cardTypeId");
            }
            if (change.Area == "promo" && change.Before is not null && !change.Before.ContainsKey("card_reductions") && live is not null)
            {
                comparison = (JsonObject)live.DeepClone();
                comparison.Remove("card_reductions");
            }
            if (!JsonNode.DeepEquals(comparison, change.Before)) conflicts.Add(new ContentConflict(change.Id, change.Area, change.Key, change.Before, live));
        }
        if (conflicts.Count > 0)
            throw new ContentChangeException(409, $"{conflicts.Count} pending change(s) no longer match the live content; review them first.", conflicts);
        // Only this transaction may update catalogue rows (migration 072).
        await work.ExecuteAsync("SELECT set_config('ultimateteam.content_publish', 'on', true)");
        foreach (var change in open.Changes.OrderBy(change => Area(change.Area).ApplyOrder).ThenBy(change => change.Id))
        {
            var area = Area(change.Area);
            await area.ValidateAsync(work, change.Operation, change.Key, change.After);
            await area.ApplyAsync(work, change.Operation, change.Key, change.After);
        }
        await work.ExecuteAsync("SELECT set_config('ultimateteam.content_publish', 'off', true)");
        // The content this transaction would commit, loaded and projected as start-up does: a failure rolls it all back.
        var sources = await ContentSources.LoadAsync(work, _game.Paths, _log, cancellationToken);
        var snapshot = ContentSnapshot.Create(sources, revision, commit ? () => _game.Packs.Rules : () => sources.Packs, _game.Time.GetUtcNow());
        if (!commit) throw new DryRun(new ContentValidation(true, open.Changes.Count, [], []), snapshot);
        await work.ExecuteAsync("""
            UPDATE ultimateteam.content_changesets SET status='published', published_at=now(), note=coalesce(@note, note),
                created_by=coalesce(@by, created_by)
            WHERE changeset_id=@id
            """, ("note", note), ("by", by), ("id", open.Id));
        await InvalidateDependentsAsync(work, open.Id);
        return ((await ReadAsync(work, open.Id))!, snapshot, sources.Packs);
    }

    private Task<int> PendingCountAsync(CancellationToken cancellationToken) =>
        _game.UnitOfWork.RunReadAsync(0, async work => (await ReadOpenAsync(work))?.Changes.Count ?? 0, cancellationToken);

    internal IContentArea Area(string name) =>
        _areas.GetValueOrDefault(name) ?? throw new ContentChangeException(400, $"This server has no content area named {name}.");

    public static ValueTask<int> LockAsync(IUnitOfWork work) => work.ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", ("key", ChangesetLock));

    private async ValueTask<long> OpenIdAsync(IUnitOfWork work, string? by)
    {
        if (_selectedId is { } selected) return selected;
        var existing = await work.QueryAsync("SELECT changeset_id FROM ultimateteam.content_changesets WHERE status='open'", row => row.GetInt64(0));
        return existing.Count > 0 ? existing[0] : await work.ScalarAsync<long>("""
            INSERT INTO ultimateteam.content_changesets(status, created_by) VALUES('open', @by) RETURNING changeset_id
            """, ("by", by));
    }

    private async ValueTask<ContentChangeset?> ReadOpenAsync(IUnitOfWork work)
    {
        if (_selectedId is { } selected)
        {
            var draft = await ReadAsync(work, selected);
            var editable = await work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.content_drops WHERE changeset_id=@id AND status IN ('draft','scheduled','failed'))", ("id", selected));
            return draft?.Status == "draft" && editable ? draft : null;
        }
        var ids = await work.QueryAsync("SELECT changeset_id FROM ultimateteam.content_changesets WHERE status='open'", row => row.GetInt64(0));
        return ids.Count == 0 ? null : await ReadAsync(work, ids[0]);
    }

    public static async ValueTask<ContentChangeset?> ReadAsync(IUnitOfWork work, long id)
    {
        var rows = await work.QueryAsync("""
            SELECT status, note, created_by, created_at, published_at, reverts_changeset_id FROM ultimateteam.content_changesets WHERE changeset_id=@id
            """, row => (Status: row.GetString(0), Note: row.IsDBNull(1) ? null : row.GetString(1), By: row.IsDBNull(2) ? null : row.GetString(2),
                Created: row.GetFieldValue<DateTimeOffset>(3), Published: row.IsDBNull(4) ? (DateTimeOffset?)null : row.GetFieldValue<DateTimeOffset>(4),
                Reverts: row.IsDBNull(5) ? (long?)null : row.GetInt64(5)), ("id", id));
        if (rows.Count == 0) return null;
        var head = rows[0];
        return new ContentChangeset(id, head.Status, head.Note, head.By, head.Created, head.Published, head.Reverts, await ChangesAsync(work, id));
    }

    /// <summary>A changeset's changes, or only the change of one row when <paramref name="area"/> and <paramref name="key"/> are given.</summary>
    private static async ValueTask<IReadOnlyList<ContentChange>> ChangesAsync(IUnitOfWork work, long changesetId, string? area = null, JsonObject? key = null) =>
        await work.QueryAsync("""
            SELECT change_id, area, target_key::text, operation, before_value::text, after_value::text, updated_at
            FROM ultimateteam.content_changes
            WHERE changeset_id=@id AND (@area::text IS NULL OR (area=@area AND target_key=@key::jsonb)) ORDER BY change_id
            """, row => new ContentChange(row.GetInt64(0), row.GetString(1), (JsonObject)JsonNode.Parse(row.GetString(2))!,
            Operation(row.GetString(3)), row.IsDBNull(4) ? null : (JsonObject)JsonNode.Parse(row.GetString(4))!,
            row.IsDBNull(5) ? null : (JsonObject)JsonNode.Parse(row.GetString(5))!, row.GetFieldValue<DateTimeOffset>(6)), ("id", changesetId),
            ("area", area), ("key", key?.ToJsonString()));

    /// <summary>The operation a second edit of a pending row leaves: a pending insert stays an insert, and is dropped when hidden.</summary>
    private static ContentOperation? Merge(ContentOperation pending, ContentOperation edit) => (pending, edit) switch
    {
        (ContentOperation.Insert, ContentOperation.Hide) => null,
        (ContentOperation.Insert, _) => ContentOperation.Insert,
        (_, ContentOperation.Insert) => throw new ContentChangeException(409, "That row already exists; edit it instead."),
        _ => edit,
    };

    public static ContentOperation Operation(string text) => text switch
    {
        "insert" => ContentOperation.Insert,
        "update" => ContentOperation.Update,
        "hide" => ContentOperation.Hide,
        _ => throw new ContentChangeException(400, "operation is insert, update or hide."),
    };

    public static string Text(ContentOperation operation) => operation switch
    {
        ContentOperation.Insert => "insert",
        ContentOperation.Update => "update",
        _ => "hide",
    };

    private static void CheckLabel(string? value, string name, int max)
    {
        if (value is not null && value.Length > max) throw new ContentChangeException(400, $"{name} is at most {max} characters.");
    }

    // Failures that mean "this content cannot be published": refused changes, database invariants, and the loader's checks.
    private static bool IsContentFailure(Exception error) =>
        error is ContentChangeException or PostgresException or InvalidDataException or RuntimeSetupException or System.Text.Json.JsonException;

    /// <summary>Carries a successful dry run out of its transaction, which the throw rolls back.</summary>
    private sealed class DryRun(ContentValidation result, ContentSnapshot snapshot) : Exception("content validation dry run")
    {
        public ContentValidation Result { get; } = result;
        public ContentSnapshot Snapshot { get; } = snapshot;
    }
}
