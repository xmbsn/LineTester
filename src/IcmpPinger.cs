using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace LineTester;

/// <summary>一次 ICMP 探测的结果。</summary>
public sealed record IcmpReply(bool Success, int RttMs, string FailureToken, string Detail)
{
    public static IcmpReply Ok(int rttMs) => new(true, rttMs, "", "");

    public static IcmpReply Failed(string token, string detail) => new(false, 0, token, detail);
}

/// <summary>
/// ICMP ping（规格：README「ICMP 探测」）。
///
/// 走 Windows 自带的 <c>IcmpSendEcho</c>（iphlpapi）而不是原始套接字，原因：
/// <list type="bullet">
/// <item>原始套接字需要**管理员权限**，现场不可能每次都提权；</item>
/// <item><c>IcmpSendEcho</c> 由系统负责 ICMP 标识/序号匹配，返回的往返时间与超时语义都是现成的；</item>
/// <item>它会把请求数据**原样带回**，因此可以像 TCP/UDP 一样用 <c>序号|探测ID|icmp</c> 校验回包。</item>
/// </list>
///
/// 两点必须说清楚（现场最容易被误解）：
/// ① **ICMP 没有端口**：链路的 Port 对 ICMP 无意义，ping 的是主机；
/// ② **回包由对端操作系统内核产生**，不是我们的程序 —— 所以
///    拿不到"对端看到的本端源地址"，也**不要求对端运行本工具**；
///    反过来说，对端防火墙禁 ping 时会表现为超时（不是线路故障）。
/// </summary>
public static class IcmpPinger
{
    private const int IcmpEchoType = 8;

    // IP_STATUS 里与现场判断相关的几个（见 IPHLPAPI 文档）
    private const uint IpSuccess = 0;
    private const uint IpDestNetUnreachable = 11002;
    private const uint IpDestHostUnreachable = 11003;
    private const uint IpDestProtUnreachable = 11004;
    private const uint IpDestPortUnreachable = 11005;
    private const uint IpReqTimedOut = 11010;
    private const uint IpBadDestination = 11013;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho(
        IntPtr handle, uint destinationAddress, byte[] requestData, ushort requestSize,
        IntPtr requestOptions, byte[] replyBuffer, uint replySize, uint timeout);

    /// <summary>
    /// ping 一次。取消或超时都通过返回值的失败原因表达，不抛异常。
    /// </summary>
    public static Task<IcmpReply> PingAsync(IPAddress address, byte[] payload, int timeoutMs, CancellationToken ct)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return Task.FromResult(IcmpReply.Failed(FailureKinds.Other, "ICMP 探测目前只支持 IPv4（对端解析到 " + address + "）"));

        return Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return IcmpReply.Failed(FailureKinds.Timeout, "已取消");

            var handle = IntPtr.Zero;

            try
            {
                handle = IcmpCreateFile();
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                    return IcmpReply.Failed(FailureKinds.Other, "无法创建 ICMP 句柄（可能被安全软件拦截）");

                // 回复缓冲区：结构头 + 数据 + 余量。32 位与 64 位下 ip_option_information 的
                // 大小不同，这里按 128 字节预留足够空间（官方示例也是这么做的）。
                var replySize = 128 + payload.Length;
                var replyBuffer = new byte[replySize];

                var destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);

                var watch = Stopwatch.StartNew();
                var status = IcmpSendEcho(handle, destination, payload, (ushort)payload.Length,
                    IntPtr.Zero, replyBuffer, (uint)replySize, (uint)Math.Clamp(timeoutMs, 1, 60000));
                watch.Stop();

                if (status == 0)
                {
                    var error = Marshal.GetLastWin32Error();

                    // 常见于"目标不可达"或超时：系统把它作为错误返回而不是状态码
                    return error switch
                    {
                        (int)IpReqTimedOut => IcmpReply.Failed(FailureKinds.Timeout, "无响应（超时）"),
                        (int)IpDestNetUnreachable or (int)IpDestHostUnreachable
                            or (int)IpDestProtUnreachable or (int)IpDestPortUnreachable
                            => IcmpReply.Failed(FailureKinds.IcmpUnreachable, $"目标不可达（ICMP 状态 {error}）"),
                        _ => IcmpReply.Failed(FailureKinds.Other, $"ICMP 失败（Win32 错误 {error}）")
                    };
                }

                // ICMP_ECHO_REPLY：Address(4) Status(4) RoundTripTime(4) DataSize(2) Reserved(2) Data(指针)
                // 用 span 直读而不是 Marshal.ReadInt32(object,int)——后者在 .NET 8 已标记过时（会出警告）
                var replySpan = replyBuffer.AsSpan();
                var replyStatus = BinaryPrimitives.ReadUInt32LittleEndian(replySpan[4..]);
                var rtt = (int)BinaryPrimitives.ReadUInt32LittleEndian(replySpan[8..]);
                var dataSize = BinaryPrimitives.ReadUInt16LittleEndian(replySpan[12..]);

                if (replyStatus != IpSuccess)
                {
                    return replyStatus switch
                    {
                        IpReqTimedOut => IcmpReply.Failed(FailureKinds.Timeout, "无响应（超时）"),
                        IpDestHostUnreachable or IpDestNetUnreachable or IpDestProtUnreachable or IpDestPortUnreachable
                            => IcmpReply.Failed(FailureKinds.IcmpUnreachable, $"目标不可达（ICMP 状态 {replyStatus}）"),
                        IpBadDestination => IcmpReply.Failed(FailureKinds.Unreachable, "目标地址不可用"),
                        _ => IcmpReply.Failed(FailureKinds.Other, $"ICMP 状态 {replyStatus}")
                    };
                }

                // 回包数据必须是原样回显：否则说明应答不是冲着这次探测来的
                var dataPointer = MemoryMarshal.Read<IntPtr>(replySpan[16..]);
                if (dataSize != payload.Length || dataPointer == IntPtr.Zero)
                    return IcmpReply.Failed(FailureKinds.PayloadMismatch, "回包数据长度不符");

                var echoed = new byte[dataSize];
                Marshal.Copy(dataPointer, echoed, 0, dataSize);

                if (!echoed.AsSpan().SequenceEqual(payload))
                    return IcmpReply.Failed(FailureKinds.PayloadMismatch, "回包内容与探测序号/ID 不一致");

                // IcmpSendEcho 的往返时间精度是 1 毫秒（不是微秒），这是系统接口的限制
                return IcmpReply.Ok(rtt >= 0 ? rtt : (int)watch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                return IcmpReply.Failed(FailureKinds.Other, "ICMP 异常：" + ex.Message);
            }
            finally
            {
                if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                {
                    try { IcmpCloseHandle(handle); } catch { }
                }
            }
        }, ct);
    }

    /// <summary>探测报文的 ICMP 版本：末段标记为 <c>icmp</c>（对端内核只会原样带回，不做解释）。</summary>
    public const string PayloadSuffix = "|icmp";

    /// <summary>给自检用的类型常量（避免魔法数字散落）。</summary>
    public const int EchoType = IcmpEchoType;
}
