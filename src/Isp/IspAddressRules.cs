using System.Net;
using System.Net.Sockets;

namespace LineTester.Isp;

/// <summary>
/// 哪些地址值得（也允许）拿去查：只查**公网**地址。
///
/// 内网/保留/回环/链路本地/CGNAT 一律本地判定，不发请求——ip-api 对它们只会回
/// <c>private range</c>，白占免费额度（15 次/分钟）。
/// </summary>
public static class IspAddressRules
{
    /// <summary>从 <c>ip:port</c> 或裸地址里取出主机部分；取不到返回空串。</summary>
    public static string HostPart(string? text)
    {
        var value = text?.Trim() ?? "";
        if (value.Length == 0 || value == "-") return "";

        // IPv6 字面量带方括号：[::1]:9000
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            return end > 1 ? value[1..end] : value;
        }

        var separator = value.LastIndexOf(':');
        if (separator <= 0) return value;

        // 裸 IPv6（"::1"、"fe80::1"）没有端口可剥，原样返回
        if (value.Count(c => c == ':') > 1) return value;

        // 只有"冒号后面全是数字"才当成端口
        var tail = value[(separator + 1)..];
        return tail.Length > 0 && tail.All(char.IsAsciiDigit) ? value[..separator] : value;
    }

    /// <summary>是否公网地址（可直接拿去查）。域名返回 false——先由调用方解析成 IP。</summary>
    public static bool IsPublicIp(string? ip)
    {
        if (!IPAddress.TryParse(ip?.Trim() ?? "", out var address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            else return !(IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal ||
                          address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                          IsUniqueLocalV6(address));
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        return bytes[0] switch
        {
            0 => false,                                     // 0.0.0.0/8
            10 => false,                                    // 10/8
            100 when bytes[1] is >= 64 and <= 127 => false,  // 100.64/10 CGNAT
            127 => false,                                   // 回环
            169 when bytes[1] == 254 => false,              // 链路本地
            172 when bytes[1] is >= 16 and <= 31 => false,   // 172.16/12
            192 when bytes[1] == 168 => false,              // 192.168/16
            192 when bytes[1] == 0 && bytes[2] == 0 => false, // 192.0.0/24（含 TEST-NET-1）
            198 when bytes[1] == 18 || bytes[1] == 19 => false, // 基准测试网段
            198 when bytes[1] == 51 && bytes[2] == 100 => false, // TEST-NET-2
            203 when bytes[1] == 0 && bytes[2] == 113 => false,  // TEST-NET-3
            >= 224 => false,                                // 组播 / 保留
            _ => true
        };
    }

    private static bool IsUniqueLocalV6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }
}
