using System;
using System.Collections.Generic;
using System.IO;

namespace Aurora21.MenuDiagnostics
{
    public sealed class Packet
    {
        public DateTime Utc { get; set; }
        public string Direction { get; set; }
        public string LocalAddress { get; set; }
        public int LocalPort { get; set; }
        public int ServerPort { get; set; }
        public uint Sequence { get; set; }
        public uint Acknowledgement { get; set; }
        public int PayloadBytes { get; set; }
        public int Window { get; set; }
        public string Flags { get; set; }
        public bool RepeatedData { get; set; }
    }

    // Reads headers only. No payload bytes or TLS secrets are exported.
    public static class TraceReader
    {
        private sealed class Interface
        {
            public ushort LinkType;
            public decimal Units = 1000000;
            public long OffsetSeconds;
        }

        private static ushort Be16(byte[] b, int at)
        { return (ushort)((b[at] << 8) | b[at + 1]); }

        private static uint Be32(byte[] b, int at)
        { return ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3]; }

        public static List<Packet> Read(string path, string serverIp)
        {
            var result = new List<Packet>();
            var interfaces = new List<Interface>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using (var input = new BinaryReader(File.OpenRead(path)))
            {
                while (input.BaseStream.Position < input.BaseStream.Length)
                {
                    if (input.BaseStream.Length - input.BaseStream.Position < 12)
                        throw new InvalidDataException("Truncated pcapng block.");
                    var type = input.ReadUInt32();
                    var length = input.ReadUInt32();
                    if (length < 12 || length > 16 * 1024 * 1024 || length % 4 != 0)
                        throw new InvalidDataException("Invalid or unsupported pcapng block size.");
                    var body = input.ReadBytes((int)length - 12);
                    if (body.Length != length - 12 || input.ReadUInt32() != length)
                        throw new InvalidDataException("Truncated or inconsistent pcapng block.");
                    if (type == 0x0A0D0D0A)
                    {
                        if (body.Length < 16 || BitConverter.ToUInt32(body, 0) != 0x1A2B3C4D)
                            throw new InvalidDataException("Only little-endian pcapng is supported.");
                        interfaces.Clear();
                    }
                    else if (type == 1)
                    {
                        if (body.Length < 8) throw new InvalidDataException("Truncated interface block.");
                        var info = new Interface { LinkType = BitConverter.ToUInt16(body, 0) };
                        if (info.LinkType != 1 && info.LinkType != 101 && info.LinkType != 228)
                            throw new InvalidDataException("Unsupported capture link type: " + info.LinkType);
                        for (int at = 8; at + 4 <= body.Length; )
                        {
                            var code = BitConverter.ToUInt16(body, at);
                            var size = BitConverter.ToUInt16(body, at + 2);
                            at += 4;
                            if (code == 0) break;
                            if (at + size > body.Length) throw new InvalidDataException("Truncated interface option.");
                            if (code == 9 && size == 1)
                            {
                                int exponent = body[at] & 127;
                                if (exponent > 20) throw new InvalidDataException("Unsupported timestamp resolution.");
                                decimal radix = (body[at] & 128) == 0 ? 10 : 2;
                                info.Units = 1;
                                for (int i = 0; i < exponent; i++) info.Units *= radix;
                            }
                            if (code == 14 && size == 8) info.OffsetSeconds = BitConverter.ToInt64(body, at);
                            at += (size + 3) & ~3;
                        }
                        interfaces.Add(info);
                    }
                    else if (type == 6)
                    {
                        if (body.Length < 20) throw new InvalidDataException("Truncated packet block.");
                        uint id = BitConverter.ToUInt32(body, 0);
                        uint cap = BitConverter.ToUInt32(body, 12);
                        if (id >= interfaces.Count || cap > body.Length - 20)
                            throw new InvalidDataException("Invalid packet interface or captured length.");
                        var info = interfaces[(int)id];
                        ulong stamp = ((ulong)BitConverter.ToUInt32(body, 4) << 32) | BitConverter.ToUInt32(body, 8);
                        long ticks = checked((long)((decimal)stamp * TimeSpan.TicksPerSecond / info.Units)
                            + info.OffsetSeconds * TimeSpan.TicksPerSecond);
                        var utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(ticks);
                        var packet = Decode(body, 20, (int)cap, info.LinkType, utc, serverIp);
                        if (packet != null)
                        {
                            // A repeated sequence is evidence, not a proof of loss: NIC duplicates/offload can occur.
                            if (packet.PayloadBytes > 0)
                                packet.RepeatedData = !seen.Add(packet.LocalAddress + ":" + packet.LocalPort + ":"
                                    + packet.ServerPort + ":" + packet.Direction + ":" + packet.Sequence + ":" + packet.PayloadBytes);
                            result.Add(packet);
                        }
                    }
                }
            }
            result.Sort((a, b) => a.Utc.CompareTo(b.Utc));
            return result;
        }

        private static Packet Decode(byte[] b, int start, int size, ushort linkType, DateTime utc, string serverIp)
        {
            int end = start + size;
            int ip = start;
            if (linkType == 1)
            {
                if (size < 14) return null;
                int protocol = Be16(b, start + 12);
                ip += 14;
                while (protocol == 0x8100 || protocol == 0x88A8)
                {
                    if (ip + 4 > end) return null;
                    protocol = Be16(b, ip + 2);
                    ip += 4;
                }
                if (protocol != 0x0800) return null;
            }
            if (ip + 20 > end || b[ip] >> 4 != 4 || b[ip + 9] != 6) return null;
            int ihl = (b[ip] & 15) * 4;
            // Ignore fragments; they cannot reliably supply a TCP header.
            if (ihl < 20 || (Be16(b, ip + 6) & 0x3FFF) != 0) return null;
            int tcp = ip + ihl;
            if (tcp + 20 > end) return null;
            var src = new System.Net.IPAddress(new byte[] { b[ip + 12], b[ip + 13], b[ip + 14], b[ip + 15] }).ToString();
            var dst = new System.Net.IPAddress(new byte[] { b[ip + 16], b[ip + 17], b[ip + 18], b[ip + 19] }).ToString();
            bool outbound = dst == serverIp;
            if (!outbound && src != serverIp) return null;
            int tcpSize = (b[tcp + 12] >> 4) * 4;
            int payload = Be16(b, ip + 2) - ihl - tcpSize;
            if (tcpSize < 20 || payload < 0) return null;
            var flags = new List<string>();
            string[] names = { "FIN", "SYN", "RST", "PSH", "ACK", "URG", "ECE", "CWR" };
            for (int i = 0; i < 8; i++) if ((b[tcp + 13] & (1 << i)) != 0) flags.Add(names[i]);
            return new Packet { Utc = utc, Direction = outbound ? "PC->server" : "server->PC",
                LocalAddress = outbound ? src : dst, LocalPort = Be16(b, tcp + (outbound ? 0 : 2)),
                ServerPort = Be16(b, tcp + (outbound ? 2 : 0)), Sequence = Be32(b, tcp + 4),
                Acknowledgement = Be32(b, tcp + 8), PayloadBytes = payload, Window = Be16(b, tcp + 14),
                Flags = String.Join("|", flags) };
        }
    }
}
