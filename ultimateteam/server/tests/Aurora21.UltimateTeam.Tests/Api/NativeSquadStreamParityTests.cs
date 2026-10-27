using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The hub route writes its squad straight into the response and never builds it as a document. That
/// is only safe while the bytes are the same ones the document form produces, so this compares the
/// two on the same club: the streamed route body against the rendered <c>HubAsync</c> document.
/// Both squad templates are covered — with saved tactics and without — because the streamed form
/// picks between them where the document form used to append the key after rendering.
/// </summary>
public sealed class NativeSquadStreamParityTests
{
    private const string G = "/ut/game/fifa21";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheStreamedHubMatchesTheRenderedDocument(bool tactics)
    {
        using var rt = TestRuntime.Create(marketTarget: 0,
            time: new TestRuntime.FixedTime(1_700_000_000));
        if (tactics)
        {
            // The verbatim client saves, so the tactics this renders are the shape FIFA really sends
            // rather than something invented to make the key appear.
            var fixture = JsonNode.Parse(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "native-custom-tactics.json")))!.AsObject();
            foreach (var save in fixture.Arr("saves")!.OfType<JsonObject>())
            {
                rt.Call("POST", save.Str("path"), save.Obj("body")!);
            }
        }

        await rt.Game.ReadAsync(async work =>
        {
            var document = await rt.Fut.Club.HubAsync(work);
            // Exercising the key the two templates differ by, so a passing run proves the intended
            // template was selected rather than both paths quietly dropping it.
            Assert.Equal(tactics, document["squad"]!.AsObject().ContainsKey("tactics"));
            return true;
        }, CancellationToken.None);

        var response = await rt.Router.HandleAsync(new FutRequest("GET", G + "/hub",
            Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()));
        Assert.Equal(200, response.Status);

        var rendered = await rt.Game.ReadAsync(
            async work => FutJson.Serialize(await rt.Fut.Club.HubAsync(work)), CancellationToken.None);
        Assert.Equal(rendered, System.Text.Encoding.UTF8.GetString(response.Body));
    }
}
