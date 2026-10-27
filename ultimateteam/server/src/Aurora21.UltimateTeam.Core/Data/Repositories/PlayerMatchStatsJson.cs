using System.Text.Json;
using Aurora21.UltimateTeam.Core.Data.Records;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

internal static class PlayerMatchStatsJson
{
    internal static JsonElement Encode(IReadOnlyList<PlayerStatValue>? rows)
    {
        var arrays = new Dictionary<string, long[]>
        {
            ["current"] = new long[5],
            ["lifetime"] = new long[5]
        };
        var seen = new HashSet<(string, int)>();
        foreach (var row in rows ?? Array.Empty<PlayerStatValue>())
        {
            if (!arrays.TryGetValue(row.Scope, out var values) || row.StatIndex < 0 || row.StatIndex >= 5
                || row.Value < 0 || !seen.Add((row.Scope, row.StatIndex)))
                throw new ArgumentException("Invalid or duplicate player match counter slot.");
            values[row.StatIndex] = row.Value;
        }
        return JsonSerializer.SerializeToElement(arrays);
    }

    internal static IReadOnlyList<PlayerStatValue> Decode(string json, ItemId item = default)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2)
            throw new InvalidOperationException("Invalid player match counter object.");
        var rows = new List<PlayerStatValue>();
        foreach (var scope in new[] { "current", "lifetime" })
        {
            if (!root.TryGetProperty(scope, out var values) || values.ValueKind != JsonValueKind.Array
                || values.GetArrayLength() != 5)
                throw new InvalidOperationException("Invalid player match counter array.");
            var index = 0;
            foreach (var value in values.EnumerateArray())
            {
                if (!value.TryGetInt64(out var number) || number < 0)
                    throw new InvalidOperationException("Invalid player match counter value.");
                if (number != 0) rows.Add(new PlayerStatValue(0, item, scope, index, number));
                index++;
            }
        }
        return rows;
    }
}
