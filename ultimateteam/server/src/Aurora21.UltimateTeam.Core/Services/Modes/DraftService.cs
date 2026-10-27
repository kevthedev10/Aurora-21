using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>
/// Single-player FUT Draft. Choice screens are generated from generators seeded by the run, so
/// reopening a screen (or a restart) reproduces the same choices.
/// </summary>
public sealed partial class DraftService
{
    private const string StatePath = "/ut/game/fifa21/squad/mode/draft/state";

    /// <summary>Squad pick ordinals: 0 is the captain, 1-10 the other pitch positions, 11-22 the bench.</summary>
    private const int PickCount = 23;

    // Seed offsets separating the independent generators of one run.

    private static readonly HashSet<string> States = new()
    {
        "INVALID", "PICK_DIFFICULTY", "FORMATION_DRAFT", "CAPTAIN_DRAFT",
        "PLAYER_DRAFT", "MANAGER_DRAFT", "READY_FOR_MATCH", "READY_FOR_REWARDS",
    };

    private readonly FutServices _fut;
    private readonly ILogger _log;

    public DraftService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Draft");
    }

    private GameContext Game => _fut.Game;
    private DraftRules Rules => Game.Rules.Draft;

    public long SquadId => Rules.SquadId;

    public JsonObject NewRun(string mode = "SINGLE_PLAYER")
    {
        var online = mode.ToUpperInvariant() == "ONLINE";
        return TemplateLibrary.Core.Object("draft/new-run", new TemplateArgs
        {
            ["mode"] = online ? "ONLINE" : "SINGLE_PLAYER",
            ["state"] = online ? "FORMATION_DRAFT" : "PICK_DIFFICULTY",
            ["difficulty"] = online ? 2 : -1,
            ["seed"] = GameRandom.Shared.NextInt64(1, 2_000_000_000),
            ["firstItemId"] = Rules.FirstItemId,
        });
    }

    // ---- requests ---------------------------------------------------------------

    /// <summary>
    /// A match end's Draft slot and whether the club's last created match was a Draft match, from one read.
    /// A normal match end then skips the Draft transaction and its exclusive club lock; the Draft path still
    /// checks the flag again under that lock.
    /// </summary>
    public Task<(DraftSlot Slot, bool LastMatchWasDraft)> MatchEndStateAsync(CancellationToken cancellationToken = default) =>
        Game.ReadAsync(uow => uow.MatchEndDraftStateAsync(), cancellationToken);

    /// <summary>
    /// Which Draft run a request belongs to. The mode in a purchase or squad path, the draft-state
    /// query, a Draft CreateMatch and the single-player offline squad name it (and all but the state
    /// query remember it); every other request uses the last named run.
    /// </summary>
    public async ValueTask<DraftSlot> SlotForAsync(FutRequest request, CancellationToken cancellationToken = default)
    {
        DraftSlot? named = null;
        if (DraftModePattern().Match(request.Lower) is { Success: true } mode)
        {
            named = mode.Groups[1].Value == "0" ? DraftSlot.Online : DraftSlot.SinglePlayer;
        }
        else if (request.Lower == StatePath)
        {
            return (request.Query.First("mode") ?? "SINGLE_PLAYER").ToUpperInvariant() == "ONLINE" ? DraftSlot.Online : DraftSlot.SinglePlayer;
        }
        else if (request.Lower == "/ut/game/fifa21/match" && request.Method == "POST" && request.PayloadObject is { } body
                 && JsonValues.Text(body.Get("squadId")) == SquadId.ToString(CultureInfo.InvariantCulture))
        {
            named = body.Str("type").ToUpperInvariant() == "ONLINE" ? DraftSlot.Online : DraftSlot.SinglePlayer;
        }
        else if (request.Lower.StartsWith("/ut/game/fifa21/offlinesquad", StringComparison.Ordinal))
        {
            named = DraftSlot.SinglePlayer;
        }
        if (named is { } slot)
        {
            await Game.RunAsync("draft-select-slot", uow => uow.SetLastDraftSlotAsync(slot), cancellationToken);
            return slot;
        }
        return await Game.ReadAsync(uow => uow.LastDraftSlotAsync(), cancellationToken);
    }

    /// <summary>
    /// POST /purchase/mode/{mode}/draft: pays the entry in coins or FIFA Points and starts a run. Other
    /// currencies (Draft tokens) are refused: the club has no token inventory to spend. A finished run whose rewards were never claimed is paid out first.
    /// </summary>
    public async ValueTask<FutResult> PurchaseEntryAsync(DraftSlot slot, bool singlePlayer, JsonNode? payload,
        CancellationToken cancellationToken = default)
    {
        return await Game.RunAsync("draft-purchase", async uow =>
        {
            await uow.LockClubAsync();
            if (await CurrentRunAsync(uow, slot) is { } finished && finished.Text("state") == "READY_FOR_REWARDS")
            {
                var unclaimed = await PayRunAsync(uow, finished, slot);
                await uow.ClearDraftRunAsync(slot);
                _log.LogInformation("Draft auto-award coins={Coins} before new purchase", unclaimed);
            }
            if (await CurrentRunAsync(uow, slot) is null)
            {
                var currency = (payload is JsonObject body ? JsonValues.Text(body.GetOr("currency", "COINS"), "None") : "COINS").ToUpperInvariant();
                if (currency is "COINS" or "CREDITS")
                {
                    if (await uow.CreditsAsync() < Rules.Entry.Coins)
                    {
                        return new FutResult((int)HttpStatusCode.Forbidden,
                            new JsonObject { ["reason"] = "insufficientCoins" }.Update(await ClubPresentationService.CreditsPayloadAsync(uow)));
                    }
                    await uow.AddCreditsToAsync(uow.ClubId, -Rules.Entry.Coins);
                }
                else if (currency is "POINTS" or "FIFAPOINTS" or "FIFA_POINTS")
                {
                    if (!Game.Rules.Club.FifaPointsPurchases)
                    {
                        // rules/club.json fifaPointsPurchases: no FIFA Points economy in phase 1.
                        _log.LogWarning("DRAFT_PURCHASE_REJECTED reason=points-disabled");
                        return FutResult.Coded(HttpStatusCode.BadRequest, "FIFA Points are not accepted");
                    }
                    if (await uow.FifaPointsAsync() < Rules.Entry.Points)
                    {
                        // The coin refusal's shape; the points reason text is not taken from a client capture.
                        return new FutResult((int)HttpStatusCode.Forbidden,
                            new JsonObject { ["reason"] = "insufficientPoints" }.Update(await ClubPresentationService.CreditsPayloadAsync(uow)));
                    }
                    await uow.AddFifaPointsAsync(-Rules.Entry.Points);
                }
                else
                {
                    _log.LogWarning("DRAFT_PURCHASE_REJECTED reason=unsupported-currency currency={Currency}", currency);
                    return FutResult.Coded(HttpStatusCode.BadRequest, $"unsupported Draft entry currency {currency}");
                }
                await SaveAsync(uow, NewRun(singlePlayer ? "SINGLE_PLAYER" : "ONLINE"), slot);
            }
            return FutResult.Ok(TemplateLibrary.Core.Array("draft/purchased", new TemplateArgs { ["credits"] = await uow.CreditsAsync() }));
        }, cancellationToken);
    }

    /// <summary>
    /// The single-player difficulty pick. It may be changed until the formation is picked; later it changes nothing: moving a
    /// drafted or playing run back to FORMATION_DRAFT let a second captain pick replace a drafted slot.
    /// </summary>
    public ValueTask<FutResult> ChooseDifficultyAsync(DraftSlot slot, JsonNode? payload, CancellationToken cancellationToken = default) =>
        WithRunAsync(slot, async (uow, run) =>
    {
        if (run.Text("mode") != "SINGLE_PLAYER" || run.Text("state") is not ("PICK_DIFFICULTY" or "FORMATION_DRAFT"))
        {
            _log.LogWarning("DRAFT DIFFICULTY ignored state={State} mode={Mode}", run.Text("state"), run.Text("mode"));
            return new JsonObject();
        }
        var requested = (payload is JsonObject body ? JsonValues.Text(body.GetOr("difficultyName", Rules.Difficulties[0]), "None") : Rules.Difficulties[0]).ToUpperInvariant();
        requested = Rules.DifficultyAliases.GetValueOrDefault(requested, requested);
        run["difficulty"] = Math.Max(0, Rules.Difficulties.IndexOf(requested));
        run["state"] = "FORMATION_DRAFT";
        await SaveAsync(uow, run, slot);
        return new JsonObject();
    }, cancellationToken: cancellationToken);

    public ValueTask<FutResult> ChoicesRequestAsync(DraftSlot slot, string kind, JsonObject? body,
        CancellationToken cancellationToken = default) => WithRunAsync(slot, async (uow, run) =>
    {
        var rawPosition = body?.GetOr("positionId", body.GetOr("positionid", 0));
        var positionId = JsonValues.HasValue(rawPosition) && !JsonValues.TryInt(rawPosition, out _) ? 0 : JsonValues.Int(rawPosition);
        return await ChoicesAsync(uow, run, slot, kind, positionId);
    }, cancellationToken: cancellationToken);

    public ValueTask<FutResult> ChooseRequestAsync(DraftSlot slot, JsonObject? body, CancellationToken cancellationToken = default) =>
        WithRunAsync(slot, async (uow, run) =>
    {
        long choiceIndex = 0;
        var positionId = run.Int("currentPositionId");
        if (body is not null
            && JsonValues.TryInt(body.GetOr("choiceIndex", 0), out var parsedChoice)
            && JsonValues.TryInt(body.GetOr("positionId", run.GetOr("currentPositionId", 0)), out var parsedPosition))
        {
            (choiceIndex, positionId) = (parsedChoice, parsedPosition);
        }
        // The lineup as FIFA shows it before this pick, one card id per slot, swaps made while drafting included.
        if (body?.Arr("swapPlayerDefIds") is { } lineup)
        {
            ApplyLayout(run, lineup.Take(PickCount).Select((node, index) => ((long)index, JsonValues.TryInt(node, out var id) ? id : 0)));
        }
        await ChooseAsync(uow, run, slot, choiceIndex, positionId);
        return new JsonObject();
    }, cancellationToken: cancellationToken);

    public ValueTask<FutResult> AutocompleteRequestAsync(DraftSlot slot, CancellationToken cancellationToken = default) =>
        WithRunAsync(slot, (uow, run) => AutocompleteAsync(uow, run, slot), cancellationToken: cancellationToken);

    /// <summary>
    /// The Draft squad, after applying <paramref name="rearrangement"/> when one is given.
    /// Draft rearrangements never replace the user's real active squad.
    /// </summary>
    public ValueTask<FutResult> SquadRequestAsync(DraftSlot slot, JsonNode? rearrangement,
        CancellationToken cancellationToken = default) => WithRunAsync(slot, async (uow, run) =>
    {
        if (rearrangement is not null)
        {
            await RearrangeAsync(uow, run, slot, rearrangement);
        }
        return await SquadAsync(uow, run);
    }, HttpStatusCode.NotFound, cancellationToken);

    /// <summary>
    /// POST /tactics entries for the Draft squad: kept on the run (one entry per mentality, a save replacing it) and
    /// sent back in its squad documents. Without an active run there is nothing to save them to.
    /// </summary>
    public async ValueTask<int> SaveTacticsAsync(DraftSlot slot, QueryValues query, JsonNode? payload,
        CancellationToken cancellationToken = default)
    {
        var entries = _fut.Inventory.TacticEntries(query, payload).Where(entry => entry.SquadId == SquadId).ToList();
        return await Game.RunAsync("draft-tactics", async uow =>
        {
            await uow.LockClubAsync();
            if (entries.Count == 0 || await CurrentRunAsync(uow, slot) is not { } run)
            {
                return 0;
            }
            var tactics = run.Obj("tactics") ?? new JsonObject();
            foreach (var (_, mentality, entry) in entries)
            {
                tactics[mentality] = entry;
            }
            run["tactics"] = tactics;
            await SaveAsync(uow, run, slot);
            return entries.Count;
        }, cancellationToken);
    }

    /// <summary>The run's saved custom tactics in mentality order, as FIFA reads them back from a squad document.</summary>
    private static void AddTactics(JsonObject squad, JsonObject run, List<string> mentalities)
    {
        squad.Remove("tactics");
        if (run.Obj("tactics") is not { Count: > 0 } saved)
        {
            return;
        }
        squad["tactics"] = new JsonArray(saved
            .OrderBy(entry => mentalities.IndexOf(entry.Key) is var index and >= 0 ? index : mentalities.Count)
            .Select(entry => entry.Value?.DeepClone())
            .ToArray());
    }

    /// <summary>Runs <paramref name="action"/> on the active run under the club gate, or answers <paramref name="notActive"/> when no Draft is active.</summary>
    private async ValueTask<FutResult> WithRunAsync(DraftSlot slot, Func<IUnitOfWork, JsonObject, ValueTask<JsonNode>> action,
        HttpStatusCode notActive = HttpStatusCode.Conflict, CancellationToken cancellationToken = default) =>
        await Game.RunAsync("draft-update", async uow =>
        {
            await uow.LockClubAsync();
            return await CurrentRunAsync(uow, slot) is { } run
                ? FutResult.Ok(await action(uow, run))
                : FutResult.Error((int)notActive, "DRAFT_NOT_ACTIVE");
        }, cancellationToken);

    // ---- run state ----------------------------------------------------------------

    /// <summary>
    /// The four or five pick rounds boosted to contain a 99-rated card (45% of runs by default),
    /// deterministic per run seed. Ordinal 0 is the captain; ordinals 1..10 map to positions 0..9.
    /// </summary>
    public IReadOnlyList<int> Elite99PickOrdinals(JsonObject run)
    {
        var random = GameRandom.Seeded("draft-elite99", RunSeed(run));
        if (random.NextDouble() >= Rules.Picks.Elite99Chance)
        {
            return Array.Empty<int>();
        }
        var count = random.Choice(Rules.Picks.Elite99RoundCounts);
        var eligible = Enumerable.Range(0, PickCount).Where(ordinal =>
        {
            if (ordinal == 0)
            {
                return true;
            }
            var allowed = AllowedPositions(run, ordinal <= 10 ? ordinal - 1 : ordinal);
            return allowed is null || Game.Players.NinetyNinePositions.Overlaps(allowed);
        }).ToList();
        return random.Sample(eligible, count).OrderBy(o => o).ToList();
    }

    /// <summary>The run's seed: every random Draft choice is derived from it, so a saved run replays identically.</summary>
    private static long RunSeed(JsonObject run) => run.IntKeep("seed", 1);

    /// <summary>The two or three intentionally weaker rounds of a draft.</summary>
    public IReadOnlyList<int> BadPickOrdinals(JsonObject run)
    {
        var random = GameRandom.Seeded("draft-weak-rounds", RunSeed(run));
        var count = random.Choice(Rules.Picks.WeakRoundCounts);
        var elite = Elite99PickOrdinals(run).ToHashSet();
        var available = Enumerable.Range(0, PickCount).Where(ordinal => !elite.Contains(ordinal)).ToList();
        return random.Sample(available, count).OrderBy(o => o).ToList();
    }

    /// <summary>The active run, with ids of migrated custom cards repaired.</summary>
    public async ValueTask<JsonObject?> CurrentRunAsync(IUnitOfWork uow, DraftSlot slot)
    {
        var run = await uow.DraftRunAsync(slot);
        if (run is null || !(JsonValues.IsString(run.Get("state")) && States.Contains(run.Text("state"))))
        {
            return null;
        }
        var items = (run.Obj("picked") ?? new JsonObject()).Select(entry => entry.Value as JsonObject)
            .Concat((run.Arr("currentChoices") ?? new JsonArray()).Select(choice => (choice as JsonObject)?.Obj("itemData")))
            .OfType<JsonObject>();
        var changed = false;
        foreach (var item in items)
        {
            var oldId = item.Int("resourceId");
            if (!Game.Players.ResourceIdMigrations.TryGetValue(oldId, out var newId))
            {
                continue;
            }
            item["resourceId"] = newId;
            if (item.IntOr("carddbid", oldId) == oldId)
            {
                item["carddbid"] = newId;
            }
            changed = true;
        }
        if (changed)
        {
            await uow.SaveDraftRunAsync(run, slot);
        }
        return run;
    }

    private static ValueTask<int> SaveAsync(IUnitOfWork uow, JsonObject run, DraftSlot slot) => uow.SaveDraftRunAsync(run, slot);

    /// <summary>This club's Online Draft squad as a match plays it, or null without an online run.</summary>
    public async ValueTask<JsonObject?> OnlineMatchSquadAsync(IUnitOfWork uow) =>
        await CurrentRunAsync(uow, DraftSlot.Online) is { } run ? await NativeMatchSquadAsync(uow, run) : null;

    /// <summary>
    /// The Draft squad of a persona (GET /squad/{Draft squad}/user/{persona}; Python server 51a1670): the caller's own run,
    /// or in an Online Draft pairing the opponent's, read from the opponent's own club. Null when there is none.
    /// </summary>
    public async ValueTask<JsonObject?> SquadForPersonaAsync(DraftSlot slot, long personaId, CancellationToken cancellationToken)
    {
        if (personaId == Game.Config.PersonaId)
        {
            return await Game.RunAsync("draft-squad-by-persona", async uow =>
                await CurrentRunAsync(uow, slot) is { } run ? await SquadAsync(uow, run) : null, cancellationToken);
        }
        if (_fut.Players?.MatchRoster?.OpponentOf(Game.Player.Id) is not { } online || online.OpponentPersonaId != personaId
            || _fut.Players.TryAcquireExisting(online.OpponentId) is not { } opponentLease)
        {
            return null;
        }
        using var lease = opponentLease;
        var peer = lease.Session.Fut;
        return await peer.Game.RunAsync("draft-peer-squad", async uow =>
        {
            var squad = await peer.Draft.OnlineMatchSquadAsync(uow);
            if (squad is not null)
            {
                squad["myStadiumInfo"] = await peer.Club.ClubStadiumAsync(uow, (squad.Arr("actives") ?? new JsonArray()).OfType<JsonObject>().ToList());
            }
            return squad;
        }, cancellationToken);
    }

    public async ValueTask<JsonObject> SquadAsync(IUnitOfWork uow, JsonObject run)
    {
        var actives = await _fut.SquadBattles.MatchActivesAsync(uow, opponent: false);
        return Squad(run, actives, await _fut.Club.HomeVenueAsync(uow, actives));
    }

    private JsonObject Squad(JsonObject run, IReadOnlyCollection<JsonObject> actives, long venue)
    {
        var picked = run.Obj("picked") ?? new JsonObject();
        var players = new JsonArray();
        var populated = new List<JsonObject>();
        for (var index = 0; index < PickCount; index++)
        {
            if (picked.Obj(index.ToString(CultureInfo.InvariantCulture)) is { } item)
            {
                players.Add(SquadDocuments.Slot(index, index + 1, item.DeepClone().AsObject(), Game.Cards));
                populated.Add(item);
            }
            else
            {
                players.Add(SquadDocuments.EmptySlot(index));
            }
        }
        var manager = run.Obj("manager");
        // The user's side keeps the club's own stadium item, and the flat aliases name the same venue: a home Draft game is
        // played at the club's stadium, while a CPU's home game loads the CPU's own (Bernabéu) stadium item. Stamping the
        // Bernabéu onto the club's stadium item id would stick in FIFA's item cache and move later home games there too.
        var squad = TemplateLibrary.Core.Object("draft/squad", new TemplateArgs
        {
            ["squadId"] = Rules.SquadId,
            ["personaId"] = Game.Config.PersonaId,
            ["squadName"] = Rules.SquadName,
            ["formation"] = JsonValues.Str(run.Get("formation"), Rules.DefaultFormation),
            ["captain"] = JsonValues.HasValue(run.Get("captain")) ? run.Int("captain") : populated.Count > 0 ? JsonValues.Int(populated[0].GetOr("id", 0)) : 0,
            ["players"] = players,
            ["managers"] = manager is null ? new JsonArray() : new JsonArray(manager.DeepClone()),
            ["actives"] = ClubPresentationService.ToArray(actives),
            ["stadiumId"] = venue,
        });
        AddTactics(squad, run, Game.Rules.Club.Squad.TacticMentalities);
        return squad;
    }

    /// <summary>
    /// The Draft editor squad projected onto the playable match contract: a valid active squad,
    /// five-star strength, kick takers and free items. Never replaces the user's real squad.
    /// </summary>
    public async ValueTask<JsonObject> NativeMatchSquadAsync(IUnitOfWork uow, JsonObject run)
    {
        var actives = await _fut.SquadBattles.MatchActivesAsync(uow, opponent: false);
        var venue = await _fut.Club.HomeVenueAsync(uow, actives);
        var squad = Squad(run, actives, venue);
        var formation = JsonValues.Str(squad.Get("formation"), Rules.DefaultFormation);
        var players = new JsonArray();
        foreach (var (rawSlot, index) in (squad.Arr("players") ?? new JsonArray()).Take(PickCount).Select((slot, index) => (slot, index)))
        {
            var slot = rawSlot is JsonObject slotObject ? slotObject.Copy() : new JsonObject { ["index"] = index };
            slot["index"] = slot.IntOr("index", index, index);
            slot["kitNumber"] = slot.IntOr("kitNumber", index + 1);
            if (slot.Obj("itemData") is { } rawItem)
            {
                var item = rawItem.Copy();
                item["itemState"] = "free";
                item["contract"] = Math.Max(1, item.Int("contract", 7));
                item["fitness"] = Math.Max(1, item.Int("fitness", 99));
                item["formation"] = formation;
                item["concept"] = false;
                item["untradeable"] = true;
                item.Remove("draftItem");
                slot["itemData"] = item;
            }
            players.Add(slot);
        }
        while (players.Count < PickCount)
        {
            players.Add(SquadDocuments.EmptySlot(players.Count));
        }

        var starters = players.Take(Game.Rules.Club.Squad.Starters).OfType<JsonObject>().Select(slot => slot.Obj("itemData")).OfType<JsonObject>().ToList();
        var starterIds = starters.Select(item => item.Int("id")).ToList();
        var captain = squad.Int("captain");
        if (!starterIds.Contains(captain) && starterIds.Count > 0)
        {
            captain = starterIds[0];
        }
        var managers = new JsonArray((squad.Arr("manager") ?? new JsonArray()).OfType<JsonObject>().Select(saved =>
        {
            var item = (saved.GetOr("itemData", saved) as JsonObject ?? saved).Copy();
            item["itemState"] = "free";
            item["concept"] = false;
            item["untradeable"] = true;
            return (JsonNode?)item;
        }).ToArray());
        var ratings = starters.Select(item => item.Int("rating")).ToList();

        squad.Update(TemplateLibrary.Core.Object("draft/match-squad", new TemplateArgs
        {
            ["squadId"] = Rules.SquadId,
            ["squadName"] = Rules.SquadName,
            ["formation"] = formation,
            ["captain"] = captain,
            ["rating"] = ratings.Count > 0 ? (long)Math.Round(ratings.Sum() / (double)ratings.Count, MidpointRounding.ToEven) : 75,
            ["custom"] = Game.Rules.Club.Squad.CustomTactics,
            ["players"] = players,
            ["managers"] = managers,
            ["kickTakers"] = SquadDocuments.KickTakers(SquadDocuments.PaddedKickTakerIds(starterIds.Where(id => id > 0), Game.Rules.Club.Squad.KickTakers)),
            ["actives"] = ClubPresentationService.ToArray(actives),
            ["stadiumId"] = venue,
        }));
        AddTactics(squad, run, Game.Rules.Club.Squad.TacticMentalities);
        return squad;
    }

    /// <summary>CardsDLL's current-state decoder expects a list; INVALID opens the entry screen.</summary>
    public async ValueTask<JsonArray> StatePayloadAsync(IUnitOfWork uow, JsonObject? run)
    {
        var args = new TemplateArgs
        {
            ["coins"] = Rules.Entry.Coins,
            ["draftTokens"] = Rules.Entry.DraftTokens,
            ["points"] = Rules.Entry.Points,
        };
        if (run is null)
        {
            return TemplateLibrary.Core.Array("draft/state-no-run", args);
        }
        var state = run.Text("state", "INVALID");
        args["wins"] = run.Int("wins");
        args["state"] = state;
        args["stateParam1"] = state == "PLAYER_DRAFT" ? "PLAYER" : "INVALID";
        args["stateParam2"] = state is "MANAGER_DRAFT" or "READY_FOR_MATCH" or "READY_FOR_REWARDS"
            ? PickCount.ToString(CultureInfo.InvariantCulture)
            : run.Int("currentPositionId").ToString(CultureInfo.InvariantCulture);
        args["roundsInfo"] = RoundsInfo(run);
        args["squad"] = await SquadAsync(uow, run);
        return TemplateLibrary.Core.Array("draft/state", args);
    }

    /// <summary>
    /// One roundsInfo entry per round, each naming the picked difficulty. CardsDLL sets a single-player Draft match's
    /// DifficultyLevel from roundsInfo[min(gamesWonCurrentMatch, n - 1)].difficulty (0x180209b30) and from 6, Ultimate,
    /// when the list is empty, so every offline Draft match was played on Ultimate whatever the pick. The entry parser
    /// (0x1802ad034) maps BEGINNER..LEGENDARY to 0..5 and any other name to 6. opponentId and the scores are left out:
    /// their parser defaults (opponentId 1, scores -1) are what the empty list gave the match loader and the hub.
    /// Online Draft keeps the empty list: its matches have no AI. A run before the pick has no difficulty yet (-1).
    /// </summary>
    private JsonArray RoundsInfo(JsonObject run)
    {
        var difficulty = run.Int("difficulty");
        if (run.Text("mode") != "SINGLE_PLAYER" || difficulty < 0 || difficulty >= Rules.Difficulties.Count)
        {
            return new JsonArray();
        }
        return new JsonArray(Enumerable.Range(1, (int)Rules.WinsToComplete).Select(round => (JsonNode?)new JsonObject
        {
            ["round"] = round,
            ["difficulty"] = Rules.Difficulties[(int)difficulty],
        }).ToArray());
    }

    // ---- choices ------------------------------------------------------------------

    /// <summary>Positions accepted by a pitch or bench slot (null means any position).</summary>
    public IReadOnlySet<string>? AllowedPositions(JsonObject run, long positionId)
    {
        if (Rules.BenchSlots.TryGetValue(positionId, out var bench))
        {
            return bench.ToHashSet();
        }
        var positions = Rules.FormationPositions(JsonValues.Str(run.Get("formation"), Rules.DefaultFormation)) ?? Array.Empty<string>();
        if (positionId < 0 || positionId >= positions.Count)
        {
            return null;
        }
        var target = positions[(int)positionId];
        return (Rules.PositionAliases.GetValueOrDefault(target) ?? new List<string> { target }).ToHashSet();
    }

    /// <summary>
    /// Which cards a run may offer (rules/draft.json picks.scheduledPool): the standard base cards and scheduled promos,
    /// or every available card. Every Draft offer goes through here; the grant-only check
    /// stays separate.
    /// </summary>
    public Func<long, long, bool> PoolFilter()
    {
        var draw = Game.CaptureCardDrawAt(Game.Time.GetUtcNow());
        var available = draw.General;
        if (!Rules.Picks.ScheduledPool)
        {
            return (resourceId, _) => available(resourceId);
        }
        return (resourceId, rareflag) => available(resourceId)
            && (_fut.Pools.IsBase(resourceId) || Game.Players.Find(resourceId)?.IsSpecial == true || rareflag > 1);
    }

    public List<JsonObject> PlayerChoices(JsonObject run, long positionId, bool captain = false)
    {
        var picks = Rules.Picks;
        var players = Game.Players;
        var draw = Game.CaptureCardDrawAt(Game.Time.GetUtcNow());
        var seenAssets = (run.Arr("seenAssets") ?? new JsonArray()).Select(node => JsonValues.TryInt(node, out var v) ? v : 0).ToHashSet();
        var allowed = captain ? null : AllowedPositions(run, positionId);
        Func<long, long, bool> inPool = (resource, _) => draw.General(resource);
        var candidates = players.Entries
            .Where(entry => inPool(entry.Key, entry.Value.Rareflag))
            .Select(entry => (Id: entry.Key, entry.Value.Rating, Asset: entry.Value.IntOr("assetId", entry.Key, entry.Key), Position: entry.Value.Position.ToUpperInvariant()))
            .Where(row => row.Rating >= picks.MinRating && !seenAssets.Contains(row.Asset) && row.Position.Length > 0 && !Game.Cards.IsGrantOnly(row.Id)
                          && (allowed is null || allowed.Contains(row.Position)))
            .Select(row => (row.Id, row.Rating, row.Asset))
            .ToList();

        var random = GameRandom.Seeded("draft-players", RunSeed(run), run.IntKeep("choiceSerial"), positionId);
        var chosen = new List<JsonObject>();
        var usedAssets = new HashSet<long>();
        var pickOrdinal = Math.Min(PickCount - 1, (run.Obj("picked") ?? new JsonObject()).Count);
        var elite99Pick = Elite99PickOrdinals(run).Contains(pickOrdinal);
        var weakPick = BadPickOrdinals(run).Contains(pickOrdinal);

        void Offer(long id, long asset)
        {
            chosen.Add(_fut.Native.DraftPlayerItem(_fut.Items.BuildPlayer(id, NextItemId(run), "none")));
            usedAssets.Add(asset);
            seenAssets.Add(asset);
        }

        // Guarantee a 99 on an elite round only when one naturally fits this slot.
        if (elite99Pick)
        {
            var elite = candidates
                .Where(row => row.Rating == 99 && (allowed is null || allowed.Contains(players.Find(row.Id)!.Text("preferredPosition").ToUpperInvariant())))
                .ToList();
            if (elite.Count > 0)
            {
                var (id, _, asset) = random.Choice(elite);
                Offer(id, asset);
                candidates = candidates.Where(row => row.Asset != asset).ToList();
            }
        }

        // Outside the weaker rounds keep the screen exciting, but only when enough natural-position
        // options remain above the floor.
        if (!weakPick)
        {
            var floor = captain ? picks.CaptainStrongFloor : picks.StrongFloor;
            var strong = candidates.Where(row => row.Rating >= floor).ToList();
            if (strong.Count >= picks.ChoicesPerScreen - chosen.Count)
            {
                candidates = strong;
            }
        }

        var specialBias = weakPick ? picks.WeakRoundSpecialBias : picks.SpecialBias;
        var configured = weakPick ? picks.WeakRoundCategoryWeights : picks.CategoryWeights;
        while (candidates.Count > 0 && chosen.Count < picks.ChoicesPerScreen)
        {
            var eligible = candidates.Select(row => row.Id).ToHashSet();
            var categories = draw.Selector.EligibleCategories(eligible, draw.ActivePromos)
                .Select(category => (Category: category, Weight: configured.GetValueOrDefault(category,
                    Store.PlayerPickDraw.IsStandard(category) ? 1 : specialBias))).Where(c => c.Weight > 0).ToArray();
            if (categories.Length == 0) throw new InvalidOperationException("No eligible Draft category has a positive weight.");
            var category = random.WeightedChoice(categories, categories.Select(c => c.Weight).ToList()).Category;
            // Draft's own Gold Rare curve (rules/draft.json picks); every other category keeps its saved group weights.
            var goldRareWeights = weakPick ? picks.WeakRoundGoldRareBucketWeights : picks.GoldRareBucketWeights;
            var id = draw.Selector.Pick(category, eligible, random,
                    bucketWeights: category == "gold_rare" && goldRareWeights.Count > 0 ? goldRareWeights : null, activePromoIds: draw.ActivePromos)
                ?? throw new InvalidOperationException("The Draft category has no eligible bucket.");
            var asset = candidates.First(row => row.Id == id).Asset;
            candidates = candidates.Where(row => row.Asset != asset).ToList();
            if (!usedAssets.Contains(asset))
            {
                Offer(id, asset);
            }
        }
        run["seenAssets"] = new JsonArray(seenAssets.OrderBy(v => v).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        run["choiceSerial"] = run.IntKeep("choiceSerial") + 1;
        _log.LogInformation("DRAFT WEIGHT pick={Pick} eliteProfile={Elite} guaranteed99={Guaranteed} badPick={Bad} ratings=[{Ratings}] rarities=[{Rarities}]",
            pickOrdinal, Elite99PickOrdinals(run).Count > 0, elite99Pick, weakPick,
            string.Join(", ", chosen.Select(item => item.Int("rating"))), string.Join(", ", chosen.Select(item => item.Int("rareflag"))));
        return chosen;
    }

    public List<JsonObject> ManagerChoices(JsonObject run)
    {
        var pool = Game.Managers.Entries
            .OrderBy(entry => -entry.Value.Int("rating"))
            .ThenBy(entry => entry.Key)
            .Select(entry => entry.Key)
            .Take(Rules.Picks.ManagerPoolSize)
            .ToList();
        GameRandom.Seeded("draft-managers", RunSeed(run)).Shuffle(pool);
        return pool.Take(Rules.Picks.ChoicesPerScreen)
            .Select(resourceId => _fut.Native.ManagerItem(_fut.Items.BuildManager(resourceId, NextItemId(run), "none")))
            .ToList();
    }

    private async ValueTask<JsonObject> ChoicesAsync(IUnitOfWork uow, JsonObject run, DraftSlot slot, string kind, long positionId = 0)
    {
        kind = kind.ToLowerInvariant();
        if (kind == "difficulty")
        {
            return new JsonObject
            {
                ["choices"] = new JsonArray(Rules.Difficulties.Select((name, index) => (JsonNode?)new JsonObject
                {
                    ["choiceIndex"] = index,
                    ["difficulty"] = index,
                    ["difficultyName"] = name,
                }).ToArray()),
            };
        }
        if (kind == "formation")
        {
            if ((run.Arr("formationChoices") ?? new JsonArray()).Count != Rules.FormationChoiceCount)
            {
                var random = GameRandom.Seeded("draft-formations", RunSeed(run));
                run["formationChoices"] = new JsonArray(random.Sample(Rules.FormationNames, Rules.FormationChoiceCount).Select(f => (JsonNode?)f).ToArray());
            }
            await SaveAsync(uow, run, slot);
            return new JsonObject
            {
                ["choices"] = new JsonArray(run.Arr("formationChoices")!.Select((node, index) => (JsonNode?)new JsonObject
                {
                    ["formation"] = node?.DeepClone(),
                    ["index"] = index,
                }).ToArray()),
                ["positionid"] = 0,
                ["tier"] = 0,
            };
        }

        if (kind == "player" && positionId is < 0 or >= PickCount)
        {
            // No such slot: AllowedPositions has no positions for it, so its offer took any position, and the pick then
            // landed in slot 0 or 22 (the goalkeeper's slot took outfield cards).
            _log.LogWarning("DRAFT CHOICES refused kind={Kind} positionId={Position}: no such slot", kind, positionId);
            return new JsonObject { ["choices"] = new JsonArray(), ["positionid"] = positionId, ["tier"] = 0 };
        }

        // Every unpicked slot keeps the first offer it was shown: regenerating on each request let
        // players re-roll by leaving the Draft or opening another slot and coming back.
        var offerKey = $"{kind}:{(kind is "captain" or "manager" ? 0 : positionId)}";
        if (run.Obj("pendingOffers") is not { } offers)
        {
            offers = new JsonObject();
        }
        List<JsonObject> items;
        if (offers.Arr(offerKey) is { Count: > 0 } offered && kind is "captain" or "player" or "manager")
        {
            items = offered.OfType<JsonObject>().Select(item => item.Copy()).ToList();
        }
        else
        {
            if (kind is "captain" or "player" or "manager")
            {
                await ReserveOfferIdsAsync(uow, run, 1);
            }
            items = kind switch
            {
                "captain" => PlayerChoices(run, 0, captain: true),
                "player" => PlayerChoices(run, positionId),
                "manager" => ManagerChoices(run),
                _ => new List<JsonObject>(),
            };
        }
        if (kind is "captain" or "player" or "manager" && items.Count > 0)
        {
            offers[offerKey] = ClubPresentationService.ToArray(items);
            run["pendingOffers"] = offers;
        }
        run["currentChoices"] = ClubPresentationService.ToArray(items);
        run["currentChoiceKind"] = kind;
        run["currentPositionId"] = positionId;
        await SaveAsync(uow, run, slot);
        return new JsonObject
        {
            ["choices"] = new JsonArray(items.Select((item, index) => (JsonNode?)new JsonObject
            {
                ["index"] = index,
                ["itemData"] = item.DeepClone(),
            }).ToArray()),
            ["positionid"] = positionId,
            ["tier"] = 0,
        };
    }

    /// <summary>
    /// Records a pick. The answered offer is kept: FIFA resumes a Draft at the slot it last picked
    /// and asks for that slot's choices again, and a fresh offer there would be a re-roll.
    /// </summary>
    private async ValueTask ChooseAsync(IUnitOfWork uow, JsonObject run, DraftSlot slot, long choiceIndex, long positionId)
    {
        JsonNode? Selected(JsonArray choices) =>
            (choiceIndex >= 0 && choiceIndex < choices.Count ? choices[(int)choiceIndex] : choices[0])?.DeepClone();

        var state = run.Text("state");
        // A pick takes only the offer of its own step: captain offers (any position, the captain floor) confirmed during
        // the player draft, or player offers during the manager pick, would fill a slot from the wrong screen.
        var expectedKind = state switch
        {
            "CAPTAIN_DRAFT" => "captain",
            "PLAYER_DRAFT" => "player",
            "MANAGER_DRAFT" => "manager",
            _ => null,
        };
        var choices = expectedKind is not null && run.Text("currentChoiceKind") == expectedKind
            ? run.Arr("currentChoices") ?? new JsonArray()
            : new JsonArray();
        switch (state)
        {
            case "FORMATION_DRAFT":
                var formations = run.Arr("formationChoices") ?? new JsonArray();
                run["formation"] = formations.Count > 0 ? Selected(formations) : Rules.DefaultFormation;
                run["state"] = "CAPTAIN_DRAFT";
                break;
            case "CAPTAIN_DRAFT" or "PLAYER_DRAFT" when choices.Count > 0:
                // A player pick goes to the slot its choices were generated for, not to another position the
                // confirmation names. Captain choices fit any position, so the confirmation places the captain.
                if (state == "PLAYER_DRAFT")
                {
                    positionId = run.Int("currentPositionId");
                }
                var picked = Picked(run);
                // The captain starts: it takes a pitch slot, never the bench.
                var lastSlot = state == "CAPTAIN_DRAFT" ? Game.Rules.Club.Squad.Starters - 1 : PickCount - 1;
                var slotKey = Math.Clamp(positionId, 0, lastSlot).ToString(CultureInfo.InvariantCulture);
                // FIFA resumes a Draft on the slot it last picked and offers it again; confirming there must not
                // replace the player already picked (Python tester #22). Picks are final.
                var selected = state == "PLAYER_DRAFT" && picked.Obj(slotKey) is { } existing ? existing.DeepClone() : Selected(choices);
                picked[slotKey] = selected;
                if (state == "CAPTAIN_DRAFT")
                {
                    run["captain"] = (selected as JsonObject)?.Int("id") ?? 0;
                    run["state"] = "PLAYER_DRAFT";
                }
                else if (picked.Count >= PickCount)
                {
                    run["state"] = "MANAGER_DRAFT";
                }
                break;
            case "MANAGER_DRAFT":
                if (choices.Count > 0)
                {
                    run["manager"] = Selected(choices);
                }
                run["state"] = "READY_FOR_MATCH";
                break;
        }
        run["currentChoices"] = new JsonArray();
        run["currentChoiceKind"] = string.Empty;
        run["currentPositionId"] = positionId;
        await SaveAsync(uow, run, slot);
    }

    /// <summary>
    /// "Complete my squad": fills every open pick and the manager. The reply is the filled squad
    /// object; the draft-state array left the button hanging and a bare {} showed an error.
    /// </summary>
    private async ValueTask<JsonNode> AutocompleteAsync(IUnitOfWork uow, JsonObject run, DraftSlot slot)
    {
        if (run.Text("state") is "READY_FOR_MATCH" or "READY_FOR_REWARDS")
        {
            // Nothing is left to draft. Setting READY_FOR_MATCH here put an eliminated or completed run back in play.
            return new JsonObject { ["squad"] = await SquadAsync(uow, run), ["squadState"] = run.Text("state") };
        }
        if (!JsonValues.HasValue(run.Get("formation")))
        {
            if ((run.Arr("formationChoices") ?? new JsonArray()).Count != Rules.FormationChoiceCount)
            {
                await ChoicesAsync(uow, run, slot, "formation");
            }
            run["formation"] = run.Arr("formationChoices")![0]?.DeepClone();
        }
        var open = Enumerable.Range(0, PickCount).Count(position => run.Obj("picked")?.ContainsKey(position.ToString(CultureInfo.InvariantCulture)) != true);
        await ReserveOfferIdsAsync(uow, run, open + (JsonValues.HasValue(run.Get("manager")) ? 0 : 1));
        for (var positionId = 0; positionId < PickCount; positionId++)
        {
            if (run.Obj("picked")?.ContainsKey(positionId.ToString(CultureInfo.InvariantCulture)) == true)
            {
                continue;
            }
            if (PlayerChoices(run, positionId).FirstOrDefault() is { } first)
            {
                Picked(run)[positionId.ToString(CultureInfo.InvariantCulture)] = first;
            }
        }
        if (!JsonValues.HasValue(run.Get("manager")))
        {
            run["manager"] = ManagerChoices(run).FirstOrDefault();
        }
        run["state"] = "READY_FOR_MATCH";
        run["pendingOffers"] = new JsonObject();
        run["currentChoices"] = new JsonArray();
        run["currentChoiceKind"] = string.Empty;
        await SaveAsync(uow, run, slot);
        return new JsonObject { ["squad"] = await SquadAsync(uow, run), ["squadState"] = run.Text("state") };
    }

    public JsonObject Stats(JsonObject? run)
    {
        var wins = run?.Int("wins") ?? 0;
        var eliminated = run?.Text("state") == "READY_FOR_REWARDS";
        var completed = wins >= Rules.WinsToComplete;
        return TemplateLibrary.Core.Object("draft/stats", new TemplateArgs
        {
            ["champion"] = completed ? 1 : 0,
            ["completed"] = eliminated || completed ? 1 : 0,
            // The run's losses: a completed run is READY_FOR_REWARDS too, and was shown a loss it never had.
            ["lost"] = run?.Int("losses") ?? 0,
            ["wins"] = wins,
        });
    }

    /// <summary>
    /// Pays any pending coin reward and ends the run. The reply lists the coins only (FIFA's traced award shape); a finished
    /// run's reward packs go to My Packs.
    /// </summary>
    public async ValueTask<JsonArray> GrantAwardAsync(DraftSlot slot, CancellationToken cancellationToken = default)
    {
        return await Game.RunAsync("draft-grant-award", async uow =>
        {
            // Paying and ending the run commit together, so a retry cannot pay the same run twice.
            await uow.LockClubAsync();
            var run = await CurrentRunAsync(uow, slot);
            if (run is not null && run.Text("state") != "READY_FOR_REWARDS")
            {
                // A claim on a run still going is a quit: as in retail, it ends the run with the reward of the wins so far.
                // It used to end the run and pay nothing.
                run["state"] = "READY_FOR_REWARDS";
                run["pendingCoinReward"] = RunPayout(run, slot).Coins;
                _log.LogInformation("Draft quit slot={Slot} wins={Wins}: paid as a finished run", slot, run.Int("wins"));
            }
            var reward = await PayRunAsync(uow, run, slot);
            await uow.ClearDraftRunAsync(slot);
            return reward != 0
                ? new JsonArray(new JsonObject { ["halId"] = 0, ["type"] = 0, ["value"] = reward })
                : new JsonArray();
        }, cancellationToken);
    }

    /// <summary>A finished run's reward by its wins and mode, its choice drawn from the run's seed (the Draft reward levels, migration 135).</summary>
    public DraftRunPayout RunPayout(JsonObject run, DraftSlot slot) =>
        Rules.RunReward(run.Int("wins"), slot == DraftSlot.Online, RunSeed(run));

    /// <summary>
    /// Pays a run's pending coins and, once it has finished, its reward packs to My Packs under the mode's grant source, each
    /// line with its own tradeability. Returns the coins paid. The caller ends the run in the same transaction.
    /// </summary>
    private async ValueTask<long> PayRunAsync(IUnitOfWork uow, JsonObject? run, DraftSlot slot)
    {
        var coins = run?.Int("pendingCoinReward") ?? 0;
        if (coins != 0)
        {
            await uow.AddCreditsToAsync(uow.ClubId, coins);
        }
        if (run?.Text("state") == "READY_FOR_REWARDS")
        {
            var payout = RunPayout(run, slot);
            foreach (var line in payout.Lines.Where(line => line.PackId is not null))
            {
                for (var copy = 0; copy < line.Count; copy++)
                {
                    await uow.AddUnopenedPackAsync(line.PackId!.Value, payout.Source, untradeable: line.Untradeable);
                }
            }
            _log.LogInformation("Draft run paid slot={Slot} wins={Wins} choice={Choice} coins={Coins} packs=[{Packs}] source={Source}",
                slot, run.Int("wins"), payout.Choice, coins, string.Join(", ", payout.Lines.Where(line => line.PackId is not null)
                    .Select(line => $"{(line.Count > 1 ? $"{line.Count}x" : "")}{line.PackId}{(line.Untradeable ? "u" : "")}")), payout.Source);
        }
        return coins;
    }

    /// <summary>
    /// Records that a Draft CreateMatch created the run's next match (the run's record so far names it): the first match end
    /// of that match settles it, and a replay of that end does not (<see cref="EndMatchAsync"/>). Only a run ready to play.
    /// </summary>
    public async ValueTask ArmMatchAsync(IUnitOfWork uow, DraftSlot slot, JsonObject run)
    {
        var key = MatchKey(slot, run, MatchCreatedStage);
        if (run.Text("state") == "READY_FOR_MATCH" && await uow.TryBeginMatchSettlementAsync(key))
        {
            await uow.CompleteMatchSettlementAsync(key, new JsonObject { ["draftMatch"] = MatchCreatedStage });
        }
    }

    private const string MatchCreatedStage = "created";
    private const string MatchEndedStage = "ended";

    /// <summary>
    /// A club match-settlement key for the run's match at its current record: the run (slot and seed) and the wins and
    /// losses so far. A match created at a record and the reply of the end that moved the run to a record are two stages.
    /// </summary>
    private static string MatchKey(DraftSlot slot, JsonObject run, string stage) => string.Create(CultureInfo.InvariantCulture,
        $"draft:{slot}:{RunSeed(run)}:{run.Int("wins")}-{run.Int("losses")}:{stage}");

    /// <summary>
    /// Settles a Draft match (enough wins complete the Draft; any other result eliminates it and pays
    /// the no-win consolation). Returns null unless the last CreateMatch was a Draft match with a
    /// run: another mode's match end must never settle the Draft.
    /// <para>
    /// FIFA sends some match ends twice, about 440 ms apart (Python server: every NO_CONTEST pair in the 09-25 journal),
    /// and each copy was settled and paid. The end that settles a created match stores its reply under the run's new
    /// record; an end arriving with no Draft CreateMatch since gets that reply back and counts nothing, however it names
    /// its game (an offline end names none). A genuine next match always follows a CreateMatch, which a search that
    /// ended without a game (DNF, gid 0) does not use up. An end for a run that is not ready to play (eliminated,
    /// completed or still drafting) with no reply stored is refused: it counts nothing and never falls through to a
    /// normal match's settlement.
    /// </para>
    /// </summary>
    public async ValueTask<JsonObject?> EndMatchAsync(DraftSlot slot, JsonNode? payload, CancellationToken cancellationToken = default)
    {
        // The run, objective progress and the response state commit before Blaze is told to refresh.
        string? leaver = null;
        var quietFirstBy = slot == DraftSlot.Online ? _fut.Matches.OpponentQuietFirstBy(payload as JsonObject ?? new JsonObject()) : null;
        var (response, refresh) = await Game.RunAsync("draft-match-end", async uow =>
        {
            leaver = null;
            await uow.LockClubAsync();
            if (!await uow.LastMatchCreateIsDraftAsync() || await CurrentRunAsync(uow, slot) is not { } run)
            {
                return ((JsonObject?)null, false);
            }
            var body = payload as JsonObject ?? new JsonObject();
            // Echo the client's own result: a rewritten endReason is flagged "match result under review".
            var reason = JsonValues.HasValue(body.Get("endReason")) ? body.Text("endReason") : "LOSS";
            var won = reason.ToUpperInvariant() is "WIN" or "DNF_WIN";
            if ((slot == DraftSlot.Online && reason.ToUpperInvariant() == "DNF" && NoGame(body.Get("gid")) && NoGame(body.Get("matchId")))
                || reason.ToUpperInvariant() == "NO_CONTEST")
            {
                // A search that ended without a game (cancelled, expired or refused) ends in DNF with gid 0 and no match
                // id, like a cancelled Rivals search (Python server 51a1670); a no-contest is a game called off before it
                // counted (5b8b918). No match was played: the run keeps its record, stays ready for the next match and
                // earns nothing.
                _log.LogInformation("Draft match end without a game ({Reason}) wins={Wins} state={State}", reason, run.Int("wins"), run.Text("state"));
                return (await NoResultAsync(uow, run, body, reason, eliminated: false), true);
            }
            if (slot == DraftSlot.SinglePlayer && reason.ToUpperInvariant() == "DNF" && body.Int("secondsPlayed") <= 0
                && run.Text("state") == "READY_FOR_MATCH")
            {
                // Offline, FIFA ends a match it leaves before kick-off (backing out of the pre-match screens to the squad) in
                // DNF with no game time; that was a loss that ended the run (live 10-07, cl8ve: DNF 4 s after the second
                // CreateMatch, eliminated after one win). No match was played: the run keeps its record and stays ready, as a
                // cancelled online search does. A quit after kick-off carries game time and still loses; killing the game
                // sends no end at all, so this lets nobody escape a result they could not already escape.
                await uow.TakeMatchClockAsync();
                _log.LogInformation("Draft match left before kick-off: no result slot={Slot} wins={Wins}", slot, run.Int("wins"));
                return (await NoResultAsync(uow, run, body, reason, eliminated: false), true);
            }
            if (await uow.MatchSettlementAsync(MatchKey(slot, run, MatchCreatedStage)) is null
                && await uow.MatchSettlementAsync(MatchKey(slot, run, MatchEndedStage)) is { } replay)
            {
                _log.LogWarning("DRAFT MATCH END repeat reason={Reason} gid={Gid} answered with the first reply wins={Wins} losses={Losses} state={State}",
                    reason, JsonValues.Text(body.Get("gid")), run.Int("wins"), run.Int("losses"), run.Text("state"));
                return (replay, false);
            }
            if (run.Text("state") != "READY_FOR_MATCH")
            {
                _log.LogWarning("DRAFT MATCH END refused reason={Reason} state={State} wins={Wins} losses={Losses}: the run is not ready to play",
                    reason, run.Text("state"), run.Int("wins"), run.Int("losses"));
                return (await NoResultAsync(uow, run, body, reason, eliminated: run.Text("state") == "READY_FOR_REWARDS"), false);
            }
            // Online, the player whose opponent left gets the reward gate's rule (rewardEligibility.opponentLeft), not the
            // client's DNF_WIN: no result leaves the run as it was, a loss ends it as any loss does; the leaver's loss is
            // settled in its own club after.
            var rules = Game.Rules.Seasons.RewardEligibility;
            var peeked = await uow.MatchClockAsync();
            var leftFacts = MatchEndFacts.From(body, peeked?.RealSeconds(uow.NowSeconds), rules, quietFirstBy);
            if (slot == DraftSlot.Online && leftFacts.OpponentLeft)
            {
                leaver = peeked?.OpponentPlayer;
                leftFacts = await _fut.Matches.NoteSerialLeaverAsync(uow, leftFacts, leaver);
                var (mine, theirs) = ObjectivesService.Goals(body);
                var stayer = RewardEligibility.StayerOutcome(leftFacts, mine, theirs);
                await _fut.Matches.RecordLeaveAsync(uow, leftFacts, peeked, RewardModes.DraftOnline, MatchKey(slot, run, MatchEndedStage), mine, theirs, stayer);
                won = stayer == MatchService.MatchOutcome.Win;
                if (stayer == MatchService.MatchOutcome.None)
                {
                    await uow.TakeMatchClockAsync();
                    _log.LogInformation("Draft opponent left: no result reason={Reason} minute={Minute} score={Mine}-{Theirs} wins={Wins}",
                        reason, leftFacts.Minute, mine, theirs, run.Int("wins"));
                    return (await NoResultAsync(uow, run, body, reason, eliminated: false), true);
                }
            }
            // A run win needs the reward gate's minimum match length, like the completion reward: a shorter win is a crafted
            // end (online, only the stayer of a left match wins earlier). No result leaves the run ready for its match.
            if (won && !leftFacts.LongEnough && !(slot == DraftSlot.Online && leftFacts.OpponentLeft))
            {
                await uow.TakeMatchClockAsync();
                _log.LogWarning("DRAFT MATCH END too short for a win: no result slot={Slot} reason={Reason} realSeconds={Real} wins={Wins}",
                    slot, reason, leftFacts.RealSeconds, run.Int("wins"));
                return (await NoResultAsync(uow, run, body, reason, eliminated: false), true);
            }
            long reward = 0;
            if (won)
            {
                run["wins"] = Math.Min(Rules.WinsToComplete, run.Int("wins") + 1);
                run["state"] = run.Int("wins") >= Rules.WinsToComplete ? "READY_FOR_REWARDS" : "READY_FOR_MATCH";
            }
            else
            {
                run["losses"] = run.Int("losses") + 1;
                run["state"] = "READY_FOR_REWARDS";
            }
            if (run.Text("state") == "READY_FOR_REWARDS")
            {
                // The run has ended, by a loss or by completing it: its reward by wins waits for the claim
                // (/draft/grant/award), or is paid before the next entry if never claimed.
                reward = RunPayout(run, slot).Coins;
                run["pendingCoinReward"] = reward;
            }
            await SaveAsync(uow, run, slot);
            // Each eligible Draft match pays its mode's flat completion coins and optional pack. The run reward
            // waits for the prizes screen's claim. Quits, short matches and online pair caps still gate completion
            // rewards; difficulty does not. The run's own record is unchanged by the completion gate.
            var seasons = Game.Rules.Seasons;
            var clock = await uow.TakeMatchClockAsync();
            // A serial leaver's early leave counts for the completion reward too, as it did for the run.
            var facts = MatchEndFacts.From(body, clock?.RealSeconds(uow.NowSeconds), seasons.RewardEligibility, quietFirstBy)
                with { SerialLeaver = leftFacts.SerialLeaver };
            var mode = slot == DraftSlot.Online ? RewardModes.DraftOnline : RewardModes.DraftSinglePlayer;
            var pairCapped = slot == DraftSlot.Online && await _fut.Matches.PairCappedAsync(uow, mode, clock?.OpponentPersona, null);
            var outcome = slot == DraftSlot.Online
                ? RewardEligibility.PairedOutcome(facts, won ? MatchService.MatchOutcome.Win : MatchService.MatchOutcome.Loss, pairCapped,
                    ObjectivesService.Goals(body).Mine, ObjectivesService.Goals(body).Theirs)
                : facts.Quitter || !won ? MatchService.MatchOutcome.Loss : MatchService.MatchOutcome.Win;
            var restricted = (await _fut.Matches.RestrictedCardsAsync(uow, MatchService.EndItemIds(body))).Count > 0;
            var decision = RewardEligibility.Decide(seasons, mode, facts, outcome, pairCapped, restricted);
            if (decision.CountsProgress)
            {
                await _fut.Objectives.RecordMatchEndAsync(uow, reason, body, won: outcome == MatchService.MatchOutcome.Win, draft: true, mode: decision.Mode);
            }
            var (award, earned, partials) = RewardEligibility.Coins(seasons, decision, outcome, body);
            var balance = await uow.AddCreditsToAsync(uow.ClubId, earned);
            await _fut.Matches.SettleRewardAsync(uow, decision, clock?.OpponentPersona, null, earned);

            var ended = TemplateLibrary.Core.Object("draft/match-ended", new TemplateArgs
            {
                ["reason"] = reason,
                ["secondsPlayed"] = body.Int("secondsPlayed"),
                ["matchDifficulty"] = JsonValues.Int(body.GetOr("matchDifficulty", run.GetOr("difficulty", 0))),
                ["items"] = body.Arr("items") ?? new JsonArray(),
                ["matchData"] = body.Str("matchData"),
                ["award"] = award,
                ["earned"] = earned,
                ["partials"] = partials,
                ["balance"] = balance,
                ["wins"] = run.Int("wins"),
                ["eliminated"] = !won || run.Int("wins") >= Rules.WinsToComplete,
                ["unopenedPacks"] = (await uow.UnopenedPackIdsAsync()).Count,
                ["won"] = won ? 1 : 0,
                ["lost"] = won ? 0 : 1,
            });
            foreach (var key in new[] { "myMatchStats", "opponentMatchStats", "myRating", "opponentRating" })
            {
                if (body.TryGetPropertyValue(key, out var value))
                {
                    ended[key] = value?.DeepClone();
                }
            }
            // The reply a repeat of this end gets, keyed by the record the run has now.
            var endedKey = MatchKey(slot, run, MatchEndedStage);
            if (await uow.TryBeginMatchSettlementAsync(endedKey))
            {
                await uow.CompleteMatchSettlementAsync(endedKey, ended);
            }
            _log.LogInformation("Draft match ended reason={Reason} wins={Wins} state={State} mode={Mode} verdict={Verdict} gameSeconds={GameSeconds} realSeconds={RealSeconds} matchCoins={Earned} runReward={Reward}",
                reason, run.Int("wins"), run.Text("state"), mode, decision.Verdict, facts.GameSeconds, facts.RealSeconds, earned, reward);
            return (ended, true);
        }, cancellationToken);
        if (refresh)
        {
            // Blaze pushes UserUpdated so FUT refreshes post-match state immediately.
            Game.UserSessionRefresh.Set();
        }
        if (leaver is not null)
        {
            await _fut.Matches.SettleLeaverAsync(leaver, payload as JsonObject ?? new JsonObject(), cancellationToken);
        }
        return response;
    }

    /// <summary>A match-end reply that counts nothing: no coins, the run's record unchanged.</summary>
    private async ValueTask<JsonObject> NoResultAsync(IUnitOfWork uow, JsonObject run, JsonObject body, string reason, bool eliminated) =>
        TemplateLibrary.Core.Object("draft/match-ended", new TemplateArgs
        {
            ["reason"] = reason,
            ["secondsPlayed"] = 0,
            ["matchDifficulty"] = JsonValues.Int(body.GetOr("matchDifficulty", run.GetOr("difficulty", 0))),
            ["items"] = new JsonArray(),
            ["matchData"] = "",
            ["award"] = 0,
            ["earned"] = 0,
            ["partials"] = null,
            ["balance"] = await uow.CreditsAsync(),
            ["wins"] = run.Int("wins"),
            ["eliminated"] = eliminated,
            ["unopenedPacks"] = (await uow.UnopenedPackIdsAsync()).Count,
            ["won"] = 0,
            ["lost"] = 0,
        });

    /// <summary>A match-end id that names no game: missing, empty or 0.</summary>
    internal static bool NoGame(JsonNode? value) =>
        !JsonValues.HasValue(value) || (JsonValues.TryInt(value, out var id) && id == 0);

    /// <summary>
    /// Draft rearrangements stay inside the run and never replace the user's squad. FIFA saves the Draft squad with bare
    /// <c>{id, dream}</c> cards, so the save is a layout of the run's own picks, never new card data (Python server 9ad75de).
    /// </summary>
    private async ValueTask RearrangeAsync(IUnitOfWork uow, JsonObject run, DraftSlot slot, JsonNode? payload)
    {
        if (payload is JsonObject body)
        {
            if (body.Arr("players") is { } supplied)
            {
                ApplyLayout(run, supplied.OfType<JsonObject>().Select(position => (
                    JsonValues.TryInt(position.Get("index"), out var index) ? index : -1,
                    JsonValues.TryInt(position.Obj("itemData")?.Get("id") ?? position.Get("itemId"), out var id) ? id : 0)));
            }
            if (JsonValues.HasValue(body.Get("formation")))
            {
                run["formation"] = JsonValues.Text(body.Get("formation"));
            }
            // The captain is named like the cards, by either id; the run keeps the pick's Draft item id.
            if (JsonValues.HasValue(body.Get("captain")) && JsonValues.Text(body.Get("captain")) is var captain && captain != "0"
                && Picked(run).Select(entry => entry.Value as JsonObject).OfType<JsonObject>().FirstOrDefault(item =>
                    item.Int("id").ToString(CultureInfo.InvariantCulture) == captain
                    || item.Int("resourceId").ToString(CultureInfo.InvariantCulture) == captain) is { } named)
            {
                run["captain"] = named.Int("id");
            }
        }
        await SaveAsync(uow, run, slot);
    }

    /// <summary>
    /// Moves the run's picks to the slots FIFA shows them in, pairing each slot index with the card in it. FIFA names a
    /// Draft card by its card id (resourceId); Draft item ids are accepted too, and ids that name no pick are ignored. A pick
    /// the lineup leaves out keeps its slot when that is still free and otherwise takes the first free one, so no pick is
    /// ever copied or dropped (Python server 9ad75de, _draft_apply_layout). Returns whether any pick moved.
    /// </summary>
    private static bool ApplyLayout(JsonObject run, IEnumerable<(long Index, long Id)> slots)
    {
        var picked = Picked(run);
        var slotOf = new Dictionary<long, string>();
        foreach (var (oldSlot, node) in picked)
        {
            if (node is JsonObject item)
            {
                foreach (var ident in new[] { item.Int("id"), item.Int("resourceId") })
                {
                    if (ident != 0)
                    {
                        slotOf.TryAdd(ident, oldSlot);
                    }
                }
            }
        }
        var rearranged = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var order = new List<string>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        void Place(string key, JsonNode? item)
        {
            rearranged[key] = item;
            order.Add(key);
        }
        foreach (var (index, ident) in slots)
        {
            var key = index.ToString(CultureInfo.InvariantCulture);
            if (!slotOf.TryGetValue(ident, out var oldSlot) || placed.Contains(oldSlot) || index is < 0 or >= PickCount || rearranged.ContainsKey(key))
            {
                continue;
            }
            Place(key, picked[oldSlot]);
            placed.Add(oldSlot);
        }
        if (placed.Count == 0)
        {
            return false;
        }
        foreach (var (oldSlot, node) in picked)
        {
            if (placed.Contains(oldSlot))
            {
                continue;
            }
            var target = !rearranged.ContainsKey(oldSlot) ? oldSlot
                : Enumerable.Range(0, PickCount).Select(index => index.ToString(CultureInfo.InvariantCulture)).FirstOrDefault(key => !rearranged.ContainsKey(key));
            if (target is null)
            {
                return false;
            }
            Place(target, node);
        }
        var moved = picked.Any(entry => !rearranged.TryGetValue(entry.Key, out var item) || !ReferenceEquals(item, entry.Value));
        run["picked"] = new JsonObject(order.Select(key => KeyValuePair.Create(key, rearranged[key]?.DeepClone())));
        return moved;
    }

    /// <summary>
    /// Reserves the item ids of the next offers from the club's own band (Python server 51a1670): with one shared range, two
    /// Online Draft squads in a match shared ids and FIFA's id-keyed item cache swapped their cards.
    /// </summary>
    private async ValueTask ReserveOfferIdsAsync(IUnitOfWork uow, JsonObject run, int offers)
    {
        if (offers > 0)
        {
            run["nextItemId"] = await uow.ReserveItemIdsAsync(offers * Rules.Picks.ChoicesPerScreen);
        }
    }

    private long NextItemId(JsonObject run)
    {
        var itemId = run.IntKeep("nextItemId", Rules.FirstItemId);
        run["nextItemId"] = itemId + 1;
        return itemId;
    }

    private static JsonObject Picked(JsonObject run)
    {
        if (run.Obj("picked") is not { } picked)
        {
            picked = new JsonObject();
            run["picked"] = picked;
        }
        return picked;
    }

    [GeneratedRegex(@"/(?:squad|purchase)/mode/(\d+)/draft")]
    private static partial Regex DraftModePattern();
}
