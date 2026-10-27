using Aurora21.UltimateTeam.Server.Hosting;
using Aurora21.UltimateTeam.Core.Config;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// The hosted serve profile (native-hosting-investigation.md): players reach the server over the network, so it must
/// never fall back to an address-based identity, plaintext game traffic or a local LSX. Contradicting options are
/// refused before anything starts.
/// </summary>
public sealed class HostedProfileTests
{
    private static ServeOptions Hosted(string host = "0.0.0.0") => new(host, LsxMode.Off, null, false)
    {
        Hosted = true,
        IdentityMode = PlayerIdentityMode.Session,
        TlsProbe = new TlsProbeOptions(TlsProbeMode.All, TlsProbeOptions.DefaultPort) { AdvertiseHostname = true },
        BlazeTlsProbePort = TlsProbeOptions.DefaultPort + 1,
    };

    public static TheoryData<string, ServeOptions> Contradictions => new()
    {
        { "single identity", Hosted() with { IdentityMode = PlayerIdentityMode.Single } },
        { "peer-IP identity", Hosted() with { IdentityMode = PlayerIdentityMode.PeerIp } },
        { "FUT-only TLS", Hosted() with { TlsProbe = new TlsProbeOptions(TlsProbeMode.Both, TlsProbeOptions.DefaultPort) { AdvertiseHostname = true } } },
        { "plaintext Blaze", Hosted() with { BlazeTlsProbePort = null } },
        { "loopback addresses", Hosted() with { TlsProbe = new TlsProbeOptions(TlsProbeMode.All, TlsProbeOptions.DefaultPort) } },
        { "local LSX", Hosted() with { LsxMode = LsxMode.Auto } },
        { "local session test", Hosted() with { LocalSessionTest = true } },
        { "HTTP-only test", Hosted() with { HttpOnlyTestPort = 19090 } },
        { "launch API without hosted profile", new ServeOptions("127.0.0.1", LsxMode.Off, null, false) { LaunchApi = Launch(18447) } },
        { "launch API on a game port", Hosted() with { LaunchApi = Launch(8090) } },
        { "launch API with relative key", Hosted() with { LaunchApi = Launch(18447) with { PublicKeyPath = "issuer.pem" } } },
        { "launch issuer without a key", Hosted() with { LaunchApi = Launch(18447) with { PublicKeyPath = null } } },
        { "launch issuer with both keys", Hosted() with { LaunchApi = Launch(18447) with { SecretPath = Path.GetFullPath("secret.txt") } } },
        { "launch key without an issuer", Hosted() with { LaunchApi = Launch(18447) with { Issuer = null } } },
    };

    // A panel without the hosted profile is allowed since step 6 of live-content-editing-plan.md: a local server serves it on
    // loopback for rules and content editing. Its port and backup folder are still checked, hosted or not.
    public static TheoryData<string, ServeOptions> PanelContradictions => new()
    {
        { "panel API shares launch port", Hosted() with { LaunchApi = Launch(18447), PanelApi = new PanelApiOptions(18447) } },
        { "panel API relative backup folder", Hosted() with { PanelApi = new PanelApiOptions(18448, "backups") } },
        { "local panel API relative backup folder", new ServeOptions("127.0.0.1", LsxMode.Off, null, false) { PanelApi = new PanelApiOptions(18448, "backups") } },
    };

    private static LaunchApiOptions Launch(int port) => new(port)
    {
        Issuer = "https://accounts.example.test",
        PublicKeyPath = Path.GetFullPath("issuer.pem"),
    };

    [Theory]
    [MemberData(nameof(Contradictions))]
    public async Task ContradictingOptionsAreRefusedBeforeAnythingStarts(string _, ServeOptions options)
    {
        var error = await Assert.ThrowsAsync<UsageException>(() => ServeCommand.RunAsync(options, null));
        Assert.Contains("--hosted", error.Message);
    }

    [Theory]
    [MemberData(nameof(PanelContradictions))]
    public async Task PanelOptionsAreCheckedBeforeAnythingStarts(string _, ServeOptions options)
    {
        var error = await Assert.ThrowsAsync<UsageException>(() => ServeCommand.RunAsync(options, null));
        Assert.Contains("--panel-api-port", error.Message);
    }

    [Fact]
    public async Task AnIpv6HostIsRefusedBecauseTheRelayIsIpv4Only()
    {
        var error = await Assert.ThrowsAsync<UsageException>(() => ServeCommand.RunAsync(Hosted("::"), null));
        Assert.Contains("IPv4", error.Message);
    }
}
