using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalTacticsTests
{
    private const string G = "/ut/game/fifa21";
    private const long Now = 1700000000;

    private static TestRuntime Create(Action<string>? observer = null, string? connection = null)
    {
        if (connection is null)
        {
            connection = PostgresTestCluster.CreateInstalledDatabase();
            using var source = NpgsqlDataSource.Create(connection);
            RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
        }
        return TestRuntime.Create(connectionString: connection, relationalSlice: true, time: new TestRuntime.FixedTime(Now),
            commandObserver: observer, initializeRelationalClub: true);
    }

    /// <summary>The two verbatim client saves captured on 2026-09-17.</summary>
    private static List<(string Path, JsonObject Body)> Captured()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "native-custom-tactics.json")))!.AsObject();
        return fixture.Arr("saves")!.OfType<JsonObject>().Select(s => (s.Str("path"), s.Obj("body")!)).ToList();
    }

    private static Task<T> Read<T>(TestRuntime rt, Func<UnitOfWork, ValueTask<T>> read) => rt.Game.ReadAsync(read, CancellationToken.None);

    private static JsonArray ActiveTactics(TestRuntime rt) => rt.CallObject("GET", G + "/squad/active").Arr("tactics")!;

    [Fact]
    public async Task CapturedClientSavesRoundTripExactlyLikeLegacyAndReopen()
    {
        using var rt = Create();
        using var legacy = TestRuntime.Create(time: new TestRuntime.FixedTime(Now));
        foreach (var (path, body) in Captured())
        {
            Assert.True(JsonNode.DeepEquals(legacy.Call("POST", path, body), rt.Call("POST", path, body)));
        }
        var expected = ActiveTactics(legacy);
        var actual = ActiveTactics(rt);
        Assert.Equal(2, actual.Count);
        // Same values and the same key order as the client sent (and the legacy document stored).
        Assert.Equal(expected.ToJsonString(), actual.ToJsonString());
        Assert.Equal(Captured().Select(c => c.Body.Arr("tactics")![0]!.ToJsonString()).Order(), actual.Select(t => t!.ToJsonString()).Order());

        var rows = await Read(rt, w => w.QueryAsync("""
            SELECT t.mentality, i.instruction_code, count(*), bool_and(i.text_value IS NULL)
            FROM ultimateteam.squad_tactic_instructions i JOIN ultimateteam.squad_tactics t ON t.id = i.tactic_id
            GROUP BY t.mentality, i.instruction_code ORDER BY 1, 2
            """, r => $"{r.GetString(0)}/{r.GetString(1)}={r.GetInt64(2)}/{r.GetBoolean(3)}"));
        Assert.Equal(new[]
        {
            "ATTACKING/instructions=22/True", "ATTACKING/positions=11/True", "ATTACKING/styles=8/True",
            "BALANCED/instructions=22/True", "BALANCED/positions=11/True", "BALANCED/styles=8/True",
        }, rows);
        Assert.Equal(0L, await Read(rt, w => w.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.squads WHERE extension_payload::text LIKE '%instructions%' OR wire_representation::text LIKE '%instructions%'")));

        // A later squad save keeps the tactics; reopening returns the same bytes.
        var squad = (await Read(rt, w => w.GetSquadAsync(1)))!;
        Assert.Equal(200, rt.CallRaw("PUT", G + "/squad/1", squad).Status);
        var connection = rt.Detach();
        using var reopened = Create(connection: connection);
        Assert.Equal(expected.ToJsonString(), ActiveTactics(reopened).ToJsonString());
    }

    [Fact]
    public async Task ResavingAMentalityReplacesItsListsAndInvalidShapesChangeNothing()
    {
        using var rt = Create();
        var (path, body) = Captured()[0];
        rt.Call("POST", path, body);
        var before = ActiveTactics(rt).ToJsonString();

        JsonObject Variant(Action<JsonObject> change)
        {
            var copy = (JsonObject)body.DeepClone();
            change(copy.Arr("tactics")![0]!.AsObject());
            return copy;
        }
        foreach (var invalid in new[]
        {
            Variant(t => t["styles"] = new JsonArray()),
            Variant(t => t.Arr("styles")!.Insert(0, new JsonObject { ["index"] = 5, ["value"] = 1 })),
            Variant(t => t.Arr("styles")![1]!.AsObject()["index"] = 0),
            Variant(t => t.Arr("styles")![0]!.AsObject()["value"] = 1.5),
            Variant(t => t.Arr("styles")![0]!.AsObject()["value"] = "2"),
            Variant(t => t.Arr("styles")![0]!.AsObject()["player"] = 900000000001),
            Variant(t => t.Arr("styles")!.Add(7)),
            Variant(t => t["nested"] = new JsonObject { ["a"] = 1 }),
        })
        {
            await Assert.ThrowsAsync<ClubRuleException>(() => rt.Fut.Inventory.SaveTacticsAsync(new Aurora21.UltimateTeam.Core.Web.QueryValues(), invalid).AsTask());
            Assert.Equal(before, ActiveTactics(rt).ToJsonString());
        }

        // The client always sends the complete entry; a shorter save must not leave stale rows behind.
        var shorter = Variant(t =>
        {
            t.Remove("styles");
            t["instructions"] = new JsonArray(new JsonObject { ["index"] = 0, ["value"] = 42 });
        });
        rt.Call("POST", path, shorter);
        var saved = Assert.Single(ActiveTactics(rt))!.AsObject();
        Assert.False(saved.ContainsKey("styles"));
        Assert.Equal("[{\"index\":0,\"value\":42}]", saved.Arr("instructions")!.ToJsonString());
        Assert.Equal(1L, await Read(rt, w => w.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.squad_tactic_instructions WHERE instruction_code <> 'positions'")));
    }

    [Fact]
    public async Task ASquadScreenFormationChangeMovesTheBalancedTacticWithIt()
    {
        using var rt = Create();
        foreach (var (path, body) in Captured())
        {
            rt.Call("POST", path, body);
        }
        // FIFA sends a formation picked on the squad screen in the squad PUT only, then reloads the squad's Balanced tactic.
        var squad = (await Read(rt, w => w.GetSquadAsync(1)))!;
        squad["formation"] = "f433c";
        Assert.Equal(200, rt.CallRaw("PUT", G + "/squad/1", squad).Status);

        var active = rt.CallObject("GET", G + "/squad/active");
        Assert.Equal("f433c", active.Str("formation"));
        var tactics = active.Arr("tactics")!.OfType<JsonObject>().ToDictionary(t => t.Str("tactic"), t => t.Str("formation"));
        Assert.Equal("f433c", tactics["BALANCED"]);
        Assert.Equal("f442", tactics["ATTACKING"]);
    }

    [Fact]
    public async Task TextInstructionRowsStayBlockedAndTacticsAreClubScoped()
    {
        using var rt = Create();
        var (path, body) = Captured()[0];
        rt.Call("POST", path, body);
        var other = (await rt.Game.RunAsync("other", w => w.EnsureRelationalClubAsync(new ClubIdentity(900000051, 900000052), 0), CancellationToken.None)).Account.ClubId;
        await rt.Game.UnitOfWork.RunAsync(other, "other-squad", w => w.SaveSquadAsync(1, "Other", true, new JsonObject { ["id"] = 1, ["squadId"] = 1, ["players"] = new JsonArray() }), CancellationToken.None);
        Assert.Empty(await rt.Game.UnitOfWork.RunReadAsync(other, w => w.SquadTacticsAsync(1), CancellationToken.None));
        Assert.Single(await Read(rt, w => w.SquadTacticsAsync(1)));

        // Text rows remain reserved: without a traced wire mapping, reads and tactic saves must refuse them.
        await rt.Game.RunAsync("text", w => w.ExecuteAsync("""
            INSERT INTO ultimateteam.squad_tactic_instructions(tactic_id, instruction_code, ordinal, text_value)
            SELECT t.id, 'text', 0, 'preserve' FROM ultimateteam.squad_tactics t JOIN ultimateteam.squads q ON q.id = t.squad_id
            WHERE q.club_id = @club
            """), CancellationToken.None);
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(rt, w => w.SquadTacticsAsync(1)));
        await Assert.ThrowsAsync<NotSupportedException>(() => rt.Game.RunAsync("resave", w => w.SaveSquadTacticAsync(1, "BALANCED", body.Arr("tactics")![0]!.AsObject()), CancellationToken.None));
        var squad = await rt.Game.UnitOfWork.RunReadAsync(rt.Game.ClubId, w => w.ListSquadsAsync(), CancellationToken.None);
        await rt.Game.RunAsync("squad", w => w.SaveSquadAsync(1, "x", true, squad[0]), CancellationToken.None);
        Assert.Equal(1L, await Read(rt, w => w.ScalarAsync<long>("SELECT count(*) FROM ultimateteam.squad_tactic_instructions WHERE text_value='preserve'")));
        Assert.Equal(0L, await Read(rt, w => w.ScalarAsync<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='squad_tactic_instructions' AND column_name='squad_player_id'")));
    }
}
