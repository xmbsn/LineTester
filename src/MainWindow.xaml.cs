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

        LoadLinkRows(cfg.Links);
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
        if (e.PropertyName is nameof(LinkRow.Problem) or nameof(LinkRow.IsValid)) return;
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
        return _linkRows.All(r => r.IsValid);
    }

    private static string DuplicateKey(LinkRow row, string protocol)
        => $"{row.Name.Trim()}|{row.Host.Trim()}|{row.Port}|{protocol}";

    private string LinksSignature()
        => string.Join(";", _linkRows.Select(
            r => $"{r.Name.Trim()}|{r.Host.Trim()}|{r.PortText?.Trim()}|{r.Tcp}|{r.Udp}"));

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
            parts.Add("有未保存的修改");

        if (_cts is not null && !string.Equals(signature, _appliedLinksSignature, StringComparison.Ordinal))
            parts.Add("⚠ 运行中的测试仍在使用启动时的链路，点“启动测试”后生效");

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

        var what = selected.Count == 1 ? $"「{selected[0].Display}」" : $"选中的 {selected.Count} 条链路";
        var message = $"确定要删除{what}吗？\n\n已统计到的数据和 logs 目录下的历史日志不会被删除。";

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
            var server = new ProbeServer(_settings.ListenPort);
            server.Start();
            _server = server;

            _session.Configure(_settings);
            ClearSession();

            // 记录本次真正生效的链路，便于提示"运行中的测试仍在使用旧链路"
            _appliedLinksSignature = LinksSignature();
            UpdateLinkSummary();

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loop = Task.Run(() => TestLoop(cts.Token));

            var notes = new List<string>();
            if (!server.DualStack) notes.Add("本机不支持 IPv6，监听为仅 IPv4");
            if (ActiveProbeCount() == 0) notes.Add("当前没有生效的探测（未勾选协议，或链路未启用该协议），只会监听应答");

            UpdateStatus(notes.Count == 0 ? null : string.Join("；", notes));
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

        _server?.Dispose();
        _server = null;

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

    // ---------------- 探测循环 ----------------

    private async Task TestLoop(CancellationToken ct)
    {
        var links = _settings.Links.ToArray();
        var interval = _settings.IntervalMs;
        var timeout = _settings.TimeoutMs;

        while (!ct.IsCancellationRequested)
        {
            var roundStart = Stopwatch.StartNew();
            var epoch = Volatile.Read(ref _loopEpoch);

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
        RefreshStats();
        RefreshStatusBar();
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
        var count = 0;
        foreach (var link in _settings.Links)
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

        StatusNote.Text = note ?? "";
        StatusNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        StatusNote.Foreground = new SolidColorBrush(_statusIsError
            ? Color.FromRgb(0xC5, 0x22, 0x1F)
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
