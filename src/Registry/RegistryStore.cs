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
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public bool Tcp { get; set; }
    public bool Udp { get; set; }

    /// <summary>节点自报：是否允许被分发（对端心跳每次都会重报）。</summary>
    public bool Distributable { get; set; } = true;

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

    /// <summary>是否真正进入分发快照：节点自报与中心覆盖取交集。</summary>
    [JsonIgnore]
    public bool IsDistributed => Distributable && !CenterHidden;
}

public sealed class RegistryState
{
    public ulong Revision { get; set; }
    public List<RegistryNode> Nodes { get; set; } = new();
}

public sealed record RegistryUpsertResult(bool Accepted, string ErrorCode, string ErrorMessage, bool Changed)
{
    public static RegistryUpsertResult Ok(bool changed) => new(true, "", "", changed);
    public static RegistryUpsertResult Rejected(string code, string message) => new(false, code, message, false);
}

/// <summary>
/// 中心清单与 revision（规格 §5.6 / §7）。
///
/// revision 规则：**只有"可分发相关状态真的变化"时才 +1**——纯心跳只刷新 lastSeen，不涨 rev，
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

    public IReadOnlyList<RegistryNode> Nodes => _state.Nodes;

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

    /// <summary>注册/心跳（幂等 upsert）。地址不合规时拒绝并带回错误码。</summary>
    public RegistryUpsertResult Upsert(RegisterMessage message, bool authenticated)
    {
        if (!RegistryAddressRules.IsDistributable(message.Ip, out var errorCode, out var errorMessage))
            return RegistryUpsertResult.Rejected(errorCode, errorMessage);

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
            node.Distributable != message.Distributable)
        {
            changed = true;
        }

        node.Name = message.Name;
        node.Ip = message.Ip;
        node.Port = message.Port;
        node.Tcp = message.Tcp;
        node.Udp = message.Udp;
        node.Distributable = message.Distributable;
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

    public bool RemoveNode(string nodeId)
    {
        var node = _state.Nodes.FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.Ordinal));
        if (node is null) return false;

        _state.Nodes.Remove(node);
        _state.Revision++;
        Save();
        return true;
    }

    public bool SetCenterHidden(string nodeId, bool hidden)
    {
        var node = _state.Nodes.FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.Ordinal));
        if (node is null || node.CenterHidden == hidden) return false;

        node.CenterHidden = hidden;
        _state.Revision++; // 影响快照内容
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

    /// <summary>当前全部可分发节点的快照（含离线节点：临时离线不该导致链路被删）。</summary>
    public List<NodeSnapshot> BuildSnapshot()
        => _state.Nodes
            .Where(n => n.IsDistributed)
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .ThenBy(n => n.NodeId, StringComparer.Ordinal)
            .Select(n => new NodeSnapshot
            {
                NodeId = n.NodeId,
                Name = n.Name,
                Ip = n.Ip,
                Port = n.Port,
                Tcp = n.Tcp,
                Udp = n.Udp,
                Online = n.Online
            })
            .ToList();

    public void Save()
    {
        var json = JsonSerializer.Serialize(_state, JsonOptions);
        AtomicFile.WriteAllText(_path, json);
    }
}
