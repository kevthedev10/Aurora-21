using System.Buffers;
using Aurora21.UltimateTeam.Protocol.Fire2;
using Aurora21.UltimateTeam.Protocol.Lsx;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>Byte vectors captured from the original Python runtime (server.py helpers).</summary>
public sealed class ProtocolVectorTests
{
    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Theory]
    [InlineData("CONF", TdfType.Struct, "8efba603")]
    [InlineData("1CON", TdfType.Integer, "463bee00")]
    public void TagsMatchPython(string tag, TdfType type, string expected)
    {
        Assert.Equal(expected, Hex(TdfTag.Encode(tag, type)));
        Assert.Equal(tag, TdfTag.Decode(TdfTag.Encode(tag, type)));
    }

    [Theory]
    [InlineData(0L, "00")]
    [InlineData(63L, "3f")]
    [InlineData(64L, "8001")]
    [InlineData(300L, "ac04")]
    [InlineData(123456789L, "95b4de75")]
    [InlineData(-1L, "bfffffffffffffffff03")]
    public void VarIntsMatchPython(long value, string expected)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        TdfWriter.WriteVarInt(buffer, value);
        Assert.Equal(expected, Hex(buffer.WrittenSpan));
        var position = 0;
        Assert.Equal(value, TdfReader.ReadVarInt(buffer.WrittenSpan, ref position));
    }

    /// <summary>The client writes negative integers sign-magnitude: 0x40 in the first byte is the sign.</summary>
    [Theory]
    [InlineData("41", -1L)]
    [InlineData("7f", -63L)]
    [InlineData("c001", -64L)]
    [InlineData("40", long.MinValue)]
    public void ClientNegativeIntegersAreSignMagnitude(string hex, long expected)
    {
        var position = 0;
        Assert.Equal(expected, TdfReader.ReadVarInt(Convert.FromHexString(hex), ref position));
        Assert.Equal(hex.Length / 2, position);
    }

    /// <summary>
    /// A StartMatchmakingScenario-shaped request: SCNA is a map of generic values (present byte, TDF type id, the value
    /// without a member header, then 0x00). The whole payload parses, so lookups need no byte search.
    /// </summary>
    [Fact]
    public void GenericMapValuesParse()
    {
        var w = new ArrayBufferWriter<byte>();
        w.Write(new TdfWriter().Str("GVER", "patchmotueka").ToArray());
        TdfTag.Write(w, "SCNA", TdfType.Map);
        w.Write(new[] { (byte)TdfType.String, (byte)TdfType.Generic });
        TdfWriter.WriteVarInt(w, 2);
        WriteText(w, "GAME_MODE");
        w.Write(new byte[] { 1 });
        TdfWriter.WriteVarInt(w, 22);
        TdfWriter.WriteVarInt(w, 76);
        w.Write(new byte[] { 0 });
        WriteText(w, "PLATFORM");
        w.Write(new byte[] { 1 });
        TdfWriter.WriteVarInt(w, 6);
        WriteText(w, "pc");
        w.Write(new byte[] { 0 });
        w.Write(new TdfWriter().Str("SCNM", "FUT_Rivals").ToArray());
        var payload = w.WrittenSpan.ToArray();

        Assert.True(TdfReader.TryParse(payload, out var fields));
        var scna = TdfReader.Find(fields, "SCNA", TdfType.Map)!;
        Assert.Equal(TdfType.Generic, scna.Children[1].Type);
        Assert.Equal(76, scna.Children[1].Children[0].Integer);
        Assert.Equal("pc", scna.Children[3].Children[0].Text);
        Assert.Equal("FUT_Rivals", TdfReader.FindString(payload, "SCNM"));
        Assert.Equal("76", TdfReader.FindGenericMap(payload, "SCNA")["GAME_MODE"]);
    }

    [Fact]
    public void OrderCheckFlagsMembersAfterAHigherTag()
    {
        Assert.Empty(TdfShape.OrderViolations(new TdfWriter().Int("ANON", 0).Struct("SESS", s => s.Int("BUID", 1).Int("UID", 2)).Int("SPAM", 0).ToArray()));
        Assert.Equal(new[] { "/: SKEY then ANON", "/SESS:3: UID then BUID" },
            TdfShape.OrderViolations(new TdfWriter().Str("SKEY", "k").Int("ANON", 0).Struct("SESS", s => s.Int("UID", 2).Int("BUID", 1)).ToArray()));
    }

    private static void WriteText(ArrayBufferWriter<byte> w, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        TdfWriter.WriteVarInt(w, bytes.Length + 1);
        w.Write(bytes);
        w.Write(new byte[] { 0 });
    }

    [Fact]
    public void StringFieldMatchesPython() =>
        Assert.Equal("ba1b6501064c6f63616c00", Hex(new TdfWriter().Str("NAME", "Local").ToArray()));

    [Fact]
    public void GameReportResultMatchesPythonAndParses()
    {
        var payload = new TdfWriter().NullVariable("DATA").Int("EROR", 0).Int("FNL", 1).Int("GHID", 12345).Int("GRID", 12345).ToArray();
        Assert.Equal("921d210700972bf200009aeb0000019e8a6400b9c0019f2a6400b9c001", Hex(payload));
        Assert.Equal(0, TdfReader.FindInteger(payload, "EROR"));
        Assert.Equal(1, TdfReader.FindInteger(payload, "FNL"));
        Assert.Equal(12345, TdfReader.FindInteger(payload, "GRID"));
    }

    [Fact]
    public void TickerGroupsMatchPython()
    {
        var payload = new TdfWriter().ListStruct("LGRP", new Action<TdfWriter>[]
        {
            w => w.Str("ID", "O_SG_TCKR").ListString("LSET", new[] { "O_TKfilter" }),
        }).ToArray();
        Assert.Equal("b27cb0040301a64000010a4f5f53475f54434b5200b339740401010b4f5f544b66696c7465720000", Hex(payload));
        Assert.True(TdfReader.TryParse(payload, out var fields));
        Assert.Equal("O_SG_TCKR", TdfReader.Find(fields, "ID", TdfType.String)?.Text);
    }

    [Fact]
    public void FrameBuildMatchesPythonAndRoundTrips()
    {
        var frame = Fire2Frame.Build(9, 7, 0x123456, Fire2MessageType.Reply, new byte[] { 1, 2 });
        Assert.Equal("000000020000000900071234562000000102", Hex(frame));
        var sequence = new ReadOnlySequence<byte>(frame);
        Assert.True(Fire2Frame.TryRead(ref sequence, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(9, parsed!.Component);
        Assert.Equal(7, parsed.Command);
        Assert.Equal(0x123456, parsed.MessageNumber);
        Assert.Equal(Fire2MessageType.Reply, parsed.Type);
        Assert.Equal(new byte[] { 1, 2 }, parsed.Payload);
        Assert.Equal(0, sequence.Length);
    }

    [Fact]
    public void FrameReadAcceptsThePayloadLimitAndRefusesLargerFrames()
    {
        var atLimit = new ReadOnlySequence<byte>(Fire2Frame.Build(4, 1, 1, Fire2MessageType.Message, new byte[Fire2Frame.MaxPayloadSize]));
        Assert.True(Fire2Frame.TryRead(ref atLimit, out var parsed));
        Assert.Equal(Fire2Frame.MaxPayloadSize, parsed!.Payload.Length);

        // Only the header has arrived: the announced size alone is enough to refuse the frame.
        var header = Fire2Frame.Build(4, 1, 1, Fire2MessageType.Message, new byte[Fire2Frame.MaxPayloadSize + 1])[..Fire2Frame.HeaderSize];
        var tooLarge = new ReadOnlySequence<byte>(header);
        var error = Assert.Throws<FormatException>(() => Fire2Frame.TryRead(ref tooLarge, out _));
        Assert.Contains("component=4 command=1", error.Message);
    }

    [Fact]
    public void LsxKeyDerivationAndCipherMatchPython()
    {
        Assert.Equal("9482289812735166117f5427a6759154", Hex(OriginCrypto.DeriveKey(0x3132)));
        Assert.Equal(0x3132u, OriginCrypto.SeedFromResponse("12ab"));
        var crypto = new OriginCrypto();
        const string challenge = "ABCDEF0123456789ABCDEF0123456789";
        Assert.Equal(
            "d378b3e61840a049257af616a7bdd9e0d378b3e61840a049257af616a7bdd9e0954f64f2e4e86e9eee82d20216684899",
            crypto.EncryptToHex(challenge));
        Assert.Equal(challenge, crypto.Decrypt(crypto.Encrypt(challenge)));
    }
}
