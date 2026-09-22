using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LineTester.Isp;
using LineTester.Registry;
using LineTester.Views;

namespace LineTester;

public partial class MainWindow : Window
{
    private const int MaxAnomalies = 1000;
    private const int MaxTrendRows = 2000;
    private const string BaseTitle = "机房线路稳定性测试工具";

    // 冻结过的静态画刷：跨线程（自检里每个界面用例一个新 STA 线程）安全
    private static readonly Brush NodeNameBrush = Frozen(0x20, 0x21, 0x24);
    private static readonly Brush NodeNameMutedBrush = Frozen(0x9A, 0xA0, 0xA6);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly ProbeClient _client = new();
    private readonly StatSession _session = new();
    private readonly CsvLogWriter _log = new(Path.Combine(AppContext.BaseDirectory, "logs"));

    private readonly ObservableCollection<StatRow> _stats = new();
    private readonly Dictionary<string, StatRow> _statRows = new(StringComparer.Ordinal);
    private readonly ObservableCollection<ProbeResult> _anomalies = new();
    private readonly ObservableCollection<ProbeResult> _trend = new();
    private readonly ConcurrentQueue<ProbeResult> _pending = new();

    /// <summary>链路编辑器里的行（界面上可直接增删改）。</summary>
    private readonly ObservableCollection<LinkRow> _linkRows = new();

    /// <summary>最近一次被删除的行，用于「撤销删除」（防误删）。</summary>
    private readonly List<(LinkRow Row, int Index)> _lastDeleted = new();

    private bool _revalidating;
    private string _appliedLinksSignature = "";
    private string _savedLinksSignature = "";

