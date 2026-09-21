using System.IO;
using System.Windows;
using System.Windows.Controls;
using LineTester.Isp;

namespace LineTester.Tests;

/// <summary>
/// 运营商信息与"跨运营商"判定的自检。
///
/// 全部用**假抓取器**（不联网）：真实 ip-api 只有 15 次/分钟、还会被墙/被限流，
/// 拿它当测试依赖既不稳定也不合适。归类规则用的却是**实测到的真实数据**
/// （见 <see cref="Sample"/>），因此断言的是一线会遇到的那几种字段组合。
/// </summary>
internal static partial class Program
{
    /// <summary>实测样本（2026-09，ip-api 免费接口）。</summary>
    private static class Sample
    {
        public static IspRecord TelecomShanghai => Ok("202.96.209.133",
            "China Telecom (Group)", "AS4812 China Telecom (Group)", "CHINANET-SH-AP");

        /// <summary>联通的老网通资源：isp 里没有 "unicom"/"联通" 字样。</summary>
        public static IspRecord Unicom169 => Ok("221.5.88.88",
            "CNC Group CHINA169 Guangdong Province Network",
            "AS17816 China Unicom IP network China169 Guangdong province", "CHINA169-GZ");

        public static IspRecord MobileGuangdong => Ok("211.136.192.6",
            "China Mobile", "AS56040 China Mobile communications corporation", "CMNET-GuangDong-AP");

        public static IspRecord Alibaba => Ok("223.5.5.5",
            "Hangzhou Alibaba Advertising Co", "AS37963 Hangzhou Alibaba Advertising Co.,Ltd.", "ALIBABA-CN-NET");

        public static IspRecord Tencent => Ok("119.29.29.29",
            "Shenzhen Tencent Computer Systems Company Limited",
            "AS132203 Tencent Building, Kejizhongyi Avenue", "TENCENT-NET-AP-CN");

        /// <summary>同一份返回里 isp 说联通、as 说电信——只能按字段优先取 isp，并在提示里给出 AS。</summary>
        public static IspRecord Conflicting114 => Ok("114.114.114.114",
            "China Unicom Shandong Province network",
            "AS137702 Nanjing, Jiangsu Province, P.R.China.", "CHINATELECOM-JIANGSU-NANJING-IDC");

        /// <summary>境外非云厂商（官方文档里的示例 IP）：用于验证"海外"这一类的展示。</summary>
        public static IspRecord Videotron => new()
        {
            Ip = "24.48.0.1",
            Status = "success",
            Country = "加拿大",
            Isp = "Le Groupe Videotron Ltee",
            Org = "Videotron Ltee",
            As = "AS5769 Videotron Ltee",
            AsName = "VIDEOTRON",
            FetchedUtc = DateTime.UtcNow
        };

        public static IspRecord GoogleDns => new()
        {
            Ip = "8.8.8.8",
            Status = "success",
            Country = "美国",
            Isp = "Google LLC",
            Org = "Google Public DNS",
            As = "AS15169 Google LLC",
            AsName = "GOOGLE",
            FetchedUtc = DateTime.UtcNow
        };

        public static IspRecord Private => new()
        {
            Ip = "192.168.1.10",
            Status = "fail",
            Message = "private range",
            FetchedUtc = DateTime.UtcNow
        };

        private static IspRecord Ok(string ip, string isp, string asField, string asName) => new()
        {
            Ip = ip,
            Status = "success",
            Country = "China",
            Isp = isp,
            Org = isp,
            As = asField,
            AsName = asName,
            FetchedUtc = DateTime.UtcNow
        };
    }

    // ---------------- 归类 ----------------

