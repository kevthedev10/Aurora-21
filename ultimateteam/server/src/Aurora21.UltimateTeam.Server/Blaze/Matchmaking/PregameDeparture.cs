namespace Aurora21.UltimateTeam.Server.Blaze.Matchmaking;

/// <summary>
/// How a departure that ends a game before IN_GAME (the kit screen) is announced. A client told there that its opponent left
/// bailed out of its own next Rivals setup 1-5 s in (live 2026-10-05), and which notification ends that is being settled by
/// live comparison, so the mode is kept in memory: the panel's <c>pregame-departure</c> edit changes it for the next
/// departure, and a restart returns to ULTIMATETEAM21_PREGAME_DEPARTURE.
/// </summary>
public sealed class PregameDeparture
{
    /// <summary>PlayerRemoved (4/40) for the leaver, then GameRemoved (4/16), to the players still in the game (live 2026-10-05).</summary>
    public const string Announce = "announce";

    /// <summary>Nothing: the remaining client learns through its own peer-loss timeout, as before 2026-10-04.</summary>
    public const string Silent = "silent";

    /// <summary>
    /// As <see cref="Announce"/>, plus each client's own PlayerRemoved: GAME_DESTROYED to the players still in the game and
    /// the leaver's own reason to the leaver. FIFA's NotifyPlayerRemoved handler destroys its local game only for its own
    /// persona (0x144137ef0), and NotifyGameRemoved alone does not for a guest (0x14414d060).
    /// </summary>
    public const string SelfRemoved = "self";

    public static readonly IReadOnlyList<string> Modes = [Announce, Silent, SelfRemoved];

    private volatile string _mode;

    public PregameDeparture(string? initial = null) => _mode = Parse(initial) ?? Silent;

    /// <summary>The mode the next pregame departure is announced with.</summary>
    public string Mode
    {
        get => _mode;
        set => _mode = Parse(value) ?? throw new ArgumentOutOfRangeException(nameof(value), value, $"The pregame departure mode is one of {string.Join(", ", Modes)}.");
    }

    /// <summary>A known mode (any case, trimmed), or null.</summary>
    public static string? Parse(string? value) =>
        value?.Trim().ToLowerInvariant() is { } mode && Modes.Contains(mode) ? mode : null;
}
