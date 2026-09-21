using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LineTester;

namespace LineTester.Tests;

/// <summary>
/// 零依赖自检宿主。覆盖本次修复涉及的纯逻辑与真实回环网络行为。
/// 退出码 0 = 全部通过，1 = 有失败。
/// </summary>
internal static partial class Program
{
    private static int _pass;
    private static readonly List<string> Failures = new();

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

        // 渲染真实窗口为 PNG，便于在不启动程序的情况下检查排版
        if (args.Length >= 2 && args[0] == "--render-ui")
        {
            RenderUi(args[1]);
            return Failures.Count == 0 ? 0 : 1;
        }

        // 用真接口跑一次运营商查询（人工排查用：确认这台机器能不能访问 ip-api）
        if (args.Length >= 2 && args[0] == "--isp-live")
        {
            return await IspLiveAsync(args.Skip(1).ToArray());
        }

        // 用真实窗口 + 真实接口复现"统计表里的运营商显示"（可加 --poison 预置坏缓存）
        if (args.Length >= 3 && args[0] == "--isp-window" &&
            int.TryParse(args[2], out var ispPort))
        {
            return IspWindowLive(args[1], ispPort, args.Any(a => a == "--poison"));
        }

        Console.WriteLine("LineTester 自检");
        Console.WriteLine(new string('=', 64));

        try
        {
            TestPercentile();
            TestStatusRules();
            TestStatSession();
            TestConfigManager();
            await TestCsvLogWriterAsync();
            TestFailureKinds();
            TestLinkRowValidation();
            TestRegistryProtocol();
            TestRegistryAuth();
            TestRegistryChunking();
            TestRegistryAddressRules();
            TestRegistryStore();
            TestRegistrySync();
            TestRegistryCenter();
            await TestRegistryEndToEndAsync();
            await TestRegistryNonCenterAsync();
            await TestRegistryAcceptanceAsync();
            TestRegistrarDraft();
            TestRegistryUiText();
            TestRegistryNodeRow();
            TestIpv4Box();
            TestRegistryPanelUi();
            TestRegistryMainWindowUi();
            TestRegistryLocalProtocolIntent();
            await TestRegistryDiagnosticsAsync();
            TestRegistryMultiAddress();
            TestSaveAndRestartRoundTrip();
            TestNodeNameHeader();
            TestIspCarrier();
            TestIspResponseParsing();
            TestIspCacheSelfHealing();
            TestIspLookupService();
            TestIspUi();
            TestLinkToggleWhileRunning();
            await TestRegistryCompatMatrixAsync();
            TestRepoTemplateLoads();
            await TestProbesAsync();
            TestMainWindowClear();
        }
        catch (Exception ex)
        {
            // 自检宿主自己的异常（端口耗尽、断言辅助写错等）也要以"失败"收场：
            // 否则进程以未处理异常退出，输出被截断，看不到到底跑到哪一步
            Check("自检执行过程中未抛出异常", false, ex.ToString());
        }

        Console.WriteLine(new string('=', 64));
        Console.WriteLine($"通过 {_pass}，失败 {Failures.Count}");
        foreach (var failure in Failures) Console.WriteLine("  失败：" + failure);

