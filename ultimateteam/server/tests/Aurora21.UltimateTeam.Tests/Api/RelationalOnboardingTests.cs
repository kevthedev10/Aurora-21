using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalOnboardingTests
{
    private const string G = "/ut/game/fifa21";
    private const long Now = 1700000000;

    private static TestRuntime Create(bool initialize = true, Action<string>? observer = null, string? connection = null)
    {
        if (connection is null)
        {
            connection = PostgresTestCluster.CreateInstalledDatabase();
            using var source = NpgsqlDataSource.Create(connection);
            RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
        }
        return TestRuntime.Create(connectionString: connection, relationalSlice: true, time: new TestRuntime.FixedTime(Now),
            commandObserver: observer, initializeRelationalClub: initialize);
    }

    private static Task<T> Read<T>(TestRuntime rt, Func<UnitOfWork, ValueTask<T>> read) => rt.Game.ReadAsync(read, CancellationToken.None);

    private static Task<long> Scalar(TestRuntime rt, string sql) => Read(rt, w => w.ScalarAsync<long>(sql));

    /// <summary>Onboarding routes have no local catch: a database failure propagates to the HTTP host after rollback.</summary>
    private static void LateFailure(Func<object> call) =>
        Assert.Equal("synthetic late onboarding failure", Assert.Throws<InvalidOperationException>(call).Message);

    /// <summary>
    /// Offer ids whose footballer is distinct and not already in the active squad: the squad slice
    /// rejects two cards of one footballer, and offers include other versions of the initial starters.
    /// </summary>
    private static async Task<List<long>> OffersAsync(TestRuntime rt)
    {
        var squad = rt.CallObject("GET", G + "/squad/active").Arr("players")!.OfType<JsonObject>()
            .Select(slot => slot["itemData"] is JsonObject item ? item.Int("resourceId") : 0).Where(id => id != 0);
        var offers = rt.CallObject("GET", G + "/loan/players").Arr("loans")!.OfType<JsonObject>()
            .Select(offer => (Offer: offer.Int("defId"), Resource: offer.Obj("itemData")!.Int("resourceId"))).ToList();
        var footballers = (await Read(rt, w => w.QueryAsync("""
            SELECT d.native_resource_id, p.footballer_id FROM ultimateteam.item_definitions d
            JOIN ultimateteam.player_card_definitions p ON p.definition_id = d.definition_id
            WHERE d.game_year = 2021 AND d.native_resource_id = ANY(@resources)
            """, r => (Resource: r.GetInt64(0), Footballer: r.GetInt64(1)),
            ("resources", squad.Concat(offers.Select(o => o.Resource)).Distinct().ToArray()))))
            .ToDictionary(f => f.Resource, f => f.Footballer);
        var taken = squad.Select(resource => footballers[resource]).ToHashSet();
        return offers.Where(o => taken.Add(footballers[o.Resource])).Select(o => o.Offer).ToList();
    }

    /// <summary>The actual onboarding route sequence, returning every client-visible response in order.</summary>
    private static async Task<List<(string Step, JsonNode Body)>> OnboardingFlowAsync(TestRuntime rt, IReadOnlyList<long> offers)
    {
        var responses = new List<(string, JsonNode)>();
        JsonNode Step(string name, string method, string path, JsonNode? body = null)
        {
            var result = rt.Call(method, path, body);
            responses.Add((name, result));
            return result;
        }
        var loanPlayers = Step("loan-players", "GET", G + "/loan/players");
        var mass = rt.CallObject("GET", G + "/userMassInfo?screen=futlivemsggen4");
        Assert.True(JsonNode.DeepEquals(loanPlayers, mass["loanPlayers"]));
        responses.Add(("mass-onboarding", mass["onboardingClientData"]!.DeepClone()));
        Step("progress-before", "GET", G + "/clientdata/onboarding");
        var signed = Step("sign", "PUT", $"{G}/loan/player/{offers[0]}").AsObject();
        Step("sign-repeat", "PUT", $"{G}/loan/player/{offers[0]}");
        var replacement = Step("sign-replacement", "PUT", $"{G}/loan/player/{offers[1]}").AsObject();
        Step("move-to-squad", "PUT", G + "/item",
            new JsonObject { ["itemData"] = new JsonArray(new JsonObject { ["id"] = replacement.Int("id"), ["pile"] = "squad" }) });
        var squad = (await Read(rt, w => w.ListSquadsAsync()))[0];
        squad["players"]![0]!["itemId"] = replacement.Int("id");
        Step("squad-save", "PUT", G + "/squad/1?onboarding=true", squad);
        Assert.Null(await Read(rt, w => w.GetItemAsync(signed.Int("id"))));
        var kits = Step("kits", "GET", G + "/onboarding/kits").AsObject();
        var home = kits.Arr("homeItemDataList")![0]!.AsObject().Int("resourceId");
        var away = kits.Arr("awayItemDataList")![0]!.AsObject().Int("resourceId");
        Step("kits-select", "POST", G + "/onboarding/kits", new JsonObject { ["homeKitId"] = home, ["awayKitId"] = away });
        var badges = Step("badges", "GET", G + "/onboarding/badges").AsObject();
        var badge = badges.Arr("badgeItemDataList")![0]!.AsObject().Int("resourceId");
        Step("badge-select", "POST", $"{G}/onboarding/badge/{badge}");
        Step("badge-repeat", "POST", $"{G}/onboarding/badge/{badge}");
        Step("loan-players-complete", "GET", G + "/loan/players");
        Step("progress", "GET", G + "/clientdata/onboarding");
        Step("loan-progress", "GET", G + "/clientdata/loanplayerreward");
        Step("squad-progress-put", "PUT", G + "/clientdata/squad",
            new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["key"] = 5, ["value"] = 0 }) });
        Step("squad-progress", "GET", G + "/clientdata/squad");
        var completed = rt.CallObject("GET", G + "/userMassInfo?screen=futlivemsggen4");
        foreach (var (section, path) in new[] { ("loanPlayers", "/loan/players"), ("onboardingClientData", "/clientdata/onboarding"),
            ("loanPlayerClientData", "/clientdata/loanplayerreward"), ("squadClientData", "/clientdata/squad") })
        {
            Assert.True(JsonNode.DeepEquals(rt.Call("GET", G + path), completed[section]), section);
        }
        Step("tutorial-put", "PUT", G + "/clientdata/tutorialpopups",
            new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["key"] = 2, ["value"] = 1 }) });
        return responses;
    }

    [Fact]
    public async Task OnboardingRoutesMatchLegacyResponsesAndReopenFromRelationalRows()
    {
        // Both catalogs are the same bundled content; footballer identity is read from the relational one.
        using var rt = Create();
        var offers = await OffersAsync(rt);
        using var legacy = TestRuntime.Create(time: new TestRuntime.FixedTime(Now));
        var expected = await OnboardingFlowAsync(legacy, offers);
        var actual = await OnboardingFlowAsync(rt, offers);
        Assert.Equal(expected.Select(e => e.Step), actual.Select(a => a.Step));
        foreach (var (step, body) in expected)
        {
            var observed = actual.Single(a => a.Step == step).Body;
            Assert.True(JsonNode.DeepEquals(body, observed), $"{step}: legacy {body.ToJsonString()} relational {observed.ToJsonString()}");
        }
        Assert.True(rt.Fut.Onboarding.IsComplete());
        // The chosen badge is granted: the onboarding row references the owned badge.
        var badgeItem = actual.Single(a => a.Step == "badge-select").Body.AsObject().Int("badgeId");
        Assert.Equal(new[] { badgeItem }, (await Read(rt, w => w.QueryAsync("SELECT badge_item_id FROM ultimateteam.onboarding WHERE club_id=@club", r => r.GetInt64(0)))).ToArray());
        Assert.Equal("activeBadge", (await Read(rt, w => w.GetItemAsync(badgeItem)))!.Str("itemState"));
        var state = await Read(rt, w => w.OnboardingAsync());
        Assert.Equal(await Read(legacy, w => w.OnboardingAsync()) with { KitItemIds = state.KitItemIds, KitChoices = state.KitChoices, StaleLoanItemIds = state.StaleLoanItemIds }, state);
        var legacyState = await Read(legacy, w => w.OnboardingAsync());
        Assert.Equal(legacyState.KitChoices, state.KitChoices);
        Assert.Equal(legacyState.StaleLoanItemIds, state.StaleLoanItemIds);
        Assert.True(JsonNode.DeepEquals(legacyState.KitItemIds, state.KitItemIds));
        Assert.Equal(2, await Scalar(rt, "SELECT count(*) FROM ultimateteam.onboarding_kit_choices WHERE club_id=@club AND item_id IS NOT NULL"));
        var reads = new[] { "/onboarding/kits", "/onboarding/badges", "/loan/players", "/clientdata/onboarding", "/clientdata/loanplayerreward", "/clientdata/squad" };
        var before = reads.Select(path => rt.Call("GET", G + path)).ToList();
        var connection = rt.Detach();
        using var reopened = Create(connection: connection);
        foreach (var (path, body) in reads.Zip(before))
        {
            Assert.True(JsonNode.DeepEquals(body, reopened.Call("GET", G + path)), path);
        }
        Assert.Equal(state, await Read(reopened, w => w.OnboardingAsync()) with { KitItemIds = state.KitItemIds, KitChoices = state.KitChoices, StaleLoanItemIds = state.StaleLoanItemIds });
        Assert.Equal(1, await Scalar(reopened, "SELECT value FROM ultimateteam.client_data WHERE club_id=@club AND kind='tutorialpopups' AND entry_key=2"));
        Assert.Equal(0, await Scalar(reopened, "SELECT count(*) FROM pg_tables WHERE schemaname='public'"));
    }

    [Fact]
    public async Task InitializationIsTypedBulkAndIdempotentAcrossReopen()
    {
        var inserts = 0;
        using var rt = Create(observer: sql => { if (sql.StartsWith("INSERT INTO ultimateteam.items(", StringComparison.Ordinal)) Interlocked.Increment(ref inserts); });
        Assert.Equal(1, inserts);
        var items = await Read(rt, w => w.ListItemsAsync());
        var players = items.Count(i => i.Str("itemType") == "player");
        Assert.Equal(Math.Max(rt.Game.Rules.Club.Squad.Starters, rt.Game.Config.InitialClubPlayerCount), players);
        Assert.Single(items, i => i.Str("itemType") == "manager");
        Assert.Equal(new[] { "activeAwayKit", "activeHomeKit" }, items.Where(i => i.Str("itemType") == "kit").Select(i => i.Str("itemState")).Order());
        // Chemistry styles and position modifiers come from packs: a new club starts without consumables.
        Assert.DoesNotContain(items, NativeItems.IsConsumable);
        var bindings = await Read(rt, w => w.StadiumSlotBindingsAsync());
        Assert.Equal(items.Single(i => i.Str("itemState") == "activeHomeKit").Int("id"), bindings.Int("3"));
        var squad = rt.CallObject("GET", G + "/squad/active");
        Assert.Equal(rt.Game.Rules.Club.Squad.Starters, squad.Arr("players")!.OfType<JsonObject>().Count(s => s["itemData"] is JsonObject));
        Assert.Equal(1, await Scalar(rt, "SELECT count(*) FROM ultimateteam.club_flags WHERE club_id=@club"));
        var account = await Read(rt, w => w.RelationalAccountAsync());

        var connection = rt.Detach();
        using var reopened = Create(connection: connection, observer: sql => { if (sql.StartsWith("INSERT INTO ultimateteam.items(", StringComparison.Ordinal)) Interlocked.Increment(ref inserts); });
        Assert.Equal(1, inserts);
        Assert.Equal(items.Count, (await Read(reopened, w => w.ListItemsAsync())).Count);
        Assert.Equal(account, await Read(reopened, w => w.RelationalAccountAsync()));
    }

    [Fact]
    public async Task RepeatedAndInvalidRequestsKeepTypedStateStable()
    {
        using var rt = Create();
        var offers = await OffersAsync(rt);
        var signed = rt.CallObject("PUT", $"{G}/loan/player/{offers[3]}");
        var account = await Read(rt, w => w.RelationalAccountAsync());
        Assert.True(JsonNode.DeepEquals(signed, rt.CallObject("POST", $"{G}/loan/player/{offers[3]}")));
        Assert.Equal(account, await Read(rt, w => w.RelationalAccountAsync()));
        Assert.Equal(404, rt.CallRaw("PUT", $"{G}/loan/player/{rt.Game.Rules.Onboarding.Loans.FirstOfferId + 1000}").Status);
        Assert.Equal(7, signed.Int("loans"));
        // FIFA shows a loan card's matches left from its contract, so every render sends the loan games there
        // (the stored contract stays 0): the sign reply, Unassigned and the single-item fetch.
        Assert.Equal(7, signed.Int("contract"));
        JsonObject Rendered(JsonNode body) => body["itemData"]!.AsArray().OfType<JsonObject>().Single(item => item.Int("id") == signed.Int("id"));
        foreach (var path in new[] { $"{G}/purchased/items", $"{G}/item/{signed.Int("id")}" })
        {
            var rendered = Rendered(rt.CallObject("GET", path));
            Assert.Equal(7, rendered.Int("loans"));
            Assert.Equal(7, rendered.Int("contract"));
            Assert.Equal(7, rendered.Int("contracts"));
        }
        Assert.Equal(0, (await Read(rt, w => w.QueryAsync("SELECT p.contracts FROM ultimateteam.player_items p JOIN ultimateteam.items i ON i.id=p.item_id WHERE i.native_item_id=@id", r => r.GetInt64(0), ("id", signed.Int("id"))))).Single());
        Assert.Equal("unassigned", (await Read(rt, w => w.GetItemAsync(signed.Int("id"))))!.Str("pile"));

        var kits = rt.CallObject("GET", G + "/onboarding/kits");
        var request = new JsonObject
        {
            ["homeKitId"] = kits.Arr("homeItemDataList")![0]!.AsObject().Int("resourceId"),
            ["awayKitId"] = kits.Arr("awayItemDataList")![0]!.AsObject().Int("resourceId"),
        };
        Assert.Equal(404, rt.CallRaw("POST", G + "/onboarding/kits", new JsonObject { ["homeKitId"] = request.Int("awayKitId"), ["awayKitId"] = request.Int("homeKitId") }).Status);
        Assert.Equal(400, rt.CallRaw("POST", G + "/onboarding/kits", new JsonArray()).Status);
        var selected = rt.CallObject("POST", G + "/onboarding/kits", request);
        Assert.True(JsonNode.DeepEquals(selected, rt.CallObject("POST", G + "/onboarding/kits", request)));
        Assert.Equal(new[] { request.Int("homeKitId"), request.Int("awayKitId") }, (await Read(rt, w => w.OnboardingAsync())).KitChoices);
        Assert.Equal(404, rt.CallRaw("POST", $"{G}/onboarding/badge/1").Status);
        Assert.False(rt.Fut.Onboarding.IsComplete());
        Assert.Equal(400, rt.CallRaw("PUT", G + "/clientdata/onboarding", new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["key"] = -1, ["value"] = 1 }) }).Status);
        // Only the kit markers the selection recorded (FIFA resumes at the badge from them); the rejected write added nothing.
        Assert.Equal(new SortedDictionary<long, long> { [1] = request.Int("homeKitId"), [2] = request.Int("awayKitId") }, await Read(rt, w => w.ClientDataAsync("onboarding")));
        Assert.Equal(2, await Scalar(rt, "SELECT count(*) FROM ultimateteam.client_data WHERE club_id=@club"));

        // Choosing a different loan reopens later selections, exactly like the legacy state reset.
        var replacement = rt.CallObject("PUT", $"{G}/loan/player/{offers[4]}");
        var state = await Read(rt, w => w.OnboardingAsync());
        Assert.Equal(replacement.Int("id"), state.LoanItemId);
        Assert.Equal(offers[4], state.LoanOfferId);
        Assert.Empty(state.KitChoices);
        Assert.Empty(state.KitItemIds);
        Assert.Equal(new[] { signed.Int("id") }, state.StaleLoanItemIds);
        Assert.Equal(0, await Scalar(rt, "SELECT count(*) FROM ultimateteam.onboarding_kit_choices WHERE club_id=@club"));
    }

    [Fact]
    public async Task StaleLoansKeepOrderAndCleanupDeletesOnlyUnreferencedLoans()
    {
        using var rt = Create();
        var offers = await OffersAsync(rt);
        var first = rt.CallObject("PUT", $"{G}/loan/player/{offers[0]}").Int("id");
        var second = rt.CallObject("PUT", $"{G}/loan/player/{offers[1]}").Int("id");
        // Returning to an earlier offer grants a fresh loan and keeps both earlier ones as stale, in order.
        var again = rt.CallObject("PUT", $"{G}/loan/player/{offers[0]}").Int("id");
        Assert.NotEqual(first, again);
        Assert.Equal(new[] { first, second }, (await Read(rt, w => w.OnboardingAsync())).StaleLoanItemIds);
        var third = rt.CallObject("PUT", $"{G}/loan/player/{offers[2]}").Int("id");
        Assert.Equal(new[] { first, second, again }, (await Read(rt, w => w.OnboardingAsync())).StaleLoanItemIds);
        Assert.Equal(new long[] { first, second, again }, await Read(rt, w => w.QueryAsync(
            "SELECT wire_item_id FROM ultimateteam.onboarding_stale_loans WHERE club_id=@club ORDER BY ordinal", r => r.GetInt64(0))));

        // A save that does not reference the current loan leaves every stale loan in place.
        var squad = (await Read(rt, w => w.ListSquadsAsync()))[0];
        squad["players"]![1]!["itemId"] = first;
        Assert.Equal(200, rt.CallRaw("PUT", G + "/squad/1", squad).Status);
        Assert.NotNull(await Read(rt, w => w.GetItemAsync(second)));
        Assert.Equal(3, await Scalar(rt, "SELECT count(*) FROM ultimateteam.onboarding_stale_loans WHERE club_id=@club"));

        squad["players"]![0]!["itemId"] = third;
        Assert.Equal(200, rt.CallRaw("PUT", G + "/squad/1", squad).Status);
        Assert.Null(await Read(rt, w => w.GetItemAsync(second)));
        Assert.Null(await Read(rt, w => w.GetItemAsync(again)));
        Assert.NotNull(await Read(rt, w => w.GetItemAsync(first)));
        Assert.NotNull(await Read(rt, w => w.GetItemAsync(third)));
        var state = await Read(rt, w => w.OnboardingAsync());
        Assert.Equal(new[] { first }, state.StaleLoanItemIds);
        Assert.Equal(third, state.LoanItemId);
        Assert.Equal(new long[] { 0 }, await Read(rt, w => w.QueryAsync(
            "SELECT ordinal::bigint FROM ultimateteam.onboarding_stale_loans WHERE club_id=@club", r => r.GetInt64(0))));
    }

    [Fact]
    public async Task ChosenKitsAreGrantedAndDeletedOwnedKitsKeepChoicesWithoutOwnedReferences()
    {
        using var rt = Create(initialize: false);
        var kits = rt.CallObject("GET", G + "/onboarding/kits");
        var home = kits.Arr("homeItemDataList")![0]!.AsObject();
        var request = new JsonObject { ["homeKitId"] = home.Int("resourceId"), ["awayKitId"] = kits.Arr("awayItemDataList")![0]!.AsObject().Int("resourceId") };
        var selected = rt.CallObject("POST", G + "/onboarding/kits", request);
        // The chosen pair is granted untradeable and equipped; the choice references the owned kits.
        var ownedHome = (await Read(rt, w => w.GetItemAsync(selected.Int("homeKitId"))))!;
        Assert.Equal((request.Int("homeKitId"), "activeHomeKit"), (ownedHome.Int("resourceId"), ownedHome.Str("itemState")));
        Assert.True(ownedHome["untradeable"]!.GetValue<bool>());
        Assert.Equal(2, await Scalar(rt, "SELECT count(*) FROM ultimateteam.onboarding_kit_choices WHERE club_id=@club AND item_id IS NOT NULL"));
        var state = await Read(rt, w => w.OnboardingAsync());
        Assert.Equal(new[] { request.Int("homeKitId"), request.Int("awayKitId") }, state.KitChoices);
        Assert.True(JsonNode.DeepEquals(selected, state.KitItemIds));
        Assert.Equal(2, (await Read(rt, w => w.ListItemsAsync())).Count);
        // Choosing the same pair again answers the same kits and grants nothing.
        Assert.True(JsonNode.DeepEquals(selected, rt.CallObject("POST", G + "/onboarding/kits", request)));
        Assert.Equal(2, (await Read(rt, w => w.ListItemsAsync())).Count);

        // An owned equipped reference survives until that kit is deleted; the choice itself remains.
        var homeKit = selected.Int("homeKitId");
        await rt.Game.RunAsync("unequip", w => rt.Fut.Inventory.UpdatePileAsync(w, homeKit, "unassigned"), CancellationToken.None);
        Assert.Single((await rt.Fut.Inventory.DiscardItemsAsync(new JsonNode?[] { JsonValue.Create(homeKit) })).Deleted);
        state = await Read(rt, w => w.OnboardingAsync());
        Assert.Equal(new[] { request.Int("homeKitId"), request.Int("awayKitId") }, state.KitChoices);
        Assert.Null(state.KitItemIds["homeKitId"]);
        Assert.Equal(selected.Int("awayKitId"), state.KitItemIds.Int("awayKitId"));

        // Service-bypassing saves cannot bind a kit reference that is neither owned nor the role's default.
        var invalid = state with { KitItemIds = new JsonObject { ["homeKitId"] = rt.Game.Rules.Club.VirtualItemId(rt.Game.ClubId, "activeAwayKit") } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.Game.RunAsync("bad-kit", w => w.SaveOnboardingAsync(invalid), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.Game.RunAsync("bad-count", w => w.SaveOnboardingAsync(state with { KitChoices = new[] { request.Int("homeKitId") } }), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.Game.RunAsync("bad-field", w => w.SaveOnboardingAsync(state with { KitItemIds = new JsonObject { ["thirdKitId"] = 1 } }), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.Game.RunAsync("bad-definition", w => w.SaveOnboardingAsync(state with { KitChoices = new[] { 1L, 2L } }), CancellationToken.None));
        Assert.Equal(state.KitChoices, (await Read(rt, w => w.OnboardingAsync())).KitChoices);
    }

    [Fact]
    public async Task OnboardingRowsAreClubScopedAndDirectSqlRejectsForeignKitReferences()
    {
        using var rt = Create();
        var offers = await OffersAsync(rt);
        var loan = rt.CallObject("PUT", $"{G}/loan/player/{offers[0]}").Int("id");
        rt.CallObject("PUT", $"{G}/loan/player/{offers[1]}");
        var kits = rt.CallObject("GET", G + "/onboarding/kits");
        var request = new JsonObject { ["homeKitId"] = kits.Arr("homeItemDataList")![0]!.AsObject().Int("resourceId"), ["awayKitId"] = kits.Arr("awayItemDataList")![0]!.AsObject().Int("resourceId") };
        var equipped = rt.CallObject("POST", G + "/onboarding/kits", request);
        rt.Call("PUT", G + "/clientdata/onboarding", new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["key"] = 7, ["value"] = 7 }) });
        var local = await Read(rt, w => w.OnboardingAsync());

        var other = (await rt.Game.RunAsync("other", w => w.EnsureRelationalClubAsync(new ClubIdentity(900000011, 900000012), 0), CancellationToken.None)).Account.ClubId;
        var foreignState = new OnboardingState { LoanItemId = loan, LoanOfferId = 1, StaleLoanItemIds = new[] { loan, loan }, BadgeChoice = 9, Complete = true };
        await rt.Game.UnitOfWork.RunAsync(other, "other-onboarding", async w =>
        {
            await w.SaveOnboardingAsync(foreignState);
            await w.SaveClientDataAsync("onboarding", new SortedDictionary<long, long> { [7] = 1 });
        }, CancellationToken.None);
        // Another club cannot resolve this club's owned kit number as its equipped reference.
        var foreignKit = foreignState with { KitChoices = local.KitChoices, KitItemIds = new JsonObject { ["homeKitId"] = equipped.Int("homeKitId") } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rt.Game.UnitOfWork.RunAsync(other, "foreign-kit", w => w.SaveOnboardingAsync(foreignKit), CancellationToken.None));

        var reread = await Read(rt, w => w.OnboardingAsync());
        Assert.Equal(local with { KitItemIds = reread.KitItemIds, KitChoices = reread.KitChoices, StaleLoanItemIds = reread.StaleLoanItemIds }, reread);
        Assert.Equal(local.StaleLoanItemIds, reread.StaleLoanItemIds);
        Assert.True(JsonNode.DeepEquals(local.KitItemIds, reread.KitItemIds));
        Assert.False(rt.Fut.Onboarding.IsComplete());
        Assert.Equal(7, (await Read(rt, w => w.ClientDataAsync("onboarding")))[7]);
        var otherState = await rt.Game.UnitOfWork.RunAsync(other, "read-other", w => w.OnboardingAsync(), CancellationToken.None);
        Assert.Equal(new[] { loan, loan }, otherState.StaleLoanItemIds);
        Assert.Empty(otherState.KitChoices);

        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO ultimateteam.onboarding_kit_choices(club_id, role, ordinal, definition_id, item_id)
            SELECT @other, 'home', 0, i.definition_id, i.id FROM ultimateteam.items i WHERE i.club_id = @local AND i.native_item_id = @native
            """, connection, transaction);
        command.Parameters.AddWithValue("other", other);
        command.Parameters.AddWithValue("local", rt.Game.ClubId);
        command.Parameters.AddWithValue("native", equipped.Int("homeKitId"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Contains("does not match club and definition", error.MessageText);
    }

    [Fact]
    public async Task LateFailuresRollBackLoanGrantStaleRowsAndKitChoices()
    {
        string? failOn = null;
        using var rt = Create(observer: sql => { if (failOn is not null && sql.StartsWith(failOn, StringComparison.Ordinal)) throw new InvalidOperationException("synthetic late onboarding failure"); });
        var offers = await OffersAsync(rt);
        var signed = rt.CallObject("PUT", $"{G}/loan/player/{offers[0]}");
        var account = await Read(rt, w => w.RelationalAccountAsync());
        var state = await Read(rt, w => w.OnboardingAsync());
        var count = (await Read(rt, w => w.ListItemsAsync())).Count;

        failOn = "INSERT INTO ultimateteam.onboarding_stale_loans";
        LateFailure(() => rt.CallRaw("PUT", $"{G}/loan/player/{offers[1]}"));
        failOn = null;
        Assert.Equal(account, await Read(rt, w => w.RelationalAccountAsync()));
        Assert.Equal(count, (await Read(rt, w => w.ListItemsAsync())).Count);
        var after = await Read(rt, w => w.OnboardingAsync());
        Assert.Equal(state.LoanItemId, after.LoanItemId);
        Assert.Empty(after.StaleLoanItemIds);

        var kits = rt.CallObject("GET", G + "/onboarding/kits");
        var request = new JsonObject { ["homeKitId"] = kits.Arr("homeItemDataList")![0]!.AsObject().Int("resourceId"), ["awayKitId"] = kits.Arr("awayItemDataList")![0]!.AsObject().Int("resourceId") };
        var badge = rt.CallObject("GET", G + "/onboarding/badges").Arr("badgeItemDataList")![0]!.AsObject().Int("resourceId");
        failOn = "INSERT INTO ultimateteam.onboarding_kit_choices";
        LateFailure(() => rt.CallRaw("POST", G + "/onboarding/kits", request));
        failOn = "INSERT INTO ultimateteam.onboarding(";
        LateFailure(() => rt.CallRaw("POST", $"{G}/onboarding/badge/{badge}"));
        failOn = "INSERT INTO ultimateteam.client_data";
        LateFailure(() => rt.CallRaw("PUT", G + "/clientdata/onboarding", new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["key"] = 1, ["value"] = 1 }) }));
        failOn = null;
        after = await Read(rt, w => w.OnboardingAsync());
        Assert.Empty(after.KitChoices);
        Assert.False(after.Complete);
        Assert.Equal(0, after.BadgeChoice);
        Assert.Empty(await Read(rt, w => w.ClientDataAsync("onboarding")));
        // The granted kits and badge rolled back with the rest.
        Assert.Equal(count, (await Read(rt, w => w.ListItemsAsync())).Count);
        Assert.True(JsonNode.DeepEquals(signed, rt.CallObject("PUT", $"{G}/loan/player/{offers[0]}")));
    }

    [Fact]
    public async Task ConcurrentOnboardingWritesSerializeWithoutLostFields()
    {
        using var rt = Create();
        var offers = await OffersAsync(rt);
        var before = (await Read(rt, w => w.ListItemsAsync())).Count;
        var signed = await Task.WhenAll(offers.Take(4).Select(offer => Task.Run(() => rt.Fut.Onboarding.SignLoanAsync(offer).AsTask())));
        Assert.All(signed, result => Assert.Equal(200, result.Status));
        var state = await Read(rt, w => w.OnboardingAsync());
        var granted = signed.Select(result => ((JsonObject)result.Body!).Int("id")).ToHashSet();
        Assert.Equal(4, granted.Count);
        Assert.Equal(before + 4, (await Read(rt, w => w.ListItemsAsync())).Count);
        Assert.Equal(3, state.StaleLoanItemIds.Count);
        Assert.Equal(granted, state.StaleLoanItemIds.Append(state.LoanItemId).ToHashSet());

        var kits = rt.CallObject("GET", G + "/onboarding/kits");
        var request = new JsonObject { ["homeKitId"] = kits.Arr("homeItemDataList")![0]!.AsObject().Int("resourceId"), ["awayKitId"] = kits.Arr("awayItemDataList")![0]!.AsObject().Int("resourceId") };
        JsonObject Entry(int key) => new() { ["entries"] = new JsonArray(new JsonObject { ["key"] = key, ["value"] = key }) };
        await Task.WhenAll(Task.Run(() => rt.Fut.Onboarding.SelectKitsAsync(request).AsTask()),
            Task.Run(() => rt.Fut.Onboarding.SaveClientDataAsync("onboarding", Entry(10)).AsTask()),
            Task.Run(() => rt.Fut.Onboarding.SaveClientDataAsync("onboarding", Entry(11)).AsTask()));
        Assert.Equal(new[] { request.Int("homeKitId"), request.Int("awayKitId") }, (await Read(rt, w => w.OnboardingAsync())).KitChoices);
    }

    [Fact]
    public async Task IndependentRunnersOnOneClubDoNotLoseOnboardingOrClientDataWrites()
    {
        // The in-process club gate serializes one runner; a second runner (another process) relies on the
        // club row lock taken before each read-modify-write. The hook pauses the first runner before it
        // locks, after its unlocked work began, so the second runner's write lands inside that window.
        var armed = 0;
        using var rt = Create(observer: sql =>
        {
            if (Volatile.Read(ref armed) == 1 && sql.StartsWith("SELECT id FROM ultimateteam.clubs WHERE id = @club FOR UPDATE", StringComparison.Ordinal))
                Thread.Sleep(600);
        });
        var second = TestRuntime.Create(connectionString: rt.ConnectionString, relationalSlice: true, time: new TestRuntime.FixedTime(Now));
        try
        {
            Assert.Equal(rt.Game.ClubId, second.Game.ClubId);
            var kits = rt.CallObject("GET", G + "/onboarding/kits");
            var request = new JsonObject { ["homeKitId"] = kits.Arr("homeItemDataList")![0]!.AsObject().Int("resourceId"), ["awayKitId"] = kits.Arr("awayItemDataList")![0]!.AsObject().Int("resourceId") };
            JsonObject Entry(int key) => new() { ["entries"] = new JsonArray(new JsonObject { ["key"] = key, ["value"] = key }) };

            async Task Interleave(Func<Task> first, Func<Task> other)
            {
                Volatile.Write(ref armed, 1);
                var slow = Task.Run(first);
                await Task.Delay(150);
                await other();
                await slow;
                Volatile.Write(ref armed, 0);
            }
            var badge = rt.CallObject("GET", G + "/onboarding/badges").Arr("badgeItemDataList")![0]!.AsObject().Int("resourceId");
            await Interleave(() => rt.Fut.Onboarding.SelectKitsAsync(request).AsTask(), () => second.Fut.Onboarding.SelectBadgeAsync(badge).AsTask());
            var state = await Read(rt, w => w.OnboardingAsync());
            Assert.True(state.Complete);
            Assert.Equal(badge, state.BadgeChoice);
            Assert.Equal(new[] { request.Int("homeKitId"), request.Int("awayKitId") }, state.KitChoices);

            await Interleave(() => rt.Fut.Onboarding.SaveClientDataAsync("onboarding", Entry(1)).AsTask(),
                () => second.Fut.Onboarding.SaveClientDataAsync("onboarding", Entry(2)).AsTask());
            Assert.Equal(new long[] { 1, 2 }, (await Read(rt, w => w.ClientDataAsync("onboarding"))).Keys);
        }
        finally
        {
            second.Detach();
        }
    }
}
