using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LineTester.Registry;

/// <summary>客户端同步循环所需的本节点与中心信息（由界面从 <see cref="RegistrarConfig"/> 投影而来）。</summary>
public sealed class RegistryClientOptions
{
    public string NodeId { get; set; } = "";
    public string NodeName { get; set; } = "";
    public string PublicHost { get; set; } = "";
    public int PublicPort { get; set; }

    /// <summary>对外宣告"可被探测的协议"（对应 AdvertiseTcp/AdvertiseUdp）。</summary>
    public bool Tcp { get; set; } = true;

    public bool Udp { get; set; } = true;
    public bool Distributable { get; set; } = true;

    public string CenterHost { get; set; } = "";
    public int CenterPort { get; set; }
    public string Secret { get; set; } = "";

    public int HeartbeatSeconds { get; set; } = 60;
    public int SyncSeconds { get; set; } = 300;
    public int RetryMs { get; set; } = 1000;
    public int MaxRetries { get; set; } = 5;

    /// <summary>协议日志目录（实际写入其下的按天子目录）。为空则不写日志。</summary>
    public string LogDirectory { get; set; } = "";
}

/// <summary>客户端同步状态（供界面显示）。</summary>
public sealed class RegistryClientStatus
{
    public bool Running { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public ulong Revision { get; set; }
    public int NodeCount { get; set; }

    /// <summary>中心看到的本端源地址（与手填的 PublicHost 交叉核对用）。</summary>
    public string? SeenFrom { get; set; }

    public string? LastError { get; set; }
    public string? LastLogLine { get; set; }
}

/// <summary>
/// 注册同步客户端（规格 §8）。
///
/// 关键实现决定：
/// ① **只用一个长驻 UDP socket**（不是每请求一个）。分块传输、位图确认、中心补发都依赖
///    "同一请求的后续帧能被送回同一个源地址"；每请求换 socket 会让源端口每跳一次就变。
/// ② socket 直接 <c>Connect</c> 到中心端点，因此只会收到中心的回包（也顺带过滤了伪造来源）。
/// ③ **本地不持久化 revision**：进程启动时从 0 开始，中心会发现"rev 不同"并回全量快照，
///    天然自愈（也覆盖中心清单被重置/回退的情况）。
/// ④ 认证失败的 ERR 也**照常解析**（见规格 §5.4），否则"密钥配错"反而看不到提示。
/// </summary>
public sealed class RegistryClient : IDisposable
{
    private readonly RegistryClientOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly object _logLock = new();

    /// <summary>
    /// 请求闸门：本类**只用一个 socket**，因此同一时刻只能有一个请求在等应答。
    /// 否则（心跳循环与界面"立即注册"并发）两个 receive 会随机各自抢到对方的回包并因 reqId 不符丢弃，
    /// 表现为间歇性"中心无应答"——实测踩过。
    /// </summary>
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    private UdpClient? _socket;
    private IPEndPoint? _center;
    private Task? _loop;
    private int _backoffMs;
    private bool _lastErrorFinal;
    private DateTime _nextHeartbeatUtc;
    private DateTime _nextSyncUtc;

    public RegistryClient(RegistryClientOptions options)
    {
        _options = options;
        _backoffMs = Math.Max(200, options.RetryMs);
    }

    /// <summary>收到中心快照（<c>changed=true</c> 时触发）。订阅方负责套用到本地链路并保存配置。</summary>
    public event Action<IReadOnlyList<NodeSnapshot>>? SnapshotReceived;

    public event Action<RegistryClientStatus>? StatusChanged;

    public RegistryClientStatus Status { get; } = new();

    public string InstanceId => _instanceId;

