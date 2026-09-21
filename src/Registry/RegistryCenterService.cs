using System.Net;
using System.Net.Sockets;

namespace LineTester.Registry;

/// <summary>
/// 注册中心服务：处理 <c>REG</c> / <c>SYNC</c> / <c>ACKP</c>（规格 §5.3 / §5.5 / §5.6）。
///
/// 不持有网络资源——由 <see cref="ProbeServer"/> 在收包处按 <c>LT1|</c> 前缀分流后调用，
/// 返回待回发的帧列表（UDP 逐包发出、TCP 顺序写入）。
///
/// 三条实现要点：
/// ① **先判重（reqId）再做别的**：重传是很正常的事（UDP 丢包），同一请求必须返回同一应答，
///    且**不能重复递增 revision**，否则每个客户端都会被无谓地触发一次全量重同步。
/// ② **限流按类型区分**：分块突发（PART/ACKP）远高于请求频率，共用同一个阈值会把正常传输掐死。
/// ③ **ERR 也要签名**，但客户端对 ERR 不做强制验签——否则"密钥配错"这种最常见的问题
///    反而会因为验签失败被静默丢弃。
/// </summary>
public sealed class RegistryCenterService
{
    /// <summary>请求类（REG/SYNC）每来源地址每秒上限。</summary>
    public const int RequestRateLimit = 20;

    /// <summary>分块类（PART/ACKP）每来源地址每秒上限。</summary>
    public const int ChunkRateLimit = 200;

    private const int MaxTransfers = 32;
    private const int IdempotencyCapacity = 256;
    private const int RateWindowCapacity = 512;

    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RateWindowLifetime = TimeSpan.FromSeconds(10);

    private readonly RegistryStore _store;
    private readonly string _secret;
    private readonly int _heartbeatSeconds;
    private readonly Func<DateTime> _clock;
    private readonly AppLog? _log;

    private readonly NonceCache _nonces = new(256);
    private readonly Dictionary<string, IReadOnlyList<byte[]>> _idempotent = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _idempotentOrder = new();
    private readonly Dictionary<string, OutgoingTransfer> _transfers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RateWindow> _rates = new(StringComparer.Ordinal);

    public RegistryCenterService(
        RegistryStore store, string secret, int heartbeatSeconds, Func<DateTime>? clock = null, AppLog? log = null)
    {
        _store = store;
        _secret = secret ?? "";
        _heartbeatSeconds = heartbeatSeconds > 0 ? heartbeatSeconds : 60;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log;
    }

    /// <summary>已认证（配置了密钥）时清单里标记 authenticated。</summary>
    private bool UsesSecret => _secret.Length > 0;

    public Task<IReadOnlyList<byte[]>> HandleAsync(
        byte[] request, int length, IPEndPoint? remote, CancellationToken cancellationToken)
        => Task.FromResult(Handle(request, length, remote));

    /// <summary>
    /// **非中心节点**的统一应答（规格 §12 兼容矩阵）：明确回 <c>ERR unsupported</c>。
    ///
    /// 为什么不直接静默回显：回显虽然也会被客户端判成"不是注册中心"，但 ERR 把原因写在报文里，
    /// 现场"把中心地址填成了普通节点"一眼可见。用本节点自己的密钥签名
    /// （客户端对 ERR 不强制验签，因此密钥不一致也能看到原因），且被客户端视为**终态不重传**。
    /// 非 <c>LT1|</c> 的帧一律不接管（返回空），探测回显路径完全不变。
    /// </summary>
    public static Task<IReadOnlyList<byte[]>> RespondUnsupportedAsync(
        string secret, byte[] request, int length, CancellationToken cancellationToken)
    {
        if (!RegistryProtocol.TryDecode(request, length, out var type, out _, out var rawPayload))
            return Task.FromResult<IReadOnlyList<byte[]>>(Array.Empty<byte[]>());

        var frame = RegistryProtocol.EncodeSigned(secret, RegistryProtocol.TypeError,
            RegistryProtocol.Serialize(new ErrorMessage
            {
                ReqId = RegistryProtocol.ExtractRequestId(rawPayload),
                Code = RegistryErrorCodes.Unsupported,
                Message = $"本端不是注册中心（不支持 {type}），请检查注册中心地址与端口是否填错"
            }));

        return Task.FromResult<IReadOnlyList<byte[]>>(new[] { frame });
    }

