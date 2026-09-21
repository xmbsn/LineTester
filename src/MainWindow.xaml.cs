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
using LineTester.Registry;
using LineTester.Views;

namespace LineTester;

public partial class MainWindow : Window
{
    private const int MaxAnomalies = 1000;
    private const int MaxTrendRows = 2000;

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

    // ---- 注册中心（切片 3）----

    private RegistryClient? _registryClient;
    private RegistryStore? _registryStore;
    private RegistryCenterService? _registryCenter;

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

        // 注册中心页：校验与落盘仍走主窗口这一条路径，页内只负责显示与即时操作
        RegistryTabPanel.SaveRequested += () => SaveConfig_Click(this, new RoutedEventArgs());
        RegistryTabPanel.RegisterRequested += OnRegistryRegisterRequested;
        RegistryTabPanel.CenterChanged += OnRegistryCenterChanged;

        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        _log.ErrorOccurred += OnLogError;

        LoadConfigFromDisk(showError: false);

        Loaded += (_, _) =>
        {
            if (_configLoadError is not null) ShowConfigError(_configLoadError);
        };
    }

    // ---------------- 配置 ----------------

    private void LoadConfigFromDisk(bool showError)
    {
        _settings = ConfigManager.Load(out var error);
        _configLoadError = error;
        _configStamp = ConfigManager.Stamp();
        ApplyConfigToUi(_settings);
        if (error is not null && showError) ShowConfigError(error);
        UpdateStatus();
    }

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

        RegistryTabPanel.Load(cfg.Registrar);

        LoadLinkRows(cfg.Links);
        RefreshRegistryUi();
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

        // 注册链路：TCP 与 UDP 都取消勾选 = 本地停测意图（规格 §8.5）。
        // 它必须写回配置：否则下一次同步会把"取消勾选"当成需要纠正的偏差，又给你勾回来。
        if (sender is LinkRow { IsRegistry: true } registryRow &&
            e.PropertyName is nameof(LinkRow.Tcp) or nameof(LinkRow.Udp))
        {
            var disabled = !registryRow.Tcp && !registryRow.Udp;

            if (registryRow.LocalDisabled != disabled)
            {
                registryRow.LocalDisabled = disabled;
                UpdateStatus(disabled
                    ? $"「{registryRow.Name}」已本地停测：注册同步不会覆盖这个意图，重新勾选任一协议即恢复"
                    : $"「{registryRow.Name}」已恢复本地探测（协议以注册中心下发的为准）");
            }

            // 恢复探测时立刻同步一次，否则要等一个同步周期才拿回对端宣告的协议
            if (!disabled && _registryClient is not null) _ = _registryClient.RegisterNowAsync();
        }
        else if (sender is LinkRow toggledRow &&
                 e.PropertyName is nameof(LinkRow.Tcp) or nameof(LinkRow.Udp) &&
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

                if (row.Tcp) seen[DuplicateKey(row, "TCP")] = i;
                if (row.Udp) seen[DuplicateKey(row, "UDP")] = i;
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

    private static string DuplicateKey(LinkRow row, string protocol)
        => $"{row.Name.Trim()}|{row.Host.Trim()}|{row.Port}|{protocol}";

    private string LinksSignature()
        => LinksSignatureOf(_linkRows.Select(r => r.ToConfig()));

    /// <summary>同一套签名口径，但直接作用于配置（注册同步自动落盘后用它比对"是否已保存"）。</summary>
    private static string LinksSignatureOf(IEnumerable<LinkConfig> links)
        => string.Join(";", links.Select(l => $"{l.Name.Trim()}|{l.Host.Trim()}|{l.Port}|{l.Tcp}|{l.Udp}"));

    private string LinkProblems()
        => string.Join("\n", _linkRows
            .Select((row, index) => (row, index))
            .Where(x => !x.row.IsValid)
            .Select(x => $"第 {x.index + 1} 行 {x.row.Display}：{x.row.Problem}"));

    private void UpdateLinkSummary()
    {
        if (LinkSummary is null) return;

        var parts = new List<string> { $"共 {_linkRows.Count} 条链路" };
        parts.Add($"配置中 TCP {_linkRows.Count(r => r.IsValid && r.Tcp)} / UDP {_linkRows.Count(r => r.IsValid && r.Udp)}");

        var invalid = _linkRows.Count(r => !r.IsValid);
        if (invalid > 0) parts.Add($"⚠ {invalid} 条校验未通过，保存与启动都会被拒绝");

        var disabled = _linkRows.Count(r => r.IsValid && !r.Tcp && !r.Udp);
        if (disabled > 0)
            parts.Add($"{disabled} 条不主动探测（TCP/UDP 都未勾选，对端可能是纯服务端）");

        var signature = LinksSignature();
        if (!string.Equals(signature, _savedLinksSignature, StringComparison.Ordinal))
            parts.Add("有未保存的修改（点“保存配置”写入文件；探测已在用新链路）");

        if (_cts is not null && !string.Equals(signature, _appliedLinksSignature, StringComparison.Ordinal))
            parts.Add("刚改动的链路将在 1 秒内生效（防抖：避免边打字边发包）");

        // 全局与链路冲突时提示谁说了算
        var suppressed = _linkRows.Where(r => r.IsValid).SelectMany(r =>
        {
            var list = new List<string>(2);
            if (!_liveTcp && r.Tcp) list.Add($"{r.Name.Trim()} 的 TCP");
            if (!_liveUdp && r.Udp) list.Add($"{r.Name.Trim()} 的 UDP");
            return list;
        }).ToList();

        if (suppressed.Count > 0)
            parts.Add($"全局未勾选，已压住：{string.Join("、", suppressed.Take(3))}" +
                      (suppressed.Count > 3 ? $" 等 {suppressed.Count} 项" : "") + "（总开关只能收紧）");

        if (_linkRows.Count == 0)
            parts.Add("没有链路时程序只监听应答，不主动发包");

        LinkSummary.Text = string.Join(" · ", parts);
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
        if (e.Column.Header is "TCP" or "UDP") return;

        e.Cancel = true;
        UpdateStatus($"「{row.Name}」来自注册中心：名称/主机/端口不能本地修改；" +
                     "要本地停测，请把该行的 TCP 与 UDP 都取消勾选（同步不会覆盖这个意图）");
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
            : new LinkRow
            {
                Name = template.Name + "-副本",
                Host = template.Host,
                PortText = template.PortText,
                Tcp = template.Tcp,
                Udp = template.Udp
            };

        AddLinkRow(row);
        RevalidateLinks();
        SelectAndFocus(row);
        UpdateStatus("已新增一条链路，请填写名称与主机/IP（点“启动测试”后生效）");
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
            last = new LinkRow
            {
                Name = source.Name + "-副本",
                Host = source.Host,
                PortText = source.PortText,
                Tcp = source.Tcp,
                Udp = source.Udp
            };
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
            "配置文件已被其它程序修改：\n" + ConfigManager.ConfigPath + "\n\n" +
            "是：重新加载文件（放弃界面上的修改）\n" +
            "否：用界面当前值覆盖文件\n" +
            "取消：放弃本次操作",
            "配置已变更", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

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

        UpdateStatus($"配置已保存 → {ConfigManager.ConfigPath}");
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
                    store, _settings.Registrar.Secret, _settings.Registrar.HeartbeatSeconds);
                server.ProtocolHandler = _registryCenter.HandleAsync;
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
            Tcp = _settings.Registrar.AdvertiseTcp,
            Udp = _settings.Registrar.AdvertiseUdp,
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
        var rows = _linkRows
            .Where(r => r.IsRegistry && !string.IsNullOrEmpty(r.NodeId))
            .ToDictionary(r => r.NodeId, StringComparer.Ordinal);

        foreach (var link in _settings.Links.Where(l => l.Source == LinkSources.Registry))
        {
            if (rows.TryGetValue(link.NodeId, out var row)) link.LocalDisabled = row.LocalDisabled;
        }
    }

    /// <summary>只同步注册来源的行：手工行保持原样，不打断用户正在编辑的内容。</summary>
    private void SyncRegistryRows(List<LinkConfig> links)
    {
        var registryLinks = links.Where(l => l.Source == LinkSources.Registry).ToList();
        var live = registryLinks.Select(l => l.NodeId).ToHashSet(StringComparer.Ordinal);

        foreach (var row in _linkRows.Where(r => r.IsRegistry && !live.Contains(r.NodeId)).ToList())
        {
            row.PropertyChanged -= OnLinkRowChanged;
            _linkRows.Remove(row);
        }

        foreach (var link in registryLinks)
        {
            var row = _linkRows.FirstOrDefault(r => r.IsRegistry && r.NodeId == link.NodeId);
            if (row is null) AddLinkRow(LinkRow.FromConfig(link));
            else row.ApplyFrom(link);
        }

        // 撤销缓冲里可能留着已被中心删除的注册行：留着会让"撤销删除"把它复活
        _lastDeleted.RemoveAll(x => x.Row.IsRegistry && !live.Contains(x.Row.NodeId));
        UpdateUndoButton();

        ApplyGlobalProtocolsToRows();
    }

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
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
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

            var tasks = new List<Task<ProbeResult?>>(links.Length * 2);
            foreach (var link in links)
            {
                if (sendTcp && link.Tcp) tasks.Add(_client.ProbeAsync(link, "TCP", timeout, ct));
                if (sendUdp && link.Udp) tasks.Add(_client.ProbeAsync(link, "UDP", timeout, ct));
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
        RefreshRegistryUi();
        RefreshStatusBar();
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
    }

    private static LinkConfig Clone(LinkConfig link) => new()
    {
        Name = link.Name,
        Host = link.Host,
        Port = link.Port,
        Tcp = link.Tcp,
        Udp = link.Udp,
        Source = link.Source,
        NodeId = link.NodeId,
        LocalDisabled = link.LocalDisabled
    };

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

        // 探测循环每轮重新读取这两个字段，因此下一轮就停止发送
        applied.Tcp = row.Tcp;
        applied.Udp = row.Udp;

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
            }
            else
            {
                _statRows[incoming.Key] = incoming;
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
        ApplyGlobalProtocolsToRows();
        RefreshStatusBar();
    }

    /// <summary>把全局协议勾选推给链路编辑器，让每行显示“实际会发出的协议”。</summary>
    private void ApplyGlobalProtocolsToRows()
    {
        foreach (var row in _linkRows) row.SetGlobalProtocols(_liveTcp, _liveUdp);
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
        }
        return count;
    }

    private void RefreshStatusBar()
    {
        var running = _cts is not null;

        StatusDot.Fill = new SolidColorBrush(running
            ? Color.FromRgb(0x1E, 0x8E, 0x3E)
            : Color.FromRgb(0x9A, 0xA0, 0xA6));
        StatusText.Text = running ? "运行中" : "已停止";

        var protocols = (_liveTcp, _liveUdp) switch
        {
            (true, true) => "TCP + UDP",
            (true, false) => "仅 TCP",
            (false, true) => "仅 UDP",
            _ => "无（只监听不应答探测）"
        };

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

        StatusNote.Text = note ?? "";
        StatusNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        StatusNote.Foreground = new SolidColorBrush(_statusIsError
            ? Color.FromRgb(0xC5, 0x22, 0x1F)
            : registry?.Warning == true
                ? Color.FromRgb(0xB2, 0x6A, 0x00)
                : Color.FromRgb(0x5F, 0x63, 0x68));

        UpdateLinkSummary();
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
