namespace Aurora21.UltimateTeam.Core.Sessions;

/// <summary>
/// The operator's switch for starting new matches, shared by every club this process serves. The hosted server loads it
/// from <c>server_access.new_matches</c> (migration 108) and the panel's Maintenance page changes it. Closed, online
/// searches are answered but never paired and single-player matches are refused; a match already being played is never
/// stopped, and its results, rewards and match end are accepted as usual.
/// </summary>
public sealed class MatchGate
{
    private volatile bool _open = true;

    /// <summary>Whether new matches may start: online pairing (Rivals, Champions, online Draft and friendlies), Squad Battles and single-player Draft.</summary>
    public bool Open
    {
        get => _open;
        set => _open = value;
    }
}
