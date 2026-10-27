using System.Text;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// A value-free structure fingerprint of a TDF payload: one line per member (path, tag and wire type), map keys, list
/// counts, union indices, and the values of an allow-list of protocol constants. No persona ids, names or addresses
/// are printed, so fingerprints of production frames can be committed as fixtures. Lists of unions (GAME.HNET) use the
/// Heat1 layout: each element is its active index followed by the struct body without a member header.
/// </summary>
internal static class TdfShape
{
    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        "GSET", "GSTA", "MCAP", "NTOP", "NRES", "PRES", "QCAP", "VOIP", "LFPJ", "QOSV", "TELM", "FIT", "MAXF", "MSCD", "RSLT", "PLAT",
        "CSID", "SID", "SLOT", "STAT", "TIDX", "JFPS", "EXID", "PSET", "RCRE", "HSLT", "DURA", "INTV", "SIZE", "GPVH", "DBPS", "NATT", "UBPS",
    };

    /// <summary>The fingerprint lines of a fixture file (comment lines skipped).</summary>
    public static IReadOnlyList<string> ReadFixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Where(line => !line.StartsWith('#')).ToList();

    public static IReadOnlyList<string> Of(byte[] payload)
    {
        var walker = new Walker(payload);
        while (walker.Position < payload.Length)
        {
            walker.Member(string.Empty);
        }
        return walker.Output;
    }

    /// <summary>
    /// Members written out of tag order, as "path: previous then tag". FIFA 21 reads a struct's members in ascending
    /// packed-tag order, so a member that follows a higher tag is skipped and keeps its default.
    /// </summary>
    public static IReadOnlyList<string> OrderViolations(byte[] payload)
    {
        var walker = new Walker(payload);
        while (walker.Position < payload.Length)
        {
            walker.Member(string.Empty);
        }
        return walker.Disorder;
    }

    private sealed class Walker(byte[] raw)
    {
        public int Position;
        public readonly List<string> Output = new();
        public readonly List<string> Disorder = new();

        /// <summary>The last packed tag seen at each open struct level (the payload itself is the outermost level).</summary>
        private readonly Stack<(uint Packed, string Tag)> _levels = new(new[] { (0u, string.Empty) });

        public void Member(string path)
        {
            var packed = (uint)(raw[Position] << 16 | raw[Position + 1] << 8 | raw[Position + 2]);
            var tag = Tag(Position);
            var type = raw[Position + 3];
            Position += 4;
            var (previous, previousTag) = _levels.Pop();
            if (packed <= previous)
            {
                Disorder.Add($"{(path.Length > 0 ? path : "/")}: {previousTag} then {tag}");
            }
            _levels.Push((packed, tag));
            var here = $"{path}/{tag}:{type}";
            Output.Add(here);
            Value(type, here, tag);
        }

        private void Struct(string path)
        {
            _levels.Push((0u, string.Empty));
            while (raw[Position] != 0)
            {
                Member(path);
            }
            Position++;
            _levels.Pop();
        }

        private void Value(int type, string path, string tag)
        {
            switch (type)
            {
                case 0:
                case 0xC:
                    {
                        var value = VarInt();
                        if (Values.Contains(tag))
                        {
                            Output[^1] += $" = {value}";
                        }
                        break;
                    }
                case 1:
                    Text();
                    break;
                case 2:
                    {
                        // Read the length before advancing: "Position += VarInt()" would add it to the pre-read position.
                        var length = (int)VarInt();
                        Position += length;
                        break;
                    }
                case 3:
                    Struct(path);
                    break;
                case 4:
                    {
                        var elementType = raw[Position++];
                        var count = (int)VarInt();
                        Output.Add($"{path} list<{elementType}> count={count}");
                        for (var index = 0; index < count; index++)
                        {
                            if (elementType == 3 && tag == "HNET")
                            {
                                Output.Add($"{path}[{index}] unionIndex={raw[Position++]}");
                                Struct($"{path}[{index}]");
                            }
                            else if (elementType == 3)
                            {
                                Struct($"{path}[{index}]");
                            }
                            else
                            {
                                Value(elementType, $"{path}[{index}]", string.Empty);
                            }
                        }
                        break;
                    }
                case 5:
                    {
                        var keyType = raw[Position++];
                        var valueType = raw[Position++];
                        var count = (int)VarInt();
                        var keys = new List<string>();
                        for (var index = 0; index < count; index++)
                        {
                            keys.Add(keyType == 1 ? Text() : VarInt().ToString(System.Globalization.CultureInfo.InvariantCulture));
                            if (valueType == 3)
                            {
                                Struct($"{path}{{{keys[^1]}}}");
                            }
                            else if (valueType == 1 && tag == "ATTR")
                            {
                                keys[^1] += "=" + Text();
                            }
                            else
                            {
                                Value(valueType, path, string.Empty);
                            }
                        }
                        Output.Add($"{path} map<{keyType},{valueType}> keys=[{string.Join(",", keys)}]");
                        break;
                    }
                case 6:
                    {
                        var index = raw[Position++];
                        Output.Add($"{path} unionIndex={index}");
                        if (index != 0x7F)
                        {
                            // The selected member is the union's only member: it starts its own level.
                            _levels.Push((0u, string.Empty));
                            Member(path);
                            _levels.Pop();
                        }
                        break;
                    }
                case 7:
                    // Variable: present byte, then (if present) the TDF type id and its members up to 0x00.
                    if (raw[Position++] != 0)
                    {
                        Output[^1] += $" tdfId={VarInt()}";
                        Struct(path);
                    }
                    break;
                case 8:
                    VarInt();
                    VarInt();
                    break;
                case 9:
                    VarInt();
                    VarInt();
                    VarInt();
                    break;
                case 0xA:
                    Position += 4;
                    break;
                default:
                    throw new InvalidDataException($"TDF type {type} at {Position} in {path}");
            }
        }

        private string Tag(int at)
        {
            var packed = (uint)(raw[at] << 16 | raw[at + 1] << 8 | raw[at + 2]);
            var characters = new char[4];
            for (var index = 0; index < 4; index++)
            {
                var code = (int)((packed >> (18 - 6 * index)) & 0x3F);
                characters[index] = code == 0 ? ' ' : (char)(code + 0x20);
            }
            return new string(characters).Trim();
        }

        private long VarInt()
        {
            var current = raw[Position++];
            long value = current & 0x3F;
            var shift = 6;
            while ((current & 0x80) != 0)
            {
                current = raw[Position++];
                value |= (long)(current & 0x7F) << shift;
                shift += 7;
            }
            return value;
        }

        private string Text()
        {
            var length = (int)VarInt();
            var text = Encoding.UTF8.GetString(raw, Position, Math.Max(0, length - 1));
            Position += length;
            return text;
        }
    }
}
