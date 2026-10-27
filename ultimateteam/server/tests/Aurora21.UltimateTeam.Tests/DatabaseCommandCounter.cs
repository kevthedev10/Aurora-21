namespace Aurora21.UltimateTeam.Tests;

/// <summary>
/// Test-only observation of application SQL: every statement a unit of work runs.
/// Cluster creation/drop and Npgsql transaction-control messages are intentionally excluded.
/// </summary>
public sealed class DatabaseCommandCounter
{
    private readonly object _gate = new();
    private readonly List<string> _commands = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _commands.Count;
            }
        }
    }

    public int Mark() => Count;

    public IReadOnlyList<string> Since(int mark)
    {
        lock (_gate)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(mark);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(mark, _commands.Count);
            return _commands.Skip(mark).ToArray();
        }
    }

    public void Observe(string sql)
    {
        lock (_gate)
        {
            _commands.Add(Normalize(sql));
        }
    }

    private static string Normalize(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
