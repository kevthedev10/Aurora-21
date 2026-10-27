using Aurora21.UltimateTeam.Server.Tools;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>
/// The hosted certificate model (native-hosting-investigation.md, certificate model a): a server's CAs are every player's
/// trust anchors, so generation never overwrites them silently, and only their public parts are exported for launchers.
/// </summary>
public sealed class TrustAnchorTests
{
    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "ultimateteam21-anchors-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GenerationKeepsAnExistingSetUnlessForced()
    {
        var root = TempRoot();
        try
        {
            Assert.Equal(0, CertificateGenerator.Run(root));
            var modulus = Path.Combine(root, "local-gos2015-modulus.bin");
            var first = File.ReadAllBytes(modulus);
            Assert.Equal(256, first.Length);

            Assert.Equal(2, CertificateGenerator.Run(root));
            Assert.Equal(first, File.ReadAllBytes(modulus));

            Assert.Equal(0, CertificateGenerator.Run(root, force: true));
            Assert.NotEqual(first, File.ReadAllBytes(modulus));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExportCopiesOnlyThePublicTrustAnchors()
    {
        var root = TempRoot();
        var output = Path.Combine(root, "exported");
        try
        {
            Assert.Equal(2, CertificateGenerator.ExportTrustAnchors(root, output));
            Assert.Equal(0, CertificateGenerator.Run(root));
            Assert.Equal(2, CertificateGenerator.ExportTrustAnchors(root, root));

            Assert.Equal(0, CertificateGenerator.ExportTrustAnchors(root, output));
            var exported = Directory.GetFiles(output).Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(new[] { "local-gos2015-ca-cert.pem", "local-gos2015-modulus.bin", "local-gs2019-ca-cert.pem", "local-gs2019-modulus.bin" }, exported);
            Assert.DoesNotContain(Directory.GetFiles(output), file => File.ReadAllText(file).Contains("PRIVATE KEY", StringComparison.Ordinal));
            Assert.Equal(File.ReadAllBytes(Path.Combine(root, "local-gs2019-modulus.bin")), File.ReadAllBytes(Path.Combine(output, "local-gs2019-modulus.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