    private static void TestIspCarrier()
    {
        Section("运营商归类：三网 / 云 / 海外 / 内网（用实测样本）");

        Eq("电信", "电信", Name(Sample.TelecomShanghai));
        Eq("联通（老网通 CNC/CHINA169 也算联通）", "联通", Name(Sample.Unicom169));
        Eq("移动", "移动", Name(Sample.MobileGuangdong));
        Eq("阿里云不是三网", "阿里云", Name(Sample.Alibaba));
        Eq("腾讯云不是三网", "腾讯云", Name(Sample.Tencent));
        Eq("字段冲突时按 isp 优先判", "联通", Name(Sample.Conflicting114));
        Eq("云厂商不算三网（Google 的网络）", "Google", Name(Sample.GoogleDns));
        Eq("海外非云厂商显示国家", "海外·加拿大", Name(Sample.Videotron));
        Eq("内网", "内网地址", Name(Sample.Private));

        Eq("AS 号提取", "AS4812", Sample.TelecomShanghai.AsNumber);
        Eq("AS 字段为空时 AS 号为空", "", new IspRecord { As = "" }.AsNumber);

        // 分类不能靠"猜"：没有数据的记录必须是未知
        Eq("无数据 → 未知", "未知", Name(null));
        var reserved = new IspRecord { Status = "fail", Message = "reserved range" };
        IspCarrier.Classify(reserved, out var reservedName);
        Eq("保留地址仍归类为未知（不被当成某家运营商）", CarrierKind.Unknown, IspCarrier.Classify(reserved, out _));
        Eq("但展示上说明是保留地址（比笼统的未知有用）", "保留地址", reservedName);

        // 大类名（比较结论里用）
        Eq("大类名：电信", "电信", IspCarrier.BrandName(CarrierKind.Telecom));
        Eq("大类名：教育网", "教育网", IspCarrier.BrandName(CarrierKind.Education));

        Section("运营商归类：同/跨运营商判定");

        var sameTelecom = IspCarrier.Compare(Sample.TelecomShanghai, Sample.TelecomShanghai);
        Eq("电信 ↔ 电信 = 同运营商", CrossNetState.Same, sameTelecom.State);
        Check("同运营商的文案点明是哪家", sameTelecom.Short.Contains("电信"), sameTelecom.Short);

        // 同一家大类的不同省网 / 不同 AS：用户要的是"同运营商"，不能因为 AS 不同就报跨网
        var telecomOtherProvince = new IspRecord
        {
            Ip = "61.139.2.69", Status = "success", Country = "China",
            Isp = "Chinanet Sichuan Province Network", Org = "China Telecom",
            As = "AS4134 CHINANET-BACKBONE", AsName = "CHINANET-BACKBONE", FetchedUtc = DateTime.UtcNow
        };
        var sameBrandDifferentAs = IspCarrier.Compare(Sample.TelecomShanghai, telecomOtherProvince);
        Eq("上海电信 ↔ 四川电信（AS 不同）= 同运营商", CrossNetState.Same, sameBrandDifferentAs.State);

        var cross = IspCarrier.Compare(Sample.TelecomShanghai, Sample.Unicom169);
        Eq("电信 ↔ 联通 = 跨运营商", CrossNetState.Cross, cross.State);
        Check("跨运营商文案给出方向", cross.Short.Contains("电信") && cross.Short.Contains("联通"), cross.Short);
        Check("提示明细里带两侧 AS 号（人工核对用）",
            cross.Detail.Contains("AS4812") && cross.Detail.Contains("AS17816"), cross.Detail);

        var carrierToCloud = IspCarrier.Compare(Sample.TelecomShanghai, Sample.Alibaba);
        Eq("电信 ↔ 阿里云 = 跨网（不同 AS）", CrossNetState.Cross, carrierToCloud.State);

        var sameCloud = IspCarrier.Compare(Sample.Alibaba, Sample.Alibaba);
        Eq("阿里云 ↔ 阿里云（同 AS）= 同网", CrossNetState.Same, sameCloud.State);
        Check("同网文案里带 AS 号", sameCloud.Short.Contains("AS37963"), sameCloud.Short);

        var cloudToCloud = IspCarrier.Compare(Sample.Alibaba, Sample.Tencent);
        Eq("阿里云 ↔ 腾讯云 = 跨网", CrossNetState.Cross, cloudToCloud.State);

        var overseas = IspCarrier.Compare(Sample.TelecomShanghai, Sample.GoogleDns);
        Eq("国内 ↔ 海外 = 跨网", CrossNetState.Cross, overseas.State);

        // 数据不足绝不硬猜：宁可灰灯
        var unknownRemote = IspCarrier.Compare(Sample.TelecomShanghai, null);
        Eq("目标查不到 = 未知（不误报红）", CrossNetState.Unknown, unknownRemote.State);
        Check("未知时说明缺了什么", unknownRemote.Detail.Contains("目标地址未知"), unknownRemote.Detail);

        var unknownLocal = IspCarrier.Compare(null, Sample.Unicom169);
        Eq("本端未知 = 未知", CrossNetState.Unknown, unknownLocal.State);

        var privateSide = IspCarrier.Compare(Sample.Private, Sample.TelecomShanghai);
        Eq("一侧是内网地址 = 未知（不判定）", CrossNetState.Unknown, privateSide.State);

        var disabled = IspCarrier.Compare(Sample.TelecomShanghai, Sample.Unicom169, enabled: false);
        Eq("未启用 = 不判定", CrossNetState.Disabled, disabled.State);

        // 两边都识别不出大类、也没有 AS：只给归属，不下结论
        var noAs = new IspRecord { Ip = "1.1.1.1", Status = "success", Isp = "Some Network", FetchedUtc = DateTime.UtcNow };
        var bothVague = IspCarrier.Compare(noAs, noAs);
        Eq("没有 AS 也没大类 = 未知", CrossNetState.Unknown, bothVague.State);

        Section("运营商归类：地址过滤与主机名解析");

        Check("公网 IPv4 认作可查", IspAddressRules.IsPublicIp("202.96.209.133"));
        Check("内网 10/8 不查", !IspAddressRules.IsPublicIp("10.1.2.3"));
        Check("内网 192.168/16 不查", !IspAddressRules.IsPublicIp("192.168.1.1"));
        Check("内网 172.16/12 不查", !IspAddressRules.IsPublicIp("172.20.1.1"));
        Check("回环不查", !IspAddressRules.IsPublicIp("127.0.0.1"));
        Check("链路本地不查", !IspAddressRules.IsPublicIp("169.254.1.1"));
        Check("CGNAT 100.64/10 不查", !IspAddressRules.IsPublicIp("100.64.0.1"));
        Check("组播不查", !IspAddressRules.IsPublicIp("224.0.0.1"));
        Check("IPv6 回环不查", !IspAddressRules.IsPublicIp("::1"));
        Check("IPv6 ULA 不查", !IspAddressRules.IsPublicIp("fd00::1"));
        Check("IPv4-mapped 按 IPv4 判（公网可查）", IspAddressRules.IsPublicIp("::ffff:202.96.209.133"));
        Check("域名不是 IP（交给解析）", !IspAddressRules.IsPublicIp("vpn.example.com"));

        Eq("从 ip:port 取主机", "202.96.209.133", IspAddressRules.HostPart("202.96.209.133:9000"));
        Eq("裸 IP 原样返回", "202.96.209.133", IspAddressRules.HostPart("202.96.209.133"));
        Eq("域名带端口", "vpn.example.com", IspAddressRules.HostPart("vpn.example.com:8800"));
        Eq("带方括号的 IPv6 带端口", "2001:db8::1", IspAddressRules.HostPart("[2001:db8::1]:9000"));
        Eq("裸 IPv6 不被当成端口切掉", "fe80::1", IspAddressRules.HostPart("fe80::1"));
        Eq("空/占位返回空串", "", IspAddressRules.HostPart("-"));
    }