    /// <summary>删除确认（测试可替换，避免模态框阻塞；默认弹“是/否”，默认选“否”）。</summary>
    private Func<string, string, bool> _confirmDelete = (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            == MessageBoxResult.Yes;

    /// <summary>提示对话框（测试可替换）。</summary>
    private Action<string, string> _alert = (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    // UI 统一按固定节奏刷新，而不是每条探测结果都刷一次
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private AppConfig _settings = new();
    private (DateTime WriteUtc, long Length)? _configStamp;
    private string? _configLoadError;
    private string? _statusNote;
    private bool _statusIsError;

    /// <summary>界面上勾选的协议开关，探测循环每轮读取，因此取消勾选立即生效。</summary>
    private volatile bool _liveTcp = true;
    private volatile bool _liveUdp = true;
    private volatile bool _liveIcmp;

    private ProbeServer? _server;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// 探测循环每轮读取的链路快照。注册中心同步到新链路、以及编辑器里的改动都会刷新它，
    /// 因此"取消勾选 TCP""改目标"等改动**不必重启测试**（现场反馈：界面写着"不发包"、包却照发）。
    /// </summary>
    private volatile LinkConfig[] _liveLinks = Array.Empty<LinkConfig>();

    /// <summary>链路编辑器最近一次改动的时刻（防抖用）；null = 没有待生效的改动。</summary>
    private DateTime? _linksDirtyUtc;

    /// <summary>链路配置与运行关键动作的日志（logs\日期\config.log）：改了什么、什么时候落盘、同步把哪些链路动了。</summary>
    private readonly AppLog _configLog = new(Path.Combine(AppContext.BaseDirectory, "logs"), "config.log");

    /// <summary>中心侧协议日志（logs\日期\registry-center.log）。</summary>
    private readonly AppLog _centerLog = new(Path.Combine(AppContext.BaseDirectory, "logs"), "registry-center.log");

    private RegistryClient? _registryClient;
    private RegistryStore? _registryStore;
    private RegistryCenterService? _registryCenter;

    /// <summary>当前同步客户端"登记在案"的注册参数签名：变了就重建客户端，让中心立刻看到新值。</summary>
    private string _registryClientSignature = "";

    // ---- 运营商查询（跨运营商判定）----

    private IspLookupService? _isp;

    /// <summary>每条链路最近一次学到的"对端看到的本端出口地址"，供同链路其它协议复用。</summary>
    private readonly Dictionary<string, string> _linkEgress = new(StringComparer.Ordinal);

    /// <summary>
    /// 「RTT 曲线」页的数据：按「链路 + 协议」保存**整个会话**的采样。
    /// 不复用「实时RTT明细」那张表——它只留最后 2000 条，现场 21 路探测只够 95 秒，画不出一个会话。
    /// </summary>
    private readonly ChartHistory _chartHistory = new();

    /// <summary>用户勾选过的曲线键（按会话键记着，链路/协议重建统计行后勾选状态不丢）。</summary>
    private readonly HashSet<string> _chartSelection = new(StringComparer.Ordinal);

    /// <summary>
    /// 历史曲线的数据源：直接读 logs 下的 CSV（逐包落盘的原始记录，本身就是"上一个会话"的数据）。
    /// 增量读取，因此 24 小时视图可以跟着实时数据一起长。
    /// </summary>
    private readonly ChartLogReader _chartLog = new(Path.Combine(AppContext.BaseDirectory, "logs"));

    /// <summary>历史视图的曲线对象（与内存里那份会话数据分开，互不干扰）。</summary>
    private readonly ChartHistory _chartHistoryView = new();

    private string _chartHistorySignature = "";
    private int _chartLoadedSamples;
    private string _chartLoadNote = "";

    /// <summary>
    /// 探测循环每轮开始时读取的“当前会话号”。
    /// 必须每轮重新读取：若固定为启动时的值，点击“清除”后所有结果都会被当成陈旧数据丢弃，
    /// 统计将永远无法重新开始。
    /// </summary>
    private long _loopEpoch;

    public MainWindow()
    {
        InitializeComponent();

        StatsGrid.ItemsSource = _stats;
        AnomalyGrid.ItemsSource = _anomalies;
        TrendGrid.ItemsSource = _trend;
        LinksGrid.ItemsSource = _linkRows;

        // 在控件树就绪后再挂事件，避免 XAML 解析期间 IsChecked 触发时其它控件还未创建
        TcpCheck.Checked += Protocol_Changed;
        TcpCheck.Unchecked += Protocol_Changed;
        UdpCheck.Checked += Protocol_Changed;
        UdpCheck.Unchecked += Protocol_Changed;
        IcmpCheck.Checked += Protocol_Changed;
        IcmpCheck.Unchecked += Protocol_Changed;

        // 注册中心页：校验与落盘仍走主窗口这一条路径，页内只负责显示与即时操作
        RegistryTabPanel.SaveRequested += () => SaveConfig_Click(this, new RoutedEventArgs());
        RegistryTabPanel.RegisterRequested += OnRegistryRegisterRequested;
        RegistryTabPanel.CenterChanged += OnRegistryCenterChanged;
        RegistryTabPanel.NodeNameChanged += UpdateNodeNameDisplay;

        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        // 切到「RTT 曲线」页时立刻补画一次（不在这页时每 500 毫秒的重画就跳过了）
        MainTabs.SelectionChanged += (_, _) => RefreshChart();

        // 曲线的时间范围/日期变化 → 立刻重新取数（默认「最近 24 小时」，数据来自 logs 日志）
        ChartPanel.RangeChanged += OnChartRangeChanged;

        _log.ErrorOccurred += OnLogError;

        LoadConfigFromDisk(showError: false);

        Loaded += (_, _) =>
        {
            if (_configLoadError is not null) ShowConfigError(_configLoadError);

            // 启动就说清"在用哪份配置"（并提示多实例）：现场"改了保存、重启又变回去"
            // 最常见的原因就是启动的不是同一份程序
            ShowConfigLocation();
        };
    }

    /// <summary>
    /// 启动时把"正在使用哪份配置"明写出来。
    ///
    /// 现场踩过：改了配置、保存、重启后"又变回去了"——实际是**启动的不是同一份程序**
    /// （不同目录各有一份 appsettings.json，读的是那份旧的）。把路径显示出来，
    /// 这类问题 5 秒就能看清，不用猜。
    /// </summary>
    private void ShowConfigLocation()
    {
        var note = "配置文件：" + ConfigManager.ConfigPath;

        var others = OtherInstanceCount();
        if (others > 0)
        {
            note = $"⚠ 检测到另外 {others} 个 LineTester 实例（同一份配置）：两个实例保存会互相覆盖，建议只留一个。"
                   + "  " + note;
        }

        UpdateStatus(note);
    }

    /// <summary>统计"用同一个程序路径在跑"的其它实例数（同一个目录 = 同一份配置）。</summary>
    private static int OtherInstanceCount()
    {
        try
        {
            var self = Process.GetCurrentProcess();
            var path = self.MainModule?.FileName ?? "";
            if (path.Length == 0) return 0;

            return Process.GetProcessesByName(self.ProcessName)
                .Count(p =>
                {
                    try
                    {
                        return p.Id != self.Id &&
                               string.Equals(p.MainModule?.FileName ?? "", path, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });
        }
        catch
        {
            return 0;
        }
    }

    private void WarnIfAnotherInstance() => ShowConfigLocation();

    // ---------------- 配置 ----------------

    private void LoadConfigFromDisk(bool showError)
    {
        _settings = ConfigManager.Load(out var error);
        _configLoadError = error;
        _configStamp = ConfigManager.Stamp();

        _configLog.Section($"加载配置 {ConfigManager.ConfigPath}");
        if (error is not null) _configLog.Write($"  读取错误：{error}");
        _configLog.Write($"  {_settings.Links.Count} 条链路，全局 TCP={_settings.EnableTcp} UDP={_settings.EnableUdp} ICMP={_settings.EnableIcmp}，" +
                         $"注册 Enabled={_settings.Registrar.Enabled} ServeAsCenter={_settings.Registrar.ServeAsCenter} " +
                         $"节点名={_settings.Registrar.NodeName} 地址=[{string.Join(",", new[] { _settings.Registrar.PublicHost }.Concat(_settings.Registrar.PublicHosts ?? new List<string>()))}]");
        foreach (var link in _settings.Links) _configLog.Write($"  {DescribeLink(link)}");

        ApplyConfigToUi(_settings);
        if (error is not null && showError) ShowConfigError(error);
        UpdateStatus();
    }

    /// <summary>链路的一行日志描述（来源/协议/本地意图都写全，排障时能直接比对）。</summary>
    private static string DescribeLink(LinkConfig link)
        => $"链路「{link.Name}」{link.Host}:{link.Port} tcp={link.Tcp} udp={link.Udp} icmp={link.Icmp} " +
           $"source={link.Source} nodeId={(link.NodeId.Length > 8 ? link.NodeId[..8] : link.NodeId)} " +
           $"localDisabled={link.LocalDisabled} localTcpOff={link.LocalTcpOff} localUdpOff={link.LocalUdpOff} localIcmpOff={link.LocalIcmpOff}";

    private void ShowConfigError(string error)
    {
        MessageBox.Show(
            "配置文件读取失败，界面已显示默认值。\n" +
            "为避免覆盖原文件，程序不会自动保存配置；修正文件或点击“保存配置”后即可正常使用。\n\n" +
            error + "\n\n文件位置：" + ConfigManager.ConfigPath,
            "配置错误", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ApplyConfigToUi(AppConfig cfg)
    {
        ListenPortBox.Text = cfg.ListenPort.ToString(CultureInfo.InvariantCulture);
        IntervalBox.Text = cfg.IntervalMs.ToString(CultureInfo.InvariantCulture);
        TimeoutBox.Text = cfg.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        WarningBox.Text = cfg.WarningRttMs.ToString(CultureInfo.InvariantCulture);
        WindowBox.Text = cfg.StatWindowMinutes.ToString(CultureInfo.InvariantCulture);
        LossWarnBox.Text = cfg.LossWarnPercent.ToString("0.###", CultureInfo.InvariantCulture);
        LossCountBox.Text = cfg.ConsecutiveLossWarn.ToString(CultureInfo.InvariantCulture);

        // 会触发 Protocol_Changed，从而把 _liveTcp/_liveUdp 同步到最新
        TcpCheck.IsChecked = cfg.EnableTcp;
        UdpCheck.IsChecked = cfg.EnableUdp;
        IcmpCheck.IsChecked = cfg.EnableIcmp;

        // 会触发 IspCheck_Changed，从而按配置开关运营商查询
        IspCheck.IsChecked = cfg.Isp.Enabled;

        RegistryTabPanel.Load(cfg.Registrar);

        LoadLinkRows(cfg.Links);
        UpdateNodeNameDisplay();
        RefreshRegistryUi();
    }

    /// <summary>
    /// 顶部大号显示本机节点名称：同一台机器/同一机房开多个实例时，靠它一眼认出这台是哪台。
    ///
    /// 取值以「注册中心」页的输入框为准（它在加载/保存时与配置同步，因此永远反映当前值）：
    /// 刚敲进去就显示、清空就显示"未命名"——若这里再回退到配置里的旧值，
    /// 用户清空后顶部还挂着旧名字，反而像是清不掉。
    /// 没填时给出去哪儿填的提示，不用机器名冒充（那会让人以为节点名已经配好了）。
    /// </summary>
    private void UpdateNodeNameDisplay()
    {
        if (NodeNameText is null) return;

        var name = RegistryTabPanel is not null
            ? RegistryTabPanel.NodeName
            : _settings.Registrar.NodeName?.Trim() ?? "";

        if (name.Length == 0)
        {
            NodeNameText.Text = "未命名节点";
            NodeNameText.Foreground = NodeNameMutedBrush;
            NodeNameText.FontWeight = FontWeights.Normal;
            NodeNameNote.Visibility = Visibility.Visible;
            Title = BaseTitle;
            return;
        }

        NodeNameText.Text = name;
        NodeNameText.Foreground = NodeNameBrush;
        NodeNameText.FontWeight = FontWeights.Bold;
        NodeNameNote.Visibility = Visibility.Collapsed;
        Title = BaseTitle + " — " + name;
    }

    // ---------------- 链路编辑器 ----------------

    private void LoadLinkRows(List<LinkConfig> links)
    {
        foreach (var row in _linkRows) row.PropertyChanged -= OnLinkRowChanged;
        _linkRows.Clear();
        foreach (var link in links ?? new List<LinkConfig>()) AddLinkRow(LinkRow.FromConfig(link));

        _lastDeleted.Clear();
        UpdateUndoButton();

        ApplyGlobalProtocolsToRows();
        _savedLinksSignature = LinksSignature();
        _appliedLinksSignature = _savedLinksSignature;
        RevalidateLinks();
    }

    private void AddLinkRow(LinkRow row)
    {
        row.PropertyChanged += OnLinkRowChanged;
        _linkRows.Add(row);
    }

    private void OnLinkRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_revalidating) return;
        if (e.PropertyName is nameof(LinkRow.Problem) or nameof(LinkRow.IsValid) or nameof(LinkRow.Diagnosis)
            or nameof(LinkRow.IsRegistry) or nameof(LinkRow.SourceText) or nameof(LinkRow.LocalDisabled)) return;

        // 注册链路：本端对协议的勾选是"本地意图"，必须写回配置——
        // 否则下一次同步会按中心宣告的值把它改回来（现场反馈的"取消勾选 TCP，重启又勾上了"）。
        if (sender is LinkRow { IsRegistry: true } registryRow &&
            e.PropertyName is nameof(LinkRow.Tcp) or nameof(LinkRow.Udp) or nameof(LinkRow.Icmp))
        {
            var disabled = !registryRow.Tcp && !registryRow.Udp && !registryRow.Icmp;

            var changed = registryRow.LocalTcpOff != !registryRow.Tcp ||
                          registryRow.LocalUdpOff != !registryRow.Udp ||
                          registryRow.LocalIcmpOff != !registryRow.Icmp ||
                          registryRow.LocalDisabled != disabled;

            if (changed)
            {
                registryRow.LocalTcpOff = !registryRow.Tcp;
                registryRow.LocalUdpOff = !registryRow.Udp;
                registryRow.LocalIcmpOff = !registryRow.Icmp;
                registryRow.LocalDisabled = disabled;

                UpdateStatus(disabled
                    ? $"「{registryRow.Name}」已本地停测：注册同步不会覆盖这个意图，重新勾选任一协议即恢复"
                    : $"「{registryRow.Name}」本地协议选择已记录（{registryRow.Effective}），注册同步不会覆盖");
            }

            // 改过协议选择后立刻同步一次，让状态尽早与中心一致
            if (_registryClient is not null) _ = _registryClient.RegisterNowAsync();
        }
        else if (sender is LinkRow toggledRow &&
                 e.PropertyName is nameof(LinkRow.Tcp) or nameof(LinkRow.Udp) or nameof(LinkRow.Icmp) &&
                 toggledRow.IsValid)
        {
            // 协议勾选只可能"收紧"发包范围 → 立刻生效，不必等防抖
            ToggleLiveProtocol(toggledRow);
        }

        RevalidateLinks();
    }

    /// <summary>单行规则 + 跨行重复检查，返回是否全部通过。</summary>
    private bool RevalidateLinks()
    {
        if (_revalidating) return _linkRows.All(r => r.IsValid);

        _revalidating = true;
        try
        {
            foreach (var row in _linkRows) row.Revalidate();

            // 同一个目标 + 同一个协议被配了多次 = 重复发包，统计也会混在一起
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _linkRows.Count; i++)
            {
                var row = _linkRows[i];
                if (!row.IsValid) continue;

                if (row.Tcp && seen.TryGetValue(DuplicateKey(row, "TCP"), out var firstTcp))
                {
                    row.SetProblem($"与第 {firstTcp + 1} 行的 TCP 目标重复");
                    continue;
                }

                if (row.Udp && seen.TryGetValue(DuplicateKey(row, "UDP"), out var firstUdp))
                {
                    row.SetProblem($"与第 {firstUdp + 1} 行的 UDP 目标重复");
                    continue;
                }

                if (row.Icmp && seen.TryGetValue(DuplicateKey(row, ProbeClient.ProtocolIcmp), out var firstIcmp))
                {
                    row.SetProblem($"与第 {firstIcmp + 1} 行的 ICMP 目标重复");
                    continue;
                }

                if (row.Tcp) seen[DuplicateKey(row, "TCP")] = i;
                if (row.Udp) seen[DuplicateKey(row, "UDP")] = i;
                if (row.Icmp) seen[DuplicateKey(row, ProbeClient.ProtocolIcmp)] = i;
            }
        }
        finally
        {
            _revalidating = false;
        }

        UpdateLinkSummary();

        // 编辑器改动 → 稍后（防抖）交给运行中的探测循环，见 ApplyLiveLinks
        _linksDirtyUtc = DateTime.UtcNow;
        return _linkRows.All(r => r.IsValid);
    }

    /// <summary>
    /// 链路的"身份签名"，用来判断界面上有没有未保存/未生效的改动。
    /// **必须带上所有协议开关**：漏了 Icmp 时，只改 ICMP 勾选不会被认为是改动
    /// （"有未保存的修改"与"1 秒内生效"两条提示都不会出现）。
    /// </summary>
    private static string DuplicateKey(LinkRow row, string protocol)
        => protocol == ProbeClient.ProtocolIcmp
            ? $"{row.Name.Trim()}|{row.Host.Trim()}|ICMP"          // ping 的是主机，端口不参与
            : $"{row.Name.Trim()}|{row.Host.Trim()}|{row.Port}|{protocol}";

    private string LinksSignature()
        => LinksSignatureOf(_linkRows.Select(r => r.ToConfig()));

    /// <summary>同一套签名口径，但直接作用于配置（注册同步自动落盘后用它比对"是否已保存"）。</summary>
    private static string LinksSignatureOf(IEnumerable<LinkConfig> links)
        => string.Join(";", links.Select(l => $"{l.Name.Trim()}|{l.Host.Trim()}|{l.Port}|{l.Tcp}|{l.Udp}|{l.Icmp}"));

    private string LinkProblems()
        => string.Join("\n", _linkRows
            .Select((row, index) => (row, index))
            .Where(x => !x.row.IsValid)
            .Select(x => $"第 {x.index + 1} 行 {x.row.Display}：{x.row.Problem}"));

    private void UpdateLinkSummary()
    {
        if (LinkSummary is null) return;

        var parts = new List<string> { $"共 {_linkRows.Count} 条链路" };
        parts.Add($"配置中 TCP {_linkRows.Count(r => r.IsValid && r.Tcp)} / UDP {_linkRows.Count(r => r.IsValid && r.Udp)} / ICMP {_linkRows.Count(r => r.IsValid && r.Icmp)}");

        var invalid = _linkRows.Count(r => !r.IsValid);
        if (invalid > 0) parts.Add($"⚠ {invalid} 条校验未通过，保存与启动都会被拒绝");

        var disabled = _linkRows.Count(r => r.IsValid && !r.AnyProtocol);
        if (disabled > 0)
            parts.Add($"{disabled} 条不主动探测（TCP/UDP/ICMP 都未勾选，对端可能是纯服务端）");

        var signature = LinksSignature();
        if (!string.Equals(signature, _savedLinksSignature, StringComparison.Ordinal))
            parts.Add("有未保存的修改（点“保存配置”写入文件；探测已在用新链路）");

        if (_cts is not null && !string.Equals(signature, _appliedLinksSignature, StringComparison.Ordinal))
            parts.Add("刚改动的链路将在 1 秒内生效（防抖：避免边打字边发包）");

        // 全局与链路冲突时提示谁说了算
        var suppressed = _linkRows.Where(r => r.IsValid).SelectMany(r =>
        {
            var list = new List<string>(3);
            if (!_liveTcp && r.Tcp) list.Add($"{r.Name.Trim()} 的 TCP");
            if (!_liveUdp && r.Udp) list.Add($"{r.Name.Trim()} 的 UDP");
            if (!_liveIcmp && r.Icmp) list.Add($"{r.Name.Trim()} 的 ICMP");
            return list;
        }).ToList();

        if (suppressed.Count > 0)
            parts.Add($"全局未勾选，已压住：{string.Join("、", suppressed.Take(3))}" +
                      (suppressed.Count > 3 ? $" 等 {suppressed.Count} 项" : "") + "（总开关只能收紧）");

        if (_linkRows.Count == 0)
            parts.Add("没有链路时程序只监听应答，不主动发包");

        // 注册链路按来源机房分组：一眼看出"某个机房有几个 IP 各占一条链路"
        var registryGroups = _linkRows
            .Where(r => r.IsRegistry && r.Name.Trim().Length > 0)
            .GroupBy(r => BaseLinkName(r.Name), StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .Where(g => g.Count > 1)
            .ToList();

        if (registryGroups.Count > 0)
        {
            parts.Add("注册机房多 IP：" + string.Join("、",
                registryGroups.Select(g => $"{g.Name} ×{g.Count}")));
        }

        LinkSummary.Text = string.Join(" · ", parts);
    }

    /// <summary>把链路名里的 <c>#2/#3</c> 后缀去掉，得到来源机房名（同一机房的多条链路归到一组）。</summary>
    private static string BaseLinkName(string name)
    {
        var index = name.LastIndexOf('#');
        if (index <= 0) return name.Trim();

        var tail = name[(index + 1)..].Trim();
        return tail.Length > 0 && tail.All(char.IsAsciiDigit) ? name[..index].Trim() : name.Trim();
    }

    private void UpdateUndoButton()
    {
        UndoDeleteButton.IsEnabled = _lastDeleted.Count > 0;
        UndoDeleteButton.Content = _lastDeleted.Count == 0 ? "撤销删除" : $"撤销删除（{_lastDeleted.Count}）";
    }

    /// <summary>校验链路；不通过时跳到链路页并告知具体问题，返回 false 表示被拦下。</summary>
    private bool EnsureLinksValid(string action)
    {
        if (RevalidateLinks()) return true;

        MainTabs.SelectedItem = LinkTab;
        _alert("链路配置校验未通过，无法" + action +
               "。请修正「链路配置」页中标红的行：\n\n" + LinkProblems(), "链路配置错误");
        return false;
    }

    /// <summary>
    /// 注册链路的名称/主机/端口禁止本地编辑：它们由中心下发，本地改了也会在下一个同步周期被覆盖，
    /// 只会让人误以为"改了没生效"。要本地停测，请把 TCP 与 UDP 都取消勾选。
    /// </summary>
    private void LinksGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not LinkRow { IsRegistry: true } row) return;

        // 注册链路的名称/主机/端口由中心维护，但**本端要不要用某个协议**必须能改。
        // 协议集合以 ProbeClient.Protocols 为唯一来源：这里以前手写的是 "TCP" or "UDP"，
        // 加 ICMP 时漏了 → 注册链路上的 ICMP 勾选框点不动（现场："icmp 很少能勾上或取消"）。
        if (e.Column.Header is string header && ProbeClient.Protocols.Contains(header)) return;

        e.Cancel = true;
        UpdateStatus($"「{row.Name}」来自注册中心：名称/主机/端口不能本地修改；" +
                     "要本地停测某个协议，直接取消该协议的勾选即可（同步不会覆盖这个意图）");
    }

    private void SelectAndFocus(LinkRow row)
    {
        LinksGrid.SelectedItem = row;
        LinksGrid.ScrollIntoView(row);

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                LinksGrid.CurrentCell = new DataGridCellInfo(row, LinksGrid.Columns[0]);
                LinksGrid.Focus();
                LinksGrid.BeginEdit();
            }
            catch
            {
                // 容器尚未生成时忽略，用户点一下单元格即可编辑
            }
        }, DispatcherPriority.Background);
    }

    private void AddLink_Click(object sender, RoutedEventArgs e)
    {
        var template = LinksGrid.SelectedItem as LinkRow;

        var row = template is null
            ? new LinkRow { Name = $"机房{_linkRows.Count + 1}", Host = "", PortText = "9001" }
            : template.CopyAsManual(template.Name + "-副本");

        AddLinkRow(row);
        RevalidateLinks();
        SelectAndFocus(row);
        UpdateStatus("已新增一条链路，请填写名称与主机/IP（点“启动测试”后生效）");
        _configLog.Write($"新增链路：「{row.Name}」{row.Target}");
    }

    private void DuplicateLink_Click(object sender, RoutedEventArgs e)
    {
        var selected = LinksGrid.SelectedItems.Cast<LinkRow>().ToList();
        if (selected.Count == 0)
        {
            _alert("请先在列表里选中要复制的链路。", "复制链路");
            return;
        }

        LinkRow? last = null;
        foreach (var source in selected)
        {
            last = source.CopyAsManual(source.Name + "-副本");
            AddLinkRow(last);
        }

        RevalidateLinks();
        if (last is not null) SelectAndFocus(last);
        UpdateStatus($"已复制 {selected.Count} 条链路（点“启动测试”后生效）");
    }

    private void DeleteLink_Click(object sender, RoutedEventArgs e)
    {
        var selected = LinksGrid.SelectedItems.Cast<LinkRow>().ToList();
        if (selected.Count == 0)
        {
            _alert("请先在列表里选中要删除的链路。", "删除链路");
            return;
        }
        // 注册链路不能在这里删：本地删掉只会在下一个同步周期被中心加回来（假成功）
        var registryRows = selected.Where(r => r.IsRegistry).ToList();
        if (registryRows.Count > 0)
        {
            var what = registryRows.Count == 1 ? $"「{registryRows[0].Name}」" : $"{registryRows.Count} 条链路";
            _alert(
                what + "来自注册中心，不能在这里删除：\n\n" +
                "· 要让它在所有节点上消失：请在注册中心侧移除该节点（「注册中心」页 → 注册中心管理 → 移除所选）。\n" +
                "· 只想在本机停测：把该行的 TCP 与 UDP 都取消勾选（同步不会覆盖这个意图）。",
                "删除链路");
            return;
        }

        var what2 = selected.Count == 1 ? $"「{selected[0].Display}」" : $"选中的 {selected.Count} 条链路";
        var message = $"确定要删除{what2}吗？\n\n已统计到的数据和 logs 目录下的历史日志不会被删除。";

        if (_linkRows.Count - selected.Count == 0)
            message += "\n\n注意：这是最后一条链路。删除后程序只会监听端口并应答对方的探测，不再主动发包。";

        if (!_confirmDelete(message, "删除链路（防误删确认）")) return;

        _lastDeleted.Clear();
        foreach (var row in selected)
        {
            _lastDeleted.Add((row, _linkRows.IndexOf(row)));
            row.PropertyChanged -= OnLinkRowChanged;
            _linkRows.Remove(row);
        }
        _lastDeleted.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        _configLog.Write($"删除链路 {selected.Count} 条：" +
                         string.Join("；", selected.Select(r => $"「{r.Name}」{r.Target}({r.SourceText})")));

        UpdateUndoButton();
        RevalidateLinks();
        UpdateStatus($"已删除 {selected.Count} 条链路（可点“撤销删除”恢复；点“启动测试”后生效）");
    }

    private void UndoDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDeleted.Count == 0) return;

        foreach (var (row, index) in _lastDeleted)
        {
            row.PropertyChanged += OnLinkRowChanged;
            _linkRows.Insert(Math.Min(index, _linkRows.Count), row);
        }

        var count = _lastDeleted.Count;
        _lastDeleted.Clear();
        UpdateUndoButton();
        RevalidateLinks();
        UpdateStatus($"已恢复 {count} 条被删除的链路（点“启动测试”后生效）");
        _configLog.Write($"撤销删除：恢复 {count} 条链路");
    }

    /// <summary>
    /// 「核对配置文件」：把磁盘上的配置与界面上的行逐条对比并说明差异。
    ///
    /// 现场多次出现"appsettings.json 里明明有 N 条链路，界面里只有 M 条"，
    /// 而原因可能是程序读的不是这份文件、文件被外部改过、或同步/删除动过它——
    /// 光看界面分不清，所以让它自己说清楚。
    /// </summary>
    private void CheckConfig_Click(object sender, RoutedEventArgs e)
    {
        var file = ConfigManager.Load(out var error);
        var stamp = ConfigManager.Stamp();

        var diff = LinkConfigDiff.Compare(file.Links, _linkRows.Select(r => r.ToConfig()));
        var text = LinkConfigDiff.Describe(diff, ConfigManager.ConfigPath, stamp?.WriteUtc.ToLocalTime());

        if (error is not null) text = "（配置文件读取失败：" + error + "）\n\n" + text;

        MessageBox.Show(text, "核对配置文件", MessageBoxButton.OK,
            diff.Same ? MessageBoxImage.Information : MessageBoxImage.Warning);

        _configLog.Write($"核对配置文件：文件 {diff.FileCount} 条 / 界面 {diff.RowCount} 条，一致={diff.Same}" +
                         (diff.OnlyInFile.Count > 0 ? $"，只在文件里 {string.Join("；", diff.OnlyInFile)}" : "") +
                         (diff.OnlyInRows.Count > 0 ? $"，只在界面里 {string.Join("；", diff.OnlyInRows)}" : ""));
    }

    private bool ReadUiToConfig()
    {
        if (!int.TryParse(ListenPortBox.Text, out var listenPort) || listenPort < 1 || listenPort > 65535 ||
            !int.TryParse(IntervalBox.Text, out var interval) || interval < 50 ||
            !int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 1 ||
            !int.TryParse(WarningBox.Text, out var warning) || warning < 0 ||
            !int.TryParse(WindowBox.Text, out var window) || window < 1 ||
            !int.TryParse(LossCountBox.Text, out var lossCount) || lossCount < 1 ||
            !TryParseDouble(LossWarnBox.Text, out var lossWarn) || lossWarn < 0)
        {
            MessageBox.Show(
                "配置参数无效，请检查：监听端口(1~65535)、测试间隔(≥50ms)、超时(≥1ms)、" +
                "统计窗口(≥1分钟)、异常RTT阈值(≥0)、丢包率告警(≥0)、连续丢包告警(≥1)。",
                "配置错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _settings.ListenPort = listenPort;
        _settings.IntervalMs = interval;
        _settings.TimeoutMs = timeout;
        _settings.WarningRttMs = warning;
        _settings.StatWindowMinutes = window;
        _settings.LossWarnPercent = lossWarn;
        _settings.ConsecutiveLossWarn = lossCount;
        _settings.EnableTcp = TcpCheck.IsChecked == true;
        _settings.EnableUdp = UdpCheck.IsChecked == true;
        _settings.EnableIcmp = IcmpCheck.IsChecked == true;

        // 注册配置（未启用注册时不做校验，与 ValidateRegistrar 口径一致）
        if (!RegistryTabPanel.TryReadInto(_settings.Registrar, out var registrarError))
        {
            MainTabs.SelectedItem = RegistryTab;
            MessageBox.Show("注册中心配置无效，请修正「注册中心」页：\n\n" + registrarError,
                "配置错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // 链路以编辑器里的行为准，校验不通过就拒绝保存/启动
        if (!EnsureLinksValid("保存或启动"))
            return false;

        _settings.Links = _linkRows.Select(r => r.ToConfig()).ToList();

        if (!ConfigManager.Validate(_settings, out var detail))
        {
            MessageBox.Show("配置参数无效：\n\n" + detail,
                "配置错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private static bool TryParseDouble(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
           || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private bool TrySaveConfig()
    {
        try
        {
            ConfigManager.Save(_settings);
            _configStamp = ConfigManager.Stamp();
            _configLoadError = null;
            _savedLinksSignature = LinksSignature();
            UpdateLinkSummary();

            _configLog.Write($"保存配置成功 → {ConfigManager.ConfigPath}（{_settings.Links.Count} 条链路）");
            foreach (var link in _settings.Links)
            {
                _configLog.Write($"  {DescribeLink(link)}");
            }

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存配置失败：" + ex.Message + "\n\n文件位置：" + ConfigManager.ConfigPath,
                "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// 检测配置文件是否被外部程序修改。返回 false 表示用户选择放弃本次操作。
    /// </summary>
    private bool ResolveExternalConfigChange()
    {
        if (_configLoadError is not null) return true; // 读取失败时本就不允许自动保存

        var current = ConfigManager.Stamp();
        if (current is null || _configStamp is null || current.Value == _configStamp.Value) return true;

        var answer = MessageBox.Show(
            "配置文件已被其它程序或另一个实例修改：\n" + ConfigManager.ConfigPath + "\n\n" +
            "是：重新加载文件（放弃界面上的修改）\n" +
            "否：用界面当前值覆盖文件\n" +
            "取消：放弃本次操作（推荐先点「打开配置文件」看看是谁改的）",
            "配置已变更", MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
            // 默认按钮给"取消"：以前默认是"是=放弃界面修改"，一个回车就把用户刚改的东西丢了
            MessageBoxResult.Cancel);

        switch (answer)
        {
            case MessageBoxResult.Yes:
                LoadConfigFromDisk(showError: true);
                return _configLoadError is null;
            case MessageBoxResult.No:
                return true;
            default:
                return false;
        }
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadUiToConfig()) return;
        if (!ResolveExternalConfigChange()) return;
        if (!TrySaveConfig()) return;

        // 注册配置变了 → 立刻重建同步客户端，否则中心/其他节点看到的还是旧值
        var restarted = RestartRegistryClientIfChanged();

        UpdateStatus($"配置已保存 → {ConfigManager.ConfigPath}" +
                     (restarted ? "；检测到注册配置变化，已重新注册（中心会立刻看到新值）" : ""));
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(ConfigManager.ConfigPath)) ConfigManager.Save(_settings);
            Process.Start(new ProcessStartInfo
            {
                FileName = ConfigManager.ConfigPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开配置文件失败：" + ex.Message, "打开失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 「重新加载配置」：把磁盘上的内容读回界面（丢弃界面上未保存的修改，先确认）。
    /// 用途：确认过别人/另一个实例改过这份配置，想看看它现在是什么。
    /// </summary>
    private void ReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(LinksSignature(), _savedLinksSignature, StringComparison.Ordinal))
        {
            var answer = MessageBox.Show(
                "界面上有未保存的修改，重新加载会丢弃它们。\n\n确定要重新加载吗？\n\n" +
                ConfigManager.ConfigPath,
                "重新加载配置", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;
        }

        LoadConfigFromDisk(showError: true);

        if (_configLoadError is null)
            UpdateStatus("已重新加载配置（丢弃了界面未保存的修改）：" + ConfigManager.ConfigPath);
    }

    // ---------------- 运行控制 ----------------

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadUiToConfig()) return;
        if (!ResolveExternalConfigChange()) return;

        // 配置文件本身有错时不做隐式覆盖，仅按界面值运行
        if (_configLoadError is null && !TrySaveConfig()) return;

        StopInternal();

        try
        {
            var notes = new List<string>();

            var server = new ProbeServer(_settings.ListenPort);
            server.Start();
            _server = server;

            // 注册协议与探测共用同一个监听端口：按 LT1| 前缀分流。
            // 中心 → 处理注册消息；非中心 → 明确回 ERR unsupported（比静默回显更容易现场定位）。
            if (_settings.Registrar.ServeAsCenter)
            {
                var store = EnsureRegistryStore();
                _registryCenter = new RegistryCenterService(
                    store, _settings.Registrar.Secret, _settings.Registrar.HeartbeatSeconds, null, _centerLog);
                server.ProtocolHandler = _registryCenter.HandleAsync;

                _centerLog.Section($"中心启动：清单 {store.Nodes.Count} 个节点，密钥 {AppLog.Redact(_settings.Registrar.Secret)}");
                foreach (var node in store.Nodes)
                {
                    _centerLog.Write($"  已有节点 {node.Name} [{node.NodeId[..Math.Min(8, node.NodeId.Length)]}] " +
                                     $"地址=[{string.Join(",", node.Ips.Count > 0 ? node.Ips : new List<string> { node.Ip })}] " +
                                     $"port={node.Port} tcp={node.Tcp} udp={node.Udp} icmp={node.Icmp} online={node.Online} lastSeen={node.LastSeenUtc:u}");
                }

                notes.Add($"本机同时是注册中心：清单 {store.Nodes.Count} 个节点");
            }
            else
            {
                _registryCenter = null;
                var secret = _settings.Registrar.Secret;
                var unsupported = RegistryCenterService.RespondUnsupportedAsync;
                server.ProtocolHandler = (request, length, _, _) =>
                    unsupported(secret, request, length, CancellationToken.None);
            }

            _session.Configure(_settings);
            ClearSession();

            // 记录本次真正生效的链路，便于提示"运行中的测试仍在使用旧链路"
            _liveLinks = _settings.Links.ToArray();
            _appliedLinksSignature = LinksSignature();
            UpdateLinkSummary();

            StartRegistryClient(notes);
            StartIspLookup();
            RequestIspForLinks(_liveLinks);

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loop = Task.Run(() => TestLoop(cts.Token));

            if (!server.DualStack) notes.Add("本机不支持 IPv6，监听为仅 IPv4");
            if (ActiveProbeCount() == 0) notes.Add("当前没有生效的探测（未勾选协议，或链路未启用该协议），只会监听应答");

            UpdateStatus(notes.Count == 0 ? null : string.Join("；", notes));
            RefreshRegistryUi();
        }
        catch (Exception ex)
        {
            StopInternal();
            MessageBox.Show("启动失败：" + ex.Message, "启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateStatus();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopInternal();
        UpdateStatus("已停止（界面保留已统计到的数据，可点“清除”重新开始统计）");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ClearSession();
        UpdateStatus($"已清除统计（{DateTime.Now:HH:mm:ss}），从当前时刻重新开始统计；历史日志未删除");
    }

    /// <summary>
    /// 清空界面统计并开启新会话。运行中可反复调用：
    /// 会话号自增后，清除之前发起、之后才返回的探测结果会被丢弃。
    /// </summary>
    private void ClearSession()
    {
        _session.Clear();

        // 让下一轮探测使用新的会话号；本轮之前发起的结果仍带旧会话号，会被丢弃
        Volatile.Write(ref _loopEpoch, _session.Epoch);

        _statRows.Clear();
        _stats.Clear();
        _anomalies.Clear();
        _trend.Clear();

        // 曲线跟着会话走：采样清零，但"勾了哪几条"保留（清除后通常还要看同样几条）
        _chartHistory.Clear();
        if (MainTabs.SelectedItem == ChartTab) ChartPanel.Update(_chartHistory.SelectedSeries, _settings.WarningRttMs, _chartHistory.RecordedCount);

        while (_pending.TryDequeue(out _)) { }
    }

    private void StopInternal()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;

        cts?.Cancel();

        StopRegistryClient();

        // 注意：**不**停运营商查询。停止测试后统计表还在，行里的运营商信息必须继续显示——
        // "停止测试"只是不再发包，不是把已经查到的资料藏起来（曾因为在这里销毁服务，
        // 停止后整个运营商列变成"未启用"）。
        _server?.Dispose();
        _server = null;

        // 中心服务与清单实例随本次运行结束（清单已落盘，界面下一次刷新会重新读文件）。
        // 留着旧实例会让"停止后再改参数启动"出现两个写入同一文件的实例。
        _registryCenter = null;
        _registryStore = null;
        _liveLinks = Array.Empty<LinkConfig>();

        if (cts is not null)
        {
            // 等探测循环真正退出后再释放 CTS，避免底层套接字仍持有该令牌时被 Dispose
            _ = Task.Run(async () =>
            {
                if (loop is not null)
                {
                    try { await loop.ConfigureAwait(false); } catch { }
                }
                cts.Dispose();
            });
        }
    }

    // ---------------- 注册中心（切片 3） ----------------

    /// <summary>中心清单文件：与配置文件同目录（自检里 ConfigManager.OverridePath 指到临时目录时也一起走）。</summary>
    private static string RegistryStatePath
        => Path.Combine(Path.GetDirectoryName(ConfigManager.ConfigPath) ?? AppContext.BaseDirectory, "registry.json");

    /// <summary>注册协议自身的日志目录（按天分目录，与探测日志同级但文件名不同）。</summary>
    private static string RegistryLogDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

    private RegistryStore EnsureRegistryStore()
    {
        _registryStore ??= new RegistryStore(RegistryStatePath, _settings.Registrar.HeartbeatSeconds);
        return _registryStore;
    }

    private void StartRegistryClient(List<string> notes)
    {
        // 未启用注册：一个字节都不发，状态栏也不加任何提示（与升级前完全一致，规格 §11.3）
        if (!_settings.Registrar.Enabled) return;
        var options = new RegistryClientOptions
        {
            NodeId = _settings.Registrar.NodeId,
            NodeName = _settings.Registrar.NodeName,
            PublicHost = _settings.Registrar.PublicHost,
            PublicPort = _settings.Registrar.PublicPort,
            Ips = new[] { _settings.Registrar.PublicHost }
                .Concat(_settings.Registrar.PublicHosts ?? new List<string>())
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Tcp = _settings.Registrar.AdvertiseTcp,
            Udp = _settings.Registrar.AdvertiseUdp,
            Icmp = _settings.Registrar.AdvertiseIcmp,
            Distributable = _settings.Registrar.Distributable,
            CenterHost = _settings.Registrar.CenterHost,
            CenterPort = _settings.Registrar.CenterPort,
            Secret = _settings.Registrar.Secret,
            HeartbeatSeconds = _settings.Registrar.HeartbeatSeconds,
            SyncSeconds = _settings.Registrar.SyncSeconds,
            RetryMs = _settings.Registrar.RetryMs,
            MaxRetries = _settings.Registrar.MaxRetries,
            LogDirectory = RegistryLogDirectory
        };

        var client = new RegistryClient(options);
        client.SnapshotReceived += OnRegistrySnapshot;

        if (!client.Start())
        {
            notes.Add("注册同步未启动：" + (client.Status.LastError ?? "未知原因"));
            client.SnapshotReceived -= OnRegistrySnapshot;
            client.Dispose();
            return;
        }

        _registryClient = client;
        _registryClientSignature = RegistryClientSignature();
    }

    /// <summary>
    /// 注册客户端用到的全部参数。任何一项变了，都必须**重建客户端**——
    /// 客户端在 Start 时把参数抓成 Options，改配置不重建的话，中心收到的永远是旧值
    /// （现场就是"改了配置，注册中心那边还是老链路信息"）。
    /// </summary>
    private string RegistryClientSignature()
    {
        var r = _settings.Registrar;
        return string.Join("|",
            r.NodeId, r.NodeName, r.PublicHost, r.PublicPort.ToString(),
            string.Join(",", r.PublicHosts ?? new List<string>()),
            r.AdvertiseTcp.ToString(), r.AdvertiseUdp.ToString(), r.AdvertiseIcmp.ToString(), r.Distributable.ToString(),
            r.CenterHost, r.CenterPort.ToString(), r.Secret,
            r.HeartbeatSeconds.ToString(), r.SyncSeconds.ToString(),
            r.RetryMs.ToString(), r.MaxRetries.ToString());
    }

    /// <summary>
    /// 注册配置改了（保存配置 / 立即注册时调用）：重建同步客户端并立刻注册一次。
    /// 返回是否真的重建了。
    /// </summary>
    private bool RestartRegistryClientIfChanged()
    {
        if (!_settings.Registrar.Enabled) return false;
        if (_registryClient is null) return false;
        if (_registryClientSignature == RegistryClientSignature()) return false;

        var notes = new List<string>();
        StopRegistryClient();
        StartRegistryClient(notes);

        return _registryClient is not null;
    }

    private void StopRegistryClient()
    {
        var client = _registryClient;
        _registryClient = null;
        if (client is null) return;

        client.SnapshotReceived -= OnRegistrySnapshot;
        try { client.Dispose(); } catch { }
    }

    /// <summary>客户端回调来自后台同步线程，一律切回 UI 线程再动界面与配置。</summary>
    private void OnRegistrySnapshot(IReadOnlyList<NodeSnapshot> nodes)
    {
        try
        {
            Dispatcher.InvokeAsync(() => ApplyRegistrySnapshot(nodes));
        }
        catch (Exception)
        {
            // 窗口已关闭 / Dispatcher 已停：忽略即可，配置仍会由中心在下次同步时重发
        }
    }

    /// <summary>
    /// 应用中心快照：注册链路增/改/删，手工链路原样保留（规格 §8.4）。
    /// 新链路**无需重启测试**即可开始探测（`_liveLinks` 立即替换）。
    /// </summary>
    private void ApplyRegistrySnapshot(IReadOnlyList<NodeSnapshot> nodes)
    {
        MergeLocalIntent();

        var outcome = RegistrySync.Apply(nodes, _settings.Registrar.NodeId, _settings.Links);
        if (!outcome.HasChanges)
        {
            RefreshRegistryUi();
            return;
        }

        _settings.Links = outcome.Links;

        SyncRegistryRows(outcome.Links);

        _configLog.Write($"应用中心快照：新增 {outcome.Added}、更新 {outcome.Updated}、删除 {outcome.Removed}、" +
                         $"跳过不可用地址 {outcome.SkippedUnusable}（中心 rev={_registryClient?.Status.Revision}）");
        foreach (var link in outcome.Links.Where(l => l.Source == LinkSources.Registry))
        {
            _configLog.Write($"  {DescribeLink(link)}");
        }

        // 交给防抖统一重建生效链路：它从"编辑器里的行"取数，
        // 因此既包含刚同步下来的注册链路，也不会丢掉用户尚未保存的手工改动。
        _linksDirtyUtc = DateTime.UtcNow;

        var persisted = TryPersistConfigQuietly(out var saveError);

        UpdateStatus(
            $"注册中心清单已更新：新增 {outcome.Added}、更新 {outcome.Updated}、删除 {outcome.Removed}" +
            (outcome.SkippedUnusable > 0 ? $"、跳过 {outcome.SkippedUnusable} 个不可用地址" : "") +
            (persisted ? "（已保存配置，新链路立刻开始探测）" : $"；但配置未能保存：{saveError}"));

        RevalidateLinks();
        RefreshRegistryUi();
    }

    /// <summary>
    /// 把编辑器里"未保存"的本地停测意图并入配置，再交给 <see cref="RegistrySync"/>。
    /// 不做这一步：本地刚取消勾选的链路会被这次同步按中心值改回勾选状态（用户的意图被吃掉）。
    /// </summary>
    private void MergeLocalIntent()
    {
        // 键必须是「节点 + 地址」：一个机房多个 IP 时同一个 NodeId 会对应多条链路，
        // 只用 NodeId 建字典会直接抛"已添加相同键的项"（实测踩过），清单就应用不上了。
        var rows = new Dictionary<string, LinkRow>(StringComparer.Ordinal);

        foreach (var row in _linkRows.Where(r => r.IsRegistry && !string.IsNullOrEmpty(r.NodeId)))
            rows[LinkSourceKey(row)] = row;

        foreach (var link in _settings.Links.Where(l => l.Source == LinkSources.Registry))
        {
            if (rows.TryGetValue(LinkSourceKey(link), out var row)) link.LocalDisabled = row.LocalDisabled;
        }
    }

    /// <summary>只同步注册来源的行：手工行保持原样，不打断用户正在编辑的内容。</summary>
    private void SyncRegistryRows(List<LinkConfig> links)
    {
        var registryLinks = links.Where(l => l.Source == LinkSources.Registry).ToList();
        var live = registryLinks.Select(l => LinkSourceKey(l)).ToHashSet(StringComparer.Ordinal);

        // 记下这次被移除的行：现场出现"文件里有、界面里没有"时，
        // 直接说明是"中心快照里已经没有这些地址了"，而不是让人猜
        var removed = new List<string>();

        foreach (var row in _linkRows.Where(r => r.IsRegistry && !live.Contains(LinkSourceKey(r))).ToList())
        {
            removed.Add($"{row.Name}（{row.Target}）");
            row.PropertyChanged -= OnLinkRowChanged;
            _linkRows.Remove(row);
        }

        if (removed.Count > 0)
        {
            _configLog.Write($"按中心快照移除注册链路 {removed.Count} 条：{string.Join("；", removed)}");
            UpdateStatus($"中心快照里已不含这些地址，已从界面移除：{string.Join("、", removed.Take(4))}" +
                         (removed.Count > 4 ? $" 等 {removed.Count} 条" : "") +
                         "（对端撤掉了地址、或该机器换回了旧版本注册）");
        }

        foreach (var link in registryLinks)
        {
            var row = _linkRows.FirstOrDefault(r => r.IsRegistry && LinkSourceKey(r) == LinkSourceKey(link));
            if (row is null) AddLinkRow(LinkRow.FromConfig(link));
            else row.ApplyFrom(link);
        }

        // 撤销缓冲里可能留着已被中心删除的注册行：留着会让"撤销删除"把它复活
        _lastDeleted.RemoveAll(x => x.Row.IsRegistry && !live.Contains(LinkSourceKey(x.Row)));
        UpdateUndoButton();

        ApplyGlobalProtocolsToRows();
    }

    /// <summary>注册链路的身份：来源节点 + 该链路指向的地址（一个机房多 IP 时靠它区分）。</summary>
    private static string LinkSourceKey(LinkConfig link) => RegistrySync.Key(link.NodeId, link.Host);

    /// <summary>同上，供界面行使用（行与配置必须用同一套身份，否则会互相错配）。</summary>
    private static string LinkSourceKey(LinkRow row) => RegistrySync.Key(row.NodeId, row.Host);

    /// <summary>
    /// 注册同步触发的自动落盘：不弹模态框。
    /// 配置文件读取失败、或被其它程序改过时放弃自动写（避免覆盖别人的修改），交给用户手动保存。
    /// </summary>
    private bool TryPersistConfigQuietly(out string error)
    {
        error = "";

        if (_configLoadError is not null)
        {
            error = "配置文件读取失败，未自动保存";
            return false;
        }

        var current = ConfigManager.Stamp();
        if (current is not null && _configStamp is not null && current.Value != _configStamp.Value)
        {
            error = "配置文件已被其它程序修改，未自动覆盖";
            return false;
        }

        try
        {
            ConfigManager.Save(_settings);
            _configStamp = ConfigManager.Stamp();
            _savedLinksSignature = LinksSignatureOf(_settings.Links);

            _configLog.Write($"注册同步自动落盘成功（{_settings.Links.Count} 条链路）→ {ConfigManager.ConfigPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _configLog.Write($"注册同步自动落盘失败：{ex.Message}");
            return false;
        }
    }

    private void RefreshRegistryUi()
    {
        var now = DateTime.UtcNow;
        RegistryTabPanel.ShowStatus(_registryClient?.Status, _settings.Registrar, now);

        var store = _settings.Registrar.ServeAsCenter ? EnsureRegistryStore() : null;
        RegistryTabPanel.ShowCenter(store, now);
    }

    private void OnRegistryRegisterRequested() => _ = RegisterNowAndRefreshAsync();

    private async Task RegisterNowAndRefreshAsync()
    {
        // 界面上刚改过注册参数（例如「可被探测的协议」）而客户端还拿着旧 Options 时，
        // 先重建客户端再发，"立即注册"才会把新值送到中心
        RestartRegistryClientIfChanged();

        var client = _registryClient;
        if (client is null)
        {
            MessageBox.Show(
                "注册同步未运行：请先点“启动测试”，并确认已在「注册中心」页勾选“参与注册同步”、填好中心地址。",
                "立即注册", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await client.RegisterNowAsync();
        RefreshRegistryUi();
        UpdateStatus($"已向注册中心发起一次注册/同步（{DateTime.Now:HH:mm:ss}）");
    }

    private void OnRegistryCenterChanged()
    {
        RefreshRegistryUi();
        UpdateStatus("注册中心清单已改动：其他节点会在各自的同步周期内更新链路（默认 300 秒）");
    }

    // ---------------- 运营商查询（跨运营商判定） ----------------

    /// <summary>运营商缓存文件：与配置文件同目录。</summary>
    private static string IspCachePath
        => Path.Combine(Path.GetDirectoryName(ConfigManager.ConfigPath) ?? AppContext.BaseDirectory, "ip-isp-cache.json");

    /// <summary>
    /// 按配置启停运营商查询。关掉后**一个字节都不会往外发**（现场可能有内网隔离/合规要求）。
    /// </summary>
    private void ApplyIspSetting(bool enabled)
    {
        _settings.Isp.Enabled = enabled;

        if (!enabled)
        {
            StopIspLookup();
            foreach (var row in _statRows.Values) row.SetCarrier(null, null, enabled: false);

            if (_cts is not null) UpdateStatus("已关闭运营商信息查询（不再向 ip-api 发送任何地址）");
            return;
        }

        StartIspLookup();

        // 只在测试运行中提示，避免开窗时就刷一条与当前动作无关的状态
        if (_cts is not null)
            UpdateStatus("已启用运营商信息查询：只查公网地址、结果缓存 " + _settings.Isp.CacheDays + " 天");
    }

    private void StartIspLookup()
    {
        if (_isp is not null || !_settings.Isp.Enabled) return;

        var ttl = TimeSpan.FromDays(Math.Clamp(_settings.Isp.CacheDays, IspConfig.MinCacheDays, IspConfig.MaxCacheDays));
        _isp = new IspLookupService(_settings.Isp, new IspCacheStore(IspCachePath, ttl));
    }

    private void StopIspLookup()
    {
        var service = _isp;
        _isp = null;
        if (service is null) return;

        try { service.Dispose(); } catch { }
    }

    private void IspCheck_Changed(object sender, RoutedEventArgs e)
        => ApplyIspSetting(IspCheck.IsChecked == true);

    /// <summary>
    /// 「重查」：清掉本地运营商缓存并立刻重新查询。
    ///
    /// 现场必需——曾经因为解析 bug 把"无数据"写进 30 天缓存，修好程序后界面照旧显示未知，
    /// 而运维无从下手（既不知道该删哪个文件，也不知道要等 30 天）。现在一键重来。
    /// </summary>
    private void IspRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (IspCheck.IsChecked != true)
        {
            UpdateStatus("运营商查询未启用：先勾选「显示运营商信息并判断是否跨运营商」");
            return;
        }

        StartIspLookup();
        var removed = _isp?.ClearCache() ?? 0;

        _linkEgress.Clear();
        foreach (var row in _statRows.Values) row.SetCarrier(null, null, enabled: true);

        UpdateStatus($"已清除运营商缓存（{removed} 条），正在重新查询…");
        RefreshStatusBar();
    }

    /// <summary>
    /// 给一行统计补上"本端 / 目标运营商"与跨网结论。
    ///
    /// 目标：优先用探测实际看到的地址；没有就用链路配置里的主机名（由查询服务解析，结果带缓存）。
    /// 本端：用报文回显的"对端看到的本端源地址"——那是这条链路真实的出口 IP，
    /// 不需要再问第三方"我的 IP 是多少"。
    /// </summary>
    private void ApplyCarrier(StatRow row)
    {
        // 开关才是"用户意图"；服务实例只是手段。服务不在（例如刚启动/刚停过）就地补起来，
        // 否则统计表里已经查到的运营商会因为"服务对象没了"被改写成"未启用"。
        var wanted = IspCheck.IsChecked == true;

        if (!wanted)
        {
            row.SetCarrier(null, null, enabled: false);
            return;
        }

        StartIspLookup();

        var enabled = _isp is not null;

        if (!enabled)
        {
            row.SetCarrier(null, null, enabled: false);
            return;
        }

        var remote = IspAddressRules.HostPart(row.Target);

        // 本端出口 IP 来自对端回显：某个协议不通时（例如机房只映射了 UDP），
        // 这一行拿不到 → 复用**同链路其它协议**已学到的出口 IP（同一台机器的出口通常相同），
        // 否则 TCP 行会永远显示"未知"，而 UDP 行明明已经查到了。
        var local = IspAddressRules.HostPart(row.SeenFrom);
        var fromFallback = false;
        var fallbackExit = "";

        if (local.Length == 0 && row.LinkName.Length > 0 &&
            _linkEgress.TryGetValue(row.LinkName, out var known))
        {
            local = IspAddressRules.HostPart(known);
            fromFallback = local.Length > 0;
            fallbackExit = known;
        }

        if (remote.Length > 0) _isp!.Request(remote);
        if (local.Length > 0 && IspAddressRules.IsPublicIp(local)) _isp!.Request(local);

        // 记下这条链路学到的出口地址（带端口），供同链路其它协议复用
        if (!fromFallback && local.Length > 0 && row.LinkName.Length > 0) _linkEgress[row.LinkName] = row.SeenFrom;

        IspRecord? localRecord;
        if (IspAddressRules.IsPublicIp(local))
        {
            localRecord = _isp!.TryGet(local);
        }
        else
        {
            // 拿不到本端出口 IP：说清原因，别显示成"查询中…"（那会让人以为在等网络）
            localRecord = new IspRecord
            {
                Ip = local,
                Status = "fail",
                Message = local.Length == 0 ? "尚无对端回显（探测还没成功过）" : "private range"
            };
        }

        row.SetCarrier(localRecord, _isp!.TryGet(remote), enabled: true, PendingText(),
            fromFallback ? fallbackExit : null);

        if (fromFallback)
            row.CarrierDetail += $"\n（本端出口取自同链路其它协议的探测结果：{fallbackExit}）";
    }

    /// <summary>
    /// "还没查到"时该显示什么：区分"正在查 / 查不通 / 被限流"，否则网络不通时
    /// 那一列会一直写着"查询中…"，用户根本看不出是卡住了还是查不到。
    /// </summary>
    private string PendingText()
    {
        var status = _isp?.Status;
        if (status is null) return "查询中…";
        if (status.Throttled) return "被限流…";
        if (status.LastError is not null) return "查询失败";

        return "查询中…";
    }

    // ---------------- 探测循环 ----------------

    private async Task TestLoop(CancellationToken ct)
    {
        var interval = _settings.IntervalMs;
        var timeout = _settings.TimeoutMs;

        while (!ct.IsCancellationRequested)
        {
            var roundStart = Stopwatch.StartNew();
            var epoch = Volatile.Read(ref _loopEpoch);

            // 每轮重新读取链路快照：注册中心同步到新链路后立刻开始探测，不必重启测试
            var links = _liveLinks;

            // 每轮重新读取协议勾选，取消勾选后本轮即停止发送该协议的探测包
            var sendTcp = _liveTcp;
            var sendUdp = _liveUdp;
            var sendIcmp = _liveIcmp;

            var tasks = new List<Task<ProbeResult?>>(links.Length * 3);
            foreach (var link in links)
            {
                if (sendTcp && link.Tcp) tasks.Add(_client.ProbeAsync(link, ProbeClient.ProtocolTcp, timeout, ct));
                if (sendUdp && link.Udp) tasks.Add(_client.ProbeAsync(link, ProbeClient.ProtocolUdp, timeout, ct));
                if (sendIcmp && link.Icmp) tasks.Add(_client.ProbeAsync(link, ProbeClient.ProtocolIcmp, timeout, ct));
            }

            if (tasks.Count > 0)
            {
                ProbeResult?[] results;
                try
                {
                    results = await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    results = Array.Empty<ProbeResult?>();
                }

                if (ct.IsCancellationRequested) break;

                var accepted = new List<ProbeResult>(results.Length);
                foreach (var result in results)
                {
                    if (result is null) continue; // 用户取消，不是丢包
                    result.Epoch = epoch;
                    accepted.Add(result);
                }

                if (accepted.Count > 0) DispatchResults(accepted);
            }

            // 按“轮次起点”计时，扣除探测本身的耗时，避免实际间隔越跑越长
            var wait = interval - (int)roundStart.ElapsedMilliseconds;
            if (wait <= 0) continue;

            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void DispatchResults(List<ProbeResult> results)
    {
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var result in results)
            {
                // 清除/重启之前发起、之后才返回的结果：不计入统计，也不写日志
                if (!_session.Accept(result.Epoch)) continue;

                _session.Add(result);
                _log.Enqueue(result);
                _pending.Enqueue(result);
            }
        });
    }

    // ---------------- 界面刷新（节流） ----------------

    private void OnUiTick(object? sender, EventArgs e)
    {
        DrainPendingResults();
        ApplyLiveLinksIfDue();
        RefreshStats();
        RefreshChart();
        UpdateNodeNameDisplay();
        RefreshRegistryUi();
        WarnOnExternalConfigChange();
        RefreshStatusBar();
    }

    // ---------------- RTT 曲线 ----------------

    /// <summary>
    /// 刷新曲线页。不在这一页时直接跳过——长会话下重画几万个点是有成本的，
    /// 没必要在用户看着别的页时每 500 毫秒白跑一次（切回来时会立刻补上）。
    ///
    /// 数据来源分两条：
    /// <list type="bullet">
    /// <item>「本会话（实时）」→ 内存里那份会话历史（含还没落盘的采样）；</item>
    /// <item>其它范围（默认「最近 24 小时」/今天/昨天/指定日期）→ logs 里的 CSV 日志，
    /// 增量读取，所以"重启测试之后照样能看到上一个会话的曲线"。</item>
    /// </list>
    /// </summary>
    private void RefreshChart()
    {
        if (MainTabs.SelectedItem != ChartTab) return;

        if (ChartPanel.Range == ChartRange.Session)
        {
            ChartPanel.Update(_chartHistory.SelectedSeries, _settings.WarningRttMs, _chartHistory.RecordedCount);
            return;
        }

        LoadChartHistory(force: false);
    }

    /// <summary>用户切换了时间范围/日期/勾选 → 立刻重新取数。</summary>
    private void OnChartRangeChanged()
    {
        _chartHistorySignature = "";
        if (MainTabs.SelectedItem == ChartTab) LoadChartHistory(force: true);
    }

    /// <summary>
    /// 从 logs 里读一段时间的历史曲线。
    /// 只读**已勾选**的那几条（没勾选的不读盘、也不用画）；
    /// 增量读取（尾部续读），所以包含"现在"的范围每 500 毫秒调一次也很便宜。
    /// </summary>
    private void LoadChartHistory(bool force)
    {
        var selected = _chartHistory.SelectedSeries;
        var signature = ChartPanel.Range + "|" + ChartPanel.SelectedDay.ToString("yyyy-MM-dd") + "|" +
                        string.Join(",", selected.Select(s => s.Key));

        // 静态历史范围（昨天/指定日期）：内容不会再变，读一次就够
        if (!force && !ChartPanel.RangeIncludesNow && signature == _chartHistorySignature) return;

        var (from, to) = ChartPanel.RangeWindow();

        if (selected.Count == 0)
        {
            _chartLoadedSamples = 0;
            _chartLoadNote = "先在「链路实时统计」页勾选链路（最左侧的「曲线」列）";
            _chartHistorySignature = signature;
            ChartPanel.Update(Array.Empty<ChartSeries>(), _settings.WarningRttMs, _chartHistory.RecordedCount,
                _chartLoadedSamples, _chartLoadNote);
            return;
        }

        Dictionary<string, List<ChartSample>> loaded;
        try
        {
            loaded = _chartLog.Load(selected.Select(s => s.Key), from, to);
        }
        catch (Exception ex)
        {
            _chartLoadNote = "读取日志失败：" + ex.Message;
            return;
        }

        // 历史视图用**独立的**曲线对象，与内存里正在记的那份完全分开
        var series = new List<ChartSeries>(selected.Count);
        var total = 0;

        foreach (var live in selected)
        {
            var historySeries = _chartHistoryView.Select(live.Key, live.LinkName, live.Protocol, live.Target);
            var samples = loaded.TryGetValue(live.Key, out var list) ? list : new List<ChartSample>();

            // 只有条数变了才整表拷贝（SetSamples 是 O(n)）
            if (historySeries.Count != samples.Count) historySeries.SetSamples(samples);

            series.Add(historySeries);
            total += samples.Count;
        }

        _chartLoadNote = total == 0
            ? $"logs 里没有 {from:MM-dd HH:mm} ~ {to:MM-dd HH:mm} 的记录（日志目录：{_chartLog.RootDirectory}）"
            : ChartPanel.RangeIncludesNow ? "随实时数据自动追加" : "静态历史范围";

        _chartLoadedSamples = total;
        _chartHistorySignature = signature;

        ChartPanel.Update(series, _settings.WarningRttMs, _chartHistory.RecordedCount, _chartLoadedSamples, _chartLoadNote);
    }

    /// <summary>
    /// 统计表最左侧的「曲线」勾选框变化 → 决定画哪几条曲线。
    /// 取消勾选只停止显示，**数据继续记、历史照留**：误点一下不该丢掉整段曲线。
    /// </summary>
    private void OnStatRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StatRow.Plot) || sender is not StatRow row) return;

        if (row.Plot)
        {
            _chartSelection.Add(row.Key);
            _chartHistory.Select(row.Key, row.LinkName, row.Protocol, row.Target);
        }
        else
        {
            _chartSelection.Remove(row.Key);
            _chartHistory.Deselect(row.Key);
        }

        // 勾选后立刻出图，不必等下一个 500 毫秒 tick
        OnChartRangeChanged();
    }

    /// <summary>
    /// 配置文件在运行期间被别的程序/另一个实例改了 → 状态栏明确提示。
    ///
    /// 不说的话用户会以为"我保存过了"，而界面上显示的其实是旧内容，下一次保存还会把对方的修改覆盖掉。
    /// </summary>
    private void WarnOnExternalConfigChange()
    {
        if (_configLoadError is not null) return;

        var current = ConfigManager.Stamp();
        if (current is null || _configStamp is null || current.Value == _configStamp.Value) return;

        _statusNote = "⚠ 配置文件已被其它程序或另一个实例修改（界面显示的是旧内容）：点「重新加载配置」读取新内容，" +
                      "或直接「保存配置」用界面当前值覆盖。  " + ConfigManager.ConfigPath;
        _statusIsError = false;
    }

    /// <summary>
    /// 链路编辑器改动后约 1 秒生效（防抖）。
    ///
    /// 为什么必须即时生效：现场把某条链路的 TCP 取消勾选后，界面写着"不发包"、TCP 探测包却照发，
    /// 统计跟着失真——这是被现场反馈抓到的真实问题（根因是循环只读了启动时的链路快照）。
    /// 为什么要防抖：端口是一格一格敲进去的（9 → 90 → 900 → 9001），逐字符立即生效会真的往
    /// 9 端口发探测包；等敲完再切，既不迟到也不打垃圾包。
    /// </summary>
    private void ApplyLiveLinksIfDue()
    {
        var dirty = _linksDirtyUtc;
        if (dirty is null) return;
        if (DateTime.UtcNow - dirty.Value < TimeSpan.FromSeconds(1)) return;

        ApplyLiveLinks();
    }

    /// <summary>把编辑器里的链路交给正在运行的探测循环（克隆一份，避免与配置对象互相影响）。</summary>
    private void ApplyLiveLinks()
    {
        _linksDirtyUtc = null;

        var links = _linkRows.Where(r => r.IsValid).Select(r => Clone(r.ToConfig())).ToList();

        // 未运行时也照样维护：定时器/同步随时可能触发，保持"生效链路 = 编辑器内容"这一条不变式，
        // 运行中的循环每轮读的就是它。
        _liveLinks = links.ToArray();
        _appliedLinksSignature = LinksSignatureOf(links);
        UpdateLinkSummary();
        RequestIspForLinks(_liveLinks);
    }

    /// <summary>
    /// 立刻为**配置里的**每个链路目标登记运营商查询（不等探测出样本）。
    ///
    /// 之前只有"统计表里已经出现的行"才会去查，于是刚启动 / 某条链路还没探测成功时，
    /// 目标运营商那一列是空的——看起来像"这个 IP 查不到运营商"。
    /// </summary>
    private void RequestIspForLinks(IEnumerable<LinkConfig> links)
    {
        if (_isp?.Enabled != true || IspCheck.IsChecked != true) return;

        foreach (var link in links)
        {
            var host = IspAddressRules.HostPart(link.Host);
            if (host.Length > 0) _isp.Request(host);
        }
    }

    private static LinkConfig Clone(LinkConfig link) => link.Clone();

    /// <summary>
    /// 只改协议勾选时**立刻**作用到运行中的链路：它只会"收窄"发包范围，
    /// 不可能把包打到半截的地址上（主机/端口改动仍走 1 秒防抖）。
    /// 现场反馈：取消勾选 TCP 后界面写着"不发包"、包却照发，统计也跟着失真。
    /// </summary>
    private void ToggleLiveProtocol(LinkRow row)
    {
        if (_cts is null) return;

        var applied = _liveLinks.FirstOrDefault(l =>
            string.Equals(l.Name, row.Name.Trim(), StringComparison.Ordinal) &&
            string.Equals(l.Host, row.Host.Trim(), StringComparison.OrdinalIgnoreCase) &&
            l.Port == row.Port);

        if (applied is null) return; // 还没生效过的行（例如刚新增）→ 交给防抖

        // 探测循环每轮重新读取这几个字段，因此下一轮就停止发送
        applied.Tcp = row.Tcp;
        applied.Udp = row.Udp;
        applied.Icmp = row.Icmp;

        _appliedLinksSignature = LinksSignatureOf(_liveLinks);
        UpdateLinkSummary();
    }

    private void DrainPendingResults()
    {
        var epoch = _session.Epoch;
        var batch = new List<ProbeResult>();

        while (_pending.TryDequeue(out var result))
        {
            if (result.Epoch == epoch) batch.Add(result);
        }

        if (batch.Count == 0) return;

        // 曲线数据要的是**整个会话**：在结果被丢进 2000 条上限的明细表之前先喂给曲线
        _chartHistory.AddRange(batch);

        // 同一批内按“最新在前”插入，保持与逐条到达时一致的显示顺序
        for (var i = batch.Count - 1; i >= 0; i--)
        {
            var result = batch[i];
            if (_session.IsAnomaly(result)) _anomalies.Insert(0, result);
            _trend.Insert(0, result);
        }

        TrimTo(_anomalies, MaxAnomalies);
        TrimTo(_trend, MaxTrendRows);
    }

    /// <summary>
    /// 就地更新统计行：行对象稳定存在，只刷新数值。
    /// 不再 Clear + 逐条 Add，因此不会每个周期都让 DataGrid 重置（闪烁/丢失选中与滚动位置）。
    /// </summary>
    private void RefreshStats()
    {
        var rows = _session.BuildRows();
        if (rows.Count == 0 && _statRows.Count == 0) return;

        var live = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < rows.Count; i++)
        {
            var incoming = rows[i];
            live.Add(incoming.Key);

            if (_statRows.TryGetValue(incoming.Key, out var existing))
            {
                existing.CopyFrom(incoming);
                ApplyCarrier(existing);
            }
            else
            {
                // 先在挂事件之前恢复勾选状态，避免"新建行 → 触发勾选事件"这种自我打扰
                if (_chartSelection.Contains(incoming.Key)) incoming.Plot = true;
                incoming.PropertyChanged += OnStatRowChanged;

                _statRows[incoming.Key] = incoming;
                ApplyCarrier(incoming);
                _stats.Insert(Math.Min(i, _stats.Count), incoming);
            }
        }

        if (_statRows.Count == live.Count) return;

        foreach (var key in _statRows.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _stats.Remove(_statRows[key]);
            _statRows.Remove(key);
        }
    }

    private static void TrimTo(IList<ProbeResult> list, int max)
    {
        while (list.Count > max) list.RemoveAt(list.Count - 1);
    }

    private void UpdateStatus(string? note = null)
    {
        _statusNote = note;
        _statusIsError = false;
        RefreshStatusBar();
    }

    /// <summary>协议勾选变化：探测循环每轮读取这两个开关，因此立即生效，无需重启测试。</summary>
    private void Protocol_Changed(object sender, RoutedEventArgs e)
    {
        _liveTcp = TcpCheck.IsChecked == true;
        _liveUdp = UdpCheck.IsChecked == true;
        _liveIcmp = IcmpCheck.IsChecked == true;
        ApplyGlobalProtocolsToRows();
        RefreshStatusBar();
    }

    /// <summary>把全局协议勾选推给链路编辑器，让每行显示“实际会发出的协议”。</summary>
    private void ApplyGlobalProtocolsToRows()
    {
        foreach (var row in _linkRows) row.SetGlobalProtocols(_liveTcp, _liveUdp, _liveIcmp);
    }

    /// <summary>当前实际会发出去的探测路数（链路 × 已勾选协议，再与链路自身的开关取交集）。</summary>
    private int ActiveProbeCount()
    {
        // 运行中以探测循环真正在用的链路为准（编辑器改动有约 1 秒防抖）
        var links = _cts is not null
            ? _liveLinks
            : _linkRows.Where(r => r.IsValid).Select(r => r.ToConfig()).ToArray();

        var count = 0;
        foreach (var link in links)
        {
            if (_liveTcp && link.Tcp) count++;
            if (_liveUdp && link.Udp) count++;
            if (_liveIcmp && link.Icmp) count++;
        }
        return count;
    }

    /// <summary>把全局协议开关描述成一行（状态栏用）。三个都没勾 = 只监听不应答探测。</summary>
    private static string DescribeProtocolSwitches(bool tcp, bool udp, bool icmp)
    {
        var parts = new List<string>(3);
        if (tcp) parts.Add("TCP");
        if (udp) parts.Add("UDP");
        if (icmp) parts.Add("ICMP");

        return parts.Count switch
        {
            0 => "无（只监听不应答探测）",
            1 => "仅 " + parts[0],
            3 => "TCP + UDP + ICMP",
            _ => string.Join(" + ", parts)
        };
    }

    private void RefreshStatusBar()
    {
        var running = _cts is not null;

        StatusDot.Fill = new SolidColorBrush(running
            ? Color.FromRgb(0x1E, 0x8E, 0x3E)
            : Color.FromRgb(0x9A, 0xA0, 0xA6));
        StatusText.Text = running ? "运行中" : "已停止";

        var protocols = DescribeProtocolSwitches(_liveTcp, _liveUdp, _liveIcmp);

        StatusParams.Text = running
            ? $"监听 {_settings.ListenPort} | 协议 {protocols} | 生效探测 {ActiveProbeCount()} 路 | " +
              $"间隔 {_settings.IntervalMs}ms | 超时 {_settings.TimeoutMs}ms"
            : $"监听 {_settings.ListenPort}（未启动） | 协议 {protocols}";

        StatusSamples.Text = $"本会话样本 {_session.TotalSamples}";

        var note = _statusNote;
        if (_configLoadError is not null)
            note = "⚠ 配置文件读取失败，未自动保存" + (note is null ? "" : " | " + note);

        // 注册状态一行（未启用注册时 DescribeConnection 返回 null，状态栏与升级前完全一致）
        var registry = RegistryUiText.DescribeConnection(_registryClient?.Status, _settings.Registrar, DateTime.UtcNow);
        if (registry is not null)
            note = string.IsNullOrEmpty(note) ? registry.Text : note + " | " + registry.Text;

        // 运营商查询出问题（被限流/查询失败）时也提示一句：它是"附加信息"，从不影响探测
        var isp = _isp?.Status;
        var ispWarning = false;
        if (isp is { Enabled: true } && (isp.LastError is not null || isp.Throttled))
        {
            var text = isp.Throttled
                ? $"运营商查询被限流，{Math.Max(1, (int)(isp.ThrottledUntilUtc!.Value - DateTime.UtcNow).TotalSeconds)} 秒后重试"
                : "运营商查询失败：" + isp.LastError;

            note = string.IsNullOrEmpty(note) ? text : note + " | " + text;
            ispWarning = true;
        }

        StatusNote.Text = note ?? "";
        StatusNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        RefreshIspNote();
        StatusNote.Foreground = new SolidColorBrush(_statusIsError
            ? Color.FromRgb(0xC5, 0x22, 0x1F)
            : registry?.Warning == true || ispWarning
                ? Color.FromRgb(0xB2, 0x6A, 0x00)
                : Color.FromRgb(0x5F, 0x63, 0x68));

        UpdateLinkSummary();
    }

    /// <summary>
    /// 统计表下方那一行"运营商查询状态"：直接回答"为什么这里没显示运营商"。
    /// 现场最常见的三种原因（没勾选 / 目标是内网 / 查询不通）都在这里写明。
    /// </summary>
    private void RefreshIspNote()
    {
        if (IspNote is null) return;

        var status = _isp?.Status;

        if (IspCheck.IsChecked != true || status is null)
        {
            IspNote.Text = "运营商信息：未启用（勾选上方「显示运营商信息并判断是否跨运营商」后开始查询）";
            IspNote.Visibility = Visibility.Visible;
            return;
        }

        var parts = new List<string> { $"运营商信息：已缓存 {status.Cached} 个地址" };

        if (status.Throttled)
            parts.Add($"被 ip-api 限流，{(int)Math.Max(0, (status.ThrottledUntilUtc!.Value - DateTime.UtcNow).TotalSeconds)} 秒后重试");
        else if (status.LastError is not null)
            parts.Add($"最近失败：{status.LastError}");

        if (status.Pending > 0) parts.Add($"{status.Pending} 个地址待查询");
        else if (status.Cached == 0 && status.Requested > 0)
            parts.Add($"已登记 {status.Requested} 个地址，等首轮查询");

        var privateTargets = _statRows.Values.Count(r => IspAddressRules.HostPart(r.Target) is { Length: > 0 } h && !IspAddressRules.IsPublicIp(h));
        if (privateTargets > 0)
            parts.Add($"{privateTargets} 条链路的目标是内网地址（内网没有运营商信息，显示为「内网地址」）");

        var noEcho = _statRows.Values.Count(r => IspAddressRules.HostPart(r.SeenFrom).Length == 0);
        if (noEcho > 0 && status.Cached == 0)
            parts.Add("本端出口 IP 要等首次探测成功后才拿得到");

        parts.Add("关掉开关即停止联网查询");
        IspNote.Text = string.Join(" · ", parts);
        IspNote.Visibility = Visibility.Visible;
    }

    private void OnLogError(string message)
        => Dispatcher.InvokeAsync(() =>
        {
            _statusNote = "日志写入失败：" + message;
            _statusIsError = true;
            RefreshStatusBar();
        });

    protected override void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        StopInternal();
        _log.Dispose();
        base.OnClosed(e);
    }
}
