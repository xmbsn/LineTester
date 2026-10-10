using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LineTester.Registry;

/// <summary>中心清单里的一个节点。</summary>
public sealed class RegistryNode
{
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>主外网地址。</summary>
    public string Ip { get; set; } = "";

    /// <summary>该节点的全部外网地址（含主地址；同一机房多个出口 IP 时会有多条）。</summary>
    public List<string> Ips { get; set; } = new();

    /// <summary>完整外网端点；空时兼容旧 registry.json 的 Ips + Port。</summary>
    public List<ExternalEndpoint> Endpoints { get; set; } = new();
    public int Port { get; set; }
    public bool Tcp { get; set; }
    public bool Udp { get; set; }

    /// <summary>节点自报：是否允许被 ICMP(ping) 探测。</summary>
    public bool Icmp { get; set; }
    /// <summary>节点自报：是否允许被分发（对端心跳每次都会重报）。</summary>
    public bool Distributable { get; set; } = true;

    /// <summary>节点自报：本机操作员已启动测试，愿意在全局会话开启后参与探测。</summary>
    public bool TestReady { get; set; } = true;

    /// <summary>中心侧覆盖：勾掉后该节点对所有人隐身。写在中心侧，**不改对端配置**，
    /// 否则会被对端下一次心跳顶回来。</summary>
    public bool CenterHidden { get; set; }

    public string InstanceId { get; set; } = "";

    /// <summary>上一个进程标识，用于识别"同一 nodeId 被两台机器同时使用"。</summary>
    public string PriorInstanceId { get; set; } = "";

    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool Online { get; set; } = true;
    public bool Authenticated { get; set; }

    /// <summary>疑似 nodeId 重复（配置被复制到多台机器）。仅告警，不阻断同步。</summary>
    [JsonIgnore]
    public bool DuplicateSuspected { get; set; }

    /// <summary>节点的分发策略开关是否允许展示（不含运行/就绪状态）。</summary>
    [JsonIgnore]
    public bool IsDistributed => Distributable && !CenterHidden;

    /// <summary>是否真正进入探测目标快照：在线、允许分发、中心未隐藏且本地已就绪。</summary>
    [JsonIgnore]
    public bool IsEligibleProbeTarget => Online && IsDistributed && TestReady;

