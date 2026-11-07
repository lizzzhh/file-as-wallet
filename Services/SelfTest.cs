using System;
using System.Linq;

namespace FileAsWallet.Services;

/// <summary>
/// 离线自检：用公开的测试向量验证 secp256k1 / EIP-55 / Base58Check 实现是否正确。
/// 运行方式：FileAsWallet --selftest
/// </summary>
public static class SelfTest
{
    private static int _failures;

    public static int Run()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 某些终端不支持 */ }

        Console.WriteLine("File as Wallet — 派生算法自检");
        Console.WriteLine(new string('-', 74));

        // 私钥 = 1 时，公钥就是生成点 G，这是最经典的测试向量
        Check(
            "私钥 0x…01",
            "0000000000000000000000000000000000000000000000000000000000000001",
            "0x7E5F4552091A69125d5DfCb7b8C2659029395Bdf");

        // ethereumjs / ethers 测试向量
        Check(
            "私钥 0x4c08…2318",
            "4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318",
            "0x2c7536E3605D9C16a7a3D7b1898e529396a65c23");

        // 私钥为全 0x46 的另一个常见向量
        Check(
            "私钥 0x4646…46",
            "4646464646464646464646464646464646464646464646464646464646464646",
            "0x9d8A62f656a8d1615C1294fd71e9CFb3E4855A4F");

        // 非法私钥：0 与 曲线阶 n 都必须被拒绝
        ExpectThrow("私钥 0x00…00 应被拒绝", "0".PadLeft(64, '0'));
        ExpectThrow("私钥 = n 应被拒绝", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");

        // Base58Check 往返：解码后应是 0x41 + 20 字节
        var sample = WalletService.Derive("0000000000000000000000000000000000000000000000000000000000000001");
        try
        {
            var payload = WalletService.Base58CheckDecode(sample.TronAddress);
            var ok = payload.Length == 21 && payload[0] == 0x41;
            Report("TRON Base58Check 往返解码", ok, ok ? "0x41 + 20 字节" : $"实际 {payload.Length} 字节");
        }
        catch (Exception ex)
        {
            Report("TRON Base58Check 往返解码", false, ex.Message);
        }

        // 靓号判定：默认认前缀/尾号的 4 位以上连号；自定义串可限定前缀、尾号或都不限定
        CheckVanity("尾号 4 连号 8888", "1234abcd5678ef901234abcd5678ef9012348888", null, false, false, true);
        CheckVanity("前缀 4 连号 0000", "0000abcd5678ef901234abcd5678ef9012345678", null, false, false, true);
        CheckVanity("中间 4 连号不算", "1234aaaa5678ef901234abcd5678ef9012345678", null, false, false, false);
        CheckVanity("3 连号不算靓号", "1234abcd5678ef901234abcd5678ef9012345888", null, false, false, false);
        CheckVanity("普通地址", "75ff5fa3b58e8f2b90b20b59cff4c5c38f0d0351", null, false, false, false);
        CheckVanity("自定义尾号 0351（不限定）", "75ff5fa3b58e8f2b90b20b59cff4c5c38f0d0351", "0351", false, false, true);
        CheckVanity("自定义前缀 75ff（不限定）", "75ff5fa3b58e8f2b90b20b59cff4c5c38f0d0351", "75ff", false, false, true);
        CheckVanity("只勾前缀时尾号不命中", "75ff5fa3b58e8f2b90b20b59cff4c5c38f0d0351", "0351", true, false, false);
        CheckVanity("只勾前缀时前缀命中", "75ff5fa3b58e8f2b90b20b59cff4c5c38f0d0351", "75ff", true, false, true);
        CheckVanity("只勾尾号时尾号命中", "75ff5fa3b58e8f2b90b20b59cff4c5c38f0d0351", "0351", false, true, true);
        CheckVanity("自定义未命中", "75ff5fa3b58e8f2b90b20b59cff4c5c38f0d0351", "9999", false, false, false);

        // 填了匹配串就不再套用默认连号规则
        CheckVanity("有匹配串时默认连号失效", "1234abcd5678ef901234abcd5678ef9012348888", "e", false, false, false);
        CheckVanity("有匹配串时默认前缀连号失效", "0000abcd5678ef901234abcd5678ef9012345678", "e", false, false, false);
        CheckVanity("匹配串为空仍走默认规则", "1234abcd5678ef901234abcd5678ef9012348888", "", false, false, true);

        Console.WriteLine(new string('-', 74));
        Console.WriteLine("地址参考值（可与 tronweb / ethers 对拍）：");
        foreach (var (label, key) in new[]
                 {
                     ("pk=0x…01   ", "0000000000000000000000000000000000000000000000000000000000000001"),
                     ("pk=0x4c08… ", "4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318"),
                     ("pk=0x4646… ", "4646464646464646464646464646464646464646464646464646464646464646"),
                 })
        {
            var d = WalletService.Derive(key);
            Console.WriteLine($"  {label} EVM  {d.EvmAddress}");
            Console.WriteLine($"  {label} TRON {d.TronAddress}");
        }

        Console.WriteLine(new string('-', 74));
        Console.WriteLine(_failures == 0 ? "全部通过 ✓" : $"{_failures} 项失败 ✗");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string label, string privateKey, string expectedEvm)
    {
        var derived = WalletService.Derive(privateKey);
        var ok = string.Equals(derived.EvmAddress, expectedEvm, StringComparison.Ordinal);
        Report($"EVM 地址 · {label}", ok, ok ? derived.EvmAddress : $"期望 {expectedEvm}，实际 {derived.EvmAddress}");
    }

    private static void CheckVanity(
        string label, string evm, string? pattern, bool matchPrefix, bool matchSuffix, bool expected)
    {
        var result = VanityAnalyzer.Evaluate(evm, null, pattern, matchPrefix, matchSuffix);
        Report($"靓号判定 · {label}", result.IsVanity == expected,
            result.IsVanity == expected ? result.Reason : $"期望 {expected}，实际 {result.IsVanity}");
    }

    private static void ExpectThrow(string label, string privateKey)    {
        try
        {
            WalletService.Derive(privateKey);
            Report(label, false, "未抛出异常");
        }
        catch (Exception)
        {
            Report(label, true, string.Empty);
        }
    }

    private static void Report(string label, bool ok, string detail)
    {
        if (!ok) _failures++;
        var mark = ok ? "PASS" : "FAIL";
        Console.WriteLine($"  [{mark}] {label}{(string.IsNullOrEmpty(detail) ? "" : $"  →  {detail}")}");
    }
}
