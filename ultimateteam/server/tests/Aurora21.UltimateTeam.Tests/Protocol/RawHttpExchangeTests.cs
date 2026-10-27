using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Web;
using Aurora21.UltimateTeam.Server.Http;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

public sealed class RawHttpExchangeTests
{
    [Fact]
    public async Task ReadsASplitRequestAndWritesOneResponse()
    {
        var input = new Pipe();
        var output = new Pipe();
        FutRequest? seen = null;
        ValueTask<FutResponse> Route(FutRequest request, CancellationToken cancellation)
        {
            seen = request;
            return ValueTask.FromResult(FutResponse.Json(new JsonObject { ["ok"] = true }, 200, ("Connection", "keep-alive")));
        }

        var exchange = RawHttpExchange.HandleAsync(input.Reader, output.Writer, Route, "SID-1", CancellationToken.None);
        await input.Writer.WriteAsync(Encoding.ASCII.GetBytes("POST /ut/game/fifa21/item HTTP/1.1\r\nContent-Length: 11\r\nX-Test: a\r\n\r\n{\"id\""));
        await input.Writer.WriteAsync(Encoding.ASCII.GetBytes(":12}trailing"));
        await exchange;
        await output.Writer.CompleteAsync();

        Assert.NotNull(seen);
        Assert.Equal("POST", seen!.Method);
        Assert.Equal("/ut/game/fifa21/item", seen.Lower);
        Assert.Equal("a", seen.Headers["X-Test"]);
        Assert.Equal("{\"id\":12}tr", Encoding.UTF8.GetString(seen.Body));

        var written = (await output.Reader.ReadAsync()).Buffer;
        var text = Encoding.ASCII.GetString(written.ToArray());
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", text);
        Assert.Contains("X-UT-SID: SID-1\r\n", text);
        Assert.Contains("Connection: keep-alive\r\n", text);
        Assert.DoesNotContain("Connection: close", text);
        Assert.EndsWith("\r\n\r\n{\"ok\":true}", text);
    }

    [Fact]
    public void RecognisesHttpVerbsOnly()
    {
        Assert.True(RawHttpExchange.LooksLikeHttp(new(Encoding.ASCII.GetBytes("GET / HTTP/1.1"))));
        Assert.False(RawHttpExchange.LooksLikeHttp(new(new byte[] { 0, 0, 0, 12, 0, 9, 0, 7 })));
    }
}
