using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Rules;
using Npgsql;

namespace Aurora21.UltimateTeam.Tests;

/// <summary>Builds the supported upgrade baseline forward in a disposable database.</summary>
internal static class Migration140Database
{
    internal const int Version = 140;

    internal static async Task InstallAsync(NpgsqlDataSource source)
    {
        var package = RelationalSetupPackage.Load();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        async Task Execute(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }
        await Execute("CREATE SCHEMA ultimateteam;" + package.LedgerSql);
        foreach (var file in package.Files) await Execute(file.Sql);
        await Execute("""
            INSERT INTO ultimateteam.setup_artifact_versions(version_code, package_sha256) VALUES(@version,@hash)
            """, ("version", package.VersionCode), ("hash", package.PackageSha256));
        var assembly = typeof(RelationalSchemaBootstrap).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("DatabaseSetup/migrations/", StringComparison.Ordinal))
            .Select(name => (Name: name, Version: int.Parse(name.Split('/')[2][..3])))
            .Where(file => file.Version <= Version).OrderBy(file => file.Version).ToArray();
        if (!migrations.Select(file => file.Version).SequenceEqual(Enumerable.Range(1, Version)))
            throw new InvalidOperationException("The migration-140 fixture requires an unbroken migration history.");
        foreach (var file in migrations)
        {
            using var stream = assembly.GetManifestResourceStream(file.Name)!;
            using var reader = new StreamReader(stream);
            await Execute(await reader.ReadToEndAsync());
        }
        // Only these rule documents changed after develop's 140. Freeze their historical values,
        // including retired keys; the current runtime fills other rule documents during upgrade.
        foreach (var file in new[] { "seasons", "objectives", "sbc" })
        {
            var document = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "Database140", file + ".json")))!;
            var rows = RuleRows.Flatten(file, document);
            await Execute("DELETE FROM ultimateteam.content_rules WHERE file=@file", ("file", file));
            await Execute("""
                INSERT INTO ultimateteam.content_rules(file,path,value,ordinal)
                SELECT @file,path::jsonb,value::jsonb,ordinal FROM unnest(@paths::text[],@values::text[],@ordinals::int[])
                    AS row(path,value,ordinal)
                """, ("file", file), ("paths", rows.Select(row => row.PathText).ToArray()),
                ("values", rows.Select(row => row.Value?.ToJsonString() ?? "null").ToArray()),
                ("ordinals", rows.Select(row => row.Ordinal).ToArray()));
        }
        await transaction.CommitAsync();
    }
}
