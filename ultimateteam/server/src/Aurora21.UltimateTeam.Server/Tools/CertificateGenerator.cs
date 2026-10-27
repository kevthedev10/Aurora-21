using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aurora21.UltimateTeam.Server.Tools;

/// <summary>
/// Creates the two local CAs (GOS 2015 and Gameplay Services 2019), their redirector leaf
/// certificates and the 256-byte CA modulus files the in-memory trust bridge writes into FIFA 21.
/// An existing set is kept unless <c>--force</c> is given: on a hosted server these CAs are every player's trust
/// anchors, and regenerating them silently would lock every installed launcher out.
/// </summary>
public static class CertificateGenerator
{
    private const int KeyBits = 2048;
    private const int ModulusBytes = 256;
    private const int ValidityDays = 3650;
    private const string CountryOid = "2.5.4.6";
    private const string StateOid = "2.5.4.8";
    private const string LocalityOid = "2.5.4.7";
    private const string OrganizationOid = "2.5.4.10";
    private const string OrganizationalUnitOid = "2.5.4.11";
    private const string EmailOid = "1.2.840.113549.1.9.1";
    private const string CommonNameOid = "2.5.4.3";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    private static readonly string[] RedirectorNames = { "winter15.gosredirector.ea.com", "spring18.gosredirector.ea.com" };

    /// <summary>One local CA and its redirector leaf, with the file names the launcher and trust bridge expect.</summary>
    private sealed record Authority(
        string Label, string CommonName, string OrganizationalUnit, string LeafCommonName,
        string CaFilePrefix, string LeafFilePrefix, string ModulusFile);

    private static readonly Authority[] Authorities =
    {
        new("GOS2015", "GOS 2015 Certificate Authority", "Global Online Studio", "winter15.gosredirector.ea.com",
            "local-gos2015-ca", "spring18", "local-gos2015-modulus.bin"),
        new("GS2019", "Gameplay Services 2019 Certificate Authority", "Gameplay Services", "spring18.gosredirector.ea.com",
            "local-gs2019-ca", "spring18-gs2019", "local-gs2019-modulus.bin"),
    };

    /// <summary>The files one generation writes, relative to the certificate root.</summary>
    private static IEnumerable<string> GeneratedFiles() => Authorities.SelectMany(authority => new[]
    {
        Path.Combine("certs", $"{authority.CaFilePrefix}-key.pem"),
        Path.Combine("certs", $"{authority.CaFilePrefix}-cert.pem"),
        Path.Combine("certs", $"{authority.LeafFilePrefix}-key.pem"),
        Path.Combine("certs", $"{authority.LeafFilePrefix}-cert.pem"),
        Path.Combine("certs", $"{authority.LeafFilePrefix}-chain.pem"),
        authority.ModulusFile,
    });

    public static int Run(string certificateRoot, bool force = false)
    {
        var existing = GeneratedFiles().Where(file => File.Exists(Path.Combine(certificateRoot, file))).ToList();
        if (existing.Count > 0 && !force)
        {
            Console.WriteLine($"Certificates already exist under {certificateRoot} ({string.Join(", ", existing)}). Nothing was changed.");
            Console.WriteLine("Regenerating replaces the CAs every installed launcher trusts; pass --force only if that is intended.");
            return 2;
        }
        var certs = Path.Combine(certificateRoot, "certs");
        Directory.CreateDirectory(certs);
        var now = DateTimeOffset.UtcNow;
        var subjects = new List<(Authority Authority, string Ca, string Leaf)>();

        foreach (var authority in Authorities)
        {
            using var caKey = RSA.Create(KeyBits);
            using var ca = CreateAuthority(caKey, authority, now);
            using var leafKey = RSA.Create(KeyBits);
            using var leaf = CreateLeaf(leafKey, ca, caKey, authority.LeafCommonName, now);
            File.WriteAllText(Path.Combine(certs, $"{authority.CaFilePrefix}-key.pem"), KeyPem(caKey));
            File.WriteAllText(Path.Combine(certs, $"{authority.CaFilePrefix}-cert.pem"), CertificatePem(ca));
            File.WriteAllText(Path.Combine(certs, $"{authority.LeafFilePrefix}-key.pem"), KeyPem(leafKey));
            File.WriteAllText(Path.Combine(certs, $"{authority.LeafFilePrefix}-cert.pem"), CertificatePem(leaf));
            File.WriteAllText(Path.Combine(certs, $"{authority.LeafFilePrefix}-chain.pem"), CertificatePem(leaf) + CertificatePem(ca));
            WriteModulus(Path.Combine(certificateRoot, authority.ModulusFile), caKey);
            subjects.Add((authority, ca.Subject, leaf.Subject));
        }

        Console.WriteLine("Created FUT 21 local CAs and redirector certificates.");
        for (var index = 0; index < subjects.Count; index++)
        {
            Console.WriteLine($"CA #{index + 1} subject: {subjects[index].Ca}");
        }
        foreach (var (authority, _, leaf) in subjects)
        {
            Console.WriteLine($"Leaf ({authority.Label}) subject: {leaf}");
        }
        Console.WriteLine($"Leaf SAN: {string.Join(", ", RedirectorNames)}");
        Console.WriteLine($"Moduli: {string.Join(", ", Authorities.Select(authority => authority.ModulusFile))}");
        return 0;
    }

