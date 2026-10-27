using Aurora21.UltimateTeam.Core.Helpers;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class BackgroundLineWriterTests
{
    [Fact]
    public void MultipleFlushBatchesStillDrainEveryAcceptedLine()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aurora21-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "test.log");
            using var writer = new BackgroundLineWriter(path, capacity: 600, flushInterval: TimeSpan.FromSeconds(1));
            for (var i = 0; i < 600; i++) Assert.True(writer.WriteLine(i.ToString()));
            writer.Dispose();
            Assert.Equal(Enumerable.Range(0, 600).Select(i => i.ToString()), File.ReadAllLines(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RoutineOverflowCannotConsumeTheImportantReserveAndAcceptedOrderIsPreserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aurora21-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "test.log");
            using var writer = new BackgroundLineWriter(path, capacity: 1, importantCapacity: 1,
                flushInterval: TimeSpan.FromSeconds(1));
            Assert.True(writer.WriteLine("routine"));
            Assert.False(writer.WriteLine("discarded"));
            Assert.True(writer.WriteLine("error", important: true));
            Assert.False(writer.WriteLine("discarded-error", important: true));
            writer.Dispose();
            var lines = File.ReadAllLines(path);
            Assert.Equal("routine", lines[0]);
            Assert.Equal("error", lines[1]);
            Assert.Contains("skipped routine=1 important=1", lines[2]);
            Assert.Equal(1, writer.DroppedLines);
            Assert.Equal(1, writer.DroppedImportantLines);
            Assert.False(writer.WriteLine("after-stop"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CharacterBudgetsBoundLargeEntriesIndependentlyOfQueueCount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aurora21-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var writer = new BackgroundLineWriter(Path.Combine(directory, "test.log"),
                maximumQueuedCharacters: 3, maximumImportantCharacters: 2,
                flushInterval: TimeSpan.FromSeconds(1));
            Assert.True(writer.WriteLine("abc"));
            Assert.False(writer.WriteLine("x"));
            Assert.True(writer.WriteLine("!!", important: true));
            Assert.False(writer.WriteLine("!", important: true));
            writer.Dispose();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
