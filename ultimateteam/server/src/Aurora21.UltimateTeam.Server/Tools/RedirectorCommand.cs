using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Server.Hosting;
using Aurora21.UltimateTeam.Server.Http;

namespace Aurora21.UltimateTeam.Server.Tools;

/// <summary>
/// TLS impersonation of spring18.gosredirector.ea.com: answers getServerInstance with the local
/// Blaze endpoint. TLS 1.2 only, and the leaf certificate alone is sent — including the local root
/// makes FIFA 21 reject the handshake (the trust bridge supplies the anchor in-process).
/// </summary>
public static class RedirectorCommand
{
    /// <param name="blazeTlsPort">When set, the reply points FIFA at the server's loopback Blaze TLS probe on this port
    /// (<c>serve --blaze-tls-probe-port</c>) with <c>&lt;secure&gt;1&lt;/secure&gt;</c>; otherwise plaintext Blaze.</param>
    public static async Task<int> RunAsync(string? dataRoot, int? blazeTlsPort = null)
    {
        var paths = RuntimePaths.Resolve(dataRoot);
        var certificatePath = Path.Combine(paths.CertificateRoot, "certs", "spring18-cert.pem");
        var keyPath = Path.Combine(paths.CertificateRoot, "certs", "spring18-key.pem");
        if (!File.Exists(certificatePath) || !File.Exists(keyPath))
        {
            Console.WriteLine("Missing certs. Run 'Aurora21-UltimateTeam-Server generate-certs' first.");
            return 2;
        }
        var port = int.TryParse(Environment.GetEnvironmentVariable("ULTIMATETEAM_REDIRECTOR_PORT"), out var configured) ? configured : Ports.TlsRedirector;

        using var pem = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        // SChannel cannot use an ephemeral PEM key directly; round-trip through PKCS#12.
        using var certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        var serverInstance = blazeTlsPort is { } tlsPort
            ? RedirectorEndpoint.ServerInstance(tlsPort, secure: true)
            : RedirectorEndpoint.ServerInstance(Ports.Blaze);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(console => console.SingleLine = true);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Any, port, endpoint =>
            {
                endpoint.Protocols = HttpProtocols.Http1;
                endpoint.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = certificate,
                    SslProtocols = SslProtocols.Tls12,
                    ClientCertificateMode = ClientCertificateMode.NoCertificate,
                    OnAuthenticate = (connection, ssl) =>
                    {
                        Console.WriteLine($"[TLS] TCP client: {connection.RemoteEndPoint}");
                    },
                });
            });
        });

        var app = builder.Build();
        app.Run(async context =>
        {
            var body = await HttpExchange.ReadBodyAsync(context.Request);
            var tls = context.Features.Get<Microsoft.AspNetCore.Connections.Features.ITlsHandshakeFeature>();
            if (tls is not null)
            {
                Console.WriteLine($"[TLS] Negotiated: {tls.Protocol} {tls.NegotiatedCipherSuite} SNI={tls.HostName}");
            }
            Console.WriteLine($"[REDIRECTOR] {context.Request.Method} {context.Request.Path} body {HttpExchange.Preview(body, 300)}");
            await RedirectorEndpoint.WriteServerInstanceAsync(context, serverInstance);
        });

        Console.WriteLine($"FIFA 21 TLS redirector listening on 0.0.0.0:{port}");
        Console.WriteLine("SNI target: spring18.gosredirector.ea.com");
        Console.WriteLine($"Certificate bundle: {Path.GetFileName(certificatePath)}");
        Console.WriteLine(blazeTlsPort is { } advertised
            ? $"Returning local Blaze TLS probe {TlsProbeOptions.Hostname}:{advertised} (secure 1)"
            : $"Returning local Blaze endpoint 127.0.0.1:{Ports.Blaze}");
        await app.RunAsync();
        return 0;
    }
}
