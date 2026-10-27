using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Services.Auth;
using Aurora21.UltimateTeam.Protocol.Fire2;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Server.Blaze;
using Aurora21.UltimateTeam.Server.Hosting;
using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

public sealed class TlsProbeConfigurationTests
{
    [Theory]
    [InlineData(TlsProbeMode.Off, false, false)]
    [InlineData(TlsProbeMode.Fut, true, false)]
    [InlineData(TlsProbeMode.Nucleus, false, true)]
    [InlineData(TlsProbeMode.Both, true, true)]
    public async Task OnlySelectedGameUrlsUseTheProbe(TlsProbeMode mode, bool futTls, bool nucleusTls)
    {
        using var runtime = TestRuntime.Create();
        var probe = new TlsProbeOptions(mode, TlsProbeOptions.DefaultPort);
        var responses = new BlazeResponses(runtime.Fut, probe);
        var preAuth = await responses.RespondAsync(Request(BlazeComponent.Util, UtilCommand.PreAuth), CancellationToken.None);
        var preAuthConfig = TdfReader.FindStringMap(preAuth.Payload, "CONF");
        Assert.Equal(nucleusTls ? probe.BaseUrl : "http://127.0.0.1:8090/", preAuthConfig["nucleusConnect"]);
        Assert.Equal("http://127.0.0.1:8090/ultimateteam/callback", preAuthConfig["identityRedirectUri"]);

        var core = await responses.RespondAsync(Request(BlazeComponent.Util, UtilCommand.FetchClientConfig,
            new TdfWriter().Str("CFID", "OSDK_CORE").ToArray()), CancellationToken.None);
        var coreConfig = TdfReader.FindStringMap(core.Payload, "CONF");
        Assert.Equal(futTls ? probe.BaseUrl : "http://127.0.0.1:8090/", coreConfig["FUT_RS4_BASE_URL"]);
        Assert.Equal(futTls ? probe.BaseUrl : "http://127.0.0.1:8090/", coreConfig["FIFA_POW_URL"]);
        Assert.Equal("http://127.0.0.1:42232/content", coreConfig["CONTENT_URL"]);
        Assert.Equal("http://127.0.0.1:8090/", LocalAuthService.Config(Ports.Fut)
            .Single(entry => entry.Key == "nucleusConnect").Value);
    }

