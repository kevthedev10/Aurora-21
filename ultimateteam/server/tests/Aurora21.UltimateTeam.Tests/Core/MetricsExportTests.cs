using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Server.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class MetricsExportTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoEndpointExportsNothing(string? endpoint) =>
        Assert.Null(MetricsExport.Start(endpoint, NullLogger.Instance));

    [Theory]
    [InlineData("127.0.0.1:9090")]
    [InlineData("ftp://127.0.0.1:9090/api/v1/otlp/v1/metrics")]
    public void AnEndpointThatIsNotAnHttpUrlIsRefused(string endpoint) =>
        Assert.Throws<UsageException>(() => MetricsExport.Start(endpoint, NullLogger.Instance));

    [Fact]
    public void AnEndpointStartsAProviderForTheServerMeters()
    {
        // Nothing listens on the discard port; the exporter only sends on its interval and on shutdown.
        using var provider = MetricsExport.Start("http://127.0.0.1:9/api/v1/otlp/v1/metrics", NullLogger.Instance);
        Assert.NotNull(provider);
        Assert.Contains(DatabaseTimings.MeterName, MetricsExport.Meters);
        Assert.True(DatabaseTimings.Enabled);
    }
}
