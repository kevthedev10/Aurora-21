using System.Net;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Server.Access;
using Aurora21.UltimateTeam.Server.Panel;
using Aurora21.UltimateTeam.Server.Http;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Blaze.Matchmaking;
using Microsoft.Extensions.Logging.Abstractions;
using Aurora21.UltimateTeam.Tests.Api;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// The shared Aurora panel for Aurora 21: Aurora 16's page and its /panel/api contract, answered from an isolated
/// PostgreSQL club. Every request is a direct loopback one unless a test says otherwise.
/// </summary>
public sealed partial class OperatorPanelTests : IDisposable
{
    private const string DiscordId = "123456789012345678";
    private static readonly Protocol.FakeDiscordApi Discord = Protocol.FakeDiscordApi.WithPlayers(DiscordId);
    private readonly string _logs = Path.Combine(Path.GetTempPath(), "aurora21-panel-logs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RuleBrowserFiltersDocumentationAndReadsExactPaths()
    {
        using var setup = await CreateAsync();
        var all = await Send(setup.Panel, "GET", "/panel/api/content/rules/market?limit=500");
        var visible = await Send(setup.Panel, "GET", "/panel/api/content/rules/market?limit=500&comments=0");
        Assert.Equal(200, visible.Status);
        Assert.True(all.Json!["total"]!.GetValue<int>() > visible.Json!["total"]!.GetValue<int>());
        Assert.DoesNotContain(visible.Json["rules"]!.AsArray(), row => row!["path"]![0]!.GetValue<string>().StartsWith('_'));
        var exact = await Send(setup.Panel, "GET", "/panel/api/content/rules/market?path=" + Uri.EscapeDataString("[\"maintenanceIntervalSeconds\"]"));
        Assert.Equal(200, exact.Status);
        Assert.Equal(setup.Runtime.Game.Rules.Market.MaintenanceIntervalSeconds, exact.Json!["definition"]!["value"]!.GetValue<int>());
        var missing = await Send(setup.Panel, "GET", "/panel/api/content/rules/market?path=" + Uri.EscapeDataString("[\"synthetic_missing_key\"]"));
        Assert.Null(missing.Json!["definition"]);
        Assert.Equal(400, (await Send(setup.Panel, "GET", "/panel/api/content/rules/market?path=%5B%5D")).Status);
        Assert.Equal(400, (await Send(setup.Panel, "GET", "/panel/api/content/rules/market?path=bad-json")).Status);
    }

    [Fact]
    public async Task PromoBucketsHaveIndependentPagesAndKeepFilteredEmptySections()
    {
        using var setup = await CreateAsync();
        var catalog = setup.Runtime.Game.Live.Current.Catalog;
        var promo = catalog.Promos.Single(p => p.Code == "totw_1");
        var buckets = catalog.PromoBuckets.Where(b => b.PromoId == promo.PromoId && b.Enabled).ToArray();
        var target = buckets.First(b => catalog.PromoCards.Count(m => m.BucketId == b.Id) > 2);
        var reply = await Send(setup.Panel, "GET", $"/panel/api/cards?promo=totw_1&group=buckets&limit=2&bucketPage.{target.Code}=2");
        Assert.Equal(200, reply.Status);
        Assert.Empty(reply.Json!["cards"]!.AsArray());
        var sections = reply.Json["buckets"]!.AsArray();
        Assert.Equal(buckets.Length, sections.Count);
        Assert.Equal(catalog.PromoCards.Count(m => m.PromoId == promo.PromoId), sections.Sum(b => b!["count"]!.GetValue<int>()));
        foreach (var bucket in buckets)
        {
            var members = catalog.PromoCards.Where(m => m.BucketId == bucket.Id).Select(m => catalog.DefinitionsById[m.PlayerCardDefinitionId].NativeResourceId!.Value.Value).ToHashSet();
            var section = sections.Single(s => s!["code"]!.GetValue<string>() == bucket.Code)!;
            var offset = bucket.Id == target.Id ? 2 : 0;
            var expected = setup.Runtime.Game.Players.Entries.Where(e => members.Contains(e.Key)).OrderByDescending(e => e.Value.Rating)
                .ThenBy(e => e.Value.Text("name"), StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Key).Skip(offset).Take(2).Select(e => e.Key);
            Assert.Equal(offset, section["offset"]!.GetValue<int>());
            Assert.Equal(expected, section["cards"]!.AsArray().Select(c => c!["resourceId"]!.GetValue<long>()));
        }
        var empty = await Send(setup.Panel, "GET", "/panel/api/cards?promo=totw_1&group=buckets&q=synthetic_no_such_player");
        Assert.Equal(buckets.Length, empty.Json!["buckets"]!.AsArray().Count);
        Assert.All(empty.Json["buckets"]!.AsArray(), b => Assert.Equal(0, b!["total"]!.GetValue<int>()));
        Assert.Equal(400, (await Send(setup.Panel, "GET", "/panel/api/cards?group=buckets")).Status);
        Assert.Equal(400, (await Send(setup.Panel, "GET", $"/panel/api/cards?promo=totw_1&group=buckets&bucketPage.{target.Code}=invalid")).Status);
    }

    [Fact]
    public async Task PromoBrowserFiltersMembershipBeforeSearchAndPaging()
    {
        using var setup = await CreateAsync();
        var catalog = setup.Runtime.Game.Live.Current.Catalog;
        var promo = catalog.Promos.First(p => catalog.PromoCards.Count(c => c.PromoId == p.PromoId) > 3);
        var members = catalog.PromoCards.Where(c => c.PromoId == promo.PromoId)
            .Select(c => catalog.DefinitionsById[c.PlayerCardDefinitionId].NativeResourceId!.Value.Value).ToHashSet();
        var overview = await Send(setup.Panel, "GET", "/panel/api/promos");
        var tile = overview.Json!["definitions"]!.AsArray().Single(p => p!["code"]!.GetValue<string>() == promo.Code)!;
        Assert.Equal(members.Count, tile["cards"]!.GetValue<int>());
        Assert.All(tile["artwork"]!.AsArray(), art => Assert.Contains(art!["resourceId"]!.GetValue<long>(), members));
        var expected = setup.Runtime.Game.Players.Entries.Where(e => members.Contains(e.Key))
            .OrderByDescending(e => e.Value.Rating).ThenBy(e => e.Value.Text("name"), StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Key)
            .Skip(1).Take(2).Select(e => e.Key).ToArray();
        var page = await Send(setup.Panel, "GET", $"/panel/api/cards?promo={promo.Code}&offset=1&limit=2");
        Assert.Equal(members.Count, page.Json!["total"]!.GetValue<int>());
        Assert.Equal(expected, page.Json["cards"]!.AsArray().Select(c => c!["resourceId"]!.GetValue<long>()));
        var outsider = setup.Runtime.Game.Players.Entries.First(e => !members.Contains(e.Key));
        foreach (var query in new[] { $"resourceId={outsider.Key}", $"q={outsider.Key}:{outsider.Value.Rating}:{outsider.Value.Rareflag}" })
        {
            var reply = await Send(setup.Panel, "GET", $"/panel/api/cards?promo={promo.Code}&{query}");
            Assert.Equal(0, reply.Json!["total"]!.GetValue<int>());
        }
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/api/cards?promo=does_not_exist")).Status);
    }

    [Fact]
    public async Task DeveloperProPortraitsUseTheirBundledNativeHeadPreviews()
    {
        using var setup = await CreateAsync();
        foreach (var resource in new long[] { 167961228, 167983460, 167984358, 167988548, 167996618, 168019987 })
        {
            var editor = await Send(setup.Panel, "GET", $"/panel/api/cards/editor/{resource}");
            Assert.Equal(200, editor.Status);
            var url = $"/panel/assets/dynamic/p{resource}.png";
            Assert.Equal(url, editor.Json!["art"]!["defaultPortraitUrl"]!.GetValue<string>());
            var list = await Send(setup.Panel, "GET", $"/panel/api/cards?resourceId={resource}");
            Assert.Equal(url, list.Json!["cards"]![0]!["art"]!["defaultPortraitUrl"]!.GetValue<string>());
            Assert.Equal(5, list.Json["cards"]![0]!["art"]!["skillMoves"]!.GetValue<int>());
            var image = await Send(setup.Panel, "GET", url);
            Assert.Equal(200, image.Status);
            Assert.Equal("image/png", image.Headers.ContentType.ToString());
            var images = await Send(setup.Panel, "GET", $"/panel/api/cards/images/{editor.Json["assetId"]!.GetValue<long>()}");
            Assert.Contains(images.Json!["cards"]!.AsArray(), card => card!["resourceId"]!.GetValue<long>() == resource && card["url"]!.GetValue<string>() == url);
        }
    }

    [Fact]
    public async Task PublishedBaseFaceIsUsedByBothTheEditorAndCatalogue()
    {
        using var setup = await CreateAsync();
        const long resource = 167961228, baseId = 189068;
        var area = new PlayerCardArea();
        var game = setup.Runtime.Game;
        var changes = new ContentChangesets(game, [area]);
        var key = new JsonObject { ["resourceId"] = resource };
        var live = (await game.UnitOfWork.RunReadAsync(0, w => area.ReadAsync(w, key)))!;
        var card = live["card"]!.DeepClone().AsObject();
        card["head_resource_id"] = baseId;
        card["custom_head_id"] = null;
        await changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update,
            new JsonObject { ["card"] = card }, "tester");
        Assert.True((await changes.ValidateAsync()).Ok);
        await changes.PublishAsync("choose base face", "tester");
        var editor = await Send(setup.Panel, "GET", $"/panel/api/cards/editor/{resource}");
        var list = await Send(setup.Panel, "GET", $"/panel/api/cards?resourceId={resource}");
        foreach (var art in new[] { editor.Json!["art"]!, list.Json!["cards"]![0]!["art"]! })
        {
            Assert.Equal(baseId, art["headResourceId"]!.GetValue<long>());
            Assert.Equal(baseId, art["portraitId"]!.GetValue<long>());
            Assert.Null(art["defaultPortraitUrl"]);
        }
    }

    [Fact]
    public async Task CardCatalogueFiltersUseIdentityIdsAndPositionSets()
    {
        using var setup = await CreateAsync();
        var players = setup.Runtime.Game.Players.Entries.Select(entry => entry.Value).ToArray();
        var source = players.First(p => p.Int("teamid") > 0 && p.Int("leagueId") > 0 && p.Int("nation") > 0);
        var team = source.Int("teamid");
        var league = source.Int("leagueId");
        var nation = source.Int("nation");
        var metadata = await Send(setup.Panel, "GET", "/panel/api/cards/filters");
        Assert.Equal(200, metadata.Status);
        Assert.Contains(metadata.Json!["clubs"]!.AsArray(), club => club!["id"]!.GetValue<long>() == team && club["leagueId"]!.GetValue<long>() == league);
        Assert.Contains(metadata.Json["leagues"]!.AsArray(), item => item!["id"]!.GetValue<long>() == league);
        Assert.Contains(metadata.Json["nations"]!.AsArray(), item => item!["id"]!.GetValue<long>() == nation);
        var combined = await Send(setup.Panel, "GET", $"/panel/api/cards?team={team}&league={league}&nation={nation}&limit=200");
        Assert.Equal(players.Count(p => p.Int("teamid") == team && p.Int("leagueId") == league && p.Int("nation") == nation), combined.Json!["total"]!.GetValue<int>());
        var row = combined.Json["cards"]![0]!;
        var nationName = metadata.Json["nations"]!.AsArray().Single(item => item!["id"]!.GetValue<long>() == nation)!["name"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(nationName));
        Assert.All(combined.Json["cards"]!.AsArray(), card => Assert.Equal(nationName, card!["nation"]!.GetValue<string>()));
        Assert.NotNull(row["skillMoves"]);
        Assert.NotNull(row["weakFoot"]);
        Assert.NotNull(row["workRateAttack"]);
        Assert.NotNull(row["workRateDefense"]);
        Assert.Equal(6, row["attributes"]!.AsArray().Count);
        foreach (var positions in new[] { "ST,CF", "LM,RM,CM,CDM,CAM", "CB,RB,RWB,LWB,LB,GK" })
        {
            var reply = await Send(setup.Panel, "GET", $"/panel/api/cards?pos={positions}&limit=200");
            Assert.Equal(200, reply.Status);
            Assert.Equal(players.Count(p => positions.Split(',').Contains(p.Position)), reply.Json!["total"]!.GetValue<int>());
            Assert.All(reply.Json["cards"]!.AsArray(), card => Assert.Contains(card!["position"]!.GetValue<string>(), positions.Split(',')));
        }
        Assert.Equal(400, (await Send(setup.Panel, "GET", "/panel/api/cards?team=invalid")).Status);
    }

