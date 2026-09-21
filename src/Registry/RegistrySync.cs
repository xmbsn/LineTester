namespace LineTester.Registry;

/// <summary>
/// 把中心快照套用到本地链路清单（规格 §8.3 的 7 步，纯函数、可单测）。
///
/// 三条硬规则：
/// ① 过滤掉本机 nodeId（自己不进自己的链路）；
/// ② 过滤掉不可分发地址（回环/未指定/link-local）——双保险，防止中心版本较旧或规则被绕过；
/// ③ **只动 `Source=registry` 的链路**，本地手工链路原样保留；`LocalDisabled` 的本地停测意图
///    必须跨同步保留（否则"取消勾选"会被下一次同步覆盖掉）。
/// </summary>
public static class RegistrySync
{
    public sealed record SyncOutcome(
        List<LinkConfig> Links,
        int Added,
        int Updated,
        int Removed,
        int SkippedUnusable)
    {
        public bool HasChanges => Added > 0 || Updated > 0 || Removed > 0;
    }

    public static SyncOutcome Apply(
        IReadOnlyList<NodeSnapshot> snapshot,
        string localNodeId,
        IReadOnlyList<LinkConfig> existing)
    {
        // 1) 目标集合：跳过自己、跳过不可用地址
        var targets = new List<NodeSnapshot>(snapshot.Count);
        var skipped = 0;

        foreach (var node in snapshot)
        {
            if (!string.IsNullOrEmpty(localNodeId) &&
                string.Equals(node.NodeId, localNodeId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!RegistryAddressRules.IsDistributable(node.Ip))
            {
                skipped++;
                continue;
            }

            targets.Add(node);
        }

        // 2) manual 链路原样保留；registry 链路按「节点 + 地址」建索引
        //    （同一个机房可能有多个外网 IP，同一个 NodeId 会对应多条链路）
        var result = new List<LinkConfig>(existing.Count + targets.Count);
        var currentByKey = new Dictionary<string, LinkConfig>(StringComparer.Ordinal);

        foreach (var link in existing)
        {
            if (link.Source == LinkSources.Registry && !string.IsNullOrEmpty(link.NodeId))
                currentByKey[Key(link.NodeId, link.Host)] = link;
            else
                result.Add(link);
        }

        // 3) 逐个目标、逐个地址：新增或原地更新（保留 LocalDisabled）
        var added = 0;
        var updated = 0;
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in targets)
        {
            var baseName = string.IsNullOrWhiteSpace(node.Name) ? $"{node.Ip}:{node.Port}" : node.Name.Trim();
            var addresses = node.AllIps.Count > 0 ? node.AllIps : new List<string> { node.Ip };

            for (var i = 0; i < addresses.Count; i++)
            {
                var ip = addresses[i];

                // 主地址用节点名，其余加 #2/#3 后缀：既能在统计表里区分，又能一眼看出是同一个机房
                var name = i == 0 ? baseName : $"{baseName}#{i + 1}";

                currentByKey.TryGetValue(Key(node.NodeId, ip), out var current);

                // 本地意图：老配置只有 LocalDisabled（两个都不勾），新配置可以单独关一个协议。
                // 迁移规则：LocalDisabled=true 视为两个协议都本地关掉。
                var tcpOff = current is null
                    ? false
                    : current.LocalTcpOff || current.LocalDisabled;
                var udpOff = current is null
                    ? false
                    : current.LocalUdpOff || current.LocalDisabled;

                var localDisabled = tcpOff && udpOff;
                var tcp = node.Tcp && !tcpOff;
                var udp = node.Udp && !udpOff;

                if (current is null)
                {
                    added++;
                }
                else if (current.Name != name ||
                         current.Port != node.Port ||
                         current.Tcp != tcp ||
                         current.Udp != udp)
                {
                    updated++;
                }

                used.Add(Key(node.NodeId, ip));
                result.Add(new LinkConfig
                {
                    Name = name,
                    Host = ip,
                    Port = node.Port,
                    Tcp = tcp,
                    Udp = udp,
                    Source = LinkSources.Registry,
                    NodeId = node.NodeId,
                    LocalDisabled = localDisabled,
                    LocalTcpOff = tcpOff,
                    LocalUdpOff = udpOff
                });
            }
        }

        // 4) 已在本地、但目标集合里没有的注册链路 → 删除（中心删节点、或对方撤掉某个 IP）
        var removed = 0;
        foreach (var key in currentByKey.Keys)
        {
            if (!used.Contains(key)) removed++;
        }

        return new SyncOutcome(result, added, updated, removed, skipped);
    }

    /// <summary>注册链路的身份：来源节点 + 该节点的一个外网地址。</summary>
    public static string Key(string nodeId, string ip)
        => $"{nodeId}|{ip?.Trim() ?? ""}";
}
