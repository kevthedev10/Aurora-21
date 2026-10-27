using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class UnknownRequestLogTests
{
    private const string G = "/ut/game/fifa21";

    private static FutRequest Request(string method, string target, string body = "", string contentType = "application/json") =>
        new(method, target, new[] { KeyValuePair.Create("Content-Type", contentType) }, Encoding.UTF8.GetBytes(body));

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ultimateteam21-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void RepeatedEndpointsAreGroupedCountedAndReportedNewOnce()
    {
        var directory = TempDirectory();
        var log = new UnknownRequestLog(directory);
        var news = new[]
        {
            log.Record(Request("GET", G + "/trade/123/history?limit=5")),
            log.Record(Request("GET", G + "/trade/456/history")),
            log.Record(Request("GET", G + "/trade/789/history")),
            log.Record(Request("POST", G + "/something/new", "{}")),
        };
        log.Dispose();

        Assert.Equal(new[] { true, false, false, true }, news);
        var lines = File.ReadAllLines(Path.Combine(directory, UnknownRequestLog.FileName));
        Assert.Equal(4, lines.Length);
        var first = JsonNode.Parse(lines[0])!.AsObject();
        Assert.Equal(G + "/trade/123/history", first["path"]!.GetValue<string>());
        Assert.Equal(G + "/trade/{id}/history", first["endpoint"]!.GetValue<string>());
        Assert.Equal("limit", Assert.Single(first["queryKeys"]!.AsArray())!.GetValue<string>());
        Assert.DoesNotContain("=5", lines[0]);

        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, UnknownRequestLog.SummaryFileName)))!["endpoints"]!.AsArray();
        Assert.Equal(2, summary.Count);
        Assert.Equal(G + "/trade/{id}/history", summary[0]!["endpoint"]!.GetValue<string>());
        Assert.Equal(3, summary[0]!["count"]!.GetValue<long>());
        Assert.Equal(1, summary[1]!["count"]!.GetValue<long>());
    }

    [Fact]
    public void CredentialLikeBodyFieldsAreRedacted()
    {
        var json = UnknownRequestLog.RedactedBody(Request("POST", G + "/x",
            """{"token":"abc","nested":{"authCode":"secret-value"},"items":[{"sessionId":"s"}],"rating":87}"""))!;
        var parsed = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(UnknownRequestLog.Redacted, parsed["token"]!.GetValue<string>());
        Assert.Equal(UnknownRequestLog.Redacted, parsed["nested"]!["authCode"]!.GetValue<string>());
        Assert.Equal(UnknownRequestLog.Redacted, parsed["items"]![0]!["sessionId"]!.GetValue<string>());
        Assert.Equal(87, parsed["rating"]!.GetValue<long>());
        Assert.DoesNotContain("secret-value", json);

        var form = UnknownRequestLog.RedactedBody(Request("POST", G + "/x", "page=2&access_token=abc123&mode=x", "application/x-www-form-urlencoded"))!;
        Assert.Contains("page=2", form);
        Assert.Contains("mode=x", form);
        Assert.DoesNotContain("abc123", form);
        Assert.Null(UnknownRequestLog.RedactedBody(Request("GET", G + "/x")));
    }

    [Fact]
    public void TheLineWriterRotatesAtItsSizeCapAndDrainsOnDispose()
    {
        var path = Path.Combine(TempDirectory(), "lines.log");
        var writer = new BackgroundLineWriter(path, maxBytes: 200);
        for (var index = 0; index < 20; index++)
        {
            writer.WriteLine("line " + index.ToString("D2") + new string('x', 20));
        }
        writer.Dispose();
        Assert.True(File.Exists(path + ".1"));
        Assert.True(new FileInfo(path).Length <= 200);
        // Order is kept: the newest line ends the current file.
        Assert.StartsWith("line 19", File.ReadAllLines(path)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRouterStillAnswersUnknownRequestsAndRecordsThem()
    {
        using var rt = TestRuntime.Create(marketTarget: 0, time: new TestRuntime.FixedTime(1_700_000_000));
        var response = await rt.Router.HandleAsync(Request("GET", G + "/definitely/not/a/route/42"));
        Assert.Equal(200, response.Status);
        Assert.Equal("{}", Encoding.UTF8.GetString(response.Body));
        Assert.Contains(response.Headers, header => header.Key == "X-UltimateTeam-Unknown");
        var endpoint = Assert.Single(rt.Game.UnknownRequests!.Summary()["endpoints"]!.AsArray())!;
        Assert.Equal(G + "/definitely/not/a/route/{id}", endpoint["endpoint"]!.GetValue<string>());
    }
}
