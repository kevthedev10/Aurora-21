using System.Text.RegularExpressions;
using Npgsql;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>An immutable club view over the SAME root connection, transaction, observer and savepoints.</summary>
internal sealed class ClubScopedUnitOfWork(IUnitOfWork root, long clubId) : IUnitOfWork
{
    public long ClubId => clubId;
    public RelationalItemCompatibilityStore RelationalItems => root.RelationalItems;
    public long NowSeconds => root.NowSeconds;
    public long NowMilliseconds => root.NowMilliseconds;
    public bool InTransaction => root.InTransaction;
    // The lock belongs to the transaction, not to this scope, so the root's record is shared.
    public IDictionary<long, bool> HeldClubLocks => root.HeldClubLocks;
    public CancellationToken CancellationToken => root.CancellationToken;
    private static readonly Regex ClubParameter = new(@"@club\b", RegexOptions.CultureInvariant);
    private (string Sql, (string Name, object? Value)[] Parameters) Bind(string sql, (string Name, object? Value)[] parameters) =>
        (ClubParameter.Replace(sql, "@scopedOwner"), parameters.Append(("scopedOwner", (object?)ClubId)).ToArray());
    public ValueTask<int> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    { var bound=Bind(sql,parameters); return root.ExecuteAsync(bound.Sql,bound.Parameters); }
    public ValueTask<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
    { var bound=Bind(sql,parameters); return root.ScalarAsync<T>(bound.Sql,bound.Parameters); }
    public ValueTask<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader,T> map, params (string Name, object? Value)[] parameters)
    { var bound=Bind(sql,parameters); return root.QueryAsync(bound.Sql,map,bound.Parameters); }
    public ValueTask SavepointAsync(Func<ValueTask> step) => root.SavepointAsync(step);
    internal static IUnitOfWork For(IUnitOfWork root,long owner) => owner==root.ClubId ? root : new ClubScopedUnitOfWork(root,owner);
}
