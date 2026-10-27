using Aurora21.UltimateTeam.Server.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aurora21-filelog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static void WriteLines(FileLoggerProvider provider, int count)
    {
        var log = provider.CreateLogger("test");
        for (var index = 0; index < count; index++) log.LogInformation("line {Index} {Padding}", index, new string('x', 40));
    }

    [Fact]
    public void ACapRotatesTheServerLogToOneOlderFile()
    {
        var path = Path.Combine(_directory, "ultimateteam.log");
        using (var provider = new FileLoggerProvider(path, maxBytes: 1024)) WriteLines(provider, 100);
        Assert.True(File.Exists(path + ".1"));
        Assert.True(new FileInfo(path).Length <= 1024);
        Assert.False(File.Exists(path + ".2"));
    }

    [Fact]
    public void NoCapKeepsOneGrowingFileAsBefore()
    {
        var path = Path.Combine(_directory, "ultimateteam.log");
        using (var provider = new FileLoggerProvider(path)) WriteLines(provider, 100);
        Assert.False(File.Exists(path + ".1"));
        Assert.True(new FileInfo(path).Length > 1024);
    }
}