    [Fact]
    public async Task AllModeMovesEveryAdvertisedHttpServiceToTls()
    {
        using var runtime = TestRuntime.Create();
        var probe = new TlsProbeOptions(TlsProbeMode.All, TlsProbeOptions.DefaultPort);
        var responses = new BlazeResponses(runtime.Fut, probe);
        var core = await responses.RespondAsync(Request(BlazeComponent.Util, UtilCommand.FetchClientConfig,
            new TdfWriter().Str("CFID", "OSDK_CORE").ToArray()), CancellationToken.None);
        var config = TdfReader.FindStringMap(core.Payload, "CONF");
        var easw = $"https://{TlsProbeOptions.Hostname}:{TlsProbeOptions.DefaultPort + 2}";
        var ssf = $"https://{TlsProbeOptions.Hostname}:{TlsProbeOptions.DefaultPort + 3}";
        Assert.Equal(easw + "/content", config["CONTENT_URL"]);
        Assert.Equal(easw + "/roster", config["ROSTERUPDATE_URL"]);
        Assert.Equal(easw, config["OSDK_EASW_AUTH_URL"]);
        Assert.Equal(ssf, config["FIFA_SSF_URL"]);
        Assert.Equal(probe.BaseUrl, config["FIFA_POW_NUCLEUS_PROXY_URL"]);
        // No advertised URL is left on plain HTTP; only the scheme-less dynamic-message host remains (public addressing).
        Assert.DoesNotContain(config.Values, value => value.StartsWith("http://", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "127.0.0.1")]
    [InlineData(true, TlsProbeOptions.Hostname)]
    public async Task AddressFieldsNameTheAdvertisedHost(bool advertiseHostname, string expected)
    {
        using var runtime = TestRuntime.Create();
        var responses = new BlazeResponses(runtime.Fut, new TlsProbeOptions(TlsProbeMode.All, TlsProbeOptions.DefaultPort) { AdvertiseHostname = advertiseHostname });
        var preAuth = await responses.RespondAsync(Request(BlazeComponent.Util, UtilCommand.PreAuth), CancellationToken.None);
        Assert.Equal(expected, TdfReader.FindString(preAuth.Payload, "PSA"));
        var postAuth = await responses.RespondAsync(Request(BlazeComponent.Util, UtilCommand.PostAuth), CancellationToken.None);
        Assert.All(TdfReader.FindAllStrings(postAuth.Payload, "ADRS"), address => Assert.Equal(expected, address));
        Assert.Equal(2, TdfReader.FindAllStrings(postAuth.Payload, "ADRS").Count);
    }

    [Fact]
    public void TheRedirectorAdvertisesSecureBlazeOnlyForTheBlazeTlsProbe()
    {
        // Unchanged default: plaintext Blaze on 127.0.0.1:44321.
        var plain = System.Text.Encoding.UTF8.GetString(Server.Http.RedirectorEndpoint.ServerInstance(Ports.Blaze));
        Assert.Contains("<hostname>127.0.0.1</hostname>", plain);
        Assert.Contains($"<port>{Ports.Blaze}</port>", plain);
        Assert.Contains("<secure>0</secure>", plain);

        // The probe: secure Blaze, named by the redirector certificate's host so FIFA can match the certificate.
        var secure = System.Text.Encoding.UTF8.GetString(Server.Http.RedirectorEndpoint.ServerInstance(18444, secure: true));
        Assert.Contains($"<hostname>{TlsProbeOptions.Hostname}</hostname>", secure);
        Assert.Contains("<ip>2130706433</ip>", secure);
        Assert.Contains("<port>18444</port>", secure);
        Assert.Contains("<secure>1</secure>", secure);
    }

    [Theory]
    [InlineData("127.0.0.1", 44321)] // a production port
    [InlineData("127.0.0.1", 80)] // privileged
    [InlineData("127.0.0.1", 18443)] // the HTTPS probe's port
    [InlineData("0.0.0.0", 18444)] // not loopback
    public async Task TheBlazeTlsProbeNeedsLoopbackAndAPortOfItsOwn(string host, int port)
    {
        var options = new ServeOptions(host, LsxMode.Off, null, false)
        {
            // The HTTPS probe only where its port is the collision under test.
            TlsProbe = port == TlsProbeOptions.DefaultPort ? new TlsProbeOptions(TlsProbeMode.Fut, TlsProbeOptions.DefaultPort) : TlsProbeOptions.Off,
            BlazeTlsProbePort = port,
        };
        var error = await Assert.ThrowsAsync<UsageException>(() => ServeCommand.RunAsync(options, null));
        Assert.Contains("Blaze TLS probe", error.Message);
    }

    /// <summary>Each connection's PreAuth gives FIFA its own machine id (MAID): it comes back as MACI in every network address.</summary>
    [Fact]
    public async Task EveryConnectionGetsItsOwnMachineId()
    {
        using var runtime = TestRuntime.Create();
        async Task<long> MachineId() => TdfReader.FindInteger((await new BlazeResponses(runtime.Fut)
            .RespondAsync(Request(BlazeComponent.Util, UtilCommand.PreAuth), CancellationToken.None)).Payload, "MAID") ?? 0;
        var ids = new[] { await MachineId(), await MachineId(), await MachineId() };
        Assert.All(ids, id => Assert.InRange(id, 1, int.MaxValue - 1));
        Assert.Equal(3, ids.Distinct().Count());
        Assert.DoesNotContain(runtime.Game.Config.PersonaId, ids);
    }

    private static Fire2Frame Request(int component, int command, byte[]? payload = null) =>
        Fire2Frame.Parse(Fire2Frame.Build(component, command, 1, Fire2MessageType.Message, payload ?? Array.Empty<byte>()));
}