    /// <summary>
    /// The public trust-anchor files of a certificate root, by distributed name: each CA's modulus file and CA
    /// certificate. The launcher API serves exactly these; nothing else under the root (never a private key).
    /// </summary>
    public static IReadOnlyList<(string Source, string Name)> TrustAnchorFiles(string certificateRoot) =>
        Authorities.SelectMany(authority => new[]
        {
            (Source: Path.Combine(certificateRoot, authority.ModulusFile), Name: authority.ModulusFile),
            (Source: Path.Combine(certificateRoot, "certs", $"{authority.CaFilePrefix}-cert.pem"), Name: $"{authority.CaFilePrefix}-cert.pem"),
        }).ToArray();

    /// <summary>
    /// Copies a server's public trust anchors to <paramref name="outputDirectory"/> for distribution with the launcher:
    /// each CA's modulus file (what <c>trust-patch --trust-anchors</c> installs) and CA certificate. Never a private key.
    /// </summary>
    public static int ExportTrustAnchors(string certificateRoot, string outputDirectory)
    {
        var files = TrustAnchorFiles(certificateRoot).ToList();
        if (files.FirstOrDefault(file => !File.Exists(file.Source)) is { Source: not null } missing)
        {
            Console.WriteLine($"Missing {missing.Source}. Run generate-certs on this server first.");
            return 2;
        }
        if (Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(certificateRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("The output folder must differ from the certificate root.");
            return 2;
        }
        Directory.CreateDirectory(outputDirectory);
        foreach (var (source, name) in files)
        {
            File.Copy(source, Path.Combine(outputDirectory, name), overwrite: true);
        }
        Console.WriteLine($"Exported {files.Count} public trust-anchor files to {outputDirectory}: {string.Join(", ", files.Select(file => file.Name))}");
        return 0;
    }

    private static X509Certificate2 CreateAuthority(RSA key, Authority authority, DateTimeOffset now)
    {
        var name = BuildName(new[]
        {
            (CountryOid, "US"), (StateOid, "California"), (LocalityOid, "Redwood City"),
            (OrganizationOid, "Electronic Arts, Inc."), (OrganizationalUnitOid, authority.OrganizationalUnit),
            (EmailOid, "GOSDirtysockSupport@ea.com"), (CommonNameOid, authority.CommonName),
        });
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.Create(name, X509SignatureGenerator.CreateForRSA(key, RSASignaturePadding.Pkcs1),
            now.AddDays(-1), now.AddDays(ValidityDays), RandomSerial());
    }

    private static X509Certificate2 CreateLeaf(RSA key, X509Certificate2 issuer, RSA issuerKey, string commonName, DateTimeOffset now)
    {
        var name = BuildName(new[]
        {
            (CountryOid, "US"), (StateOid, "California"), (OrganizationOid, "Electronic Arts, Inc."),
            (OrganizationalUnitOid, "Global Online Studio"), (CommonNameOid, commonName),
        });
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in RedirectorNames)
        {
            san.AddDnsName(dns);
        }
        request.CertificateExtensions.Add(san.Build(false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(ServerAuthenticationOid) }, false));
        return request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1),
            now.AddDays(-1), now.AddDays(ValidityDays), RandomSerial());
    }

    /// <summary>Encodes RDNs in the given order: country as PrintableString, email as IA5String, the rest as UTF8String.</summary>
    private static X500DistinguishedName BuildName(IEnumerable<(string Oid, string Value)> attributes)
    {
        var builder = new X500DistinguishedNameBuilder();
        // Build() emits the most recently added RDN first, so add them in reverse.
        foreach (var (oid, value) in attributes.Reverse())
        {
            switch (oid)
            {
                case CountryOid:
                    builder.AddCountryOrRegion(value);
                    break;
                case EmailOid:
                    builder.AddEmailAddress(value);
                    break;
                default:
                    builder.Add(oid, value, UniversalTagNumber.UTF8String);
                    break;
            }
        }
        return builder.Build();
    }

    /// <summary>A random positive 159-bit serial number (20 bytes, top bit clear).</summary>
    private static byte[] RandomSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(20);
        serial[0] &= 0x7F;
        serial[0] |= 0x40;
        return serial;
    }

    private static string KeyPem(RSA key) => PemEncoding.WriteString("RSA PRIVATE KEY", key.ExportRSAPrivateKey()) + "\n";

    private static string CertificatePem(X509Certificate2 certificate) =>
        PemEncoding.WriteString("CERTIFICATE", certificate.Export(X509ContentType.Cert)) + "\n";

    /// <summary>The CA modulus as exactly <see cref="ModulusBytes"/> big-endian bytes.</summary>
    private static void WriteModulus(string path, RSA key)
    {
        var modulus = key.ExportParameters(false).Modulus!;
        var output = new byte[ModulusBytes];
        modulus.AsSpan(Math.Max(0, modulus.Length - ModulusBytes)).CopyTo(output.AsSpan(Math.Max(0, ModulusBytes - modulus.Length)));
        File.WriteAllBytes(path, output);
    }
}
