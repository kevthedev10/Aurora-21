namespace Aurora21.UltimateTeam.Server.Hosting;

[Flags]
public enum TlsProbeMode
{
    Off = 0,
    Fut = 1,
    Nucleus = 2,
    Both = Fut | Nucleus,
    Easw = 4,
    Ssf = 8,

    /// <summary>Every advertised HTTP service over TLS: FUT/POW, Nucleus, EASW and SSF.</summary>
    All = Fut | Nucleus | Easw | Ssf,
}

/// <summary>
/// Loopback-only HTTPS experiment using the already-installed redirector certificate. FUT and Nucleus share
/// <see cref="Port"/>; EASW and SSF (in <see cref="TlsProbeMode.All"/>) get their own TLS listeners on the next two ports.
/// </summary>
public sealed record TlsProbeOptions(TlsProbeMode Mode, int Port)
{
    public const int DefaultPort = 18443;
    public const string Hostname = "spring18.gosredirector.ea.com";

    public static TlsProbeOptions Off { get; } = new(TlsProbeMode.Off, DefaultPort);

    public bool Fut => (Mode & TlsProbeMode.Fut) != 0;
    public bool Nucleus => (Mode & TlsProbeMode.Nucleus) != 0;
    public bool Easw => (Mode & TlsProbeMode.Easw) != 0;
    public bool Ssf => (Mode & TlsProbeMode.Ssf) != 0;
    public string Origin => $"https://{Hostname}:{Port}";
    public string BaseUrl => Origin + "/";

    /// <summary>The EASW TLS listener's port (<see cref="Port"/> + 2; <see cref="Port"/> + 1 is the Blaze TLS probe's default).</summary>
    public int EaswPort => Port + 2;

    /// <summary>The SSF TLS listener's port (<see cref="Port"/> + 3).</summary>
    public int SsfPort => Port + 3;

    public string EaswOrigin => $"https://{Hostname}:{EaswPort}";
    public string SsfOrigin => $"https://{Hostname}:{SsfPort}";

    /// <summary>
    /// Whether the other addresses in Blaze replies (the QoS ping site, telemetry, ticker and the dynamic-message host)
    /// name <see cref="Hostname"/> instead of 127.0.0.1, so the hosts entry decides where they point (a hosted server's
    /// address, or loopback in a local rehearsal).
    /// </summary>
    public bool AdvertiseHostname { get; init; }

    /// <summary>The host the address fields in Blaze replies name.</summary>
    public string AdvertisedHost => AdvertiseHostname ? Hostname : "127.0.0.1";
}
