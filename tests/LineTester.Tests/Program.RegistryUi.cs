using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LineTester.Registry;
using LineTester.Views;

namespace LineTester.Tests;

/// <summary>
/// 切片 3（界面）的自检：注册配置页、中心节点管理、链路页联动、兼容矩阵的最后一行。
///
/// 三块分工：
/// ① 纯逻辑（<see cref="RegistrarDraft"/> / <see cref="RegistryUiText"/> / <see cref="RegistryNodeRow"/>）直接断言；
/// ② 页面的装配与往返在真实 <see cref="RegistryPanel"/> 上断言（STA 线程，不 Show 窗口）；
/// ③ 主窗口联动走真实 <see cref="MainWindow"/>（含落盘与链路编辑器）。
/// </summary>
internal static partial class Program
{
    // ---------------- 注册配置草稿 ----------------

    private static void TestRegistrarDraft()
    {
        Section("注册配置页：草稿往返与校验");

        var source = new RegistrarConfig
        {
            Enabled = true,
            ServeAsCenter = true,
            NodeName = "机房A",
            PublicHost = "203.0.113.9",
            PublicPort = 9000,
            Distributable = true,
            AdvertiseTcp = false,
            AdvertiseUdp = true,
            CenterHost = "203.0.113.1",
            CenterPort = 50000,
            Secret = "s3cret",
            HeartbeatSeconds = 60,
            SyncSeconds = 300,
            RetryMs = 1000,
            MaxRetries = 5,
            NodeId = "0123456789abcdef0123456789abcdef"
        };

        var draft = RegistrarDraft.From(source);
        Eq("草稿：节点名称", "机房A", draft.NodeName);
        Eq("草稿：外网端口转成文本", "9000", draft.PublicPortText);
        Eq("草稿：同步周期", "300", draft.SyncText);
        Eq("草稿：节点身份随配置带入", source.NodeId, draft.NodeId);
        Check("草稿：密钥原样", draft.Secret == "s3cret");
        var noSecret = RegistrarDraft.From(source);
        noSecret.Secret = "";
        Check("注册草稿拒绝空密钥", noSecret.Validate().Contains("预共享密钥"));

        var sharedIp = RegistrarDraft.From(source);
        sharedIp.AdditionalEndpoints.Add(new ExternalEndpointDraft(source.PublicHost, "9001"));
        Eq("同 IP 不同端口可作为额外端点", "", sharedIp.Validate());
        sharedIp.AdditionalEndpoints.Add(new ExternalEndpointDraft(source.PublicHost, "9001"));
        Check("完全相同的 IP 与端口会被拒绝", sharedIp.Validate().Contains("重复"));

        var target = new RegistrarConfig();
        Check("往返：写回配置成功", draft.TryApply(target, out var roundTripError), roundTripError);
        Eq("往返：节点名称", source.NodeName, target.NodeName);
        Eq("往返：外网地址", source.PublicHost, target.PublicHost);
        Eq("往返：外网端口", source.PublicPort, target.PublicPort);
        Eq("往返：中心地址", source.CenterHost, target.CenterHost);
        Eq("往返：中心端口", source.CenterPort, target.CenterPort);
        Eq("往返：心跳周期", source.HeartbeatSeconds, target.HeartbeatSeconds);
        Eq("往返：同步周期", source.SyncSeconds, target.SyncSeconds);
        Eq("往返：重传间隔", source.RetryMs, target.RetryMs);
        Eq("往返：最大重传", source.MaxRetries, target.MaxRetries);
        Eq("往返：节点身份保持不变（不能每次保存都换身份）", source.NodeId, target.NodeId);
        Check("往返：可被 TCP 探测（false 也要带回）", !target.AdvertiseTcp && target.AdvertiseUdp);
        Check("往返：承担中心角色", target.ServeAsCenter);

        // 首次启用：空 NodeId 必须自动生成，否则中心会把同一台机器当成新节点
        var fresh = new RegistrarConfig();
        var freshDraft = RegistrarDraft.From(fresh);
        freshDraft.NodeName = "新节点";
        freshDraft.PublicHost = "203.0.113.20";
        freshDraft.PublicPortText = "9000";
        freshDraft.CenterHost = "203.0.113.1";
        freshDraft.CenterPortText = "50000";
        freshDraft.Secret = "first-secret";
        freshDraft.Enabled = true;
        Check("首次启用可写回", freshDraft.TryApply(fresh, out var freshError), freshError);
        Eq("首次启用自动生成 32 位节点身份", 32, fresh.NodeId.Length);
        Eq("生成的身份也回填到草稿", fresh.NodeId, freshDraft.NodeId);

        // 未启用注册：不校验空字段（与 ConfigManager.ValidateRegistrar 口径一致）
        var empty = new RegistrarDraft { Enabled = false };
        Eq("未启用时不做任何校验", "", empty.Validate());

        // 逐项校验：每条都要能定位到具体字段
        var bad = RegistrarDraft.From(source);
        bad.NodeName = "  ";
        Check("空节点名称被拦下", bad.Validate().Contains("节点名称"), bad.Validate());

        bad = RegistrarDraft.From(source);
        bad.PublicHost = "";
        Check("空外网地址被拦下并说明原因", bad.Validate().Contains("外网地址"), bad.Validate());

        bad = RegistrarDraft.From(source);
        bad.PublicPortText = "abc";
        Check("非数字外网端口被拦下", bad.Validate().Contains("外网端口"), bad.Validate());

        bad = RegistrarDraft.From(source);
        bad.PublicPortText = "70000";
        Check("越界外网端口被拦下", bad.Validate().Contains("外网端口"), bad.Validate());

        bad = RegistrarDraft.From(source);
        bad.CenterHost = "";
        Check("空中心地址被拦下", bad.Validate().Contains("注册中心地址"), bad.Validate());

        bad = RegistrarDraft.From(source);
        bad.CenterPortText = "0";
        Check("中心端口 0 被拦下", bad.Validate().Contains("注册中心端口"), bad.Validate());

        bad = RegistrarDraft.From(source);
        bad.HeartbeatText = (RegistrarConfig.MinIntervalSeconds - 1).ToString();
        Check("心跳周期过小被拦下", bad.Validate().Contains("心跳周期"), bad.Validate());

        bad = RegistrarDraft.From(source);
        bad.SyncText = (RegistrarConfig.MaxIntervalSeconds + 1).ToString();
        Check("同步周期过大被拦下", bad.Validate().Contains("同步周期"), bad.Validate());

        bad = RegistrarDraft.From(source);
        bad.RetryText = "10";
        Check("重传间隔过小被拦下", bad.Validate().Contains("重传间隔"), bad.Validate());

        bad = RegistrarDraft.From(source);
        bad.MaxRetriesText = (RegistrarConfig.MaxRetriesLimit + 1).ToString();
        Check("最大重传次数过大被拦下", bad.Validate().Contains("最大重传次数"), bad.Validate());

        // 多个问题要一次列全，而不是让用户改一个报一个
        bad = RegistrarDraft.From(source);
        bad.NodeName = "";
        bad.CenterHost = "";
        Check("多个问题一次列全", bad.Validate().Split('；').Length >= 2, bad.Validate());

        // 拦下时必须不改动配置：否则会出现"改了一半"的配置被落盘
        var untouched = RegistrarDraft.From(source);
        untouched.CenterPortText = "99999";
        var before = (target.NodeName, target.PublicHost, target.PublicPort, target.CenterHost, target.CenterPort);
        Check("非法草稿写回失败", !untouched.TryApply(target, out _));
        Eq("失败后节点名称未被改写", before.NodeName, target.NodeName);
        Eq("失败后中心端口未被改写", before.CenterPort, target.CenterPort);

        // 边界值：两端都要能通过
        var bounds = RegistrarDraft.From(source);
        bounds.PublicPortText = "1";
        bounds.CenterPortText = "65535";
        bounds.HeartbeatText = RegistrarConfig.MinIntervalSeconds.ToString();
        bounds.SyncText = RegistrarConfig.MaxIntervalSeconds.ToString();
        bounds.RetryText = RegistrarConfig.MinRetryMs.ToString();
        bounds.MaxRetriesText = "0";
        Eq("边界值全部通过", "", bounds.Validate());

        // 主机名是合法的外网地址（可分发规则允许主机名）
        var hostname = RegistrarDraft.From(source);
        hostname.PublicHost = "vpn.example.com";
        Eq("主机名形式的外网地址通过校验", "", hostname.Validate());

        // ---- 现场反馈：映射端口（例如 16298）绝不能被判成越界 ----
        var mapped = RegistrarDraft.From(source);
        mapped.PublicPortText = "16298";
        mapped.CenterPortText = "16298";
        Eq("映射端口 16298 两端都合法", "", mapped.Validate());

        var minMax = RegistrarDraft.From(source);
        minMax.PublicPortText = "1";
        minMax.CenterPortText = "65535";
        Eq("端口边界 1 与 65535 都合法", "", minMax.Validate());

        // 报错时必须回显"实际读到的是什么"：否则只会看到"必须是 1~65535"，
        // 而自己明明填了 16298（其实是空值 / 全角 / 混了字符）
        var blank = RegistrarDraft.From(source);
        blank.PublicPortText = "";
        var blankError = blank.Validate();
        Check("外网端口为空时提示“未填写”而不是笼统的整数要求",
            blankError.Contains("本端外网端口未填写"), blankError);

        var fullWidth = RegistrarDraft.From(source);
        fullWidth.PublicPortText = "１６２９８"; // 中文输入法全角数字
        var fullWidthError = fullWidth.Validate();
        Check("全角数字被拦下并回显原样内容",
            fullWidthError.Contains("本端外网端口") && fullWidthError.Contains("１６２９８") &&
            fullWidthError.Contains("全角"), fullWidthError);

        var spaced = RegistrarDraft.From(source);
        spaced.PublicPortText = " 16298 ";
        Eq("前后空格自动忽略（16298 仍然合法）", "", spaced.Validate());

        var overRange = RegistrarDraft.From(source);
        overRange.PublicPortText = "70000";
        var overRangeError = overRange.Validate();
        Check("越界端口提示里带上当前值",
            overRangeError.Contains("之间") && overRangeError.Contains("70000"), overRangeError);

        var blankCenterPort = RegistrarDraft.From(source);
        blankCenterPort.CenterPortText = "";
        Check("中心端口为空时明确指出是哪一个框",
            blankCenterPort.Validate().Contains("注册中心端口未填写"), blankCenterPort.Validate());

        // ---- 地址格式：只判"非空"是不够的，填错地址的现象是"链路全不通"，极难定位 ----
        var partialIp = RegistrarDraft.From(source);
        partialIp.PublicHost = "203.0.113";
        var partialError = partialIp.Validate();
        Check("少一段的 IPv4 被拦下并回显原样内容",
            partialError.Contains("本端外网地址") && partialError.Contains("合法的 IPv4") &&
            partialError.Contains("203.0.113"), partialError);

        var overOctet = RegistrarDraft.From(source);
        overOctet.PublicHost = "203.0.113.999";
        Check("越界段（999）被拦下",
            overOctet.Validate().Contains("合法的 IPv4"), overOctet.Validate());

        var commaIp = RegistrarDraft.From(source);
        commaIp.PublicHost = "203,0,113,9";
        Check("用逗号分隔的地址被拦下并提示用半角点",
            commaIp.Validate().Contains("非法字符"), commaIp.Validate());

        var fullWidthIp = RegistrarDraft.From(source);
        fullWidthIp.PublicHost = "２０３.0.113.9";
        Check("全角数字的地址被拦下", fullWidthIp.Validate().Contains("本端外网地址"), fullWidthIp.Validate());

        var badCenterIp = RegistrarDraft.From(source);
        badCenterIp.CenterHost = "203.0.113";
        Check("中心地址少一段也被拦下",
            badCenterIp.Validate().Contains("注册中心地址"), badCenterIp.Validate());

        var loopbackIp = RegistrarDraft.From(source);
        loopbackIp.PublicHost = "127.0.0.1";
        Eq("回环地址本身是合法 IPv4（是否分发由地址规则在别处判断）", "", loopbackIp.Validate());

        // ---- 纯客户端：只想测别人、不需要被别人测 → 不要求自己的外网地址/端口 ----
        var clientOnly = RegistrarDraft.From(source);
        clientOnly.Distributable = false;
        clientOnly.PublicHost = "";
        clientOnly.PublicPortText = "";
        Eq("关闭「参与网状」后，本端外网地址/端口可以留空", "", clientOnly.Validate());

        var clientOnlyBadPort = RegistrarDraft.From(source);
        clientOnlyBadPort.Distributable = false;
        clientOnlyBadPort.PublicPortText = "70000";
        Check("关闭「参与网状」但填了越界端口仍然拦下（填了就要填对）",
            clientOnlyBadPort.Validate().Contains("本端外网端口"), clientOnlyBadPort.Validate());

        var clientOnlyWrite = new RegistrarConfig();
        var clientOnlyDraft = RegistrarDraft.From(source);
        clientOnlyDraft.Distributable = false;
        clientOnlyDraft.PublicHost = "";
        clientOnlyDraft.PublicPortText = "";
        Check("纯客户端配置可写回", clientOnlyDraft.TryApply(clientOnlyWrite, out var clientOnlyError), clientOnlyError);
        Eq("纯客户端的外网端口写回为 0", 0, clientOnlyWrite.PublicPort);
        Eq("纯客户端的外网地址写回为空", "", clientOnlyWrite.PublicHost);
        Eq("纯客户端仍要填中心地址", "203.0.113.1", clientOnlyWrite.CenterHost);

        Check("配置层校验同样放行纯客户端（Distributable=false）",
            ConfigManager.Validate(new AppConfig { Registrar = clientOnlyWrite }, out var configError), configError);

        // ---- 中心侧：纯客户端（无地址、不可分发）必须能注册上，否则它连别人都测不了 ----
        var storeRoot = Path.Combine(AppContext.BaseDirectory, ".testtmp", "clientonly_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);
        var store = new RegistryStore(Path.Combine(storeRoot, "registry.json"), 60);

        var clientOnlyNode = store.Upsert(new RegisterMessage
        {
            NodeId = "client-only",
            Name = "只看别人",
            Ip = "",
            Port = 0,
            Tcp = false,
            Udp = false,
            Distributable = false
        }, authenticated: true);

        Check("纯客户端能注册（不参与网状 → 不做地址检查）", clientOnlyNode.Accepted,
            clientOnlyNode.ErrorCode + " " + clientOnlyNode.ErrorMessage);
        Check("纯客户端照常出现在中心清单里（供运维查看）",
            store.Nodes.Any(n => n.NodeId == "client-only"));
        Eq("纯客户端不会被分发出去（快照里没有它）", 0, store.BuildSnapshot().Count);

        // 参与网状的节点仍然必须提供可用地址（回环会让别人测到自己）
        var loopbackNode = store.Upsert(new RegisterMessage
        {
            NodeId = "loop-node",
            Name = "回环节点",
            Ip = "127.0.0.1",
            Port = 8800,
            Distributable = true
        }, authenticated: true);
        Check("参与网状的节点填回环地址仍被拒绝", !loopbackNode.Accepted);
        Eq("拒绝码为 loopback", RegistryErrorCodes.Loopback, loopbackNode.ErrorCode);

        var row = RegistryNodeRow.FromNode(store.Nodes.First(n => n.NodeId == "client-only"),
            DateTime.UtcNow, (_, _) => true);
        Check("无地址的节点在中心表里显示为“未提供”，而不是 :0",
            row.Address.Contains("未提供"), row.Address);

        try { Directory.Delete(storeRoot, true); } catch { }
    }

    // ---------------- 文案 ----------------

    private static void TestRegistryUiText()
    {
        Section("注册页文案：连接状态 / 对外地址核对 / 分发状态");

        var now = new DateTime(2026, 9, 20, 12, 3, 0, DateTimeKind.Utc);

        var disabled = new RegistrarConfig();
        Check("未启用注册：状态栏不显示注册信息", RegistryUiText.DescribeConnection(null, disabled, now) is null);
        Check("未启用注册：不显示对外地址核对", RegistryUiText.DescribeSelfCheck(null, disabled) is null);
        Check("未启用注册：不显示分发状态", RegistryUiText.DescribeSelfDistribution(disabled) is null);

        var config = new RegistrarConfig
        {
            Enabled = true,
            NodeName = "机房A",
            PublicHost = "203.0.113.9",
            PublicPort = 9000,
            CenterHost = "203.0.113.1",
            CenterPort = 50000
        };

        var notRunning = RegistryUiText.DescribeConnection(null, config, now);
        Check("启用但未启动：提示需先启动测试", notRunning is { Warning: true } &&
            notRunning.Text.Contains("未运行"), notRunning?.Text);

        var waiting = RegistryUiText.DescribeConnection(new RegistryClientStatus { Running = true }, config, now);
        Check("已启动未回执：提示等待首次回执",
            waiting is { Warning: false } && waiting.Text.Contains("等待首次回执"), waiting?.Text);

        var healthy = RegistryUiText.DescribeConnection(new RegistryClientStatus
        {
            Running = true,
            LastSuccessUtc = now.AddSeconds(-20),
            Revision = 138,
            NodeCount = 6
        }, config, now);
        Check("正常：状态栏给出中心地址/rev/节点数",
            healthy is { Warning: false } &&
            healthy.Text.Contains("203.0.113.1:50000") &&
            healthy.Text.Contains("rev 138") &&
            healthy.Text.Contains("节点 6"), healthy?.Text);
        Check("正常：状态栏带同步时刻",
            healthy!.Text.Contains(now.AddSeconds(-20).ToLocalTime().ToString("HH:mm")), healthy.Text);

        var unreachable = RegistryUiText.DescribeConnection(new RegistryClientStatus
        {
            Running = true,
            LastAttemptUtc = now,
            LastSuccessUtc = now.AddMinutes(-4),
            LastError = "中心无应答（超时）"
        }, config, now);
        Check("不可达：给出原因与已持续时长",
            unreachable is { Warning: true } &&
            unreachable.Text.Contains("不可达") &&
            unreachable.Text.Contains("已 4 分钟") &&
            unreachable.Text.Contains("退避中"), unreachable?.Text);

        // ---- 对外地址核对 ----
        var noReply = RegistryUiText.DescribeSelfCheck(null, config);
        Check("未收到回执：说明外网地址必须手工填写",
            noReply is { Warning: false } && noReply.Text.Contains("手工填写"), noReply?.Text);

        var matched = RegistryUiText.DescribeSelfCheck(
            new RegistryClientStatus { Running = true, SeenFrom = "203.0.113.9:9000" }, config);
        Check("IP 与端口都一致：给出确认", matched is { Warning: false } && matched.Text.StartsWith("✓"), matched?.Text);

        var portDiff = RegistryUiText.DescribeSelfCheck(
            new RegistryClientStatus { Running = true, SeenFrom = "203.0.113.9:51234" }, config);
        Check("IP 一致但端口不同：提示这是出向 NAT 的正常现象而不是错误",
            portDiff is { Warning: true } &&
            portDiff.Text.Contains("51234") && portDiff.Text.Contains("9000") &&
            portDiff.Text.Contains("正常现象"), portDiff?.Text);

        var ipDiff = RegistryUiText.DescribeSelfCheck(
            new RegistryClientStatus { Running = true, SeenFrom = "198.51.100.7:51234" }, config);
        Check("IP 不同：提示确认地址是否可达",
            ipDiff is { Warning: true } && ipDiff.Text.Contains("198.51.100.7"), ipDiff?.Text);

        var hostname = new RegistrarConfig
        {
            Enabled = true, PublicHost = "vpn.example.com", PublicPort = 9000,
            CenterHost = "203.0.113.1", CenterPort = 50000
        };
        var hostnameCheck = RegistryUiText.DescribeSelfCheck(
            new RegistryClientStatus { Running = true, SeenFrom = "203.0.113.9:51234" }, hostname);
        Check("填的是主机名：不误报为错误，只说无法直接比对",
            hostnameCheck is { Warning: false } && hostnameCheck.Text.Contains("主机名"), hostnameCheck?.Text);

        // ---- 分发状态 ----
        var normal = RegistryUiText.DescribeSelfDistribution(config);
        Check("正常：说明可被其他节点探测",
            normal is { Warning: false } && normal.Text.Contains("203.0.113.9:9000") &&
            normal.Text.Contains("TCP + UDP"), normal?.Text);

        var loopback = new RegistrarConfig
        {
            Enabled = true, PublicHost = "127.0.0.1", PublicPort = 9000,
            CenterHost = "127.0.0.1", CenterPort = 9000
        };
        var loopbackNote = RegistryUiText.DescribeSelfDistribution(loopback);
        Check("回环地址：明确提示不会被分发（否则其他节点测的是自己的回环 = 假线路）",
            loopbackNote is { Warning: true } &&
            loopbackNote.Text.Contains("不可分发") &&
            loopbackNote.Text.Contains("不会出现在别人的链路里"), loopbackNote?.Text);

        var offGrid = new RegistrarConfig { Enabled = true, Distributable = false, PublicHost = "203.0.113.9", PublicPort = 9000 };
        var offGridNote = RegistryUiText.DescribeSelfDistribution(offGrid);
        Check("关闭参与网状：说明不会被分发（不是错误）",
            offGridNote is { Warning: false } && offGridNote.Text.Contains("参与网状"), offGridNote?.Text);

        var noAdvertise = new RegistrarConfig
        {
            Enabled = true, PublicHost = "203.0.113.9", PublicPort = 9000,
            AdvertiseTcp = false, AdvertiseUdp = false
        };
        var noAdvertiseNote = RegistryUiText.DescribeSelfDistribution(noAdvertise);
        Check("两个可被探测协议都关掉：说明会出现链路但不会被发包",
            noAdvertiseNote is { Warning: false } && noAdvertiseNote.Text.Contains("不会向本机发包"), noAdvertiseNote?.Text);

        var emptyHost = new RegistrarConfig { Enabled = true, PublicPort = 9000 };
        Check("未填外网地址：标红",
            RegistryUiText.DescribeSelfDistribution(emptyHost) is { Warning: true });

        // 协议文案必须覆盖 ICMP：中心节点表的「协议」列曾经因为漏传 ICMP 而少显示一个协议
        Eq("协议文案：全开", "TCP + UDP + ICMP", RegistryUiText.DescribeProtocols(true, true, true));
        Eq("协议文案：仅 TCP", "仅 TCP", RegistryUiText.DescribeProtocols(true, false, false));
        Eq("协议文案：仅 UDP", "仅 UDP", RegistryUiText.DescribeProtocols(false, true, false));
        Eq("协议文案：仅 ICMP", "仅 ICMP", RegistryUiText.DescribeProtocols(false, false, true));
        Eq("协议文案：TCP+UDP", "TCP + UDP", RegistryUiText.DescribeProtocols(true, true, false));
        Eq("协议文案：都未勾选", "都未勾选", RegistryUiText.DescribeProtocols(false, false, false));

        // 只宣告 ICMP 的节点不能被说成"协议都未勾选"
        var icmpOnly = new RegistrarConfig
        {
            Enabled = true, PublicHost = "203.0.113.9", PublicPort = 16298,
            AdvertiseTcp = false, AdvertiseUdp = false, AdvertiseIcmp = true
        };
        var icmpOnlyNote = RegistryUiText.DescribeSelfDistribution(icmpOnly);
        Check("只宣告 ICMP 时状态说明包含 ICMP 且不报「都未勾选」",
            icmpOnlyNote is not null && icmpOnlyNote.Text.Contains("ICMP") && !icmpOnlyNote.Text.Contains("都未勾选"),
            icmpOnlyNote?.Text ?? "null");

        var noneAdvertised = new RegistrarConfig
        {
            Enabled = true, PublicHost = "203.0.113.9", PublicPort = 16298,
            AdvertiseTcp = false, AdvertiseUdp = false, AdvertiseIcmp = false
        };
        var noneNote = RegistryUiText.DescribeSelfDistribution(noneAdvertised);
        Check("三个协议都不宣告时才提示「都未勾选」",
            noneNote is not null && noneNote.Text.Contains("都未勾选"),
            noneNote?.Text ?? "null");

        // ---- 节点状态与时间 ----
        Eq("节点状态：在线+分发+认证", "在线 · 分发中 · 已认证",
            RegistryUiText.DescribeNodeState(true, true, false, true, false));
        Eq("节点状态：中心隐藏", "在线 · 已隐藏 · 已认证",
            RegistryUiText.DescribeNodeState(true, true, true, true, false));
        Eq("节点状态：对端自己关了分发", "在线 · 对端关分发 · 已认证",
            RegistryUiText.DescribeNodeState(true, false, false, true, false));
        Eq("节点状态：无密钥", "在线 · 分发中 · 无密钥",
            RegistryUiText.DescribeNodeState(true, true, false, false, false));
        Check("节点状态：疑似重复 nodeId 要显眼",
            RegistryUiText.DescribeNodeState(true, true, false, true, true).Contains("疑似重复"));

        Eq("相对时间：刚刚", "刚刚", RegistryUiText.DescribeAgo(TimeSpan.FromSeconds(30)));
        Eq("相对时间：分钟", "3 分钟前", RegistryUiText.DescribeAgo(TimeSpan.FromMinutes(3)));
        Eq("相对时间：小时", "2 小时前", RegistryUiText.DescribeAgo(TimeSpan.FromHours(2)));
        Eq("相对时间：天", "4 天前", RegistryUiText.DescribeAgo(TimeSpan.FromDays(4)));
        Eq("相对时间：时钟偏差不算负数", "刚刚", RegistryUiText.DescribeAgo(TimeSpan.FromSeconds(-30)));

        var lastSeen = RegistryUiText.DescribeLastSeen(new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 20, 12, 5, 0, DateTimeKind.Utc));
        Check("最后心跳：本地时刻 + 相对时间",
            lastSeen.Contains("5 分钟前") && lastSeen.Contains(":"), lastSeen);
    }

