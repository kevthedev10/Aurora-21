using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Models;

/// <summary>A feature result carrying an HTTP status and a JSON body.</summary>
public sealed record FutResult(int Status, JsonNode? Body)
{
    public static FutResult Ok(JsonNode? body) => new(200, body);

    public static FutResult Error(int status, string reason) => new(status, new JsonObject { ["reason"] = reason });

    /// <summary>The <c>{"code": "400", "reason": ...}</c> error shape of the store and player picks.</summary>
    public static FutResult Coded(HttpStatusCode status, string reason) =>
        new((int)status, new JsonObject { ["code"] = ((int)status).ToString(CultureInfo.InvariantCulture), ["reason"] = reason });
}
