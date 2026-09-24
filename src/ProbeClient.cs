using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LineTester;

/// <summary>
/// 主动探测客户端。
///
/// 修复要点：
/// 1. 用户主动取消（停止/重启/清除）返回 <c>null</c>，不再伪造成“丢包”；
/// 2. 自行解析地址并优先 IPv4，避免双栈主机解析到 IPv6 而服务端只监听 IPv4 时全量超时；
/// 3. 解析结果带 60 秒缓存，DNS 开销不进入每次探测；
/// 4. RttMs 只统计“发出首字节 → 收齐回包”，建连/解析单独记在 SetupMs；
/// 5. 校验回包内容与探测 ID 一致，丢弃串包/陈旧回包。
/// </summary>
public sealed class ProbeClient
{
    private static readonly TimeSpan DnsTtl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, CacheEntry> _addressCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>“链路|IP|端口|协议” → 下一个探测序号。</summary>
    private readonly ConcurrentDictionary<string, long> _sequence = new(StringComparer.Ordinal);

    /// <summary>探测包末尾的版本标记：请求被动端回显“它看到的本端源地址”。</summary>
    private const string ObservedAddressRequest = "|v2";

    /// <summary>协议名（与界面/日志/统计里的写法一致）。</summary>
    public const string ProtocolTcp = "TCP";
    public const string ProtocolUdp = "UDP";
    public const string ProtocolIcmp = "ICMP";

    /// <summary>支持探测的协议列表（界面、校验、统计共用一份）。</summary>
    public static readonly string[] Protocols = { ProtocolTcp, ProtocolUdp, ProtocolIcmp };

    /// <summary>
    /// 等待回包中可选附加信息的宽限时间。
    /// 老版本被动端只回显原包、不会有这段，宽限到期就按“没有附加信息”处理。
    /// 这段等待发生在 RTT 计时结束之后，不影响时延测量。
    /// </summary>
    public const int TrailerGraceMs = 120;

    private const int LegacyMaxReplyBytes = 512;

    private sealed record CacheEntry(IPAddress Address, DateTime ExpiresUtc);

    /// <summary>
    /// 执行一次探测。返回 null 表示“被调用方取消”，不属于丢包，不得计入统计或日志。
    /// </summary>
    public async Task<ProbeResult?> ProbeAsync(
        LinkConfig link, string protocol, int timeoutMs, CancellationToken ct,
        int streamPacketMinBytes = 0, int streamPacketMaxBytes = 0)
    {
        var timestamp = DateTime.Now;
        var seq = NextSeq(link, protocol);
        var probeId = Guid.NewGuid().ToString("N");

        // 报文格式：序号|探测ID|v2（ICMP 用 |icmp：回包由对端内核产生，不会附源地址）
        // 序号让抓包（Wireshark 等）能直接读出这是第几个探测包，
        // 探测ID 让客户端能确认回包确实属于本次探测，
        // 末尾的 v2 表示“请把你看我的源地址一并回显”（老版本被动端会忽略它、原样回显）。
        // ICMP 是控制报文，不属于远程串流数据路径；它继续用小包，避免影响系统 ping 行为。
        // 直接调用 ProbeClient 的旧代码也保持 v2 小包兼容，只有界面传入范围时才启用串流模拟。
        var useStreamPacket = protocol != ProtocolIcmp && streamPacketMinBytes > 0 && streamPacketMaxBytes > 0;
        var suffix = protocol == ProtocolIcmp ? IcmpPinger.PayloadSuffix : ObservedAddressRequest;
        var payload = useStreamPacket
            ? StreamPacket.Create(seq, probeId, streamPacketMinBytes, streamPacketMaxBytes)
            : Encoding.UTF8.GetBytes($"{seq}|{probeId}{suffix}");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            var token = timeout.Token;

            return protocol switch
            {
                ProtocolTcp => await ProbeTcpAsync(link, protocol, timestamp, seq, probeId, payload, token).ConfigureAwait(false),
                ProtocolIcmp => await ProbeIcmpAsync(link, protocol, timestamp, seq, probeId, payload, timeoutMs, token).ConfigureAwait(false),
                _ => await ProbeUdpAsync(link, protocol, timestamp, seq, probeId, payload, token).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null; // 用户取消
        }
        catch (OperationCanceledException)
        {
            return Lost(link, protocol, timestamp, seq, probeId, FailureKinds.Timeout); // 超时
        }
        catch (ProbeFailureException ex)
        {
            return ct.IsCancellationRequested ? null : Lost(link, protocol, timestamp, seq, probeId, ex.Kind);
        }
        catch (SocketException ex)
        {
            return ct.IsCancellationRequested
                ? null
                : Lost(link, protocol, timestamp, seq, probeId, Classify(ex, protocol == "UDP"));
        }
        catch (IOException ex) when (ex.InnerException is SocketException inner)
        {
            return ct.IsCancellationRequested
                ? null
                : Lost(link, protocol, timestamp, seq, probeId, Classify(inner, protocol == "UDP"));
        }
        catch (Exception)
        {
            // 取消过程中底层可能抛出 ObjectDisposedException 等，同样不能算丢包
            return ct.IsCancellationRequested
                ? null
                : Lost(link, protocol, timestamp, seq, probeId, FailureKinds.Other);
        }
    }

