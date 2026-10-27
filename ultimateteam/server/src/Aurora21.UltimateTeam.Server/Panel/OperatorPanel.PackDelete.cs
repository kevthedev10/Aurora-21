using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Deleting a pack definition. Hide only takes a pack out of the store lineup; Delete removes the definition itself, staged
/// and published through the content changeset like every other pack edit, and refused while anything still uses it: the
/// lineup, a published rule that names it, or a player pick a club holds. Since migration 119 any pack may be deleted then.
/// </summary>
public sealed partial class OperatorPanel
{
    /// <summary>What still depends on one pack, read live.</summary>
    private sealed record PackDeleteReport(long Id, bool Exists, string? Name, string? Kind, bool AlwaysPresent, bool InLineup,
        long Copies, long Clubs, long HeldPicks, IReadOnlyList<string> References)
    {
        /// <summary>Blockers nothing can override: the definition is still in use.</summary>
        public IReadOnlyList<(string Code, string Message)> HardBlockers()
        {
            var list = new List<(string, string)>();
            if (!Exists) return [("not_found", "No such pack.")];
            if (Kind != "store")
                list.Add(("not_pack", "This row is the custom-pack template or a display-only reward name, not a pack."));
            if (AlwaysPresent) list.Add(("built_in", "This built-in pack must stay available, so it cannot be deleted."));
            if (InLineup)
                list.Add(("in_lineup", "This pack is in a published store lineup. Remove it from both lineups and publish that first, then delete it."));
            if (References.Count > 0)
                list.Add(("referenced", $"Rewards or rule tables still name this pack ({string.Join("; ", References.Take(5))}" +
                    $"{(References.Count > 5 ? $"; and {References.Count - 5} more" : "")}). Remove those references and publish that first."));
            if (HeldPicks > 0)
                list.Add(("held_picks", $"{HeldPicks.ToString(CultureInfo.InvariantCulture)} player pick item(s) held by clubs still draw from this pack's definition. Clubs must use them first."));
            return list;
        }

        public string CopiesMessage => $"{Copies.ToString(CultureInfo.InvariantCulture)} unopened cop{(Copies == 1 ? "y is" : "ies are")} owned by " +
            $"{Clubs.ToString(CultureInfo.InvariantCulture)} club{(Clubs == 1 ? "" : "s")}.";
    }

    private static async ValueTask<PackDeleteReport> InspectPackDeleteAsync(IUnitOfWork work, SbcCatalog sbc, long id)
    {
        var rows = await work.QueryAsync("""
            SELECT kind, always_present, (coalesce(enabled,true) AND NOT coalesce(admin_only,false)) OR EXISTS(SELECT 1 FROM ultimateteam.token_store_listings WHERE pack_id=@id AND listed), name FROM ultimateteam.store_packs WHERE pack_id=@id
            """, row => (Kind: row.GetString(0), Locked: row.GetBoolean(1), Lineup: row.GetBoolean(2), Name: row.IsDBNull(3) ? null : row.GetString(3)), ("id", id));
        if (rows.Count == 0) return new PackDeleteReport(id, false, null, null, false, false, 0, 0, 0, []);
        var owned = (await work.QueryAsync("SELECT count(*), count(DISTINCT club_id) FROM ultimateteam.unopened_packs WHERE pack_id=@id",
            row => (Copies: row.GetInt64(0), Clubs: row.GetInt64(1)), ("id", id)))[0];
        var picks = await work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.player_pick_items WHERE source_pack_id=@id", ("id", id));
        var references = new List<string>();
        foreach (var (file, text) in await RuleRows.DocumentsAsync(work))
            if (JsonNode.Parse(text) is { } document) PackReferences.Find(document, id, file, references);
        references.AddRange((await ModeRewardRepository.PackUsesAsync(work)).GetValueOrDefault(id) ?? []);
        references.AddRange(sbc.PackUses().GetValueOrDefault(id) ?? []);
        var row = rows[0];
        return new PackDeleteReport(id, true, row.Name, row.Kind, row.Locked, row.Lineup, owned.Copies, owned.Clubs, picks, references);
    }

