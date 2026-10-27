using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// Reviewed content edits (live-content-editing-plan.md, step 3): pending changes publish all together or not at all,
/// validated as start-up validates, refused when a row changed since it was edited, and revertible.
/// </summary>
public sealed class ContentChangesetTests
{
    [Fact]
    public async Task APendingEditChangesNothingUntilPublishedAndValidateRollsBack()
    {
        using var rt = TestRuntime.Create();
        var changes = new ContentChangesets(rt.Game, [new RatingArea()]);
        var (definition, resource, rating) = Card(rt, 0);
        var edited = rating == 99 ? 98 : rating + 1;

        var open = await changes.SetChangeAsync("test_rating", Key(definition), ContentOperation.Update, Rating(edited), "tester");
        var change = Assert.Single(open!.Changes);
        Assert.True(JsonNode.DeepEquals(Rating(rating), change.Before));
        Assert.Equal(rating, await RatingAsync(rt, definition));
        Assert.Equal(rating, rt.Game.Players.Find(resource)!.Rating);

        var validation = await changes.ValidateAsync();
        Assert.True(validation.Ok, string.Join("; ", validation.Errors));
        Assert.Equal(1, validation.Changes);
        Assert.Equal(rating, await RatingAsync(rt, definition));
        Assert.Equal(1, rt.Game.Live.Current.Revision);

        var published = await changes.PublishAsync("raise one card", null);
        Assert.Equal(2, published.Snapshot.Revision);
        Assert.Equal("published", published.Changeset.Status);
        Assert.Equal(edited, await RatingAsync(rt, definition));
        Assert.Equal(edited, rt.Game.Players.Find(resource)!.Rating);
        Assert.Null(await changes.OpenAsync());
        var history = Assert.Single(await changes.HistoryAsync(10));
        Assert.Equal("raise one card", history.Note);
        Assert.Equal("tester", history.CreatedBy);

        // Publishing again finds nothing pending, and outside a publish the catalogue stays insert-only.
        Assert.Equal(409, (await Assert.ThrowsAsync<ContentChangeException>(() => changes.PublishAsync(null, null))).Status);
        var direct = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(rt,
            $"UPDATE ultimateteam.player_card_definitions SET rating=50 WHERE definition_id={definition}"));
        Assert.Equal("23514", direct.SqlState);
    }

    [Fact]
    public async Task ASecondEditKeepsTheFirstBeforeValueAndEditingBackDropsTheChange()
    {
        using var rt = TestRuntime.Create();
        var changes = new ContentChangesets(rt.Game, [new RatingArea()]);
        var (definition, _, rating) = Card(rt, 0);
        await changes.SetChangeAsync("test_rating", Key(definition), ContentOperation.Update, Rating(rating == 99 ? 97 : rating + 1), null);
        var second = await changes.SetChangeAsync("test_rating", Key(definition), ContentOperation.Update, Rating(rating == 99 ? 98 : rating + 2 > 99 ? 99 : rating + 2), null);
        Assert.True(JsonNode.DeepEquals(Rating(rating), Assert.Single(second!.Changes).Before));

        var back = await changes.SetChangeAsync("test_rating", Key(definition), ContentOperation.Update, Rating(rating), null);
        Assert.Empty(back!.Changes);
        Assert.Equal(400, (await Assert.ThrowsAsync<ContentChangeException>(() =>
            changes.SetChangeAsync("test_rating", Key(definition), ContentOperation.Update, Rating(100), null))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ContentChangeException>(() =>
            changes.SetChangeAsync("unknown_area", Key(definition), ContentOperation.Update, Rating(50), null))).Status);
    }

    [Fact]
    public async Task ARowChangedSinceItWasEditedIsAConflictAndNothingPublishes()
    {
        using var rt = TestRuntime.Create();
        var changes = new ContentChangesets(rt.Game, [new RatingArea()]);
        var (first, _, firstRating) = Card(rt, 0);
        var (second, _, secondRating) = Card(rt, 1);
        await changes.SetChangeAsync("test_rating", Key(first), ContentOperation.Update, Rating(firstRating == 99 ? 98 : firstRating + 1), null);
        await changes.SetChangeAsync("test_rating", Key(second), ContentOperation.Update, Rating(secondRating == 99 ? 98 : secondRating + 1), null);
        // Someone else changes the second card meanwhile (with the publish setting, as another publish would).
        await ExecuteAsync(rt, $"""
            BEGIN; SELECT set_config('ultimateteam.content_publish', 'on', true);
            UPDATE ultimateteam.player_card_definitions SET rating={(secondRating == 60 ? 61 : 60)} WHERE definition_id={second}; COMMIT;
            """);

        var validation = await changes.ValidateAsync();
        Assert.False(validation.Ok);
        Assert.Equal(second, Assert.Single(validation.Conflicts).Key["definitionId"]!.GetValue<long>());
        var refused = await Assert.ThrowsAsync<ContentChangeException>(() => changes.PublishAsync(null, null));
        Assert.Equal(409, refused.Status);
        Assert.Single(refused.Conflicts);
        Assert.Equal(firstRating, await RatingAsync(rt, first));
        Assert.Equal(1, rt.Game.Live.Current.Revision);
        Assert.Equal(2, (await changes.OpenAsync())!.Changes.Count);
    }

    [Fact]
    public async Task APublishThatFailsPartWayCommitsNoneOfItsChanges()
    {
        using var rt = TestRuntime.Create();
        var changes = new ContentChangesets(rt.Game, [new RatingArea(), new BrokenArea()]);
        var (good, resource, goodRating) = Card(rt, 0);
        var (bad, _, _) = Card(rt, 1);
        var edited = goodRating == 99 ? 98 : goodRating + 1;
        await changes.SetChangeAsync("test_rating", Key(good), ContentOperation.Update, Rating(edited), null);
        await changes.SetChangeAsync("test_broken", Key(bad), ContentOperation.Update, Rating(1), null);

        var validation = await changes.ValidateAsync();
        Assert.False(validation.Ok);
        Assert.NotEmpty(validation.Errors);
        var refused = await Assert.ThrowsAsync<ContentChangeException>(() => changes.PublishAsync(null, null));
        Assert.Equal(422, refused.Status);
        Assert.Equal(goodRating, await RatingAsync(rt, good));
        Assert.Equal(goodRating, rt.Game.Players.Find(resource)!.Rating);
        Assert.Equal(1, rt.Game.Live.Current.Revision);
        Assert.Equal(2, (await changes.OpenAsync())!.Changes.Count);

        await changes.DiscardAsync();
        Assert.Null(await changes.OpenAsync());
    }

    [Fact]
    public async Task ARevertPublishesTheBeforeValuesThroughTheSameReview()
    {
        using var rt = TestRuntime.Create();
        var changes = new ContentChangesets(rt.Game, [new RatingArea()]);
        var (definition, resource, rating) = Card(rt, 0);
        await changes.SetChangeAsync("test_rating", Key(definition), ContentOperation.Update, Rating(rating == 99 ? 98 : rating + 1), null);
        var published = await changes.PublishAsync(null, null);

        var revert = await changes.RevertAsync(published.Changeset.Id, "tester");
        Assert.Equal(published.Changeset.Id, revert!.RevertsChangesetId);
        Assert.True(JsonNode.DeepEquals(Rating(rating), Assert.Single(revert.Changes).After));
        var reverted = await changes.PublishAsync("undo", null);
        Assert.Equal(3, reverted.Snapshot.Revision);
        Assert.Equal(rating, await RatingAsync(rt, definition));
        Assert.Equal(rating, rt.Game.Players.Find(resource)!.Rating);
        Assert.Equal(2, (await changes.HistoryAsync(10)).Count);

        // A revert needs no other pending edit.
        await changes.SetChangeAsync("test_rating", Key(definition), ContentOperation.Update, Rating(rating == 99 ? 97 : rating + 1), null);
        Assert.Equal(409, (await Assert.ThrowsAsync<ContentChangeException>(() => changes.RevertAsync(published.Changeset.Id, null))).Status);
    }

    // ---- fixtures

    /// <summary>The n-th player card definition: its definition id, native resource id and rating.</summary>
    internal static (long Definition, long Resource, long Rating) Card(TestRuntime rt, int index)
    {
        var catalog = rt.Game.Live.Current.Catalog;
        var card = catalog.Players.OrderBy(p => p.DefinitionId.Value).Skip(index).First();
        return (card.DefinitionId.Value, catalog.DefinitionsById[card.DefinitionId].NativeResourceId!.Value.Value, card.Rating);
    }

    internal static JsonObject Key(long definition) => new() { ["definitionId"] = definition };

    internal static JsonObject Rating(long rating) => new() { ["rating"] = rating };

    private static async Task<long> RatingAsync(TestRuntime rt, long definition)
    {
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using var command = source.CreateCommand($"SELECT rating FROM ultimateteam.player_card_definitions WHERE definition_id={definition}");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(TestRuntime rt, string sql)
    {
        await using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A test-only area: a player card's rating, 1 to 99, updated in place.</summary>
    internal class RatingArea : IContentArea
    {
        public virtual string Name => "test_rating";

        public int ApplyOrder => 0;

        public JsonObject NormalizeKey(JsonObject key) =>
            key["definitionId"] is JsonValue value && value.TryGetValue(out long id) && id > 0 ? Key(id)
                : throw new ContentChangeException(400, "definitionId is required.");

        public async ValueTask<JsonObject?> ReadAsync(IUnitOfWork work, JsonObject key)
        {
            var rows = await work.QueryAsync("SELECT rating FROM ultimateteam.player_card_definitions WHERE definition_id=@id",
                row => row.GetInt64(0), ("id", Id(key)));
            return rows.Count == 0 ? null : Rating(rows[0]);
        }

        public virtual ValueTask ValidateAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after)
        {
            if (operation != ContentOperation.Update) throw new ContentChangeException(400, "Only rating updates.");
            var rating = after!["rating"]!.GetValue<long>();
            if (rating is < 1 or > 99) throw new ContentChangeException(400, "rating is 1 to 99.");
            return ValueTask.CompletedTask;
        }

        public virtual async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after) =>
            await work.ExecuteAsync("UPDATE ultimateteam.player_card_definitions SET rating=@rating WHERE definition_id=@id",
                ("rating", after!["rating"]!.GetValue<long>()), ("id", Id(key)));

        protected static long Id(JsonObject key) => key["definitionId"]!.GetValue<long>();
    }

    /// <summary>A test-only area whose write breaks a database invariant, to fail a publish part-way.</summary>
    private sealed class BrokenArea : RatingArea
    {
        public override string Name => "test_broken";

        public override async ValueTask ApplyAsync(IUnitOfWork work, ContentOperation operation, JsonObject key, JsonObject? after) =>
            await work.ExecuteAsync("UPDATE ultimateteam.player_card_definitions SET rating=NULL WHERE definition_id=@id", ("id", Id(key)));
    }
}
