using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>Marks a parameter bound to a <c>json</c> column.</summary>
internal readonly record struct JsonParameter(string Text);

/// <summary>Command construction, value conversion and document helpers shared by every <see cref="IUnitOfWork"/>.</summary>
internal static class ClubSql
{
    /// <summary>
    /// Builds a command: <c>@club</c> is bound to <paramref name="clubId"/>, a parameter is bound only when the
    /// statement names it, and <see cref="JsonParameter"/> values are sent as <c>json</c>. The observer sees the
    /// SQL before the command exists, so a test fault injected there fails before the statement runs.
    /// </summary>
    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, long clubId,
        Action<string>? observer, string sql, (string Name, object? Value)[] parameters)
    {
        observer?.Invoke(sql);
        var command = new NpgsqlCommand(sql, connection, transaction);
        if (sql.Contains("@club", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("club", clubId);
        }
        foreach (var (name, value) in parameters)
        {
            if (!sql.Contains("@" + name, StringComparison.Ordinal))
            {
                continue;
            }
            command.Parameters.Add(value switch
            {
                JsonParameter json => new NpgsqlParameter(name, NpgsqlDbType.Json) { Value = json.Text },
                null => new NpgsqlParameter(name, DBNull.Value),
                _ => new NpgsqlParameter(name, value),
            });
        }
        return command;
    }

    /// <summary>A scalar result as <typeparamref name="T"/>; SQL NULL is the type's default.</summary>
    public static T Convert<T>(object? value)
    {
        if (value is null or DBNull)
        {
            return default!;
        }
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    public static JsonParameter Json(JsonNode node) => new(FutJson.Serialize(node));

    public static JsonParameter JsonText(string text) => new(text);

    public static JsonParameter LongListJson(IEnumerable<long> values) =>
        JsonText(FutJson.Serialize(new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())));

    public static List<long> LongList(string json) => JsonValues.Longs(FutJson.Parse(json));

    /// <summary>The resource_id lookup column: the document's integer resource id, or 0.</summary>
    public static long LookupInt(JsonNode? node) => JsonValues.TryInt(node, out var value) ? value : 0;

    public static string? LookupText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => node.ToJsonString(),
    };

    public static long? NullableLong(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static string NewSid() => "LOCAL15-" + RandomHex(bytes: 10);

    public static string NewToken() => "LOCAL15TOKEN-" + RandomHex(bytes: 12);

    private static string RandomHex(int bytes) => System.Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));

    // ---- document queries -----------------------------------------------------

    /// <summary>The first-column JSON object of every row (rows that are not objects are skipped).</summary>
    public static async ValueTask<List<JsonObject>> QueryDocumentsAsync(this IUnitOfWork work, string sql, params (string Name, object? Value)[] parameters) =>
        (await work.QueryAsync(sql, reader => FutJson.ParseObject(reader.GetString(0)), parameters)).OfType<JsonObject>().ToList();

    /// <summary><c>(id, data)</c> rows whose data is a JSON object.</summary>
    public static async ValueTask<List<(long Id, JsonObject Data)>> DocumentRowsAsync(this IUnitOfWork work, string sql,
        params (string Name, object? Value)[] parameters) =>
        (await work.QueryAsync(sql, reader => (Id: reader.GetInt64(0), Data: FutJson.ParseObject(reader.GetString(1))), parameters))
            .Where(row => row.Data is not null)
            .Select(row => (row.Id, row.Data!))
            .ToList();

    /// <summary>Squad documents from <c>(data, active)</c> rows, with the stored flag written into <c>active</c>.</summary>
    public static async ValueTask<List<JsonObject>> SquadRowsAsync(this IUnitOfWork work, string sql, params (string Name, object? Value)[] parameters) =>
        (await work.QueryAsync(sql, reader => FutJson.ParseObject(reader.GetString(0))?.Set("active", reader.GetBoolean(1)), parameters))
            .OfType<JsonObject>()
            .ToList();
}
