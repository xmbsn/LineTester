using System.Net;
using System.Net.Sockets;
using System.Text;
using LineTester.Registry;

namespace LineTester;

/// <summary>
/// 被动应答端：收到探测包立即原样回发。
///
/// 绑定优先使用双栈（IPv6Any + DualMode），这样 IPv4 与 IPv6 的探测都能应答；
/// 系统不支持 IPv6 时自动退回仅 IPv4。
/// </summary>
public sealed class ProbeServer : IDisposable
{
    private readonly int _port;
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public ProbeServer(int port) => _port = port;

    public bool DualStack { get; private set; }

    /// <summary>
    /// 注册协议消息处理器（规格 docs/REGISTRY-PROTOCOL.md §5.1）。
    ///
    /// 仅在报文以 <c>LT1|</c> 开头且已设置处理器时调用，因此**既有探测回显路径一行都不受影响**。
    /// 返回待回发的帧；空列表表示"已处理但无需回包"。未设置处理器时按老版本行为**原样回显**，
    /// 让发起方据此判定"对端不支持协议"（规格 §12 兼容矩阵的一行）。
    /// </summary>
    public Func<byte[], int, IPEndPoint?, CancellationToken, Task<IReadOnlyList<byte[]>>>? ProtocolHandler { get; set; }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        try
        {
            _tcp = CreateTcpListener(_port, out var tcpDual);
            _ = AcceptTcpLoop(_cts.Token);

            _udp = CreateUdpClient(_port, out var udpDual);
            _ = UdpLoop(_cts.Token);

            DualStack = tcpDual && udpDual;
        }
        catch (SocketException ex)
        {
            Dispose();
            throw new InvalidOperationException(
                $"无法监听端口 {_port}（{ex.SocketErrorCode}）。请确认端口未被占用、未被防火墙拦截，" +
                "且当前账户有绑定权限。", ex);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static TcpListener CreateTcpListener(int port, out bool dualStack)
    {
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Any, port);

            // DualMode 必须真的生效才算双栈：否则会变成“只听 IPv6”，
            // 而外部一般用 IPv4 访问，表现为完全收不到包。
            listener.Server.DualMode = true;
            if (!listener.Server.DualMode) throw new NotSupportedException("DualMode 未生效");

            listener.Start();
            dualStack = true;
            return listener;
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException or InvalidOperationException)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            dualStack = false;
            return listener;
        }
    }

    private static UdpClient CreateUdpClient(int port, out bool dualStack)
    {
        try
        {
            var client = new UdpClient(AddressFamily.InterNetworkV6);

            client.Client.DualMode = true;
            if (!client.Client.DualMode) throw new NotSupportedException("DualMode 未生效");

            client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            dualStack = true;
            return client;
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException or InvalidOperationException)
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            dualStack = false;
            return client;
        }
    }

    private async Task AcceptTcpLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _tcp!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleTcp(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { if (!ct.IsCancellationRequested) await SafeDelay(ct).ConfigureAwait(false); }
        }
    }

    private async Task HandleTcp(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            using var stream = client.GetStream();
            var buffer = new byte[4096];
            try
            {
                var n = await ReadFrameAsync(stream, buffer, ct).ConfigureAwait(false);
                if (n <= 0) return;

                var remote = client.Client.RemoteEndPoint as IPEndPoint;

                if (RegistryProtocol.LooksLikeProtocolMessage(buffer, n) && ProtocolHandler is { } protocol)
                {
                    var replies = await protocol(buffer, n, remote, ct).ConfigureAwait(false);
                    foreach (var reply in replies)
                    {
                        await stream.WriteAsync(reply, ct).ConfigureAwait(false);
                    }

                    return;
                }

                await stream.WriteAsync(BuildReply(buffer, n, remote), ct).ConfigureAwait(false);
            }
            catch { }
        }
    }

    /// <summary>
    /// 读一帧。探测包读一次即可（与既有行为一致）；协议帧以 <c>\n</c> 结束，需要读到结束符。
    /// </summary>
    private static async Task<int> ReadFrameAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;

            read += n;

            // 不是协议帧（探测包）→ 读一次就走，保持既有语义不变
            if (!RegistryProtocol.LooksLikeProtocolMessage(buffer, read)) break;
            if (buffer[read - 1] == RegistryProtocol.FrameTerminator) break;
        }

        return read;
    }

    private async Task UdpLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);

                if (RegistryProtocol.LooksLikeProtocolMessage(result.Buffer, result.Buffer.Length) &&
                    ProtocolHandler is { } protocol)
                {
                    var replies = await protocol(result.Buffer, result.Buffer.Length, result.RemoteEndPoint, ct)
                        .ConfigureAwait(false);

                    foreach (var frame in replies)
                    {
                        await _udp.SendAsync(frame.AsMemory(), result.RemoteEndPoint, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                var reply = BuildReply(result.Buffer, result.Buffer.Length, result.RemoteEndPoint);
                await _udp.SendAsync(reply.AsMemory(), result.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { if (!ct.IsCancellationRequested) await SafeDelay(ct).ConfigureAwait(false); }
        }
    }

    private static async Task SafeDelay(CancellationToken ct)
    {
        try { await Task.Delay(100, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    // ---------------- 回包组装 ----------------

    /// <summary>
    /// 客户端在探测包末尾带的标记：请把“你看到的我的源地址”一并回显。
    /// <b>老版本客户端不带这个标记</b>，此时被动端必须严格原样回显
    /// （老客户端是逐字节比对回包的，多一个字节都会被判为丢包）。
    /// </summary>
    private const string ObservedAddressRequest = "|v2";

    /// <summary>回包里的附加信息以换行结束，便于主动端确定边界。</summary>
    private const byte ReplyTerminator = (byte)'\n';

    /// <summary>
    /// 组装回包：默认原样回显；仅当请求带 v2 标记时，
    /// 追加“对端观察到的源地址”——跨公网时它就是对方的出口公网 IP 与 NAT 后的源端口。
    /// </summary>
    private static byte[] BuildReply(byte[] request, int length, IPEndPoint? remote)
    {
        if (remote is null || !RequestsObservedAddress(request, length))
            return length == request.Length ? request : request.AsSpan(0, length).ToArray();

        var payload = Encoding.UTF8.GetString(request, 0, length);
        var reply = payload + "|" + FormatEndpoint(remote) + (char)ReplyTerminator;
        return Encoding.UTF8.GetBytes(reply);
    }

    private static bool RequestsObservedAddress(byte[] request, int length)
    {
        if (length < ObservedAddressRequest.Length) return false;

        for (var i = 0; i < ObservedAddressRequest.Length; i++)
        {
            if (request[length - ObservedAddressRequest.Length + i] != (byte)ObservedAddressRequest[i])
                return false;
        }

        return true;
    }

    /// <summary>
    /// 把端点格式化成 ip:port。
    /// 双栈监听下 IPv4 会以 IPv4-mapped IPv6 形式出现，这里还原成普通 IPv4；
    /// IPv6 用方括号包起来，避免和端口分隔符混淆。
    /// </summary>
    public static string FormatEndpoint(IPEndPoint endpoint)
    {
        var address = endpoint.Address;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            address = new IPAddress(address.GetAddressBytes());

        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]:{endpoint.Port}"
            : $"{address}:{endpoint.Port}";
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _tcp?.Stop(); } catch { }
        try { _udp?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _tcp = null;
        _udp = null;
        _cts = null;
    }
}