    private IReadOnlyList<byte[]> Handle(byte[] request, int length, IPEndPoint? remote)
    {
        if (!RegistryProtocol.TryDecode(request, length, out var type, out var mac, out var rawPayload))
            return Reply(new ErrorMessage { Code = RegistryErrorCodes.BadRequest, Message = "无法解析协议帧" });

        if (!Allow(remote, type))
        {
            _log?.Write($"限流丢弃：{type} 来自 {remote}（超过每秒上限）");
            return Reply(new ErrorMessage
            {
                Code = RegistryErrorCodes.Throttled,
                Message = "请求过于频繁",
                RetryAfter = 5
            });
        }

        return type switch
        {
            RegistryProtocol.TypeRegister => HandleRegister(mac, rawPayload, remote),
            RegistryProtocol.TypeSync => HandleSync(mac, rawPayload, remote),
            RegistryProtocol.TypePartAck => HandlePartAck(mac, rawPayload),
            _ => BadRequest("", $"本端不是注册中心（不支持 {type}）")
        };
    }

    // ---------------- REG / SYNC ----------------

    private IReadOnlyList<byte[]> HandleRegister(string mac, string rawPayload, IPEndPoint? remote)
    {
        var message = RegistryProtocol.Deserialize<RegisterMessage>(rawPayload);
        if (message is null) return BadRequest("", "REG 载荷不是合法 JSON");

        // 幂等：重传的同一请求直接回缓存，既不重复递增 rev，也不重复消耗限流与 nonce
        if (TryGetCached(message.ReqId, out var cached)) return cached;

        if (!Authorize(RegistryProtocol.TypeRegister, mac, rawPayload, message.NodeId, message.Nonce, message.Ts,
                message.ReqId, out var error))
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(message.NodeId)) return BadRequest(message.ReqId, "缺少 nodeId");

        var addresses = message.AllIps;
        var beforeRevision = _store.Revision;

        var result = _store.Upsert(message, UsesSecret);
        if (!result.Accepted)
        {
            _log?.Write($"REG 被拒 nodeId={Short(message.NodeId)} name={message.Name} 地址=[{string.Join(",", addresses)}] " +
                        $"port={message.Port} tcp={message.Tcp} udp={message.Udp} → {result.ErrorCode}：{result.ErrorMessage}");

            return Reply(new ErrorMessage
            {
                ReqId = message.ReqId,
                Code = result.ErrorCode,
                Message = result.ErrorMessage
            });
        }

        _log?.Write($"REG 成功 nodeId={Short(message.NodeId)} name={message.Name} " +
                    $"地址=[{string.Join(",", addresses)}] port={message.Port} tcp={message.Tcp} udp={message.Udp} " +
                    $"distributable={message.Distributable} rev {beforeRevision}→{_store.Revision} 清单 {_store.Nodes.Count} 个节点" +
                    (message.Distributable && addresses.Count > 1 ? $"（多地址：会给每个地址各生成一条链路）" : ""));