    private async Task<object> PackDeleteCheckAsync(long id, CancellationToken token) => await game.UnitOfWork.RunReadAsync(0, async work =>
    {
        var report = await InspectPackDeleteAsync(work, game.Sbc, id);
        if (!report.Exists) throw new PanelError(404, "No such pack.");
        var blockers = report.HardBlockers().Select(blocker => new { code = blocker.Code, message = blocker.Message, overridable = false }).ToList();
        if (report.Copies > 0)
            blockers.Add(new { code = "owned_copies", message = report.CopiesMessage + " Wait for clubs to open them, or use Force delete.", overridable = true });
        var hard = blockers.Any(blocker => !blocker.overridable);
        return (object)new
        {
            id, name = report.Name, kind = report.Kind, revision = await PackRevisionAsync(work),
            blockers, canDelete = blockers.Count == 0, canForceDelete = !hard && report.Copies > 0,
            unopened = new { copies = report.Copies, clubs = report.Clubs },
        };
    }, token);

    /// <summary>
    /// Stages the deletion. A plain delete is refused while any club owns an unopened copy. A force delete also removes
    /// those copies when it is published: the operator must send back the copy and club counts they were shown.
    /// </summary>
    private async Task<object> StagePackDeleteAsync(ApiRequest request, long id)
    {
        if(request.Query("dropId").Length>0) throw new PanelError(400,"Pack definition deletion is an immediate operation; it cannot be scheduled.");
        var body = request.Body;
        var revision = RequiredRevision(body);
        var force = body.TryGetProperty("force", out var forceNode) && forceNode.ValueKind == JsonValueKind.True;
        long? confirmedCopies = null, confirmedClubs = null;
        if (force)
        {
            if (!body.TryGetProperty("copies", out var copies) || copies.ValueKind != JsonValueKind.Number || !copies.TryGetInt64(out var copyCount) ||
                !body.TryGetProperty("clubs", out var clubs) || clubs.ValueKind != JsonValueKind.Number || !clubs.TryGetInt64(out var clubCount))
                throw new PanelError(400, "A force delete needs the copies and clubs counts that were shown to the operator.");
            confirmedCopies = copyCount;
            confirmedClubs = clubCount;
        }
        string? by = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("by", out var byNode) && byNode.ValueKind == JsonValueKind.String ? byNode.GetString() : null;
        await _catalogEdits.WaitAsync(request.Aborted);
        try
        {
            var report = await game.UnitOfWork.RunReadAsync(0, async work =>
            {
                if (await PackRevisionAsync(work) != revision) throw new PanelError(409, "The pack catalogue changed. Refresh before saving.");
                return await InspectPackDeleteAsync(work, game.Sbc, id);
            }, request.Aborted);
            if (!report.Exists)
                throw new PanelError(404, "No such pack. A pack that is only staged as new is dropped under Changes instead.");
            if (report.HardBlockers() is { Count: > 0 } hard) throw new PanelError(409, string.Join(" ", hard.Select(blocker => blocker.Message)));
            if (report.Copies > 0 && !force)
                throw new PanelError(409, report.CopiesMessage + " Wait for clubs to open them, or use Force delete.");
            if (force && (report.Copies != confirmedCopies || report.Clubs != confirmedClubs))
                throw new PanelError(409, $"The unopened copies changed: {report.CopiesMessage} Review the counts and confirm again.",
                    new { unopened = new { copies = report.Copies, clubs = report.Clubs } });
            var changesets = await RequestChangesetsAsync(request);
            var open = request.Query("apply") == "1" ? null : await changesets.OpenAsync(request.Aborted);
            var other = force ? PanelPackDeleteArea.Plain : PanelPackDeleteArea.Force;
            // One delete per pack: switching between delete and force delete replaces the pending one.
            if (open?.Changes.FirstOrDefault(change => change.Area == other && change.Key["packId"]?.GetValue<long>() == id) is { } stale)
                await changesets.RemoveChangeAsync(stale.Id, request.Aborted);
            var staged = await changesets.SetChangeAsync(force ? PanelPackDeleteArea.Force : PanelPackDeleteArea.Plain,
                new JsonObject { ["packId"] = id }, ContentOperation.Hide, null, by, request.Aborted);
            _log.LogInformation("PANEL {Remote} staged {Kind} of pack {Pack} ({Copies} unopened copies, {Clubs} clubs)", request.Remote,
                force ? "force delete" : "delete", id, report.Copies, report.Clubs);
            return new
            {
                id, force, pending = staged?.Status == "published" ? 0 : staged?.Changes.Count ?? 0, applies = staged?.Status == "published" ? "live" : "after-review",
                unopened = new { copies = report.Copies, clubs = report.Clubs },
            };
        }
        finally { _catalogEdits.Release(); }
    }