    /// <summary>探测自身判定失败时抛这个，携带稳定的失败原因。</summary>
    private sealed class ProbeFailureException(string kind, string message) : Exception(message)
    {
        public string Kind { get; } = kind;
    }

    /// <summary>
    /// 把套接字错误翻译成现场可用的失败原因。
    /// UDP 的 ConnectionReset 在 Windows 上表示收到了 ICMP 端口不可达，
    /// 与 TCP 的“连接被重置”含义完全不同，必须分开。
    /// </summary>
    private static string Classify(SocketException ex, bool udp) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => FailureKinds.Refused,
        SocketError.TimedOut => FailureKinds.Timeout,
        SocketError.HostNotFound or SocketError.NoData => FailureKinds.HostNotFound,
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => FailureKinds.Unreachable,
        SocketError.ConnectionReset => udp ? FailureKinds.IcmpUnreachable : FailureKinds.Reset,
        SocketError.ConnectionAborted => FailureKinds.Reset,
        _ => FailureKinds.Other
    };

    /// <summary>
    /// 取下一个探测序号：按“链路 + 协议”独立计数，从 1 开始，程序运行期间单调递增。
    /// 不因停止 / 清除 / 重启测试而重置，这样 CSV 里的序号始终唯一，
    /// 界面详单、日志与抓包三者的序号可以直接对齐。
    /// </summary>
    private long NextSeq(LinkConfig link, string protocol)
    {
        var key = $"{link.Name}|{link.Host}|{link.Port}|{protocol}";
        return _sequence.AddOrUpdate(key, 1, static (_, current) => current + 1);
    }

    private async Task<ProbeResult> ProbeTcpAsync(
        LinkConfig link, string protocol, DateTime timestamp, long seq, string probeId,
        byte[] payload, CancellationToken token)
    {
        var setup = Stopwatch.StartNew();
        var address = await ResolveAsync(link.Host, token).ConfigureAwait(false);

        using var tcp = new TcpClient(address.AddressFamily);
        await tcp.ConnectAsync(address, link.Port, token).ConfigureAwait(false);
        var stream = tcp.GetStream();
        setup.Stop();

        var rtt = Stopwatch.StartNew();
        await stream.WriteAsync(payload, token).ConfigureAwait(false);

        var buffer = new byte[payload.Length + 256];
        var read = 0;
        while (read < payload.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), token).ConfigureAwait(false);
            if (n == 0) throw new ProbeFailureException(FailureKinds.ClosedEarly, "对端提前关闭连接");
            read += n;
        }
        rtt.Stop(); // 附加信息不计入 RTT

        if (!buffer.AsSpan(0, payload.Length).SequenceEqual(payload))
            throw new ProbeFailureException(FailureKinds.PayloadMismatch, "回包内容与探测序号/ID 不一致");

        var seenFrom = await ReadOptionalTrailerAsync(stream, buffer, read, payload.Length, token).ConfigureAwait(false);

        return Ok(link, protocol, timestamp, seq, probeId, seenFrom,
            rtt.Elapsed.TotalMilliseconds, setup.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 读取可选的附加信息（对端看到的本端源地址 + 换行）。
    /// 老版本被动端不会有这段，因此只给有限宽限；宽限到期按“无附加信息”处理，绝不算作丢包。
    /// </summary>
    private static async Task<string> ReadOptionalTrailerAsync(
        NetworkStream stream, byte[] buffer, int read, int payloadLength, CancellationToken token)
    {
        if (IsTrailerComplete(buffer, read, payloadLength))
            return ExtractSeenFrom(buffer, payloadLength, read);

        using var grace = CancellationTokenSource.CreateLinkedTokenSource(token);
        grace.CancelAfter(TrailerGraceMs);

        try
        {
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), grace.Token)
                    .ConfigureAwait(false);
                if (n == 0) break;
                read += n;
                if (buffer[read - 1] == (byte)'\n') break;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // 宽限到期（对端是旧版本）：按没有附加信息处理
        }

        return ExtractSeenFrom(buffer, payloadLength, read);
    }

    private static bool IsTrailerComplete(byte[] buffer, int read, int payloadLength)
        => read > payloadLength && buffer[read - 1] == (byte)'\n';

    /// <summary>
    /// 从回包尾部取出“对端看到的本端源地址”。
    /// 只采信以换行结束的完整附加信息，避免半个地址被写进记录。
    /// </summary>
    private static string ExtractSeenFrom(byte[] buffer, int from, int read)
    {
        if (read <= from || buffer[read - 1] != (byte)'\n') return "";

        var text = Encoding.UTF8.GetString(buffer, from, read - from).Trim();
        return text.TrimStart('|').Trim();
    }

    private static bool StartsWith(byte[] buffer, byte[] prefix)
    {
        if (buffer.Length < prefix.Length) return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            if (buffer[i] != prefix[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// ICMP 探测。
    ///
    /// 与 TCP/UDP 的差别（都在统计口径里体现）：
    /// ① **没有端口**：ping 的是主机，链路的 Port 不参与；
    /// ② 回包由**对端内核**产生，因此没有"对端看到的本端源地址"（SeenFrom 为空）；
    /// ③ 往返时间精度是 **1 毫秒**（系统接口限制），不像 TCP/UDP 是微秒级；
    /// ④ 对端禁 ping 时表现为**超时**，失败原因里会额外提示这一点（见 FailureKinds.Describe）。
    /// </summary>
    private async Task<ProbeResult> ProbeIcmpAsync(
        LinkConfig link, string protocol, DateTime timestamp, long seq, string probeId,
        byte[] payload, int timeoutMs, CancellationToken token)
    {
        var setup = Stopwatch.StartNew();
        var address = await ResolveAsync(link.Host, token).ConfigureAwait(false);
        setup.Stop();

        var reply = await IcmpPinger.PingAsync(address, payload, timeoutMs, token).ConfigureAwait(false);

        if (!reply.Success)
            throw new ProbeFailureException(reply.FailureToken, reply.Detail);

        // 对端看到的源地址：ICMP 拿不到（内核不会替我们回显），留空
        return Ok(link, protocol, timestamp, seq, probeId, "", reply.RttMs, setup.Elapsed.TotalMilliseconds);
    }

    private async Task<ProbeResult> ProbeUdpAsync(        LinkConfig link, string protocol, DateTime timestamp, long seq, string probeId,
        byte[] payload, CancellationToken token)
    {
        var setup = Stopwatch.StartNew();
        var address = await ResolveAsync(link.Host, token).ConfigureAwait(false);

        using var udp = new UdpClient(address.AddressFamily);
        udp.Connect(new IPEndPoint(address, link.Port));
        setup.Stop();

        var rtt = Stopwatch.StartNew();
        await udp.SendAsync(payload.AsMemory(), token).ConfigureAwait(false);

        // 前缀一致即认为属于本次探测（老版本被动端只回显原包，也在其中）；
        // 内容不一致的回包（串包/上一轮迟到包）直接丢弃，继续等，直到超时为止。
        string seenFrom;
        while (true)
        {
            var reply = await udp.ReceiveAsync(token).ConfigureAwait(false);
            if (!StartsWith(reply.Buffer, payload)) continue;
            seenFrom = ExtractSeenFrom(reply.Buffer, payload.Length, reply.Buffer.Length);
            break;
        }
        rtt.Stop();

        return Ok(link, protocol, timestamp, seq, probeId, seenFrom,
            rtt.Elapsed.TotalMilliseconds, setup.Elapsed.TotalMilliseconds);
    }

    private async Task<IPAddress> ResolveAsync(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;

        if (_addressCache.TryGetValue(host, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Address;

        var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);

        // 优先 IPv4：被动端即便做了双栈，优先 IPv4 也能避免双栈路径带来的额外变量
        var picked = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? addresses.FirstOrDefault()
                     ?? throw new SocketException((int)SocketError.HostNotFound);

        _addressCache[host] = new CacheEntry(picked, DateTime.UtcNow.Add(DnsTtl));
        return picked;
    }

    private static ProbeResult Ok(
        LinkConfig link, string protocol, DateTime timestamp, long seq, string probeId,
        string seenFrom, double rttMs, double setupMs) => new()
    {
        Timestamp = timestamp,
        LinkName = link.Name,
        Host = link.Host,
        Port = link.Port,
        Protocol = protocol,
        RttMs = rttMs,
        SetupMs = setupMs,
        Seq = seq,
        ProbeId = probeId,
        SeenFrom = seenFrom
    };

    private static ProbeResult Lost(
        LinkConfig link, string protocol, DateTime timestamp, long seq, string probeId, string failure) => new()
    {
        Timestamp = timestamp,
        LinkName = link.Name,
        Host = link.Host,
        Port = link.Port,
        Protocol = protocol,
        Lost = true,
        Seq = seq,
        ProbeId = probeId,
        Failure = failure
    };
}
