using System.IO;
using System.Net;
using System.Windows.Controls;
using LineTester.Registry;

namespace LineTester.Tests;

/// <summary>
/// ICMP(ping) 探测协议的自检。
///
/// 真回环 ping（127.0.0.1）由 Windows 内核应答，用来验证"能通、有 RTT"；
/// 不可达/黑地址用来验证失败原因分类。断言里不依赖外网。
/// </summary>
internal static partial class Program
{
    private static void TestIcmpPing()
    {
        Section("ICMP：真实回环 ping 与失败分类");

        var payload = System.Text.Encoding.UTF8.GetBytes("1|abc123|icmp");

        var loopback = IcmpPinger.PingAsync(IPAddress.Loopback, payload, 2000, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("回环 ping 成功（Windows 内核应答 127.0.0.1）", loopback.Success, loopback.Detail);
        Check("回环 ping 往返时间 ≥ 0 且很小于超时时间",
            loopback.RttMs >= 0 && loopback.RttMs < 2000, loopback.RttMs.ToString());
        Eq("成功时没有失败原因", "", loopback.FailureToken);

        // 回包内容必须原样带回，否则会判成 mismatch
        var odd = System.Text.Encoding.UTF8.GetBytes("7|deadbeefdeadbeef|custom-payload");
        var again = IcmpPinger.PingAsync(IPAddress.Loopback, odd, 2000, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check("任意载荷都能被原样回显（可作为探测ID校验）", again.Success, again.Detail);

        // 不可达/黑洞地址：短超时下应当失败，且原因是我们认识的 token
        var blackhole = IcmpPinger.PingAsync(IPAddress.Parse("203.0.113.254"), payload, 400, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("黑洞地址 ping 失败", !blackhole.Success, "意外成功");
        Check("失败原因是超时或不可达（不是 error）",
            blackhole.FailureToken is FailureKinds.Timeout or FailureKinds.IcmpUnreachable or FailureKinds.Unreachable,
            blackhole.FailureToken + " / " + blackhole.Detail);

        // ICMP 只支持 IPv4：IPv6 目标要给出明确原因，而不是静默失败
        var v6 = IcmpPinger.PingAsync(IPAddress.IPv6Loopback, payload, 500, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check("IPv6 目标明确报只支持 IPv4", !v6.Success && v6.Detail.Contains("IPv4"), v6.Detail);
    }

    private static void TestIcmpProbe()
    {
        Section("ICMP：作为探测协议接入（ProbeClient / 统计口径）");

        var client = new ProbeClient();
        var link = new LinkConfig { Name = "回环", Host = "127.0.0.1", Port = 9000, Icmp = true };

        var result = client.ProbeAsync(link, ProbeClient.ProtocolIcmp, 2000, CancellationToken.None)
            .GetAwaiter().GetResult();

        Check("ICMP 探测成功", result is { Lost: false }, result?.Info);
        Eq("协议标记为 ICMP", ProbeClient.ProtocolIcmp, result?.Protocol ?? "");
        Eq("统计键包含 ICMP", "回环|127.0.0.1|9000|ICMP", result?.Key ?? "");
        Check("ICMP 有 RTT 数值", result?.RttMs is >= 0, result?.RttMs?.ToString() ?? "null");
        Check("ICMP 不产生对端看到的本端源地址（内核不会替我们回显）",
            string.IsNullOrEmpty(result?.SeenFrom), result?.SeenFrom);
        Eq("ICMP 源地址展示为占位符（与 TCP/UDP 的显示约定一致）", "-", result?.SeenFromText ?? "");

        // 失败原因文案：ICMP 的超时必须额外提示"对端可能禁 ping"
        Eq("ICMP 超时文案带禁 ping 提示",
            "无响应（超时；对端可能禁 ping 或线路不通）",
            FailureKinds.Describe(FailureKinds.Timeout, ProbeClient.ProtocolIcmp));
        Eq("TCP 超时文案不受影响", "无响应（超时）", FailureKinds.Describe(FailureKinds.Timeout, "TCP"));

        // 全局协议开关与链路开关取交集
        var row = new LinkRow { Name = "回环", Host = "127.0.0.1", PortText = "9000", Tcp = false, Udp = false, Icmp = true };
        row.SetGlobalProtocols(true, true, true);
        Eq("只勾 ICMP 时「生效」列显示 ICMP", "ICMP", row.Effective);

        row.SetGlobalProtocols(true, true, false);
        Eq("全局取消 ICMP 后压住链路设置", "不发包", row.Effective);

        var config = row.ToConfig();
        Check("ICMP 勾选写进配置", config.Icmp);
        var back = LinkRow.FromConfig(config);
        Check("ICMP 勾选从配置读回", back.Icmp);

        // 读回的行用的是默认全局开关（ICMP 默认关闭）→ 此时"生效"应为不发包（总开关只能收紧）
        Eq("全局 ICMP 未开时读回的行不发包", "不发包", back.Effective);
        back.SetGlobalProtocols(true, true, true);
        Eq("全局 ICMP 打开后「生效」只显示 ICMP", "ICMP", back.Effective);
    }

    private static void TestIcmpRegistry()
    {
        Section("ICMP：注册中心宣告与链路生成（含本地意图）");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "icmp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var store = new RegistryStore(Path.Combine(root, "registry.json"), 60);
        store.Upsert(new RegisterMessage
        {
            NodeId = "icmp-node",
            Name = "厦门机房",
            Ip = "110.86.28.214",
            Port = 17078,
            Tcp = false,
            Udp = true,
            Icmp = true
        }, true);

        var node = store.Nodes.First(n => n.NodeId == "icmp-node");
        Check("中心存下 ICMP 宣告", node.Icmp);
        Check("快照带出 ICMP 宣告", store.BuildSnapshot().First(n => n.NodeId == "icmp-node").Icmp);

        var outcome = RegistrySync.Apply(store.BuildSnapshot(), "self", new List<LinkConfig>());
        var link = outcome.Links.First(l => l.NodeId == "icmp-node");
        Check("生成的链路带了 ICMP（对端宣告可被 ping）", link.Icmp);
        Check("TCP 仍按宣告关闭", !link.Tcp);

        // 本地单独关掉 ICMP → 同步不能改回来
        link.Icmp = false;
        link.LocalIcmpOff = true;

        var afterEdit = RegistrySync.Apply(store.BuildSnapshot(), "self", outcome.Links);
        var kept = afterEdit.Links.First(l => l.NodeId == "icmp-node");
        Check("本地关掉的 ICMP 不会被同步改回", !kept.Icmp, kept.Icmp.ToString());
        Check("本地 ICMP 意图被保留", kept.LocalIcmpOff);
        Check("UDP 照常按中心宣告", kept.Udp);

        // 重新勾选 → 恢复按中心值
        kept.Icmp = true;
        kept.LocalIcmpOff = false;
        var restored = RegistrySync.Apply(store.BuildSnapshot(), "self", afterEdit.Links);
        Check("重新勾选后恢复中心宣告的 ICMP",
            restored.Links.First(l => l.NodeId == "icmp-node").Icmp);

        // 老配置（没有 Icmp 字段）→ 默认关闭，不影响既有链路
        store.Upsert(new RegisterMessage
        {
            NodeId = "old-node", Name = "老节点", Ip = "203.0.113.9", Port = 9000, Tcp = true, Udp = true
        }, true);

        var oldLink = RegistrySync.Apply(store.BuildSnapshot(), "self", new List<LinkConfig>())
            .Links.First(l => l.NodeId == "old-node");
        Check("未宣告 ICMP 的节点生成的链路不开 ICMP", !oldLink.Icmp);
        Check("同时不影响它的 TCP/UDP", oldLink.Tcp && oldLink.Udp);

        try { Directory.Delete(root, true); } catch { }
    }

    private static void TestIcmpUi()
    {
        Section("ICMP：界面开关、链路列与统计行");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "icmpui_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        var listenPort = FindBindablePort();
        ConfigManager.Save(new AppConfig
        {
            ListenPort = listenPort,
            IntervalMs = 300,
            TimeoutMs = 1000,
            EnableTcp = false,
            EnableUdp = false,
            EnableIcmp = true,
            Isp = new IspConfig { Enabled = false },
            // ICMP 不带端口：这里端口随意，ping 的是主机
            Links = { new LinkConfig { Name = "本机ping", Host = "127.0.0.1", Port = 9000, Tcp = false, Udp = false, Icmp = true } }
        });

        try
        {
            RunOnSta(() =>
            {
                var window = new MainWindow();

                Check("参数区有 ICMP 总开关", window.FindName("IcmpCheck") is CheckBox);
                Check("ICMP 总开关按配置勾选", ((CheckBox)window.FindName("IcmpCheck")!).IsChecked == true);

                var grid = (DataGrid)window.FindName("LinksGrid")!;
                Check("链路配置页有 ICMP 列", grid.Columns.Any(c => Equals(c.Header, "ICMP")));

                var rows = (System.Collections.IList)GetPrivateField(window, "_linkRows")!;
                var row = (LinkRow)rows[0]!;
                Check("链路带了 ICMP 勾选", row.Icmp);
                Eq("「生效」列显示 ICMP", "ICMP", row.Effective);

                InvokePrivateHandler(window, "Start_Click");
                Pump(3000);

                var trend = (System.Collections.IList)GetPrivateField(window, "_trend")!;
                var icmpRows = trend.Cast<ProbeResult>().Where(r => r.Protocol == "ICMP").ToList();

                Check("只开 ICMP 时产生了 ICMP 探测结果", icmpRows.Count > 0, "行数=" + trend.Count);
                Check("ICMP 结果都成功（本机 ping 通）", icmpRows.All(r => !r.Lost),
                    string.Join(",", icmpRows.Where(r => r.Lost).Select(r => r.FailureText).Distinct()));
                Check("ICMP 有 RTT", icmpRows.Any(r => r.RttMs is >= 0));

                var stats = (System.Collections.IList)GetPrivateField(window, "_stats")!;
                var statNames = stats.Cast<StatRow>().Select(r => r.Name).ToList();
                Check("统计表里出现「链路 / ICMP」行",
                    statNames.Any(n => n.Contains("ICMP")), string.Join(",", statNames));

                var statusParams = (TextBlock)window.FindName("StatusParams")!;
                Check("状态栏协议显示 ICMP", statusParams.Text.Contains("ICMP"), statusParams.Text);

                InvokePrivateHandler(window, "Stop_Click");
                window.Close();
            }, "ICMP 界面断言");
        }
        finally
        {
            ConfigManager.OverridePath = previousOverride;
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