    /// <summary>
    /// A store-pack deletion: the change's operation is hide and means "remove this definition" in this area. The
    /// force variant also removes every unopened copy and records a panel action receipt in the same publish transaction.
    /// </summary>
    private sealed class PanelPackDeleteArea(GameContext game, bool force) : IContentArea
    {
        public const string Plain = "store_pack_delete";
        public const string Force = "store_pack_force_delete";
        public string Name => force ? Force : Plain;
        // After store packs (10) and the store lineup (20), so a changeset that edits those first is checked against the result.
        public int ApplyOrder => 30;

        public JsonObject NormalizeKey(JsonObject key)
        {
            if (key["packId"] is not JsonValue value || !value.TryGetValue(out long id) || id is < 1 or > 32767)
                throw new ContentChangeException(400, "packId must be a FIFA 21 store pack id from 1 to 32767.");
            return new JsonObject { ["packId"] = id };
        }

        public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key)
        {
            var id = key["packId"]!.GetValue<long>();
            var rows = await work.QueryAsync("SELECT to_jsonb(p)::text FROM ultimateteam.store_packs p WHERE pack_id=@id",
                row => JsonNode.Parse(row.GetString(0))!.AsObject(), ("id", id));
            if (rows.Count == 0) return null;
            var read = new JsonObject { ["pack"] = rows[0] };
            if (!force) return read;
            // The counts the operator confirmed: a copy granted or opened since makes this change conflict at publish.
            var report = await InspectPackDeleteAsync(work, game.Sbc, id);
            read["unopened"] = new JsonObject { ["copies"] = report.Copies, ["clubs"] = report.Clubs };
            return read;
        }

        public async ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
        {
            if (operation != ContentOperation.Hide || after is not null)
                throw new ContentChangeException(400, "A pack deletion is a remove change without a value.");
            var report = await InspectPackDeleteAsync(work, game.Sbc, key["packId"]!.GetValue<long>());
            if (!report.Exists) throw new ContentChangeException(404, "No such pack.");
            var hard = report.HardBlockers();
            if (hard.Count > 0) throw new ContentChangeException(409, string.Join(" ", hard.Select(blocker => blocker.Message)));
            if (!force && report.Copies > 0)
                throw new ContentChangeException(409, report.CopiesMessage + " Wait for clubs to open them, or use Force delete.");
        }