    private static string Name(IspRecord? record)
    {
        IspCarrier.Classify(record, out var display);
        return display;
    }

    // ---------------- 查询管线（假抓取器） ----------------

    private static void TestIspLookupService()
    {
        Section("运营商查询：批量/缓存/限流/降级（假抓取器，不联网）");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "isp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cachePath = Path.Combine(root, "ip-isp-cache.json");

        var fetcher = new FakeFetcher();
        var config = new IspConfig { Enabled = true, CacheDays = 30, TimeoutMs = 1000 };
        var cache = new IspCacheStore(cachePath, TimeSpan.FromDays(30));

        using (var service = new IspLookupService(config, cache, fetcher))
        {
            // 只查公网、且去重
            service.Request("202.96.209.133");
            service.Request("202.96.209.133");
            service.Request("192.168.1.10");   // 内网：本地判定，不发请求
            service.Request("-");               // 占位符：忽略
            service.Request("");                 // 空：忽略

            fetcher.WaitForCalls(1);

            Eq("只发了一次批量请求（去重 + 内网不查）", 1, fetcher.CallCount);
            Eq("批量里只有公网地址", "202.96.209.133", string.Join(",", fetcher[0]));

            WaitUntil(() => service.TryGet("202.96.209.133") is not null, 5000);
            var record = service.TryGet("202.96.209.133");
            Check("查询结果进入缓存", record is not null);
            Eq("结果里的 ISP 被保留", Sample.TelecomShanghai.Isp, record?.Isp ?? "");

            // 内网地址本地就能判定：不发请求，但会记成"内网"事实，避免每半秒重试
            var privateFact = service.TryGet("192.168.1.10");
            Check("内网地址没有发请求出去（批量里没有它）", !fetcher[0].Contains("192.168.1.10"));
            Check("内网地址被记为内网事实（不会反复重试）",
                privateFact is { Status: "fail", Message: "private range" }, privateFact?.Message);

            // 已缓存的地址不再请求
            service.Request("202.96.209.133");
            Thread.Sleep(1500);
            Eq("已缓存的地址不再发起请求", 1, fetcher.CallCount);

            // 未知域名：解析不出来 → 不阻塞、不刷屏，并给出状态
            service.Request("no-such-host.invalid");
            WaitUntil(() => service.Status.LastError is not null, 5000);
            Check("解析失败的域名会给出提示", service.Status.LastError?.Contains("无法解析") == true,
                service.Status.LastError);
            Check("解析失败不会无限重试", fetcher.CallCount == 1, fetcher.CallCount.ToString());
        }

        // 缓存要落盘且能重载（重启程序不该重新查一遍）
        Check("缓存文件已写出", File.Exists(cachePath), cachePath);

        var reloaded = new IspCacheStore(cachePath, TimeSpan.FromDays(30));
        Check("缓存可重载", reloaded.TryGet("202.96.209.133", out var fromDisk));
        Eq("重载后的 ISP 一致", Sample.TelecomShanghai.Isp, fromDisk.Isp);

        // 过期即失效
        var expired = new IspCacheStore(cachePath, TimeSpan.FromMilliseconds(1));
        Thread.Sleep(20);
        Check("超过 TTL 的记录视为过期（会重新查）", !expired.TryGet("202.96.209.133", out _));

        // 限流：X-Rl=0 + X-Ttl 之后不得再发请求（否则会被封 1 小时）
        var throttleFetcher = new FakeFetcher
        {
            Result = new IspFetchResult
            {
                Ok = true,
                Records = new List<IspRecord>(),
                Remaining = 0,
                ResetAfterSeconds = 3
            }
        };
        var throttleCache = new IspCacheStore(Path.Combine(root, "throttle.json"), TimeSpan.FromDays(30));
        using (var service = new IspLookupService(config, throttleCache, throttleFetcher))
        {
            service.Request("223.5.5.5");
            throttleFetcher.WaitForCalls(1);
            Check("额度用尽后进入限流状态", service.Status.Throttled);
            Eq("限流时不再发第二次请求", 1, throttleFetcher.CallCount);

            service.Request("119.29.29.29");
            Thread.Sleep(1500);
            Eq("限流期间新地址也不会发请求", 1, throttleFetcher.CallCount);
        }

        // 抓取失败：只报状态，不影响别的功能
        var failingFetcher = new FakeFetcher { Result = IspFetchResult.Failure("网络不可达") };
        var failingCache = new IspCacheStore(Path.Combine(root, "fail.json"), TimeSpan.FromDays(30));
        using (var service = new IspLookupService(config, failingCache, failingFetcher))
        {
            service.Request("180.76.76.76");
            failingFetcher.WaitForCalls(1);
            WaitUntil(() => service.Status.LastError is not null, 3000);
            Eq("失败原因进状态", "网络不可达", service.Status.LastError ?? "");
            Check("失败时没有产生假的缓存记录", service.TryGet("180.76.76.76") is null);
        }

        // 关闭：一个请求都不发
        var offFetcher = new FakeFetcher();
        var offConfig = new IspConfig { Enabled = false };
        using (var service = new IspLookupService(offConfig, new IspCacheStore(Path.Combine(root, "off.json"), TimeSpan.FromDays(30)), offFetcher))
        {
            service.Request("202.96.209.133");
            Thread.Sleep(1200);
            Eq("未启用时不发任何请求", 0, offFetcher.CallCount);
            Check("未启用时状态标记为关闭", !service.Status.Enabled);
        }

        try { Directory.Delete(root, true); } catch { }
    }

