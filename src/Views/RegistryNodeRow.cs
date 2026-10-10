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
    private readonly bool _testReady;
    private bool _centerHidden;

    private RegistryNodeRow(
        string nodeId, string name, string address, string protocols, string lastSeen,
        bool online, bool distributable, bool authenticated, bool duplicateSuspected, bool testReady,
        bool centerHidden, bool removed, Func<string, bool, bool> setCenterHidden,
        bool longOffline = false, string allAddresses = "")
    {
        _setCenterHidden = setCenterHidden;

        NodeId = nodeId;
        Name = name;
        Address = address;
        Protocols = protocols;
        LastSeen = lastSeen;
        AllAddresses = allAddresses.Length > 0 ? allAddresses : address;

        _online = online;
        _distributable = distributable;
        _authenticated = authenticated;
        _duplicateSuspected = duplicateSuspected;
        _testReady = testReady;
        _centerHidden = centerHidden;
        IsRemoved = removed;
        LongOffline = longOffline;
    }

    /// <param name="setCenterHidden">参数为 (nodeId, hidden)，返回是否真的改动了中心清单。</param>
    public static RegistryNodeRow FromNode(RegistryNode node, DateTime nowUtc, Func<string, bool, bool> setCenterHidden)
    {
        var endpoints = node.AllEndpoints;
        if (endpoints.Count == 0 && !string.IsNullOrWhiteSpace(node.Ip))
            endpoints.Add(new ExternalEndpoint { Ip = node.Ip, Port = node.Port });

        // 一个机房多个外网 IP 时，**必须都显示出来**——曾经只显示主地址，
        // 现场看到的就成了"我注册了 3 个 IP，中心界面只有 1 个"。
        var primary = endpoints.Count == 0 ? "（未提供，不分发）" : $"{endpoints[0].Ip}:{endpoints[0].Port}";
        var shown = endpoints.Count > 1
            ? $"{primary}  等 {endpoints.Count} 个"
            : primary;

        var all = string.Join("、", endpoints.Select(endpoint => $"{endpoint.Ip}:{endpoint.Port}"));

        return new RegistryNodeRow(
            node.NodeId,
            string.IsNullOrWhiteSpace(node.Name) ? node.NodeId : node.Name,
            shown,
            RegistryUiText.DescribeProtocols(node.Tcp, node.Udp, node.Icmp),
            RegistryUiText.DescribeLastSeen(node.LastSeenUtc, nowUtc),
            node.Online, node.Distributable, node.Authenticated, node.DuplicateSuspected, node.TestReady, node.CenterHidden, false,
            setCenterHidden,
            // 长期离线（>7 天没有心跳）：这些行往往已经不会再回来，却会让所有节点一直维持到它的链路。
            // 判据只看"多久没心跳"，不依赖 Online 标志——那个标志要等中心刷新才会更新。
            nowUtc - node.LastSeenUtc > TimeSpan.FromDays(7),
            all);
    }

    /// <summary>已被中心移除、仍在移除名单里的节点。</summary>
    public static RegistryNodeRow FromRemoved(string nodeId, Func<string, bool, bool> setCenterHidden)
        => new(nodeId, nodeId, "-", "-", "-",
            false, false, false, false, false, false, true, setCenterHidden);

    public string NodeId { get; }
    public string Name { get; }

    /// <summary>地址列显示文本：多地址时显示主地址并注明总数。</summary>
    public string Address { get; }

    /// <summary>全部地址（含端口），用于提示框——一眼确认 3 个 IP 都注册上来了。</summary>
    public string AllAddresses { get; }

    public string Protocols { get; }
    public string LastSeen { get; }

    /// <summary>true = 已被中心移除（只能"恢复"，不能再移除一次）。</summary>
    public bool IsRemoved { get; }

    /// <summary>节点身份前 8 位：用来发现"同一台机器的历史遗留旧身份"（重复行）。</summary>
    public string ShortId => NodeId.Length <= 8 ? NodeId : NodeId[..8];

    /// <summary>是否长期离线（&gt; 7 天）：这类行往往是换过配置/去掉了的机器，链路在别人那里一直重建。</summary>
    public bool LongOffline { get; private init; }

    /// <summary>「分发」勾选框是否可写：已移除的节点不可写。</summary>
    public bool CanDistribute => !IsRemoved;

    public string State => IsRemoved
        ? RegistryUiText.RemovedNodeState
        : RegistryUiText.DescribeNodeState(_online, _distributable, _centerHidden, _authenticated, _duplicateSuspected, _testReady);

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