    // ---------------- 中心节点行 ----------------

    private static void TestRegistryNodeRow()
    {
        Section("注册中心管理：节点行（分发勾选写中心）");

        var node = new RegistryNode
        {
            NodeId = "peer-a",
            Name = "机房B",
            Ip = "203.0.113.10",
            Port = 9000,
            Tcp = true,
            Udp = false,
            Online = true,
            Authenticated = true,
            LastSeenUtc = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc)
        };

        var calls = new List<(string NodeId, bool Hidden)>();
        var row = RegistryNodeRow.FromNode(node, node.LastSeenUtc.AddSeconds(30),
            (id, hidden) => { calls.Add((id, hidden)); return true; });

        Eq("行：名称", "机房B", row.Name);
        Eq("行：地址", "203.0.113.10:9000", row.Address);
        Eq("行：协议列", "仅 TCP", row.Protocols);
        Eq("行：默认参与分发", true, row.Distributed);
        Check("行：状态列含在线与认证", row.State.Contains("在线") && row.State.Contains("已认证"), row.State);
        Check("行：可写分发", row.CanDistribute);

        row.Distributed = false;
        Eq("取消分发：写中心侧 centerHidden=true", (true, true), calls.Count == 1 ? (true, calls[0].Hidden) : (false, false));
        Eq("取消分发：回调带上 nodeId", "peer-a", calls[0].NodeId);
        Check("取消分发后状态列显示已隐藏", row.State.Contains("已隐藏"), row.State);
        Check("取消分发后复选框为未勾选", !row.Distributed);

        // 写不进去（节点已被移除）时必须回滚显示，不能骗用户"已经隐藏了"
        var refusing = RegistryNodeRow.FromNode(node, node.LastSeenUtc, (_, _) => false);
        refusing.Distributed = false;
        Check("写入失败时勾选状态回滚", refusing.Distributed, "写不进去却显示已取消勾选");