    // ---------------- 界面：统计表里的运营商与状态灯 ----------------

    private static void TestIspUi()
    {
        Section("统计表：运营商列与跨网状态灯（假抓取器）");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "ispui_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        // 假抓取器：本端出口 IP 当电信，目标当联通 → 应显示红灯"跨运营商"
        var fetcher = new FakeFetcher
        {
            Result = new IspFetchResult
            {
                Ok = true,
                Records = new List<IspRecord> { Sample.TelecomShanghai, Sample.Unicom169 },
                Remaining = 20,
                ResetAfterSeconds = 60
            }
        };

        IspLookupService.FetcherFactory = _ => fetcher;

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
            TimeoutMs = 500,
            Isp = new IspConfig { Enabled = true },
            Links = { new LinkConfig { Name = "机房B", Host = "221.5.88.88", Port = echoPort } }
        });

        try
        {
            RunOnSta(() =>
            {
                var window = new MainWindow();

                var statsGrid = (DataGrid)window.FindName("StatsGrid")!;
                Check("统计表新增「本端运营商」列", statsGrid.Columns.Any(c => Equals(c.Header, "本端运营商")));
                Check("统计表新增「目标运营商」列", statsGrid.Columns.Any(c => Equals(c.Header, "目标运营商")));
                Check("统计表新增「跨网」列", statsGrid.Columns.Any(c => Equals(c.Header, "跨网")));
                Check("运营商开关默认勾选（可按需关闭）",
                    window.FindName("IspCheck") is CheckBox { IsChecked: true });

                InvokePrivateHandler(window, "Start_Click");

                // 等本端出口 IP 被查到（回显里带 127.0.0.1 的临时端口 → 内网，本地判定不联网）
                // 因此这一轮只验"目标侧 + 未启用时的表现"，本端用假数据直接验 SetCarrier
                WaitUntilPump(() => ((StatSession)GetPrivateField(window, "_session")!).TotalSamples > 0, 6000);

                var rows = (System.Collections.IList)GetPrivateField(window, "_stats")!;
                WaitUntilPump(() => rows.Count > 0, 6000);
                Check("统计表出现数据行", rows.Count > 0, rows.Count.ToString());

                if (rows.Count > 0)
                {
                    var row = (StatRow)rows[0]!;

                    // 查询是后台跑的：等到目标侧出结果再断言（否则会读到"查询中…"）
                    WaitUntilPump(() => row.RemoteCarrier != "查询中…", 6000);

                    Check("本端出口是回环地址 → 不联网查询、显示为内网/未知",
                        row.LocalCarrier is "内网地址" or "未知" or "尚无对端回显（探测还没成功过）",
                        row.LocalCarrier);
                    Check("目标运营商来自查询结果", row.RemoteCarrier == "联通", row.RemoteCarrier);
                    Check("本端不可判定 → 灰灯（不误报跨运营商）", row.CrossNetState == "unknown", row.CrossNetState);
                }

                // 直接验表行的显示取值（不依赖真实公网出口）：电信 ↔ 联通 = 红灯
                var crossRow = new StatRow { Key = "k1", Name = "机房B / TCP", Target = "221.5.88.88:9000" };
                crossRow.SetCarrier(Sample.TelecomShanghai, Sample.Unicom169, enabled: true);
                Eq("红灯：跨运营商", "cross", crossRow.CrossNetState);
                Check("红灯文案给出方向", crossRow.CrossNetText.Contains("跨运营商"), crossRow.CrossNetText);
                Eq("本端运营商列", "电信", crossRow.LocalCarrier);
                Eq("目标运营商列", "联通", crossRow.RemoteCarrier);

                var sameRow = new StatRow { Key = "k2", Name = "机房C / TCP", Target = "202.96.209.133:9000" };
                sameRow.SetCarrier(Sample.TelecomShanghai, Sample.TelecomShanghai, enabled: true);
                Eq("绿灯：同运营商", "same", sameRow.CrossNetState);

                var unknownRow = new StatRow { Key = "k3", Name = "机房D / TCP", Target = "10.0.0.9:9000" };
                unknownRow.SetCarrier(Sample.TelecomShanghai, null, enabled: true);
                Eq("灰灯：数据不足", "unknown", unknownRow.CrossNetState);
                Eq("查不到时目标列显示查询中", "查询中…", unknownRow.RemoteCarrier);

                var offRow = new StatRow { Key = "k4", Name = "机房E / TCP", Target = "202.96.209.133:9000" };
                offRow.SetCarrier(null, null, enabled: false);
                Eq("未启用时不显示灯", "disabled", offRow.CrossNetState);
                Eq("未启用时本端列", "未启用", offRow.LocalCarrier);
                Check("未启用时提示说明不发数据", offRow.CarrierDetail.Contains("不会把 IP 发给"), offRow.CarrierDetail);

                // 关掉开关：行立刻回到"未启用"，且不再查询
                InvokePrivateHandler(window, "Stop_Click");
                var ispCheck = (CheckBox)window.FindName("IspCheck")!;
                ispCheck.IsChecked = false;
                Pump(300);

                Check("关闭运营商查询后不再有查询服务", GetPrivateField(window, "_isp") is null);
                foreach (var item in rows.Cast<StatRow>())
                    Eq("关闭后行显示未启用", "未启用", item.LocalCarrier);

                window.Close();
            }, "运营商界面断言");
        }
        finally
        {
            IspLookupService.FetcherFactory = null;
            ConfigManager.OverridePath = previousOverride;
            echoServer.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>假抓取器：记录收到的批量请求，按需返回预设结果。</summary>
    private sealed class FakeFetcher : IIspFetcher
    {
        private readonly object _gate = new();
        private readonly List<List<string>> _calls = new();

        public IspFetchResult Result { get; set; } = new()
        {
            Ok = true,
            Records = new List<IspRecord> { Sample.TelecomShanghai },
            Remaining = 20,
            ResetAfterSeconds = 60
        };

        public int CallCount
        {
            get { lock (_gate) return _calls.Count; }
        }

        public List<string> this[int index]
        {
            get { lock (_gate) return _calls[index]; }
        }

        public Task<IspFetchResult> FetchAsync(IReadOnlyList<string> ips, CancellationToken ct)
        {
            lock (_gate)
            {
                _calls.Add(ips.ToList());

                // 只回答这次请求里出现过的地址，模拟真实服务端行为
                var records = Result.Records.Where(r => ips.Contains(r.Ip, StringComparer.OrdinalIgnoreCase)).ToList();
                return Task.FromResult(new IspFetchResult
                {
                    Ok = Result.Ok,
                    Error = Result.Error,
                    Records = records,
                    Remaining = Result.Remaining,
                    ResetAfterSeconds = Result.ResetAfterSeconds
                });
            }
        }

        public void WaitForCalls(int count, int timeoutMs = 5000)
            => WaitUntil(() => CallCount >= count, timeoutMs);
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(50);
        }
    }

    /// <summary>在 STA 线程上跑消息泵直到条件成立。</summary>
    private static void WaitUntilPump(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Pump(100);
        }
    }
}
