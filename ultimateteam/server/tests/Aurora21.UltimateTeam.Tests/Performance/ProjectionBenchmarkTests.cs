using Xunit;

namespace Aurora21.UltimateTeam.Tests.Performance;

public sealed class ProjectionBenchmarkTests
{
    [Fact]
    public async Task CurrentSchemaBackfillAndRolledBackWriteTrialsPreserveTheFixture()
    {
        var report = await ProjectionBenchmarkScenario.RunAsync(2, 200, 1, "synthetic-current-schema");
        Assert.True(report["passed"]!.GetValue<bool>());
        Assert.Equal(400, report["ownedPlayers"]!.GetValue<long>());
        Assert.True(report["projectionTotalBytes"]!.GetValue<long>() > 0);
        Assert.True(report["projectionBackfillMs"]!.GetValue<double>() >= 0);
        Assert.Single(report["insertTriggerEnabledSamplesMs"]!.AsArray());
        Assert.Single(report["moveTriggerEnabledSamplesMs"]!.AsArray());
        Assert.Single(report["deleteCascadeEnabledSamplesMs"]!.AsArray());
    }
}
