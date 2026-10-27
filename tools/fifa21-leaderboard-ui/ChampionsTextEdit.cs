using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class ChampionsTextEdit
{
    internal static string Apply(string original, JsonObject edit, string context)
    {
        var source = edit["source"]!.GetValue<string>();
        var target = edit["target"]!.GetValue<string>();
        if (original != source) throw new InvalidDataException("Unexpected Champions source text: " + context);
        if (source == target ||
            Regex.Replace(target, @"(?<![A-Za-z])Aurora(?![A-Za-z])", "FUT", RegexOptions.IgnoreCase) != source)
            throw new InvalidDataException("Invalid Champions replacement: " + context);
        if (!Tokens(source, @"%[0-9]*[A-Za-z]").SequenceEqual(Tokens(target, @"%[0-9]*[A-Za-z]")) ||
            !Tokens(source, @"\r\n|\r|\n").SequenceEqual(Tokens(target, @"\r\n|\r|\n")))
            throw new InvalidDataException("Champions formatting changed: " + context);
        return target;
    }

    private static IEnumerable<string> Tokens(string value, string pattern) =>
        Regex.Matches(value, pattern).Select(m => m.Value);

    internal static void SelfTest()
    {
        var source = "Enter FUT Champions\r\n%1d matches";
        var good = new JsonObject { ["source"] = source, ["target"] = "Enter Aurora Champions\r\n%1d matches" };
        if (Apply(source, good, "fixture") != good["target"]!.GetValue<string>()) throw new Exception("Valid Champions edit failed");
        var native = new JsonObject { ["source"] = "FUT 챔피언스", ["target"] = "Aurora 챔피언스" };
        if (Apply("FUT 챔피언스", native, "Korean fixture") != "Aurora 챔피언스") throw new Exception("Localised Champions edit failed");
        foreach (var bad in new[] {
            new JsonObject { ["source"] = "other", ["target"] = good["target"]!.GetValue<string>() },
            new JsonObject { ["source"] = source, ["target"] = "Enter Aurora Champions\n%1d matches" },
            new JsonObject { ["source"] = source, ["target"] = "Enter Aurora Champions\r\n%2d matches" },
            new JsonObject { ["source"] = source, ["target"] = source },
            new JsonObject { ["source"] = source, ["target"] = "Enter Aurora Championship\r\n%1d matches" },
        })
        {
            try { Apply(source, bad, "invalid fixture"); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Invalid Champions edit accepted");
        }
        Console.WriteLine("PASS: Champions source text, placeholders and line breaks validated.");
    }
}
