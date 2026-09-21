using System.Net;

namespace LineTester.Registry;

/// <summary>一行提示文本 + 是否需要标红。</summary>
public sealed record StatusNote(string Text, bool Warning);

/// <summary>
/// 注册中心相关的界面文案（规格 §11.1~§11.3）。
///
/// 单独抽出来的原因：这些文案是**运维判断依据**（地址填错、端口填错、中心不可达、
/// 回环地址被拦），必须能被零依赖自检逐条断言，而不是散在窗口代码里靠肉眼检查。
/// 本类不引用任何 WPF 类型。
/// </summary>
public static class RegistryUiText
{
    /// <summary>
    /// 状态栏一行（规格 §11.3）。未启用注册时返回 null —— 状态栏保持与升级前完全一致。
    /// </summary>
    public static StatusNote? DescribeConnection(RegistryClientStatus? status, RegistrarConfig? config, DateTime nowUtc)
    {
        if (config is null || !config.Enabled) return null;

        var center = $"{config.CenterHost.Trim()}:{config.CenterPort}";

        if (status is null || !status.Running)
            return new StatusNote("⚠ 注册已启用，但同步未运行（点“启动测试”后开始注册与同步）", true);

        if (status.LastError is not null)
        {
            var since = status.LastSuccessUtc ?? status.LastAttemptUtc;
            var elapsed = since is null ? "" : $"（{DescribeElapsed(nowUtc - since.Value)}，退避中）";
            return new StatusNote($"⚠ 注册中心 {center} 不可达：{status.LastError}{elapsed}", true);
        }

        if (status.LastSuccessUtc is null)
            return new StatusNote($"注册中心 {center} · 已启动，等待首次回执", false);

        return new StatusNote(
            $"注册中心 {center} · rev {status.Revision} · 节点 {status.NodeCount} · " +
            $"{status.LastSuccessUtc.Value.ToLocalTime():HH:mm} 同步", false);
    }

    /// <summary>
    /// 中心看到的源地址 vs 手填的外网地址（规格 §6.3 的交叉核对）。
    /// 它**只做提示**：出向 NAT 源端口 ≠ 入向映射端口，端口不同是正常的，不能据此自动改配置。
    /// </summary>
    public static StatusNote? DescribeSelfCheck(RegistryClientStatus? status, RegistrarConfig? config)
    {
        if (config is null || !config.Enabled) return null;

        var host = config.PublicHost.Trim();
        var port = config.PublicPort;

        if (status?.SeenFrom is null || status.SeenFrom.Length == 0)
        {
            return new StatusNote(
                "中心还没回过包，暂时无法核对对外地址。注意：外网地址必须手工填写，" +
                "不能照抄中心看到的源地址（出方向源端口 ≠ 入方向映射端口）。", false);
        }

        var seen = status.SeenFrom;
        var separator = seen.LastIndexOf(':');
        if (separator <= 0)
            return new StatusNote($"中心回执里的源地址无法解析：{seen}", false);

        var seenIp = seen[..separator];
        var seenPortText = seen[(separator + 1)..];
        var seenPort = int.TryParse(seenPortText, out var parsed) ? parsed : 0;

        if (string.Equals(host, seenIp, StringComparison.OrdinalIgnoreCase))
        {
            if (seenPort == port)
                return new StatusNote($"✓ 中心看到的源地址 {seen} 与本端填写的一致", false);

            return new StatusNote(
                $"⚠ 中心看到的源 IP 一致（{seenIp}），但源端口是 {seenPort}，本端填写的是 {port}。" +
                $"出方向 NAT 源端口 ≠ 入方向映射端口属正常现象，请确认 {port} 就是机房映射进来的那个端口，否则其他节点连不上本端。",
                true);
        }

        if (!IPAddress.TryParse(host, out _))
        {
            return new StatusNote(
                $"中心看到的源 IP 是 {seenIp}；本端填写的是主机名 {host}，无法直接比对（请确认它解析后就是该地址）。", false);
        }

        return new StatusNote(
            $"⚠ 中心看到的源 IP 是 {seenIp}，本端填写的是 {host}。" +
            "若本机有多出口或走代理属正常，但请确认填写的是其他节点能访问到的地址。", true);
    }

    /// <summary>本节点会不会被分发（规格 §6.2 / §10：回环、未指定、链路本地地址一律不分发）。</summary>
    public static StatusNote? DescribeSelfDistribution(RegistrarConfig? config)
    {
        if (config is null || !config.Enabled) return null;

        if (!config.Distributable)
            return new StatusNote("已关闭「参与网状」：本节点注册后不会被分发给其他节点，其他节点上不会出现本机链路。", false);

        if (string.IsNullOrWhiteSpace(config.PublicHost))
            return new StatusNote("⚠ 未填写外网地址，其他节点无法探测本机。", true);

        if (!RegistryAddressRules.IsDistributable(config.PublicHost, out _, out var reason))
        {
            return new StatusNote(
                $"⚠ 本端外网地址 {config.PublicHost} 不可分发（{reason}）：中心不会把它发给其他节点，" +
                "本机不会出现在别人的链路里。", true);
        }

        var protocols = DescribeProtocols(config.AdvertiseTcp, config.AdvertiseUdp);
        var target = $"{config.PublicHost.Trim()}:{config.PublicPort}";

        if (!config.AdvertiseTcp && !config.AdvertiseUdp)
        {
            return new StatusNote(
                $"地址可分发（{target}），但「可被探测的协议」都未勾选：" +
                "其他节点上会出现这条链路，却不会向本机发包。", false);
        }

        return new StatusNote($"可被其他节点探测：{target}（{protocols}）", false);
    }

    public static string DescribeProtocols(bool tcp, bool udp) => (tcp, udp) switch
    {
        (true, true) => "TCP + UDP",
        (true, false) => "仅 TCP",
        (false, true) => "仅 UDP",
        _ => "都未勾选"
    };

    /// <summary>中心节点表里的状态列：在线/分发/认证/疑似重复。</summary>
    public static string DescribeNodeState(RegistryNode node)
        => DescribeNodeState(node.Online, node.Distributable, node.CenterHidden, node.Authenticated, node.DuplicateSuspected);

    public static string DescribeNodeState(
        bool online, bool distributable, bool centerHidden, bool authenticated, bool duplicateSuspected)
    {
        var parts = new List<string>(4)
        {
            online ? "在线" : "离线",
            distributable && !centerHidden ? "分发中" : centerHidden ? "已隐藏" : "对端关分发",
            authenticated ? "已认证" : "无密钥"
        };

        if (duplicateSuspected) parts.Add("⚠ 疑似重复 nodeId");
        return string.Join(" · ", parts);
    }

    /// <summary>已被中心移除的节点：它继续心跳也不会被加回清单。</summary>
    public const string RemovedNodeState = "已移除（不会被心跳加回，需在中心侧恢复）";

    /// <summary>最后心跳：本地时刻 + 相对时间。</summary>
    public static string DescribeLastSeen(DateTime lastSeenUtc, DateTime nowUtc)
        => $"{lastSeenUtc.ToLocalTime():HH:mm:ss}（{DescribeAgo(nowUtc - lastSeenUtc)}）";

    public static string DescribeAgo(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
        return $"{(int)span.TotalDays} 天前";
    }

    private static string DescribeElapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return "已不到 1 分钟";
        if (span.TotalMinutes < 60) return $"已 {(int)span.TotalMinutes} 分钟";
        if (span.TotalHours < 24) return $"已 {(int)span.TotalHours} 小时";
        return $"已 {(int)span.TotalDays} 天";
    }
}
