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

        // 2) manual 链路原样保留；registry 链路按 NodeId 建索引，便于原地更新
        var result = new List<LinkConfig>(existing.Count + targets.Count);
        var currentByNodeId = new Dictionary<string, LinkConfig>(StringComparer.Ordinal);

        foreach (var link in existing)
        {
            if (link.Source == LinkSources.Registry && !string.IsNullOrEmpty(link.NodeId))
                currentByNodeId[link.NodeId] = link;
            else
                result.Add(link);
        }

        // 3) 逐个目标：新增或原地更新（保留 LocalDisabled）
        var added = 0;
        var updated = 0;
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in targets)
        {
            var name = string.IsNullOrWhiteSpace(node.Name) ? $"{node.Ip}:{node.Port}" : node.Name.Trim();
            currentByNodeId.TryGetValue(node.NodeId, out var current);

            var localDisabled = current?.LocalDisabled ?? false;
            var tcp = node.Tcp && !localDisabled;
            var udp = node.Udp && !localDisabled;

            if (current is null)
            {
                added++;
            }
            else if (current.Name != name ||
                     current.Host != node.Ip ||
                     current.Port != node.Port ||
                     current.Tcp != tcp ||
                     current.Udp != udp)
            {
                updated++;
            }

            used.Add(node.NodeId);
            result.Add(new LinkConfig
            {
                Name = name,
                Host = node.Ip,
                Port = node.Port,
                Tcp = tcp,
                Udp = udp,
                Source = LinkSources.Registry,
                NodeId = node.NodeId,
                LocalDisabled = localDisabled
            });
        }

        // 4) 已在本地、但目标集合里没有的注册链路 → 删除（这就是"中心删节点后其他节点消失"）
        var removed = 0;
        foreach (var nodeId in currentByNodeId.Keys)
        {
            if (!used.Contains(nodeId)) removed++;
        }

        return new SyncOutcome(result, added, updated, removed, skipped);
    }
}