    [JsonIgnore]
    public List<ExternalEndpoint> AllEndpoints
    {
        get
        {
            var source = Endpoints is { Count: > 0 }
                ? Endpoints
                : (Ips is { Count: > 0 } ? Ips : new List<string> { Ip })
                    .Select(ip => new ExternalEndpoint { Ip = ip, Port = Port }).ToList();
            var result = new List<ExternalEndpoint>();
            foreach (var endpoint in source)
            {
                var ip = endpoint.Ip?.Trim() ?? "";
                if (ip.Length == 0 || endpoint.Port is < 1 or > 65535) continue;
                if (!result.Any(existing => existing.Port == endpoint.Port &&
                    string.Equals(existing.Ip, ip, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new ExternalEndpoint { Ip = ip, Port = endpoint.Port });
            }
            return result;
        }
    }
}

public sealed class RegistryState
{
    public ulong Revision { get; set; }

    /// <summary>只有注册中心可切换的全局测试会话状态。</summary>
    public bool GlobalTestRunning { get; set; }
    public List<RegistryNode> Nodes { get; set; } = new();

    /// <summary>
    /// 已在中心被移除的 nodeId（持久化）。
    /// 必须有这个名单：被删除的节点**还在运行、还会继续心跳**，否则下一次心跳就把它加回来了，
    /// "在中心删除"就变成了一次性动作而不是权威决定。
    /// 需要恢复时由中心显式恢复（见 <see cref="RegistryStore.RestoreNode"/>）。
    /// </summary>
    public List<string> RemovedNodeIds { get; set; } = new();
}

public sealed record RegistryUpsertResult(bool Accepted, string ErrorCode, string ErrorMessage, bool Changed)
{
    public static RegistryUpsertResult Ok(bool changed) => new(true, "", "", changed);
    public static RegistryUpsertResult Rejected(string code, string message) => new(false, code, message, false);
}

/// <summary>
/// 中心清单与 revision（规格 §5.6 / §7）。
///
/// revision 规则：**只有影响控制状态或目标快照的状态真的变化时才 +1**——纯心跳只刷新 lastSeen，不涨 rev，
/// 否则每个客户端每个心跳都要拉一次快照，白白放大流量。
/// 快照语义下不需要墓碑：客户端每次拿到完整清单并整体替换，离线过的节点也不会复活已删链路。
/// </summary>
public sealed class RegistryStore
{
    /// <summary>超过该倍数的心跳周期未见即判离线（只标记，不删除）。</summary>
    public const int OfflineTtlHeartbeats = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 与主配置一致：中文节点名不转义，现场可直接看
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly int _heartbeatSeconds;
    private readonly Func<DateTime> _clock;
    private RegistryState _state = new();

    public RegistryStore(string path, int heartbeatSeconds, Func<DateTime>? clock = null)
    {
        _path = path;
        _heartbeatSeconds = heartbeatSeconds > 0 ? heartbeatSeconds : 60;
        _clock = clock ?? (() => DateTime.UtcNow);
        Load();
    }

    public string Path => _path;

    public ulong Revision => _state.Revision;

    public bool GlobalTestRunning => _state.GlobalTestRunning;

    public IReadOnlyList<RegistryNode> Nodes => _state.Nodes;

    /// <summary>已被中心移除、且在其重新注册时会被拒绝的 nodeId 列表。</summary>
    public IReadOnlyList<string> RemovedNodeIds => _state.RemovedNodeIds;

    public TimeSpan OfflineTtl => TimeSpan.FromSeconds(_heartbeatSeconds * OfflineTtlHeartbeats);

    /// <summary>载入时若文件损坏，从空清单开始（节点会在一个心跳内重新注册），不阻塞中心启动。</summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _state = new RegistryState();
                return;
            }

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<RegistryState>(json, JsonOptions);

            _state = loaded ?? new RegistryState();
            _state.Nodes ??= new List<RegistryNode>();
            _state.RemovedNodeIds ??= new List<string>();

            // online 由 lastSeen 推导，不接受持久化的陈旧值
            var now = _clock();
            foreach (var node in _state.Nodes) node.Online = IsRecent(node.LastSeenUtc, now);
        }
        catch
        {
            _state = new RegistryState();
        }
    }

    public bool IsRecent(DateTime lastSeenUtc, DateTime nowUtc) => nowUtc - lastSeenUtc <= OfflineTtl;

    /// <summary>注册/心跳（幂等 upsert）。地址不合规或被中心移除时拒绝并带回错误码。</summary>
    public RegistryUpsertResult Upsert(RegisterMessage message, bool authenticated)
    {
        if (_state.RemovedNodeIds.Contains(message.NodeId, StringComparer.Ordinal))
        {
            return RegistryUpsertResult.Rejected(RegistryErrorCodes.Removed,
                "本节点已被注册中心移除，如需恢复请在注册中心侧恢复");
        }

        // 地址检查只针对**会被分发出去**的节点：它没有可被访问的地址，别人就会去连错地址。
        // 自报 Distributable=false 的节点（只想测别人、不需要被别人测）不会被分发，
        // 因此允许地址留空或填回环——否则"纯客户端"根本注册不上，也就测不了别人。
        var endpoints = message.AllEndpoints;
        if (message.Distributable)
        {
            if (message.Port is < 1 or > 65535)
                return RegistryUpsertResult.Rejected(RegistryErrorCodes.BadRequest,
                    $"主外网端口必须在 1~65535 之间（当前是 {message.Port}）");
            if (!RegistryAddressRules.IsDistributable(message.Ip, out var primaryCode, out var primaryReason))
                return RegistryUpsertResult.Rejected(primaryCode, $"{message.Ip}：{primaryReason}");

            if (message.Endpoints is { Count: > 0 })
            {
                var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var endpoint in message.Endpoints)
                {
                    var ip = endpoint.Ip?.Trim() ?? "";
                    if (ip.Length == 0)
                        return RegistryUpsertResult.Rejected(RegistryErrorCodes.BadRequest, "外网端点 IP 不能为空");
                    if (!seenIps.Add(ip))
                        return RegistryUpsertResult.Rejected(RegistryErrorCodes.BadRequest, $"外网端点 IP {ip} 重复");
                    if (endpoint.Port is < 1 or > 65535)
                        return RegistryUpsertResult.Rejected(RegistryErrorCodes.BadRequest,
                            $"{ip} 的外网端口必须在 1~65535 之间");
                    if (!RegistryAddressRules.IsDistributable(ip, out var code, out var reason))
                        return RegistryUpsertResult.Rejected(code, $"{ip}：{reason}");
                }
            }

            if (endpoints.Count == 0)
                return RegistryUpsertResult.Rejected(RegistryErrorCodes.BadRequest,
                    "至少需要一个有效的 IP 与端口外网端点");

            foreach (var endpoint in endpoints)
            {
                if (!RegistryAddressRules.IsDistributable(endpoint.Ip, out var code, out var text))
                    return RegistryUpsertResult.Rejected(code, $"{endpoint.Ip}：{text}");
            }
        }

        var now = _clock();
        var node = _state.Nodes.FirstOrDefault(n => string.Equals(n.NodeId, message.NodeId, StringComparison.Ordinal));
        var changed = false;

        if (node is null)
        {
            node = new RegistryNode { NodeId = message.NodeId, FirstSeenUtc = now };
            _state.Nodes.Add(node);
            changed = true;
        }
        else if (!string.IsNullOrEmpty(message.Instance) && message.Instance != node.InstanceId)
        {
            // 进程标识变了：干净重启（正常），或配置被复制到两台机器在交替注册（疑似）。
            if (message.Instance == node.PriorInstanceId) node.DuplicateSuspected = true;
            else
            {
                node.PriorInstanceId = node.InstanceId;
                node.DuplicateSuspected = false;
            }
        }

        if (node.Name != message.Name ||
            node.Ip != message.Ip ||
            node.Port != message.Port ||
            node.Tcp != message.Tcp ||
            node.Udp != message.Udp ||
            node.Icmp != message.Icmp ||
            node.Distributable != message.Distributable ||
            node.TestReady != message.TestReady ||
            !node.AllEndpoints.Select(endpoint => $"{endpoint.Ip}|{endpoint.Port}")
                .SequenceEqual(endpoints.Select(endpoint => $"{endpoint.Ip}|{endpoint.Port}"), StringComparer.OrdinalIgnoreCase))
        {
            changed = true;
        }

        node.Name = message.Name;
        node.Ip = message.Ip;
        node.Endpoints = endpoints;
        node.Ips = endpoints.Select(endpoint => endpoint.Ip).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        node.Port = message.Port;
        node.Tcp = message.Tcp;
        node.Udp = message.Udp;
        node.Icmp = message.Icmp;
        node.Distributable = message.Distributable;
        node.TestReady = message.TestReady;
        node.InstanceId = message.Instance;
        node.LastSeenUtc = now;
        node.Authenticated = authenticated;

        if (!node.Online)
        {
            node.Online = true; // 心跳回来即在线（也覆盖"重启后立刻在线"）
            changed = true;
        }

        if (changed)
        {
            _state.Revision++;
            Save();
        }

        return RegistryUpsertResult.Ok(changed);
    }

    /// <summary>
    /// 移除节点（中心侧的权威删除）：从清单移除，并记入移除名单——
    /// 该节点**继续心跳也不会被加回来**，直到中心显式恢复。
    /// </summary>
    public bool RemoveNode(string nodeId)
    {
        var node = _state.Nodes.FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.Ordinal));

        var added = false;
        if (!_state.RemovedNodeIds.Contains(nodeId, StringComparer.Ordinal))
        {
            _state.RemovedNodeIds.Add(nodeId);
            added = true;
        }

        if (node is null && !added) return false;

        if (node is not null) _state.Nodes.Remove(node);

        _state.Revision++;
        Save();
        return true;
    }

    /// <summary>恢复被移除的节点（它会随下一次心跳重新出现）。</summary>
    public bool RestoreNode(string nodeId)
    {
        if (!_state.RemovedNodeIds.Remove(nodeId)) return false;

        _state.Revision++;
        Save();
        return true;
    }

    public bool IsRemoved(string nodeId) => _state.RemovedNodeIds.Contains(nodeId, StringComparer.Ordinal);

    public bool SetCenterHidden(string nodeId, bool hidden)
    {
        var node = _state.Nodes.FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.Ordinal));
        if (node is null || node.CenterHidden == hidden) return false;

        node.CenterHidden = hidden;
        _state.Revision++; // 影响快照内容
        Save();
        return true;
    }

    /// <summary>由中心“启动/停止测试”切换；变化进入 revision，使所有客户端拿到新快照。</summary>
    public bool SetGlobalTestRunning(bool running)
    {
        if (_state.GlobalTestRunning == running) return false;

        _state.GlobalTestRunning = running;
        _state.Revision++;
        Save();
        return true;
    }

    /// <summary>刷新在线状态：超 TTL **只标记离线、不删除**（抖动/临时断网不该让链路从所有人清单里消失）。</summary>
    public bool RefreshOnlineState()
    {
        var now = _clock();
        var changed = false;

        foreach (var node in _state.Nodes)
        {
            var online = IsRecent(node.LastSeenUtc, now);
            if (node.Online == online) continue;

            node.Online = online;
            changed = true;
        }

        if (changed)
        {
            _state.Revision++;
            Save();
        }

        return changed;
    }

    /// <summary>当前全部有效探测目标：必须在线、允许分发、未隐藏且已本地启动测试。</summary>
    public List<NodeSnapshot> BuildSnapshot()
        => _state.Nodes
            .Where(n => n.IsEligibleProbeTarget)
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .ThenBy(n => n.NodeId, StringComparer.Ordinal)
            .Select(n => new NodeSnapshot
            {
                NodeId = n.NodeId,
                Name = n.Name,
                Ip = n.Ip,
                // 多地址只在真的多于一个时才下发：单地址保持与老客户端/老中心完全一致的报文
                Ips = n.Ips.Count > 1 ? n.Ips.ToList() : null,
                Port = n.Port,
                Tcp = n.Tcp,
                Udp = n.Udp,
                Icmp = n.Icmp,
                Online = n.Online,
                TestReady = n.TestReady,
                Endpoints = n.AllEndpoints.Any(endpoint => endpoint.Port != n.Port)
                    ? n.AllEndpoints.Select(endpoint => new ExternalEndpoint { Ip = endpoint.Ip, Port = endpoint.Port }).ToList()
                    : null
            })
            .ToList();

    public void Save()
    {
        var json = JsonSerializer.Serialize(_state, JsonOptions);
        AtomicFile.WriteAllText(_path, json);
    }
}
