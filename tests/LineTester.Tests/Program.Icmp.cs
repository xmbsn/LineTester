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
    /// <summary>
    /// 人工排查入口：连续 ping 同一个目标，逐次打印结果。
    ///
    /// 用途是复现"只发了几次就不发了"这类**连续性问题**——单次 ping 的用例看不出来：
    /// <c>dotnet run --project .\tests\LineTester.Tests -c Release -- --icmp-loop 127.0.0.1 20 300</c>
    /// </summary>
    private static int IcmpLoop(string host, int count, int intervalMs, int timeoutMs)
    {
        Console.WriteLine($"ICMP 连续探测：host={host} 次数={count} 间隔={intervalMs}ms 超时={timeoutMs}ms");
        Console.WriteLine(new string('-', 72));

        var client = new ProbeClient();
        var link = new LinkConfig { Name = "loop", Host = host, Port = 9000, Icmp = true };
        var okCount = 0;
        var consecutiveLoss = 0;
        var maxConsecutiveLoss = 0;

        for (var i = 1; i <= count; i++)
        {
            var started = DateTime.UtcNow;
            ProbeResult? result = null;
            string? thrown = null;
            try
            {
                result = client.ProbeAsync(link, ProbeClient.ProtocolIcmp, timeoutMs, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                thrown = ex.GetType().Name + ": " + ex.Message;
            }

            var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;

            if (result is { Lost: false }) { okCount++; consecutiveLoss = 0; }
            else { consecutiveLoss++; maxConsecutiveLoss = Math.Max(maxConsecutiveLoss, consecutiveLoss); }

            var verdict = result is null
                ? $"抛异常 {thrown}"
                : result.Lost
                    ? $"失败 {result.FailureText}"
                    : $"成功 RTT={result.RttMs:F1}ms";
            Console.WriteLine($"  #{i,3}  {verdict}  （本次耗时 {elapsed}ms，连续失败 {consecutiveLoss}）");

            if (i < count) Thread.Sleep(Math.Max(0, intervalMs - elapsed));
        }

        Console.WriteLine(new string('-', 72));
        Console.WriteLine($"成功 {okCount}/{count}，最长连续失败 {maxConsecutiveLoss}");
        Console.WriteLine(okCount == count ? "结论：连续探测没有中断" : "结论：出现中断（见上面逐次结果）");
        return okCount == count ? 0 : 1;
    }

    /// <summary>
    /// 人工排查入口：用**真实窗口 + 真实探测循环**跑一段时间，每秒打印各协议累计条数
    /// 与三个开关的当前取值，用来定位"某一路探测跑着跑着就不发了"。
    ///
    /// <c>dotnet run --project .\tests\LineTester.Tests -c Release -- --icmp-window 15</c>
    /// </summary>
    private static int IcmpWindow(int seconds)
    {
        Console.WriteLine($"真实窗口连续探测：{seconds} 秒（ICMP + TCP 同链路，回环）");
        Console.WriteLine(new string('-', 72));

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "icmpwin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        var echoServer = StartEchoServer(out var echoPort);
        if (echoServer is null) { Console.WriteLine("回显服务启动失败"); return 1; }

        ConfigManager.Save(new AppConfig
        {
            ListenPort = FindBindablePort(),
            IntervalMs = 300,
            TimeoutMs = 1000,
            EnableTcp = true,
            EnableUdp = false,
            EnableIcmp = true,
            Isp = new IspConfig { Enabled = false },
            Links = { new LinkConfig { Name = "本机", Host = "127.0.0.1", Port = echoPort, Tcp = true, Udp = false, Icmp = true } }
        });

        var bad = 0;
        try
        {
            RunOnSta(() =>
            {
                var window = new MainWindow();
                InvokePrivateHandler(window, "Start_Click");

                var trend = (System.Collections.IList)GetPrivateField(window, "_trend")!;
                var rows = (System.Collections.IList)GetPrivateField(window, "_linkRows")!;
                var check = (CheckBox)window.FindName("IcmpCheck")!;
                var tcpCheck = (CheckBox)window.FindName("TcpCheck")!;

                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                var lastIcmp = 0;
                var stalledTicks = 0;

                while (DateTime.UtcNow < deadline)
                {
                    Pump(1000);

                    var all = trend.Cast<ProbeResult>().ToList();
                    var icmp = all.Count(r => r.Protocol == "ICMP");
                    var tcp = all.Count(r => r.Protocol == "TCP");

                    var liveIcmp = (bool)GetPrivateField(window, "_liveIcmp")!;
                    var liveTcp = (bool)GetPrivateField(window, "_liveTcp")!;
                    var rowIcmp = ((LinkRow)rows[0]!).Icmp;
                    var snapshotIcmp = ((LinkConfig[])GetPrivateField(window, "_liveLinks")!).FirstOrDefault()?.Icmp ?? false;
                    var trendIcmp = ((LinkConfig[])GetPrivateField(window, "_liveLinks")!).Length;
                    var settingsIcmp = ((AppConfig)GetPrivateField(window, "_settings")!).EnableIcmp;

                    Console.WriteLine($"  ICMP={icmp,3} TCP={tcp,3} | liveIcmp={liveIcmp} 勾选={check.IsChecked} " +
                                      $"行.Icmp={rowIcmp} 快照.Icmp={snapshotIcmp} 链路数={trendIcmp} 配置.EnableIcmp={settingsIcmp} | " +
                                      $"liveTcp={liveTcp} 勾选Tcp={tcpCheck.IsChecked}");

                    if (icmp == lastIcmp) stalledTicks++; else stalledTicks = 0;
                    lastIcmp = icmp;
                }

                if (stalledTicks >= 2) { bad = 1; Console.WriteLine($"  → ICMP 已连续 {stalledTicks} 秒没有新增"); }

                InvokePrivateHandler(window, "Stop_Click");
                window.Close();
            }, "ICMP 连续性诊断");
        }
        finally
        {
            echoServer.Dispose();
            ConfigManager.OverridePath = previousOverride;
            try { Directory.Delete(root, true); } catch { }
        }

        return bad;
    }

    /// <summary>
    /// 现场缺陷回归：ICMP 只发几次就停。
    ///
    /// 根因是链路快照的复制函数（当时是 MainWindow 里手写的 Clone）漏了 Icmp 字段：
    /// 启动约 1 秒后防抖定时器把 <c>_liveLinks</c> 换成副本，ICMP 就永远是 false 了，
    /// 而 TCP/UDP 字段还在，所以只有 ICMP 停——现场表现就是"ping 只发两次就不发了"。
    ///
    /// 这里用**反射**逐个字段验复制完整性：以后再加协议字段，只要复制漏了就会红，
    /// 不必再靠人记得改每一个拷贝点。
    /// </summary>
    private static void TestLinkConfigCloneIsComplete()
    {
        Section("链路配置复制：每个字段都必须被带上（ICMP 静默停止的根因回归）");

        var link = new LinkConfig();
        var props = typeof(LinkConfig).GetProperties()
            .Where(p => p is { CanRead: true, CanWrite: true })
            .ToList();
        Check("LinkConfig 有可写字段可测", props.Count >= 8, props.Count.ToString());

        // 每个字段给一个"与默认值不同"的值，这样才能看出漏拷贝
        foreach (var p in props)
        {
            object value = p.PropertyType == typeof(bool)
                ? true
                : p.PropertyType == typeof(int)
                    ? 16951
                    : "copy-" + p.Name;
            p.SetValue(link, value);
        }

        var clone = link.Clone();
        foreach (var p in props)
        {
            Eq($"复制带上 {p.Name}", p.GetValue(link)?.ToString() ?? "", p.GetValue(clone)?.ToString() ?? "");
        }

        // 逐个显式点名协议字段：反射用例若被误删，这几条仍然守着现场这个缺陷
        Check("复制带上 Icmp（现场缺陷：漏了它 → ping 只发几次就停）", clone.Icmp);
        Check("复制带上 Tcp / Udp", clone.Tcp && clone.Udp);
        Check("复制带上本地意图（LocalTcpOff / LocalUdpOff / LocalIcmpOff / LocalDisabled）",
            clone.LocalTcpOff && clone.LocalUdpOff && clone.LocalIcmpOff && clone.LocalDisabled);
        Check("复制是另一份对象（改副本不影响原件）", !ReferenceEquals(link, clone));

        clone.Icmp = false;
        Check("改副本不影响原件", link.Icmp);

        // 复制链路行：协议勾选必须整行照抄（以前手写字段也会漏 ICMP）
        var row = new LinkRow { Name = "机房A", Host = "127.0.0.1", PortText = "8800", Tcp = false, Udp = true, Icmp = true };
        var copy = row.CopyAsManual("机房A-副本");
        Check("复制链路保留 ICMP 勾选", copy.Icmp);
        Check("复制链路保留 TCP/UDP 勾选", !copy.Tcp && copy.Udp);
        Eq("复制链路用新名字", "机房A-副本", copy.Name);
        Eq("复制链路保留目标", row.Target, copy.Target);
        Check("复制链路不继承注册来源（否则同步会把它删掉）", !copy.IsRegistry && copy.NodeId.Length == 0);
    }

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

    private static void TestIcmpKeepsSending()
    {
        Section("ICMP：连续探测不中断（现场反馈“只发 2 次就不发了”）");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "icmploop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        var listenPort = FindBindablePort();
        var echoServer = StartEchoServer(out var echoPort);
        if (echoServer is null)
        {
            Check("回显服务启动", false, "找不到可绑定端口");
            ConfigManager.OverridePath = previousOverride;
            return;
        }

        ConfigManager.Save(new AppConfig
        {
            ListenPort = listenPort,
            IntervalMs = 300,
            TimeoutMs = 1000,
            EnableTcp = true,
            EnableUdp = false,
            EnableIcmp = true,
            Isp = new IspConfig { Enabled = false },
            // 同一条链路同时开 TCP 与 ICMP：用来区分"只有 ICMP 停了"和"整个探测循环停了"
            Links = { new LinkConfig { Name = "本机", Host = "127.0.0.1", Port = echoPort, Tcp = true, Udp = false, Icmp = true } }
        });

        try
        {
            RunOnSta(() =>
            {
                var window = new MainWindow();
                InvokePrivateHandler(window, "Start_Click");

                var trend = (System.Collections.IList)GetPrivateField(window, "_trend")!;
                List<ProbeResult> Of(string protocol) =>
                    trend.Cast<ProbeResult>().Where(r => r.Protocol == protocol).ToList();

                // 前段：确认确实在发
                Pump(2000);
                var icmpEarly = Of("ICMP").Count;
                var tcpEarly = Of("TCP").Count;
                Check("前 2 秒已经在发 ICMP", icmpEarly >= 2, "条数=" + icmpEarly);

                // 后段：如果"发两次就停"，这里会一条都不增加
                Pump(4500);
                var icmp = Of("ICMP");
                var tcpLate = Of("TCP").Count;

                Check("整个探测循环还活着（TCP 一直在发，用于区分是不是只有 ICMP 停了）",
                    tcpLate - tcpEarly >= 5, $"TCP 前段 {tcpEarly} 条 → 后段 {tcpLate} 条");
                Check("后 4.5 秒仍在持续发 ICMP（不是发几次就停）",
                    icmp.Count - icmpEarly >= 5, $"ICMP 前段 {icmpEarly} 条 → 后段 {icmp.Count} 条");

                var newest = icmp.Count > 0 ? icmp.Max(r => r.Timestamp) : DateTime.MinValue;
                Check("最近 1.5 秒内还收到过 ICMP 结果", (DateTime.Now - newest).TotalSeconds < 1.5,
                    $"最后一条距今 {(DateTime.Now - newest).TotalSeconds:F1} 秒");

                // 序号按"链路+协议"单调递增；跳号说明有结果被丢弃
                var seqs = icmp.Select(r => r.Seq).OrderBy(s => s).ToList();
                Check("ICMP 序号连续无跳号", seqs.Count > 0 && seqs.SequenceEqual(Enumerable.Range(1, seqs.Count).Select(i => (long)i)),
                    seqs.Count > 0 ? $"1..{seqs.Max()} 共 {seqs.Count} 条" : "无结果");

                Check("持续探测期间 ICMP 全部成功（本机 ping 通）", icmp.All(r => !r.Lost),
                    string.Join(",", icmp.Where(r => r.Lost).Select(r => r.FailureText).Distinct()));

                InvokePrivateHandler(window, "Stop_Click");
                window.Close();
            }, "ICMP 连续性断言");
        }
        finally
        {
            echoServer.Dispose();
            ConfigManager.OverridePath = previousOverride;
            try { Directory.Delete(root, true); } catch { }
        }
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

        // 只本地关掉 TCP+UDP、保留 ICMP：不能被判成"整条链路本地停测"，
        // 否则下一次同步会把 ICMP 一起关掉（LocalDisabled 原来只算了 TCP+UDP）
        var mixed = new List<LinkConfig>
        {
            new()
            {
                Name = "厦门机房", Host = "110.86.28.214", Port = 17078,
                Tcp = false, Udp = false, Icmp = true,
                Source = LinkSources.Registry, NodeId = "icmp-node",
                LocalTcpOff = true, LocalUdpOff = true, LocalIcmpOff = false, LocalDisabled = false
            }
        };

        var mixedOut = RegistrySync.Apply(store.BuildSnapshot(), "self", mixed);
        var mixedLink = mixedOut.Links.First(l => l.NodeId == "icmp-node");
        Check("只关 TCP+UDP 不算「整条链路本地停测」", !mixedLink.LocalDisabled,
            $"LocalDisabled={mixedLink.LocalDisabled} tcpOff={mixedLink.LocalTcpOff} udpOff={mixedLink.LocalUdpOff} icmpOff={mixedLink.LocalIcmpOff}");
        Check("保留的 ICMP 不会被同步关掉", mixedLink.Icmp, mixedLink.Icmp.ToString());
        Check("本地关掉的 TCP/UDP 依然被保留", !mixedLink.Tcp && !mixedLink.Udp);

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
