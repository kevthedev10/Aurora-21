using System.IO.Pipelines;
using Aurora21.UltimateTeam.Core.Sessions;

namespace Aurora21.UltimateTeam.Server.Blaze.Matchmaking;

/// <summary>
/// One authenticated FIFA Blaze connection. Its writes are serialized so other connections can
/// push matchmaking notifications to it while it is answering its own requests.
/// </summary>
public sealed class BlazeClient
{
    private readonly PipeWriter _output;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private long _machineId;

    public BlazeClient(PlayerSession session, BlazeResponses responses, PipeWriter output, string peerAddress, string serverAddress)
    {
        Session = session;
        Responses = responses;
        _output = output;
        PeerAddress = peerAddress;
        ServerAddress = serverAddress;
    }

    public PlayerSession Session { get; }
    public BlazeResponses Responses { get; }
    public string PlayerId => Session.Player.Id;
    public long PersonaId => Session.Player.PersonaId;
    public string DisplayName => Session.Game.Config.DisplayName;
    public string PeerAddress { get; }

    /// <summary>The server address this client's connection reached (advertised for its relay port).</summary>
    public string ServerAddress { get; }

    /// <summary>
    /// The machine id FIFA 21 reports in UpdateNetworkInfo and StartMatchmaking (MACI), carried in
    /// every NetworkAddress of the peer mesh. It is distinct from the persona id.
    /// </summary>
    public long MachineId
    {
        get => Interlocked.Read(ref _machineId);
        set => Interlocked.Exchange(ref _machineId, value);
    }

    /// <summary>
    /// The message id of this connection's latest Messaging.SendMessage: its reply and the NotifyMessage the relay then fans
    /// out carry the same id. A connection's frames are handled one at a time, so the reply and the relay see the same value.
    /// </summary>
    internal long PendingMessageId { get; set; }

    /// <summary>The machine id to publish: the reported one, or the persona id before FIFA reports one.</summary>
    public long AdvertisedMachineId => MachineId != 0 ? MachineId : PersonaId;

    public async Task SendAsync(byte[] frame, CancellationToken cancellation = default)
    {
        await _sendGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(frame, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Sends to this client, reporting false instead of throwing when its connection is gone.</summary>
    public async Task<bool> TrySendAsync(byte[] frame)
    {
        try
        {
            await SendAsync(frame).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException
                                          or Microsoft.AspNetCore.Connections.ConnectionResetException)
        {
            return false;
        }
    }
}
