using System.Security.Cryptography;
using System.Text;

namespace Aurora21.UltimateTeam.Protocol.Lsx;

/// <summary>The MSVC <c>rand()</c> linear congruential generator used by the Origin SDK for LSX key derivation.</summary>
public sealed class OriginRandom
{
    private uint _seed;

    public OriginRandom(uint seed) => _seed = seed;

    public void SetSeed(uint seed) => _seed = seed;

    public int Next()
    {
        _seed = unchecked(_seed * 214013u + 2531011u);
        return (int)((_seed >> 16) & 0x7FFF);
    }
}

/// <summary>AES-128-ECB/PKCS7 cipher used for Origin LSX frames.</summary>
public sealed class OriginCrypto
{
    private static readonly byte[] PrimaryKey = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    private readonly Aes _aes;

    public OriginCrypto(uint seed = 0)
    {
        _aes = Aes.Create();
        _aes.Key = DeriveKey(seed);
    }

    public byte[] Key => _aes.Key;

    public static byte[] DeriveKey(uint seed)
    {
        if (seed == 0)
        {
            return (byte[])PrimaryKey.Clone();
        }
        var random = new OriginRandom(7);
        random.SetSeed(unchecked((uint)random.Next() + seed));
        var key = new byte[16];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(random.Next() & 0xFF);
        }
        return key;
    }

    /// <summary>Seed derived from the first two ASCII characters of a hex challenge response (protocol 3).</summary>
    public static uint SeedFromResponse(string hexResponse)
    {
        if (hexResponse.Length < 2)
        {
            throw new ArgumentException("challenge response is too short", nameof(hexResponse));
        }
        return (uint)((hexResponse[0] << 8) | hexResponse[1]);
    }

    public byte[] Encrypt(string text) => _aes.EncryptEcb(Encoding.UTF8.GetBytes(text), PaddingMode.PKCS7);

    public string EncryptToHex(string text) => Convert.ToHexString(Encrypt(text)).ToLowerInvariant();

    public string Decrypt(ReadOnlySpan<byte> ciphertext) =>
        Encoding.UTF8.GetString(_aes.DecryptEcb(ciphertext, PaddingMode.PKCS7));
}
