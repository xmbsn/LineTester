using System.Security.Cryptography;
using System.Text;

namespace LineTester.Registry;

/// <summary>
/// 认证与防重放（规格 §5.4）。
///
/// <c>mac = HMAC-SHA256(Secret, TYPE + "|" + rawPayload)</c>：
/// 对收到的**原始文本**计算，不做 JSON 重新序列化——否则字段顺序或转义差异会让校验失败。
/// 密钥不上线，只在 HMAC 输入里使用。
/// </summary>
public static class RegistryAuth
{
    /// <summary>时间戳容忍窗口（秒）。</summary>
    public const int TimestampToleranceSeconds = 300;

    public static string ComputeMac(string secret, string type, string rawPayload)
    {
        var key = Encoding.UTF8.GetBytes(secret ?? "");
        var data = Encoding.UTF8.GetBytes($"{type}|{rawPayload}");
        return Convert.ToHexString(HMACSHA256.HashData(key, data)).ToLowerInvariant();
    }

    public static bool VerifyMac(string secret, string type, string rawPayload, string? mac)
        => !string.IsNullOrEmpty(mac) && FixedTimeEquals(ComputeMac(secret, type, rawPayload), mac);

    /// <summary>常量时间比较，避免通过响应时间逐字节猜测 mac。</summary>
    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;

        var difference = 0;
        for (var i = 0; i < left.Length; i++)
        {
            difference |= char.ToLowerInvariant(left[i]) ^ char.ToLowerInvariant(right[i]);
        }

        return difference == 0;
    }

    public static bool IsTimestampFresh(long unixSeconds, DateTime nowUtc)
        => Math.Abs(ToUnixSeconds(nowUtc) - unixSeconds) <= TimestampToleranceSeconds;

    public static long NowUnixSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static long ToUnixSeconds(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>8 位十六进制随机 nonce。</summary>
    public static string NewNonce() => RandomHex(4);

    /// <summary>8 位十六进制随机请求号。</summary>
    public static string NewRequestId() => RandomHex(4);

    private static string RandomHex(int byteCount)
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
}

/// <summary>
/// nonce 去重：保留最近 N 条（默认 256），超出淘汰最旧的。
/// 重复 nonce 视为重放，直接拒绝。
/// </summary>
public sealed class NonceCache
{
    private readonly int _capacity;
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public NonceCache(int capacity = 256) => _capacity = capacity > 0 ? capacity : 1;

    public int Count => _seen.Count;

    /// <summary>返回 false 表示该 (nodeId, nonce) 已经出现过（重放）。</summary>
    public bool TryAdd(string nodeId, string nonce)
    {
        var key = $"{nodeId}|{nonce}";
        if (!_seen.Add(key)) return false;

        _order.AddLast(key);
        while (_order.Count > _capacity)
        {
            var oldest = _order.First!.Value;
            _order.RemoveFirst();
            _seen.Remove(oldest);
        }

        return true;
    }
}
