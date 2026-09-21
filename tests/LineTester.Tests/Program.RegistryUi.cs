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

        Eq("协议文案：全开", "TCP + UDP", RegistryUiText.DescribeProtocols(true, true));
        Eq("协议文案：仅 TCP", "仅 TCP", RegistryUiText.DescribeProtocols(true, false));
        Eq("协议文案：仅 UDP", "仅 UDP", RegistryUiText.DescribeProtocols(false, true));
        Eq("协议文案：都未勾选", "都未勾选", RegistryUiText.DescribeProtocols(false, false));

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
