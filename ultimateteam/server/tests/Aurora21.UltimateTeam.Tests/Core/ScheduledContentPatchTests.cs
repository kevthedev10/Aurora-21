using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class ScheduledContentPatchTests
{
    private static JsonObject Row(string json) => JsonNode.Parse(json)!.AsObject();
    private static JsonObject Rebase(string baseline, string desired, string live, string area = "rules") =>
        ScheduledContentPatch.Rebase(area, new JsonObject { ["file"] = "objectives" }, ContentOperation.Update,
            Row(baseline), Row(desired), Row(live))!;

    [Fact]
    public void IndependentScalarEditsMergeWithoutMutatingInputs()
    {
        var baseline = Row("{\"name\":\"A\",\"weight\":1}");
        var desired = Row("{\"name\":\"B\",\"weight\":1}");
        var live = Row("{\"name\":\"A\",\"weight\":2}");
        var result = ScheduledContentPatch.Rebase("promo", new(), ContentOperation.Update, baseline, desired, live)!;
        Assert.True(JsonNode.DeepEquals(Row("{\"name\":\"B\",\"weight\":2}"), result));
        result["weight"] = 3;
        Assert.Equal(2, live["weight"]!.GetValue<int>());
        Assert.Equal("A", baseline["name"]!.GetValue<string>());
    }

    [Fact]
    public void DivergentEditsToSameScalarConflict()
    {
        var error = Assert.Throws<ContentChangeException>(() => Rebase("{\"name\":\"A\"}", "{\"name\":\"B\"}", "{\"name\":\"C\"}"));
        Assert.Equal(409, error.Status);
        Assert.Contains("/name", error.Message);
    }

    [Fact]
    public void EqualOverlappingEditsAreIdempotent()
    {
        Assert.True(JsonNode.DeepEquals(Row("{\"name\":\"B\"}"), Rebase("{\"name\":\"A\"}", "{\"name\":\"B\"}", "{\"name\":\"B\"}")));
    }

    [Theory]
    [InlineData("packId")]
    [InlineData("setKey")]
    [InlineData("group")]
    [InlineData("id")]
    [InlineData("code")]
    public void StableRowsRetainEarlierAdditionsAndApplyIndependentRemoval(string field)
    {
        var baseline = $"{{\"listing\":[{{\"{field}\":\"a\",\"enabled\":true}},{{\"{field}\":\"b\",\"enabled\":true}}]}}";
        var desired = $"{{\"listing\":[{{\"{field}\":\"b\",\"enabled\":true}}]}}";
        var live = $"{{\"listing\":[{{\"{field}\":\"a\",\"enabled\":true}},{{\"{field}\":\"b\",\"enabled\":false}},{{\"{field}\":\"c\",\"enabled\":true}}]}}";
        var result = Rebase(baseline, desired, live)["listing"]!.AsArray();
        Assert.Equal(2, result.Count);
        Assert.Equal("b", result[0]![field]!.GetValue<string>());
        Assert.False(result[0]!["enabled"]!.GetValue<bool>());
        Assert.Equal("c", result[1]![field]!.GetValue<string>());
    }

    [Fact]
    public void ChangedItemCannotBeRemovedByStaleDraft()
    {
        Assert.Equal(409, Assert.Throws<ContentChangeException>(() => Rebase(
            "{\"listing\":[{\"packId\":1,\"price\":100}]}", "{\"listing\":[]}",
            "{\"listing\":[{\"packId\":1,\"price\":200}]}")).Status);
    }

    [Fact]
    public void NestedObjectiveChangesMergeByGroupAndObjectiveId()
    {
        var result = Rebase("{\"value\":[{\"id\":1,\"objectives\":[{\"id\":10,\"target\":5}]}]}",
            "{\"value\":[{\"id\":1,\"objectives\":[{\"id\":10,\"target\":6}]}]}",
            "{\"value\":[{\"id\":1,\"objectives\":[{\"id\":10,\"target\":5},{\"id\":11,\"target\":2}]},{\"id\":2,\"objectives\":[]}]}");
        Assert.Equal(2, result["value"]!.AsArray().Count);
        var objectives = result["value"]![0]!["objectives"]!.AsArray();
        Assert.Equal(2, objectives.Count);
        Assert.Equal(6, objectives[0]!["target"]!.GetValue<int>());
    }

    [Fact]
    public void PromoMembershipAndReductionDeltasPreserveOtherCards()
    {
        var result = Rebase("{\"buckets\":[{\"code\":\"main\",\"cards\":[1,2]}],\"card_reductions\":{\"1\":2}}",
            "{\"buckets\":[{\"code\":\"main\",\"cards\":[2,3]}],\"card_reductions\":{\"3\":4}}",
            "{\"buckets\":[{\"code\":\"main\",\"cards\":[1,2,4]}],\"card_reductions\":{\"1\":2,\"4\":5}}", "promo");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[2,4,3]"), result["buckets"]![0]!["cards"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"3\":4,\"4\":5}"), result["card_reductions"]));
    }

    [Fact]
    public void ConflictingMovesAcrossBucketsAreRejected()
    {
        Assert.Equal(409, Assert.Throws<ContentChangeException>(() => Rebase(
            "{\"buckets\":[{\"code\":\"a\",\"cards\":[1]},{\"code\":\"b\",\"cards\":[]},{\"code\":\"c\",\"cards\":[]}]}",
            "{\"buckets\":[{\"code\":\"a\",\"cards\":[]},{\"code\":\"b\",\"cards\":[1]},{\"code\":\"c\",\"cards\":[]}]}",
            "{\"buckets\":[{\"code\":\"a\",\"cards\":[]},{\"code\":\"b\",\"cards\":[]},{\"code\":\"c\",\"cards\":[1]}]}", "promo")).Status);
    }

    [Fact]
    public void OrdinaryArraysRejectDivergentReplacement()
    {
        Assert.Equal(409, Assert.Throws<ContentChangeException>(() => Rebase("{\"value\":[1,2]}", "{\"value\":[1,3]}", "{\"value\":[1,4]}" )).Status);
    }

    [Fact]
    public void MissingPropertyAndExplicitNullAreDistinct()
    {
        Assert.Equal(409, Assert.Throws<ContentChangeException>(() => Rebase("{\"a\":1}", "{}", "{\"a\":null}" )).Status);
    }

    [Fact]
    public void InsertAndHideRefuseStaleRows()
    {
        var row = Row("{\"a\":1}");
        Assert.Equal(409, Assert.Throws<ContentChangeException>(() => ScheduledContentPatch.Rebase("promo", new(), ContentOperation.Insert, null, row, row)).Status);
        Assert.Equal(409, Assert.Throws<ContentChangeException>(() => ScheduledContentPatch.Rebase("promo", new(), ContentOperation.Hide, row, null, Row("{\"a\":2}"))).Status);
        Assert.Equal(409, Assert.Throws<ContentChangeException>(() => ScheduledContentPatch.Rebase("promo", new(), ContentOperation.Update, row, Row("{\"a\":2}"), null)).Status);
        Assert.Null(ScheduledContentPatch.Rebase("promo", new(), ContentOperation.Hide, row, null, row));
    }
}
