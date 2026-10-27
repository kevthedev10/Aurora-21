using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class JsonTemplateTests
{
    [Fact]
    public void SubstitutesWholeValuePlaceholdersAndKeepsKeyOrder()
    {
        var template = JsonTemplate.Parse("test", """
            {"id":"{{id}}","name":"{{name}}","alias":"{{name}}","chance":0.10,"fixed":1.0,"count":3,"nested":{"items":"{{items}}","none":"{{none}}"},"text":"a {{name}} b"}
            """);
        var rendered = template.RenderObject(new TemplateArgs
        {
            ["id"] = 42L,
            ["name"] = "Gold",
            ["items"] = new JsonArray(1, 2),
            ["none"] = null,
        });
        Assert.Equal("""{"id":42,"name":"Gold","alias":"Gold","chance":0.1,"fixed":1.0,"count":3,"nested":{"items":[1,2],"none":null},"text":"a {{name}} b"}""",
            FutJson.Serialize(rendered));
    }

    [Fact]
    public void RejectsMissingAndUnusedArguments()
    {
        var template = JsonTemplate.Parse("test", """{"id":"{{id}}"}""");
        Assert.Throws<InvalidOperationException>(() => template.Render());
        Assert.Throws<InvalidOperationException>(() => template.Render(new TemplateArgs { ["id"] = 1L, ["extra"] = 2L }));
    }

    [Fact]
    public void ADeferredWriterCountsAsProvidedButCannotBeRendered()
    {
        var template = JsonTemplate.Parse("test", """{"id":"{{id}}"}""");
        var args = new TemplateArgs();
        args.Defer("id", writer => writer.WriteNumberValue(42));
        Assert.True(args.Contains("id"));
        Assert.True(args.HasDeferred);
        template.Validate(args);
        // There is no node to hand back, so this fails loudly instead of materialising the value
        // the caller deferred precisely to avoid building.
        Assert.Throws<InvalidOperationException>(() => template.Render(args));

        var unused = new TemplateArgs { ["id"] = 1L };
        unused.Defer("extra", writer => writer.WriteNullValue());
        Assert.Throws<InvalidOperationException>(() => template.Validate(unused));
    }

    [Fact]
    public void ANameHoldsEitherANodeOrADeferredWriter_NotBoth()
    {
        var template = JsonTemplate.Parse("test", """{"id":"{{id}}"}""");
        var args = new TemplateArgs();
        args.Defer("id", writer => writer.WriteNumberValue(42));
        args["id"] = 7L;
        Assert.False(args.HasDeferred);
        Assert.Equal(["id"], args.Names);
        Assert.Equal("""{"id":7}""", FutJson.Serialize(template.Render(args)));

        args.Defer("id", writer => writer.WriteNumberValue(42));
        Assert.Equal(["id"], args.Names);
        Assert.Throws<InvalidOperationException>(() => template.Render(args));
    }

    [Fact]
    public void RenderedDocumentsAreIndependentCopies()
    {
        var template = JsonTemplate.Parse("test", """{"list":[1],"value":"{{value}}"}""");
        var shared = new JsonObject { ["x"] = 1 };
        var first = template.RenderObject(new TemplateArgs { ["value"] = shared });
        ((JsonArray)first["list"]!).Add(2);
        ((JsonObject)first["value"]!)["x"] = 9;
        var second = template.RenderObject(new TemplateArgs { ["value"] = shared });
        Assert.Equal("""{"list":[1],"value":{"x":1}}""", FutJson.Serialize(second));
    }
}