    [Fact]
    public async Task CardCatalogueSortsBeforePagingAndValidatesColumns()
    {
        using var setup = await CreateAsync();
        var entries = setup.Runtime.Game.Players.Entries.ToArray();
        foreach (var direction in new[] { "asc", "desc" })
        {
            var expected = direction == "asc"
                ? entries.OrderBy(e => e.Value.Rating)
                : entries.OrderByDescending(e => e.Value.Rating);
            var ids = expected.ThenBy(e => e.Value.Text("name"), StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Key)
                .Skip(64).Take(3).Select(e => e.Key).ToArray();
            var reply = await Send(setup.Panel, "GET", $"/panel/api/cards?sort=rating&direction={direction}&offset=64&limit=3");
            Assert.Equal(200, reply.Status);
            Assert.Equal(ids, reply.Json!["cards"]!.AsArray().Select(c => c!["resourceId"]!.GetValue<long>()).ToArray());
        }
        var pace = entries.Select(e => (e.Value.Get("face") as JsonArray)?[0]?.GetValue<long>() ?? 0).ToArray();
        var fastest = await Send(setup.Panel, "GET", "/panel/api/cards?sort=PAC&direction=desc&limit=1");
        var slowest = await Send(setup.Panel, "GET", "/panel/api/cards?sort=PAC&direction=asc&limit=1");
        Assert.Equal(pace.Max(), fastest.Json!["cards"]![0]!["attributes"]![0]!.GetValue<long>());
        Assert.Equal(pace.Min(), slowest.Json!["cards"]![0]!["attributes"]![0]!.GetValue<long>());
        Assert.Equal(400, (await Send(setup.Panel, "GET", "/panel/api/cards?sort=unknown")).Status);
        Assert.Equal(400, (await Send(setup.Panel, "GET", "/panel/api/cards?sort=rating&direction=unknown")).Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_logs)) Directory.Delete(_logs, recursive: true);
    }

    private sealed record Setup(TestRuntime Runtime, PlayerRegistry Registry, OperatorPanel Panel, LaunchIssuer Issuer, NativeSessions Sessions) : IDisposable
    {
        public void Dispose()
        {
            Registry.Dispose();
            Runtime.Dispose();
        }
    }

    private async Task<Setup> CreateAsync(IReadOnlyList<Aurora21.UltimateTeam.Core.Content.IContentArea>? contentAreas = null, string? assets = null, string? restartRequests = null, MatchmakingRegistry? lobby = null,
        OnlineGameSpeed? gameSpeed = null)
    {
        var runtime = TestRuntime.Create();
        using (await runtime.Game.ForPlayerAsync(PlayerIdentity.Of(DiscordId))) { }
        var registry = new PlayerRegistry(runtime.Fut, new PlayerRegistryOptions(), shared: true);
        var sessions = new NativeSessions(runtime.Game.Config.NativeAuth);
        var access = await PlayerAccess.LoadAsync(runtime.Game.UnitOfWork, CancellationToken.None);
        await access.RecordLaunchAsync(DiscordId, "Synthetic Player", CancellationToken.None);
        var issuer = new LaunchIssuer(null, sessions, access, new DiscordRoles(Discord), new PlayerPresence());
        Directory.CreateDirectory(_logs);
        var panel = new OperatorPanel(runtime.Game, registry, issuer, lobby, new OperatorPanelOptions { LogsDirectory = _logs, ContentAreas = contentAreas ?? Aurora21.UltimateTeam.Core.Content.ContentAreas.Default, AssetsDirectory = assets, RestartRequestDirectory = restartRequests, GameSpeed = gameSpeed });
        return new Setup(runtime, registry, panel, issuer, sessions);
    }

    /// <summary>
    /// online.gameSpeed lists the shared online game speed (Normal when none is sent) and changes it at once, 0 to 2 only;
    /// a panel without matchmaking has no such setting.
    /// </summary>
    [Fact]
    public async Task GameSpeedSettingChangesTheNextOnlineMatchAtOnce()
    {
        var speed = new OnlineGameSpeed();
        using (var setup = await CreateAsync(gameSpeed: speed))
        {
            JsonNode Row(JsonNode state) => state["onlineGameSpeed"]!;
            var row = Row((await Send(setup.Panel, "GET", "/panel/api/maintenance")).Json!);
            Assert.Equal(OnlineGameSpeed.Normal, row["value"]!.GetValue<long>());
            Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/maintenance/game-speed", "{\"value\":2}")).Status);
            Assert.Equal(OnlineGameSpeed.Fast, speed.Value);
            Assert.Equal(OnlineGameSpeed.Fast, Row((await Send(setup.Panel, "GET", "/panel/api/maintenance")).Json!)["value"]!.GetValue<long>());
            foreach (var invalid in new[] { "3", "-1", "fast", "1.5" })
            {
                Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/maintenance/game-speed", new JsonObject { ["value"] = invalid }.ToJsonString())).Status);
            }
            Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/maintenance/game-speed", "{\"value\":0,\"maintenance\":true}")).Status);
            Assert.Equal(OnlineGameSpeed.Fast, speed.Value);
        }
        using var without = await CreateAsync();
        Assert.Null((await Send(without.Panel, "GET", "/panel/api/maintenance")).Json!["onlineGameSpeed"]);
        Assert.Equal(400, (await Send(without.Panel, "POST", "/panel/api/maintenance/game-speed", "{\"value\":2}")).Status);
    }

    /// <summary>
    /// Leavers ranks players by early leaves and marks a serial leaver; Market log flags a Buy Now seconds after listing and
    /// a price far above the card's other sales, and lists only flagged sales on request.
    /// </summary>
    [Fact]
    public async Task AntiCheatPagesListSerialLeaversAndFlaggedSales()
    {
        using var setup = await CreateAsync();
        setup.Runtime.Game.InUnit(async uow =>
        {
            for (var i = 0; i < 3; i++)
                await uow.ExecuteAsync("""
                    INSERT INTO ultimateteam.match_leaves(leaver_player, stayer_player, stayer_club_id, mode, match_key, minute, stayer_goals, leaver_goals,
                        early, serial, stayer_outcome, left_at)
                    VALUES (@leaver, @stayer, 1, 'weekendLeague', @match, 4, 1, 0, true, false, 'None', @at)
                    """, ("leaver", DiscordId), ("stayer", $"99999{i}"), ("match", $"id:{i}"), ("at", uow.NowSeconds - 600 * (i + 1)));
            // Four ordinary sales of one card around 1,000 coins, then one at 60,000 bought 5 seconds after listing.
            for (var i = 0; i < 5; i++)
                await uow.ExecuteAsync("""
                    INSERT INTO ultimateteam.market_trades(trade_id, sold_at, sale, seller_club_id, seller_name, buyer_club_id, definition_id, resource_id,
                        rating, rareflag, price, seller_proceeds, starting_bid, buy_now, listed_at)
                    VALUES (@trade, @sold, 'buyNow', 70, 'seller', 71 + @i, 1, 158023, 80, 1, @price, @price, 150, @price, @listed)
                    """, ("trade", 9000L + i), ("i", (long)i), ("sold", uow.NowSeconds - 100 + i), ("price", i == 4 ? 60_000L : 1_000L + i * 50),
                    ("listed", uow.NowSeconds - 100 + i - (i == 4 ? 5 : 3600)));
        });

        var leavers = (await Send(setup.Panel, "GET", "/panel/api/leavers")).Json!;
        var leaver = Assert.Single(leavers["leavers"]!.AsArray())!;
        Assert.Equal(("Synthetic Player", 3L, true), (leaver["name"]!.GetValue<string>(), leaver["earlyInWindow"]!.GetValue<long>(), leaver["serialNow"]!.GetValue<bool>()));
        Assert.Equal(3, leavers["recent"]!.AsArray().Count);

        var all = (await Send(setup.Panel, "GET", "/panel/api/market-log")).Json!;
        Assert.Equal((5L, 1L), (all["totals"]!["trades"]!.GetValue<long>(), all["totals"]!["flagged"]!.GetValue<long>()));
        var flagged = Assert.Single((await Send(setup.Panel, "GET", "/panel/api/market-log?flagged=1")).Json!["trades"]!.AsArray())!;
        Assert.Equal(9004L, flagged["tradeId"]!.GetValue<long>());
        Assert.Equal(["overpriced", "instantBuy"], flagged["flags"]!.AsArray().Select(flag => flag!.GetValue<string>()).ToArray());
        Assert.Single((await Send(setup.Panel, "GET", "/panel/api/market-log?club=72")).Json!["trades"]!.AsArray());
        Assert.Equal(400, (await Send(setup.Panel, "GET", "/panel/api/market-log?club=abc")).Status);
    }

    [Fact]
    public async Task MatchmakingReportsUnavailableWithoutARegistry()
    {
        using var setup = await CreateAsync();
        var response = await Send(setup.Panel, "GET", "/panel/api/matchmaking");
        Assert.Equal(200, response.Status);
        Assert.False(response.Json!["snapshot"]!["available"]!.GetValue<bool>());
        Assert.False(response.Json["snapshot"]!["current"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MatchmakingIncludesQueuedPlayersWithoutPresenceAndStoppedSearches()
    {
        var lobby = new MatchmakingRegistry(null, NullLogger.Instance);
        using var setup = await CreateAsync(lobby: lobby);
        const string guestId = "234567890123456789";
        using var presence = setup.Issuer.Presence!.Enter(DiscordId, () => { });
        lobby.Start(DiscordId, "FUT_Rivals", [], null);
        lobby.Start(guestId, "FUT_Draft", [], null);
        var response = await Send(setup.Panel, "GET", "/panel/api/matchmaking");
        Assert.Equal(2, response.Json!["counts"]!["searching"]!.GetValue<int>());
        Assert.Equal(1, response.Json["counts"]!["online"]!.GetValue<int>());
        Assert.Equal(2, response.Json["players"]!.AsArray().Count);
        Assert.True(response.Json["splitQueue"]!.GetValue<bool>());
        var guest = response.Json["players"]!.AsArray().Single(row => row!["discordId"]!.GetValue<string>() == guestId)!;
        Assert.False(guest["connected"]!.GetValue<bool>());
        Assert.True(guest["queuedForMs"]!.GetValue<long>() >= 0);
        lobby.StopAccepting();
        response = await Send(setup.Panel, "GET", "/panel/api/matchmaking");
        Assert.False(response.Json!["snapshot"]!["accepting"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MatchmakingSeparatesSetupPlayingAndPostgameAndKeepsTheMode()
    {
        var lobby = new MatchmakingRegistry(null, NullLogger.Instance);
        using var setup = await CreateAsync(lobby: lobby);
        const string guestId = "234567890123456789";
        using var hostLease = setup.Registry.Acquire(PlayerIdentity.Of(DiscordId));
        using var guestLease = setup.Registry.Acquire(PlayerIdentity.Of(guestId));
        var host = new BlazeClient(hostLease.Session, new BlazeResponses(hostLease.Session.Fut), new Pipe().Writer, "127.0.0.1", "127.0.0.1");
        var guest = new BlazeClient(guestLease.Session, new BlazeResponses(guestLease.Session.Fut), new Pipe().Writer, "127.0.0.1", "127.0.0.1");
        lobby.Start(DiscordId, "FUT_Rivals", [], host);
        lobby.Start(guestId, "FUT_Rivals", [], guest);
        var gameId = lobby.GameIdFor(DiscordId)!.Value;
        var captured = lobby.PanelSnapshot();
        foreach (var (state, phase, counter) in new[] { (GameState.ConnectionVerification, "pre_match", "preMatch"), (GameState.PreGame, "pre_match", "preMatch"), (GameState.InGame, "in_match", "inGame"), (GameState.PostGame, "post_match", "postMatch") })
        {
            lobby.Read(gameId, match => match!.State = state);
            var response = await Send(setup.Panel, "GET", "/panel/api/matchmaking");
            Assert.Equal(2, response.Json!["counts"]![counter]!.GetValue<int>());
            Assert.Equal(1, response.Json["counts"]!["games"]!.GetValue<int>());
            Assert.All(response.Json["players"]!.AsArray(), row =>
            {
                Assert.Equal(phase, row!["phase"]!.GetValue<string>());
                Assert.Equal("FUT_Rivals", row["mode"]!.GetValue<string>());
                Assert.Equal(gameId, row["gameId"]!.GetValue<long>());
            });
            if (state != GameState.InGame) Assert.Equal(0, response.Json["counts"]!["inGame"]!.GetValue<int>());
        }
        Assert.All(captured.Players, player => Assert.Equal(GameState.ConnectionVerification, player.GameState));
    }

    [Fact]
    public async Task PublicMatchesListEachGameOnceWithNamesAndNoIds()
    {
        var lobby = new MatchmakingRegistry(null, NullLogger.Instance);
        using var setup = await CreateAsync(lobby: lobby);
        const string guestId = "234567890123456789";
        await setup.Issuer.Access.RecordLaunchAsync(DiscordId, "Host Player", CancellationToken.None);
        using var hostLease = setup.Registry.Acquire(PlayerIdentity.Of(DiscordId));
        using var guestLease = setup.Registry.Acquire(PlayerIdentity.Of(guestId));
        var host = new BlazeClient(hostLease.Session, new BlazeResponses(hostLease.Session.Fut), new Pipe().Writer, "127.0.0.1", "127.0.0.1");
        var guest = new BlazeClient(guestLease.Session, new BlazeResponses(guestLease.Session.Fut), new Pipe().Writer, "127.0.0.1", "127.0.0.1");
        var client = new ClientApiEndpoint(setup.Issuer, lobby, new ClientApiOptions(new LauncherApiSettings("Aurora 21", 3, 600, "Down.", "Full.",
            "Blocked.", "No play role.", "Roles unavailable.", "Not configured."), null, null, null, null));

        var empty = await SendClient(client, "/client/matches");
        Assert.Equal(200, empty.Status);
        Assert.Equal(0, empty.Json!["count"]!.GetValue<int>());

        lobby.Start(DiscordId, "FUT_Rivals", [], host);
        lobby.Start(guestId, "FUT_Rivals", [], guest);
        var gameId = lobby.GameIdFor(DiscordId)!.Value;
        var paired = await SendClient(client, "/client/matches");
        var match = paired.Json!["matches"]!.AsArray().Single()!;
        Assert.Equal(gameId, match["id"]!.GetValue<long>());
        Assert.Equal("rivals", match["mode"]!.GetValue<string>());
        Assert.Equal("pre_match", match["phase"]!.GetValue<string>());
        Assert.Null(match["inMatchForSeconds"]);
        Assert.Equal(["Host Player", "Player"], match["players"]!.AsArray().Select(player => player!["name"]!.GetValue<string>()));
        Assert.DoesNotContain(DiscordId, paired.Body);
        Assert.DoesNotContain(guestId, paired.Body);
        Assert.Equal("*", paired.Headers.AccessControlAllowOrigin.ToString());

        lobby.Read(gameId, game => game!.State = GameState.PreGame);
        lobby.Read(gameId, game => game!.State = GameState.InGame);
        var playing = await SendClient(client, "/client/matches");
        Assert.Equal(1, playing.Json!["inMatch"]!.GetValue<int>());
        Assert.Equal("in_match", playing.Json["matches"]![0]!["phase"]!.GetValue<string>());
        Assert.True(playing.Json["matches"]![0]!["inMatchForSeconds"]!.GetValue<long>() >= 0);

        lobby.EndGame(DiscordId, gameId);
        Assert.Equal(0, (await SendClient(client, "/client/matches")).Json!["count"]!.GetValue<int>());
        Assert.Equal(503, (await SendClient(new ClientApiEndpoint(setup.Issuer, null, new ClientApiOptions(new LauncherApiSettings("Aurora 21", 3, 600,
            "Down.", "Full.", "Blocked.", "No play role.", "Roles unavailable.", "Not configured."), null, null, null, null)), "/client/matches")).Status);
    }

    private static async Task<(int Status, string Body, JsonNode? Json, IHeaderDictionary Headers)> SendClient(ClientApiEndpoint endpoint, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        await endpoint.HandleAsync(context);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, body, JsonNode.Parse(body), context.Response.Headers);
    }

    [Fact]
    public async Task RestartRequiresAnActiveWorkerAndQueuesOnlyTheServerServiceOnce()
    {
        var requests = Path.Combine(_logs, "restart-requests");
        Directory.CreateDirectory(requests);
        using var setup = await CreateAsync(restartRequests: requests);
        Assert.Equal(409, (await Send(setup.Panel, "POST", "/panel/api/maintenance/restart", "{\"service\":\"server\",\"confirm\":true}")).Status);
        File.WriteAllText(Path.Combine(requests, "worker.heartbeat"), "ready");
        Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/maintenance/restart", "{\"service\":\"db\",\"confirm\":true}")).Status);
        Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/maintenance/restart", "{\"service\":\"server\"}")).Status);
        var state = await Send(setup.Panel, "GET", "/panel/api/maintenance");
        Assert.True(state.Json!["restart"]!["available"]!.GetValue<bool>());
        var queued = await Send(setup.Panel, "POST", "/panel/api/maintenance/restart", "{\"service\":\"server\",\"confirm\":true}");
        Assert.Equal(202, queued.Status);
        Assert.True(File.Exists(Path.Combine(requests, "restart-server.request")));
        Assert.Equal(409, (await Send(setup.Panel, "POST", "/panel/api/maintenance/restart", "{\"service\":\"server\",\"confirm\":true}")).Status);
        File.Delete(Path.Combine(requests, "restart-server.request"));
        File.SetLastWriteTimeUtc(Path.Combine(requests, "worker.heartbeat"), DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(409, (await Send(setup.Panel, "POST", "/panel/api/maintenance/restart", "{\"service\":\"server\",\"confirm\":true}")).Status);
    }

    [Fact]
    public async Task OnlyADirectSameOriginLoopbackRequestReachesThePanel()
    {
        using var setup = await CreateAsync();
        Assert.Equal(403, (await Send(setup.Panel, "GET", "/panel/api/dashboard", headers: [("X-Forwarded-For", "198.51.100.4")])).Status);
        Assert.Equal(403, (await Send(setup.Panel, "GET", "/panel/api/dashboard", host: "evil.example")).Status);
        Assert.Equal(403, (await Send(setup.Panel, "GET", "/panel/api/dashboard", remote: IPAddress.Parse("198.51.100.4"))).Status);
        Assert.Equal(403, (await Send(setup.Panel, "POST", "/panel/api/maintenance/blaze-access", "{}", contentType: "text/plain")).Status);
        Assert.Equal(403, (await Send(setup.Panel, "POST", "/panel/api/maintenance/blaze-access", "{}", headers: [("Sec-Fetch-Site", "cross-site")])).Status);
        var session = await Send(setup.Panel, "GET", "/panel/api/session");
        Assert.Equal("loopback", session.Json!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheCardEditorReadsACardAndStagesEditsAndNewCards()
    {
        using var setup = await CreateAsync();
        var resource = setup.Runtime.Game.Players.Entries.OrderBy(entry => entry.Key).First(entry => entry.Value.Rareflag is 0 or 1).Key;
        var asset = setup.Runtime.Game.Players.Find(resource)!.Int("assetId");

        var editor = await Send(setup.Panel, "GET", $"/panel/api/cards/editor/{resource}");
        Assert.Equal(200, editor.Status);
        var card = editor.Json!["card"]!.AsObject();
        Assert.True(card.ContainsKey("rating") && card.ContainsKey("gk_positioning"));
        Assert.Contains("ST", editor.Json["options"]!["positions"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.NotEmpty(editor.Json["options"]!["cardTypes"]!.AsArray());
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/api/cards/editor/9999999999")).Status);

        // An edit is staged through the generic changeset route and waits for publish.
        card["rating"] = card["rating"]!.GetValue<long>() == 99 ? 98 : card["rating"]!.GetValue<long>() + 1;
        var staged = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes",
            new JsonObject { ["area"] = "player_card", ["key"] = new JsonObject { ["resourceId"] = resource }, ["operation"] = "update", ["after"] = new JsonObject { ["card"] = card.DeepClone() } }.ToJsonString());
        Assert.Equal(200, staged.Status);
        Assert.Equal(1, staged.Json!["pending"]!.GetValue<int>());

        // The next free resource id is the player's asset id in a revision namespace, skipping one already staged.
        var first = await Send(setup.Panel, "POST", "/panel/api/cards/new", $"{{\"baseResourceId\":{resource}}}");
        Assert.Equal(200, first.Status);
        var newId = first.Json!["resourceId"]!.GetValue<long>();
        Assert.Equal(0, (newId - asset) % Aurora21.UltimateTeam.Core.Catalog.PlayerCatalog.ResourceRevisionSize);
        Assert.True(newId > asset);
        var added = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes",
            new JsonObject { ["area"] = "player_card", ["key"] = new JsonObject { ["resourceId"] = newId }, ["operation"] = "insert",
                ["after"] = new JsonObject { ["base"] = resource, ["cardTypeId"] = editor.Json["cardTypeId"]!.GetValue<long>(), ["card"] = card.DeepClone() } }.ToJsonString());
        Assert.Equal(200, added.Status);
        var second = await Send(setup.Panel, "POST", "/panel/api/cards/new", $"{{\"baseResourceId\":{resource}}}");
        Assert.NotEqual(newId, second.Json!["resourceId"]!.GetValue<long>());
        Assert.Equal(404, (await Send(setup.Panel, "POST", "/panel/api/cards/new", "{\"baseResourceId\":9999999999}")).Status);

        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        var exact = await Send(setup.Panel, "GET", $"/panel/api/cards?resourceId={resource}&limit=1");
        Assert.Equal(200, exact.Status);
        var only = Assert.Single(exact.Json!["cards"]!.AsArray());
        Assert.Equal(resource, only!["resourceId"]!.GetValue<long>());
        Assert.Equal(card["rating"]!.GetValue<long>(), only["rating"]!.GetValue<long>());
        Assert.Empty((await Send(setup.Panel, "GET", "/panel/api/cards?resourceId=9999999999")).Json!["cards"]!.AsArray());
        Assert.Equal(400, (await Send(setup.Panel, "GET", "/panel/api/cards?resourceId=invalid")).Status);
    }

    [Fact]
    public async Task HeadsAreUploadedForAPlayerAndOfferedWithTheirCardsArt()
    {
        using var setup = await CreateAsync();
        var (resource, card) = setup.Runtime.Game.Players.Entries.First(entry => entry.Value.Int("assetId") > 0);
        var asset = card.Int("assetId");
        var png = Convert.ToBase64String([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3]);
        var rgba = Convert.ToBase64String(new byte[180 * 180 * 4]);

        Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/heads", $"{{\"assetId\":{asset},\"label\":\"x\",\"png\":\"{png}\",\"rgba\":\"AAAA\"}}")).Status);
        Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/heads", $"{{\"assetId\":{asset},\"label\":\"x\",\"png\":\"AAAA\",\"rgba\":\"{rgba}\"}}")).Status);
        Assert.Equal(404, (await Send(setup.Panel, "POST", "/panel/api/heads", $"{{\"assetId\":999999999,\"label\":\"x\",\"png\":\"{png}\",\"rgba\":\"{rgba}\"}}")).Status);
        Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/heads/preview", "{\"rgba\":\"AAAA\"}")).Status);
        var prepared = await Send(setup.Panel, "POST", "/panel/api/heads/preview", $"{{\"rgba\":\"{rgba}\"}}");
        Assert.Equal(200, prepared.Status);
        Assert.Equal(Convert.ToBase64String(HeadDds.PreviewPng(HeadDds.FromRgba(new byte[HeadDds.RgbaBytes]))), prepared.Json!["png"]!.GetValue<string>());
        Assert.Empty((await Send(setup.Panel, "GET", $"/panel/api/cards/images/{asset}")).Json!["uploads"]!.AsArray());
        var uploaded = await Send(setup.Panel, "POST", "/panel/api/heads", $"{{\"assetId\":{asset},\"label\":\"Winter kit\",\"png\":\"{png}\",\"rgba\":\"{rgba}\"}}");
        Assert.Equal(200, uploaded.Status);
        var head = uploaded.Json!["headId"]!.GetValue<long>();

        var images = await Send(setup.Panel, "GET", $"/panel/api/cards/images/{asset}");
        Assert.Equal(200, images.Status);
        Assert.Empty(images.Json!["cards"]!.AsArray()); // No downloaded dynamic assets in this fixture.
        var upload = Assert.Single(images.Json["uploads"]!.AsArray());
        Assert.Equal("Winter kit", upload!["label"]!.GetValue<string>());

        var preview = await Send(setup.Panel, "GET", $"/panel/heads/{head}.png");
        Assert.Equal(200, preview.Status);
        Assert.Equal("image/png", preview.Headers.ContentType.ToString());
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/heads/999999.png")).Status);
        Assert.Equal(403, (await Send(setup.Panel, "GET", $"/panel/heads/{head}.png", headers: [("X-Forwarded-For", "203.0.113.9")])).Status);

        var editor = await Send(setup.Panel, "GET", $"/panel/api/cards/editor/{resource}");
        var originalArt = editor.Json!["art"]!.DeepClone();
        var value = editor.Json["card"]!.DeepClone().AsObject();
        value["custom_head_id"] = head;
        value["head_resource_id"] = null;
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        {
            ["area"] = "player_card", ["key"] = new JsonObject { ["resourceId"] = resource },
            ["operation"] = "update", ["after"] = new JsonObject { ["card"] = value },
        }.ToJsonString())).Status);
        // Staging cannot change the published portrait; the card list and editor must agree after publishing.
        Assert.True(JsonNode.DeepEquals(originalArt, (await Send(setup.Panel, "GET", $"/panel/api/cards/editor/{resource}")).Json!["art"]));
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        var published = await Send(setup.Panel, "GET", $"/panel/api/cards?q={resource}:{card.Rating}:{card.Rareflag}&limit=1");
        Assert.Equal(head, published.Json!["cards"]![0]!["art"]!["customHeadId"]!.GetValue<long>());
        Assert.Equal(head, (await Send(setup.Panel, "GET", $"/panel/api/cards/editor/{resource}")).Json!["art"]!["customHeadId"]!.GetValue<long>());
    }

    [Fact]
    public async Task ThePanelPageIsServedWithItsPolicy()
    {
        using var setup = await CreateAsync();
        Assert.Equal(302, (await Send(setup.Panel, "GET", "/")).Status);
        var page = await Send(setup.Panel, "GET", "/panel/");
        Assert.Equal(200, page.Status);
        Assert.Contains("Aurora 21", page.Body);
        Assert.Contains("frame-ancestors", page.Headers.ContentSecurityPolicy.ToString());
        Assert.Contains("id=\"root\"", page.Body);
        Assert.Equal(page.Body, (await Send(setup.Panel, "GET", "/panel/index.html")).Body);
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/js/core/panel.js")).Status);
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/js/pages/players.js")).Status);
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/js/pages/modes.js")).Status);
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/js/pages/changes.js")).Status);
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/PROVENANCE.md")).Status);
    }

    [Fact]
    public async Task ReactIsTheOnlyPanelAndOldBookmarksRedirect()
    {
        using var setup = await CreateAsync();
        foreach (var oldPath in new[] { "/panel/next", "/panel/next/", "/panel/next/index.html" })
        {
            var redirect = await Send(setup.Panel, "GET", oldPath);
            Assert.Equal(302, redirect.Status);
            Assert.Equal("/panel/", redirect.Headers.Location.ToString());
        }
        Assert.Equal("/panel/?review=1", (await Send(setup.Panel, "GET", "/panel/next/?review=1")).Headers.Location.ToString());
        Assert.Equal("/panel/", (await Send(setup.Panel, "GET", "/panel")).Headers.Location.ToString());

        var page = await Send(setup.Panel, "GET", "/panel/");
        Assert.Equal(200, page.Status);
        var policy = page.Headers.ContentSecurityPolicy.ToString();
        Assert.Contains("default-src 'self'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
        // The policy allows no inline script: every script the shell loads is a file.
        Assert.All(Regex.Matches(page.Body, "<script[^>]*>"), tag => Assert.Contains(" src=\"/panel/app/", tag.Value));
        var files = Regex.Matches(page.Body, "(?:src|href)=\"(/panel/app/[^\"]+)\"").Select(match => match.Groups[1].Value).ToList();
        Assert.Contains(files, file => file.EndsWith(".js", StringComparison.Ordinal));
        Assert.Contains(files, file => file.EndsWith(".css", StringComparison.Ordinal));
        Assert.Contains(files, file => file.EndsWith(".svg", StringComparison.Ordinal));
        foreach (var file in files)
        {
            var reply = await Send(setup.Panel, "GET", file);
            Assert.Equal(200, reply.Status);
            Assert.StartsWith(Path.GetExtension(file) switch { ".js" => "application/javascript", ".svg" => "image/svg+xml", _ => "text/css" }, reply.Headers.ContentType.ToString());
        }

        Assert.DoesNotContain("js/core/panel.js", page.Body);
        Assert.Equal(403, (await Send(setup.Panel, "GET", "/panel/", headers: [("X-Forwarded-For", "203.0.113.9")])).Status);
        Assert.Equal(403, (await Send(setup.Panel, "GET", "/panel/next/", headers: [("X-Forwarded-For", "203.0.113.9")])).Status);
        Assert.Equal(403, (await Send(setup.Panel, "GET", "/panel/", host: "panel.example.com")).Status);
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/build-stamp.txt")).Status);
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/next/build-stamp.txt")).Status);
    }

    [Fact]
    public async Task GiftDropdownIncludesRewardPicksAndGrantsBothTradeabilities()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        const long pickId = 513; // Retained 82+ Player Pick, outside the store catalogue.
        Assert.NotNull(setup.Runtime.Game.Packs.Rules.Pack(pickId));
        Assert.False(setup.Runtime.Game.Packs.IsForSale(pickId));
        var overview = await Send(setup.Panel, "GET", "/panel/api/packs");
        Assert.Equal(200, overview.Status);
        var pick = overview.Json!["packs"]!["packs"]!.AsArray().Single(row => row!["id"]!.GetValue<long>() == pickId)!;
        Assert.Equal("82+ Player Pick", pick["name"]!.GetValue<string>());
        Assert.False(pick["enabled"]!.GetValue<bool>());
        Assert.Contains(overview.Json["grantable"]!.AsArray(), row => row!["id"]!.GetValue<long>() == pickId);
        var detail = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}");
        Assert.Contains(detail.Json!["grantablePacks"]!.AsArray(), row => row!["id"]!.GetValue<long>() == pickId);
        foreach (var untradeable in new[] { false, true })
        {
            var flag = untradeable ? "true" : "false";
            Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}",
                $"{{\"op\":\"grantPacks\",\"packId\":{pickId},\"count\":1,\"untradeable\":{flag}}}")).Status);
            Assert.Equal(1L, await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, w => w.ScalarAsync<long>("""
                SELECT count(*) FROM ultimateteam.unopened_packs p JOIN ultimateteam.clubs c ON c.id=p.club_id
                WHERE c.persona_id=@persona AND p.pack_id=@pack AND p.untradeable=@flag
                """, ("persona", persona), ("pack", pickId), ("flag", untradeable)), CancellationToken.None));
        }
    }

    [Fact]
    public async Task CardSearchMatchesAccentsAndKeepsResourceIdLookup()
    {
        using var setup = await CreateAsync();
        var accented = await Send(setup.Panel, "GET", "/panel/api/cards?q=" + Uri.EscapeDataString("Eusébio"));
        Assert.NotEmpty(accented.Json!["cards"]!.AsArray());
        foreach (var name in new[] { "Eusebio", "EUSÉBIO", "Euse\u0301bio" })
        {
            var result = await Send(setup.Panel, "GET", "/panel/api/cards?q=" + Uri.EscapeDataString(name));
            Assert.Equal(accented.Json["total"]!.GetValue<long>(), result.Json!["total"]!.GetValue<long>());
            Assert.Equal(accented.Json["cards"]!.ToJsonString(), result.Json["cards"]!.ToJsonString());
        }
        var resource = accented.Json["cards"]![0]!["resourceId"]!.GetValue<long>();
        var exact = await Send(setup.Panel, "GET", $"/panel/api/cards?resourceId={resource}");
        Assert.Single(exact.Json!["cards"]!.AsArray());
        Assert.Equal(resource, exact.Json["cards"]![0]!["resourceId"]!.GetValue<long>());
    }

    [Fact]
    public async Task SeasonXpGrantsPreserveClaimsAndRejectInvalidAmounts()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var club = await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<long>(
            "SELECT id FROM ultimateteam.clubs WHERE persona_id=@persona", ("persona", persona)));
        var state = new ObjectivesState(5000, true, new long[] { 2 })
        {
            Stats = new Dictionary<string, long> { ["matches"] = 3 },
            ClaimedObjectives = new long[] { 7 }, ClaimedGroups = new long[] { 9 },
        };
        await setup.Runtime.Game.UnitOfWork.RunAsync(club, "test-objectives", work => work.SaveObjectivesAsync(state));
        var before = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}");
        Assert.Equal(5000, before.Json!["club"]!["seasonXp"]!.GetValue<long>());
        var grant = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", "{\"op\":\"grantSeasonXp\",\"amount\":1500}");
        Assert.Equal(200, grant.Status);
        Assert.Equal(6500, grant.Json!["club"]!["seasonXp"]!.GetValue<long>());
        Assert.Single(grant.Json["edits"]!.AsArray());
        Assert.Equal("grantSeasonXp", JsonNode.Parse(grant.Json["edits"]![0]!["patch"]!.GetValue<string>())!["op"]!.GetValue<string>());
        foreach (var amount in new[] { "0", "-1", "1.5", "100000000", "null", "\"bad\"" })
            Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}",
                $"{{\"op\":\"grantSeasonXp\",\"amount\":{amount}}}")).Status);
        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}",
            "{\"op\":\"grantSeasonXp\",\"amount\":100,\"coins\":1}")).Status);
        var after = await setup.Runtime.Game.UnitOfWork.RunReadAsync(club, work => work.ObjectivesAsync());
        Assert.Equal(6500, after.UserXp);
        Assert.True(after.RewardClaimed);
        Assert.Equal(state.ClaimedLevels, after.ClaimedLevels);
        Assert.Equal(state.ClaimedObjectives, after.ClaimedObjectives);
        Assert.Equal(state.ClaimedGroups, after.ClaimedGroups);
        Assert.Equal(3, after.Stat("matches"));
        var detail = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}");
        Assert.Single(detail.Json!["edits"]!.AsArray());
        Assert.Equal(before.Json["club"]!["coins"]!.GetValue<long>(), detail.Json["club"]!["coins"]!.GetValue<long>());
        Assert.Equal(before.Json["club"]!["unopenedPacks"]!.GetValue<long>(), detail.Json["club"]!["unopenedPacks"]!.GetValue<long>());
    }

    [Fact]
    public async Task PlayersBalancesAndGiftsApplyAtOnceAndAreRecorded()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var list = await Send(setup.Panel, "GET", "/panel/api/players");
        var row = list.Json!["players"]!.AsArray().Single(player => player!["id"]!.GetValue<long>() == persona)!;
        Assert.Equal(DiscordId, row["discordId"]!.GetValue<string>());
        Assert.False(row["access"]!["blocked"]!.GetValue<bool>());

        var detail = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}");
        var coins = detail.Json!["club"]!["coins"]!.GetValue<long>();
        var grant = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", "{\"coinsDelta\":500,\"pointsDelta\":25}");
        Assert.Equal(200, grant.Status);
        Assert.Equal(coins + 500, grant.Json!["club"]!["coins"]!.GetValue<long>());
        var set = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", "{\"coins\":1234}");
        Assert.Equal(1234, set.Json!["club"]!["coins"]!.GetValue<long>());
        var mixed = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", "{\"coins\":1,\"coinsDelta\":5}");
        Assert.Equal(400, mixed.Status);
        var rename = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", "{\"clubName\":\"Renamed\"}");
        Assert.Equal(400, rename.Status);

        var packs = detail.Json["club"]!["unopenedPacks"]!.GetValue<long>();
        const long goldPackId = 74; // Store Gold Pack (095); migration 097 removed the former testing pack 3.
        var gift = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", $"{{\"op\":\"grantPacks\",\"packId\":{goldPackId},\"count\":2}}");
        Assert.Equal(200, gift.Status);
        Assert.Equal(packs + 2, gift.Json!["club"]!["unopenedPacks"]!.GetValue<long>());
        var edits = gift.Json["edits"]!.AsArray();
        Assert.Equal(3, edits.Count);
        Assert.All(edits, edit => Assert.Equal("ok", edit!["result"]!.GetValue<string>()));

        var cards = await Send(setup.Panel, "GET", "/panel/api/cards?limit=1&min=80");
        var key = cards.Json!["cards"]![0]!["cardKey"]!.GetValue<string>();
        var card = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", $"{{\"op\":\"grantCard\",\"card\":\"{key}\",\"count\":1}}");
        Assert.Equal(200, card.Status);
        var inventory = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}/inventory?pile=new");
        Assert.Contains(inventory.Json!["piles"]!["new"]!.AsArray(), item => item!["resourceId"]!.GetValue<long>() == long.Parse(key.Split(':')[0]));
    }

    [Fact]
    public async Task AContentReloadMakesACardAddedInTheDatabaseGrantableWithoutARestart()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        const long resource = 184_783_754; // revision 11 of base card 234378, a synthetic test card
        var status = await Send(setup.Panel, "GET", "/panel/api/content");
        Assert.Equal(200, status.Status);
        Assert.Equal(1, status.Json!["revision"]!.GetValue<long>());
        Assert.Equal(0, status.Json["draftRunsInProgress"]!.GetValue<long>());
        var players = status.Json["counts"]!["players"]!.GetValue<long>();

        // Added straight to the database the way migration 056 adds its Pro shells: invisible until a reload.
        await using (var source = NpgsqlDataSource.Create(setup.Runtime.ConnectionString))
        await using (var insert = source.CreateCommand($"""
            BEGIN;
            SET CONSTRAINTS ALL DEFERRED;
            CREATE TEMP TABLE new_card ON COMMIT DROP AS
            SELECT b.definition_id AS base, (SELECT max(definition_id) FROM ultimateteam.item_definitions) + 1 AS id
            FROM ultimateteam.item_definitions b WHERE b.family_id = 1 AND b.native_resource_id = 234378;
            INSERT INTO ultimateteam.item_definitions
            SELECT (jsonb_populate_record(NULL::ultimateteam.item_definitions, to_jsonb(d) || jsonb_build_object(
                'definition_id', n.id, 'native_resource_id', {resource}, 'native_asset_id', 234378,
                'catalog_order', (SELECT max(catalog_order) + 1 FROM ultimateteam.item_definitions)))).*
            FROM new_card n JOIN ultimateteam.item_definitions d ON d.definition_id = n.base;
            INSERT INTO ultimateteam.player_card_definitions
            SELECT (jsonb_populate_record(NULL::ultimateteam.player_card_definitions, to_jsonb(p) || jsonb_build_object(
                'definition_id', n.id, 'rating', 98))).*
            FROM new_card n JOIN ultimateteam.player_card_definitions p ON p.definition_id = n.base;
            COMMIT;
            """))
            await insert.ExecuteNonQueryAsync();
        Assert.Empty((await Send(setup.Panel, "GET", $"/panel/api/cards?q={resource}")).Json!["cards"]!.AsArray());

        var reload = await Send(setup.Panel, "POST", "/panel/api/content/reload", "{}");
        Assert.Equal(200, reload.Status);
        Assert.Equal(2, reload.Json!["revision"]!.GetValue<long>());
        Assert.True(reload.Json["catalogChanged"]!.GetValue<bool>());
        Assert.Equal(players + 1, reload.Json["counts"]!["players"]!.GetValue<long>());

        var found = (await Send(setup.Panel, "GET", $"/panel/api/cards?q={resource}")).Json!["cards"]!.AsArray();
        var key = Assert.Single(found)!["cardKey"]!.GetValue<string>();
        Assert.StartsWith($"{resource}:98:", key);
        var grant = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", $"{{\"op\":\"grantCard\",\"card\":\"{key}\",\"count\":1}}");
        Assert.Equal(200, grant.Status);
        // The owned item renders through the bound item mapper, which now reads the reloaded catalogue.
        var inventory = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}/inventory?pile=new");
        Assert.Contains(inventory.Json!["piles"]!["new"]!.AsArray(), item => item!["resourceId"]!.GetValue<long>() == resource);
    }

    [Fact]
    public async Task AContentReloadThatFailsAnswers422AndKeepsTheRevision()
    {
        using var setup = await CreateAsync();
        await using (var source = NpgsqlDataSource.Create(setup.Runtime.ConnectionString))
        await using (var ahead = source.CreateCommand("INSERT INTO ultimateteam.schema_migrations(version) VALUES(999)"))
            await ahead.ExecuteNonQueryAsync();
        var reload = await Send(setup.Panel, "POST", "/panel/api/content/reload", "{}");
        Assert.Equal(422, reload.Status);
        Assert.Contains("nothing changed", reload.Json!["error"]!.GetValue<string>());
        Assert.Equal(1, (await Send(setup.Panel, "GET", "/panel/api/content")).Json!["revision"]!.GetValue<long>());
        Assert.Equal(405, (await Send(setup.Panel, "GET", "/panel/api/content/reload")).Status);
    }

    [Fact]
    public async Task ContentChangesetsAreStagedReviewedPublishedAndRevertedThroughThePanel()
    {
        using var setup = await CreateAsync([new ContentChangesetTests.RatingArea()]);
        var (definition, resource, rating) = ContentChangesetTests.Card(setup.Runtime, 0);
        var edited = rating == 99 ? 98 : rating + 1;
        var empty = await Send(setup.Panel, "GET", "/panel/api/content/changeset");
        Assert.False(empty.Json!["open"]!.GetValue<bool>());
        Assert.Contains(empty.Json["areas"]!.AsArray(), area => area!.GetValue<string>() == "test_rating");
        Assert.Contains(empty.Json["areas"]!.AsArray(), area => area!.GetValue<string>() == "store_pack");
        Assert.Contains(empty.Json["areas"]!.AsArray(), area => area!.GetValue<string>() == "store_lineup");

        var staged = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes",
            $"{{\"area\":\"test_rating\",\"key\":{{\"definitionId\":{definition}}},\"operation\":\"update\",\"after\":{{\"rating\":{edited}}},\"by\":\"panel tester\"}}");
        Assert.Equal(200, staged.Status);
        Assert.Equal(1, staged.Json!["pending"]!.GetValue<int>());
        var change = staged.Json["changeset"]!["changes"]![0]!;
        Assert.Equal(rating, change["before"]!["rating"]!.GetValue<long>());
        Assert.Equal(400, (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes",
            $"{{\"area\":\"test_rating\",\"key\":{{\"definitionId\":{definition}}},\"after\":{{\"rating\":100}}}}")).Status);

        var validate = await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}");
        Assert.True(validate.Json!["ok"]!.GetValue<bool>());
        Assert.Equal(rating, setup.Runtime.Game.Players.Find(resource)!.Rating);

        var publish = await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{\"note\":\"panel publish\"}");
        Assert.Equal(200, publish.Status);
        Assert.Equal(2, publish.Json!["content"]!["revision"]!.GetValue<long>());
        Assert.True(publish.Json["content"]!["catalogChanged"]!.GetValue<bool>());
        Assert.Equal(edited, setup.Runtime.Game.Players.Find(resource)!.Rating);
        Assert.Equal(409, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);

        var history = await Send(setup.Panel, "GET", "/panel/api/content/history");
        var id = history.Json!["changesets"]![0]!["id"]!.GetValue<long>();
        Assert.Equal("panel publish", history.Json["changesets"]![0]!["note"]!.GetValue<string>());
        var revert = await Send(setup.Panel, "POST", $"/panel/api/content/history/{id}/revert", "{}");
        Assert.Equal(200, revert.Status);
        Assert.Equal(id, revert.Json!["changeset"]!["revertsChangesetId"]!.GetValue<long>());
        var removed = await Send(setup.Panel, "DELETE", $"/panel/api/content/changeset/changes/{revert.Json["changeset"]!["changes"]![0]!["id"]!.GetValue<long>()}");
        Assert.Equal(0, removed.Json!["pending"]!.GetValue<int>());
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/discard", "{}")).Status);
        Assert.Equal(409, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/discard", "{}")).Status);
        Assert.Equal(edited, setup.Runtime.Game.Players.Find(resource)!.Rating);
    }

    [Fact]
    public async Task PortraitOptionsRequireDynamicImagesAndExcludeChampionsCopies()
    {
        var assets = Path.Combine(_logs, "portrait-assets");
        Directory.CreateDirectory(Path.Combine(assets, "dynamic"));
        using var setup = await CreateAsync(assets: assets);
        var game = setup.Runtime.Game;
        var red = game.Players.Entries.First(entry => game.Cards.Rules.FutChampionsRedRareflags.Contains(entry.Value.Rareflag));
        var asset = red.Value.Int("assetId");
        var original = game.Players.Entries.First(entry => entry.Value.Int("assetId") == asset
            && !game.Cards.Rules.FutChampionsRedRareflags.Contains(entry.Value.Rareflag));
        Assert.Empty((await Send(setup.Panel, "GET", $"/panel/api/cards/images/{asset}")).Json!["cards"]!.AsArray());
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        File.WriteAllBytes(Path.Combine(assets, "dynamic", $"p{original.Key}.png"), png);
        File.WriteAllBytes(Path.Combine(assets, "dynamic", $"p{red.Key}.png"), png);
        var options = (await Send(setup.Panel, "GET", $"/panel/api/cards/images/{asset}")).Json!["cards"]!.AsArray();
        var option = Assert.Single(options);
        Assert.Equal(original.Key, option!["resourceId"]!.GetValue<long>());
        Assert.Equal($"/panel/assets/dynamic/p{original.Key}.png", option["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task DownloadedImagesAreServedFromTheAssetsFolderOnly()
    {
        var assets = Path.Combine(Path.GetTempPath(), "aurora21-panel-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(assets, "players"));
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        File.WriteAllBytes(Path.Combine(assets, "players", "41.png"), png);
        File.WriteAllBytes(Path.Combine(assets, "secret.png"), png);
        try
        {
            using var setup = await CreateAsync(assets: assets);
            var image = await Send(setup.Panel, "GET", "/panel/assets/players/41.png");
            Assert.Equal(200, image.Status);
            Assert.Equal("image/png", image.Headers.ContentType.ToString());
            Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/assets/players/42.png")).Status);
            // Only the category folders are served: not the folder itself, other folders, or escaped paths.
            Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/assets/secret.png")).Status);
            Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/assets/other/41.png")).Status);
            Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/assets/players/..%2F..%2Fsecret.png")).Status);
            // Like the whole panel, the images are refused to anything but a direct loopback request.
            Assert.Equal(403, (await Send(setup.Panel, "GET", "/panel/assets/players/41.png", headers: [("X-Forwarded-For", "198.51.100.4")])).Status);
            var report = await Send(setup.Panel, "GET", "/panel/api/assets");
            Assert.True(report.Json!["available"]!.GetValue<bool>());
            Assert.Equal(1, report.Json["categories"]!["players"]!.GetValue<int>());
            Assert.Equal(2, report.Json["categories"]!["cards/hd"]!.GetValue<int>());
            foreach (var frame in new[] { "10_pro_player.png", "18_champions_gold.png" })
            {
                Assert.Contains(frame, report.Json["frames"]!.AsArray().Select(node => node!.GetValue<string>()));
                var bundled = await Send(setup.Panel, "GET", $"/panel/assets/cards/hd/{frame}");
                Assert.Equal(200, bundled.Status);
                Assert.Equal("image/png", bundled.Headers.ContentType.ToString());
            }
            foreach (var (resource, name) in new[] { (20801L, "Ronaldo"), (201942L, "Firmino"), (190871L, "Neymar"), (192985L, "De Bruyne") })
            {
                var reply = await Send(setup.Panel, "GET", $"/panel/api/cards?resourceId={resource}&limit=1");
                Assert.Equal(name, Assert.Single(reply.Json!["cards"]!.AsArray())!["art"]!["cardName"]!.GetValue<string>());
            }
        }
        finally
        {
            Directory.Delete(assets, recursive: true);
        }
    }

    [Fact]
    public async Task ADevelopersProCardIsGrantedOnceOnTheWholeServer()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        const string jota = "167996618:99:10";
        string Grant(int count) => $"{{\"op\":\"grantCard\",\"card\":\"{jota}\",\"count\":{count},\"untradeable\":false}}";
        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", Grant(2))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", Grant(1))).Status);
        var inventory = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}/inventory?pile=new");
        Assert.True(inventory.Json!["piles"]!["new"]!.AsArray().Single(i => i!["resourceId"]!.GetValue<long>() == 167996618)!["untradeable"]!.GetValue<bool>());

        // A second copy is refused, to the same player and to anyone else (here the server's own club).
        var again = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", Grant(1));
        Assert.Equal(409, again.Status);
        Assert.Contains("Mustafa", again.Body);
        var local = setup.Runtime.Game.Config.PersonaId;
        Assert.Equal(409, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{local}", Grant(1))).Status);
        Assert.Equal(1L, await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<long>("""
            SELECT count(*) FROM ultimateteam.items i JOIN ultimateteam.item_definitions d ON d.definition_id = i.definition_id
            WHERE d.native_resource_id = 167996618
            """), CancellationToken.None));
    }

    [Fact]
    public async Task ThePanelTakesAnItemAwayForNothingAndRecordsIt()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}",
            "{\"op\":\"grantCard\",\"card\":\"158023:93:1\",\"count\":1}")).Status);
        var before = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}/inventory?pile=new");
        var itemId = before.Json!["piles"]!["new"]!.AsArray().Single(i => i!["resourceId"]!.GetValue<long>() == 158023)!["id"]!.GetValue<long>();
        var coins = (await Send(setup.Panel, "GET", $"/panel/api/players/{persona}")).Json!["club"]!["coins"]!.GetValue<long>();

        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", $"{{\"op\":\"takeItem\",\"itemId\":{itemId},\"pay\":true}}")).Status);
        var taken = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", $"{{\"op\":\"takeItem\",\"itemId\":{itemId}}}");
        Assert.Equal(200, taken.Status);
        Assert.Equal(coins, taken.Json!["club"]!["coins"]!.GetValue<long>());
        Assert.Contains(taken.Json["edits"]!.AsArray(), edit => edit!["patch"]!.GetValue<string>().Contains("takeItem"));
        var after = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}/inventory?pile=new");
        Assert.DoesNotContain(after.Json!["piles"]!["new"]!.AsArray(), i => i!["id"]!.GetValue<long>() == itemId);

        var again = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", $"{{\"op\":\"takeItem\",\"itemId\":{itemId}}}");
        Assert.Equal(409, again.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PanelGrantsPersistTheSelectedTradeability(bool untradeable)
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var flag = untradeable ? "true" : "false";
        // A panel card grant is one copy.
        Assert.Equal(400, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}",
            $"{{\"op\":\"grantCard\",\"card\":\"158023:93:1\",\"count\":2,\"untradeable\":{flag}}}")).Status);
        Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}",
            $"{{\"op\":\"grantCard\",\"card\":\"158023:93:1\",\"count\":1,\"untradeable\":{flag}}}")).Status);
        var inventory = await Send(setup.Panel, "GET", $"/panel/api/players/{persona}/inventory?pile=new");
        Assert.Equal(untradeable, inventory.Json!["piles"]!["new"]!.AsArray().Single(i => i!["resourceId"]!.GetValue<long>() == 158023)!["untradeable"]!.GetValue<bool>());
        Assert.Equal(200, (await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}",
            $"{{\"op\":\"grantPacks\",\"packId\":70,\"count\":1,\"untradeable\":{flag}}}")).Status);
        Assert.Equal(1L, await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, w => w.ScalarAsync<long>("""
            SELECT count(*) FROM ultimateteam.unopened_packs p JOIN ultimateteam.clubs c ON c.id=p.club_id
            WHERE c.persona_id=@persona AND p.pack_id=70 AND p.untradeable=@flag
            """, ("persona", persona), ("flag", untradeable)), CancellationToken.None));
    }

    [Fact]
    public async Task BlockingAPlayerEndsTheirCodesAndMaintenanceListsAreDiscordIds()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var code = setup.Sessions.IssueCode(PlayerIdentity.Of(DiscordId));
        var block = await Send(setup.Panel, "PATCH", $"/panel/api/players/{persona}", "{\"op\":\"access\",\"blocked\":true}");
        Assert.Equal(200, block.Status);
        Assert.True(block.Json!["access"]!["blocked"]!.GetValue<bool>());
        Assert.Null(setup.Sessions.PlayerForCode(code));

        var address = await Send(setup.Panel, "POST", "/panel/api/maintenance/blaze-access", "{\"bypassIps\":\"198.51.100.4\"}");
        Assert.Equal(400, address.Status);
        var save = await Send(setup.Panel, "POST", "/panel/api/maintenance/blaze-access",
            $"{{\"maintenance\":true,\"maxPlayers\":\"8\",\"bypassNames\":\"{DiscordId}\",\"blockedIps\":\"\",\"maintenanceMessage\":\"Back at eight.\"}}");
        Assert.Equal(200, save.Status);
        var state = await Send(setup.Panel, "GET", "/panel/api/maintenance");
        var access = state.Json!["blazeAccess"]!;
        Assert.True(access["maintenance"]!.GetValue<bool>());
        Assert.Equal(8, access["maxPlayers"]!.GetValue<int>());
        Assert.Equal(DiscordId, access["bypassNames"]!.GetValue<string>());
        Assert.Equal("", access["blockedIps"]!.GetValue<string>());
        Assert.False(setup.Issuer.Access.Find(DiscordId)!.Blocked);
        var reloaded = await PlayerAccess.LoadAsync(setup.Runtime.Game.UnitOfWork, CancellationToken.None);
        Assert.Equal("Back at eight.", reloaded.Settings.MaintenanceMessage);
        Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/maintenance/restart", "{\"service\":\"fut\"}")).Status);

        // Aurora 16's role tools: the Discord server, a play role, the server's roles and one player's check.
        var roles = await Send(setup.Panel, "POST", "/panel/api/maintenance/blaze-access",
            $"{{\"guildId\":\"{Protocol.FakeDiscordApi.GuildId}\",\"playRoles\":\"{Protocol.FakeDiscordApi.PlayRole}\"}}");
        Assert.Equal(200, roles.Status);
        var picker = await Send(setup.Panel, "GET", "/panel/api/maintenance/discord/roles");
        Assert.Equal(2, picker.Json!["roles"]!.AsArray().Count);
        var check = await Send(setup.Panel, "POST", "/panel/api/maintenance/discord/check", $"{{\"id\":\"{DiscordId}\"}}");
        Assert.Equal("ok", check.Json!["status"]!.GetValue<string>());
        Assert.True(check.Json["canPlay"]!.GetValue<bool>());
        Assert.Equal(["Aurora 21 player"], check.Json["roles"]!.AsArray().Select(role => role!.GetValue<string>()).ToArray());
        var stranger = await Send(setup.Panel, "POST", "/panel/api/maintenance/discord/check", "{\"id\":\"987654321098765432\"}");
        Assert.Equal("not_member", stranger.Json!["status"]!.GetValue<string>());
        Assert.False(stranger.Json["canPlay"]!.GetValue<bool>());
        Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/maintenance/blaze-access", "{\"playRoles\":\"not-a-role\"}")).Status);
    }

    [Fact]
    public async Task PausingNewMatchesAppliesAtOnceAndSurvivesAReload()
    {
        using var setup = await CreateAsync();
        var gate = setup.Runtime.Game.MatchGate;
        var capBefore = setup.Issuer.Access.Settings.MaxPlayers;
        Assert.True((await Send(setup.Panel, "GET", "/panel/api/maintenance")).Json!["blazeAccess"]!["newMatches"]!.GetValue<bool>());

        var pause = await Send(setup.Panel, "POST", "/panel/api/maintenance/blaze-access", "{\"newMatches\":false}");
        Assert.Equal(200, pause.Status);
        Assert.Contains("new matches paused", pause.Json!["changed"]!.AsArray().Select(line => line!.GetValue<string>()));
        Assert.False(gate.Open);
        Assert.False((await Send(setup.Panel, "GET", "/panel/api/maintenance")).Json!["blazeAccess"]!["newMatches"]!.GetValue<bool>());
        // Pausing changes nothing the access form owns.
        Assert.Equal(capBefore, setup.Issuer.Access.Settings.MaxPlayers);

        // A restarted server loads the pause.
        var reloaded = await PlayerAccess.LoadAsync(setup.Runtime.Game.UnitOfWork, CancellationToken.None);
        Assert.False(reloaded.Settings.NewMatches);
        var restarted = new MatchGate();
        reloaded.Settings.ApplyTo(restarted);
        Assert.False(restarted.Open);

        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/maintenance/blaze-access", "{\"newMatches\":true}")).Status);
        Assert.True(gate.Open);
        Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/maintenance/blaze-access", "{\"newMatches\":\"maybe\"}")).Status);
    }

    [Fact]
    public async Task LogsAndDashboardAnswerAndTheSettingsPageIsGone()
    {
        using var setup = await CreateAsync();
        // The Settings page and its API were removed (2026-10-04): pack limits stay in the database, access is on Access.
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/api/settings")).Status);
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/api/settings/config")).Status);

        await File.WriteAllTextAsync(Path.Combine(_logs, "ultimateteam.log"), "first line\nPANEL something\nlast line\n");
        var logs = await Send(setup.Panel, "GET", "/panel/api/logs");
        Assert.Equal("ultimateteam.log", logs.Json!["pinned"]![0]!.GetValue<string>());
        var tail = await Send(setup.Panel, "GET", "/panel/api/logs/ultimateteam.log?lines=5&q=panel");
        Assert.Equal(["PANEL something"], tail.Json!["lines"]!.AsArray().Select(line => line!.GetValue<string>()).ToArray());
        Assert.Equal(404, (await Send(setup.Panel, "GET", "/panel/api/logs/..%2Fsecret")).Status);

        var dashboard = await Send(setup.Panel, "GET", "/panel/api/dashboard");
        Assert.Equal(200, dashboard.Status);
        Assert.True(dashboard.Json!["totals"]!["clubs"]!.GetValue<int>() >= 1);
        Assert.Equal(setup.Issuer.Access.Settings.MaxPlayers, dashboard.Json["services"]!["blazeAccess"]!["maxPlayers"]!.GetValue<int>());
        var specials = await Send(setup.Panel, "GET", "/panel/api/cards/specials");
        Assert.NotEmpty(specials.Json!["types"]!.AsArray());
    }

    [Fact]
    public async Task LogsBoundAndFilterTheTailAndIgnoreOtherFiles()
    {
        using var setup = await CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(_logs, "server.log"), string.Join('\n', Enumerable.Range(0, 2200).Select(i => $"line {i}")));
        await File.WriteAllTextAsync(Path.Combine(_logs, "secret.txt"), "not a log");
        var files = (await Send(setup.Panel, "GET", "/panel/api/logs")).Json!["files"]!.AsArray();
        Assert.DoesNotContain(files, row => row!["name"]!.GetValue<string>() == "secret.txt");
        var tail = (await Send(setup.Panel, "GET", "/panel/api/logs/server.log?lines=500")).Json!;
        Assert.Equal(500, tail["lines"]!.AsArray().Count);
        Assert.Equal("line 2199", tail["lines"]!.AsArray().Last()!.GetValue<string>());
        Assert.Equal(2000, (await Send(setup.Panel, "GET", "/panel/api/logs/server.log?lines=9999")).Json!["lines"]!.AsArray().Count);
        Assert.Equal(400, (await Send(setup.Panel, "GET", "/panel/api/logs/server.log?q=" + new string('x', 201))).Status);
    }

    [Fact]
    public async Task PlayerPickGroupsCanBeCreatedPreviewedAndPublishedWithPromoSources()
    {
        using var setup = await CreateAsync();
        var catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        var revision = catalog["revision"]!.GetValue<string>();
        Assert.Contains(catalog["promos"]!.AsArray(), node => node!["code"]!.GetValue<string>() == "totw_5");
        var pack = new JsonObject
        {
            ["name"]="Panel pick test",["size"]=3,["rare_slots"]=0,
            ["include_general_pool"]=false,["promo_codes"]=new JsonArray("totw_5"),
            ["category_chances"]=new JsonObject { ["totw"]=1,["icon"]=0 },
            ["player_picks"]=new JsonArray(new JsonObject { ["count"]=2,["choice_count"]=3,["min_rating"]=80,["promo_only"]=true,["label"]="TOTW 5 choice" }),
            ["bonus_player_count"]=1,["bonus_min_rating"]=80,["bonus_promo_only"]=true,
        };
        var invalid = pack.DeepClone().AsObject();
        invalid["player_picks"]![0]!["choice_count"] = 100;
        Assert.Equal(400,(await Send(setup.Panel,"POST","/panel/api/packs/catalog",new JsonObject { ["revision"]=revision,["pack"]=invalid }.ToJsonString())).Status);
        invalid = pack.DeepClone().AsObject();
        invalid["promo_codes"] = new JsonArray("fut_champions");
        Assert.Equal(400,(await Send(setup.Panel,"POST","/panel/api/packs/catalog",new JsonObject { ["revision"]=revision,["pack"]=invalid }.ToJsonString())).Status);
        var create = await Send(setup.Panel,"POST","/panel/api/packs/catalog",new JsonObject { ["revision"]=revision,["pack"]=pack }.ToJsonString());
        Assert.Equal(201,create.Status);
        var id = create.Json!["id"]!.GetValue<long>();
        Assert.Empty(setup.Runtime.Game.Packs.GetPackConfig(id));
        var preview = await Send(setup.Panel,"POST","/panel/api/packs/catalog/test",new JsonObject { ["packId"]=id,["pack"]=new JsonObject() }.ToJsonString());
        Assert.Equal(200,preview.Status);
        Assert.Equal(7,preview.Json!["items"]!.AsArray().Count);
        Assert.Equal(6,preview.Json["items"]!.AsArray().Count(item => item!["pickIndex"] is not null));
        var check = (await Send(setup.Panel,"POST","/panel/api/content/changeset/validate","{}")).Json!;
        Assert.True(check["ok"]!.GetValue<bool>(),check.ToJsonString());
        Assert.Equal(200,(await Send(setup.Panel,"POST","/panel/api/content/changeset/publish","{}")).Status);
        var stored = setup.Runtime.Game.Packs.GetPackConfig(id);
        Assert.Equal("totw_5",stored["promo_codes"]![0]!.GetValue<string>());
        Assert.Equal(2,stored["player_picks"]![0]!["count"]!.GetValue<int>());
        Assert.False(setup.Runtime.Game.Packs.IsForSale(id));
    }

    [Fact]
    public async Task PackLimitsAreExclusiveAndCanSwitchKindsThroughPublish()
    {
        using var setup = await CreateAsync();
        async Task<string> Revision() => (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!["revision"]!.GetValue<string>();
        var revision = await Revision();
        var invalid = await Send(setup.Panel, "POST", "/panel/api/packs/catalog",
            System.Text.Json.JsonSerializer.Serialize(new { revision, pack = new { name = "Exclusive limit test", purchase_limit = 3, community_limit = 5 } }));
        Assert.Equal(400, invalid.Status);
        var create = await Send(setup.Panel, "POST", "/panel/api/packs/catalog",
            System.Text.Json.JsonSerializer.Serialize(new { revision, pack = new { name = "Exclusive limit test", purchase_limit = 3 } }));
        Assert.Equal(201, create.Status);
        var id = create.Json!["id"]!.GetValue<long>();
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        revision = await Revision();
        // Sending just one nonzero field must still detect the other stored limit.
        Assert.Equal(400, (await Send(setup.Panel, "PUT", $"/panel/api/packs/catalog/{id}",
            System.Text.Json.JsonSerializer.Serialize(new { revision, pack = new { community_limit = 5 } }))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "PUT", $"/panel/api/packs/catalog/{id}",
            System.Text.Json.JsonSerializer.Serialize(new { revision, pack = new { purchase_limit = 0, community_limit = 5 } }))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(5, setup.Runtime.Game.Packs.GetPackConfig(id).Int("community_limit"));
        revision = await Revision();
        Assert.Equal(200, (await Send(setup.Panel, "PUT", $"/panel/api/packs/catalog/{id}",
            System.Text.Json.JsonSerializer.Serialize(new { revision, pack = new { purchase_limit = 2, community_limit = 0 } }))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(2, setup.Runtime.Game.Packs.GetPackConfig(id).Int("purchase_limit"));
        Assert.Equal(0, setup.Runtime.Game.Packs.GetPackConfig(id).Int("community_limit"));
    }

    [Fact]
    public async Task PackCatalogueAndLineupWaitForReviewedPublishAndKeepOwnedDefinitions()
    {
        using var setup = await CreateAsync();
        var catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        var revision = catalog["revision"]!.GetValue<string>();
        var create = await Send(setup.Panel, "POST", "/panel/api/packs/catalog",
            System.Text.Json.JsonSerializer.Serialize(new { revision, pack = new { name = "Panel test pack", size = 3, gold_weight = 100, rare_slots = 1 } }));
        Assert.Equal(201, create.Status);
        var id = create.Json!["id"]!.GetValue<long>();
        Assert.Equal("after-review", create.Json["applies"]!.GetValue<string>());
        Assert.False(setup.Runtime.Game.Packs.IsForSale(id));
        Assert.Empty(setup.Runtime.Game.Packs.GetPackConfig(id));
        var current = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        Assert.Equal(revision, current["revision"]!.GetValue<string>());

        var invalid = await Send(setup.Panel, "PUT", $"/panel/api/packs/catalog/{id}",
            System.Text.Json.JsonSerializer.Serialize(new { revision, pack = new { rare_slots = 4 } }));
        Assert.Equal(200, invalid.Status);
        var invalidCheck = await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}");
        Assert.False(invalidCheck.Json!["ok"]!.GetValue<bool>());
        Assert.Equal(200, (await Send(setup.Panel, "PUT", $"/panel/api/packs/catalog/{id}",
            System.Text.Json.JsonSerializer.Serialize(new { revision, pack = new { rare_slots = 1 } }))).Status);

        var preview = await Send(setup.Panel, "POST", "/panel/api/packs/catalog/test",
            System.Text.Json.JsonSerializer.Serialize(new { packId = id, pack = new { } }));
        Assert.Equal(200, preview.Status);
        Assert.Equal(3, preview.Json!["items"]!.AsArray().Count);
        var rolls = await Send(setup.Panel, "POST", "/panel/api/packs/simulate",
            System.Text.Json.JsonSerializer.Serialize(new { packId = id, rolls = 2 }));
        Assert.Equal(200, rolls.Status);
        Assert.Equal(2, rolls.Json!["rolls"]!.AsArray().Count);
        Assert.All(rolls.Json["rolls"]!.AsArray(), roll => Assert.Equal(3, roll!["items"]!.AsArray().Count));
        var listing = current["listing"]!.DeepClone().AsArray();
        listing.Add(new JsonObject { ["packId"] = id, ["price"] = 100, ["enabled"] = false, ["untradeable"] = false });
        var lineup = await Send(setup.Panel, "PUT", "/panel/api/packs/lineup",
            new JsonObject { ["revision"] = revision, ["listing"] = listing }.ToJsonString());
        Assert.Equal(200, lineup.Status);
        Assert.False(setup.Runtime.Game.Packs.IsForSale(id));
        var ready = await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}");
        Assert.True(ready.Json!["ok"]!.GetValue<bool>(), ready.Json.ToJsonString());
        var publish = await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{\"note\":\"pack and lineup\"}");
        Assert.Equal(200, publish.Status);
        Assert.Equal(2, publish.Json!["published"]!["changes"]!.AsArray().Count);
        Assert.False(setup.Runtime.Game.Packs.IsForSale(id));
        Assert.Equal(200, (await ReleaseAvailabilityAsync(setup, "store", id.ToString())).Status);
        Assert.True(setup.Runtime.Game.Packs.IsForSale(id));
        var next = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!["revision"]!.GetValue<string>();
        Assert.NotEqual(revision, next);
        var stale = await Send(setup.Panel, "PUT", $"/panel/api/packs/catalog/{id}",
            System.Text.Json.JsonSerializer.Serialize(new { revision, pack = new { name = "Stale" } }));
        Assert.Equal(409, stale.Status);
        Assert.Equal(200, (await Send(setup.Panel, "DELETE", $"/panel/api/packs/catalog/{id}",
            System.Text.Json.JsonSerializer.Serialize(new { revision = next }))).Status);
        Assert.True(setup.Runtime.Game.Packs.IsForSale(id));
        Assert.True((await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}")).Json!["ok"]!.GetValue<bool>());
        var hidden = await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}");
        Assert.Equal(200, hidden.Status);
        Assert.False(setup.Runtime.Game.Packs.IsForSale(id));
        Assert.NotEmpty(setup.Runtime.Game.Packs.GetPackConfig(id));
        var hideId = hidden.Json!["published"]!["id"]!.GetValue<long>();
        Assert.Equal(200, (await Send(setup.Panel, "POST", $"/panel/api/content/history/{hideId}/revert", "{}")).Status);
        var revertCheck = (await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}")).Json!;
        Assert.True(revertCheck["ok"]!.GetValue<bool>(), revertCheck.ToJsonString());
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.True(setup.Runtime.Game.Packs.IsForSale(id));
    }

    [Fact]
    public async Task MixedPackDraftRoundTripsButAnUnfillableLineupCannotBePublished()
    {
        using var setup = await CreateAsync();
        var catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        Assert.Equal(10, catalog["publicPackIds"]!.AsArray().Count);
        var bronze = catalog["packs"]!.AsArray().Single(pack => pack!["id"]!.GetValue<long>() == 70)!;
        Assert.Null(bronze["unavailableReason"]);
        var revision = catalog["revision"]!.GetValue<string>();
        var pack = bronze["config"]!.AsObject();
        var contents = pack["mixed_contents"]!.DeepClone();
        // Deliberately require a missing bronze consumable to test the publish gate.
        contents["consumables"]!["min"] = 1;
        contents["consumables"]!["max"] = 1;
        contents["players"]!["max"] = 8;
        var edit = await Send(setup.Panel, "PUT", "/panel/api/packs/catalog/70",
            new JsonObject { ["revision"] = revision, ["pack"] = new JsonObject { ["mixed_contents"] = contents } }.ToJsonString());
        var disabledListing = catalog["listing"]!.DeepClone().AsArray();
        foreach (var entry in disabledListing.OfType<JsonObject>())
            if (entry["packId"]!.GetValue<long>() == 70) entry["enabled"] = false;
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/packs/lineup",
            new JsonObject { ["revision"] = revision, ["listing"] = disabledListing }.ToJsonString())).Status);
        Assert.Equal(200, edit.Status);
        Assert.True((await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}")).Json!["ok"]!.GetValue<bool>());
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        bronze = catalog["packs"]!.AsArray().Single(pack => pack!["id"]!.GetValue<long>() == 70)!;
        Assert.Equal(8, bronze["config"]!["mixed_contents"]!["players"]!["max"]!.GetValue<int>());
        // The same unavailable contents must also block a token-only offer while the coin listing is off.
        var tokenListing = catalog["tokenListing"]!.DeepClone().AsArray();
        tokenListing.Add(new JsonObject { ["packId"] = 70, ["price"] = 25, ["enabled"] = false, ["untradeable"] = false });
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/packs/tokens-lineup", new JsonObject
        { ["revision"] = catalog["revision"]!.DeepClone(), ["listing"] = tokenListing }.ToJsonString())).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal(422, (await ReleaseAvailabilityAsync(setup, "token", "70")).Status);
        Assert.False(setup.Runtime.Game.Packs.TokenOffers().Single(offer => offer.Value!.AsObject().Int("base_pack_id") == 70).Value!.AsObject().Bool("enabled"));
        catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        var goldContents = new JsonObject { ["tier"] = "gold" };
        foreach (var category in new[] { "players", "consumables", "stadium", "staff" })
            goldContents[category] = new JsonObject { ["min"] = category is "players" or "consumables" ? 1 : 0,
                ["max"] = category is "players" or "consumables" ? 1 : 0 };
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/packs/catalog/74", new JsonObject
        {
            ["revision"] = catalog["revision"]!.DeepClone(),
            ["pack"] = new JsonObject { ["size"] = 2, ["rare_slots"] = 1, ["max_rating"] = 80, ["mixed_contents"] = goldContents },
        }.ToJsonString())).Status);
        var odds = await Send(setup.Panel, "POST", "/panel/api/packs/odds", "{\"packId\":74,\"pending\":true,\"samples\":25,\"seed\":\"mixed-nonplayers\"}");
        Assert.Equal(200, odds.Status);
        Assert.All(odds.Json!["metrics"]!.AsArray().Where(metric => metric!["code"]!.GetValue<string>().StartsWith("rare_", StringComparison.Ordinal)),
            metric => Assert.Equal(0, metric!["hits"]!.GetValue<int>()));
        var lineup = await Send(setup.Panel, "PUT", "/panel/api/packs/lineup",
            new JsonObject { ["revision"] = catalog["revision"]!.DeepClone(), ["publicPackIds"] = new JsonArray(70) }.ToJsonString());
        Assert.Equal(422, lineup.Status); // The old enable endpoint cannot bypass the required dates.
        var datedCoinRelease = await ReleaseAvailabilityAsync(setup, "store", "70");
        Assert.Equal(422, datedCoinRelease.Status);
        Assert.Contains("cannot be enabled", datedCoinRelease.Json!["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(setup.Runtime.Game.Packs.IsForSale(70));
    }

    /// <summary>
    /// The My Stadium Pack (83, migration 123) is edited like any other pack: its slots, item types and pools are stored,
    /// previewed and simulated, and a spec the store cannot sell (unknown type, reward-only pool, wrong size) is refused.
    /// </summary>
    [Fact]
    public async Task MyStadiumPackContentsAreEditablePreviewableAndChecked()
    {
        using var setup = await CreateAsync();
        var vanity = setup.Runtime.Game.Rules.Club.Vanity;
        var catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        var stadium = catalog["packs"]!.AsArray().Single(pack => pack!["id"]!.GetValue<long>() == 83)!;
        Assert.Null(stadium["viewOnlyReason"]);
        Assert.Contains(83L, catalog["publicPackIds"]!.AsArray().Select(id => id!.GetValue<long>()));
        var types = catalog["vanityTypes"]!.AsArray().OfType<JsonObject>().ToDictionary(type => type["subtype"]!.GetValue<long>());
        Assert.Equal("Goal song", types[517]["name"]!.GetValue<string>());
        Assert.Equal(vanity.Catalogue[517].Count - vanity.RewardOnly[517].Count, types[517]["items"]!.GetValue<int>());

        // Test contents and Simulate draw from the definition without giving anything to anyone.
        var preview = await Send(setup.Panel, "POST", "/panel/api/packs/catalog/test", "{\"packId\":83,\"pack\":{}}");
        Assert.Equal(200, preview.Status);
        Assert.Equal(new long[] { 500, 500, 505, 506, 517, 518 }, preview.Json!["items"]!.AsArray().Select(item => item!["subtype"]!.GetValue<long>()).Order());
        Assert.All(preview.Json!["items"]!.AsArray(), item => Assert.False(vanity.IsRewardOnly(item!["subtype"]!.GetValue<long>(), item["assetId"]!.GetValue<long>())));
        var simulate = await Send(setup.Panel, "POST", "/panel/api/packs/simulate", "{\"packId\":83,\"rolls\":3}");
        Assert.Equal(3, simulate.Json!["rolls"]!.AsArray().Count);

        string Put(JsonObject pack) => new JsonObject { ["revision"] = catalog["revision"]!.DeepClone(), ["pack"] = pack }.ToJsonString();
        JsonObject Spec(params JsonObject[] slots) => new() { ["slots"] = new JsonArray(slots.Select(slot => (JsonNode?)slot).ToArray()) };
        JsonObject Slot(long subtype, JsonArray? pools = null) => pools is null
            ? new JsonObject { ["subtypes"] = new JsonArray(subtype) } : new JsonObject { ["subtypes"] = new JsonArray(subtype), ["pools"] = pools };

        // Refused: an item type with no catalogue, a pool of reward-only items only, and a size that is not the slot count.
        var rewardTifo = vanity.RewardOnly[500][0];
        foreach (var (pack, error) in new[]
        {
            (new JsonObject { ["size"] = 1, ["vanity_pack"] = Spec(Slot(999)) }, "catalogued item type"),
            (new JsonObject { ["size"] = 1, ["vanity_pack"] = Spec(Slot(500, new JsonArray(new JsonObject { ["weight"] = 1, ["min"] = rewardTifo, ["max"] = rewardTifo }))) }, "no item a store pack may give"),
            (new JsonObject { ["size"] = 4, ["vanity_pack"] = Spec(Slot(505)) }, "size must equal"),
        })
        {
            var refused = await Send(setup.Panel, "PUT", "/panel/api/packs/catalog/83", Put(pack));
            if (refused.Status == 200)
            {
                var check = await Send(setup.Panel, "POST", "/panel/api/content/changeset/validate", "{}");
                Assert.Contains(error, check.Body);
                Assert.NotEqual(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
                Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/discard", "{}")).Status);
            }
            else Assert.Contains(error, refused.Body);
        }

        // Accepted: two mow patterns and a goal song, published to the store definition and opened by a club.
        var edited = Spec(Slot(505), Slot(505), Slot(517));
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/packs/catalog/83", Put(new JsonObject { ["size"] = 3, ["vanity_pack"] = edited }))).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.True(JsonNode.DeepEquals(edited, setup.Runtime.Game.Packs.GetPackConfig(83)["vanity_pack"]));
        var opened = setup.Runtime.Fut.Packs.CreatePack(83).Arr("itemData")!.OfType<JsonObject>();
        Assert.Equal(new long[] { 505, 505, 517 }, opened.Select(item => item.Int("cardsubtypeid")).Order());
    }

    [Fact]
    public async Task RewardPicksAreEditableAndPreviewableWithoutBecomingStorePacks()
    {
        using var setup = await CreateAsync();
        var catalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        var pick = catalog["packs"]!.AsArray().Single(pack => pack!["id"]!.GetValue<long>() == 550)!;
        // A red pick keeps its draw (view only) and stays out of the store; its name can still change.
        Assert.False(pick["inStore"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(pick["viewOnlyReason"]?.GetValue<string>()));
        Assert.DoesNotContain(550L, catalog["publicPackIds"]!.AsArray().Select(id => id!.GetValue<long>()));
        var preview = await Send(setup.Panel, "POST", "/panel/api/packs/catalog/test", "{\"packId\":550,\"pack\":{}}");
        Assert.True(preview.Status == 200, preview.Json?.ToJsonString());
        Assert.Equal(5, preview.Json!["items"]!.AsArray().Count);
        var forbidden = await Send(setup.Panel, "POST", "/panel/api/packs/catalog/test",
            "{\"pack\":{\"size\":1,\"player_picks\":[{\"count\":1,\"choice_count\":3,\"rareflags\":[18],\"resource_pool\":\"totw_1_3_red\"}]}}");
        Assert.Equal(422, forbidden.Status);
        var staged = await Send(setup.Panel, "PUT", "/panel/api/packs/catalog/550", new JsonObject
        {
            ["revision"] = catalog["revision"]!.DeepClone(),
            ["pack"] = new JsonObject { ["name"] = "Reviewed Champions Pick" },
        }.ToJsonString());
        Assert.Equal(200, staged.Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.Equal("Reviewed Champions Pick", setup.Runtime.Game.Packs.GetPackConfig(550)["name"]!.GetValue<string>());
        Assert.False(setup.Runtime.Game.Packs.IsForSale(550));
    }

    [Fact]
    public async Task ModesShowClubProgressAndResetOnlyTheChosenMode()
    {
        using var setup = await CreateAsync();
        var persona = PlayerIdentity.Of(DiscordId).PersonaId;
        var club = await setup.Runtime.Game.UnitOfWork.RunReadAsync(0, work => work.ScalarAsync<long>(
            "SELECT id FROM ultimateteam.clubs WHERE persona_id=@persona", ("persona", persona)));
        var squadWeek = setup.Runtime.Game.Rules.SquadBattles.Competition.WeekId(setup.Runtime.Game.NowSeconds);
        var eventId = setup.Runtime.Game.Rules.Seasons.WeekendLeague.Slot(setup.Runtime.Game.NowSeconds).Id;
        await setup.Runtime.Game.UnitOfWork.RunAsync(club, "test-mode-state", async work =>
        {
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.rivals_progress(club_id,skill_rating,division,placed,wins,draws,losses,weekly_score,weekly_games,fc_points,form)
                VALUES(@club,800,6,true,3,1,2,1200,6,250,'WWD')
                """);
            await work.ExecuteAsync("INSERT INTO ultimateteam.squad_battles_weeks(club_id,week_id,points) VALUES(@club,@week,100)", ("week", squadWeek));
            await work.ExecuteAsync("INSERT INTO ultimateteam.squad_battles_weeks(club_id,week_id,points) VALUES(@club,@week,200)", ("week", squadWeek - 1));
            await work.ExecuteAsync("INSERT INTO ultimateteam.weekend_league_records(club_id,event_id,games) VALUES(@club,@event,2)", ("event", eventId));
            await work.ExecuteAsync("INSERT INTO ultimateteam.weekend_league_records(club_id,event_id,games) VALUES(@club,@event,4)", ("event", eventId - 1));
            await work.ExecuteAsync("INSERT INTO ultimateteam.draft_runs(club_id,slot,run_revision,mode,state,difficulty,seed,current_position_id,choice_serial,next_wire_item_id,wins,losses,drafts_completed,pending_coin_reward) VALUES(@club,0,1,'online','active',1,42,0,0,1,0,0,0,0)");
        });
        var before = await Send(setup.Panel, "GET", $"/panel/api/modes/{persona}");
        Assert.Equal(800, before.Json!["rivals"]!["skillRating"]!.GetValue<long>());
        Assert.Equal(100, before.Json["squadBattles"]!["points"]!.GetValue<long>());
        Assert.Equal(2, before.Json["champions"]!["games"]!.GetValue<long>());
        Assert.Single(before.Json["draft"]!.AsArray());
        var reset = await Send(setup.Panel, "POST", $"/panel/api/modes/{persona}/rivals/reset", "{\"confirm\":true}");
        Assert.Equal(200, reset.Status);
        Assert.Null(reset.Json!["rivals"]);
        Assert.Single(reset.Json["draft"]!.AsArray());
        Assert.Equal(200, (await Send(setup.Panel, "POST", $"/panel/api/modes/{persona}/squad-battles/reset", "{\"confirm\":true}")).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", $"/panel/api/modes/{persona}/champions/reset", "{\"confirm\":true}")).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", $"/panel/api/modes/{persona}/draft/reset", "{\"confirm\":true}")).Status);
        var after = await Send(setup.Panel, "GET", $"/panel/api/modes/{persona}");
        Assert.Empty(after.Json!["draft"]!.AsArray());
        Assert.Null(after.Json["squadBattles"]);
        Assert.Null(after.Json["champions"]);
        var history = await setup.Runtime.Game.UnitOfWork.RunReadAsync(club, async work =>
            (Squad: await work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.squad_battles_weeks WHERE club_id=@club"),
             Champions: await work.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.weekend_league_records WHERE club_id=@club")));
        Assert.Equal(1, history.Squad);
        Assert.Equal(1, history.Champions);
    }

    [Fact]
    public async Task PromoEndpointsPreviewPendingBucketsAndReportWholePackOddsWithoutPublishing()
    {
        using var setup = await CreateAsync();
        foreach (var endpoint in new[] { "promos", "promo-events", "icon-rotations", "promos/availability" })
            Assert.Equal(200, (await Send(setup.Panel, "GET", "/panel/api/" + endpoint)).Status);
        var promo = await Send(setup.Panel, "GET", "/panel/api/promos/gold_rare");
        var after = promo.Json!["definition"]!.DeepClone().AsObject();
        var buckets = after["buckets"]!.AsArray();
        // Strongly favor the 90+ bucket; a pending simulation must use that value.
        foreach (var bucket in buckets.OfType<JsonObject>()) bucket["draw_weight"] = bucket["code"]!.GetValue<string>() == "rating_90_99" ? 10000m : 0.001m;
        var staged = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        { ["area"] = "promo", ["key"] = new JsonObject { ["code"] = "gold_rare" }, ["operation"] = "update", ["after"] = after }.ToJsonString());
        Assert.True(staged.Status == 200, staged.Body);
        var proposal = await Send(setup.Panel, "POST", "/panel/api/promos/gold_rare/buckets/from-ratings", """
            {"definition":{"ranges":[{"code":"all_gold","name":"All Gold","draw_weight":1,"min_rating":75,"max_rating":99}]}}
            """);
        Assert.Equal(200, proposal.Status);
        Assert.Single(proposal.Json!["buckets"]!.AsArray());
        Assert.Empty(proposal.Json["unassigned"]!.AsArray());
        var body = "{\"packId\":400,\"samples\":40,\"seed\":\"synthetic-panel\",\"at\":\"2026-11-01T00:00:00Z\",\"pending\":true}";
        var first = await Send(setup.Panel, "POST", "/panel/api/packs/odds", body);
        var second = await Send(setup.Panel, "POST", "/panel/api/packs/odds", body);
        Assert.True(first.Status == 200, first.Body);
        Assert.True(JsonNode.DeepEquals(first.Json, second.Json));
        Assert.False(first.Json!["changedClubState"]!.GetValue<bool>());
        Assert.NotEmpty(first.Json["metrics"]!.AsArray());
        Assert.Equal(40, first.Json["metrics"]!.AsArray().Single(m => m!["code"]!.GetValue<string>() == "rare_88_plus")!["hits"]!.GetValue<int>());
        var distribution = await Send(setup.Panel, "POST", "/panel/api/promos/distribution", "{\"category\":\"gold_rare\",\"pending\":true,\"limit\":10}");
        Assert.Equal(200, distribution.Status);
        Assert.All(distribution.Json!["cards"]!.AsArray(), c => Assert.True(setup.Runtime.Game.Players.Find(c!["resourceId"]!.GetValue<long>())!.Rating >= 90));
        Assert.Equal(1, setup.Runtime.Game.Live.Current.Revision);
        Assert.True((await Send(setup.Panel, "GET", "/panel/api/content/changeset")).Json!["open"]!.GetValue<bool>());
        Assert.Equal(400, (await Send(setup.Panel, "POST", "/panel/api/packs/odds", "{\"packId\":400,\"samples\":1001}")).Status);
    }

    [Fact]
    public async Task NewCardPromoEventAndPackCanBePreviewedAndPublishedTogether()
    {
        using var setup = await CreateAsync();
        var game = setup.Runtime.Game;
        var catalog = game.Live.Current.Catalog;
        var promoId = catalog.Promos.Single(p => p.Code == "totw_1").PromoId;
        var definition = catalog.PromoCards.First(c => c.PromoId == promoId && catalog.PlayersById[c.PlayerCardDefinitionId].Rating >= 80).PlayerCardDefinitionId;
        var resource = catalog.DefinitionsById[definition].NativeResourceId!.Value.Value;
        var editor = (await Send(setup.Panel, "GET", $"/panel/api/cards/editor/{resource}")).Json!;
        var newId = (await Send(setup.Panel, "POST", "/panel/api/cards/new", $"{{\"baseResourceId\":{resource}}}")).Json!["resourceId"]!.GetValue<long>();
        async Task Stage(string area, JsonObject key, JsonObject after)
        {
            var response = await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
                { ["area"] = area, ["key"] = key, ["operation"] = "insert", ["after"] = after }.ToJsonString());
            Assert.True(response.Status == 200, response.Body);
        }
        await Stage("player_card", new JsonObject { ["resourceId"] = newId }, new JsonObject
            { ["base"] = resource, ["cardTypeId"] = editor["cardTypeId"]!.DeepClone(), ["card"] = editor["card"]!.DeepClone() });
        await Stage("promo", new JsonObject { ["code"] = "synthetic_new_promo" }, new JsonObject
        {
            ["name"] = "Synthetic new promo", ["odds_category"] = "totw", ["status"] = "active", ["draw_weight"] = 1,
            ["buckets"] = new JsonArray(new JsonObject { ["code"] = "single", ["name"] = "Single", ["draw_weight"] = 1, ["cards"] = new JsonArray(newId) }),
        });
        await Stage("promo_event", new JsonObject { ["code"] = "synthetic_new_event" }, new JsonObject
        {
            ["name"] = "Synthetic new event", ["enabled"] = true, ["starts_at"] = "2026-11-01T00:00:00Z", ["ends_at"] = "2026-11-08T00:00:00Z",
            ["promos"] = new JsonArray("synthetic_new_promo"),
        });
        var packCatalog = (await Send(setup.Panel, "GET", "/panel/api/packs/catalog")).Json!;
        var pack = new JsonObject { ["name"] = "Synthetic single-player pack", ["size"] = 1, ["rare_slots"] = 1, ["gold_weight"] = 100,
            ["promo_only"] = true, ["include_general_pool"] = false, ["promo_codes"] = new JsonArray("synthetic_new_promo"),
            ["category_chances"] = new JsonObject { ["totw"] = 1 } };
        var created = await Send(setup.Panel, "POST", "/panel/api/packs/catalog", new JsonObject { ["revision"] = packCatalog["revision"]!.DeepClone(), ["pack"] = pack }.ToJsonString());
        Assert.True(created.Status == 201, created.Body);
        var packId = created.Json!["id"]!.GetValue<long>();
        var preview = await Send(setup.Panel, "POST", "/panel/api/packs/catalog/test", $"{{\"packId\":{packId},\"pack\":{{}}}}");
        Assert.True(preview.Status == 200, preview.Body);
        Assert.Equal(newId, Assert.Single(preview.Json!["items"]!.AsArray())!["resourceId"]!.GetValue<long>());
        Assert.Null(game.Players.Find(newId));
        var publish = await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}");
        Assert.True(publish.Status == 200, publish.Body);
        Assert.NotNull(game.Players.Find(newId));
        Assert.Contains("synthetic_new_promo", game.Packs.GetPackConfig(packId)["promo_codes"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task ExternalContentStatusIgnoresRollbacksAndOwnPublishAndClearsAfterReload()
    {
        using var setup = await CreateAsync();
        Assert.False((await Send(setup.Panel, "GET", "/panel/api/content/status")).Json!["externalChanges"]!.GetValue<bool>());
        await using var source = NpgsqlDataSource.Create(setup.Runtime.ConnectionString);
        await using (var command = source.CreateCommand("""
            BEGIN; SELECT set_config('ultimateteam.content_publish','on',true);
            UPDATE ultimateteam.promos SET name='Synthetic outside edit' WHERE code='totw_1'; ROLLBACK;
            """)) await command.ExecuteNonQueryAsync();
        Assert.False((await Send(setup.Panel, "GET", "/panel/api/content/status")).Json!["externalChanges"]!.GetValue<bool>());
        var promo = (await Send(setup.Panel, "GET", "/panel/api/promos/totw_1")).Json!["definition"]!.DeepClone().AsObject();
        promo["name"] = "Synthetic panel edit";
        Assert.Equal(200, (await Send(setup.Panel, "PUT", "/panel/api/content/changeset/changes", new JsonObject
        { ["area"] = "promo", ["key"] = new JsonObject { ["code"] = "totw_1" }, ["operation"] = "update", ["after"] = promo }.ToJsonString())).Status);
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/changeset/publish", "{}")).Status);
        Assert.False((await Send(setup.Panel, "GET", "/panel/api/content/status")).Json!["externalChanges"]!.GetValue<bool>());
        await using (var command = source.CreateCommand("""
            BEGIN; SELECT set_config('ultimateteam.content_publish','on',true);
            UPDATE ultimateteam.promos SET name='Synthetic outside edit' WHERE code='totw_1'; COMMIT;
            """)) await command.ExecuteNonQueryAsync();
        var status = await Send(setup.Panel, "GET", "/panel/api/content/status");
        Assert.True(status.Json!["externalChanges"]!.GetValue<bool>());
        Assert.Contains(status.Json["tables"]!.AsArray(), t => t!["table"]!.GetValue<string>() == "promos");
        Assert.Equal(200, (await Send(setup.Panel, "POST", "/panel/api/content/reload", "{}")).Status);
        Assert.False((await Send(setup.Panel, "GET", "/panel/api/content/status")).Json!["externalChanges"]!.GetValue<bool>());
    }

    private static async Task<(int Status, string Body, JsonNode? Json, IHeaderDictionary Headers)> Send(OperatorPanel panel, string method,
        string pathAndQuery, string? body = null, (string Name, string Value)[]? headers = null, string host = "127.0.0.1:18448",
        IPAddress? remote = null, string contentType = "application/json")
    {
        var context = new DefaultHttpContext();
        var query = pathAndQuery.IndexOf('?');
        context.Request.Method = method;
        context.Request.Path = query < 0 ? pathAndQuery : pathAndQuery[..query];
        if (query >= 0) context.Request.QueryString = new QueryString(pathAndQuery[query..]);
        context.Request.Host = new HostString(host);
        context.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        foreach (var (name, value) in headers ?? []) context.Request.Headers[name] = value;
        if (body is not null || method is not ("GET" or "HEAD"))
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? "{}");
            context.Request.ContentType = contentType;
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
        }
        context.Response.Body = new MemoryStream();
        await panel.HandleAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        JsonNode? json = null;
        if (context.Response.ContentType?.StartsWith("application/json", StringComparison.Ordinal) == true && text.Length > 0) json = JsonNode.Parse(text);
        return (context.Response.StatusCode, text, json, context.Response.Headers);
    }
}
