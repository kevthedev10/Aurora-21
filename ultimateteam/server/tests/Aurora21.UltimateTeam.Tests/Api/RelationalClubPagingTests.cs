using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Tests.Api.Reference;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalClubPagingTests
{
    private const string G = "/ut/game/fifa21";

    [Fact]
    public async Task PlayerSearchProjectionTracksOwnedCardInsertMoveAndDelete()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        await rt.Game.RunAsync("player-search-projection", async work =>
        {
            var resource = rt.Game.Players.Ids.First();
            var id = await work.NextItemIdAsync();
            await work.SaveItemAsync(rt.Fut.Items.BuildPlayer(resource, id, "club"));

            var row = await work.QueryAsync("""
                SELECT s.pile,s.rating,p.rating,s.resource_id,
                    COALESCE(d.native_resource_id,d.native_asset_id,0)
                FROM ultimateteam.club_player_search s
                JOIN ultimateteam.items i ON i.id=s.item_id
                JOIN ultimateteam.player_card_definitions p ON p.definition_id=i.definition_id
                JOIN ultimateteam.item_definitions d ON d.definition_id=i.definition_id
                WHERE s.club_id=@club AND s.native_item_id=@id
                """, reader => (
                    Pile: reader.GetString(0), Rating: reader.GetInt64(1), CatalogRating: reader.GetInt64(2),
                    Resource: reader.GetInt64(3), CatalogResource: reader.GetInt64(4)), ("id", id));
            Assert.Single(row);
            Assert.Equal("club", row[0].Pile);
            Assert.Equal(row[0].CatalogRating, row[0].Rating);
            Assert.Equal(row[0].CatalogResource, row[0].Resource);

            await work.ExecuteAsync("UPDATE ultimateteam.items SET pile='trade' WHERE club_id=@club AND native_item_id=@id", ("id", id));
            Assert.Equal("trade", await work.ScalarAsync<string>(
                "SELECT pile FROM ultimateteam.club_player_search WHERE club_id=@club AND native_item_id=@id", ("id", id)));
            await work.ExecuteAsync("UPDATE ultimateteam.items SET pile='club' WHERE club_id=@club AND native_item_id=@id", ("id", id));
            Assert.Equal("club", await work.ScalarAsync<string>(
                "SELECT pile FROM ultimateteam.club_player_search WHERE club_id=@club AND native_item_id=@id", ("id", id)));

            Assert.True(await work.DeleteItemAsync(id));
            Assert.Equal(0L, await work.ScalarAsync<long>(
                "SELECT count(*) FROM ultimateteam.club_player_search WHERE club_id=@club AND native_item_id=@id", ("id", id)));
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountsPreserveVirtualIdCollisionsAndImportedRepresentations(bool representation)
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var badgeId = rt.Game.Rules.Club.VirtualItemId(rt.Game.ClubId, "activeBadge");
        await rt.Game.RunAsync("summary-count-fixtures", async work =>
        {
            var resource = rt.Game.Players.Ids.First();
            // A virtual badge replaces this id during search, but user clubPlayers counts owned cards.
            var collision = rt.Fut.Items.BuildPlayer(resource, badgeId, "club");
            await work.SaveItemAsync(collision);
            var unassigned = rt.Fut.Items.BuildPlayer(resource, await work.NextItemIdAsync(), "unassigned");
            await work.SaveItemAsync(unassigned);
            var unplaced = rt.Fut.Items.BuildPlayer(resource, await work.NextItemIdAsync(), "club");
            await work.SaveItemAsync(unplaced);
            // The installed schema allows a NULL pile; the wire renderer treats it as empty text for counts.
            await work.ExecuteAsync("UPDATE ultimateteam.items SET pile=NULL WHERE club_id=@club AND native_item_id=@id", ("id", unplaced.Int("id")));
            if (representation)
                await work.ExecuteAsync("""
                    INSERT INTO ultimateteam.item_extensions(item_id,schema_revision,payload,wire_representation)
                    SELECT id,1,'{}'::jsonb,'{"overrides":{"rareflag":999}}'::jsonb
                    FROM ultimateteam.items WHERE club_id=@club AND native_item_id=@badge
                    ON CONFLICT(item_id) DO UPDATE SET wire_representation=EXCLUDED.wire_representation
                    """, ("badge", badgeId));
        }, CancellationToken.None);
        var kitDefinition = rt.Game.Kits.Kits.First();
        var kit = ItemFactory.KitItem(101, rt.Game.NowSeconds, kitDefinition.IntKeep("resourceId"),
            "free", "club", kitDefinition.Int("teamid"), ItemFactory.KitType(kitDefinition),
            2 + kitDefinition.Int("kitType"), kitDefinition.Text("name"));
        await rt.Game.RunAsync("search-equipped-kit", work => work.SaveItemAsync(kit), CancellationToken.None);
        await rt.Fut.Inventory.EquipAsync(101, "activeHomeKit");
        await rt.Game.ReadAsync(async work =>
        {
            var full = await ClubSnapshot.LoadAsync(work);
            var items = await rt.Fut.Club.ClubItemsWithActivesAsync(work, full);
            foreach (var query in new[] { "type=player&count=16", "type=player&rare=SP&count=16", "type=all&count=16" })
            {
                var request = new FutRequest("GET", G + "/club?" + query, Array.Empty<KeyValuePair<string,string>>(), Array.Empty<byte>());
                var expected = rt.Fut.Search.Search(full, items, request.Query, request.Payload);
                var actual = await rt.Fut.Search.SearchAsync(work, request.Query, request.Payload);
                Assert.True(JsonNode.DeepEquals(expected, actual), query + "\nexpected: " + expected + "\nactual: " + actual);
            }
            Assert.True(JsonNode.DeepEquals(await rt.Fut.Club.NativeUserAsync(work, full), await rt.Fut.Club.NativeUserAsync(work)));
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(250)]
    [InlineData(800)]
    [InlineData(2000)]
    public async Task SqlPagesAndNarrowPresentationMatchFullDocuments(int inventory)
    {
        using var rt = TestRuntime.Create(marketTarget: 0,
            time: new TestRuntime.FixedTime(1_700_000_000), prepareRoot: root =>
            {
                var config = JsonNode.Parse(File.ReadAllText(Path.Combine(TestRuntime.DataRoot, "config.example.json")))!.AsObject();
                config["initial_club_player_count"] = inventory;
                var folder = Path.Combine(root, "user-config"); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "config.json"), config.ToJsonString());
            });
        var selectedManager = await rt.Game.RunAsync("paging-state-fixtures", async work =>
        {
            var player = (await work.ListItemsAsync("club")).First(item => item.Text("itemType") == "player");
            player["preferredPosition"] = "LM"; player["playStyle"] = rt.Game.Rules.Cards.ChemistryStyles.Names.Keys.Last();
            player["loans"] = 9; player["discardValue"] = 0; player["untradeable"] = true;
            await work.SaveItemAsync(player.Int("id"), player);
            var manager = await rt.Fut.Items.MakeManagerAsync(work, rt.Game.Managers.BestManagerId()!.Value, "club");
            var squad = (await work.ListSquadsAsync())[0];
            squad["manager"] = new JsonArray(new JsonObject { ["id"] = manager.Int("id") });
            await work.SaveSquadAsync(squad.Int("id"), squad.Text("squadName"), true, squad);
            return manager.Int("id");
        }, CancellationToken.None);
        var baseline = new ClubPresentationBaseline(rt.Fut);
        await rt.Game.ReadAsync(async work =>
        {
            var snapshot = await ClubSnapshot.LoadAsync(work);
            var full = await rt.Fut.Club.ClubItemsWithActivesAsync(work, snapshot);
            var player = full.First(item => item.Text("itemType") == "player");
            var requests = new List<FutRequest>();
            void Get(string query) => requests.Add(new("GET", G + "/club?" + query, Array.Empty<KeyValuePair<string,string>>(), Array.Empty<byte>()));
            void Post(string query, JsonObject body) => requests.Add(new("POST", G + "/club?" + query, Array.Empty<KeyValuePair<string,string>>(), Encoding.UTF8.GetBytes(body.ToJsonString())));
            foreach (var query in new[] {
                "type=player&count=16&start=0", "type=player&count=16&start=16", "type=player&count=16&start=99999",
                "type=player&count=0", "type=player&count=-1", "type=player&defId=999999999&count=16",
                "type=player&pageSize=16&page=2", "type=player&num=16&sort=asc",
                "type=player&count=16&acquiredDate=asc", "type=player&count=16&acquiredDate=desc", "type=player&count=16&sortBy=value",
                "type=player&count=16&sortBy=value&sort=asc", "type=playerDefender&count=16", "type=playerMidfielder&count=16",
                "type=playerForward&count=16", "type=player&level=gold&count=16", "type=player&level=silver&count=16",
                "type=player&level=bronze&count=16", "type=player&rare=SP&count=16", "type=player&playStyle=250&count=16",
                "type=player&playStyle=273&count=16", "type=player&isUntradeable=true&count=16", "type=player&isUntradeable=false&count=16",
                "type=player&nat=-1&count=16", "type=manager&count=2", "type=clubinfo&category=badge&count=2",
                "type=clubinfo&count=2", "type=equippables&count=2", "type=all&count=16", "type=player" }) Get(query);
            Get($"type=player&defId={player.Int("assetId")},0,0&count=16");
            Get($"type=player&exclitem={player.Int("id")},{full.Where(item => item.Text("itemType") == "player").Skip(1).First().Int("id")}&count=16");
            Get($"type=player&leagueId={player.Int("leagueId")}&teamid={player.Int("teamid")}&nation={player.Int("nation")}&count=16");
            Get($"type=player&maskedDefId={player.Int("assetId")}&pos={player.Text("preferredPosition")}&count=16");
            Get($"type=equippables&excldef={full.First(item => item.Text("itemType") == "kit").Int("resourceId")}&count=2");
            Post("type=manager&count=1", new() { ["criteria"] = new JsonObject { ["type"] = "player", ["count"] = 5 }, ["count"] = 16, ["offset"] = 16 });
            foreach (var request in requests)
            {
                // The My Stadium pickers also list the club's owned vanity items (virtual, not counted in the piles).
                var vanity = request.RawTarget.Contains("type=equippables") ? await rt.Fut.Vanity.ItemsAsync(work) : null;
                var expected = rt.Fut.Search.Search(snapshot, full, request.Query, request.Payload, virtualItems: vanity);
                var actual = await rt.Fut.Search.SearchAsync(work, request.Query, request.Payload);
                Assert.True(JsonNode.DeepEquals(expected, actual), request.RawTarget + "\nexpected: " + expected + "\nactual: " + actual);
            }
            var old = await ClubSnapshotBaseline.LoadAsync(work);
            // The frozen legacy reference predates owned-manager references. Keep its counters,
            // and compare the squad with the unchanged full-document renderer for the current contract.
            var expectedHub = await baseline.HubAsync(work);
            expectedHub["squad"] = await rt.Fut.Club.NativeSquadAsync(work, snapshot);
            var actualHub = await rt.Fut.Club.HubAsync(work);
            Assert.Equal(FutJson.Serialize(expectedHub), FutJson.Serialize(actualHub));
            Assert.Equal(selectedManager, actualHub["squad"]!["manager"]![0]!["id"]!.GetValue<long>());
            // The frozen reference lists each squad with its saved chemistry. The current contract lists the chemistry that
            // squad's own screen computes (port-python.md C3), so that one field comes from the squad renderer.
            var expectedUser = await baseline.NativeUserAsync(work, old);
            // Likewise its rating: a squad the client never saved is rated from its XI, as its own document is (Python server
            // SQUAD_IDENTITY_RATE_UNSAVED).
            foreach (var summary in expectedUser["squadList"]!["squad"]!.AsArray().OfType<JsonObject>())
            {
                var document = await rt.Fut.Club.NativeSquadAsync(work, snapshot, summary.Int("id"));
                summary["chemistry"] = document["chemistry"]!.DeepClone();
                summary["rating"] = document["rating"]!.DeepClone();
            }
            Assert.Equal(FutJson.Serialize(expectedUser), FutJson.Serialize(await rt.Fut.Club.NativeUserAsync(work)));
            return true;
        }, CancellationToken.None);

        var metadataRows = new List<long>(); var countRows = new List<long>();
        var headerRows = new List<long>(); var pageRows = new List<long>(); var names = new List<string>();
        var measuring = new AsyncLocal<bool> { Value = true };
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == DatabaseTimings.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!measuring.Value || instrument.Name != "ultimateteam.database.query.rows") return;
            foreach (var tag in tags)
            {
                if (tag.Key == "query" && Equals(tag.Value, "club.presentation.metadata")) metadataRows.Add(value);
                if (tag.Key == "query" && Equals(tag.Value, "club.presentation.shape")) countRows.Add(value);
                if (tag.Key == "query" && Equals(tag.Value, "inventory.headers")) headerRows.Add(value);
                if (tag.Key == "query" && Equals(tag.Value, "club.search.page")) pageRows.Add(value);
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (!measuring.Value || instrument.Name != "ultimateteam.database.query") return;
            foreach (var tag in tags) if (tag.Key == "endpoint") names.Add(tag.Value!.ToString()!);
        });
        listener.Start();
        var response = await rt.Router.HandleAsync(new("GET", G + "/club?type=player&count=16&start=16", Array.Empty<KeyValuePair<string,string>>(), Array.Empty<byte>()));
        Assert.Equal(200, response.Status);
        Assert.Contains(16L, pageRows);
        Assert.NotEmpty(headerRows); Assert.All(headerRows, rows => Assert.InRange(rows, 0, 16));
        Assert.Contains("GET ClubRoutes.SearchAsync", names);
        Assert.NotEmpty(metadataRows); Assert.All(metadataRows, rows => Assert.InRange(rows, 0, 80));
        // The representation check and grouped omitted-player counts return one row, never the inventory.
        Assert.NotEmpty(countRows); Assert.All(countRows, rows => Assert.Equal(1, rows));
        metadataRows.Clear();
        headerRows.Clear();
        _ = await rt.Fut.Club.HubAsync();
        Assert.NotEmpty(headerRows); Assert.All(headerRows, rows => Assert.InRange(rows, 0, 40));
        Assert.NotEmpty(metadataRows); Assert.All(metadataRows, rows => Assert.InRange(rows, 0, 80));
    }

    /// <summary>
    /// A squad rendered twice from one view, with the first result mutated in between. The card the
    /// fetch stores is the view's own and a JsonNode has a single parent, so a renderer handing out
    /// the stored instance would throw on the second render, and one handing out a live reference
    /// would let a caller change it. Rendering a fresh card per slot rules out both.
    /// The sibling risk — one footballer in two slots of a single squad — is not tested here because
    /// it cannot be reached: RelationalSquadCompatibilityStore.SaveAsync rejects it with
    /// DUPLICATE_FOOTBALLER_IN_SQUAD.
    /// </summary>
    [Fact]
    public async Task RenderingASquadTwiceFromOneViewStaysDetached()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        await rt.Game.ReadAsync(async work =>
        {
            var snapshot = ClubSnapshot.FromPresentation(
                await ClubPresentationView.LoadSquadAsync(work, null, rt.Fut.Club.CustomisationStates()));
            var first = await rt.Fut.Club.NativeSquadAsync(work, snapshot);
            var populated = first["players"]!.AsArray().First(slot => slot!["itemData"]?["id"] is not null)!;
            var expected = FutJson.Serialize(first);
            populated["itemData"]!["contract"] = 999;
            Assert.Equal(expected, FutJson.Serialize(await rt.Fut.Club.NativeSquadAsync(work, snapshot)));
            return true;
        }, CancellationToken.None);
    }

    /// <summary>
    /// The squad fetch builds native cards for the slots it renders and never their wire documents.
    /// The byte contract is covered by the hub comparison above — the selection view's squad must
    /// equal the full-document renderer's — but that comparison is also satisfied by quietly
    /// rendering a document for every row, which is the cost this path exists to avoid. So assert
    /// the short path is actually taken, and that the rows other readers need keep their documents.
    /// </summary>
    [Fact]
    public async Task SquadSlotsAreFetchedAsNativeCardsWithoutTheirWireDocuments()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        await rt.Game.ReadAsync(async work =>
        {
            var view = await ClubPresentationView.LoadSquadAsync(work, null, rt.Fut.Club.CustomisationStates());
            var rows = view.FetchedRows().ToList();
            Assert.NotEmpty(rows);
            Assert.Contains(rows, row => row.NativeCard);
            Assert.DoesNotContain(rows, row => row.NativeCard && row.Document);
            // The manager is read back as a document by Manager(id), so it must not have taken the
            // native path; NativeOnlySquadRows excludes it.
            Assert.NotNull(view.Manager());
            return true;
        }, CancellationToken.None);
    }
}