    /// <summary>解析中心地址并启动循环。返回是否成功启动。</summary>
    public bool Start()
    {
        if (_loop is not null) return true;

        if (!TryResolveCenter(out var center, out var error))
        {
            SetError(error);
            return false;
        }

        _center = center;
        _socket = CreateSocket(center.AddressFamily);

        var now = DateTime.UtcNow;
        _nextHeartbeatUtc = now; // 启动即注册，不等一个周期
        _nextSyncUtc = now;

        Status.Running = true;
        Status.LastError = null;
        Publish();
        Log($"启动同步：本端 {_options.PublicHost}:{_options.PublicPort} → 中心 {center}");

        _loop = Task.Run(() => LoopAsync(_cts.Token));
        return true;
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _socket?.Dispose(); } catch { }
        _socket = null;
        Status.Running = false;
        Publish();
    }

    /// <summary>立即注册一次（界面"立即注册"按钮）。任何失败都只反映在状态里，不抛异常。</summary>
    public async Task<bool> RegisterNowAsync(CancellationToken ct = default)
    {
        try
        {
            return await SendRegisterAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            SetError($"注册失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>立即拉取一次（与注册等价，中心按 rev 决定是否下发快照）。</summary>
    public Task<bool> SyncNowAsync(CancellationToken ct = default) => RegisterNowAsync(ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;

                if (now >= _nextHeartbeatUtc || now >= _nextSyncUtc)
                {
                    var ok = await SendRegisterAsync(ct).ConfigureAwait(false);

                    if (ok)
                    {
                        _backoffMs = Math.Max(200, _options.RetryMs);
                        _nextHeartbeatUtc = DateTime.UtcNow + TimeSpan.FromSeconds(_effectiveHeartbeatSeconds);
                        _nextSyncUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(1, _options.SyncSeconds));
                    }
                    else
                    {
                        // 指数退避，封顶 30× 基础间隔，避免中心不可达时刷屏
                        _nextHeartbeatUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(_backoffMs);
                        _nextSyncUtc = _nextHeartbeatUtc;
                        _backoffMs = Math.Min(_backoffMs * 2, 30 * Math.Max(200, _options.RetryMs));
                    }
                }

                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 循环任务绝不允许因异常而终止（否则同步会静默停掉）
                SetError($"同步循环异常：{ex.Message}");
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private int _effectiveHeartbeatSeconds => Math.Max(RegistrarConfig.MinIntervalSeconds, _options.HeartbeatSeconds);

    /// <summary>一次注册：请求 →（必要时重传）→ 解析 ACK → 应用快照。用闸门保证同一时刻只有一个请求在飞。</summary>
    private async Task<bool> SendRegisterAsync(CancellationToken ct)
    {
        await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SendRegisterCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            try { _requestGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task<bool> SendRegisterCoreAsync(CancellationToken ct)
    {
        var socket = _socket;
        var center = _center;
        if (socket is null || center is null) return false;

        Status.LastAttemptUtc = DateTime.UtcNow;
        _lastErrorFinal = false;

        var requestId = RegistryAuth.NewRequestId();
        var rawPayload = RegistryProtocol.Serialize(new RegisterMessage
        {
            ReqId = requestId,
            NodeId = _options.NodeId,
            Name = _options.NodeName,
            Ip = _options.PublicHost,
            Port = _options.PublicPort,
            Tcp = _options.Tcp,
            Udp = _options.Udp,
            Distributable = _options.Distributable,
            Instance = _instanceId,
            Rev = Status.Revision,
            Ts = RegistryAuth.NowUnixSeconds(),
            Nonce = RegistryAuth.NewNonce()
        });

        var frame = RegistryProtocol.EncodeSigned(_options.Secret, RegistryProtocol.TypeRegister, rawPayload);

        for (var attempt = 0; attempt <= Math.Max(0, _options.MaxRetries); attempt++)
        {
            if (ct.IsCancellationRequested) return false;

            try
            {
                await socket.SendAsync(frame, frame.Length, center).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetError($"发送失败：{ex.Message}");
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(300, _options.RetryMs));
            var ack = await AwaitAckAsync(requestId, rawPayload, deadline, ct).ConfigureAwait(false);

            if (ack is not null)
            {
                ApplyAck(ack);
                Status.LastSuccessUtc = DateTime.UtcNow;
                Status.LastError = null;
                Publish();
                return true;
            }

            if (Status.LastError is not null && attempt < _options.MaxRetries)
            {
                Log($"注册失败（第 {attempt + 1} 次）：{Status.LastError}");
            }

            // 确定性错误（密钥不符 / 参数非法 / 已被中心移除 / 对端不是中心）重试无意义，
            // 立刻退出让界面提示；超时类错误才继续重传。
            if (_lastErrorFinal) break;
        }

        SetError(Status.LastError ?? "中心无应答（超时）");
        Log($"注册失败：{Status.LastError}");
        return false;
    }

    /// <summary>
    /// 等待本次请求的应答。会一直读到"完整的 ACK"为止：
    /// 单帧 ACK 直接返回；分块 ACK 先攒齐所有块并回位图，再拼装解析。
    /// </summary>
    private async Task<AckMessage?> AwaitAckAsync(
        string requestId, string requestPayload, DateTime deadline, CancellationToken ct)
    {
        var socket = _socket;
        var center = _center;
        if (socket is null || center is null) return null;

        string transferId = "";
        var chunks = new List<string?>();

        while (DateTime.UtcNow < deadline)
        {
            UdpReceiveResult datagram;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return null;

                timeout.CancelAfter(remaining);
                try
                {
                    datagram = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (ObjectDisposedException)
                {
                    return null; // 停止/释放与接收竞争
                }
                catch (SocketException ex)
                {
                    // 回环上对端未监听等会产生 ICMP 错误，跳过本次继续等
                    SetError($"接收失败：{ex.SocketErrorCode}");
                    return null;
                }
            }

            if (!RegistryProtocol.TryDecode(datagram.Buffer, datagram.Buffer.Length,
                    out var type, out var mac, out var payload))
            {
                continue;
            }

            // 对端把我们的请求原样回显了 → 该地址不是注册中心（旧版本服务端是纯回显）。
            // 明确报出来，比"等到超时"更容易现场定位（规格 §12 兼容矩阵）。
            if (payload == requestPayload)
            {
                SetError("对端原样回显了请求 → 该地址不是注册中心（可能是未启用注册的节点或旧版本）");
                return null;
            }

            // ERR 不做强制验签：密钥配错时报文必然验不过，但正是此时最需要把原因显示出来
            if (type != RegistryProtocol.TypeError &&
                !RegistryProtocol.VerifyFrame(_options.Secret, type, mac, payload))
            {
                Log($"丢弃未通过验签的 {type} 帧");
                continue;
            }

            switch (type)
            {
                case RegistryProtocol.TypeAck:
                {
                    var ack = RegistryProtocol.Deserialize<AckMessage>(payload);
                    if (ack is null || (ack.ReqId.Length > 0 && ack.ReqId != requestId)) continue;
                    return ack;
                }

                case RegistryProtocol.TypePart:
                {
                    var part = RegistryProtocol.Deserialize<PartMessage>(payload);
                    if (part is null || (part.ReqId.Length > 0 && part.ReqId != requestId)) continue;

                    if (transferId.Length == 0)
                    {
                        transferId = part.XferId;
                        chunks.AddRange(new string?[Math.Max(0, part.N)]);
                    }

                    if (part.XferId != transferId || part.I < 0 || part.I >= chunks.Count) continue;
                    chunks[part.I] = part.Data;

                    await SendPartAckAsync(socket, center, transferId, chunks).ConfigureAwait(false);

                    if (RegistryChunking.TryJoin(chunks, out var assembled))
                    {
                        var full = RegistryProtocol.Deserialize<AckMessage>(assembled);
                        if (full is not null) return full;
                    }

                    break;
                }

                case RegistryProtocol.TypeError:
                {
                    var error = RegistryProtocol.Deserialize<ErrorMessage>(payload);
                    if (error is null) continue;
                    if (error.ReqId.Length > 0 && error.ReqId != requestId) continue;

                    _lastErrorFinal = error.Code is RegistryErrorCodes.Auth
                        or RegistryErrorCodes.BadRequest
                        or RegistryErrorCodes.Removed
                        or RegistryErrorCodes.Unsupported;

                    SetError($"{error.Code}：{error.Message}");
                    return null;
                }
            }
        }

        return null;
    }

    private async Task SendPartAckAsync(UdpClient socket, IPEndPoint center, string transferId, List<string?> chunks)
    {
        var bitmap = RegistryChunking.EmptyBitmap(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i] is not null) bitmap = RegistryChunking.SetBit(bitmap, i);
        }

        var frame = RegistryProtocol.EncodeSigned(_options.Secret, RegistryProtocol.TypePartAck,
            RegistryProtocol.Serialize(new PartAckMessage { XferId = transferId, Bitmap = bitmap }));

        try
        {
            await socket.SendAsync(frame, frame.Length, center).ConfigureAwait(false);
        }
        catch
        {
            // 位图丢失只是让中心不补发；下一个同步周期会重新请求
        }
    }

    private void ApplyAck(AckMessage ack)
    {
        Status.Revision = ack.Rev;
        Status.NodeCount = ack.Nodes?.Count ?? Status.NodeCount;

        if (ack.SelfCheck is not null)
        {
            Status.SeenFrom = $"{ack.SelfCheck.SeenIp}:{ack.SelfCheck.SeenPort}";
        }

        if (!ack.Changed) return;

        var nodes = ack.Nodes ?? new List<NodeSnapshot>();
        Status.NodeCount = nodes.Count;
        Log($"收到清单快照：rev={ack.Rev}，节点 {nodes.Count} 个");

        try
        {
            SnapshotReceived?.Invoke(nodes);
        }
        catch (Exception ex)
        {
            SetError($"应用清单失败：{ex.Message}");
        }
    }

    private bool TryResolveCenter(out IPEndPoint center, out string error)
    {
        center = new IPEndPoint(IPAddress.Loopback, 0);
        error = "";

        var host = _options.CenterHost?.Trim() ?? "";
        if (host.Length == 0 || _options.CenterPort < 1 || _options.CenterPort > 65535)
        {
            error = "注册中心地址或端口未配置";
            return false;
        }

        try
        {
            IPAddress? address = null;
            if (IPAddress.TryParse(host, out var literal))
            {
                address = literal;
            }
            else
            {
                var addresses = Dns.GetHostAddresses(host);
                address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault();
            }

            if (address is null)
            {
                error = $"无法解析注册中心地址：{host}";
                return false;
            }

            center = new IPEndPoint(address, _options.CenterPort);
            return true;
        }
        catch (Exception ex)
        {
            error = $"无法解析注册中心地址：{ex.Message}";
            return false;
        }
    }

    /// <summary>与被动端一致：优先双栈（IPv4/IPv6 目标都能发），失败时按中心地址族退回。</summary>
    private static UdpClient CreateSocket(AddressFamily centerFamily)
    {
        try
        {
            var client = new UdpClient(AddressFamily.InterNetworkV6);
            client.Client.DualMode = true;
            if (!client.Client.DualMode) throw new NotSupportedException("DualMode 未生效");
            client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            return client;
        }
        catch
        {
            return centerFamily == AddressFamily.InterNetworkV6
                ? new UdpClient(new IPEndPoint(IPAddress.IPv6Any, 0))
                : new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        }
    }

    private void SetError(string message)
    {
        Status.LastError = message;
        Publish();
    }

    private void Publish()
    {
        try
        {
            StatusChanged?.Invoke(Status);
        }
        catch
        {
            // 界面回调异常不应影响同步循环
        }
    }

    /// <summary>协议日志：写入按天目录，且**绝不记录密钥或 mac**。</summary>
    private void Log(string message)
    {
        Status.LastLogLine = message;

        if (_options.LogDirectory.Length == 0) return;

        try
        {
            lock (_logLock)
            {
                var directory = Path.Combine(_options.LogDirectory, CsvLogWriter.FolderNameFor(DateTime.Now));
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "registry.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志写不进去不影响同步
        }
    }

    public void Dispose()
    {
        Stop();
        try { _cts.Dispose(); } catch { }
        try { _requestGate.Dispose(); } catch { }
    }
}
