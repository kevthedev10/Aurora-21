using Aurora21.UltimateTeam.Core.Sessions;

namespace Aurora21.UltimateTeam.Server.Http;

/// <summary>The plain HTTP services the client uses besides Blaze: FUT, QoS, SSF, EASW and the debug redirector.</summary>
public sealed class HttpEndpoints
{
    public HttpEndpoints(PlayerRegistry players)
    {
        var game = players.Local.Game;
        var log = game.Logger("UltimateTeam.HttpServices");
        Fut = new FutEndpoint(players);
        Qos = new QosEndpoint(log);
        Ssf = new SsfEndpoint(game, log);
        Easw = new EaswEndpoint(game, log);
        Redirector = new RedirectorEndpoint(log);
    }

    public FutEndpoint Fut { get; }
    public QosEndpoint Qos { get; }
    public SsfEndpoint Ssf { get; }
    public EaswEndpoint Easw { get; }
    public RedirectorEndpoint Redirector { get; }
}
