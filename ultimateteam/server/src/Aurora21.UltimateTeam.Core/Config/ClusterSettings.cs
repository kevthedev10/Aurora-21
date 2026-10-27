using System.Globalization;
using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>
/// Cluster-wide PostgreSQL settings for the managed cluster, from the <c>database</c> section of
/// config.json (defaults in config.example.json).
///
/// Unlike the session timeouts in <see cref="DatabaseSettings"/> these are server-wide and only take
/// effect when this process starts the cluster, because PostgreSQL reads them at start-up. An already
/// running cluster keeps the values it started with.
///
/// The shipped defaults suit one local player. A measurement or deployment profile raises them
/// deliberately; see the server README (<c>--cluster-config</c> and the hosted profile).
/// </summary>
public sealed record ClusterSettings(
    int MaxConnections,
    int SharedBuffersMb,
    int MaxLocksPerTransaction,
    int WorkMemMb,
    int EffectiveCacheSizeMb,
    int MaxWalSizeMb)
{
    /// <summary>The file this is written to inside the data directory, included from postgresql.conf.</summary>
    public const string FileName = "aurora21-tuning.conf";

    public static ClusterSettings From(JsonObject? values, JsonObject defaults)
    {
        long Read(string key) => LimitSettings.Read(values, defaults, "database", key);
        return new ClusterSettings(
            Bound(Read("cluster_max_connections"), "cluster_max_connections", 10, 5_000),
            Bound(Read("cluster_shared_buffers_mb"), "cluster_shared_buffers_mb", 16, 65_536),
            // The lock table is sized from this at start-up. 64, the PostgreSQL default, was exhausted
            // once the schema passed 65 relations, because opening a club locks the catalog and club
            // tables in one transaction; PostgreSQL reports that as "out of shared memory".
            Bound(Read("cluster_max_locks_per_transaction"), "cluster_max_locks_per_transaction", 64, 8_192),
            Bound(Read("cluster_work_mem_mb"), "cluster_work_mem_mb", 1, 1_024),
            Bound(Read("cluster_effective_cache_size_mb"), "cluster_effective_cache_size_mb", 32, 262_144),
            Bound(Read("cluster_max_wal_size_mb"), "cluster_max_wal_size_mb", 32, 65_536));
    }

    /// <summary>The postgresql.conf fragment written before the cluster is started.</summary>
    public string ToConfigFile() => string.Create(CultureInfo.InvariantCulture, $"""
        # Written by Aurora 21 from config.json database.cluster_* every time this process starts the
        # managed cluster. Edit config.json rather than this file, which is regenerated.
        max_connections = {MaxConnections}
        shared_buffers = {SharedBuffersMb}MB
        max_locks_per_transaction = {MaxLocksPerTransaction}
        work_mem = {WorkMemMb}MB
        effective_cache_size = {EffectiveCacheSizeMb}MB
        max_wal_size = {MaxWalSizeMb}MB

        """);

    private static int Bound(long value, string key, int minimum, int maximum) =>
        value >= minimum && value <= maximum
            ? (int)value
            : throw new RuntimeSetupException($"config.json database.{key} must be between {minimum} and {maximum}.");
}
