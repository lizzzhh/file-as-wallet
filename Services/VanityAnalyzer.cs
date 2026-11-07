using System;
using System.Collections.Generic;
using System.Linq;

namespace FileAsWallet.Services;

/// <summary>
/// 靓号判定（两套规则互斥）：
/// 1. 匹配串为空时，默认规则生效：前缀或尾号出现 4 位及以上连号；
/// 2. 填了匹配串：只按自定义规则判断，任意长度，可限定只看前缀、只看尾号，或不限定（前后缀都算）。
/// </summary>
public static class VanityAnalyzer
{
    /// <summary>默认连号阈值：4 位及以上。</summary>
    public const int MinRunLength = 4;

    public sealed record Result(bool Evm, bool Tron, string Reason)
    {
        public bool IsVanity => Evm || Tron;
    }

    /// <param name="matchPrefix">自定义串只匹配前缀。</param>
    /// <param name="matchSuffix">自定义串只匹配尾号。</param>
    /// <remarks>两者都为 false 时表示不限定位置，前缀或尾号命中都算。</remarks>
    public static Result Evaluate(
        string? evmAddress,
        string? tronAddress,
        string? pattern,
        bool matchPrefix,
        bool matchSuffix)
    {
        var needle = (pattern ?? string.Empty).Trim();
        if (needle.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            needle = needle[2..];

        // 都没勾 = 不限定位置，前后缀均可
        var unrestricted = !matchPrefix && !matchSuffix;
        var allowPrefix = unrestricted || matchPrefix;
        var allowSuffix = unrestricted || matchSuffix;

        var evmBody = Normalize(evmAddress, stripHexPrefix: true);
        var tronBody = Normalize(tronAddress, stripHexPrefix: false);

        var evmHit = Check(evmBody, needle, allowPrefix, allowSuffix, out var evmReason);
        var tronHit = Check(tronBody, needle, allowPrefix, allowSuffix, out var tronReason);

        var reasons = new List<string>();
        if (evmHit) reasons.Add($"EVM {evmReason}");
        if (tronHit) reasons.Add($"TRON {tronReason}");

        return new Result(evmHit, tronHit, string.Join("；", reasons.Distinct()));
    }

    private static string? Normalize(string? address, bool stripHexPrefix)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var value = address.Trim();
        if (stripHexPrefix && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        return value;
    }

    private static bool Check(
        string? body,
        string needle,
        bool allowPrefix,
        bool allowSuffix,
        out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrEmpty(body)) return false;

        var parts = new List<string>();

        if (needle.Length == 0)
        {
            // 用户没填匹配串时，才使用默认规则：前缀 / 尾号的 4 位以上连号
            var headRun = RunLength(body, fromEnd: false);
            if (headRun >= MinRunLength)
                parts.Add($"前缀 {headRun} 连号 {body[..headRun]}");

            var tailRun = RunLength(body, fromEnd: true);
            if (tailRun >= MinRunLength)
                parts.Add($"尾号 {tailRun} 连号 {body[^tailRun..]}");
        }
        else if (needle.Length <= body.Length)
        {
            // 用户填了匹配串：只按自定义规则判断，不再叠加默认连号
            var atPrefix = allowPrefix && body.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
            var atSuffix = allowSuffix && body.EndsWith(needle, StringComparison.OrdinalIgnoreCase);

            if (atPrefix) parts.Add($"前缀 {needle}（自定义）");
            if (atSuffix && !atPrefix) parts.Add($"尾号 {needle}（自定义）");
        }

        if (parts.Count == 0) return false;

        reason = string.Join("、", parts);
        return true;
    }

    /// <summary>从开头或结尾数连续相同字符的长度。</summary>
    private static int RunLength(string body, bool fromEnd)
    {
        var first = fromEnd ? body[^1] : body[0];
        var count = 1;

        for (var i = 1; i < body.Length; i++)
        {
            var c = fromEnd ? body[^(i + 1)] : body[i];
            if (char.ToUpperInvariant(c) != char.ToUpperInvariant(first)) break;
            count++;
        }

        return count;
    }
}
