using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>
/// The one-shot HTTP fallback accepted on the Blaze port: read a single request, route it
/// through the FUT API and close the connection.
/// </summary>
internal static class RawHttpExchange
{
    private const int MaxHeaderBytes = 1024 * 1024;
    private const int VerbProbeBytes = 8;

    private static readonly byte[][] Verbs =
    {
        "GET "u8.ToArray(), "POST "u8.ToArray(), "PUT "u8.ToArray(), "DELETE "u8.ToArray(), "OPTIONS "u8.ToArray(), "HEAD "u8.ToArray(),
    };

    private static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;

    public static bool LooksLikeHttp(ReadOnlySequence<byte> buffer)
    {
        var prefix = buffer.Slice(0, Math.Min(VerbProbeBytes, buffer.Length)).ToArray();
        return Verbs.Any(verb => prefix.AsSpan().StartsWith(verb));
    }

    public static async Task HandleAsync(PipeReader input, PipeWriter output, Func<FutRequest, CancellationToken, ValueTask<FutResponse>> route, string sid,
        CancellationToken cancellation)
    {
        var head = await ReadHeadAsync(input, cancellation);
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return;
        }
        var headers = lines.Skip(1)
            .Select(line => (Line: line, Colon: line.IndexOf(':')))
            .Where(entry => entry.Colon >= 0)
            .Select(entry => KeyValuePair.Create(entry.Line[..entry.Colon].Trim(), entry.Line[(entry.Colon + 1)..].Trim()))
            .ToList();
        var contentLength = headers
            .Where(header => header.Key == "Content-Length")
            .Select(header => int.TryParse(header.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ? Math.Max(0, length) : (int?)null)
            .OfType<int>()
            .LastOrDefault();
        var body = await ReadBodyAsync(input, contentLength, cancellation);

        var response = await route(new FutRequest(requestLine[0], requestLine[1], headers, body), cancellation);
        await WriteResponseAsync(output, response, sid, cancellation);
    }

    /// <summary>The header block as Latin-1 text, consuming it and its blank line (or everything read, when there is none).</summary>
    private static async Task<string> ReadHeadAsync(PipeReader input, CancellationToken cancellation)
    {
        while (true)
        {
            var result = await input.ReadAsync(cancellation);
            var buffer = result.Buffer;
            if (TryFindHead(buffer, out var head, out var afterHead))
            {
                var text = Encoding.Latin1.GetString(head.ToArray());
                input.AdvanceTo(afterHead);
                return text;
            }
            if (result.IsCompleted || buffer.Length >= MaxHeaderBytes)
            {
                var text = Encoding.Latin1.GetString(buffer.ToArray());
                input.AdvanceTo(buffer.End);
                return text;
            }
            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static bool TryFindHead(ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> head, out SequencePosition afterHead)
    {
        var reader = new SequenceReader<byte>(buffer);
        var found = reader.TryReadTo(out head, HeaderTerminator, advancePastDelimiter: true);
        afterHead = reader.Position;
        return found;
    }

    /// <summary>Up to <paramref name="length"/> body bytes; fewer when the client closes early.</summary>
    private static async Task<byte[]> ReadBodyAsync(PipeReader input, int length, CancellationToken cancellation)
    {
        if (length == 0)
        {
            return Array.Empty<byte>();
        }
        while (true)
        {
            var result = await input.ReadAsync(cancellation);
            var buffer = result.Buffer;
            if (buffer.Length >= length || result.IsCompleted)
            {
                var body = buffer.Slice(0, Math.Min(length, buffer.Length));
                var bytes = body.ToArray();
                input.AdvanceTo(body.End);
                return bytes;
            }
            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static async Task WriteResponseAsync(PipeWriter output, FutResponse response, string sid, CancellationToken cancellation)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Content-Type", FutResponse.JsonContentType),
            new("Content-Length", response.Body.Length.ToString(CultureInfo.InvariantCulture)),
            new("Connection", "close"),
            new("X-UT-SID", sid),
        };
        // Headers set by the route replace the defaults of the same name.
        foreach (var header in response.Headers)
        {
            headers.RemoveAll(existing => existing.Key == header.Key);
            headers.Add(header);
        }
        var head = new StringBuilder($"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Error")}\r\n");
        foreach (var (name, value) in headers)
        {
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        head.Append("\r\n");
        await output.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellation);
        await output.WriteAsync(response.Body, cancellation);
        await output.FlushAsync(cancellation);
    }
}