        var replies = BuildAck(message.ReqId, message.Rev, remote);
        CacheReply(message.ReqId, replies);
        return replies;
    }

    private IReadOnlyList<byte[]> HandleSync(string mac, string rawPayload, IPEndPoint? remote)
    {
        var message = RegistryProtocol.Deserialize<SyncMessage>(rawPayload);
        if (message is null) return BadRequest("", "SYNC 载荷不是合法 JSON");

        if (TryGetCached(message.ReqId, out var cached)) return cached;

        if (!Authorize(RegistryProtocol.TypeSync, mac, rawPayload, message.NodeId, message.Nonce, message.Ts,
                message.ReqId, out var error))
        {
            return error;
        }

        var replies = BuildAck(message.ReqId, message.Since, remote);

        _log?.Write($"SYNC nodeId={Short(message.NodeId)} since={message.Since} " +
                    $"中心 rev={_store.Revision} 清单 {_store.Nodes.Count} 个节点 → " +
                    (message.Since == _store.Revision ? "无变化（短路应答）" : "下发完整快照"));

        CacheReply(message.ReqId, replies);
        return replies;
    }

    private static string Short(string nodeId)
        => nodeId.Length <= 8 ? nodeId : nodeId[..8];

    /// <summary>认证链：签名 → 时间窗 → nonce 唯一性。任何一步失败都回 ERR 并说明原因。</summary>
    private bool Authorize(
        string type, string mac, string rawPayload, string nodeId, string nonce, long ts, string reqId,
        out IReadOnlyList<byte[]> error)
    {
        error = Array.Empty<byte[]>();

        if (!RegistryProtocol.VerifyFrame(_secret, type, mac, rawPayload))
        {
            error = Reply(new ErrorMessage
            {
                ReqId = reqId,
                Code = RegistryErrorCodes.Auth,
                Message = "签名校验失败（两端密钥是否一致？）"
            });
            return false;
        }

        if (!RegistryAuth.IsTimestampFresh(ts, _clock()))
        {
            error = Reply(new ErrorMessage
            {
                ReqId = reqId,
                Code = RegistryErrorCodes.Auth,
                Message = "时间戳偏差过大（请检查本机时间）"
            });
            return false;
        }

        if (!_nonces.TryAdd(nodeId, nonce))
        {
            error = Reply(new ErrorMessage
            {
                ReqId = reqId,
                Code = RegistryErrorCodes.Auth,
                Message = "重复的 nonce（疑似重放）"
            });
            return false;
        }

        return true;
    }

    /// <summary>
    /// 组装 ACK。客户端 rev 与中心一致时走"短路应答"（<c>changed=false</c>，稳态只有 1 个包）；
    /// 有变化时才下发完整可分发快照，超过单包上限则分块。
    /// </summary>
    private IReadOnlyList<byte[]> BuildAck(string requestId, ulong clientRevision, IPEndPoint? remote)
    {
        // 判活必须有人做：超 TTL 的节点在这里被标成离线（只标记、不删除）。
        // 每个心跳都会走到这里，因此不会出现"中心进程活着但节点永远显示在线"。
        _store.RefreshOnlineState();

        var revision = _store.Revision;
        var changed = clientRevision != revision;

        var ack = new AckMessage
        {
            ReqId = requestId,
            Rev = revision,
            Changed = changed,
            ReannounceAfter = _heartbeatSeconds
        };

        if (remote is not null)
        {
            var (ip, port) = Normalize(remote);
            ack.SelfCheck = new SelfCheckInfo { SeenIp = ip, SeenPort = port };
        }

        if (changed) ack.Nodes = _store.BuildSnapshot();

        var payload = RegistryProtocol.Serialize(ack);

        if (payload.Length <= RegistryProtocol.SinglePayloadLimit)
        {
            return new[] { RegistryProtocol.EncodeSigned(_secret, RegistryProtocol.TypeAck, payload) };
        }

        return Split(requestId, payload);
    }

    // ---------------- 分块 ----------------

    private IReadOnlyList<byte[]> Split(string requestId, string payload)
    {
        PruneTransfers();

        var chunks = RegistryChunking.Split(payload);
        if (chunks.Count > RegistryChunking.MaxChunks)
        {
            return Reply(new ErrorMessage
            {
                ReqId = requestId,
                Code = RegistryErrorCodes.Internal,
                Message = $"清单过大（需要 {chunks.Count} 块，上限 {RegistryChunking.MaxChunks}）"
            });
        }

        var transfer = new OutgoingTransfer
        {
            XferId = RegistryAuth.NewRequestId(),
            ReqId = requestId,
            Chunks = chunks,
            TouchedUtc = _clock()
        };

        _transfers[transfer.XferId] = transfer;
        while (_transfers.Count > MaxTransfers) RemoveOldestTransfer();

        var frames = new List<byte[]>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++) frames.Add(PartFrame(transfer, i));
        return frames;
    }

    private IReadOnlyList<byte[]> HandlePartAck(string mac, string rawPayload)
    {
        // 位图也要验签：否则伪造的 ACKP 能让中心把（本就是成员清单的）分块发到任意地址
        if (!RegistryProtocol.VerifyFrame(_secret, RegistryProtocol.TypePartAck, mac, rawPayload))
            return Array.Empty<byte[]>();

        var message = RegistryProtocol.Deserialize<PartAckMessage>(rawPayload);
        if (message is null) return Array.Empty<byte[]>();

        if (!_transfers.TryGetValue(message.XferId, out var transfer))
        {
            // 传输上下文已过期：客户端收不到补齐块，会在下一个同步周期重新发起请求
            return Array.Empty<byte[]>();
        }

        var missing = RegistryChunking.MissingChunks(message.Bitmap, transfer.Chunks.Count);
        if (missing.Count == 0)
        {
            _transfers.Remove(message.XferId); // 传完了，释放上下文
            return Array.Empty<byte[]>();
        }

        transfer.TouchedUtc = _clock();

        var frames = new List<byte[]>(missing.Count);
        foreach (var index in missing) frames.Add(PartFrame(transfer, index));
        return frames;
    }

    private byte[] PartFrame(OutgoingTransfer transfer, int index)
        => RegistryProtocol.EncodeSigned(_secret, RegistryProtocol.TypePart, RegistryProtocol.Serialize(new PartMessage
        {
            XferId = transfer.XferId,
            ReqId = transfer.ReqId,
            I = index,
            N = transfer.Chunks.Count,
            Data = transfer.Chunks[index]
        }));

    private void PruneTransfers()
    {
        var now = _clock();
        foreach (var xferId in _transfers.Where(p => now - p.Value.TouchedUtc > TransferTimeout)
                     .Select(p => p.Key).ToList())
        {
            _transfers.Remove(xferId);
        }
    }

    private void RemoveOldestTransfer()
    {
        var oldest = _transfers.Values.OrderBy(t => t.TouchedUtc).FirstOrDefault();
        if (oldest is not null) _transfers.Remove(oldest.XferId);
    }

    // ---------------- 幂等缓存 ----------------

    private bool TryGetCached(string requestId, out IReadOnlyList<byte[]> replies)
    {
        if (requestId.Length > 0 && _idempotent.TryGetValue(requestId, out var cached))
        {
            replies = cached;
            return true;
        }

        replies = Array.Empty<byte[]>();
        return false;
    }

    private void CacheReply(string requestId, IReadOnlyList<byte[]> replies)
    {
        if (requestId.Length == 0) return;

        _idempotent[requestId] = replies;
        _idempotentOrder.AddLast(requestId);

        while (_idempotentOrder.Count > IdempotencyCapacity)
        {
            var oldest = _idempotentOrder.First!.Value;
            _idempotentOrder.RemoveFirst();
            _idempotent.Remove(oldest);
        }
    }

    // ---------------- 限流 ----------------

    private bool Allow(IPEndPoint? remote, string type)
    {
        var key = remote?.Address.ToString() ?? "unknown";
        var limit = type is RegistryProtocol.TypePart or RegistryProtocol.TypePartAck
            ? ChunkRateLimit
            : RequestRateLimit;

        var now = _clock();

        if (!_rates.TryGetValue(key, out var window) || now - window.Start >= TimeSpan.FromSeconds(1))
        {
            _rates[key] = new RateWindow { Start = now, Count = 1 };
            PruneRates(now);
            return true;
        }

        window.Count++;
        return window.Count <= limit;
    }

    private void PruneRates(DateTime now)
    {
        if (_rates.Count <= RateWindowCapacity) return;

        foreach (var key in _rates.Where(p => now - p.Value.Start > RateWindowLifetime).Select(p => p.Key).ToList())
        {
            _rates.Remove(key);
        }
    }

    // ---------------- 工具 ----------------

    private IReadOnlyList<byte[]> Reply(ErrorMessage error)
        => new[] { RegistryProtocol.EncodeSigned(_secret, RegistryProtocol.TypeError, RegistryProtocol.Serialize(error)) };

    private IReadOnlyList<byte[]> BadRequest(string requestId, string message)
        => Reply(new ErrorMessage
        {
            ReqId = requestId,
            Code = RegistryErrorCodes.BadRequest,
            Message = message
        });

    /// <summary>把对端源地址规范化成"公网/内网都是人能读的 IP"（去掉 IPv4-mapped 与 scope）。</summary>
    private static (string Ip, int Port) Normalize(IPEndPoint remote)
    {
        var address = remote.Address;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            address = new IPAddress(address.GetAddressBytes());

        return (address.ToString(), remote.Port);
    }

    private sealed class OutgoingTransfer
    {
        public string XferId { get; init; } = "";
        public string ReqId { get; init; } = "";
        public List<string> Chunks { get; init; } = new();
        public DateTime TouchedUtc { get; set; }
    }

    private sealed class RateWindow
    {
        public DateTime Start { get; init; }
        public int Count { get; set; }
    }
}
