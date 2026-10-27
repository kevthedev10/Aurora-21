using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Aurora21.UltimateTeam.Server.Http;
using Aurora21.UltimateTeam.Server.Lsx;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// Exact responses of the non-FUT services (LSX, QoS, SSF, EASW, redirector). Clock values are
/// masked. Set ULTIMATETEAM_UPDATE_GOLDEN=1 to rewrite the fixture after an intentional change.
/// </summary>
public sealed partial class ServerPayloadGoldenTests
{
    private static readonly string[] LsxRequests =
    {
        "GetConfig", "GetSettings", "GetInternetConnectedState", "GetUtcTime", "GetAuthToken", "GetAuthCode",
        "GetProfile", "GetBlockList", "QueryFriends", "QueryAreFriends", "QueryContent", "QueryEntitlements",
        "GetPresence", "QueryPresence", "GetWalletBalance", "GetAllGameInfo", "RequestLicense", "SomethingUnmodelled",
    };

    private static readonly string[] LsxSettings = { "LANGUAGE", "ENVIRONMENT", "IS_IGO_AVAILABLE", "IS_IGO_ENABLED", "IS_TELEMETRY_ENABLED", "IS_MANUAL_OFFLINE", "UNKNOWN" };

    private static readonly string[] LsxGameInfo =
    {
        "UPTODATE", "LANGUAGES", "FREETRIAL", "EXPIRATION", "EXPIRATION_DURATION", "INSTALLED_VERSION", "INSTALLED_LANGUAGE",
        "AVAILABLE_VERSION", "DISPLAY_NAME", "FULLGAME_PURCHASED", "FULLGAME_IS_RELEASED", "FULLGAME_RELEASE_DATE",
        "MAX_GROUP_SIZE", "ENTITLEMENT_SOURCE", "UNKNOWN",
    };

    [Fact]
    public async Task ServicePayloadsMatchTheRecordedFixture()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(1700000000));
        var actual = new JsonObject();

        var lsx = new LsxResponder(rt.Game);
        string Request(string name, string attributes = "") =>
            $"<LSX><Request recipient=\"EbisuSDK\" id=\"7\"><{name} {attributes}/></Request></LSX>";
        foreach (var name in LsxRequests)
        {
            actual[$"lsx:{name}"] = MaskClock(lsx.Respond(Request(name)));
        }
        foreach (var setting in LsxSettings)
        {
            actual[$"lsx:GetSetting:{setting}"] = lsx.Respond(Request("GetSetting", $"SettingId=\"{setting}\""));
        }
        foreach (var info in LsxGameInfo)
        {
            actual[$"lsx:GetGameInfo:{info}"] = lsx.Respond(Request("GetGameInfo", $"GameInfoId=\"{info}\""));
        }
        actual["lsx:malformed"] = lsx.Respond("<not xml");

        using var players = new Aurora21.UltimateTeam.Core.Sessions.PlayerRegistry(rt.Fut);
        var http = new HttpEndpoints(players);
        async Task Capture(string key, Func<HttpContext, Task> handler, string method, string path, string query = "", string body = "", params (string, string)[] headers)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.QueryString = new QueryString(query);
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            foreach (var (name, value) in headers)
            {
                context.Request.Headers[name] = value;
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            var responseBody = new MemoryStream();
            context.Response.Body = responseBody;
            await handler(context);
            var headerText = string.Join("\n", context.Response.Headers.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key}: {h.Value}"));
            actual[key] = $"{context.Response.StatusCode}\n{headerText}\n\n{MaskClock(Encoding.UTF8.GetString(responseBody.ToArray()))}";
        }

        await Capture("qos:ping", http.Qos.HandleAsync, "GET", "/qos/qos", "?qtyp=1");
        await Capture("qos:bandwidth", http.Qos.HandleAsync, "GET", "/qos/qos", "?qtyp=2");
        await Capture("qos:firewall", http.Qos.HandleAsync, "GET", "/qos/firewall");
        await Capture("qos:firetype", http.Qos.HandleAsync, "GET", "/qos/firetype");
        await Capture("qos:missing", http.Qos.HandleAsync, "GET", "/qos/missing");
        await Capture("qos:post", http.Qos.HandleAsync, "POST", "/qos/qos");
        await Capture("ssf:auth", http.Ssf.HandleAsync, "POST", "/sf/auth", body: "{}", headers: ("X-SF-SID", "session-1"));
        await Capture("ssf:auth-no-session", http.Ssf.HandleAsync, "POST", "/sf/auth", body: "{}");
        await Capture("ssf:missing", http.Ssf.HandleAsync, "GET", "/sf/other");
        await Capture("ssf:put", http.Ssf.HandleAsync, "PUT", "/sf/auth");
        await Capture("easw:extension", http.Easw.HandleAsync, "POST", "/extension", body: "<x/>");
        await Capture("easw:other-post", http.Easw.HandleAsync, "POST", "/other", body: "<x/>");
        await Capture("easw:roster", http.Easw.HandleAsync, "GET", "/roster");
        await Capture("easw:routing", http.Easw.HandleAsync, "GET", "/routing");
        await Capture("easw:routing-file", http.Easw.HandleAsync, "GET", "/routing/cfgrouting.xml");
        await Capture("easw:ctl", http.Easw.HandleAsync, "GET", "/ctl");
        await Capture("easw:content-missing", http.Easw.HandleAsync, "GET", "/contentfifa/does/not/exist.xml");
        await Capture("easw:unknown", http.Easw.HandleAsync, "GET", "/nowhere");
        await Capture("easw:delete", http.Easw.HandleAsync, "DELETE", "/roster");
        await Capture("redirect:get", http.Redirector.HandleCleartextAsync, "GET", "/redirector/getServerInstance");
        await Capture("redirect:put", http.Redirector.HandleCleartextAsync, "PUT", "/redirector/getServerInstance");
        actual["redirector:tls"] = Encoding.UTF8.GetString(RedirectorEndpoint.ServerInstance(44321));

        var serialized = actual.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";
        if (Environment.GetEnvironmentVariable("ULTIMATETEAM_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(Path.Combine(SourceFixtures(), "server-payloads.json"), serialized);
            return;
        }
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "server-payloads.json")))!.AsObject();
        Assert.Equal(expected.Select(e => e.Key), actual.Select(e => e.Key));
        foreach (var (key, value) in expected)
        {
            Assert.True(value!.GetValue<string>() == actual[key]!.GetValue<string>(), $"{key} differs:\n{actual[key]}");
        }
    }

    private static string MaskClock(string text) => ClockPattern().Replace(text, "<clock>");

    private static string SourceFixtures()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var relative in new[] { Path.Combine("tests", "Aurora21.UltimateTeam.Tests", "Fixtures"),
                Path.Combine("ultimateteam", "server", "tests", "Aurora21.UltimateTeam.Tests", "Fixtures") })
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        throw new DirectoryNotFoundException("test Fixtures folder");
    }

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z?")]
    private static partial Regex ClockPattern();
}
