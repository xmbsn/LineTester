using System.ComponentModel;
using System.Runtime.CompilerServices;
using LineTester.Registry;

namespace LineTester.Views;

/// <summary>
/// 中心节点表的一行（规格 §11.1 的「注册中心管理」）。
///
/// 「分发」勾选框直接写中心侧 <c>centerHidden</c>（只影响中心快照，不改对端配置）：
/// 取消勾选后，该节点立刻从所有子节点的链路里消失，且**不会被它的心跳顶回来**。
///
/// 已移除的节点也作为一行显示（<see cref="IsRemoved"/>）：否则"恢复"按钮没法选中它——
/// 它已经不在清单里了。纯数据 + 回调，不引用 WPF 类型，因此可以在零依赖自检里驱动。
/// </summary>
public sealed class RegistryNodeRow : INotifyPropertyChanged
{
    private readonly Func<string, bool, bool> _setCenterHidden;

    private readonly bool _online;
    private readonly bool _distributable;
    private readonly bool _authenticated;
    private readonly bool _duplicateSuspected;
    private bool _centerHidden;

    private RegistryNodeRow(
        string nodeId, string name, string address, string protocols, string lastSeen,
        bool online, bool distributable, bool authenticated, bool duplicateSuspected,
        bool centerHidden, bool removed, Func<string, bool, bool> setCenterHidden)
    {
        _setCenterHidden = setCenterHidden;

        NodeId = nodeId;
        Name = name;
        Address = address;
        Protocols = protocols;
        LastSeen = lastSeen;

        _online = online;
        _distributable = distributable;
        _authenticated = authenticated;
        _duplicateSuspected = duplicateSuspected;
        _centerHidden = centerHidden;
        IsRemoved = removed;
    }

    /// <param name="setCenterHidden">参数为 (nodeId, hidden)，返回是否真的改动了中心清单。</param>
    public static RegistryNodeRow FromNode(RegistryNode node, DateTime nowUtc, Func<string, bool, bool> setCenterHidden)
        => new(
            node.NodeId,
            string.IsNullOrWhiteSpace(node.Name) ? node.NodeId : node.Name,
            // 不参与网状的节点地址本来就是空的：显示成"-"比 ":0" 诚实
            string.IsNullOrWhiteSpace(node.Ip) ? "（未提供，不分发）" : $"{node.Ip}:{node.Port}",
            RegistryUiText.DescribeProtocols(node.Tcp, node.Udp),
            RegistryUiText.DescribeLastSeen(node.LastSeenUtc, nowUtc),
            node.Online, node.Distributable, node.Authenticated, node.DuplicateSuspected, node.CenterHidden, false,
            setCenterHidden);

    /// <summary>已被中心移除、仍在移除名单里的节点。</summary>
    public static RegistryNodeRow FromRemoved(string nodeId, Func<string, bool, bool> setCenterHidden)
        => new(nodeId, nodeId, "-", "-", "-",
            false, false, false, false, false, true, setCenterHidden);

    public string NodeId { get; }
    public string Name { get; }
    public string Address { get; }
    public string Protocols { get; }
    public string LastSeen { get; }

    /// <summary>true = 已被中心移除（只能"恢复"，不能再移除一次）。</summary>
    public bool IsRemoved { get; }

    /// <summary>「分发」勾选框是否可写：已移除的节点不可写。</summary>
    public bool CanDistribute => !IsRemoved;

    public string State => IsRemoved
        ? RegistryUiText.RemovedNodeState
        : RegistryUiText.DescribeNodeState(_online, _distributable, _centerHidden, _authenticated, _duplicateSuspected);

    /// <summary>是否参与分发（= 中心侧未隐藏）。写入失败（节点已被移除）时回滚显示。</summary>
    public bool Distributed
    {
        get => !IsRemoved && !_centerHidden;
        set
        {
            if (IsRemoved || value == Distributed) return;

            if (!_setCenterHidden(NodeId, !value))
            {
                Raise(nameof(Distributed)); // 写不进去 → 让复选框回到实际状态
                return;
            }

            _centerHidden = !value;
            Raise(nameof(Distributed));
            Raise(nameof(State));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