        public async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
        {
            var id = key["packId"]!.GetValue<long>();
            if (force)
            {
                // Block new grants of this pack until the transaction ends, then take the copies that exist right now.
                await work.ExecuteAsync("LOCK TABLE ultimateteam.unopened_packs IN SHARE ROW EXCLUSIVE MODE");
                var name = (await InspectPackDeleteAsync(work, game.Sbc, id)).Name;
                var removed = await work.QueryAsync("DELETE FROM ultimateteam.unopened_packs WHERE pack_id=@id RETURNING club_id",
                    row => row.GetInt64(0), ("id", id));
                var perClub = new JsonArray(removed.GroupBy(club => club).OrderBy(group => group.Key)
                    .Select(group => (JsonNode?)new JsonObject { ["clubId"] = group.Key, ["copies"] = group.Count() }).ToArray());
                var open = await work.QueryAsync("SELECT changeset_id, created_by FROM ultimateteam.content_changesets WHERE status='open'",
                    row => (Id: row.GetInt64(0), By: row.IsDBNull(1) ? null : row.GetString(1)));
                var result = new JsonObject
                {
                    ["packId"] = id, ["name"] = name, ["copies"] = removed.Count, ["clubs"] = perClub.Count, ["perClub"] = perClub,
                    ["changesetId"] = open.Count > 0 ? open[0].Id : null, ["stagedBy"] = open.Count > 0 ? open[0].By : null,
                    ["source"] = "operator panel",
                };
                var text = result.ToJsonString();
                await work.ExecuteAsync("""
                    INSERT INTO ultimateteam.panel_action_receipts(request_id, operation, payload_hash, result)
                    VALUES(@request, 'pack.force_delete', @hash, @result::jsonb)
                    """, ("request", $"pack-force-delete-{id}-{Guid.NewGuid():N}"),
                    ("hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()), ("result", text));
            }
            // Purchase counters go with the pack: a deleted id can be reused by the next new pack, which must start from zero.
            await work.ExecuteAsync("DELETE FROM ultimateteam.club_pack_purchases WHERE pack_id=@id", ("id", id));
            await work.ExecuteAsync("DELETE FROM ultimateteam.community_pack_purchases WHERE pack_id=@id", ("id", id));
            if (await work.ExecuteAsync("DELETE FROM ultimateteam.store_packs WHERE pack_id=@id AND kind='store'", ("id", id)) == 0)
                throw new ContentChangeException(404, "No such pack.");
        }
    }
}

/// <summary>Finds where the rules documents name a pack id.</summary>
public static class PackReferences
{
    // Properties whose number, or list of numbers, is a pack id.
    private static readonly HashSet<string> IdKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "packId", "pack", "packs", "hardPacks", "neverPacks", "rewardPackId", "alwaysPresentPackIds", "packIds",
        "defaultRewardPack", "matchPackId", "sourcePackId", "sourcePackIds",
    };
    // Objects keyed by pack id, at any depth below.
    private static readonly HashSet<string> KeyedMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        "packs", "sourcePacks",
    };
    // Objects whose direct number values are pack ids.
    private static readonly HashSet<string> ValueMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        "rewardPacks", "legacyRewardPacks", "playerPickPacks",
    };

    public static void Find(JsonNode node, long id, string path, List<string> hits) => Visit(node, id.ToString(CultureInfo.InvariantCulture), id, path, hits, false, false);

    private static void Visit(JsonNode? node, string name, long id, string path, List<string> hits, bool keyed, bool valued)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key.StartsWith('_')) continue;
                    var here = $"{path}.{key}";
                    if (keyed && key == name) hits.Add(here);
                    var childKeyed = keyed || KeyedMaps.Contains(key);
                    var childValued = ValueMaps.Contains(key) && value is JsonObject;
                    if (valued && value is JsonValue number && IsId(number, id)) hits.Add(here);
                    else if (IdKeys.Contains(key) && IsIdOrList(value, id)) hits.Add(here);
                    else Visit(value, name, id, here, hits, childKeyed, childValued);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++) Visit(array[index], name, id, $"{path}[{index}]", hits, keyed, false);
                break;
        }
    }

    private static bool IsId(JsonValue value, long id) =>
        (value.TryGetValue<long>(out var number) && number == id) || (value.TryGetValue<int>(out var small) && small == id)
        || (value.TryGetValue<double>(out var real) && real == id);

    private static bool IsIdOrList(JsonNode? value, long id) => value switch
    {
        JsonValue scalar => IsId(scalar, id),
        JsonArray list => list.Any(item => item is JsonValue scalar && IsId(scalar, id)),
        _ => false,
    };
}
