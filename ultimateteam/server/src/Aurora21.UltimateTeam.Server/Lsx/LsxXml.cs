using System.Xml.Linq;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Server.Lsx;

/// <summary>LSX message text: requests are parsed with XLinq, replies are written exactly as the SDK expects.</summary>
public static class LsxXml
{
    public sealed record Request(string Id, string Name, XElement? Operation)
    {
        public string Attribute(string name) => (string?)Operation?.Attribute(name) ?? string.Empty;
    }

    /// <summary>The first operation inside the message's Request element; malformed XML reads as an Unknown request.</summary>
    public static Request ParseRequest(string xml)
    {
        try
        {
            var request = XElement.Parse(xml).Descendants().FirstOrDefault(element => element.Name.LocalName == "Request");
            if (request is not null)
            {
                var operation = request.Elements().FirstOrDefault();
                return new((string?)request.Attribute("id") ?? "0", operation?.Name.LocalName ?? "Unknown", operation);
            }
        }
        catch (System.Xml.XmlException)
        {
        }
        return new("0", "Unknown", null);
    }

    public static string Response(string requestId, string inner, string sender = "EbisuSDK") =>
        $"<LSX><Response id=\"{Attr(requestId)}\" sender=\"{Attr(sender)}\">{inner}</Response></LSX>";

    public static string Event(string sender, string inner) => $"<LSX><Event sender=\"{Attr(sender)}\">{inner}</Event></LSX>";

    /// <summary>A self-closing element; attribute values are escaped.</summary>
    public static string Element(string name, IEnumerable<(string Name, string Value)> attributes) =>
        $"<{name}{string.Concat(attributes.Select(attribute => $" {attribute.Name}=\"{Attr(attribute.Value)}\""))} />";

    /// <summary>A configured element with its placeholders filled from <paramref name="values"/>.</summary>
    public static string Element(LsxElement element, (string Name, object Value)[] values) =>
        Element(element.Element, element.Attributes.Select(attribute => (attribute.Key, RuleText.Format(attribute.Value!.GetValue<string>(), values))));

    /// <summary>XML-escapes an attribute value, including both quote characters.</summary>
    public static string Attr(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#x27;");
}