        var removed = RegistryNodeRow.FromRemoved("peer-b", (_, _) => throw new InvalidOperationException("已移除的节点不该写分发"));
        Check("已移除行有标记", removed.IsRemoved);
        Check("已移除行不可写分发", !removed.CanDistribute);
        Check("已移除行不勾选分发", !removed.Distributed);
        Check("已移除行状态说明需在中心侧恢复", removed.State.Contains("恢复"), removed.State);
        removed.Distributed = true; // 不应抛异常，也不应写中心
        Check("已移除行写分发被忽略", !removed.Distributed);
    }

    // ---------------- 注册页（真实控件） ----------------

    private static void TestRegistryPanelUi()
    {
        Section("注册页：真实控件的填充 / 读回 / 状态 / 节点表");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "panel_" + Guid.NewGuid().ToString("N"));

        RunOnSta(() =>
        {
            var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

            var panel = new RegistryPanel();
            Check("注册页装配成功", true);

            Check("含“参与注册同步”开关", panel.FindName("EnabledCheck") is CheckBox);
            Check("含节点名称输入框", panel.FindName("NodeNameBox") is TextBox);
            Check("含本端外网地址输入框（标准 IPv4 四段式）", panel.FindName("PublicHostBox") is Ipv4Box);
            Check("含注册中心地址输入框（标准 IPv4 四段式）", panel.FindName("CenterHostBox") is Ipv4Box);
            Check("含密钥密码框", panel.FindName("SecretBox") is PasswordBox);
            Check("含“显示密钥”开关", panel.FindName("ShowSecretCheck") is CheckBox);
            Check("含可被 TCP 探测开关", panel.FindName("AdvertiseTcpCheck") is CheckBox);
            Check("含可被 UDP 探测开关", panel.FindName("AdvertiseUdpCheck") is CheckBox);
            Check("含立即注册按钮", panel.FindName("RegisterNowButton") is Button);
            Check("含中心节点表", panel.FindName("NodeGrid") is DataGrid);
            Check("含外网端点表（一机房多端点）", panel.FindName("PublicEndpointsGrid") is DataGrid);
            Check("含「添加」外网地址按钮", panel.FindName("AddPublicHostButton") is Button);
            Check("含「删除所选」外网地址按钮", panel.FindName("RemovePublicHostButton") is Button);

            var config = new RegistrarConfig
            {
                Enabled = true,
                NodeName = "机房A",
                PublicHost = "203.0.113.9",
                PublicPort = 9000,
                AdvertiseUdp = true,
                CenterHost = "203.0.113.1",
                CenterPort = 50000,
                Secret = "s3cret",
                HeartbeatSeconds = 60,
                SyncSeconds = 300,
                RetryMs = 1000,
                MaxRetries = 5,
                NodeId = "0123456789abcdef0123456789abcdef"
            };

            panel.Load(config);

            Eq("界面按配置填充节点名称", "机房A", ((TextBox)panel.FindName("NodeNameBox")!).Text);
            Eq("界面按配置填充外网地址", "203.0.113.9", ((Ipv4Box)panel.FindName("PublicHostBox")!).Text);
            var endpointGrid = (DataGrid)panel.FindName("PublicEndpointsGrid")!;
            Eq("单地址时额外端点表为空", 0, endpointGrid.Items.Count);
            // ---- 一机房多端点：IP 与端口成对编辑 / 添加 / 删除 / 往返 ----
            var multiConfig = new RegistrarConfig
            {
                Enabled = true,
                NodeName = "东莞机房",
                PublicHost = "120.233.128.228",
                AdditionalEndpoints = new List<ExternalEndpoint>
                {
                    new() { Ip = "157.255.153.208", Port = 16298 },
                    new() { Ip = "183.60.150.36", Port = 16299 }
                },
                PublicPort = 16298,
                CenterHost = "203.0.113.1",
                CenterPort = 16298,
                Secret = "s3cret"
            };

            panel.Load(multiConfig);
            Eq("额外端点表显示两行", 2, endpointGrid.Items.Count);
            Eq("第一行额外端点 IP", "157.255.153.208", ((ExternalEndpointDraft)endpointGrid.Items[0]!).Ip);
            Eq("第二行端口独立保存", "16299", ((ExternalEndpointDraft)endpointGrid.Items[1]!).PortText);

            var multiTarget = new RegistrarConfig();
            Check("多地址配置可读回", panel.TryReadInto(multiTarget, out var multiError), multiError);
            Eq("读回：主地址", "120.233.128.228", multiTarget.PublicHost);
            Eq("读回：其余地址", "157.255.153.208,183.60.150.36", string.Join(",", multiTarget.PublicHosts));
            Eq("读回：各端点独立端口", "16298,16299", string.Join(",", multiTarget.AdditionalEndpoints.Select(e => e.Port)));

            var pickAlerts = new List<string>();
            panel.Alert = (message, _) => pickAlerts.Add(message);

            ((Button)panel.FindName("AddPublicHostButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Eq("添加后额外端点表有三行", 3, endpointGrid.Items.Count);
            ((ExternalEndpointDraft)endpointGrid.Items[2]!).Ip = "221.5.88.88";
            ((ExternalEndpointDraft)endpointGrid.Items[2]!).PortText = "16300";

            ((Button)panel.FindName("AddPublicHostButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((ExternalEndpointDraft)endpointGrid.Items[3]!).Ip = "221.5.88.88";
            ((ExternalEndpointDraft)endpointGrid.Items[3]!).PortText = "16300";
            Check("重复 IP 与端口被拦下", !panel.TryReadInto(new RegistrarConfig(), out var duplicateError) &&
                duplicateError.Contains("重复"), duplicateError);
            ((ExternalEndpointDraft)endpointGrid.Items[3]!).PortText = "16301";
            Check("相同 IP 不同端口允许", panel.TryReadInto(new RegistrarConfig(), out var differentPortError), differentPortError);
            endpointGrid.SelectedItem = endpointGrid.Items[3];
            ((Button)panel.FindName("RemovePublicHostButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var afterAdd = new RegistrarConfig();
            Check("添加后可读回", panel.TryReadInto(afterAdd, out var addError), addError);
            Eq("读回包含新地址", "157.255.153.208,183.60.150.36,221.5.88.88",
                string.Join(",", afterAdd.PublicHosts));

            endpointGrid.SelectedItem = endpointGrid.Items[0];
            ((Button)panel.FindName("RemovePublicHostButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Eq("删除后额外端点表有两行", 2, endpointGrid.Items.Count);

            var afterRemove = new RegistrarConfig();
            Check("删除后可读回", panel.TryReadInto(afterRemove, out var removeError), removeError);
            Eq("被删的地址不再出现", "183.60.150.36,221.5.88.88", string.Join(",", afterRemove.PublicHosts));
            Check("主地址仍是第一个", afterRemove.PublicHost == "120.233.128.228", afterRemove.PublicHost);

            endpointGrid.SelectedItems.Clear();
            foreach (var item in endpointGrid.Items.Cast<object>().ToList()) endpointGrid.SelectedItems.Add(item);
            ((Button)panel.FindName("RemovePublicHostButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Ipv4Box)panel.FindName("PublicHostBox")!).Text = "";
            Check("地址全部清空后被拦下",
                !panel.TryReadInto(new RegistrarConfig(), out var emptyHostError) &&
                emptyHostError.Contains("本端外网地址"), emptyHostError);

            panel.Load(config); // 还原成单地址配置，继续后面的断言

            Eq("界面按配置填充外网端口", "9000", ((TextBox)panel.FindName("PublicPortBox")!).Text);
            Eq("界面按配置填充中心地址", "203.0.113.1", ((Ipv4Box)panel.FindName("CenterHostBox")!).Text);
            Eq("界面按配置填充密钥", "s3cret", ((PasswordBox)panel.FindName("SecretBox")!).Password);
            Eq("界面显示节点身份", config.NodeId, ((TextBlock)panel.FindName("NodeIdText")!).Text);
            Check("密钥默认隐藏", ((PasswordBox)panel.FindName("SecretBox")!).Visibility == Visibility.Visible &&
                                  ((TextBox)panel.FindName("SecretTextBox")!).Visibility == Visibility.Collapsed);

            // 改界面 → 读回配置
            ((TextBox)panel.FindName("NodeNameBox")!).Text = "机房A-新";
            ((TextBox)panel.FindName("PublicPortBox")!).Text = "9100";
            ((CheckBox)panel.FindName("AdvertiseTcpCheck")!).IsChecked = false;

            var target = new RegistrarConfig();
            Check("界面值可读回配置", panel.TryReadInto(target, out var readError), readError);
            Eq("读回：节点名称", "机房A-新", target.NodeName);
            Eq("读回：外网端口", 9100, target.PublicPort);
            Check("读回：取消勾选“可被 TCP 探测”生效", !target.AdvertiseTcp && target.AdvertiseUdp);
            Eq("读回：节点身份沿用配置（不会每次保存换身份）", config.NodeId, target.NodeId);

            // 非法输入：拦下且不改动已有配置
            ((TextBox)panel.FindName("NodeNameBox")!).Text = "   ";
            var keepName = target.NodeName;
            var keepPort = target.PublicPort;
            Check("非法输入被界面拦下", !panel.TryReadInto(target, out var badError));
            Check("拦下时给出可读原因", badError.Contains("节点名称"), badError);
            Eq("拦下时节点名称未被改写", keepName, target.NodeName);
            Eq("拦下时端口未被改写", keepPort, target.PublicPort);

            ((TextBox)panel.FindName("NodeNameBox")!).Text = "机房A-新";

            // 现场报错的那个场景：机房映射端口 16298 填进来必须能通过，且两组端口互不干扰
            ((TextBox)panel.FindName("PublicPortBox")!).Text = "16298";
            ((TextBox)panel.FindName("CenterPortBox")!).Text = "16298";
            var mappedTarget = new RegistrarConfig();
            Check("界面里填映射端口 16298 能读回配置",
                panel.TryReadInto(mappedTarget, out var mappedError), mappedError);
            Eq("读回：本端外网端口 16298", 16298, mappedTarget.PublicPort);
            Eq("读回：注册中心端口 16298", 16298, mappedTarget.CenterPort);

            // 留空时必须说清是哪个框、它是什么（原来只说"必须是 1~65535"，现场以为程序算错了）
            ((TextBox)panel.FindName("PublicPortBox")!).Text = "";
            Check("界面里外网端口留空时，提示指出具体是哪个框",
                !panel.TryReadInto(new RegistrarConfig(), out var emptyPortError) &&
                emptyPortError.Contains("本端外网端口未填写"), emptyPortError);
            ((TextBox)panel.FindName("PublicPortBox")!).Text = "16298";

            // 密钥“显示”切换：两个输入框必须同步，不能一个显示旧值
            ((CheckBox)panel.FindName("ShowSecretCheck")!).IsChecked = true;
            var secretText = (TextBox)panel.FindName("SecretTextBox")!;
            Eq("切到“显示”后文本框拿到密钥", "s3cret", secretText.Text);
            Eq("“显示”时密码框隐藏", Visibility.Collapsed, ((PasswordBox)panel.FindName("SecretBox")!).Visibility);
            secretText.Text = "s3cret2";
            Eq("改文本框会同步回密码框", "s3cret2", ((PasswordBox)panel.FindName("SecretBox")!).Password);
            Eq("“显示”状态下以文本框为权威值", "s3cret2",
                panel.TryReadInto(target, out _) ? target.Secret : "读回失败");

            // 状态区
            panel.ShowStatus(new RegistryClientStatus
            {
                Running = true,
                LastSuccessUtc = now.AddSeconds(-30),
                Revision = 138,
                NodeCount = 6,
                SeenFrom = "203.0.113.9:51234"
            }, config, now);

            var connection = (TextBlock)panel.FindName("ConnectionText")!;
            var selfCheck = (TextBlock)panel.FindName("SelfCheckText")!;
            var distribution = (TextBlock)panel.FindName("SelfDistributionText")!;
            var centerSection = (Border)panel.FindName("CenterSection")!;

            Check("状态区显示中心地址与 revision",
                connection.Text.Contains("203.0.113.1:50000") && connection.Text.Contains("rev 138"), connection.Text);
            Check("状态区显示对外地址比对结果", selfCheck.Text.Contains("51234"), selfCheck.Text);
            Check("状态区显示本节点分发状态", distribution.Text.Contains("203.0.113.9:9000"), distribution.Text);
            Eq("未承担中心角色时隐藏中心管理区", Visibility.Collapsed, centerSection.Visibility);
            Check("未承担中心角色时不显示节点表内容",
                panel.FindName("NodeGrid") is DataGrid idleGrid && (idleGrid.ItemsSource as ICollection)?.Count == 0);

            config.ServeAsCenter = true;
            panel.ShowStatus(null, config, now);
            Eq("承担中心角色时显示中心管理区", Visibility.Visible, centerSection.Visibility);
            Check("未运行时状态区提示需先启动测试",
                connection.Text.Contains("未运行"), connection.Text);

            // 中心节点表：真实 RegistryStore
            Directory.CreateDirectory(root);
            var store = new RegistryStore(Path.Combine(root, "registry.json"), 60);
            store.Upsert(new RegisterMessage
            {
                NodeId = "peer-a", Name = "机房B", Ip = "203.0.113.10", Port = 9000, Tcp = true, Udp = true
            }, authenticated: true);
            store.Upsert(new RegisterMessage
            {
                NodeId = "peer-b", Name = "机房C", Ip = "203.0.113.11", Port = 9000, Tcp = false, Udp = true
            }, authenticated: false);
            store.Save();

            panel.ShowCenter(store, now, force: true);

            var grid = (DataGrid)panel.FindName("NodeGrid")!;
            var rows = ((IEnumerable)grid.ItemsSource!).Cast<RegistryNodeRow>().ToList();
            Eq("节点表列出中心清单里的全部节点", 2, rows.Count);
            Check("节点表显示协议列", rows.Any(r => r.Protocols == "TCP + UDP") && rows.Any(r => r.Protocols == "仅 UDP"),
                string.Join(" / ", rows.Select(r => r.Protocols)));
            Check("未认证的节点被标出", rows.Any(r => r.State.Contains("无密钥")),
                string.Join(" / ", rows.Select(r => r.State)));

            // 分发勾选写中心侧（不勾选 → centerHidden）
            var peerB = rows.First(r => r.NodeId == "peer-a");
            peerB.Distributed = false;
            Check("取消分发写入中心清单",
                store.Nodes.First(n => n.NodeId == "peer-a").CenterHidden && !store.Nodes.First(n => n.NodeId == "peer-a").IsDistributed);
            Check("取消分发后不会再被分发出去（快照里没有它）",
                store.BuildSnapshot().All(n => n.NodeId != "peer-a"));
            Check("取消分发后状态列变成已隐藏", peerB.State.Contains("已隐藏"), peerB.State);

            // 移除节点：确认框必须说明会从所有子节点的链路里消失
            var confirmMessage = "";
            var alerts = new List<string>();
            panel.Confirm = (message, _) => { confirmMessage = message; return true; };
            panel.Alert = (message, _) => alerts.Add(message);

            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(peerB);
            ((Button)panel.FindName("RemoveNodeButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Check("移除确认框说明会从所有子节点的链路里移除", confirmMessage.Contains("所有子节点"), confirmMessage);
            Check("移除后节点不在清单里", store.Nodes.All(n => n.NodeId != "peer-a"));
            Check("移除后进入移除名单（心跳也加不回来）", store.IsRemoved("peer-a"));

            rows = ((IEnumerable)grid.ItemsSource!).Cast<RegistryNodeRow>().ToList();
            Check("已移除的节点仍列在表里（否则无法恢复）", rows.Any(r => r.NodeId == "peer-a" && r.IsRemoved),
                string.Join(" / ", rows.Select(r => r.NodeId + (r.IsRemoved ? "(已移除)" : ""))));
            Check("已移除行的分发勾选框不可用",
                ((DataGrid)panel.FindName("NodeGrid")!).ItemsSource is IEnumerable list &&
                list.Cast<RegistryNodeRow>().Any(r => r.IsRemoved && !r.CanDistribute));
            Check("恢复按钮在存在移除名单时可用", ((Button)panel.FindName("RestoreNodeButton")!).IsEnabled);
            Check("移除名单文案列出被移除的节点",
                ((TextBlock)panel.FindName("RemovedNodesText")!).Text.Contains("peer-a"),
                ((TextBlock)panel.FindName("RemovedNodesText")!).Text);

            // 恢复
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(rows.First(r => r.IsRemoved));
            ((Button)panel.FindName("RestoreNodeButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("恢复后移除名单清空", !store.IsRemoved("peer-a"));
            Check("恢复后给出提示", alerts.Any(a => a.Contains("恢复")), string.Join(" / ", alerts));

            // 操作按钮的事件
            var saveRequested = 0;
            var registerRequested = 0;
            panel.SaveRequested += () => saveRequested++;
            panel.RegisterRequested += () => registerRequested++;

            ((Button)panel.FindName("SaveRegistryButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)panel.FindName("RegisterNowButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)panel.FindName("SyncNowButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Eq("“保存配置”交给主窗口统一落盘", 1, saveRequested);
            Eq("“立即注册/立即同步”触发注册请求", 2, registerRequested);

            // 未运行时点“立即注册/移除”要有可读提示，而不是静默失败
            var idlePanel = new RegistryPanel();
            var idleAlerts = new List<string>();
            idlePanel.Alert = (message, _) => idleAlerts.Add(message);
            idlePanel.ShowCenter(null, now, force: true);
            ((Button)idlePanel.FindName("RegisterNowButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)idlePanel.FindName("RemoveNodeButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("注册中心未运行时移除节点给出提示",
                idleAlerts.Any(a => a.Contains("未运行")), string.Join(" / ", idleAlerts));

            // 排版：注册页自身的文本/控件不能被横向裁剪（纵向由 ScrollViewer 承担）
            var panelProblems = new List<string>();
            AuditPanelWidth(panel, 1320, panelProblems);
            Check("注册页无横向裁剪", panelProblems.Count == 0, string.Join("；", panelProblems));
        }, "注册页断言");

        try { Directory.Delete(root, true); } catch { }
    }

    /// <summary>
    /// 对已在真实窗口里的子树做**只读**横向审查（不改动布局）：
    /// 窗口级的 Report 会跳过 TabControl 内部，因此注册页要单独看一次。
    /// </summary>
    private static void AuditNestedWidth(FrameworkElement root, List<string> problems)
    {
        foreach (var child in EnumerateStatic(root))
        {
            if (child is DataGrid or TabControl) continue;
            if (child is not FrameworkElement element || element.Visibility != Visibility.Visible) continue;
            if (element.ActualWidth <= 0) continue;

            if (child is TextBlock text && !string.IsNullOrWhiteSpace(text.Text) &&
                ContentWidth(text) > text.ActualWidth + 1)
            {
                problems.Add($"文本被裁剪：“{Shorten(text.Text)}” 需要 {ContentWidth(text):F0}px，只有 {text.ActualWidth:F0}px");
            }

            if (child is CheckBox or Button or TextBox or PasswordBox && ContentWidth(element) > element.ActualWidth + 1)
            {
                problems.Add($"{child.GetType().Name}“{Describe(child)}”宽度不足：" +
                             $"需要 {ContentWidth(element):F0}px，只有 {element.ActualWidth:F0}px");
            }
        }
    }

    /// <summary>
    /// 注册页的横向排版审查：只查宽度方向的裁剪。
    /// 它包在 ScrollViewer 里，纵向超出是设计允许的（可以滚动），所以不能复用带边界检查的那套。
    /// </summary>
    private static void AuditPanelWidth(FrameworkElement root, double width, List<string> problems)
    {
        root.Measure(new Size(width, 4000));
        root.Arrange(new Rect(0, 0, width, 4000));
        root.UpdateLayout();

        foreach (var child in EnumerateStatic(root))
        {
            if (child is DataGrid or TabControl) continue;
            if (child is not FrameworkElement element || element.Visibility != Visibility.Visible) continue;
            if (element.ActualWidth <= 0) continue;

            if (child is TextBlock text && !string.IsNullOrWhiteSpace(text.Text) &&
                ContentWidth(text) > text.ActualWidth + 1)
            {
                problems.Add($"文本被裁剪：“{Shorten(text.Text)}” 需要 {ContentWidth(text):F0}px，只有 {text.ActualWidth:F0}px");
            }

            if (child is CheckBox or Button or TextBox or PasswordBox && ContentWidth(element) > element.ActualWidth + 1)
            {
                problems.Add($"{child.GetType().Name}“{Describe(child)}”宽度不足：" +
                             $"需要 {ContentWidth(element):F0}px，只有 {element.ActualWidth:F0}px");
            }
        }
    }

    // ---------------- 主窗口联动 ----------------

    private static void TestRegistryMainWindowUi()
    {
        Section("注册页与链路页联动：快照应用 / 本地停测 / 只读与删除保护 / 落盘");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "registry_ui_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        ConfigManager.Save(new AppConfig
        {
            ListenPort = 9000,
            IntervalMs = 1000,
            TimeoutMs = 1000,
            Links =
            {
                new LinkConfig { Name = "本地手工链路", Host = "192.0.2.50", Port = 9100 }
            },
            Registrar = new RegistrarConfig
            {
                Enabled = true,
                ServeAsCenter = true,
                NodeId = "self-node",
                NodeName = "本机",
                PublicHost = "203.0.113.9",
                PublicPort = 9000,
                CenterHost = "127.0.0.1",
                CenterPort = 9000,
                Secret = "s3cret",
                HeartbeatSeconds = 60,
                SyncSeconds = 60
            }
        });

        RunOnSta(() =>
        {
            var window = new MainWindow();

            Check("窗口里存在「注册中心」标签页",
                window.FindName("MainTabs") is TabControl tabs &&
                tabs.Items.OfType<TabItem>().Any(t => Equals(t.Header, "注册中心")));

            var panel = window.FindName("RegistryTabPanel") as RegistryPanel;
            Check("标签页里挂的是注册页控件", panel is not null);

            var linksGrid = (DataGrid)window.FindName("LinksGrid")!;
            Check("链路配置页新增「来源」列", linksGrid.Columns.Any(c => Equals(c.Header, "来源")));
            Check("「校验」列绑定到备注（本地停测/自动维护）",
                linksGrid.Columns.Any(c => Equals(c.Header, "校验") &&
                    (c as DataGridTextColumn)?.Binding is Binding binding &&
                    binding.Path.Path == nameof(LinkRow.Diagnosis)));

            Check("注册页按配置回填（含本端外网地址）",
                ((Ipv4Box)panel!.FindName("PublicHostBox")!).Text == "203.0.113.9",
                ((Ipv4Box)panel.FindName("PublicHostBox")!).Text);
            Check("承担中心角色时中心管理区可见",
                ((Border)panel.FindName("CenterSection")!).Visibility == Visibility.Visible);

            var linkRows = (IList)GetPrivateField(window, "_linkRows")!;
            Eq("启动时只有手工链路", 1, linkRows.Count);
            Check("手工链路的来源显示为“本地”", ((LinkRow)linkRows[0]!).SourceText == "本地");

            // ---- 应用一次中心快照 ----
            var snapshot = new List<NodeSnapshot>
            {
                new() { NodeId = "self-node", Name = "本机", Ip = "203.0.113.9", Port = 9000, Tcp = true, Udp = true },
                new() { NodeId = "peer-a", Name = "机房B", Ip = "203.0.113.10", Port = 9000, Tcp = true, Udp = true },
                new() { NodeId = "peer-b", Name = "机房C", Ip = "203.0.113.11", Port = 9000, Tcp = false, Udp = true },
                new() { NodeId = "peer-loop", Name = "回环节点", Ip = "127.0.0.1", Port = 9000, Tcp = true, Udp = true }
            };

            ApplySnapshot(window, snapshot);

            Eq("快照应用后：手工链路 + 2 条注册链路", 3, linkRows.Count);
            Check("没把自己加成链路", linkRows.Cast<LinkRow>().All(r => r.NodeId != "self-node"));
            Check("跳过了回环地址（否则测的是自己的回环 = 假线路）",
                linkRows.Cast<LinkRow>().All(r => r.NodeId != "peer-loop"));
            Check("注册链路的来源显示为“注册中心”",
                linkRows.Cast<LinkRow>().Where(r => r.IsRegistry).All(r => r.SourceText == "注册中心"));
            Check("注册链路带上中心下发的目标",
                linkRows.Cast<LinkRow>().Any(r => r.NodeId == "peer-a" && r.Host == "203.0.113.10" && r.Port == 9000));
            Check("协议按对端宣告（peer-b 只宣告 UDP）",
                linkRows.Cast<LinkRow>().First(r => r.NodeId == "peer-b") is { Tcp: false, Udp: true });
            Check("正常注册链路的备注说明来自注册中心",
                linkRows.Cast<LinkRow>().First(r => r.NodeId == "peer-a").Diagnosis.Contains("注册中心"),
                linkRows.Cast<LinkRow>().First(r => r.NodeId == "peer-a").Diagnosis);

            // 新链路必须立刻进入探测集合（网格测试自动发现的关键：不必重启测试）。
            // 生效走"约 1 秒防抖"（避免边打字边发包），这里等到它成立为止。
            Check("新链路立刻进入探测集合（无需重启测试）",
                PumpUntil(() => ((LinkConfig[])GetPrivateField(window, "_liveLinks")!).Length == 3),
                "当前 " + ((LinkConfig[])GetPrivateField(window, "_liveLinks")!).Length + " 条");

            var liveLinks = (LinkConfig[])GetPrivateField(window, "_liveLinks")!;
            Check("探测集合里包含注册链路", liveLinks.Any(l => l.Source == LinkSources.Registry),
                string.Join(" / ", liveLinks.Select(l => $"{l.Name}:{l.Source}")));

            // 落盘：注册链路必须带上来源，否则下次启动会变成手工链路
            var saved = ConfigManager.Load(out var loadError);
            Eq("自动落盘成功", "", loadError ?? "");
            var savedRegistry = saved.Links.Where(l => l.Source == LinkSources.Registry).ToList();
            Eq("落盘后注册链路仍在", 2, savedRegistry.Count);
            Check("落盘保留了 NodeId 与 Source",
                savedRegistry.All(l => l.NodeId.Length > 0 && l.Source == LinkSources.Registry));
            Check("落盘保留了手工链路", saved.Links.Any(l => l.Source == LinkSources.Manual && l.Name == "本地手工链路"));

            // ---- 本地停测（注册链路） ----
            var peerA = linkRows.Cast<LinkRow>().First(r => r.NodeId == "peer-a");
            peerA.Tcp = false;
            peerA.Udp = false;
            Check("两条协议都取消勾选 = 本地停测意图", peerA.LocalDisabled);
            Eq("本地停测后“生效”为不发包", "不发包", peerA.Effective);
            Check("本地停测在备注列有说明", peerA.Diagnosis.Contains("本地已停测"), peerA.Diagnosis);

            // 关键：本地停测意图不能被下一次同步覆盖掉。
            // 这一轮同步确实会动到这一行（对端改名了），因此不是"没变化所以碰巧没被覆盖"。
            var renamed = snapshot
                .Select(n => n.NodeId == "peer-a"
                    ? new NodeSnapshot
                    {
                        NodeId = n.NodeId, Name = "机房B-改名", Ip = n.Ip, Port = n.Port, Tcp = n.Tcp, Udp = n.Udp
                    }
                    : n)
                .ToList();

            ApplySnapshot(window, renamed);
            peerA = linkRows.Cast<LinkRow>().First(r => r.NodeId == "peer-a");
            Eq("同步确实改动了这一行（对端改名）", "机房B-改名", peerA.Name);
            Check("同步不会覆盖本地停测意图", peerA.LocalDisabled, "本地停测被同步改回来了");
            Check("同步后该链路仍不发包", !peerA.Tcp && !peerA.Udp);
            var savedAfterSync = ConfigManager.Load(out _);
            Check("本地停测意图被落盘",
                savedAfterSync.Links.First(l => l.NodeId == "peer-a").LocalDisabled);

            peerA.Tcp = true;
            Check("重新勾选任一协议即恢复本地探测", !peerA.LocalDisabled);

            // ---- 注册链路的名称/主机/端口只读 ----
            var nameColumn = linksGrid.Columns.First(c => Equals(c.Header, "名称"));
            var hostColumn = linksGrid.Columns.First(c => Equals(c.Header, "主机 / IP"));
            var tcpColumn = linksGrid.Columns.First(c => Equals(c.Header, "TCP"));

            Check("注册链路的名称列禁止编辑",
                BeginningEditCancelled(window, peerA, nameColumn));
            Check("注册链路的主机列禁止编辑",
                BeginningEditCancelled(window, peerA, hostColumn));
            Check("注册链路的端口列禁止编辑",
                BeginningEditCancelled(window, peerA, linksGrid.Columns.First(c => Equals(c.Header, "端口"))));
            Check("注册链路的 TCP 勾选仍可改（这就是唯一的本地开关）",
                !BeginningEditCancelled(window, peerA, tcpColumn));

            var manualRow = linkRows.Cast<LinkRow>().First(l => !l.IsRegistry);
            Check("手工链路照常可编辑名称", !BeginningEditCancelled(window, manualRow, nameColumn));

            // ---- 删除注册链路要被拦下并给出正确出路 ----
            var alerts = new List<string>();
            SetPrivateField(window, "_alert", (Action<string, string>)((message, _) => alerts.Add(message)));
            SetPrivateField(window, "_confirmDelete", (Func<string, string, bool>)((_, _) =>
                throw new InvalidOperationException("注册链路不该走到删除确认")));

            linksGrid.SelectedItems.Clear();
            linksGrid.SelectedItems.Add(peerA);
            InvokePrivateHandler(window, "DeleteLink_Click");

            Check("删除注册链路被拦下", linkRows.Cast<LinkRow>().Any(r => r.NodeId == "peer-a"));
            Check("拦下时说明要去注册中心移除并提示本地停测的做法",
                alerts.Any(a => a.Contains("注册中心") && a.Contains("取消勾选")), string.Join(" / ", alerts));

            // ---- 中心侧移除节点 → 其他节点的链路消失 ----
            var afterRemoval = snapshot.Where(n => n.NodeId != "peer-b").ToList();
            ApplySnapshot(window, afterRemoval);
            Eq("中心移除节点后链路消失", 2, linkRows.Count);
            Check("消失的是被移除的那个节点",
                linkRows.Cast<LinkRow>().All(r => r.NodeId != "peer-b"));
            Check("手工链路不受任何同步影响",
                linkRows.Cast<LinkRow>().Any(r => !r.IsRegistry && r.Name == "本地手工链路"));

            window.Close();
        }, "主窗口注册联动断言");

        ConfigManager.OverridePath = previousOverride;
        try { Directory.Delete(root, true); } catch { }
    }

    private static void ApplySnapshot(MainWindow window, IReadOnlyList<NodeSnapshot> nodes)
    {
        var method = typeof(MainWindow).GetMethod("ApplyRegistrySnapshot",
                         BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new InvalidOperationException("未找到 ApplyRegistrySnapshot");

        try
        {
            method.Invoke(window, new object[] { nodes });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>真跑一次 BeginningEdit 处理器，返回它是否取消了编辑。</summary>
    private static bool BeginningEditCancelled(MainWindow window, LinkRow row, DataGridColumn column)
    {
        var grid = (DataGrid)window.FindName("LinksGrid")!;
        var container = new DataGridRow { Item = row };
        var args = new DataGridBeginningEditEventArgs(column, container, new RoutedEventArgs());

        InvokePrivateHandlerWith(window, "LinksGrid_BeginningEdit", grid, args);
        return args.Cancel;
    }

    // ---------------- 非中心节点：兼容矩阵最后一行 ----------------

    private static async Task TestRegistryNonCenterAsync()
    {
        Section("注册协议：向非中心节点发注册 → 明确回 unsupported（不重试风暴）");

        var port = FindBindablePort();
        var server = new ProbeServer(port);
        server.ProtocolHandler = (request, length, _, ct) =>
            RegistryCenterService.RespondUnsupportedAsync("preshared", request, length, ct);

        try
        {
            server.Start();
        }
        catch
        {
            Check("非中心节点启动", false, "端口不可绑定");
            server.Dispose();
            return;
        }

        using var client = new RegistryClient(new RegistryClientOptions
        {
            NodeId = "node-x",
            NodeName = "节点X",
            PublicHost = "203.0.113.30",
            PublicPort = 9000,
            CenterHost = "127.0.0.1",
            CenterPort = port,
            Secret = "preshared",
            HeartbeatSeconds = 15,
            SyncSeconds = 15,
            RetryMs = 200,
            MaxRetries = 1,
            LogDirectory = Path.Combine(AppContext.BaseDirectory, ".testtmp", "registry_logs")
        });

        Check("客户端启动", client.Start());

        var ok = await client.RegisterNowAsync();
        Check("把普通节点当中心会被明确拒绝", !ok);
        Check("状态里给出 unsupported 与可读原因",
            client.Status.LastError?.Contains("unsupported") == true &&
            client.Status.LastError.Contains("不是注册中心"), client.Status.LastError);
        Check("客户端仍保持运行（不影响本机探测）", client.Status.Running);

        // 终态错误不做重传风暴：连续两次请求不应叠加超时重试
        var started = DateTime.UtcNow;
        await client.RegisterNowAsync();
        var elapsed = DateTime.UtcNow - started;
        Check("终态错误不触发重传风暴（单次请求 < 1.5 秒）", elapsed < TimeSpan.FromSeconds(1.5),
            $"耗时 {elapsed.TotalMilliseconds:F0}ms");

        client.Stop();
        server.Dispose();
    }

    /// <summary>
    /// 仓库根目录的 `appsettings.json` 是**发布模板**（会被复制到输出/发布目录）。
    /// 它带就地注释，必须能被程序自己读进来——模板坏掉等于现场开局就报配置错误。
    /// </summary>
    private static void TestRepoTemplateLoads()
    {
        Section("发布模板：仓库根目录的 appsettings.json 可被读取");

        var template = FindRepoFile("appsettings.json");
        if (template is null)
        {
            Console.WriteLine("  [INFO] 未找到仓库根目录（跳过模板检查）");
            return;
        }

        var previous = ConfigManager.OverridePath;
        try
        {
            ConfigManager.OverridePath = template;

            var loaded = ConfigManager.Load(out var error);
            Eq("模板可被读取（注释与尾逗号不报错）", "", error ?? "");
            Eq("模板里注册默认不启用（升级后行为不变）", false, loaded.Registrar.Enabled);
            Eq("模板里的监听端口与代码默认值一致", new AppConfig().ListenPort, loaded.ListenPort);
            Eq("模板里链路默认是本地手工链路", LinkSources.Manual, loaded.Links[0].Source);
            Eq("模板里示例链路的目标端口", 9001, loaded.Links[0].Port);
            Check("模板里的注册字段被认出来（不是被当成未知键丢掉）",
                loaded.Registrar.HeartbeatSeconds == 60 && loaded.Registrar.SyncSeconds == 300 &&
                loaded.Registrar.MaxRetries == 5,
                $"心跳={loaded.Registrar.HeartbeatSeconds} 同步={loaded.Registrar.SyncSeconds} 重传={loaded.Registrar.MaxRetries}");
        }
        finally
        {
            ConfigManager.OverridePath = previous;
        }
    }

    /// <summary>从测试程序集位置向上找仓库根目录下的文件（用 LineTester.sln 作为根标记）。</summary>
    private static string? FindRepoFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var i = 0; i < 8 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LineTester.sln")))
            {
                var candidate = Path.Combine(directory.FullName, fileName);
                return File.Exists(candidate) ? candidate : null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    // ---------------- 保存与重启（现场反馈：改了保存，重启又回来） ----------------

    /// <summary>
    /// 现场反馈：在链路配置里取消勾选 TCP（或删掉一条链路）→ 点保存配置 → 重启客户端，
    /// TCP 又是勾上的、被删的链路又回来了。
    ///
    /// 这条链路必须整体验证一遍：**保存真的写进了文件**，且**重启读回来与文件一致**。
    /// 任何一环出问题，现场看到的就是"改了没用"。
    /// </summary>
    private static void TestSaveAndRestartRoundTrip()
    {
        Section("保存配置与重启：取消勾选 / 删除链路 / 改端口 都必须落盘并在重启后保持");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "save_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        ConfigManager.Save(new AppConfig
        {
            ListenPort = FindBindablePort(),
            Links =
            {
                new LinkConfig { Name = "文一西路", Host = "115.231.111.101", Port = 16298, Tcp = true, Udp = true },
                new LinkConfig { Name = "要删掉的", Host = "203.0.113.77", Port = 9000, Tcp = true, Udp = true }
            }
        });

        RunOnSta(() =>
        {
            // ---- 第一次启动：改界面并保存 ----
            var window = new MainWindow();
            var rows = (System.Collections.IList)GetPrivateField(window, "_linkRows")!;
            Eq("启动时读入 2 条链路", 2, rows.Count);

            var keep = rows.Cast<LinkRow>().First(r => r.Name == "文一西路");
            keep.Tcp = false;                      // 取消勾选 TCP
            keep.PortText = "16299";               // 顺手改个端口

            // 删掉第二条
            var deleteThis = rows.Cast<LinkRow>().First(r => r.Name == "要删掉的");
            var grid = (DataGrid)window.FindName("LinksGrid")!;
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(deleteThis);
            SetPrivateField(window, "_confirmDelete", (Func<string, string, bool>)((_, _) => true));
            SetPrivateField(window, "_alert", (Action<string, string>)((_, _) => { }));
            InvokePrivateHandler(window, "DeleteLink_Click");
            Eq("界面上删掉后剩 1 条", 1, rows.Count);

            InvokePrivateHandler(window, "SaveConfig_Click");
            window.Close();

            // ---- 文件里必须已经写对 ----
            var saved = ConfigManager.Load(out var loadError);
            Eq("保存后文件可读", "", loadError ?? "");
            Eq("文件里只剩 1 条链路（删除已落盘）", 1, saved.Links.Count);
            Eq("文件里的链路名", "文一西路", saved.Links[0].Name);
            Check("文件里 TCP 已取消勾选（没有又变回 true）", !saved.Links[0].Tcp, saved.Links[0].Tcp.ToString());
            Check("文件里 UDP 保持勾选", saved.Links[0].Udp);
            Eq("文件里端口已更新", 16299, saved.Links[0].Port);

            // ---- 重启：界面必须与文件一致 ----
            var restarted = new MainWindow();
            var restartRows = (System.Collections.IList)GetPrivateField(restarted, "_linkRows")!;
            Eq("重启后仍是 1 条链路（被删的没有恢复）", 1, restartRows.Count);
            var restored = (LinkRow)restartRows[0]!;
            Eq("重启后链路名", "文一西路", restored.Name);
            Check("重启后 TCP 仍是未勾选", !restored.Tcp, restored.Tcp.ToString());
            Check("重启后 UDP 仍是勾选", restored.Udp);
            Eq("重启后端口", "16299", restored.PortText);
            Eq("重启后「生效」列只显示 UDP", "UDP", restored.Effective);
            Check("重启后链路仍然合法", restored.IsValid, restored.Problem);

            restarted.Close();
        }, "保存与重启断言");

        // ---- 配置路径可见性 / 外部修改提示 / 重新加载 ----
        RunOnSta(() =>
        {
            var window = new MainWindow();
            var note = (TextBlock)window.FindName("StatusNote")!;

            // 窗口没 Show() 时 Loaded 不会触发 → 直接调用它做的事（ShowConfigLocation）
            typeof(MainWindow).GetMethod("ShowConfigLocation", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, null);
            Pump(200);
            Check("启动后状态栏写明正在使用哪份配置",
                note.Text.Contains("配置文件：") && note.Text.Contains("appsettings.json"), note.Text);
            Check("状态栏里的配置路径就是程序目录那份",
                note.Text.Contains(ConfigManager.ConfigPath), note.Text);
            Check("存在「重新加载配置」按钮", window.FindName("ReloadConfigButton") is Button);

            // 外部改动 → 状态栏提示（模拟另一个实例/记事本改了它）
            var external = ConfigManager.Load(out _);
            external.Links.Add(new LinkConfig { Name = "别人加的", Host = "203.0.113.200", Port = 9000 });
            ConfigManager.Save(external);

            Pump(1200); // 等一个 UI tick
            Check("检测到配置被外部修改后给出提示",
                note.Text.Contains("已被其它程序或另一个实例修改"), note.Text);

            // 点「重新加载配置」→ 界面读回外部内容
            InvokePrivateHandler(window, "ReloadConfig_Click");
            Pump(300);

            var rows = (System.Collections.IList)GetPrivateField(window, "_linkRows")!;
            Check("重新加载后界面拿到了外部新增的链路",
                rows.Cast<LinkRow>().Any(r => r.Name == "别人加的"),
                string.Join(",", rows.Cast<LinkRow>().Select(r => r.Name)));
            Check("重新加载后状态栏提示已加载",
                note.Text.Contains("已重新加载配置"), note.Text);

            window.Close();
        }, "配置路径与外部修改断言");

        ConfigManager.OverridePath = previousOverride;
        try { Directory.Delete(root, true); } catch { }
    }

    // ---------------- 注册链路：本地协议选择必须持久（现场反馈） ----------------

    /// <summary>
    /// 现场反馈："在链路配置里把 TCP 取消勾选、保存、重启 → TCP 又勾上了，删掉的链路也回来了"。
    ///
    /// 前两个症状来自**注册链路被中心/registry.json 覆盖**：
    /// ① 本地单独取消一个协议过去只是"改了这一行"，没有记成本地意图 → 同步按中心宣告值改回来；
    /// ② 已删除的注册链路会在下一次同步被重新生成（中心仍然在分发那个节点）。
    /// 这里把①的行为钉死；②的出路是中心侧移除/清理长期离线（见下）。
    /// </summary>
    private static void TestRegistryLocalProtocolIntent()
    {
        Section("注册链路：本地取消勾选一个协议必须持久（不被同步改回）");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "intent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new RegistryStore(Path.Combine(root, "registry.json"), 60);

        store.Upsert(new RegisterMessage
        {
            NodeId = "peer",
            Name = "东莞机房",
            Ip = "120.233.128.228",
            Port = 16298,
            Tcp = true,
            Udp = true
        }, true);

        var snapshot = store.BuildSnapshot();

        // 第一次同步：两条链路都按中心宣告（TCP + UDP）
        var first = RegistrySync.Apply(snapshot, "self", new List<LinkConfig>());
        var link = first.Links.First(l => l.NodeId == "peer");
        Check("首次同步按中心宣告：TCP 开着", link.Tcp);
        Check("首次同步按中心宣告：UDP 开着", link.Udp);
        Check("首次同步没有本地意图", !link.LocalTcpOff && !link.LocalUdpOff && !link.LocalDisabled);

        // 运维在界面上取消勾选 TCP（保留 UDP）→ 记成本地意图
        link.Tcp = false;
        link.LocalTcpOff = true;

        var second = RegistrySync.Apply(snapshot, "self", first.Links);
        var afterEdit = second.Links.First(l => l.NodeId == "peer");
        Check("同步后 TCP 仍是本地关掉的（没有被改回勾选）", !afterEdit.Tcp, afterEdit.Tcp.ToString());
        Check("UDP 照常按中心宣告", afterEdit.Udp);
        Check("本地意图被保留", afterEdit.LocalTcpOff);
        Check("只关一个协议不算整体停测", !afterEdit.LocalDisabled);

        // 再同步十次也不会被改回
        var links = second.Links;
        for (var i = 0; i < 10; i++)
        {
            links = RegistrySync.Apply(snapshot, "self", links).Links;
            links.First(l => l.NodeId == "peer").LocalTcpOff = true;
        }

        Check("反复同步十次 TCP 仍保持本地关闭", !links.First(l => l.NodeId == "peer").Tcp);

        // 重新勾选 → 恢复按中心值
        var restore = links.First(l => l.NodeId == "peer");
        restore.Tcp = true;
        restore.LocalTcpOff = false;

        var third = RegistrySync.Apply(snapshot, "self", links);
        Check("重新勾选后恢复中心宣告的 TCP", third.Links.First(l => l.NodeId == "peer").Tcp);

        // 老配置迁移：只有 LocalDisabled=true 的链路 = 两个协议都本地关掉
        var legacy = first.Links.Select(l => new LinkConfig
        {
            Name = l.Name,
            Host = l.Host,
            Port = l.Port,
            Tcp = false,
            Udp = false,
            Source = LinkSources.Registry,
            NodeId = l.NodeId,
            LocalDisabled = true
        }).ToList();

        var migrated = RegistrySync.Apply(snapshot, "self", legacy);
        var migratedLink = migrated.Links.First(l => l.NodeId == "peer");
        Check("老配置（LocalDisabled）迁移后仍不发包", !migratedLink.Tcp && !migratedLink.Udp);
        Check("迁移后本地意图写进两个协议", migratedLink.LocalTcpOff && migratedLink.LocalUdpOff);
        Check("迁移后仍标记为整体停测", migratedLink.LocalDisabled);

        // 中心表：长期离线的节点可被识别并清理（"删掉的链路又回来"的根源）
        var fakeNow = DateTime.UtcNow.AddDays(-10);
        var offline = new RegistryStore(Path.Combine(root, "offline.json"), 60, () => fakeNow);
        offline.Upsert(new RegisterMessage
        {
            NodeId = "gone", Name = "已退役机器", Ip = "203.0.113.50", Port = 9000, Tcp = true, Udp = true
        }, true);

        fakeNow = DateTime.UtcNow;      // 时间前进 10 天
        offline.RefreshOnlineState();

        var now = DateTime.UtcNow;
        var goneRow = RegistryNodeRow.FromNode(offline.Nodes.First(n => n.NodeId == "gone"), now, (_, _) => true);
        Check("心跳停了 10 天的节点被标记为长期离线", goneRow.LongOffline, goneRow.LastSeen);
        Check("中心也把它标成离线", !offline.Nodes.First(n => n.NodeId == "gone").Online);
        Eq("节点 ID 短号显示前 8 位", "gone", goneRow.ShortId);
        Check("长期离线节点仍在清单里（中心只标记不删除）", offline.Nodes.Any(n => n.NodeId == "gone"));

        // 刚离线一会儿的不算长期：避免把临时断网当成退役
        var fresh = new RegistryStore(Path.Combine(root, "fresh.json"), 60, () => DateTime.UtcNow.AddHours(-3));
        fresh.Upsert(new RegisterMessage
        {
            NodeId = "sleepy", Name = "临时断网", Ip = "203.0.113.51", Port = 9000, Tcp = true, Udp = true
        }, true);

        var sleepyRow = RegistryNodeRow.FromNode(fresh.Nodes.First(), DateTime.UtcNow, (_, _) => true);
        Check("只离线几小时的节点不算长期离线", !sleepyRow.LongOffline, sleepyRow.LastSeen);

        Check("中心侧移除后不再分发（链路随之消失）", offline.RemoveNode("gone"));
        Eq("移除后快照为空", 0, offline.BuildSnapshot().Count);
        Check("被移除的节点进入移除名单（心跳也加不回来）", offline.IsRemoved("gone"));

        try { Directory.Delete(root, true); } catch { }
    }

    // ---------------- 多 IP：界面按快照重建时不能把多条压成一条（现场 bug） ----------------

    /// <summary>
    /// 现场 bug：appsettings.json 里一个机房有 4 个 IP（4 条链路），界面却只显示 1 条
    /// （名字是最后处理的那个地址，如「厦门机房#4」）。
    ///
    /// 根因：界面按快照重建时**只按 NodeId 匹配行**，没带地址 →
    /// 同一机房的 4 条链路反复覆盖同一行，最后只剩最后一条。
    /// 这条用例把它钉死：N 个地址必须出现 N 行、名字与主机一一对应。
    /// </summary>
    private static void TestRegistryRowsKeepAllAddresses()
    {
        Section("注册同步：一个机房多个地址在界面上必须是多条链路（不能被压成一条）");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "multirow_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        // 起始：只有一条手工链路
        ConfigManager.Save(new AppConfig
        {
            ListenPort = FindBindablePort(),
            Links = { new LinkConfig { Name = "手工链路", Host = "203.0.113.99", Port = 9000 } }
        });

        try
        {
            RunOnSta(() =>
            {
                var window = new MainWindow();
                var rows = (System.Collections.IList)GetPrivateField(window, "_linkRows")!;

                // 中心快照：厦门机房 4 个地址（同一个 NodeId）
                var fourAddresses = new List<NodeSnapshot>
                {
                    new()
                    {
                        NodeId = "xiamen-node",
                        Name = "厦门机房",
                        Ip = "110.86.28.214",
                        Port = 17078,
                        Tcp = false,
                        Udp = true,
                        Ips = new List<string>
                        {
                            "110.86.28.214", "183.250.153.182", "183.250.153.183", "183.250.153.184"
                        }
                    }
                };

                ApplySnapshot(window, fourAddresses);
                Pump(1500);

                var xiamenRows = rows.Cast<LinkRow>().Where(r => r.NodeId == "xiamen-node").ToList();
                Eq("4 个地址 → 界面 4 条链路（不是 1 条）", 4, xiamenRows.Count);
                Check("四条链路的地址各不相同",
                    xiamenRows.Select(r => r.Host).Distinct(StringComparer.Ordinal).Count() == 4,
                    string.Join(",", xiamenRows.Select(r => $"{r.Name}@{r.Host}")));
                Check("名字是 厦门机房 / #2 / #3 / #4",
                    string.Join(",", xiamenRows.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal))
                        == "厦门机房,厦门机房#2,厦门机房#3,厦门机房#4",
                    string.Join(",", xiamenRows.Select(r => r.Name)));
                Check("4 个地址都在，且与名字一一对应",
                    xiamenRows.Any(r => r.Name == "厦门机房" && r.Host == "110.86.28.214") &&
                    xiamenRows.Any(r => r.Name == "厦门机房#2" && r.Host == "183.250.153.182") &&
                    xiamenRows.Any(r => r.Name == "厦门机房#3" && r.Host == "183.250.153.183") &&
                    xiamenRows.Any(r => r.Name == "厦门机房#4" && r.Host == "183.250.153.184"),
                    string.Join("；", xiamenRows.Select(r => $"{r.Name}@{r.Host}")));
                Check("手工链路没被动过",
                    rows.Cast<LinkRow>().Any(r => r.Name == "手工链路" && !r.IsRegistry));

                // 再同步一次（幂等）：还是 4 条，不能重复添加
                ApplySnapshot(window, fourAddresses);
                Pump(1500);
                Eq("重复同步不会重复添加", 4, rows.Cast<LinkRow>().Count(r => r.NodeId == "xiamen-node"));

                // 对端撤掉两个地址 → 界面只剩 2 条，且删掉的是对应那两条
                var twoAddresses = new List<NodeSnapshot>
                {
                    new()
                    {
                        NodeId = "xiamen-node",
                        Name = "厦门机房",
                        Ip = "110.86.28.214",
                        Port = 17078,
                        Tcp = false,
                        Udp = true,
                        Ips = new List<string> { "110.86.28.214", "183.250.153.184" }
                    }
                };

                ApplySnapshot(window, twoAddresses);
                Pump(1500);

                var remaining = rows.Cast<LinkRow>().Where(r => r.NodeId == "xiamen-node").ToList();
                Eq("撤掉两个地址后剩 2 条", 2, remaining.Count);
                Check("剩下的是主地址与第 4 个地址",
                    remaining.Any(r => r.Host == "110.86.28.214") && remaining.Any(r => r.Host == "183.250.153.184"),
                    string.Join(",", remaining.Select(r => r.Host)));
                Check("被撤掉的地址不再出现",
                    remaining.All(r => r.Host != "183.250.153.182" && r.Host != "183.250.153.183"));

                // 文件的链路数必须与界面一致（现场"文件里有、界面里没有"的根因就是这里不一致）
                var saved = ConfigManager.Load(out _);
                var diff = LinkConfigDiff.Compare(saved.Links, rows.Cast<LinkRow>().Select(r => r.ToConfig()));
                Check("落盘后的配置与界面行数一致",
                    diff.FileCount == diff.RowCount && diff.Same, LinkConfigDiff.Describe(diff, ConfigManager.ConfigPath, null));

                window.Close();
            }, "多地址行断言");
        }
        finally
        {
            ConfigManager.OverridePath = previousOverride;
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ---------------- 现场排查：日志 + 中心表格多地址显示 ----------------

    /// <summary>
    /// 现场反馈两件事：
    /// ① 东莞机房注册了 3 个 IP，但中心界面只显示 1 个 → 中心表只显示主地址（漏显示）；
    /// ② 需要把注册/同步/链路配置的日志写到文件，便于两端对照排查。
    /// </summary>
    private static async Task TestRegistryDiagnosticsAsync()
    {
        Section("现场排查：多地址在中心表显示 + 注册/同步/配置日志落盘");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "diag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // ---- ① 中心表：3 个地址都要能被看到 ----
        var store = new RegistryStore(Path.Combine(root, "registry.json"), 60);
        store.Upsert(new RegisterMessage
        {
            NodeId = "dg-node-1234",
            Name = "东莞机房",
            Ip = "120.233.128.228",
            Port = 16298,
            Tcp = true,
            Udp = true,
            Ips = new List<string> { "120.233.128.228", "157.255.153.208", "183.60.150.36" }
        }, true);

        var node = store.Nodes.First(n => n.NodeId == "dg-node-1234");
        var row = RegistryNodeRow.FromNode(node, DateTime.UtcNow, (_, _) => true);

        Check("中心表地址列注明还有更多地址（不再只显示主地址）",
            row.Address.Contains("120.233.128.228:16298") && row.Address.Contains("等 3 个"), row.Address);
        Check("提示框里列出全部 3 个地址",
            row.AllAddresses.Contains("120.233.128.228:16298") &&
            row.AllAddresses.Contains("157.255.153.208:16298") &&
            row.AllAddresses.Contains("183.60.150.36:16298"),
            row.AllAddresses);

        var singleRow = RegistryNodeRow.FromNode(
            new RegistryNode { NodeId = "one", Name = "单地址", Ip = "203.0.113.9", Port = 9000, Ips = new List<string> { "203.0.113.9" } },
            DateTime.UtcNow, (_, _) => true);
        Check("单地址节点不显示“等 N 个”", !singleRow.Address.Contains("等"), singleRow.Address);
        Eq("单地址节点提示框内容与地址列一致", "203.0.113.9:9000", singleRow.AllAddresses);

        // ---- ② 日志：中心侧把每个 REG 的地址与结论写下来 ----
        var logsDir = Path.Combine(root, "logs");
        var centerLog = new AppLog(logsDir, "registry-center.log", () => new DateTime(2026, 9, 21, 15, 0, 0));
        var centerStore = new RegistryStore(Path.Combine(root, "center-registry.json"), 60);
        var center = new RegistryCenterService(centerStore, "secret-should-never-appear",
            60, () => DateTime.UtcNow, centerLog);

        var registerFrame = RegistryProtocol.EncodeSigned("secret-should-never-appear", RegistryProtocol.TypeRegister,
            RegistryProtocol.Serialize(new RegisterMessage
            {
                ReqId = "req-log-1",
                NodeId = "dg-node-1234",
                Name = "东莞机房",
                Ip = "120.233.128.228",
                Port = 16298,
                Tcp = true,
                Udp = true,
                Ips = new List<string> { "120.233.128.228", "157.255.153.208", "183.60.150.36" },
                Ts = RegistryAuth.NowUnixSeconds(),
                Nonce = RegistryAuth.NewNonce()
            }));

        await center.HandleAsync(registerFrame, registerFrame.Length, new IPEndPoint(IPAddress.Loopback, 5000), CancellationToken.None);

        var centerLogText = File.ReadAllText(centerLog.CurrentPath);
        Check("中心日志记录了 REG 成功", centerLogText.Contains("REG 成功"), centerLogText.Split('\n').LastOrDefault());
        Check("中心日志记录了全部 3 个地址",
            centerLogText.Contains("120.233.128.228") && centerLogText.Contains("157.255.153.208") &&
            centerLogText.Contains("183.60.150.36"), centerLogText);
        Check("中心日志记录了节点名与端口", centerLogText.Contains("东莞机房") && centerLogText.Contains("120.233.128.228:16298"));
        Check("中心日志记录了 rev 变化与清单规模",
            centerLogText.Contains("rev 0→1") && centerLogText.Contains("清单 1 个节点"), centerLogText);
        Check("中心日志**不含密钥**", !centerLogText.Contains("secret-should-never-appear"));

        // 被拒的注册也要记（现场最常见的"注册不上"）
        var badFrame = RegistryProtocol.EncodeSigned("secret-should-never-appear", RegistryProtocol.TypeRegister,
            RegistryProtocol.Serialize(new RegisterMessage
            {
                ReqId = "req-log-2", NodeId = "bad-node", Name = "坏地址", Ip = "127.0.0.1", Port = 9000,
                Ts = RegistryAuth.NowUnixSeconds(), Nonce = RegistryAuth.NewNonce()
            }));

        await center.HandleAsync(badFrame, badFrame.Length, new IPEndPoint(IPAddress.Loopback, 5001), CancellationToken.None);
        var afterReject = File.ReadAllText(centerLog.CurrentPath);
        Check("被拒的注册也写进日志（含拒绝原因）",
            afterReject.Contains("REG 被拒") && afterReject.Contains("loopback"), afterReject);

        // 客户端日志（非中心侧）
        var clientLogRoot = Path.Combine(root, "client-logs");
        var clientPort = FindBindablePort();
        var clientStore = new RegistryStore(Path.Combine(root, "client-registry.json"), 60);
        var clientCenter = new RegistryCenterService(clientStore, "s", 60, () => DateTime.UtcNow, centerLog);
        using var server = new ProbeServer(clientPort);
        server.ProtocolHandler = clientCenter.HandleAsync;
        server.Start();

        using var client = new RegistryClient(new RegistryClientOptions
        {
            NodeId = "client-node",
            NodeName = "非中心节点",
            PublicHost = "203.0.113.9",
            PublicPort = 16298,
            Ips = new List<string> { "203.0.113.9", "203.0.113.10" },
            CenterHost = "127.0.0.1",
            CenterPort = clientPort,
            Secret = "s",
            RetryMs = 200,
            MaxRetries = 1,
            LogDirectory = clientLogRoot
        });

        Check("客户端启动", client.Start());
        Check("客户端注册成功", await client.RegisterNowAsync(), client.Status.LastError);

        var clientLogPath = Path.Combine(clientLogRoot, CsvLogWriter.FolderNameFor(DateTime.Now), "registry-client.log");
        Check("非中心侧日志已生成", File.Exists(clientLogPath), clientLogPath);
        var clientLogText = File.ReadAllText(clientLogPath);
        Check("客户端日志记录了启动与本端地址",
            clientLogText.Contains("启动同步") && clientLogText.Contains("203.0.113.9:16298"), clientLogText);
        Check("客户端日志记录了收到的清单快照", clientLogText.Contains("收到清单快照"), clientLogText);

        client.Stop();
        server.Dispose();

        // ---- ③ 链路配置日志（改了什么、删了什么都要留痕） ----
        RunOnSta(() =>
        {
            var configRoot = Path.Combine(root, "cfg");
            Directory.CreateDirectory(configRoot);

            var previousOverride = ConfigManager.OverridePath;
            ConfigManager.OverridePath = Path.Combine(configRoot, "appsettings.json");

            try
            {
                ConfigManager.Save(new AppConfig
                {
                    Links =
                    {
                        new LinkConfig { Name = "文一西路", Host = "115.231.111.101", Port = 16298 },
                        new LinkConfig { Name = "东莞机房", Host = "120.233.128.228", Port = 16298 }
                    }
                });

                var window = new MainWindow();
                var rows = (System.Collections.IList)GetPrivateField(window, "_linkRows")!;

                var grid = (DataGrid)window.FindName("LinksGrid")!;
                grid.SelectedItems.Clear();
                grid.SelectedItems.Add(rows.Cast<LinkRow>().First(r => r.Name == "东莞机房"));
                SetPrivateField(window, "_confirmDelete", (Func<string, string, bool>)((_, _) => true));
                SetPrivateField(window, "_alert", (Action<string, string>)((_, _) => { }));
                InvokePrivateHandler(window, "DeleteLink_Click");

                var keep = rows.Cast<LinkRow>().First(r => r.Name == "文一西路");
                keep.Tcp = false;
                InvokePrivateHandler(window, "SaveConfig_Click");
                window.Close();

                var configLog = (AppLog)GetPrivateField(new MainWindow(), "_configLog")!;
                var configLogPath = Path.Combine(AppContext.BaseDirectory, "logs", CsvLogWriter.FolderNameFor(DateTime.Now), "config.log");
                Check("链路配置日志文件已生成", File.Exists(configLogPath), configLogPath);

                var text = File.ReadAllText(configLogPath);
                Check("配置日志记录了加载配置与链路明细",
                    text.Contains("加载配置") && text.Contains("链路「文一西路」115.231.111.101:16298"), text);
                Check("配置日志记录了删除动作", text.Contains("删除链路 1 条") && text.Contains("东莞机房"), text);
                Check("配置日志记录了保存后的链路状态（TCP 与本地意图）",
                    text.Contains("tcp=False"), text);
            }
            finally
            {
                ConfigManager.OverridePath = previousOverride;
            }
        }, "链路配置日志断言");

        try { Directory.Delete(root, true); } catch { }
    }

    // ---------------- 一机房多 IP ----------------

    /// <summary>
    /// 现场需求：同一个机房可能有多个外网 IP、端口相同（例如东莞机房 120.233.128.228 / 157.255.153.208 /
    /// 183.60.150.36）。注册时带上全部地址，其他节点为**每个地址各生成一条链路**，
    /// 这样三个地址的运营商与线路质量能分别看。
    /// </summary>
    private static void TestRegistryMultiAddress()
    {
        Section("链路注册：一个节点多个外网 IP（每个地址各一条链路）");

        // ---- 协议层：Ips 字段与有效地址列表 ----
        var message = new RegisterMessage
        {
            NodeId = "dg",
            Name = "东莞机房",
            Ip = "120.233.128.228",
            Port = 16298,
            Tcp = true,
            Udp = true,
            Ips = new List<string> { "120.233.128.228", "157.255.153.208", "183.60.150.36" }
        };
        Eq("有效地址 3 个", 3, message.AllIps.Count);
        Eq("主地址排第一", "120.233.128.228", message.AllIps[0]);

        var legacyMessage = new RegisterMessage { Ip = "203.0.113.9", Port = 9000 };
        Eq("老报文（只有 Ip）有效地址 1 个", 1, legacyMessage.AllIps.Count);
        Eq("老报文地址", "203.0.113.9", legacyMessage.AllIps[0]);

        var duplicated = new RegisterMessage
        {
            Ip = "1.1.1.1",
            Ips = new List<string> { "2.2.2.2", "1.1.1.1", "2.2.2.2" }
        };
        Eq("重复地址去重且主地址在前", "1.1.1.1,2.2.2.2", string.Join(",", duplicated.AllIps));

        // ---- 中心：存下全部地址，快照里带出去 ----
        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "multi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new RegistryStore(Path.Combine(root, "registry.json"), 60);

        Check("三个地址都能注册", store.Upsert(message, authenticated: true).Accepted);
        var node = store.Nodes.First(n => n.NodeId == "dg");
        Eq("中心存下 3 个地址", 3, node.Ips.Count);
        Eq("中心里的主地址", "120.233.128.228", node.Ip);

        var snapshot = store.BuildSnapshot().First(n => n.NodeId == "dg");
        Eq("快照带出 3 个地址", 3, snapshot.AllIps.Count);
        Eq("快照的主地址字段仍是第一个（老客户端只读它）", "120.233.128.228", snapshot.Ip);

        // 单个地址的节点：快照不下发 Ips（保持与老客户端一致的报文）
        store.Upsert(new RegisterMessage { NodeId = "one", Name = "单地址机房", Ip = "203.0.113.9", Port = 9000, Tcp = true, Udp = true }, true);
        var single = store.BuildSnapshot().First(n => n.NodeId == "one");
        Check("单地址节点的快照不带 Ips 字段（兼容老客户端）", single.Ips is null);
        Eq("单地址节点有效地址仍是 1 个", 1, single.AllIps.Count);

        // 地址不合法（回环）→ 整条注册被拒，且错误里点出是哪个地址
        var bad = store.Upsert(new RegisterMessage
        {
            NodeId = "bad",
            Name = "坏地址",
            Ip = "203.0.113.9",
            Port = 9000,
            Ips = new List<string> { "203.0.113.9", "127.0.0.1" }
        }, true);
        Check("多地址里有一个不可分发 → 拒绝注册", !bad.Accepted);
        Check("拒绝原因指出是哪个地址", (bad.ErrorMessage ?? "").Contains("127.0.0.1"), bad.ErrorMessage);

        // ---- 对端：为每个地址各生成一条链路 ----
        var outcome = RegistrySync.Apply(store.BuildSnapshot(), "self", new List<LinkConfig>());
        var dgLinks = outcome.Links.Where(l => l.NodeId == "dg").OrderBy(l => l.Name, StringComparer.Ordinal).ToList();
        Eq("东莞机房生成 3 条链路", 3, dgLinks.Count);
        Check("三条链路的目标是三个不同地址",
            dgLinks.Select(l => l.Host).OrderBy(h => h, StringComparer.Ordinal)
                .SequenceEqual(new[] { "120.233.128.228", "157.255.153.208", "183.60.150.36" }),
            string.Join(",", dgLinks.Select(l => l.Host)));
        Check("三条链路端口相同", dgLinks.All(l => l.Port == 16298));
        Check("主地址用节点名，其余带 #2/#3",
            dgLinks.Any(l => l.Name == "东莞机房") &&
            dgLinks.Any(l => l.Name == "东莞机房#2") &&
            dgLinks.Any(l => l.Name == "东莞机房#3"),
            string.Join(",", dgLinks.Select(l => l.Name)));
        Check("三条链路都是注册来源且 NodeId 相同",
            dgLinks.All(l => l.Source == LinkSources.Registry && l.NodeId == "dg"));

        // 幂等：同样快照再来一次，不新增不删除
        var again = RegistrySync.Apply(store.BuildSnapshot(), "self", outcome.Links);
        Eq("重复同步不新增链路", 0, again.Added);
        Eq("重复同步不删除链路", 0, again.Removed);

        // 对方撤掉一个地址 → 只删那一条
        store.Upsert(new RegisterMessage
        {
            NodeId = "dg",
            Name = "东莞机房",
            Ip = "120.233.128.228",
            Port = 16298,
            Tcp = true,
            Udp = true,
            Ips = new List<string> { "120.233.128.228", "183.60.150.36" }
        }, true);

        var afterRemoval = RegistrySync.Apply(store.BuildSnapshot(), "self", outcome.Links);
        Eq("撤掉一个地址后删掉 1 条链路", 1, afterRemoval.Removed);
        var remaining = afterRemoval.Links.Where(l => l.NodeId == "dg").ToList();
        Eq("剩下 2 条", 2, remaining.Count);
        Check("被撤掉的地址不再出现", remaining.All(l => l.Host != "157.255.153.208"));

        // 某个地址本地停测：只影响那一条
        var links = afterRemoval.Links;
        var target = links.First(l => l.NodeId == "dg" && l.Host == "183.60.150.36");
        target.LocalDisabled = true;
        target.Tcp = false;
        target.Udp = false;

        var afterLocalStop = RegistrySync.Apply(store.BuildSnapshot(), "self", links);
        var stopped = afterLocalStop.Links.First(l => l.NodeId == "dg" && l.Host == "183.60.150.36");
        Check("本地停测的那条保持不发包", stopped.LocalDisabled && !stopped.Tcp && !stopped.Udp);
        Check("同机房另外一条不受影响",
            afterLocalStop.Links.Any(l => l.NodeId == "dg" && l.Host == "120.233.128.228" && l.Tcp));

        try { Directory.Delete(root, true); } catch { }
    }

    // ---------------- 运行中改链路（现场反馈） ----------------

    /// <summary>
    /// 现场反馈的两件事，用"能数包的纯回显服务端"实测：
    /// ① 运行中把某条链路的 TCP 取消勾选后，**必须真的停止发 TCP**（原来界面写着"不发包"，包却照发）；
    /// ② 明细里的协议标注必须与对端**实际收到的包**一一对应（"发 TCP 却显示 UDP"）。
    /// </summary>
    private static void TestLinkToggleWhileRunning()
    {
        Section("运行中改链路：取消勾选 TCP 立刻停发；明细协议标注与实际发包一致");

        var port = FindBindablePort();
        using var echo = new CountingEchoServer(port);

        try
        {
            echo.Start();
        }
        catch
        {
            Check("计数回显服务端启动", false, "端口不可绑定");
            return;
        }

        var listenPort = FindBindablePort();
        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "toggle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        ConfigManager.Save(new AppConfig
        {
            ListenPort = listenPort,
            IntervalMs = 200,
            TimeoutMs = 500,
            Links = { new LinkConfig { Name = "本机", Host = "127.0.0.1", Port = port, Tcp = true, Udp = true } }
        });

        RunOnSta(() =>
        {
            var window = new MainWindow();
            InvokePrivateHandler(window, "Start_Click");
            Pump(2000);

            var trend = (IList)GetPrivateField(window, "_trend")!;
            var rows = (IList)GetPrivateField(window, "_linkRows")!;
            var session = (StatSession)GetPrivateField(window, "_session")!;
            var tcpGlobal = (CheckBox)window.FindName("TcpCheck")!;
            var udpGlobal = (CheckBox)window.FindName("UdpCheck")!;

            Check("TCP 探测确实到达对端", echo.TcpCount > 0, "tcp=" + echo.TcpCount);
            Check("UDP 探测确实到达对端", echo.UdpCount > 0, "udp=" + echo.UdpCount);

            // ② 明细里的协议标注 vs 对端**实际收到的包**。
            //    用"只开一个协议"来做判定，比数行数更硬：只开 TCP 时，对端一个 UDP 都不该收到，
            //    明细里也不该出现任何 UDP 行（反之亦然）。
            udpGlobal.IsChecked = false; // 全局只留 TCP
            InvokePrivateHandler(window, "Clear_Click");

            var tcpBeforeTcpOnly = echo.TcpCount;
            var udpBeforeTcpOnly = echo.UdpCount;
            Pump(2000);

            Check("只勾 TCP 时明细里全是 TCP 行（没有把 TCP 标成 UDP）",
                trend.Count > 0 && trend.Cast<ProbeResult>().All(r => r.Protocol == "TCP"),
                "协议取值：" + string.Join(",", trend.Cast<ProbeResult>().Select(r => r.Protocol).Distinct()));
            Check("只勾 TCP 时对端几乎不再收到 UDP（最多允许切换那一刻已在途的 1 个）",
                echo.UdpCount - udpBeforeTcpOnly <= 1 && echo.TcpCount > tcpBeforeTcpOnly,
                $"tcp {tcpBeforeTcpOnly}→{echo.TcpCount} / udp {udpBeforeTcpOnly}→{echo.UdpCount}");
            Check("会话样本数与对端收到的探测数一致（结果没有被吞）",
                Math.Abs(session.TotalSamples - (echo.TcpCount - tcpBeforeTcpOnly)) <= 2,
                $"样本 {session.TotalSamples} / 对端新增 {echo.TcpCount - tcpBeforeTcpOnly}");

            udpGlobal.IsChecked = true;  // 全局只留 UDP
            tcpGlobal.IsChecked = false;
            InvokePrivateHandler(window, "Clear_Click");

            var tcpBeforeUdpOnly = echo.TcpCount;
            var udpBeforeUdpOnly = echo.UdpCount;
            Pump(2000);

            Check("只勾 UDP 时明细里全是 UDP 行",
                trend.Count > 0 && trend.Cast<ProbeResult>().All(r => r.Protocol == "UDP"),
                "协议取值：" + string.Join(",", trend.Cast<ProbeResult>().Select(r => r.Protocol).Distinct()));
            Check("只勾 UDP 时对端几乎不再收到 TCP（同上，最多 1 个在途）",
                echo.TcpCount - tcpBeforeUdpOnly <= 1 && echo.UdpCount > udpBeforeUdpOnly,
                $"tcp {tcpBeforeUdpOnly}→{echo.TcpCount} / udp {udpBeforeUdpOnly}→{echo.UdpCount}");

            tcpGlobal.IsChecked = true; // 恢复两个协议，继续做"运行中改链路"的验证
            InvokePrivateHandler(window, "Clear_Click");
            Pump(1500);

            // ① 运行中取消该链路的 TCP 勾选 → 立刻停发 TCP
            //    （协议勾选是"收紧"，立即生效；主机/端口改动才有 1 秒防抖）
            ((LinkRow)rows[0]!).Tcp = false;

            Pump(1200); // 让"已经发起的那一轮"跑完
            var tcpAfterToggle = echo.TcpCount;
            var udpAtToggle = echo.UdpCount;

            Pump(2500); // 再看一段时间：不该再有任何新的 TCP 探测

            Check("取消勾选 TCP 后对端不再收到新的 TCP 探测", echo.TcpCount == tcpAfterToggle,
                $"切换后 {tcpAfterToggle} → 再等 2.5 秒 {echo.TcpCount}");
            Check("同一链路仍在正常发 UDP", echo.UdpCount > udpAtToggle, $"udp {udpAtToggle} → {echo.UdpCount}");
            Check("该链路「生效」列同步显示只发 UDP", ((LinkRow)rows[0]!).Effective == "UDP",
                ((LinkRow)rows[0]!).Effective);

            // 重新勾上 → 必须恢复发 TCP（这条走防抖路径）
            var tcpResume = echo.TcpCount;
            ((LinkRow)rows[0]!).Tcp = true;
            Check("重新勾选 TCP 后恢复发送", PumpUntil(() => echo.TcpCount > tcpResume),
                $"{tcpResume} → {echo.TcpCount}");

            // ---- ③ 运行中由注册中心同步下来的新链路，立刻进入探测集合（无需重启测试） ----
            ApplySnapshot(window, new List<NodeSnapshot>
            {
                new() { NodeId = "peer-new", Name = "新机房", Ip = "203.0.113.250", Port = 50099, Tcp = true, Udp = true }
            });

            Check("运行中同步到的新链路立刻进入探测集合（无需重启测试）",
                PumpUntil(() => ((LinkConfig[])GetPrivateField(window, "_liveLinks")!)
                    .Any(l => l.NodeId == "peer-new" && l.Host == "203.0.113.250" && l.Port == 50099)),
                string.Join(" / ", ((LinkConfig[])GetPrivateField(window, "_liveLinks")!)
                    .Select(l => $"{l.Name}@{l.Host}:{l.Port}")));

            InvokePrivateHandler(window, "Stop_Click");
            window.Close();
        }, "运行中改链路断言");

        ConfigManager.OverridePath = previousOverride;
        try { Directory.Delete(root, true); } catch { }
    }

    /// <summary>
    /// 能数入站探测次数的纯回显服务端（等价老版本被动端：读到什么回什么，不追加任何信息）。
    /// 用它才能回答"界面说没发 TCP，实际上到底有没有 TCP 包出去"。
    /// </summary>
    private sealed class CountingEchoServer : IDisposable
    {
        private readonly TcpListener _tcp;
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _cts = new();
        private int _tcpCount;
        private int _udpCount;

        public CountingEchoServer(int port)
        {
            _tcp = new TcpListener(IPAddress.Loopback, port);
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        }

        public int TcpCount => Volatile.Read(ref _tcpCount);
        public int UdpCount => Volatile.Read(ref _udpCount);

        public void Start()
        {
            _tcp.Start();
            _ = Task.Run(TcpLoopAsync);
            _ = Task.Run(UdpLoopAsync);
        }

        private async Task TcpLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var accepted = await _tcp.AcceptTcpClientAsync(_cts.Token);
                    using var stream = accepted.GetStream();

                    var buffer = new byte[4096];
                    var n = await stream.ReadAsync(buffer, _cts.Token);
                    if (n <= 0) continue;

                    Interlocked.Increment(ref _tcpCount);
                    await stream.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // 客户端中途关闭等：继续下一轮
                }
            }
        }

        private async Task UdpLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var query = await _udp.ReceiveAsync(_cts.Token);
                    Interlocked.Increment(ref _udpCount);
                    await _udp.SendAsync(query.Buffer, query.RemoteEndPoint, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // 同上
                }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _tcp.Stop(); } catch { }
            try { _udp.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }

    // ---------------- 验收 A3 / A12 ----------------

    /// <summary>
    /// 两条"最容易做错"的验收：
    /// A12 —— 中心勾掉「分发」后，该节点**继续心跳也不会把自己顶回来**（只是标记，不是删配置）；
    /// A3 —— 在删除期间**离线**的节点，重新上线后同样拿不到那条链路（靠"全量快照"语义，
    ///       如果客户端按差值合并就会复活已删链路）。
    /// </summary>
    private static async Task TestRegistryAcceptanceAsync()
    {
        Section("验收 A3 / A12：中心隐藏不被心跳顶回；离线期间被删的链路重连后也消失");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "accept_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var port = FindBindablePort();
        var store = new RegistryStore(Path.Combine(root, "registry.json"), 60);
        var center = new RegistryCenterService(store, "preshared", 60);

        using var server = new ProbeServer(port);
        server.ProtocolHandler = center.HandleAsync;

        try
        {
            server.Start();
        }
        catch
        {
            Check("验收用例节点启动", false, "端口不可绑定");
            server.Dispose();
            return;
        }

        var logDir = Path.Combine(root, "logs");

        try
        {
            // ---- A12：勾掉「分发」以后，心跳不能把它顶回来 ----
            var register = new RegisterMessage
            {
                NodeId = "b",
                Name = "机房B",
                Ip = "203.0.113.12",
                Port = 50002,
                Tcp = true,
                Udp = true,
                Instance = "inst-b"
            };

            Check("先让 B 进清单", store.Upsert(register, true).Accepted);
            Eq("B 在快照里", 1, store.BuildSnapshot().Count);

            Check("中心勾掉 B 的「分发」", store.SetCenterHidden("b", true));
            Eq("勾掉后快照里没有 B（其他节点该链路消失）", 0, store.BuildSnapshot().Count);

            for (var i = 0; i < 3; i++) store.Upsert(register, true);

            Check("B 继续心跳不会把自己顶回分发",
                store.Nodes.First(n => n.NodeId == "b").CenterHidden);
            Eq("心跳后快照仍为空", 0, store.BuildSnapshot().Count);

            Check("取消勾选后立即恢复", store.SetCenterHidden("b", false));
            Eq("恢复后快照又有 B", 1, store.BuildSnapshot().Count);

            // ---- A3：A 离线期间 B 被移除，A 重新上线后同样没有 B 的链路 ----
            using (var nodeB = new RegistryTestNode("b", "203.0.113.12", 50002, port, "preshared", logDir))
            {
                var first = new RegistryTestNode("a", "203.0.113.11", 50001, port, "preshared", logDir);
                try
                {
                    Check("A 首次注册成功", await first.Client.RegisterNowAsync(), first.Client.Status.LastError);
                    Eq("A 先拿到 B 的链路", 1, first.Snapshot().Count);
                }
                finally
                {
                    first.Dispose(); // A 下线
                }

                Check("A 离线期间，中心移除 B", store.RemoveNode("b"));

                // 重新上线：全新客户端（进程内 rev 从 0 开始），必须整体替换本地注册链路
                using var again = new RegistryTestNode("a", "203.0.113.11", 50001, port, "preshared", logDir);
                Check("A 重连注册成功", await again.Client.RegisterNowAsync(), again.Client.Status.LastError);
                Eq("重连后 B 的链路同样消失（全量快照，不复活已删链路）", 0, again.Snapshot().Count);
                Check("重连拿到的确实是全量快照（rev 变了）", again.Client.Status.Revision == store.Revision,
                    $"客户端 rev={again.Client.Status.Revision}，中心 rev={store.Revision}");
            }
        }
        finally
        {
            server.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ---------------- IPv4 输入控件 ----------------

    /// <summary>
    /// 现场根因：地址框与端口框长得一样，IP 被敲进了端口框。
    /// 现在地址是"四段数字 + 固定圆点"的标准 IPv4 样式，端口只收数字——两边都不该再填错。
    /// </summary>
    private static void TestIpv4Box()
    {
        Section("IPv4 输入控件：四段式 / 主机名回退 / 越界标红");

        RunOnSta(() =>
        {
            var box = new Ipv4Box();

            box.Text = "203.0.113.9";
            Eq("IPv4 往返", "203.0.113.9", box.Text);
            Eq("IPv4 模式下第一段", "203", ((TextBox)box.FindName("Octet1")!).Text);
            Eq("IPv4 模式下末段", "9", ((TextBox)box.FindName("Octet4")!).Text);
            Check("四段齐全判定为完整 IPv4", box.IsCompleteIpv4);
            Check("默认不是主机名模式", !box.IsHostMode);

            box.Text = "";
            Eq("清空后回到 IPv4 模式且内容为空", ("", false), (box.Text, box.IsHostMode));

            box.Text = "vpn.example.com";
            Eq("域名自动切到主机名模式并保留原文", "vpn.example.com", box.Text);
            Check("主机名模式下 IsHostMode 为真", box.IsHostMode);
            Check("主机名模式不算完整 IPv4", !box.IsCompleteIpv4);

            box.Text = "127.0.0.1";
            Check("切回 IPv4 模式", !box.IsHostMode);
            Eq("切回后内容正确", "127.0.0.1", box.Text);

            // 只填了一半：必须能原样读回来（外层校验据此报"不是合法的 IPv4 地址"）
            box.Text = "203.0.113";
            Check("不完整的地址不会被当成 IPv4 铺进四段", box.IsHostMode || box.Text != "203.0.113.0",
                box.Text);
            Check("不完整地址判定为不完整", !box.IsCompleteIpv4, box.Text);

            // 越界的段要标红（999 不是合法段）
            box.Text = "";
            ((TextBox)box.FindName("Octet1")!).Text = "999";
            var border = ((TextBox)box.FindName("Octet1")!).BorderBrush as SolidColorBrush;
            Check("越界的段标红", border?.Color == Color.FromRgb(0xC5, 0x22, 0x1F),
                border?.Color.ToString() ?? "null");

            ((TextBox)box.FindName("Octet1")!).Text = "203";
            border = ((TextBox)box.FindName("Octet1")!).BorderBrush as SolidColorBrush;
            Check("合法的段恢复正常颜色", border?.Color == Color.FromRgb(0xDA, 0xDC, 0xE0),
                border?.Color.ToString() ?? "null");

            // 解析规则本身
            Check("TryParseOctets 接受合法地址", Ipv4Box.TryParseOctets("203.0.113.9", out var parts) && parts[1] == "0");
            Check("TryParseOctets 拒绝三段", !Ipv4Box.TryParseOctets("203.0.113", out _));
            Check("TryParseOctets 拒绝越界段", !Ipv4Box.TryParseOctets("203.0.113.999", out _));
            Check("TryParseOctets 拒绝域名", !Ipv4Box.TryParseOctets("vpn.example.com", out _));
            Check("TryParseOctets 拒绝全角数字", !Ipv4Box.TryParseOctets("２０３.0.113.9", out _));
            Check("TryParseOctets 拒绝空段", !Ipv4Box.TryParseOctets("203..113.9", out _));
        }, "IPv4 控件断言");
    }

    // ---------------- 顶部大号节点名 ----------------

    /// <summary>
    /// 本机节点名称大号显示在界面顶部：同机开多个实例时一眼认出"这台是哪台"。
    /// 这里锁住三件事：字号足够大、正在编辑的名字立刻跟随、没填时给提示而不是拿机器名冒充。
    /// </summary>
    private static void TestNodeNameHeader()
    {
        Section("顶部大号节点名：取值来源 / 编辑实时跟随 / 未填写提示");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "nodename_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        ConfigManager.Save(new AppConfig
        {
            ListenPort = 8800,
            Links = { new LinkConfig { Name = "本机", Host = "127.0.0.1", Port = 9001 } },
            Registrar = new RegistrarConfig { Enabled = true, NodeName = "机房A", NodeId = "self-node", PublicHost = "203.0.113.9", PublicPort = 8800, CenterHost = "203.0.113.1", CenterPort = 16298, Secret = "test-secret" }
        });

        RunOnSta(() =>
        {
            var window = new MainWindow();
            var header = window.FindName("NodeNameText") as TextBlock;
            var note = window.FindName("NodeNameNote") as TextBlock;

            Check("窗口顶部有节点名显示控件", header is not null);
            Check("节点名是大号显示（≥20px）", header!.FontSize >= 20, header.FontSize.ToString());
            Check("节点名加粗", header.FontWeight == FontWeights.Bold);
            Eq("节点名取值来自配置", "机房A", header.Text);
            Eq("有名字时不显示提示", Visibility.Collapsed, note!.Visibility);
            Check("窗口标题也带节点名（任务栏能区分多个实例）",
                window.Title.Contains("机房A"), window.Title);

            // 在注册中心页改名字 → 顶部立刻跟随（不必等保存/启动）
            var panel = (RegistryPanel)window.FindName("RegistryTabPanel")!;
            ((TextBox)panel.FindName("NodeNameBox")!).Text = "机房A-改";
            Eq("编辑节点名后顶部立刻跟随", "机房A-改", header.Text);
            Check("窗口标题同步跟随", window.Title.Contains("机房A-改"), window.Title);

            // 清空 → 提示该去哪儿填，且不用机器名冒充
            ((TextBox)panel.FindName("NodeNameBox")!).Text = "   ";
            Eq("清空后显示未命名", "未命名节点", header.Text);
            Eq("未命名时给出填写提示", Visibility.Visible, note.Visibility);
            Check("未命名时窗口标题不拼接空名字", window.Title == "机房线路稳定性测试工具", window.Title);
            Check("未命名时用灰色弱化显示", (header.Foreground as SolidColorBrush)?.Color == Color.FromRgb(0x9A, 0xA0, 0xA6),
                (header.Foreground as SolidColorBrush)?.Color.ToString() ?? "null");

            ((TextBox)panel.FindName("NodeNameBox")!).Text = "机房B";
            Eq("重新填写后恢复大号加粗", "机房B", header.Text);
            Check("恢复后重新加粗", header.FontWeight == FontWeights.Bold);
            Eq("恢复后提示收起", Visibility.Collapsed, note.Visibility);

            window.Close();
        }, "顶部节点名断言");

        ConfigManager.OverridePath = previousOverride;
        try { Directory.Delete(root, true); } catch { }
    }

    // ---------------- 兼容矩阵逐行验证 ----------------

    /// <summary>
    /// 规格 §12 兼容矩阵里最容易悄悄坏掉的两行：
    /// ① 老探测 → 新节点：装了协议处理器之后，探测回显必须**逐字节**不变（老客户端是逐字节比对的）；
    /// ② 老配置 → 新程序：没有 `Registrar` 段时一个字节的注册流量都不该产生。
    /// （另外三行在既有用例里：新↔新见三节点端到端；新→老节点见"不是注册中心"；新→非中心见 unsupported。）
    /// </summary>
    private static async Task TestRegistryCompatMatrixAsync()
    {
        Section("兼容矩阵：老探测 → 新节点（逐字节）/ 老配置 → 无注册行为");

        var port = FindBindablePort();
        var server = new ProbeServer(port);

        // 刻意装上协议处理器（新节点就是这样）：证明分流不会动到老路径
        server.ProtocolHandler = (request, length, _, ct) =>
            RegistryCenterService.RespondUnsupportedAsync("preshared", request, length, ct);

        try
        {
            server.Start();
        }
        catch
        {
            Check("兼容用例节点启动", false, "端口不可绑定");
            server.Dispose();
            return;
        }

        // ---- 老客户端的探测报文格式：不带 v2 标记、可以没有换行 ----
        var legacyBare = Encoding.UTF8.GetBytes("45|deadbeefdeadbeefdeadbeefdeadbeef");
        var legacyV1 = Encoding.UTF8.GetBytes("46|deadbeefdeadbeefdeadbeefdeadbeef\n");

        using (var udp = new UdpClient(AddressFamily.InterNetwork))
        {
            udp.Connect(IPAddress.Loopback, port);

            await udp.SendAsync(legacyBare, legacyBare.Length);
            var bareReply = (await udp.ReceiveAsync()).Buffer;
            Check("老格式 UDP 探测（无换行）仍逐字节原样回显",
                bareReply.AsSpan().SequenceEqual(legacyBare), Encoding.UTF8.GetString(bareReply));

            await udp.SendAsync(legacyV1, legacyV1.Length);
            var v1Reply = (await udp.ReceiveAsync()).Buffer;
            Check("连带换行的老格式 UDP 也逐字节原样回显",
                v1Reply.AsSpan().SequenceEqual(legacyV1), Encoding.UTF8.GetString(v1Reply));

            // ---- 同一端口上的注册帧：明确回 ERR（而不是被当成探测包回显） ----
            var register = RegistryProtocol.EncodeSigned("preshared", RegistryProtocol.TypeRegister,
                RegistryProtocol.Serialize(new RegisterMessage
                {
                    ReqId = "req-1",
                    NodeId = "node-x",
                    Name = "节点X",
                    Ip = "203.0.113.30",
                    Port = 9000,
                    Ts = RegistryAuth.NowUnixSeconds(),
                    Nonce = RegistryAuth.NewNonce()
                }));

            await udp.SendAsync(register, register.Length);
            var errorReply = (await udp.ReceiveAsync()).Buffer;

            Check("同端口的注册帧被分流到协议路径（不是原样回显）",
                RegistryProtocol.TryDecode(errorReply, errorReply.Length, out var errorType, out _, out var errorPayload) &&
                errorType == RegistryProtocol.TypeError,
                Encoding.UTF8.GetString(errorReply));

            var error = RegistryProtocol.Deserialize<ErrorMessage>(errorPayload);
            Eq("ERR 带上请求的 reqId（发起方能对上号）", "req-1", error?.ReqId ?? "");
            Eq("ERR 错误码为 unsupported", RegistryErrorCodes.Unsupported, error?.Code ?? "");
        }

        // ---- 老格式 TCP 探测 ----
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(legacyV1);
            var reply = await ReadFrameAsync(stream, 3000);

            Check("老格式 TCP 探测仍逐字节原样回显",
                reply.AsSpan().SequenceEqual(legacyV1), Encoding.UTF8.GetString(reply));
        }

        server.Dispose();

        // ---- 老配置：没有 Registrar 段 → 不产生任何注册行为 ----
        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "legacy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        var listenPort = FindBindablePort();
        ConfigManager.Save(new AppConfig
        {
            ListenPort = listenPort,
            IntervalMs = 500,
            TimeoutMs = 500,
            Links = { new LinkConfig { Name = "本机", Host = "127.0.0.1", Port = port } }
        });

        RunOnSta(() =>
        {
            var window = new MainWindow();

            InvokePrivateHandler(window, "Start_Click");
            Pump(400);

            Check("老配置启动后不创建注册客户端", GetPrivateField(window, "_registryClient") is null);
            Check("老配置启动后没有中心服务", GetPrivateField(window, "_registryCenter") is null);
            Check("服务端照常起来（探测不受影响）", GetPrivateField(window, "_server") is not null);

            var note = ((TextBlock)window.FindName("StatusNote")!).Text;
            Check("状态栏不出现注册相关提示（与升级前一致）", !note.Contains("注册"), note);

            Check("不会凭空空生成中心清单文件", !File.Exists(Path.Combine(root, "registry.json")));

            InvokePrivateHandler(window, "Stop_Click");
            window.Close();
        }, "老配置兼容断言");

        ConfigManager.OverridePath = previousOverride;
        try { Directory.Delete(root, true); } catch { }
    }

    /// <summary>按换行读一帧（探测回显以 <c>\n</c> 结尾）。</summary>
    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, int timeoutMs)
    {
        var buffer = new List<byte>(64);
        var one = new byte[1];

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (buffer.Count < 4096)
            {
                var read = await stream.ReadAsync(one.AsMemory(0, 1), cts.Token);
                if (read == 0) break;

                buffer.Add(one[0]);
                if (one[0] == (byte)'\n') break;
            }
        }
        catch (OperationCanceledException)
        {
            // 超时：把已读到的部分返回，由断言判定失败
        }

        return buffer.ToArray();
    }

    // ---------------- STA 辅助 ----------------

    /// <summary>
    /// 一边跑消息泵一边等条件成立（上限 timeoutMs）。
    ///
    /// 为什么不用固定 Pump(N)：界面链路是"500ms 定时器 + 1 秒防抖"，
    /// 固定睡眠在机器忙的时候可能一次 tick 都赶不上——实测偶发失败过一次。
    /// 这里等的是"最终会成立"，上限远大于防抖窗口，既不脆也不放水。
    /// </summary>
    private static bool PumpUntil(Func<bool> condition, int timeoutMs = 6000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Pump(100);
        }

        return condition();
    }

    private static void RunOnSta(Action action, string label, int timeoutSeconds = 90)
    {
        string? error = null;

        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex.ToString(); }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            Check(label + "在超时内完成", false, "超时（可能弹出了模态对话框）");
            return;
        }

        Check(label + "未抛异常", error is null, error);
    }

    /// <summary>带参数地调用主窗口的私有处理器（<see cref="InvokePrivateHandler(object,string)"/> 只支持单参数）。</summary>
    private static void InvokePrivateHandlerWith(object target, string name, params object[] args)
    {
        var methods = target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == name && m.GetParameters().Length == args.Length)
            .ToList();

        if (methods.Count == 0) throw new InvalidOperationException("未找到处理器：" + name);

        try
        {
            methods[0].Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
