using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Panel;

/// <summary>
/// Game content (live-content-editing-plan.md, steps 1 and 3): the content revision this server runs; a reload that reads
/// the PostgreSQL catalogue and store packs and the bundled rules, SBC, chemistry and stadium files again through the
/// start-up loader and validation; and reviewed edits, which collect in one open changeset until the operator validates
/// and publishes them. A reload or publish that fails changes nothing; requests already running finish on the revision
/// they started with.
/// </summary>
public sealed partial class OperatorPanel
{
    private readonly ContentChangesets _changesets = new(game, PanelContentAreas(game, options.ContentAreas));

    private async Task<(int, object)?> ContentAsync(ApiRequest request)
    {
        var parts = request.Parts;
        var sub = string.Join('/', parts.Skip(1));
        try
        {
            var scheduleReply = await ScheduleApiAsync(request);
            if (scheduleReply is not null) return scheduleReply;
            var changesets = await RequestChangesetsAsync(request);
            switch (sub, request.Method)
            {
                case ("", "GET"):
                    return (200, ContentReport(game.Live.Current, previous: null, await DraftRunsInProgressAsync(request.Aborted)));
                case ("status", "GET"):
                    return (200, await ExternalContentStatusAsync(request.Aborted));
                case ("reload", "POST"):
                    return (200, await ReloadContentAsync(request));
                case ("changeset", "GET"):
                    return (200, ChangesetView(await changesets.OpenAsync(request.Aborted)));
                case ("changeset/changes", "PUT"):
                    return (200, ChangesetView(await SetChangeAsync(request)));
                case ("changeset/validate", "POST"):
                    if(request.Query("dropId") is { } selectedText && long.TryParse(selectedText,out var selectedId))
                    {
                        var selected=await Schedule.GetDropAsync(selectedId,request.Aborted);
                        return (200,ValidationView(await Schedule.ValidateAsync(selectedId,selected.Version,request.Aborted)));
                    }
                    return (200, ValidationView(await changesets.ValidateAsync(request.Aborted)));
                case ("changeset/publish", "POST"):
                    return (200, await PublishAsync(request));
                case ("changeset/discard", "POST"):
                    await changesets.DiscardAsync(request.Aborted);
                    _log.LogInformation("PANEL {Remote} discarded the pending content changes", request.Remote);
                    return (200, ChangesetView(null));
                case ("history", "GET"):
                    var history = await changesets.HistoryAsync(QueryInt(request, "limit", 20, 1, 200), request.Aborted);
                    return (200, new { changesets = history.Select(ChangesetJson) });
                case ("rules", "GET"):
                    return (200, await RuleFilesAsync(request));
                case ("modes/champions", "GET"):
                    return (200, ChampionsModeView());
                case ("modes/rivals", "GET"):
                    return (200, await RivalsModeViewAsync(request.Aborted));
                case ("modes/squad-battles", "GET"):
                    return (200, await SquadBattlesModeViewAsync(request.Aborted));
                case ("modes/draft", "GET"):
                    return (200, await DraftModeViewAsync(request.Aborted));
                case ("modes/leaderboards", "GET"):
                    return (200, LeaderboardsView());
                case ("rules/import", "POST"):
                    var imported = await changesets.ImportRulesAsync(TextField(request.Body, "file"), TextField(request.Body, "by"), request.Aborted);
                    _log.LogInformation("PANEL {Remote} imported the bundled rules files: {Count} change(s) staged", request.Remote, imported.Staged);
                    return (200, new { staged = imported.Staged, changeset = ChangesetView(imported.Open) });
                case ("" or "status" or "reload" or "changeset" or "changeset/changes" or "changeset/validate" or "changeset/publish" or "changeset/discard"
                    or "history" or "rules" or "rules/import" or "modes/champions" or "modes/rivals" or "modes/squad-battles" or "modes/draft" or "modes/leaderboards", _):
                    throw new PanelError(405, "method not allowed");
            }
            if (parts is [_, "rules", var ruleFile] && ruleFile != "import")
            {
                if (request.Method != "GET") throw new PanelError(405, "method not allowed");
                return (200, await RuleRowsAsync(request, ruleFile));
            }
            if (parts is [_, "changeset", "changes", var changeText] && long.TryParse(changeText, NumberStyles.None, CultureInfo.InvariantCulture, out var changeId))
            {
                if (request.Method != "DELETE") throw new PanelError(405, "method not allowed");
                return (200, ChangesetView(await changesets.RemoveChangeAsync(changeId, request.Aborted)));
            }
            if (parts is [_, "history", var idText, "revert"] && long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var revertId))
            {
                if (request.Method != "POST") throw new PanelError(405, "method not allowed");
                var opened = await changesets.RevertAsync(revertId, TextField(request.Body, "by"), request.Aborted);
                _log.LogInformation("PANEL {Remote} opened a revert of content changeset {Changeset}", request.Remote, revertId);
                return (200, ChangesetView(opened));
            }
            return null;
        }
        catch (ContentChangeException refused)
        {
            throw new PanelError(refused.Status, refused.Message,
                refused.Conflicts.Count == 0 ? null : new { conflicts = refused.Conflicts.Select(ConflictJson) });
        }
    }

    /// <summary><c>{area, key, operation, after, by}</c>: adds or replaces the pending change of one row.</summary>
    private async Task<ContentChangeset?> SetChangeAsync(ApiRequest request)
    {
        var body = request.Body;
        var area = TextField(body, "area") ?? throw new PanelError(400, "area is required.");
        var key = ObjectField(body, "key") ?? throw new PanelError(400, "key must be an object.");
        var operation = ContentChangesets.Operation(TextField(body, "operation") ?? "update");
        var after = ObjectField(body, "after");
        await RequireAvailabilityWindowAsync(request, area, key, after);
        var changed = await (await RequestChangesetsAsync(request)).SetChangeAsync(area, key, operation, after, TextField(body, "by"), request.Aborted);
        _log.LogInformation("PANEL {Remote} staged a content change: {Area} {Key} {Operation}", request.Remote, area, key.ToJsonString(),
            ContentChangesets.Text(operation));
        return changed;
    }

    private async Task<object> PublishAsync(ApiRequest request)
    {
        if (request.Query("dropId") is { } text && long.TryParse(text,out var dropId))
        {
            var drop=await Schedule.GetDropAsync(dropId,request.Aborted);
            await Schedule.RunOnceAsync(request.Aborted,dropId,drop.Version);
            var published = await game.UnitOfWork.RunReadAsync(0,work=>ContentChangesets.ReadAsync(work,drop.ChangesetId),request.Aborted);
            return new { published = ChangesetJson(published!), content = ContentReport(game.Live.Current,null,await DraftRunsInProgressAsync(request.Aborted)) };
        }
        var before = game.Live.Current;
        ContentPublishResult result;
        // Pack edits publish pack rules as well; one at a time keeps both in step.
        await _catalogEdits.WaitAsync(request.Aborted);
        try
        {
            result = await _changesets.PublishAsync(TextField(request.Body, "note"), TextField(request.Body, "by"), request.Aborted);
        }
        finally
        {
            _catalogEdits.Release();
        }
        _log.LogInformation("PANEL {Remote} published content changeset {Changeset}: revision {Before} -> {After}", request.Remote,
            result.Changeset.Id, before.Revision, result.Snapshot.Revision);
        return new
        {
            published = ChangesetJson(result.Changeset),
            content = ContentReport(result.Snapshot, before, await DraftRunsInProgressAsync(request.Aborted)),
        };
    }

    /// <summary>
    /// Modes → Champions (todo.md section 2): the published settings (rules/champions_rewards.json, edited as rules) and reward
    /// ranks (the champions_rank area), each rank with FIFA's tier number. Pending edits come from the changeset.
    /// </summary>
    private object ChampionsModeView()
    {
        var rewards = game.Rules.ChampionsRewards;
        return new
        {
            area = ChampionsRankArea.AreaName,
            settingsFile = "champions_rewards",
            // weekendLeague.maxMatches lives with the Weekend League schedule in seasons.json; the win ranks must cover 0 to it.
            seasonsFile = "seasons",
            leaderboardSize = game.Rules.Seasons.WeekendLeague.TopX,
            leaderboardMinimum = LeaderboardSizes.ChampionsMinimum,
            leaderboardMaximum = LeaderboardSizes.Maximum,
            settings = new
            {
                minMatchesToRank = rewards.MinMatchesToRank, minWinsForTop = rewards.MinWinsForTop, minWinsForTopFromEvent = rewards.MinWinsForTopFromEvent, fcPointsCap = rewards.FcPointsCap,
            },
            maxWins = game.Rules.Seasons.WeekendLeague.MaxMatches,
            clientWinTiers = ChampionsRewardRules.ClientWinTiers,
            clientTopBands = ChampionsRewardRules.ClientTopBands,
            maxTierLevel = ChampionsRankArea.MaxTierLevel,
            maxLineCount = RewardLine.MaxCount,
            ranks = rewards.Ranks.Select(rank =>
            {
                var row = ChampionsRankArea.ToJson(rank);
                row["tierLevel"] = rank.TierLevel;
                row["clientTier"] = rank.ClientTier;
                // What FIFA calls the rank: its own tier name for a win rank; a Top 200 band shows its positions.
                row["fifaName"] = rank.ByWins ? ChampionsRewardRules.ClientWinTierName(rank.ClientTier) : $"Band {rank.ClientTier}";
                return row;
            }).ToArray(),
        };
    }

    private object LeaderboardsView()
    {
        var rules = game.Rules;
        return new
        {
            area = RulesArea.AreaName,
            modes = new object[]
            {
                new { mode = "champions", size = rules.Seasons.WeekendLeague.TopX, minimum = LeaderboardSizes.ChampionsMinimum,
                    maximum = LeaderboardSizes.Maximum, key = new { file = "seasons", path = new[] { "weekendLeague", "topX" } } },
                new { mode = "squadBattles", size = rules.SquadBattles.Competition.BoardSize, minimum = LeaderboardSizes.ChampionsMinimum,
                    maximum = LeaderboardSizes.Maximum, key = new { file = "squad_battles", path = new[] { "competition", "boardSize" } } },
            },
            clientManifestPath = Aurora21.UltimateTeam.Server.Http.LeaderboardUiManifest.Path,
            clientManifest = Aurora21.UltimateTeam.Server.Http.LeaderboardUiManifest.Build(rules),
        };
    }

    /// <summary>
    /// Modes → Rivals (todo.md section 2): the published settings (rules/rivals_rewards.json, edited as rules), every division's six
    /// ranks with their weekly reward options (the rivals_rank area) and each division's promotion reward (rivals_promotion), in
    /// the shape those areas read. Pending edits come from the changeset.
    /// </summary>
    private Task<object> RivalsModeViewAsync(CancellationToken token) => game.UnitOfWork.RunReadAsync(0, async work =>
    {
        var rules = game.Rules.RivalsRewards;
        var tables = await ModeRewardRepository.RivalsTablesAsync(work);
        var score = rules.WeeklyScore;
        return (object)new
        {
            area = RivalsRankArea.AreaName,
            promotionArea = RivalsPromotionArea.AreaName,
            settingsFile = "rivals_rewards",
            settings = new
            {
                win = score.Win, draw = score.Draw, loss = score.Loss, perGoal = score.PerGoal, goalCap = score.GoalCap,
                matchCap = score.MatchCap, weekendLeagueCounts = score.WeekendLeagueCounts, weekendLeagueMatch = score.WeekendLeagueMatch,
                rankShares = rules.RankShares, poolBelowClubs = rules.PoolBelowClubs,
            },
            maxOptions = RivalsRewardRules.MaxRankOptions,
            maxLineCount = RewardLine.MaxCount,
            divisions = tables.Divisions.OrderBy(pair => pair.Key).Select(pair => new
            {
                division = pair.Key,
                ranks = pair.Value.Select((options, index) => new
                {
                    rank = index + 1, name = ModeRewardRepository.RivalsRankName(index + 1), row = RivalsRankArea.ToJson(options),
                }).ToArray(),
            }).ToArray(),
            promotion = tables.Promotion.OrderBy(pair => pair.Key).Select(pair => new { division = pair.Key, reward = pair.Value.ToJson() }).ToArray(),
        };
    }, token);

    /// <summary>
    /// Modes → Squad Battles (todo.md section 2): the published settings (rules/sqbt_rewards.json, and the Top players number in
    /// squad_battles.json competition.boardSize, edited as rules) and every tier (the squad_battles_tier area) in that area's shape:
    /// the six Top slots by position, named from their places, then the twelve points tiers by FIFA's names. Pending edits come
    /// from the changeset.
    /// </summary>
    private Task<object> SquadBattlesModeViewAsync(CancellationToken token) => game.UnitOfWork.RunReadAsync(0, async work =>
    {
        var rewards = game.Rules.SquadBattlesRewards;
        var tiers = await ModeRewardRepository.SquadBattlesTiersAsync(work);
        return (object)new
        {
            area = SquadBattlesTierArea.AreaName,
            settingsFile = "sqbt_rewards",
            boardFile = "squad_battles",
            settings = new
            {
                mode = rewards.Mode, minPointsToQualify = rewards.MinPointsToQualify,
                rankedEnabled = rewards.RankedTiers.Enabled, rankedMinParticipants = rewards.RankedTiers.MinParticipants,
            },
            // The playable Team of the Week (rules/sqbt_totw.json current, edited as a rule; 0 = no TOTW tile) and the archive.
            totwFile = "sqbt_totw",
            totw = new
            {
                current = game.Rules.Totw.Current,
                weeks = game.Rules.Totw.Weeks.OrderBy(week => week.Week).Select(week => new
                {
                    week = week.Week, released = week.Released,
                    headline = week.Starters.MaxBy(card => card.Rating) is { } best ? $"{best.Name} {best.Rating}" : null,
                }).ToArray(),
            },
            boardSize = game.Rules.SquadBattles.Competition.BoardSize,
            boardMinimum = LeaderboardSizes.ChampionsMinimum,
            boardMaximum = LeaderboardSizes.Maximum,
            maxLineCount = RewardLine.MaxCount,
            tiers = tiers.Select(tier =>
            {
                var row = SquadBattlesTierArea.ToJson(tier);
                row["tierId"] = tier.Id;
                row["name"] = tier.Ranked ? LeaderboardSizes.Name(tier.RankStart, tier.RankEnd) : ChampionsRewardRules.ClientWinTierName(tier.TierLevel);
                return row;
            }).ToArray(),
        };
    }, token);

    /// <summary>
    /// GET content/modes/draft: entry costs and active pick settings (edited as rules), the run length, and every reward
    /// level (the draft_level area) in that area's shape with its mode, wins and name: online first, by wins. Pending edits come
    /// from the changeset.
    /// </summary>
    private Task<object> DraftModeViewAsync(CancellationToken token) => game.UnitOfWork.RunReadAsync(0, async work =>
    {
        var draft = game.Rules.Draft;
        var catalog = game.Live.Current.Catalog;
        var goldRarePromos = catalog.Promos.Where(promo => promo.OddsCategory == "gold_rare").Select(promo => promo.PromoId).ToHashSet();
        var levels = await ModeRewardRepository.DraftLevelsAsync(work);
        return (object)new
        {
            area = DraftLevelArea.AreaName,
            settingsFile = "draft",
            settings = new { entryCoins = draft.Entry.Coins, entryPoints = draft.Entry.Points },
            pickSettings = new
            {
                choicesPerScreen = draft.Picks.ChoicesPerScreen, minRating = draft.Picks.MinRating,
                strongFloor = draft.Picks.StrongFloor, captainStrongFloor = draft.Picks.CaptainStrongFloor,
                managerPoolSize = draft.Picks.ManagerPoolSize, specialBias = draft.Picks.SpecialBias,
                weakRoundSpecialBias = draft.Picks.WeakRoundSpecialBias, elite99Chance = draft.Picks.Elite99Chance,
                elite99RoundCounts = draft.Picks.Elite99RoundCounts, weakRoundCounts = draft.Picks.WeakRoundCounts,
                goldRareBucketWeights = draft.Picks.GoldRareBucketWeights, weakRoundGoldRareBucketWeights = draft.Picks.WeakRoundGoldRareBucketWeights,
                categoryWeights = draft.Picks.CategoryWeights, weakRoundCategoryWeights = draft.Picks.WeakRoundCategoryWeights,
            },
            pickOptions = new
            {
                categories = catalog.Promos.Where(promo => promo.Status == "active" && promo.Code is not ("fut_champions" or "developer_cards"))
                    .Select(promo => promo.OddsCategory).Distinct().Order().ToArray(),
                goldRareGroups = catalog.PromoBuckets.Where(bucket => goldRarePromos.Contains(bucket.PromoId) && bucket.Enabled)
                    .Select(bucket => bucket.Code).Distinct().Order().ToArray(),
            },
            winsToComplete = draft.WinsToComplete,
            maxChoices = DraftRules.MaxChoices,
            maxWeight = DraftRules.MaxWeight,
            maxLineCount = RewardLine.MaxCount,
            levels = levels.Select(level =>
            {
                var row = DraftLevelArea.ToJson(level);
                row["mode"] = level.Mode;
                row["wins"] = level.Wins;
                row["name"] = ModeRewardRepository.DraftLevelName(level.Mode, level.Wins);
                return row;
            }).ToArray(),
        };
    }, token);

    /// <summary>The rules files the database holds (migration 073) and how many rules each has.</summary>
    private Task<object> RuleFilesAsync(ApiRequest request) => game.UnitOfWork.RunReadAsync(0, async work =>
    {
        var rows = await RuleRows.ReadAsync(work);
        return (object)new
        {
            area = RulesArea.AreaName,
            files = GameRules.DatabaseFiles.Select(file => new { file, rules = rows.TryGetValue(file, out var list)
                ? list.Count(row => BrowserRowVisible(request, file, row.Path)) : 0 })
                .Where(entry => request.Query("view") != "rules" || entry.rules > 0),
        };
    }, request.Aborted);

    private static bool BrowserRowVisible(ApiRequest request, string file, JsonArray path) =>
        (request.Query("view") != "rules" || RuleBrowser.Visible(file, path))
        && (request.Query("comments") != "0" || path[0] is not JsonValue first
            || !first.TryGetValue<string>(out var name) || !name.StartsWith('_'));

    /// <summary>
    /// One file's rules as rows (<c>?q=</c> matches the path or the value, <c>offset</c>/<c>limit</c> page them), each with
    /// its path (keys and list positions) and value.
    /// </summary>
    private Task<object> RuleRowsAsync(ApiRequest request, string file)
    {
        if (!GameRules.DatabaseFiles.Contains(file)) throw new PanelError(404, $"There are no {file} rules.");
        if (request.Query("path") is { Length: > 0 } pathText)
        {
            JsonArray path;
            try { path = JsonNode.Parse(pathText) as JsonArray ?? throw new JsonException(); }
            catch (JsonException) { throw new PanelError(400, "path must be a JSON list of keys and positions."); }
            var area = new RulesArea();
            var key = area.NormalizeKey(new JsonObject { ["file"] = file, ["path"] = path });
            return game.UnitOfWork.RunReadAsync(0, async work => (object)new
                { file, definition = await area.ReadAsync(work, key) }, request.Aborted);
        }
        var query = request.Query("q").Trim();
        var offset = QueryInt(request, "offset", 0, 0, 1_000_000);
        var limit = QueryInt(request, "limit", 100, 1, 500);
        return game.UnitOfWork.RunReadAsync(0, async work =>
        {
            var rows = (await RuleRows.ReadAsync(work, file)).GetValueOrDefault(file) ?? [];
            var visible = rows.Where(row => BrowserRowVisible(request, file, row.Path)).ToList();
            var matches = query.Length == 0 ? visible : visible.Where(row =>
                row.PathText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (row.Value?.ToJsonString() ?? "null").Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            return (object)new
            {
                file,
                total = matches.Count,
                offset,
                rules = matches.Skip(offset).Take(limit).Select(row => new { path = row.Path, value = row.Value }),
            };
        }, request.Aborted);
    }

    private static JsonObject? ObjectField(JsonElement body, string name) => Field(body, name) switch
    {
        null or { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.Object } value => JsonNode.Parse(value.GetRawText())!.AsObject(),
        _ => throw new PanelError(400, $"{name} must be an object."),
    };

    private object ChangesetView(ContentChangeset? open) => new
    {
        areas = _changesets.AreaNames,
        open = open?.Status is "open" or "draft",
        pending = open?.Status is "open" or "draft" ? open.Changes.Count : 0,
        changeset = open is null ? null : ChangesetJson(open),
    };

    private static object ChangesetJson(ContentChangeset changeset) => new
    {
        id = changeset.Id,
        status = changeset.Status,
        note = changeset.Note,
        createdBy = changeset.CreatedBy,
        createdAt = Iso(changeset.CreatedAt),
        publishedAt = Iso(changeset.PublishedAt),
        revertsChangesetId = changeset.RevertsChangesetId,
        changes = changeset.Changes.Select(change => new
        {
            id = change.Id,
            area = change.Area,
            key = change.Key,
            operation = ContentChangesets.Text(change.Operation),
            before = change.Before,
            after = change.After,
            updatedAt = Iso(change.UpdatedAt),
        }),
    };

    private static object ValidationView(ContentValidation validation) => new
    {
        ok = validation.Ok,
        changes = validation.Changes,
        conflicts = validation.Conflicts.Select(ConflictJson),
        errors = validation.Errors,
    };

    private static object ConflictJson(ContentConflict conflict) => new
    {
        changeId = conflict.ChangeId,
        area = conflict.Area,
        key = conflict.Key,
        expected = conflict.Expected,
        live = conflict.Live,
    };

    private async Task<object> ReloadContentAsync(ApiRequest request)
    {
        var before = game.Live.Current;
        ContentSnapshot after;
        // Pack edits publish pack rules as well; one at a time keeps both in step.
        await _catalogEdits.WaitAsync(request.Aborted);
        try
        {
            after = await game.ReloadContentAsync(request.Aborted);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogError(error, "PANEL {Remote} content reload failed; revision {Revision} stays current", request.Remote, before.Revision);
            throw new PanelError(422, $"The content did not load, so nothing changed (revision {before.Revision} stays current): {error.Message}");
        }
        finally
        {
            _catalogEdits.Release();
        }
        _log.LogInformation("PANEL {Remote} reloaded content: revision {Before} -> {After}, catalogue {Catalog}", request.Remote,
            before.Revision, after.Revision, after.Catalog.RevisionSha256 == before.Catalog.RevisionSha256 ? "unchanged" : "changed");
        return ContentReport(after, before, await DraftRunsInProgressAsync(request.Aborted));
    }

    /// <summary>Draft runs of every club that a reload reaches: their later choice screens draw from the reloaded content.</summary>
    private Task<long> DraftRunsInProgressAsync(CancellationToken cancellationToken) => game.UnitOfWork.RunReadAsync(0,
        work => work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.draft_runs WHERE state <> 'INVALID'"), cancellationToken);

    private Task<object> ExternalContentStatusAsync(CancellationToken token)
    {
        var loaded = game.Live.Current;
        return game.UnitOfWork.RunReadAsync(0, async work =>
        {
            var changes = await work.QueryAsync("SELECT table_name,revision,changed_at FROM ultimateteam.content_table_revisions WHERE revision>@loaded ORDER BY table_name",
                r => new { table = r.GetString(0), revision = r.GetInt64(1), changedAt = Iso(r.GetDateTime(2)) }, ("loaded", loaded.Catalog.DatabaseRevision));
            return (object)new { revision = loaded.Revision, loadedDatabaseRevision = loaded.Catalog.DatabaseRevision,
                externalChanges = changes.Count > 0, tables = changes, reloadRequired = changes.Count > 0,
                message = changes.Count > 0 ? "Database content changed outside this loaded revision. Review pending changes and reload explicitly." : "The loaded content is current." };
        }, token);
    }

    private static object ContentReport(ContentSnapshot content, ContentSnapshot? previous, long draftRuns)
    {
        var warnings = new List<string>();
        // SBC sets and stadiums fall back to empty when their file cannot be read (as at start-up); say so.
        if (previous is not null && previous.Sbc.Sets.Count > 0 && content.Sbc.Sets.Count == 0)
            warnings.Add("No SBC sets loaded: sbc.json could not be read (see the server log). SBCs are empty until it is fixed and reloaded.");
        return new
        {
            revision = content.Revision,
            loadedAt = Iso(content.LoadedAt),
            catalogSha256 = content.Catalog.RevisionSha256,
            previousRevision = previous?.Revision,
            catalogChanged = previous is null ? (bool?)null : previous.Catalog.RevisionSha256 != content.Catalog.RevisionSha256,
            counts = new
            {
                players = content.Players.Count,
                managers = content.Catalog.Managers.Count,
                consumables = content.Catalog.Consumables.Count,
                customisations = content.Catalog.Customisations.Count,
                sbcSets = content.Sbc.Sets.Count,
            },
            draftRunsInProgress = draftRuns,
            warnings,
            notes = new[]
            {
                "Applies to the next request of every club; requests already running finish on the previous revision.",
                "New clubs get the reloaded starter squad and kits; existing clubs keep theirs.",
                draftRuns == 0 ? "No Draft runs are in progress." : $"{draftRuns} Draft run(s) in progress: screens already shown stay as they are, later screens draw from the reloaded catalogue and Draft rules, and drafted cards show edited stats.",
                "What the game client shows before a player logs in again is unconfirmed.",
            },
        };
    }
}
