namespace Aurora21.UltimateTeam.Server.Blaze.Matchmaking;

/// <summary>
/// The game speed online matches are played at: GameSetup GAME.ATTR <c>fifaGameSpeed</c>. Each client reads it from its own
/// copy of the game attributes into the match settings (FIFA21.exe 0x140c2d95b, 0x144322682), 0 Slow, 1 Normal, 2 Fast, and
/// plays Normal when the key is missing. Fast raises top sprint speed and quickens acceleration only; the match clock does
/// not read it. Null sends no key, as before. Kept in memory: the panel's <c>online.gameSpeed</c> changes it for the next
/// match, and a restart returns to blaze.json <c>online.gameSpeed</c>.
/// </summary>
public sealed class OnlineGameSpeed
{
    public const long Slow = 0;
    public const long Normal = 1;
    public const long Fast = 2;
    private const long None = -1;

    private long _value = None;

    public OnlineGameSpeed(long? initial = null) => Value = initial;

    /// <summary>The speed the next match gets; null sends no fifaGameSpeed.</summary>
    public long? Value
    {
        get
        {
            var value = Interlocked.Read(ref _value);
            return value == None ? null : value;
        }
        set
        {
            if (value is < Slow or > Fast) throw new ArgumentOutOfRangeException(nameof(value), value, "The game speed is 0 (Slow), 1 (Normal) or 2 (Fast).");
            Interlocked.Exchange(ref _value, value ?? None);
        }
    }
}