        return Failures.Count == 0 ? 0 : 1;
    }

    // ---------------- 断言辅助 ----------------

    private static void Section(string title) => Console.WriteLine("\n# " + title);

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine("  [PASS] " + name);
        }
        else
        {
            var line = name + (detail is null ? "" : " → " + detail);
            Failures.Add(line);
            Console.WriteLine("  [FAIL] " + line);
        }
    }

    private static void Eq<T>(string name, T expected, T actual)
        => Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"期望 [{expected}]，实际 [{actual}]");

    private static void Near(string name, double expected, double actual, double tolerance = 1e-6)
        => Check(name, Math.Abs(expected - actual) <= tolerance, $"期望 {expected}，实际 {actual}");

    private static ProbeResult Result(
        LinkConfig link, string protocol, DateTime timestamp, double? rtt = null, bool lost = false,
        long seq = 0, string probeId = "", string seenFrom = "", string failure = "") => new()
    {
        Timestamp = timestamp,
        LinkName = link.Name,
        Host = link.Host,
        Port = link.Port,
        Protocol = protocol,
        RttMs = lost ? null : rtt,
        SetupMs = lost ? null : 1.0,
        Lost = lost,
        Seq = seq,
        ProbeId = probeId,
        SeenFrom = seenFrom,
        Failure = failure
    };

    // ---------------- 1. 百分位 ----------------

    private static void TestPercentile()
    {
        Section("Statistics.Percentile");

        Check("空集合返回 null", Statistics.Percentile(Array.Empty<double>(), 0.5) is null);
        Eq("单元素返回其本身", 42.0, Statistics.Percentile(new double[] { 42 }, 0.95)!.Value);

        var hundred = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        Near("1..100 的 P50 = 50.5", 50.5, Statistics.Percentile(hundred, 0.50)!.Value);
        Near("1..100 的 P95 = 95.05", 95.05, Statistics.Percentile(hundred, 0.95)!.Value);
        Near("1..100 的 P99 = 99.01", 99.01, Statistics.Percentile(hundred, 0.99)!.Value);
        Near("P0 = 最小值", 1, Statistics.Percentile(hundred, 0)!.Value);
        Near("P100 = 最大值", 100, Statistics.Percentile(hundred, 1)!.Value);
        Near("乱序输入结果不变", 3, Statistics.Percentile(new double[] { 5, 1, 3, 2, 4 }, 0.5)!.Value);
        Near("[10,20,30,40,50] 的 P95 = 48", 48, Statistics.Percentile(new double[] { 10, 20, 30, 40, 50 }, 0.95)!.Value);
        Near("亚毫秒小数不被截断", 0.412, Statistics.Percentile(new double[] { 0.412 }, 0.5)!.Value, 1e-9);
    }

    // ---------------- 2. 状态判定 ----------------

    private static void TestStatusRules()
    {
        Section("StatusRules.Compute");

        Eq("连续丢包达阈值 → 中断",
            StatusRules.Interrupted, StatusRules.Compute(100, 5, 3, 10, 300, 1.0, 3));
        Eq("丢包率 2% ≥ 1% → 有丢包",
            StatusRules.PacketLoss, StatusRules.Compute(1000, 20, 0, 10, 300, 1.0, 3));
        Eq("窗口内仅 1/3600 丢包 → 正常（不再永久报警）",
            StatusRules.Normal, StatusRules.Compute(3600, 1, 0, 10, 300, 1.0, 3));
        Eq("丢包率低于阈值但延迟超标 → 高延迟",
            StatusRules.HighLatency, StatusRules.Compute(3600, 1, 0, 400, 300, 1.0, 3));
        Eq("无丢包且延迟超标 → 高延迟",
            StatusRules.HighLatency, StatusRules.Compute(100, 0, 0, 350, 300, 1.0, 3));
        Eq("一切正常 → 正常",
            StatusRules.Normal, StatusRules.Compute(100, 0, 0, 10, 300, 1.0, 3));
        Eq("中断优先级高于丢包率",
            StatusRules.Interrupted, StatusRules.Compute(10, 10, 3, 999, 300, 1.0, 3));
        Eq("无样本 → 正常",
            StatusRules.Normal, StatusRules.Compute(0, 0, 0, null, 300, 1.0, 3));
    }

    // ---------------- 3. 统计会话 ----------------

    private static void TestStatSession()
    {
        Section("StatSession");

        var link = new LinkConfig { Name = "机房B", Host = "10.0.0.1", Port = 9001 };
        var key = $"{link.Name}|{link.Host}|{link.Port}|TCP";
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);

        var session = new StatSession();
        session.Configure(new AppConfig
        {
            StatWindowMinutes = 60,
            WarningRttMs = 300,
            LossWarnPercent = 1.0,
            ConsecutiveLossWarn = 3
        });

        for (var i = 0; i < 10; i++) session.Add(Result(link, "TCP", t0.AddSeconds(i), rtt: 10 + i));

        var rows = session.BuildRows();
        Eq("只生成一行", 1, rows.Count);
        Eq("发送计数", 10, rows[0].Sent);
        Eq("丢包计数", 0, rows[0].Lost);
        Eq("丢包率", "0.00%", rows[0].LossRate);
        Eq("平均 RTT（10..19 → 14.5）", "14.5 ms", rows[0].Avg);
        Eq("P50 为 14.5", "14.5 ms", rows[0].P50);
        Eq("目标串含端口", "10.0.0.1:9001", rows[0].Target);
        Eq("状态为正常", StatusRules.Normal, rows[0].Status);
        Eq("行主键稳定", key, rows[0].Key);

        Check("正常结果不算异常", !session.IsAnomaly(Result(link, "TCP", t0.AddSeconds(11), rtt: 50)));
        Check("RTT 达到阈值算异常", session.IsAnomaly(Result(link, "TCP", t0.AddSeconds(12), rtt: 300)));
        Check("丢包算异常", session.IsAnomaly(Result(link, "TCP", t0.AddSeconds(13), lost: true)));

        // 统计窗口滑动（按结果自身时间戳裁剪，行为确定可测）
        var trimmed = new StatSession();
        trimmed.Configure(new AppConfig { StatWindowMinutes = 1 });
        trimmed.Add(Result(link, "TCP", t0, rtt: 10));
        trimmed.Add(Result(link, "TCP", t0.AddMinutes(2), rtt: 20));
        Eq("超出窗口的样本被剔除", 1, trimmed.TotalSamples);

        // 连续丢包
        var lossy = new StatSession();
        lossy.Configure(new AppConfig { ConsecutiveLossWarn = 3 });
        lossy.Add(Result(link, "TCP", t0, rtt: 5));
        Eq("成功一次后连续丢包为 0", 0, lossy.GetConsecutiveLoss(key));
        lossy.Add(Result(link, "TCP", t0.AddSeconds(1), lost: true));
        lossy.Add(Result(link, "TCP", t0.AddSeconds(2), lost: true));
        Eq("连续丢包累计到 2", 2, lossy.GetConsecutiveLoss(key));
        lossy.Add(Result(link, "TCP", t0.AddSeconds(3), rtt: 5));
        Eq("恢复成功后连续丢包归零", 0, lossy.GetConsecutiveLoss(key));
        lossy.Add(Result(link, "TCP", t0.AddSeconds(4), lost: true));
        lossy.Add(Result(link, "TCP", t0.AddSeconds(5), lost: true));
        lossy.Add(Result(link, "TCP", t0.AddSeconds(6), lost: true));
        Eq("连续 3 次丢包 → 中断", StatusRules.Interrupted, lossy.BuildRows()[0].Status);

        // 清除 / 会话号
        var epochBefore = session.Epoch;
        session.Clear();
        Eq("清除使会话号 +1", epochBefore + 1, session.Epoch);
        Check("清除前发起的旧结果被拒绝", !session.Accept(epochBefore));
        Check("清除后发起的结果被接受", session.Accept(session.Epoch));
        Eq("清除后样本归零", 0, session.TotalSamples);
        Eq("清除后统计行为空", 0, session.BuildRows().Count);
        Eq("清除后跟踪的键清空", 0, session.TrackedKeys);

        // TCP / UDP 分行
        var two = new StatSession();
        two.Add(Result(link, "TCP", t0, rtt: 5));
        two.Add(Result(link, "UDP", t0, rtt: 6));
        Eq("TCP 与 UDP 分行统计", 2, two.BuildRows().Count);

        // 对端看到的本端源地址：保留最近一次成功探测的值
        var seen = new StatSession();
        seen.Configure(new AppConfig());
        seen.Add(Result(link, "TCP", t0, rtt: 5, seenFrom: "203.0.113.7:50001"));
        Eq("统计行带出对端看到的本端源地址", "203.0.113.7:50001", seen.BuildRows()[0].SeenFrom);
        Eq("可直接查询最近一次源地址", "203.0.113.7:50001", seen.LastSeenFrom(key));

        seen.Add(Result(link, "TCP", t0.AddSeconds(1), lost: true));
        Eq("丢包不会抹掉已知的源地址", "203.0.113.7:50001", seen.BuildRows()[0].SeenFrom);

        seen.Add(Result(link, "TCP", t0.AddSeconds(2), rtt: 6, seenFrom: "203.0.113.8:50002"));
        Eq("源地址变化会跟着更新（多出口 / 运营商 NAT 池）", "203.0.113.8:50002", seen.BuildRows()[0].SeenFrom);

        seen.Clear();
        Eq("清除后源地址记录归零", "", seen.LastSeenFrom(key));

        // 失败原因：只反映“当前”状态，成功即清零
        var failing = new StatSession();
        failing.Configure(new AppConfig());
        failing.Add(Result(link, "TCP", t0, lost: true, failure: FailureKinds.Timeout));
        Eq("统计行显示当前失败原因", "无响应（超时）", failing.BuildRows()[0].LastFailure);
        Eq("可按链路查询失败原因", "无响应（超时）", failing.LastFailure(key));

        failing.Add(Result(link, "TCP", t0.AddSeconds(1), lost: true, failure: FailureKinds.Refused));
        Eq("失败原因跟随最新一次失败更新", "连接被拒绝（端口无监听）", failing.BuildRows()[0].LastFailure);

        failing.Add(Result(link, "TCP", t0.AddSeconds(2), rtt: 5));
        Eq("恢复成功后失败原因清空（避免历史偶发误导）", "-", failing.BuildRows()[0].LastFailure);
    }

    // ---------------- 4. 配置 ----------------

    private static void TestConfigManager()
    {
        Section("ConfigManager");

        var dir = Path.Combine(AppContext.BaseDirectory, ".testtmp", "cfg_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "appsettings.json");
        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = path;

        try
        {
            var fresh = ConfigManager.Load(out var loadError);
            Check("首次加载无错误", loadError is null, loadError);
            Check("自动创建配置文件", File.Exists(path));
            Eq("默认监听端口", 8800, fresh.ListenPort);
            Eq("新增字段默认丢包率阈值", 1.0, fresh.LossWarnPercent);
            Eq("新增字段默认连续丢包阈值", 3, fresh.ConsecutiveLossWarn);
            Eq("新增字段默认勾选 TCP", true, fresh.EnableTcp);
            Eq("新增字段默认勾选 UDP", true, fresh.EnableUdp);
            Eq("Registrar 段默认不启用（升级后行为不变）", false, fresh.Registrar.Enabled);
            Eq("Registrar 默认心跳周期", 60, fresh.Registrar.HeartbeatSeconds);
            Eq("Registrar 默认同步周期", 300, fresh.Registrar.SyncSeconds);
            Eq("Registrar 默认参与网状", true, fresh.Registrar.Distributable);
            Eq("Registrar 默认宣告 TCP 与 UDP 均可被探测",
                true, fresh.Registrar.AdvertiseTcp && fresh.Registrar.AdvertiseUdp);
            Eq("链路默认来源为本地手工", LinkSources.Manual, fresh.Links[0].Source);
            Eq("链路默认未停测", false, fresh.Links[0].LocalDisabled);
            Check("未启用注册时，空 Registrar 字段不影响校验",
                ConfigManager.Validate(new AppConfig { Links = new List<LinkConfig>() }, out _));

            fresh.Links = new List<LinkConfig> { new() { Name = "机房B", Host = "10.1.1.1", Port = 9001 } };
            fresh.LossWarnPercent = 0.5;
            fresh.EnableUdp = false;

            // Registrar 往返（启用后必须填齐本端与中心信息）
            fresh.Registrar.Enabled = true;
            fresh.Registrar.ServeAsCenter = true;
            fresh.Registrar.NodeId = "3f2a9c1d4b5e6f708192a3b4c5d6e7f8";
            fresh.Registrar.NodeName = "机房A";
            fresh.Registrar.PublicHost = "203.0.113.10";
            fresh.Registrar.PublicPort = 50001;
            fresh.Registrar.CenterHost = "203.0.113.1";
            fresh.Registrar.CenterPort = 50000;
            fresh.Registrar.Secret = "s3cret";
            fresh.Registrar.AdvertiseTcp = false;
            fresh.Registrar.HeartbeatSeconds = 30;
            fresh.Links[0].Source = LinkSources.Registry;
            fresh.Links[0].NodeId = "aa11bb22";
            fresh.Links[0].LocalDisabled = true;

            Check("填齐后注册配置校验通过",
                ConfigManager.Validate(fresh, out var enabledDetail), enabledDetail);

            ConfigManager.Save(fresh);

            var raw = File.ReadAllText(path, Encoding.UTF8);
            Check("中文不被转义为 \\uXXXX", raw.Contains("机房B") && !raw.Contains("\\u"),
                raw.Replace("\r", "").Replace("\n", " "));
            Check("原子写入不残留 .tmp 文件", !File.Exists(path + ".tmp"));

            // 配置文件只能出现契约内的字段：公开的只读派生属性（如 Key / Target）
            // 也会被 System.Text.Json 序列化进去，现场看到会一头雾水。
            using (var doc = JsonDocument.Parse(raw))
            {
                var actualTop = doc.RootElement.EnumerateObject()
                    .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                var expectedTop = new[]
                {
                    "ConsecutiveLossWarn", "EnableTcp", "EnableUdp", "IntervalMs", "Isp", "Links", "ListenPort",
                    "LossWarnPercent", "Registrar", "StatWindowMinutes", "TimeoutMs", "WarningRttMs"
                };
                Check("顶层字段与配置契约完全一致（无派生字段泄漏）",
                    actualTop.SequenceEqual(expectedTop), string.Join(",", actualTop));

                var actualLink = doc.RootElement.GetProperty("Links")[0].EnumerateObject()
                    .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                var expectedLink = new[] { "Host", "LocalDisabled", "LocalTcpOff", "LocalUdpOff", "Name", "NodeId", "Port", "Source", "Tcp", "Udp" };
                Check("链路字段与配置契约完全一致（不含 Key/Target 等派生字段）",
                    actualLink.SequenceEqual(expectedLink), string.Join(",", actualLink));
            }

            var reloaded = ConfigManager.Load(out var reloadError);
            Check("往返加载无错误", reloadError is null, reloadError);
            Eq("链路名往返保留", "机房B", reloaded.Links[0].Name);
            Eq("小数阈值往返保留", 0.5, reloaded.LossWarnPercent);
            Eq("协议开关往返保留（UDP 关闭）", false, reloaded.EnableUdp);
            Eq("协议开关往返保留（TCP 开启）", true, reloaded.EnableTcp);
            Eq("链路数量往返保留", 1, reloaded.Links.Count);

            Eq("Registrar 启用状态往返保留", true, reloaded.Registrar.Enabled);
            Eq("Registrar 节点名往返保留", "机房A", reloaded.Registrar.NodeName);
            Eq("Registrar 本端外网地址往返保留", "203.0.113.10", reloaded.Registrar.PublicHost);
            Eq("Registrar 本端外网端口往返保留", 50001, reloaded.Registrar.PublicPort);
            Eq("Registrar 中心地址往返保留", "203.0.113.1", reloaded.Registrar.CenterHost);
            Eq("Registrar 密钥往返保留", "s3cret", reloaded.Registrar.Secret);
            Eq("Registrar 可被探测协议往返保留（TCP 关闭）", false, reloaded.Registrar.AdvertiseTcp);
            Eq("Registrar 心跳周期往返保留", 30, reloaded.Registrar.HeartbeatSeconds);
            Eq("链路来源往返保留", LinkSources.Registry, reloaded.Links[0].Source);
            Eq("链路来源节点 id 往返保留", "aa11bb22", reloaded.Links[0].NodeId);
            Eq("本地停测标记往返保留", true, reloaded.Links[0].LocalDisabled);

            var commented =
                "{\n  // 现场加的注释\n  \"ListenPort\": 9100,\n" +
                "  \"Links\": [ { \"Name\": \"A\", \"Host\": \"1.2.3.4\", \"Port\": 9001 }, ],\n}";
            File.WriteAllText(path, commented, new UTF8Encoding(false));
            var tolerant = ConfigManager.Load(out var tolerantError);
            Check("容忍注释与尾逗号", tolerantError is null, tolerantError);
            Eq("读到注释后的端口", 9100, tolerant.ListenPort);

            // 目标文件被瞬时独占占用时，原子替换必须自动重试而不是直接失败
            // （Windows 上索引器/杀毒软件会短暂持有文件，实测偶发“拒绝访问”）
            var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var releaser = Task.Run(async () =>
            {
                await Task.Delay(150).ConfigureAwait(false);
                hold.Dispose();
            });

            try
            {
                ConfigManager.Save(new AppConfig { ListenPort = 9200, Links = new List<LinkConfig>() });
                Check("目标文件被瞬时占用时保存自动重试成功", true);
            }
            catch (Exception ex)
            {
                Check("目标文件被瞬时占用时保存自动重试成功", false, ex.GetType().Name + ": " + ex.Message);
            }

            releaser.Wait();
            Check("重试后配置内容已更新", File.ReadAllText(path).Contains("9200"));
            Check("重试后不残留 .tmp 文件", !File.Exists(path + ".tmp"));

            var broken = "{ \"ListenPort\": 9200, ";
            File.WriteAllText(path, broken, new UTF8Encoding(false));
            var onBroken = ConfigManager.Load(out var brokenError);
            Check("JSON 语法错误被报告", brokenError is not null);
            Check("语法错误时返回默认值以便界面可用", onBroken.ListenPort == 8800);
            Eq("语法错误时不覆盖原文件", broken, File.ReadAllText(path));

            Check("监听端口越界被拒绝",
                !ConfigManager.Validate(new AppConfig { ListenPort = 0 }, out _));
            Check("测试间隔过小被拒绝",
                !ConfigManager.Validate(new AppConfig { IntervalMs = 10 }, out _));
            Check("链路端口越界被拒绝",
                !ConfigManager.Validate(
                    new AppConfig { Links = { new LinkConfig { Name = "A", Host = "h", Port = 70000 } } }, out _));
            Check("空 Host 被拒绝",
                !ConfigManager.Validate(
                    new AppConfig { Links = { new LinkConfig { Name = "A", Host = "", Port = 1 } } }, out _));
            Check("两个协议都不勾选也合法（该链路不主动探测）",
                ConfigManager.Validate(
                    new AppConfig { Links = { new LinkConfig { Name = "A", Host = "h", Port = 1, Tcp = false, Udp = false } } },
                    out _));
            Check("纯服务端配置（没有任何链路）也合法",
                ConfigManager.Validate(new AppConfig { Links = new List<LinkConfig>() }, out _));
            Check("合法配置通过校验",
                ConfigManager.Validate(
                    new AppConfig { Links = { new LinkConfig { Name = "A", Host = "h", Port = 1 } } }, out _));
        }
        finally
        {
            ConfigManager.OverridePath = previousOverride;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------- 5. 日志 ----------------

    private static async Task TestCsvLogWriterAsync()
    {
        Section("CsvLogWriter（按天滚动）");

        var dir = Path.Combine(AppContext.BaseDirectory, ".testtmp", "logs_" + Guid.NewGuid().ToString("N"));
        var link = new LinkConfig { Name = "机房B", Host = "10.0.0.1", Port = 9001 };
        var t0 = new DateTime(2026, 1, 1, 8, 0, 0);

        var writer = new CsvLogWriter(dir);
        writer.Enqueue(Result(link, "TCP", t0, rtt: 12.3456, seq: 1, probeId: "a1b2c3d4", seenFrom: "203.0.113.7:50001"));
        writer.Enqueue(Result(link, "UDP", t0.AddSeconds(1), lost: true, seq: 7, probeId: "d4e5f6a7",
            failure: FailureKinds.Timeout));
        writer.Enqueue(Result(link, "TCP", t0.AddSeconds(2), rtt: 0.412, seq: 2, probeId: "0a0b0c0d"));
        writer.Dispose(); // 完成落盘

        Eq("目录名按天", "2026-01-01", CsvLogWriter.FolderNameFor(t0));
        var file = Path.Combine(dir, "2026-01-01", "机房B_10.0.0.1_9001.csv");
        Eq("文件路径按天分目录", file, writer.FilePathFor(Result(link, "TCP", t0)));
        Check("按天目录下的日志文件已生成", File.Exists(file), file);
        Check("根目录下不再直接堆日志文件（旧布局已不再产生）",
            !File.Exists(Path.Combine(dir, "机房B_10.0.0.1_9001.csv")));

        if (File.Exists(file))
        {
            var lines = File.ReadAllLines(file, Encoding.UTF8);
            Eq("表头 + 3 行数据", 4, lines.Length);
            Eq("表头内容一致", CsvLogWriter.Header, lines[0]);
            Check("表头含 setup_ms", lines[0].Contains("setup_ms"), lines[0]);
            Check("表头以 seq,probe_id,seen_from,failure 结尾",
                lines[0].EndsWith(",seq,probe_id,seen_from,failure"), lines[0]);
            Eq("成功行含序号、探测ID、源地址，失败原因为空",
                "2026-01-01 08:00:00.000,10.0.0.1,9001,TCP,12.346,0,1.000,1,a1b2c3d4,203.0.113.7:50001,", lines[1]);
            Eq("丢包行带失败原因 token",
                "2026-01-01 08:00:01.000,10.0.0.1,9001,UDP,,1,,7,d4e5f6a7,,timeout", lines[2]);
            Eq("亚毫秒 RTT 不被截断为 0",
                "2026-01-01 08:00:02.000,10.0.0.1,9001,TCP,0.412,0,1.000,2,0a0b0c0d,,", lines[3]);

            var appender = new CsvLogWriter(dir);
            appender.Enqueue(Result(link, "TCP", t0.AddSeconds(3), rtt: 1.0, seq: 3, probeId: "ffeeddcc"));
            appender.Dispose();

            var afterAppend = File.ReadAllLines(file, Encoding.UTF8);
            Eq("同一天内追加而非覆盖", 5, afterAppend.Length);
            Eq("追加行序号延续", "3", afterAppend[4].Split(',')[7]);
        }

        Check("后台写入无错误", writer.LastError is null, writer.LastError);

        // ---- 按天滚动 ----
        var roller = new CsvLogWriter(dir);
        var day1 = new DateTime(2026, 3, 1, 23, 59, 59);
        var day2 = day1.AddSeconds(2); // 跨天

        for (var i = 0; i < 3; i++) roller.Enqueue(Result(link, "TCP", day1.AddSeconds(-i), rtt: 1 + i, seq: 10 + i));
        Check("等待日志落盘成功", await roller.WaitForIdleAsync(TimeSpan.FromSeconds(5)));
        Eq("同一天内只开一个文件句柄", 1, roller.OpenFileCount);
        Check("跨天前只有 day1 目录",
            Directory.Exists(roller.DirectoryFor(day1)) && !Directory.Exists(roller.DirectoryFor(day2)));

        roller.Enqueue(Result(link, "TCP", day2, rtt: 2.0, seq: 20));
        roller.Enqueue(Result(link, "TCP", day2.AddSeconds(1), rtt: 2.5, seq: 21));
        Check("跨天后等待日志落盘成功", await roller.WaitForIdleAsync(TimeSpan.FromSeconds(5)));
        Eq("跨天后句柄没有累积（旧的一天已关闭）", 1, roller.OpenFileCount);
        roller.Dispose();

        var day1File = Path.Combine(dir, "2026-03-01", "机房B_10.0.0.1_9001.csv");
        var day2File = Path.Combine(dir, "2026-03-02", "机房B_10.0.0.1_9001.csv");
        Check("跨天生成新一天的目录与文件", File.Exists(day1File) && File.Exists(day2File), day2File);
        Eq("day1 文件：表头 + 3 行", 4, File.ReadAllLines(day1File, Encoding.UTF8).Length);
        Eq("day2 文件：表头 + 2 行", 3, File.ReadAllLines(day2File, Encoding.UTF8).Length);
        Check("每个按天文件都自带表头",
            File.ReadAllLines(day1File, Encoding.UTF8)[0] == CsvLogWriter.Header &&
            File.ReadAllLines(day2File, Encoding.UTF8)[0] == CsvLogWriter.Header);
        Check("跨天后的行落在新文件里",
            File.ReadAllLines(day2File, Encoding.UTF8)[1].Contains("2026-03-02 00:00:01.000"),
            File.ReadAllLines(day2File, Encoding.UTF8)[1]);

        // ---- 链路名可能来自界面输入，必须保证拼出来的路径不会跑出日志根目录 ----
        var hostile = new LinkConfig { Name = "../../evil", Host = "1.2.3.4", Port = 1 };
        var hostilePath = roller.FilePathFor(Result(hostile, "TCP", t0));
        Check("恶意链路名不会越出日志根目录",
            Path.GetFullPath(hostilePath).StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            hostilePath);

        try { Directory.Delete(dir, true); } catch { }
    }

    // ---------------- 6. 真实回环探测 ----------------

    private static async Task TestProbesAsync()
    {
        Section("ProbeClient / ProbeServer（真实回环端到端）");

        var port = FindBindablePort();
        var server = new ProbeServer(port);

        try
        {
            server.Start();
            Check("监听端口成功", true);
        }
        catch (Exception ex)
        {
            Check("监听端口成功", false, ex.Message);
            return;
        }

        try
        {
            Console.WriteLine($"  [INFO] 监听端口 {port}，双栈={server.DualStack}");

            var client = new ProbeClient();
            var link = new LinkConfig { Name = "本机", Host = "127.0.0.1", Port = port };

            var tcp = await client.ProbeAsync(link, "TCP", 1000, CancellationToken.None);
            Check("TCP 探测成功", tcp is { Lost: false }, tcp?.Info);
            Check("TCP 记录到 RTT", tcp?.RttMs is >= 0, tcp?.RttMs?.ToString());
            Check("TCP 记录到建连耗时", tcp?.SetupMs is >= 0, tcp?.SetupMs?.ToString());

            var udp = await client.ProbeAsync(link, "UDP", 1000, CancellationToken.None);
            Check("UDP 探测成功", udp is { Lost: false }, udp?.Info);
            Check("UDP 记录到 RTT", udp?.RttMs is >= 0, udp?.RttMs?.ToString());

            // 主机名解析（IPv4 优先）也应命中双栈监听
            var byName = await client.ProbeAsync(
                new LinkConfig { Name = "本机名", Host = "localhost", Port = port }, "TCP", 1000, CancellationToken.None);
            Check("主机名 localhost 探测成功（IPv4 优先解析）", byName is { Lost: false }, byName?.Info);

            // 解析缓存：第二次探测不应再来一次 DNS
            var cached = await client.ProbeAsync(
                new LinkConfig { Name = "本机名", Host = "localhost", Port = port }, "TCP", 1000, CancellationToken.None);
            Check("解析缓存后仍可探测", cached is { Lost: false }, cached?.Info);

            // 未监听端口 → 计为丢包
            var deadPort = FindBindablePort();
            var dead = new LinkConfig { Name = "死端口", Host = "127.0.0.1", Port = deadPort };
            var deadTcp = await client.ProbeAsync(dead, "TCP", 4000, CancellationToken.None);
            Check("未监听端口 TCP 计为丢包", deadTcp is { Lost: true }, deadTcp?.Info);
            var deadUdp = await client.ProbeAsync(dead, "UDP", 300, CancellationToken.None);
            Check("未监听端口 UDP 计为丢包", deadUdp is { Lost: true }, deadUdp?.Info);

            // ---- 失败原因分类：这是现场区分“映射缺失”与“端口没服务”的关键 ----
            // 注：Windows 上被拒绝（RST）大约要 2 秒才返回，所以这里给了 4 秒预算；
            // 超时预算设得太小会把“被拒绝”误报成“无响应”。
            Eq("TCP 连到无监听端口 → 被拒绝（说明网络路径是通的）",
                FailureKinds.Refused, deadTcp?.Failure);
            Check("被拒绝的文案点明“端口无监听”",
                deadTcp?.Info.Contains("端口无监听") == true, deadTcp?.Info);
            Check("UDP 打到无监听端口 → 判定为 ICMP 端口不可达或超时",
                deadUdp?.Failure is FailureKinds.IcmpUnreachable or FailureKinds.Timeout, deadUdp?.Failure);
            Eq("成功探测没有失败原因", FailureKinds.None, tcp?.Failure);
            Eq("成功探测没有失败原因（UDP）", FailureKinds.None, udp?.Failure);

            // TCP：对端读完探测包后正常关闭（干净的 FIN）→ 对端提前关闭连接
            using (var closingTcp = new TcpListener(IPAddress.Loopback, 0))
            {
                closingTcp.Start();
                var closingPort = ((IPEndPoint)closingTcp.LocalEndpoint).Port;
                var closingTask = Task.Run(async () =>
                {
                    try
                    {
                        using var accepted = await closingTcp.AcceptTcpClientAsync();
                        using var stream = accepted.GetStream();
                        var buffer = new byte[4096];
                        await stream.ReadAsync(buffer); // 必须先把数据读掉，否则关闭时会发 RST
                    }
                    catch { }
                });

                var closedEarly = await client.ProbeAsync(
                    new LinkConfig { Name = "秒关", Host = "127.0.0.1", Port = closingPort },
                    "TCP", 900, CancellationToken.None);
                await closingTask;

                Eq("TCP 对端读完后关闭 → 失败原因是“对端提前关闭连接”",
                    FailureKinds.ClosedEarly, closedEarly?.Failure);
            }

            // TCP：对端没读就把连接关掉（会发 RST）→ 连接被重置
            using (var abortTcp = new TcpListener(IPAddress.Loopback, 0))
            {
                abortTcp.Start();
                var abortPort = ((IPEndPoint)abortTcp.LocalEndpoint).Port;
                var abortTask = Task.Run(async () =>
                {
                    try
                    {
                        using var accepted = await abortTcp.AcceptTcpClientAsync();
                        // 不读就关闭：接收缓冲区里有未读数据，Windows 会回 RST
                    }
                    catch { }
                });

                var aborted = await client.ProbeAsync(
                    new LinkConfig { Name = "硬关", Host = "127.0.0.1", Port = abortPort },
                    "TCP", 900, CancellationToken.None);
                await abortTask;

                Eq("TCP 对端未读就断开 → 失败原因是“连接被重置”",
                    FailureKinds.Reset, aborted?.Failure);
            }

            // TCP：回包长度相同但内容不同
            using (var rogueTcp = new TcpListener(IPAddress.Loopback, 0))
            {
                rogueTcp.Start();
                var rogueTcpPort = ((IPEndPoint)rogueTcp.LocalEndpoint).Port;
                var rogueTcpTask = Task.Run(async () =>
                {
                    try
                    {
                        using var accepted = await rogueTcp.AcceptTcpClientAsync();
                        using var stream = accepted.GetStream();
                        var buffer = new byte[4096];
                        var n = await stream.ReadAsync(buffer);
                        var wrong = new byte[n];
                        Array.Fill(wrong, (byte)'x');
                        await stream.WriteAsync(wrong);
                        await Task.Delay(300);
                    }
                    catch { }
                });

                var mismatched = await client.ProbeAsync(
                    new LinkConfig { Name = "回包不符", Host = "127.0.0.1", Port = rogueTcpPort },
                    "TCP", 900, CancellationToken.None);
                await Task.WhenAny(rogueTcpTask, Task.Delay(400));

                Eq("回包内容不匹配 → 失败原因是“回包内容不匹配”",
                    FailureKinds.PayloadMismatch, mismatched?.Failure);
            }

            // UDP：端口开放但从不回包 → 只可能是超时
            using (var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                var silentPort = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
                var timedOut = await client.ProbeAsync(
                    new LinkConfig { Name = "静默", Host = "127.0.0.1", Port = silentPort },
                    "UDP", 300, CancellationToken.None);
                Eq("UDP 对端不回包 → 失败原因是“无响应（超时）”",
                    FailureKinds.Timeout, timedOut?.Failure);
            }

            // 探测序号：随包发出、按“链路 + 协议”独立递增
            var seqLink = new LinkConfig { Name = "序号", Host = "127.0.0.1", Port = port };
            var s1 = await client.ProbeAsync(seqLink, "TCP", 1000, CancellationToken.None);
            var s2 = await client.ProbeAsync(seqLink, "TCP", 1000, CancellationToken.None);
            var s3 = await client.ProbeAsync(seqLink, "UDP", 1000, CancellationToken.None);
            var s4 = await client.ProbeAsync(seqLink, "TCP", 1000, CancellationToken.None);

            Check("该链路首次探测序号为 1", s1 is { Seq: 1 }, s1?.Seq.ToString());
            Check("同链路同协议序号递增到 2", s2 is { Seq: 2 }, s2?.Seq.ToString());
            Check("UDP 序号独立计数（同为 1）", s3 is { Seq: 1 }, s3?.Seq.ToString());
            Check("TCP 计数不受 UDP 影响（继续到 3）", s4 is { Seq: 3 }, s4?.Seq.ToString());
            Check("探测ID 为 32 位十六进制",
                s1 is not null && s1.ProbeId.Length == 32 && s1.ProbeId.All(Uri.IsHexDigit), s1?.ProbeId);
            Check("丢包结果同样带序号与探测ID",
                deadTcp is { Lost: true } && deadTcp.Seq > 0 && deadTcp.ProbeId.Length == 32,
                $"seq={deadTcp?.Seq} id={deadTcp?.ProbeId}");

            // 直接接住探测包，确认报文里真的带上了序号与探测ID
            using (var capture = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                var capturePort = ((IPEndPoint)capture.Client.LocalEndPoint!).Port;
                byte[]? capturedBytes = null;
                var responder = Task.Run(async () =>
                {
                    try
                    {
                        var query = await capture.ReceiveAsync();
                        capturedBytes = query.Buffer;
                        await capture.SendAsync(query.Buffer, query.Buffer.Length, query.RemoteEndPoint);
                    }
                    catch { }
                });

                var captureLink = new LinkConfig { Name = "抓包", Host = "127.0.0.1", Port = capturePort };
                var captured = await client.ProbeAsync(captureLink, "UDP", 1000, CancellationToken.None);
                await responder;

                var text = capturedBytes is null ? "" : Encoding.UTF8.GetString(capturedBytes);
                Check("探测报文以“序号|”开头",
                    captured is not null && text.StartsWith(captured.Seq + "|", StringComparison.Ordinal), text);
                Check("探测报文含本次探测ID",
                    captured is not null && text.Contains(captured.ProbeId, StringComparison.Ordinal), text);
                Check("探测报文以 v2 标记结尾（请求回显源地址）",
                    text.EndsWith("|v2", StringComparison.Ordinal), text);
                Eq("报文格式为 序号|探测ID|v2", 3, text.Split('|').Length);
            }

            // ---- 对端回显“它看到的本端源地址” ----
            var seenLink = new LinkConfig { Name = "源地址", Host = "127.0.0.1", Port = port };
            var seenTcp = await client.ProbeAsync(seenLink, "TCP", 1000, CancellationToken.None);
            var seenUdp = await client.ProbeAsync(seenLink, "UDP", 1000, CancellationToken.None);

            Check("TCP 记录了本端源地址", seenTcp is { Lost: false } && seenTcp.SeenFrom.StartsWith("127.0.0.1:"),
                seenTcp?.SeenFrom);
            Check("UDP 记录了本端源地址", seenUdp is { Lost: false } && seenUdp.SeenFrom.StartsWith("127.0.0.1:"),
                seenUdp?.SeenFrom);
            Check("源地址带回显的端口",
                seenTcp is not null && int.TryParse(seenTcp.SeenFrom.Split(':').Last(), out var seenPort)
                    && seenPort is > 0 and <= 65535, seenTcp?.SeenFrom);

            var seenI6 = await client.ProbeAsync(
                new LinkConfig { Name = "源地址v6", Host = "::1", Port = port }, "TCP", 1000, CancellationToken.None);
            Check("IPv6 源地址用方括号形式", seenI6 is { Lost: false } && seenI6.SeenFrom.StartsWith("[::1]:"),
                seenI6?.SeenFrom);

            // ---- 版本兼容：新被动端必须能被老主动端(逐字节比对)接受 ----
            using (var oldClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                oldClient.Client.ReceiveTimeout = 2000;
                var legacyProbe = Encoding.UTF8.GetBytes("45|deadbeefdeadbeefdeadbeefdeadbeef");
                await oldClient.SendAsync(legacyProbe, legacyProbe.Length, new IPEndPoint(IPAddress.Loopback, port));
                var legacyReply = await oldClient.ReceiveAsync();

                Check("老格式探测（不带 v2 标记）得到逐字节原样回显",
                    legacyReply.Buffer.AsSpan().SequenceEqual(legacyProbe),
                    Encoding.UTF8.GetString(legacyReply.Buffer));
            }

            // ---- 版本兼容：老被动端(只原样回显) 不能导致误判丢包 ----
            using (var legacyTcp = new TcpListener(IPAddress.Loopback, 0))
            using (var legacyUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                legacyTcp.Start();
                var legacyTcpPort = ((IPEndPoint)legacyTcp.LocalEndpoint).Port;
                var legacyUdpPort = ((IPEndPoint)legacyUdp.Client.LocalEndPoint!).Port;

                // 老版本被动端：读到什么就回什么，TCP 回完即关（不追加任何信息）
                var legacyTcpTask = Task.Run(async () =>
                {
                    try
                    {
                        using var accepted = await legacyTcp.AcceptTcpClientAsync();
                        using var stream = accepted.GetStream();
                        var buffer = new byte[4096];
                        var n = await stream.ReadAsync(buffer);
                        if (n > 0) await stream.WriteAsync(buffer.AsMemory(0, n));
                    }
                    catch { }
                });

                var legacyUdpTask = Task.Run(async () =>
                {
                    try
                    {
                        var query = await legacyUdp.ReceiveAsync();
                        await legacyUdp.SendAsync(query.Buffer, query.Buffer.Length, query.RemoteEndPoint);
                    }
                    catch { }
                });

                var toLegacyTcp = await client.ProbeAsync(
                    new LinkConfig { Name = "老被动端", Host = "127.0.0.1", Port = legacyTcpPort },
                    "TCP", 1000, CancellationToken.None);
                var toLegacyUdp = await client.ProbeAsync(
                    new LinkConfig { Name = "老被动端", Host = "127.0.0.1", Port = legacyUdpPort },
                    "UDP", 1000, CancellationToken.None);

                await legacyTcpTask;
                await legacyUdpTask;

                Check("对端是老版本时 TCP 仍判为成功（不误判丢包）",
                    toLegacyTcp is { Lost: false }, toLegacyTcp?.Info);
                Check("对端是老版本时 UDP 仍判为成功",
                    toLegacyUdp is { Lost: false }, toLegacyUdp?.Info);
                Eq("对端是老版本时源地址留空（而不是写进半个地址）", "", toLegacyTcp?.SeenFrom);
                Eq("对端是老版本时 UDP 源地址留空", "", toLegacyUdp?.SeenFrom);
                Check("等待附加信息的宽限不计入 RTT",
                    toLegacyUdp is not null && toLegacyUdp.RttMs < ProbeClient.TrailerGraceMs,
                    toLegacyUdp?.RttMs?.ToString());
            }

            // ---- 老被动端“回完不退连接”时，宽限到期也必须算成功 ----
            using (var stickyTcp = new TcpListener(IPAddress.Loopback, 0))
            {
                stickyTcp.Start();
                var stickyPort = ((IPEndPoint)stickyTcp.LocalEndpoint).Port;

                var stickyTask = Task.Run(async () =>
                {
                    try
                    {
                        using var accepted = await stickyTcp.AcceptTcpClientAsync();
                        using var stream = accepted.GetStream();
                        var buffer = new byte[4096];
                        var n = await stream.ReadAsync(buffer);
                        if (n > 0) await stream.WriteAsync(buffer.AsMemory(0, n));
                        await Task.Delay(2000); // 保持连接不关，模拟老被动端没有立刻断开
                    }
                    catch { }
                });

                var stickyResult = await client.ProbeAsync(
                    new LinkConfig { Name = "老被动端2", Host = "127.0.0.1", Port = stickyPort },
                    "TCP", 1200, CancellationToken.None);

                Check("对端不关连接时宽限到期仍判成功", stickyResult is { Lost: false }, stickyResult?.Info);
                Eq("宽限到期时源地址留空", "", stickyResult?.SeenFrom);
                Check("宽限等待不影响 RTT 计时",
                    stickyResult is not null && stickyResult.RttMs < ProbeClient.TrailerGraceMs,
                    stickyResult?.RttMs?.ToString());

                stickyTcp.Stop();
                await Task.WhenAny(stickyTask, Task.Delay(50));
            }

            // 取消 ≠ 丢包（本次修复的核心点）
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var cancelledTcp = await client.ProbeAsync(link, "TCP", 1000, cancelled.Token);
                Check("已取消的 TCP 探测返回 null（不计为丢包）", cancelledTcp is null, cancelledTcp?.Info);
                var cancelledUdp = await client.ProbeAsync(link, "UDP", 1000, cancelled.Token);
                Check("已取消的 UDP 探测返回 null（不计为丢包）", cancelledUdp is null, cancelledUdp?.Info);
            }

            // 中途取消：需要一个“端口开放但永不回包”的对端，
            // 否则回环上关闭的 UDP 端口会立刻回 ICMP，探测快速失败而不是等待取消。
            using (var silentSink = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                var sinkPort = ((IPEndPoint)silentSink.Client.LocalEndPoint!).Port;
                var silent = new LinkConfig { Name = "静默", Host = "127.0.0.1", Port = sinkPort };

                using var midCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var interrupted = await client.ProbeAsync(silent, "UDP", 5000, midCancel.Token);
                stopwatch.Stop();

                Check("探测中途取消返回 null（不计为丢包）", interrupted is null, interrupted?.Info);
                Check("取消立即生效、不必等到超时", stopwatch.ElapsedMilliseconds < 2000,
                    stopwatch.ElapsedMilliseconds + "ms");
            }

            // 回包内容校验：非本工具的回包必须被丢弃，不能误判为成功
            using (var rogue = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                var roguePort = ((IPEndPoint)rogue.Client.LocalEndPoint!).Port;
                var rogueTask = Task.Run(async () =>
                {
                    try
                    {
                        var query = await rogue.ReceiveAsync();
                        var garbage = Encoding.UTF8.GetBytes("not-a-probe-id");
                        await rogue.SendAsync(garbage, garbage.Length, query.RemoteEndPoint);
                    }
                    catch { }
                });

                var spoofed = await client.ProbeAsync(
                    new LinkConfig { Name = "伪装", Host = "127.0.0.1", Port = roguePort }, "UDP", 400, CancellationToken.None);
                Check("回包内容不匹配时判为超时", spoofed is { Lost: true }, spoofed?.Info);
                await rogueTask;
            }

            // 双栈：IPv6 回环
            if (IPv6Available())
            {
                var v6 = new LinkConfig { Name = "v6", Host = "::1", Port = port };
                var v6tcp = await client.ProbeAsync(v6, "TCP", 1000, CancellationToken.None);
                Check("IPv6 回环 TCP 探测成功（双栈监听生效）", v6tcp is { Lost: false }, v6tcp?.Info);
                var v6udp = await client.ProbeAsync(v6, "UDP", 1000, CancellationToken.None);
                Check("IPv6 回环 UDP 探测成功", v6udp is { Lost: false }, v6udp?.Info);
            }
            else
            {
                Console.WriteLine("  [SKIP] 本机不支持 IPv6，跳过双栈用例");
            }
        }
        finally
        {
            server.Dispose();
        }
    }

    // ---------------- 7. 界面 + 清除按钮（端到端） ----------------

    private static void TestMainWindowClear()
    {
        Section("MainWindow + 清除按钮（端到端，真实窗口与真实探测）");

        var echoServer = StartEchoServer(out var echoPort);
        if (echoServer is null)
        {
            Check("回显服务启动", false, "连续 10 次都找不到可绑定端口");
            return;
        }

        // MainWindow 的“启动测试”会真的绑定端口；失败时会弹模态框，
        // 因此这里也必须先确认端口真的可绑定。
        var listenPort = FindBindablePort();

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "ui_" + Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "appsettings.json");
        var previousOverride = ConfigManager.OverridePath;

        // MainWindow 通过 ConfigManager.ConfigPath 读取配置（支持 OverridePath），
        // 因此可以完全控制它启动时要探测的目标。
        ConfigManager.OverridePath = configPath;
        ConfigManager.Save(new AppConfig
        {
            ListenPort = listenPort,
            IntervalMs = 200,
            TimeoutMs = 500,
            StatWindowMinutes = 60,
            Links = { new LinkConfig { Name = "本机", Host = "127.0.0.1", Port = echoPort } }
        });

        string? threadError = null;

        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                Check("窗口与 XAML 装配成功（含新增控件）", true);

                Check("存在清除按钮", window.FindName("ClearButton") is Button);
                Check("清除按钮默认可点击", window.FindName("ClearButton") is Button { IsEnabled: true });
                Check("存在丢包率告警输入框", window.FindName("LossWarnBox") is TextBox);
                Check("存在连续丢包告警输入框", window.FindName("LossCountBox") is TextBox);

                var trendGrid = window.FindName("TrendGrid") as DataGrid;
                var anomalyGrid = window.FindName("AnomalyGrid") as DataGrid;
                var tcpCheck = window.FindName("TcpCheck") as CheckBox;
                var udpCheck = window.FindName("UdpCheck") as CheckBox;
                var statusParams = (TextBlock)window.FindName("StatusParams")!;
                var statusText = (TextBlock)window.FindName("StatusText")!;

                Check("存在 TCP 协议勾选框", tcpCheck is not null);
                Check("存在 UDP 协议勾选框", udpCheck is not null);
                Check("协议勾选框按配置默认勾选（配置为全开）",
                    tcpCheck?.IsChecked == true && udpCheck?.IsChecked == true);
                Check("明细表含序号列",
                    trendGrid is not null && trendGrid.Columns.Any(c => Equals(c.Header, "序号")));
                Check("明细表含探测ID列",
                    trendGrid is not null && trendGrid.Columns.Any(c => Equals(c.Header, "探测ID")));
                Check("异常表含序号列",
                    anomalyGrid is not null && anomalyGrid.Columns.Any(c => Equals(c.Header, "序号")));
                var statsGrid = window.FindName("StatsGrid") as DataGrid;
                Check("统计表含「本端出口（运营商）」列（对端看到的本端地址已并入该列）",
                    statsGrid is not null && statsGrid.Columns.Any(c => Equals(c.Header, "本端出口（运营商）")));
                Check("统计表不再重复显示「对端看到的本端地址」列",
                    statsGrid is not null && statsGrid.Columns.All(c => !Equals(c.Header, "对端看到的本端地址")));
                Check("统计表含“失败原因”列",
                    statsGrid is not null && statsGrid.Columns.Any(c => Equals(c.Header, "失败原因")));
                Check("明细表含“对端看到的源地址”列",
                    trendGrid is not null && trendGrid.Columns.Any(c => Equals(c.Header, "对端看到的源地址")));
                Check("异常表含“对端看到的源地址”列",
                    anomalyGrid is not null && anomalyGrid.Columns.Any(c => Equals(c.Header, "对端看到的源地址")));

                var lossWarnBox = (TextBox)window.FindName("LossWarnBox")!;
                Eq("界面按配置文件填充丢包率阈值", "1", lossWarnBox.Text);

                var session = (StatSession)GetPrivateField(window, "_session")!;
                var stats = (System.Collections.IList)GetPrivateField(window, "_stats")!;
                var anomalies = (System.Collections.IList)GetPrivateField(window, "_anomalies")!;
                var trend = (System.Collections.IList)GetPrivateField(window, "_trend")!;

                InvokePrivateHandler(window, "Start_Click");
                Pump(2500);

                var samples = session.TotalSamples;
                Check("启动后开始累积样本", samples > 0, "样本=" + samples);
                Check("统计表格出现数据行", stats.Count > 0, "行数=" + stats.Count);
                Check("明细表格出现数据行", trend.Count > 0, "行数=" + trend.Count);

                var firstTrend = (ProbeResult)trend[0]!;
                Check("界面明细行带探测序号", firstTrend.Seq > 0, "Seq=" + firstTrend.Seq);
                Check("界面明细行带探测ID", firstTrend.ProbeId.Length == 32, firstTrend.ProbeId);
                Check("界面明细行带对端看到的本端源地址",
                    firstTrend.SeenFrom.StartsWith("127.0.0.1:"), firstTrend.SeenFrom);
                Check("统计表带出本端源地址",
                    session.LastSeenFrom($"本机|127.0.0.1|{echoPort}|TCP").StartsWith("127.0.0.1:"),
                    session.LastSeenFrom($"本机|127.0.0.1|{echoPort}|TCP"));

                // 运行中第 1 次清除：必须立刻生效（在同一 UI 线程回合内即为 0）
                var epochBefore = session.Epoch;
                InvokePrivateHandler(window, "Clear_Click");
                Eq("清除后样本立刻归零", 0, session.TotalSamples);
                Eq("清除后统计表格立刻清空", 0, stats.Count);
                Eq("清除后异常记录立刻清空", 0, anomalies.Count);
                Eq("清除后明细立刻清空", 0, trend.Count);
                Eq("清除使会话号 +1", epochBefore + 1, session.Epoch);
                Check("清除前的会话号被拒绝（陈旧结果不再计入）", !session.Accept(epochBefore));

                // 运行中连续多次点击
                InvokePrivateHandler(window, "Clear_Click");
                InvokePrivateHandler(window, "Clear_Click");
                Eq("可连续多次点击清除", epochBefore + 3, session.Epoch);
                Eq("多次清除后仍为 0", 0, session.TotalSamples);

                // 清除之后统计应自动重新开始
                Pump(2000);
                var resumed = session.TotalSamples;
                Check("清除后自动重新开始统计", resumed > 0, "样本=" + resumed);
                Check("重新统计后统计表格有数据", stats.Count > 0, "行数=" + stats.Count);

                // 停止：保留已统计数据，但不再增长。
                // 注意：停止前已完成测量、还在排队等待刷新的那一轮结果属于真实数据，
                // 停止后仍会落地，所以先让它落定再观察是否继续增长。
                InvokePrivateHandler(window, "Stop_Click");
                Pump(800);
                var settled = session.TotalSamples;
                Pump(800);
                Eq("停止后样本不再增长", settled, session.TotalSamples);
                Check("停止后统计表格仍保留数据", stats.Count > 0, "行数=" + stats.Count);

                // 界面按 500ms 节流刷新，等它收敛后统计行必须与样本总数完全一致
                // （既证明节流后不丢数据，也证明清除前的陈旧数据没有混进来）
                var sentSum = 0;
                foreach (var row in stats) sentSum += ((StatRow)row).Sent;
                Eq("节流刷新收敛后统计行与样本总数一致", settled, sentSum);

                // ---- 协议勾选：不勾选就不发包，且运行中立即生效 ----
                InvokePrivateHandler(window, "Start_Click");
                Pump(1500);

                var tcpKey = $"本机|127.0.0.1|{echoPort}|TCP";
                var udpKey = $"本机|127.0.0.1|{echoPort}|UDP";

                Check("启动后 TCP 与 UDP 都在发包",
                    session.History(tcpKey).Count > 0 && session.History(udpKey).Count > 0,
                    $"tcp={session.History(tcpKey).Count} udp={session.History(udpKey).Count}");
                Check("状态栏显示运行中", statusText.Text == "运行中", statusText.Text);
                Check("状态栏显示协议 TCP + UDP", statusParams.Text.Contains("TCP + UDP"), statusParams.Text);

                // 取消勾选 UDP（运行中，不重启测试）
                udpCheck!.IsChecked = false;
                Pump(800); // 等在途的 UDP 结果落地
                var udpFrozen = session.History(udpKey).Count;
                var tcpBefore = session.History(tcpKey).Count;
                Pump(1500);
                Eq("取消勾选 UDP 后不再发送 UDP 探测包", udpFrozen, session.History(udpKey).Count);
                Check("TCP 不受影响、继续发包", session.History(tcpKey).Count > tcpBefore,
                    $"tcp {tcpBefore} → {session.History(tcpKey).Count}");
                Check("状态栏协议切换为“仅 TCP”", statusParams.Text.Contains("仅 TCP"), statusParams.Text);

                // 再取消勾选 TCP → 完全停止发包
                tcpCheck!.IsChecked = false;
                Pump(800);
                var tcpFrozen = session.History(tcpKey).Count;
                var allFrozen = session.TotalSamples;
                Pump(1500);
                Eq("两个协议都不勾选时不再发送任何探测包", allFrozen, session.TotalSamples);
                Check("状态栏提示只监听不发包", statusParams.Text.Contains("无（只监听"), statusParams.Text);

                // 重新勾选 → 立即恢复发包
                tcpCheck.IsChecked = true;
                udpCheck.IsChecked = true;
                Pump(1500);
                Check("重新勾选后立即恢复发包",
                    session.History(tcpKey).Count > tcpFrozen && session.History(udpKey).Count > udpFrozen,
                    $"tcp={session.History(tcpKey).Count} udp={session.History(udpKey).Count}");

                InvokePrivateHandler(window, "Stop_Click");

                // ---- 链路编辑器：新增 / 校验 / 重复检测 / 防误删 / 撤销 ----
                var linksGrid = (DataGrid)window.FindName("LinksGrid")!;
                var linkSummary = (TextBlock)window.FindName("LinkSummary")!;
                var linkTab = window.FindName("LinkTab") as TabItem;
                var undoButton = (Button)window.FindName("UndoDeleteButton")!;
                var linkRows = (System.Collections.IList)GetPrivateField(window, "_linkRows")!;
                var settings = (AppConfig)GetPrivateField(window, "_settings")!;

                var alerts = new List<string>();
                SetPrivateField(window, "_alert", new Action<string, string>((message, _) => alerts.Add(message)));
                var confirmAnswer = false;
                SetPrivateField(window, "_confirmDelete",
                    new Func<string, string, bool>((message, _) => { alerts.Add(message); return confirmAnswer; }));

                Check("存在“链路配置”页", linkTab is not null);
                Check("链路表格可编辑（不受只读表格样式影响）", !linksGrid.IsReadOnly);
                Eq("链路行按配置载入", 1, linkRows.Count);
                Eq("载入的是配置里的链路", "本机", ((LinkRow)linkRows[0]!).Name);
                Check("摘要显示链路条数", linkSummary.Text.Contains("共 1 条链路"), linkSummary.Text);

                // 新增一行（未选中任何行 → 给默认值，缺主机应为非法）
                linksGrid.SelectedItem = null;
                InvokePrivateHandler(window, "AddLink_Click");
                Eq("新增后多了一行", 2, linkRows.Count);

                var added = (LinkRow)linkRows[1]!;
                Check("新增行默认非法（缺主机/IP）", !added.IsValid, added.Problem);
                Check("摘要提示有校验未通过的行", linkSummary.Text.Contains("校验未通过"), linkSummary.Text);

                // 填成与第 1 行完全相同的目标 → 必然判重复
                added.Name = "本机";
                added.Host = "127.0.0.1";
                added.PortText = echoPort.ToString();
                added.Tcp = true;
                added.Udp = true;
                Check("重复链路被拒绝", !added.IsValid, added.Problem);
                Check("重复提示指出是哪一行", added.Problem.Contains("与第 1 行"), added.Problem);

                added.PortText = (echoPort + 1).ToString();
                Check("改成不重复的目标后恢复合法", added.IsValid, added.Problem);

                // 两个协议都不勾选 = 该链路不主动探测（对端可能是纯服务端）
                added.Tcp = false;
                added.Udp = false;
                Check("TCP/UDP 都不勾选的链路仍然合法", added.IsValid, added.Problem);
                Eq("该行“生效”显示不发包", "不发包", added.Effective);
                Check("摘要提示有链路不主动探测", linkSummary.Text.Contains("不主动探测"), linkSummary.Text);
                added.Tcp = true;
                added.Udp = true;
                Check("重新勾选后摘要不再提示", !linkSummary.Text.Contains("不主动探测"), linkSummary.Text);

                // 非法时启动必须被拦下，并跳到链路页
                added.PortText = "abc";
                alerts.Clear();
                InvokePrivateHandler(window, "Start_Click");
                Check("非法链路会拦下启动", GetPrivateField(window, "_cts") is null);
                Check("拦下时给出可读提示", alerts.Any(a => a.Contains("校验未通过")), string.Join(" | ", alerts));
                Eq("拦下时不会改写生效链路配置", 1, settings.Links.Count);
                Check("拦下时自动切到链路配置页",
                    ReferenceEquals(((TabControl)window.FindName("MainTabs")!).SelectedItem, linkTab));

                added.PortText = (echoPort + 1).ToString();

                // 删除：确认框选“否” → 绝不允许删掉（防误删）
                alerts.Clear();
                linksGrid.SelectedItems.Clear();
                linksGrid.SelectedItems.Add(added);
                confirmAnswer = false;
                InvokePrivateHandler(window, "DeleteLink_Click");
                Eq("确认框选“否”时不会删除", 2, linkRows.Count);
                Check("确认框里带上了链路的目标", alerts.Any(a => a.Contains("127.0.0.1")), string.Join(" | ", alerts));
                Check("取消后撤销按钮不可用", !undoButton.IsEnabled);

                // 确认删除 → 删除并写入撤销缓冲
                confirmAnswer = true;
                InvokePrivateHandler(window, "DeleteLink_Click");
                Eq("确认后删除成功", 1, linkRows.Count);
                Check("删除后撤销按钮可用", undoButton.IsEnabled);

                // 撤销删除 → 恢复到原位置
                InvokePrivateHandler(window, "UndoDelete_Click");
                Eq("撤销删除恢复行数", 2, linkRows.Count);
                Check("撤销恢复的是同一行且回到原位置", ReferenceEquals(linkRows[1], added));
                Check("撤销后撤销按钮复位", !undoButton.IsEnabled);

                // 删除最后一条时的额外警告
                alerts.Clear();
                linksGrid.SelectedItems.Clear();
                linksGrid.SelectedItems.Add(linkRows[0]!);
                linksGrid.SelectedItems.Add(linkRows[1]!);
                confirmAnswer = false;
                InvokePrivateHandler(window, "DeleteLink_Click");
                Check("删除最后一条链路时给出额外警告",
                    alerts.Any(a => a.Contains("最后一条链路")), string.Join(" | ", alerts));
                Eq("取消后仍然保留两行", 2, linkRows.Count);

                // ---- 全局协议 与 链路协议 冲突时：两者都勾才发包（总开关只能收紧） ----
                var first = (LinkRow)linkRows[0]!;
                first.Udp = false;
                Check("链路关掉 UDP 后“生效”列只剩 TCP", first.Effective == "TCP", first.Effective);
                Check("此时全局全开，摘要不应提示被压住",
                    !linkSummary.Text.Contains("已压住"), linkSummary.Text);

                InvokePrivateHandler(window, "Start_Click");
                Pump(1500);
                Eq("链路未启用 UDP：即使全局勾了 UDP 也不发", 0, session.History(udpKey).Count);
                Check("TCP 照常发包", session.History(tcpKey).Count > 0,
                    "tcp=" + session.History(tcpKey).Count);

                // 反向：全局取消 TCP，链路里还勾着也不发
                tcpCheck!.IsChecked = false;
                Pump(900);
                var tcpFrozenByGlobal = session.History(tcpKey).Count;
                Pump(1200);
                Eq("全局取消 TCP：即使链路勾着也不发", tcpFrozenByGlobal, session.History(tcpKey).Count);
                Check("“生效”列显示不发包", first.Effective == "不发包", first.Effective);
                Check("摘要明确提示全局压住了链路设置",
                    linkSummary.Text.Contains("已压住"), linkSummary.Text);

                tcpCheck.IsChecked = true;
                InvokePrivateHandler(window, "Stop_Click");

                // ---- 单条链路停测：只有它不发包，其它链路照常 ----
                var standBy = (LinkRow)linkRows[0]!;
                standBy.Tcp = false;
                standBy.Udp = false;
                Check("链路级停测后仍然合法（纯服务端场景）", standBy.IsValid, standBy.Problem);
                Eq("停测链路的“生效”为不发包", "不发包", standBy.Effective);

                InvokePrivateHandler(window, "Start_Click");
                Pump(1500);
                Eq("停测的链路不发送任何探测包",
                    0, session.History(tcpKey).Count + session.History(udpKey).Count);
                Check("同一配置里其它链路照常发包", session.TotalSamples > 0, "样本=" + session.TotalSamples);
                InvokePrivateHandler(window, "Stop_Click");

                try
                {
                    window.Close();
                    Console.WriteLine("  [INFO] 窗口关闭清理路径正常");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  [INFO] 未显示窗口执行 Close()：" + ex.GetType().Name + "（不影响界面逻辑）");
                }
            }
            catch (Exception ex)
            {
                threadError = ex.ToString();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(90)))
            Check("端到端用例在 90 秒内完成", false, "超时（可能弹出了模态对话框）");

        Check("端到端用例未抛异常", threadError is null, threadError);

        ConfigManager.OverridePath = previousOverride;
        echoServer.Dispose();
        try { Directory.Delete(root, true); } catch { }
    }

    // ---------------- 8. 界面渲染（排版检查） ----------------

    /// <summary>
    /// 把真实窗口渲染成 PNG。窗口不 Show()，因此不会在桌面闪窗；
    /// 用于在不启动程序的情况下检查排版与配色。
    /// </summary>
    private static void RenderUi(string outputPath)
    {
        const int width = 1360;
        const int height = 900;

        var echoServer = StartEchoServer(out var echoPort);
        if (echoServer is null)
        {
            Console.WriteLine("渲染失败：找不到可绑定端口");
            return;
        }

        var listenPort = FindBindablePort();
        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "render_" + Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "appsettings.json");
        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = configPath;

        ConfigManager.Save(new AppConfig
        {
            ListenPort = listenPort,
            IntervalMs = 300,
            TimeoutMs = 500,
            Registrar = new RegistrarConfig
            {
                Enabled = true,
                NodeName = "机房A-测试端",
                NodeId = "render-node",
                PublicHost = "203.0.113.9",
                PublicPort = 16298,
                CenterHost = "203.0.113.1",
                CenterPort = 16298
            },
            Links =
            {
                new LinkConfig { Name = "机房B", Host = "127.0.0.1", Port = echoPort },
                new LinkConfig { Name = "机房C", Host = "192.0.2.9", Port = echoPort }
            }
        });

        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                InvokePrivateHandler(window, "Start_Click");
                Pump(3000);

                var content = (FrameworkElement)window.Content;
                var background = window.Background;
                var tabs = (TabControl)window.FindName("MainTabs")!;

                // 故意留一条非法链路，用于检查“非法行标红 + 校验列给出原因”是否真的生效
                var linksGrid = (DataGrid)window.FindName("LinksGrid")!;
                var linkRows = (System.Collections.IList)GetPrivateField(window, "_linkRows")!;
                var badRow = (LinkRow)linkRows[linkRows.Count - 1]!;
                badRow.PortText = "abc";
                Pump(300);

                for (var index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    Pump(400);
                    var file = outputPath.Replace(".png", $"_{index}.png");
                    SaveVisual(content, width, height, background, file);
                    Console.WriteLine("已渲染 " + file);
                }

                linksGrid.UpdateLayout();
                if (linksGrid.ItemContainerGenerator.ContainerFromItem(badRow) is not DataGridRow container)
                {
                    Check("非法行已生成行容器", false, "行容器未生成，无法检查行样式");
                }
                else
                {
                    var brush = container.Background as SolidColorBrush;
                    Check("非法行被标红", brush?.Color == Color.FromRgb(0xFD, 0xEC, 0xEA),
                        container.Background?.ToString() ?? "null");
                    Eq("非法行在“校验”列给出原因", "端口必须是整数", badRow.Problem);
                }

                // 注册页的真实排版：报表里的检查会跳过 TabControl 内部，这里单独看一次
                // （只查横向裁剪，纵向超出由 ScrollViewer 承担，是设计允许的）
                if (window.FindName("RegistryTab") is TabItem registryTab &&
                    window.FindName("RegistryTabPanel") is FrameworkElement registryPanel)
                {
                    tabs.SelectedItem = registryTab;
                    Pump(400);

                    var panelProblems = new List<string>();
                    AuditNestedWidth(registryPanel, panelProblems);
                    Check("注册页在真实窗口里无横向裁剪", panelProblems.Count == 0,
                        string.Join("；", panelProblems));
                }

                Report(content, width, height);

                InvokePrivateHandler(window, "Stop_Click");
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(90))) Console.WriteLine("渲染超时");

        ConfigManager.OverridePath = previousOverride;
        echoServer.Dispose();
        try { Directory.Delete(root, true); } catch { }

        if (error is not null) Console.WriteLine("渲染失败：" + error);
        else Console.WriteLine($"渲染完成，通过 {_pass}，失败 {Failures.Count}");
    }

    private static void SaveVisual(FrameworkElement content, int width, int height, Brush background, string file)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        // 先铺窗口底色，再叠加内容树（窗口未 Show() 时不会自带背景）
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    /// <summary>
    /// 没有视觉模型时用数值方式审查排版：静态区域的文本是否被裁剪、
    /// 关键控件占了多少空间、表格还剩多少可用高度。
    /// </summary>
    private static void Report(FrameworkElement root, double width, double height)
    {
        Console.WriteLine("\n  --- 排版审查 ---");
        Console.WriteLine($"  画布 {width}×{height}");

        var problems = new List<string>();
        AuditStatic(root, root, width, height, problems);

        foreach (var element in EnumerateStatic(root))
        {
            // 注意：窗口没 Show() 时 UIElement.IsVisible 恒为 false，
            // 因此这里用 Visibility + ActualWidth 判断是否真正参与了布局。
            if (element is not FrameworkElement fe || fe.Visibility != Visibility.Visible || fe.ActualWidth <= 0) continue;
            var point = fe.TransformToAncestor(root).Transform(new Point(0, 0));
            Console.WriteLine($"  {Describe(element),-24} x={point.X,6:F0} y={point.Y,6:F0} " +
                              $"w={fe.ActualWidth,6:F0} h={fe.ActualHeight,5:F0}");
        }

        Check("静态区域无文本裁剪、无越界（排版审查）", problems.Count == 0,
            string.Join("；", problems));
    }

    private static IEnumerable<DependencyObject> EnumerateStatic(DependencyObject node)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;

            // 表格内部由 DataGrid/TabControl 自行裁剪，不再深入
            if (child is DataGrid or TabControl) continue;

            foreach (var grandChild in EnumerateStatic(child)) yield return grandChild;
        }
    }

    /// <summary>DesiredSize 含 Margin，比较可用宽度时必须先扣掉左右外边距。</summary>
    private static double ContentWidth(FrameworkElement element)
        => element.DesiredSize.Width - element.Margin.Left - element.Margin.Right;

    private static void AuditStatic(
        DependencyObject node, FrameworkElement root, double width, double height, List<string> problems)
    {
        foreach (var child in EnumerateStatic(node))
        {
            if (child is DataGrid or TabControl) continue;

            if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible) continue;
            if (fe.ActualWidth <= 0) continue;

            if (child is TextBlock text && text.TextTrimming == TextTrimming.None
                && !string.IsNullOrWhiteSpace(text.Text)
                && ContentWidth(text) > text.ActualWidth + 1)
            {
                problems.Add($"文本被裁剪：“{Shorten(text.Text)}” 需要 {ContentWidth(text):F0}px，只有 {text.ActualWidth:F0}px");
            }

            if (child is CheckBox or Button or TextBox
                && ContentWidth(fe) > fe.ActualWidth + 1)
            {
                problems.Add($"{child.GetType().Name}“{Describe(child)}”宽度不足：需要 {ContentWidth(fe):F0}px，只有 {fe.ActualWidth:F0}px");
            }

            var origin = fe.TransformToAncestor(root).Transform(new Point(0, 0));
            if (origin.X < -1 || origin.Y < -1 ||
                origin.X + fe.ActualWidth > width + 1 || origin.Y + fe.ActualHeight > height + 1)
            {
                problems.Add($"{child.GetType().Name}“{Describe(child)}”越界：({origin.X:F0},{origin.Y:F0}) " +
                             $"{fe.ActualWidth:F0}×{fe.ActualHeight:F0}");
            }
        }
    }

    private static string Describe(DependencyObject element) => element switch
    {
        TextBlock text => Shorten(text.Text),
        ContentControl content when content.Content is not null => Shorten(content.Content.ToString() ?? ""),
        FrameworkElement fe when !string.IsNullOrEmpty(fe.Name) => fe.Name,
        _ => element.GetType().Name
    };

    private static string Shorten(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 22 ? text : text[..22] + "…";
    }

    // ---------------- 6. 失败原因文案 ----------------

    private static void TestFailureKinds()
    {
        Section("FailureKinds 失败原因");

        Eq("超时文案", "无响应（超时）", FailureKinds.Describe(FailureKinds.Timeout));
        Eq("被拒绝文案", "连接被拒绝（端口无监听）", FailureKinds.Describe(FailureKinds.Refused));
        Eq("ICMP 不可达文案", "UDP 端口不可达（收到 ICMP）", FailureKinds.Describe(FailureKinds.IcmpUnreachable));
        Eq("网络不可达文案", "网络/主机不可达", FailureKinds.Describe(FailureKinds.Unreachable));
        Eq("解析失败文案", "主机名解析失败", FailureKinds.Describe(FailureKinds.HostNotFound));
        Eq("空 token 回退到通用文案", "超时/丢包", FailureKinds.Describe(FailureKinds.None));
        Eq("未知 token 回退到通用文案", "超时/丢包", FailureKinds.Describe("something-else"));

        var ok = Result(new LinkConfig(), "TCP", DateTime.Now, rtt: 12.34);
        Eq("成功探测的展示为 RTT", "12.3 ms", ok.Info);
        Eq("成功探测没有失败原因", FailureKinds.None, ok.Failure);

        var bad = Result(new LinkConfig(), "TCP", DateTime.Now, lost: true, failure: FailureKinds.Timeout);
        Eq("失败探测的展示为失败原因", "无响应（超时）", bad.Info);
    }

    // ---------------- 7. 链路编辑器行校验 ----------------

    private static void TestLinkRowValidation()
    {
        Section("LinkRow 校验（链路编辑器逐行校验）");

        Check("合法行通过校验", new LinkRow { Name = "机房B", Host = "10.0.0.1", PortText = "9001" }.Revalidate());
        Check("端口越界被拒绝", !new LinkRow { Name = "机房B", Host = "10.0.0.1", PortText = "70000" }.Revalidate());
        Check("端口为 0 被拒绝", !new LinkRow { Name = "机房B", Host = "10.0.0.1", PortText = "0" }.Revalidate());

        var badPort = new LinkRow { Name = "机房B", Host = "10.0.0.1", PortText = "abc" };
        Check("端口非数字被拒绝（可进入模型而非被绑定吞掉）", !badPort.Revalidate());
        Eq("端口非数字的提示", "端口必须是整数", badPort.Problem);
        Eq("非法行的端口解析为 0", 0, badPort.Port);

        var noName = new LinkRow { Name = "", Host = "10.0.0.1", PortText = "9001" };
        Check("缺名称被拒绝", !noName.Revalidate());
        Check("缺名称的提示可读", noName.Problem.Contains("名称"), noName.Problem);

        var noHost = new LinkRow { Name = "机房B", Host = "  ", PortText = "9001" };
        Check("缺主机被拒绝", !noHost.Revalidate());
        Check("缺主机的提示可读", noHost.Problem.Contains("主机"), noHost.Problem);

        var noProtocol = new LinkRow { Name = "机房B", Host = "10.0.0.1", PortText = "9001", Tcp = false, Udp = false };
        Check("两个协议都不勾选也是合法行（不主动探测，例如纯服务端）", noProtocol.Revalidate(), noProtocol.Problem);
        Eq("不主动探测时“生效”列显示不发包", "不发包", noProtocol.Effective);
        Eq("不主动探测的行没有校验问题", "", noProtocol.Problem);

        var spaced = new LinkRow { Name = "  机房B  ", Host = "  10.0.0.1  ", PortText = " 9001 " };
        Check("首尾空格不影响校验", spaced.Revalidate());
        var config = spaced.ToConfig();
        Eq("写回配置时去掉首尾空格（名称）", "机房B", config.Name);
        Eq("写回配置时去掉首尾空格（主机）", "10.0.0.1", config.Host);
        Eq("写回配置时端口为整数", 9001, config.Port);

        var roundTrip = LinkRow.FromConfig(new LinkConfig
        {
            Name = "机房C", Host = "10.0.0.2", Port = 9002, Tcp = false, Udp = true
        });
        Check("从配置载入的行通过校验", roundTrip.Revalidate());
        Eq("从配置载入的端口文本", "9002", roundTrip.PortText);
        Eq("从配置载入的协议", false, roundTrip.Tcp);
        Eq("展示串含名称与目标", "机房C（10.0.0.2:9002）", roundTrip.Display);

        // 与配置文件校验共用同一套规则
        var link = new LinkConfig { Name = "", Host = "h", Port = 1 };
        Check("ConfigManager.ValidateLink 与行校验规则一致（缺名称）", !ConfigManager.ValidateLink(link, out _));
        Check("ConfigManager.ValidateLink 通过合法链路",
            ConfigManager.ValidateLink(new LinkConfig { Name = "A", Host = "h", Port = 1 }, out _));

        // 全局协议勾选 与 链路自身勾选：取交集，总开关只能收紧、不能放宽
        var row = new LinkRow { Name = "A", Host = "h", PortText = "1" };

        row.SetGlobalProtocols(true, true);
        Eq("全局全开 + 链路全开 → TCP + UDP", "TCP + UDP", row.Effective);

        row.SetGlobalProtocols(true, false);
        Eq("全局关 UDP → 生效只剩 TCP", "TCP", row.Effective);

        row.SetGlobalProtocols(false, true);
        Eq("全局关 TCP → 生效只剩 UDP", "UDP", row.Effective);

        row.SetGlobalProtocols(false, false);
        Eq("全局全关 → 不发包", "不发包", row.Effective);

        row.SetGlobalProtocols(false, true);   // 全局只放行 UDP
        row.Tcp = false;                       // 链路自己也关掉 TCP
        Eq("全局与链路都关 TCP → 生效只剩 UDP", "UDP", row.Effective);

        row.SetGlobalProtocols(true, true);
        Eq("全局放开后由链路说了算（链路关了 TCP 就只发 UDP）", "UDP", row.Effective);

        var notified = new List<string>();
        row.PropertyChanged += (_, args) => notified.Add(args.PropertyName ?? "");
        row.SetGlobalProtocols(false, true);
        Check("全局勾选变化会通知“生效”列刷新",
            notified.Contains(nameof(LinkRow.Effective)), string.Join(",", notified));
        notified.Clear();
        row.Udp = false;
        Check("链路协议变化会通知“生效”列刷新",
            notified.Contains(nameof(LinkRow.Effective)), string.Join(",", notified));
    }

    /// <summary>在 STA 线程上跑消息泵，让 DispatcherTimer 与 InvokeAsync 回调真正执行。</summary>
    private static void Pump(int milliseconds)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(15);
        }
    }

    private static object? GetPrivateField(object target, string name)
        => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);

    private static void SetPrivateField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("未找到字段：" + name);
        field.SetValue(target, value);
    }

    private static void InvokePrivateHandler(object target, string name)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new InvalidOperationException("未找到处理器：" + name);

        try
        {
            method.Invoke(target, new object[] { target, new RoutedEventArgs() });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// 找一个真正可绑定的端口：系统临时端口可能落在保留/排除区间，
    /// 直接用它绑定会得到 AccessDenied。这里按 ProbeServer 的双栈策略实测一次再交付。
    ///
    /// 注意：Windows **按顺序**分配临时端口，一旦游标落进一段排除区间（Hyper-V/WSL 常占大段），
    /// 连着几十个候选会一起失败——所以临时端口试完必须退回**随机扫描**一段高端口
    /// （只试临时端口时实测偶发"找不到可绑定的端口"而整轮自检崩掉）。
    /// </summary>
    private static int FindBindablePort()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = FreePort();
            if (CanBind(candidate)) return candidate;
        }

        var random = new Random(unchecked(Environment.TickCount ^ (int)DateTime.UtcNow.Ticks));
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var candidate = random.Next(20000, 60000);
            if (!CanBind(candidate)) continue;

            // 走到这里说明系统临时端口整段不可用；留一行记录，便于分辨"端口问题"与"代码问题"
            Console.WriteLine($"  [INFO] 临时端口全部不可用，随机扫描到端口 {candidate}");
            return candidate;
        }

        throw new InvalidOperationException("找不到可绑定的端口（50 个临时端口 + 300 个随机端口都不可用）");
    }

    private static bool CanBind(int port)
    {
        TcpListener? tcp = null;
        UdpClient? udp = null;

        try
        {
            try
            {
                tcp = new TcpListener(IPAddress.IPv6Any, port);
                try { tcp.Server.DualMode = true; } catch { }
                tcp.Start();
            }
            catch
            {
                try { tcp?.Stop(); } catch { }
                tcp = new TcpListener(IPAddress.Any, port);
                tcp.Start();
            }

            try
            {
                udp = new UdpClient(AddressFamily.InterNetworkV6);
                try { udp.Client.DualMode = true; } catch { }
                udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            catch
            {
                try { udp?.Dispose(); } catch { }
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { tcp?.Stop(); } catch { }
            try { udp?.Dispose(); } catch { }
        }
    }

    private static ProbeServer? StartEchoServer(out int port)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            port = FindBindablePort();
            var server = new ProbeServer(port);
            try
            {
                server.Start();
                return server;
            }
            catch
            {
                server.Dispose();
            }
        }

        port = 0;
        return null;
    }

    private static bool IPv6Available()
    {
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
