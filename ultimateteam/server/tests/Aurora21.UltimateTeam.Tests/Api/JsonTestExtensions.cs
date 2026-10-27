using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Tests.Api;

internal static class JsonTestExtensions
{
    public static long L(this JsonNode? node) => JsonValues.IsStrictInt(node, out var value) ? value : throw new InvalidOperationException($"not an integer: {node?.ToJsonString()}");

    public static double D(this JsonNode? node) => node!.GetValue<double>();

    public static string S(this JsonNode? node) => node!.GetValue<string>();

    public static bool B(this JsonNode? node) => node!.GetValue<bool>();

    public static JsonObject O(this JsonNode? node) => node!.AsObject();

    public static JsonArray A(this JsonNode? node) => node!.AsArray();

    public static IEnumerable<JsonObject> Objects(this JsonNode? node) => node!.AsArray().Select(item => item!.AsObject());

    public static bool IsObject(this JsonNode? node) => node is JsonObject;

    public static bool JsonEquals(this JsonNode? left, JsonNode? right) => JsonNode.DeepEquals(left, right);
}
