using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace FileAsWallet.Services;

/// <summary>
/// 用 secp256k1 私钥（这里就是文件的 SHA-256）派生 EVM / TRON 地址。
/// 全程本地计算，不联网、不依赖任何第三方节点。
/// </summary>
public static class WalletService
{
    private const string Base58Alphabet =
        "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    private static readonly Org.BouncyCastle.Asn1.X9.X9ECParameters Curve = SecNamedCurves.GetByName("secp256k1");
    private static readonly ECDomainParameters Domain =
        new(Curve.Curve, Curve.G, Curve.N, Curve.H);

    public sealed record DerivedAddress(string PrivateKeyHex, string EvmAddress, string TronAddress);

    /// <summary>
    /// 把 32 字节十六进制私钥派生为地址。私钥必须落在 [1, n-1] 区间内，否则抛出异常。
    /// </summary>
    public static DerivedAddress Derive(string privateKeyHex)
    {
        var keyHex = Normalize(privateKeyHex);
        var bytes = Convert.FromHexString(keyHex);
        if (bytes.Length != 32)
            throw new ArgumentException("私钥必须是 32 字节（64 个十六进制字符）");

        var d = new BigInteger(1, bytes);
        if (d.SignValue == 0 || d.CompareTo(Domain.N) >= 0)
            throw new InvalidOperationException("该哈希不在 secp256k1 私钥的有效范围内（为 0 或大于等于曲线阶 n），无法派生地址");

        ECPoint q = Domain.G.Multiply(d).Normalize();
        byte[] uncompressed = q.GetEncoded(false); // 0x04 || X(32) || Y(32)
        byte[] publicKey = uncompressed[1..];

        byte[] hash = Keccak256(publicKey);
        byte[] address20 = hash[12..];

        return new DerivedAddress(
            PrivateKeyHex: keyHex,
            EvmAddress: ToEip55(address20),
            TronAddress: ToTronBase58(address20));
    }

    /// <summary>去掉 0x 前缀并统一小写，同时校验字符合法性。</summary>
    public static string Normalize(string hex)
    {
        var s = (hex ?? string.Empty).Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        if (s.Length != 64)
            throw new ArgumentException("私钥必须是 64 个十六进制字符");
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c))
                throw new ArgumentException("私钥包含非十六进制字符");
        }
        return s.ToLowerInvariant();
    }

    private static byte[] Keccak256(ReadOnlySpan<byte> data)
    {
        var digest = new KeccakDigest(256);
        var input = data.ToArray();
        digest.BlockUpdate(input, 0, input.Length);
        var result = new byte[32];
        digest.DoFinal(result, 0);
        return result;
    }

    private static byte[] Sha256(byte[] data)
    {
        var digest = new Sha256Digest();
        digest.BlockUpdate(data, 0, data.Length);
        var result = new byte[32];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <summary>EIP-55 大小写校验和格式。</summary>
    private static string ToEip55(byte[] address20)
    {
        var lower = Convert.ToHexString(address20).ToLowerInvariant();
        var hash = Keccak256(Encoding.ASCII.GetBytes(lower));
        var sb = new StringBuilder("0x");
        for (var i = 0; i < lower.Length; i++)
        {
            var c = lower[i];
            var nibble = (i % 2 == 0) ? (hash[i / 2] >> 4) : (hash[i / 2] & 0x0F);
            sb.Append(c is >= 'a' and <= 'f' && nibble >= 8 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    /// <summary>TRON 地址 = Base58Check(0x41 || keccak256(pubkey)[12..32])。</summary>
    private static string ToTronBase58(byte[] address20)
    {
        var payload = new byte[21];
        payload[0] = 0x41;
        address20.CopyTo(payload, 1);
        return Base58CheckEncode(payload);
    }

    private static string Base58CheckEncode(byte[] payload)
    {
        var checksum = Sha256(Sha256(payload))[..4];
        var data = payload.Concat(checksum).ToArray();

        var leadingZeros = 0;
        while (leadingZeros < data.Length && data[leadingZeros] == 0)
            leadingZeros++;

        var sb = new StringBuilder();
        var value = new BigInteger(1, data);
        var radix = BigInteger.ValueOf(58);
        while (value.SignValue > 0)
        {
            var qr = value.DivideAndRemainder(radix);
            value = qr[0];
            sb.Insert(0, Base58Alphabet[qr[1].IntValue]);
        }

        for (var i = 0; i < leadingZeros; i++)
            sb.Insert(0, '1');

        return sb.ToString();
    }

    /// <summary>解码工具，用于自检（Base58Check 反向验证）。</summary>
    public static byte[] Base58CheckDecode(string encoded)
    {
        var value = BigInteger.Zero;
        var radix = BigInteger.ValueOf(58);
        foreach (var c in encoded)
        {
            var idx = Base58Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException("非法的 Base58 字符");
            value = value.Multiply(radix).Add(BigInteger.ValueOf(idx));
        }

        var bytes = value.ToByteArrayUnsigned();
        var leadingOnes = encoded.TakeWhile(c => c == '1').Count();
        var buffer = new List<byte>(new byte[leadingOnes]);
        buffer.AddRange(bytes);

        if (buffer.Count < 5) throw new FormatException("Base58Check 数据太短");
        var payload = buffer.Take(buffer.Count - 4).ToArray();
        var checksum = buffer.Skip(buffer.Count - 4).ToArray();
        var expected = Sha256(Sha256(payload))[..4];
        if (!checksum.SequenceEqual(expected)) throw new FormatException("Base58Check 校验和不匹配");
        return payload;
    }
}
