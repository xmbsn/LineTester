using System.Net;
using System.Net.Sockets;

namespace LineTester.Registry;

/// <summary>
/// 可分发地址判定（规格 §6.1）。
///
/// 最关键的一条：**回环地址绝不分发**。否则其他节点会去探测自己的回环，
/// 显示出一条"0.1ms、零丢包"的完美假链路——对测量工具是灾难性误导。
/// </summary>
public static class RegistryAddressRules
{
    /// <summary>
    /// 该地址是否允许分发给其他节点。
    /// 返回 false 时 <paramref name="errorCode"/> 为规格 §5.3.4 的错误码。
    /// </summary>
    public static bool IsDistributable(string? host, out string errorCode, out string message)
    {
        errorCode = "";
        message = "";

        var value = host?.Trim() ?? "";
        if (value.Length == 0)
        {
            errorCode = RegistryErrorCodes.Unspecified;
            message = "地址为空（PublicHost 需要手工填写本端对外地址）";
            return false;
        }

        if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = RegistryErrorCodes.Loopback;
            message = "localhost 是本机回环地址，不会被分发";
            return false;
        }

        // 域名：本机无法判定其指向，放行（由运维负责）
        if (!IPAddress.TryParse(value, out var address)) return true;

        // 双栈监听下 IPv4 会以 IPv4-mapped IPv6 形式出现，先还原
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
        {
            errorCode = RegistryErrorCodes.Loopback;
            message = $"{value} 是本机回环地址，不会被分发（其他节点会测到自己的回环）";
            return false;
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            errorCode = RegistryErrorCodes.Unspecified;
            message = $"{value} 是未指定地址，无可达语义";
            return false;
        }

        if (IsLinkLocal(address))
        {
            errorCode = RegistryErrorCodes.LinkLocal;
            message = $"{value} 是 link-local 地址，不可路由";
            return false;
        }

        return true;
    }

    public static bool IsDistributable(string? host) => IsDistributable(host, out _, out _);

    private static bool IsLinkLocal(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254;
        }

        return address.IsIPv6LinkLocal;
    }
}
