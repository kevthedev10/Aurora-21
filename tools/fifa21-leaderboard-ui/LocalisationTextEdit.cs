using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class LocalisationTextEdit
{
    internal static string Apply(string original, JsonObject edit, string context)
    {
        var expected = edit["source"]!.GetValue<string>();
        var target = edit["target"]!.GetValue<string>();
        if (original != expected) throw new InvalidDataException($"Unexpected currency source text: {context}");
        if (!Tokens(original, @"%[0-9]*[A-Za-z]").SequenceEqual(Tokens(target, @"%[0-9]*[A-Za-z]")))
            throw new InvalidDataException($"Currency placeholders changed: {context}");
        if (!Tokens(original, @"\r\n|\r|\n").SequenceEqual(Tokens(target, @"\r\n|\r|\n")))
            throw new InvalidDataException($"Currency line breaks changed: {context}");
        if (original == target || !target.Replace("\r", "").Replace("\n", " ").Contains("Aurora Tokens", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid currency replacement: {context}");
        return target;
    }

    private static IEnumerable<string> Tokens(string value, string pattern) => Regex.Matches(value, pattern).Select(m => m.Value);

    internal static void SelfTest()
    {
        static JsonObject Edit(string source, string target) => new() { ["source"] = source, ["target"] = target };
        var source = "%1d FIFA Points\r\n%2s";
        var target = "%1d Aurora Tokens\r\n%2s";
        if (Apply(source, Edit(source, target), "fixture") != target) throw new Exception("Valid edit failed");
        if (Apply("FIFA\nPoints", Edit("FIFA\nPoints", "Aurora\nTokens"), "header") != "Aurora\nTokens") throw new Exception("Wrapped header failed");
        foreach (var edit in new[] { Edit("unexpected", target), Edit(source, "%2d Aurora Tokens\r\n%2s"), Edit(source, "%1d Aurora Tokens\n%2s"), Edit(source, source), Edit(source, "%1d Champions Points\r\n%2s") })
        {
            var refused = false;
            try { Apply(source, edit, "invalid fixture"); } catch (InvalidDataException) { refused = true; }
            if (!refused) throw new Exception("Invalid currency edit accepted");
        }
        Console.WriteLine("PASS: exact currency source, placeholders and line breaks validated; unrelated currency rejected.");
    }
}
