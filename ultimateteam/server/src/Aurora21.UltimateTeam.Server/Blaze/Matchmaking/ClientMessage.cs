using System.Text;
using Aurora21.UltimateTeam.Protocol.Tdf;

namespace Aurora21.UltimateTeam.Server.Blaze.Matchmaking;

/// <summary>
/// One Blaze::Messaging::ClientMessage (Messaging.SendMessage): ATTR map&lt;uint32,string&gt;, FLAG MessageFlags, STAT, TAG,
/// TIDS list&lt;int64&gt;, TTYP ObjectType and TYPE (schema read from the FIFA 21 dump by the Python server). The client's builder
/// writes only TTYP, TIDS, FLAG, TYPE, TAG and ATTR and may omit empty members, so every member is optional.
/// </summary>
public sealed record ClientMessage(
    IReadOnlyList<KeyValuePair<long, string>> Attributes,
    long Flags,
    long Status,
    long Tag,
    IReadOnlyList<long> TargetIds,
    (long Component, long EntityType)? TargetType,
    long Type,
    IReadOnlyList<string> UnknownTags)
{
    /// <summary>MessageFlags bit 1, Echo: deliver a copy back to the sender (FIFA sets it on every co-op gctl message).</summary>
    public bool Echo => (Flags & 0x2) != 0;

    /// <summary>The decoded message, or null when the payload is not a readable TDF.</summary>
    public static ClientMessage? Parse(byte[] payload)
    {
        if (!TdfReader.TryParse(payload, out var fields))
        {
            return null;
        }
        var attributes = new List<KeyValuePair<long, string>>();
        var targets = new List<long>();
        var unknown = new List<string>();
        (long, long)? targetType = null;
        long flags = 0, status = 0, tag = 0, type = 0;
        foreach (var field in fields)
        {
            switch (field.Tag, field.Type)
            {
                case ("ATTR", TdfType.Map):
                    for (var index = 0; index + 1 < field.Children.Count; index += 2)
                    {
                        attributes.Add(KeyValuePair.Create(field.Children[index].Integer, field.Children[index + 1].Text ?? string.Empty));
                    }
                    break;
                case ("TIDS", TdfType.List):
                    targets.AddRange(field.Children.Where(child => child.Type == TdfType.Integer).Select(child => child.Integer));
                    break;
                case ("TTYP", TdfType.ObjectType):
                    targetType = (field.Children[0].Integer, field.Children[1].Integer);
                    break;
                case ("FLAG", TdfType.Integer):
                    flags = field.Integer;
                    break;
                case ("STAT", TdfType.Integer):
                    status = field.Integer;
                    break;
                case ("TAG", TdfType.Integer):
                    tag = field.Integer;
                    break;
                case ("TYPE", TdfType.Integer):
                    type = field.Integer;
                    break;
                default:
                    unknown.Add($"{field.Tag}/{(int)field.Type}");
                    break;
            }
        }
        return new ClientMessage(attributes, flags, status, tag, targets, targetType, type, unknown);
    }

    /// <summary>
    /// Why this message must not be relayed, or null. The receiving bridge (0x140d75ea0) drops anything whose TTYP component
    /// is not GameManager before it reaches GSGame, and dereferences TIDS.begin() without an emptiness check, so an empty
    /// target list would crash the receiving client.
    /// </summary>
    public string? Problem(BlazeMessagingLimits limits)
    {
        if (TargetType is not { } targetType)
        {
            return "no-target-type";
        }
        if (targetType.Component != BlazeComponent.GameManager)
        {
            return "target-type-not-gamemanager";
        }
        if (TargetIds.Count == 0)
        {
            return "no-target-ids";
        }
        if (TargetIds.Count > limits.MaxTargetIds)
        {
            return "too-many-target-ids";
        }
        if (Attributes.Count > limits.MaxAttributePairs)
        {
            return "too-many-attributes";
        }
        return Attributes.Any(pair => Encoding.UTF8.GetByteCount(pair.Value) > limits.MaxAttributeBytes) ? "oversize-attribute" : null;
    }

    /// <summary>The attributes for a log line, keys as FourCCs.</summary>
    public string DescribeAttributes()
    {
        var text = "{" + string.Join(", ", Attributes.Select(pair => $"{FourCc(pair.Key)}={pair.Value}")) + "}";
        return text.Length > 400 ? text[..400] + "…" : text;
    }

    /// <summary>A Blaze FourCC (ClientMessage TYPE and TAG, ATTR keys) as text, or hex when it is not printable ASCII.</summary>
    public static string FourCc(long value)
    {
        if (value is < 0 or > 0xFFFFFFFF)
        {
            return $"0x{value:x}";
        }
        Span<byte> raw = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(raw, (uint)value);
        foreach (var character in raw)
        {
            if (character is < 0x20 or > 0x7E)
            {
                return $"0x{value:x8}";
            }
        }
        return Encoding.ASCII.GetString(raw);
    }
}
