using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LineTester.Registry;

namespace LineTester.Views;

/// <summary>
/// 「注册中心」标签页（规格 §11.1）。
///
/// 这一层只做两件事：把控件值装进/取出 <see cref="RegistrarDraft"/>，
/// 以及把 <see cref="RegistryUiText"/> 算出来的文案贴到界面上。
/// 判断逻辑（校验、文案、比对）都在不引用 WPF 的纯类里，因此可以被零依赖自检直接断言；
/// 主窗口负责真正的运行生命周期（启动/停止同步、落盘、链路联动）。
/// </summary>
public partial class RegistryPanel : UserControl
{
    private static readonly Brush NormalBrush = new SolidColorBrush(Color.FromRgb(0x5F, 0x63, 0x68));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xB2, 0x6A, 0x00));

    private readonly ObservableCollection<RegistryNodeRow> _nodeRows = new();

    private RegistryStore? _store;
    private ulong _shownRevision = ulong.MaxValue;
    private DateTime _lastCenterRefreshUtc = DateTime.MinValue;
    private bool _syncingSecret;

    /// <summary>界面上显示的节点身份：读回配置时必须带上它，否则每次保存都会换一个身份。</summary>
    private string _nodeId = "";

    public RegistryPanel()
    {
        InitializeComponent();

        // 两个中心表的行集合由本页持有：主窗口只把 store 递进来
        NodeGrid.ItemsSource = _nodeRows;

        SecretBox.PasswordChanged += (_, _) => SyncSecret(fromPasswordBox: true);
        SecretTextBox.TextChanged += (_, _) => SyncSecret(fromPasswordBox: false);
        ShowSecretCheck.Checked += (_, _) => ApplySecretVisibility();
        ShowSecretCheck.Unchecked += (_, _) => ApplySecretVisibility();
    }

    /// <summary>点「保存配置」：交给主窗口统一校验并落盘（与窗口底部按钮同一条路径）。</summary>
    public event Action? SaveRequested;

    /// <summary>点「立即注册 / 立即同步」。</summary>
    public event Action? RegisterRequested;

    /// <summary>中心清单被本页改动（移除 / 恢复 / 分发勾选）→ 主窗口刷新状态栏与链路页。</summary>
    public event Action? CenterChanged;

    /// <summary>确认框。测试可替换，避免模态框阻塞；默认「是/否」，默认选「否」。</summary>
    public Func<string, string, bool> Confirm { get; set; } = (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            == MessageBoxResult.Yes;

    /// <summary>提示框（测试可替换）。</summary>
    public Action<string, string> Alert { get; set; } = (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---------------- 配置 ↔ 界面 ----------------

    /// <summary>把配置填进界面（窗口启动、重新加载配置时调用）。</summary>
    public void Load(RegistrarConfig config)
    {
        var draft = RegistrarDraft.From(config);

        EnabledCheck.IsChecked = draft.Enabled;
        ServeAsCenterCheck.IsChecked = draft.ServeAsCenter;
        DistributableCheck.IsChecked = draft.Distributable;
        AdvertiseTcpCheck.IsChecked = draft.AdvertiseTcp;
        AdvertiseUdpCheck.IsChecked = draft.AdvertiseUdp;

        NodeNameBox.Text = draft.NodeName;
        PublicHostBox.Text = draft.PublicHost;
        PublicPortBox.Text = draft.PublicPortText;
        CenterHostBox.Text = draft.CenterHost;
        CenterPortBox.Text = draft.CenterPortText;
        HeartbeatBox.Text = draft.HeartbeatText;
        SyncBox.Text = draft.SyncText;
        RetryBox.Text = draft.RetryText;
        MaxRetriesBox.Text = draft.MaxRetriesText;

        SecretBox.Password = draft.Secret;
        SecretTextBox.Text = draft.Secret;

        NodeIdText.Text = string.IsNullOrEmpty(draft.NodeId)
            ? "（首次保存时自动生成）"
            : draft.NodeId;

        _nodeId = draft.NodeId;
    }

    /// <summary>读界面到配置。校验不通过返回 false 且不改动配置（避免写回"改了一半"的值）。</summary>
    public bool TryReadInto(RegistrarConfig config, out string error)
    {
        var draft = RegistrarDraft.From(config);

        // 身份来自界面（不是来自传进来的对象）：否则把配置读到一个新对象上就会重新生成身份
        draft.NodeId = string.IsNullOrWhiteSpace(_nodeId) ? config.NodeId : _nodeId;

        draft.Enabled = EnabledCheck.IsChecked == true;
        draft.ServeAsCenter = ServeAsCenterCheck.IsChecked == true;
        draft.Distributable = DistributableCheck.IsChecked == true;
        draft.AdvertiseTcp = AdvertiseTcpCheck.IsChecked == true;
        draft.AdvertiseUdp = AdvertiseUdpCheck.IsChecked == true;

        draft.NodeName = NodeNameBox.Text;
        draft.PublicHost = PublicHostBox.Text;
        draft.PublicPortText = PublicPortBox.Text;
        draft.CenterHost = CenterHostBox.Text;
        draft.CenterPortText = CenterPortBox.Text;
        draft.HeartbeatText = HeartbeatBox.Text;
        draft.SyncText = SyncBox.Text;
        draft.RetryText = RetryBox.Text;
        draft.MaxRetriesText = MaxRetriesBox.Text;

        // 密钥以"当前可见的那个"为准：隐藏时密码框是权威值，显示时文本框是权威值
        draft.Secret = ShowSecretCheck.IsChecked == true ? SecretTextBox.Text : SecretBox.Password;

        if (!draft.TryApply(config, out error)) return false;

        _nodeId = config.NodeId;
        NodeIdText.Text = config.NodeId;
        return true;
    }

    // ---------------- 状态显示 ----------------

    /// <summary>刷新状态区（连接、对外地址核对、分发状态、最近错误）。主窗口定时调用。</summary>
    public void ShowStatus(RegistryClientStatus? status, RegistrarConfig config, DateTime nowUtc)
    {
        var connection = RegistryUiText.DescribeConnection(status, config, nowUtc);
        ConnectionText.Text = connection?.Text ?? "未启用注册同步：勾选“参与注册同步”并填写中心地址后启用";
        ConnectionText.Foreground = PickBrush(connection);

        var selfCheck = RegistryUiText.DescribeSelfCheck(status, config);
        SelfCheckText.Text = selfCheck?.Text ?? "";
        SelfCheckText.Foreground = PickBrush(selfCheck);

        var distribution = RegistryUiText.DescribeSelfDistribution(config);
        SelfDistributionText.Text = distribution?.Text ?? "";
        SelfDistributionText.Foreground = PickBrush(distribution);

        var error = status?.LastError;
        LastErrorText.Text = error is null ? "" : "最近错误：" + error;
        LastErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;

        CenterSection.Visibility = config.ServeAsCenter ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 刷新中心节点表。同一 revision 且 5 秒内不重建：否则每 500ms 重建一次表格会
    /// 丢失选中行、并让勾选框跟着闪。
    /// </summary>
    public void ShowCenter(RegistryStore? store, DateTime nowUtc, bool force = false)
    {
        _store = store;

        if (store is null)
        {
            _nodeRows.Clear();
            RemovedNodesText.Text = "";
            RestoreNodeButton.IsEnabled = false;
            return;
        }

        // 判活也要有人做：中心进程或界面长时间没有请求时，离线状态不能停在旧值
        store.RefreshOnlineState();

        var stale = nowUtc - _lastCenterRefreshUtc > TimeSpan.FromSeconds(5);
        if (!force && store.Revision == _shownRevision && !stale) return;

        _shownRevision = store.Revision;
        _lastCenterRefreshUtc = nowUtc;

        var selected = NodeGrid.SelectedItems.Cast<RegistryNodeRow>()
            .Select(r => r.NodeId)
            .ToHashSet(StringComparer.Ordinal);

        _nodeRows.Clear();
        foreach (var node in store.Nodes.OrderBy(n => n.Name, StringComparer.Ordinal))
            _nodeRows.Add(RegistryNodeRow.FromNode(node, nowUtc, SetCenterHidden));

        // 已移除的节点也列出来，否则"恢复"按钮选不中它
        foreach (var nodeId in store.RemovedNodeIds)
        {
            if (store.Nodes.Any(n => string.Equals(n.NodeId, nodeId, StringComparison.Ordinal))) continue;
            _nodeRows.Add(RegistryNodeRow.FromRemoved(nodeId, SetCenterHidden));
        }

        foreach (var row in _nodeRows.Where(r => selected.Contains(r.NodeId)))
            NodeGrid.SelectedItems.Add(row);

        var removed = store.RemovedNodeIds.Count;
        RestoreNodeButton.IsEnabled = removed > 0;
        RemovedNodesText.Text = removed == 0
            ? "移除名单为空。"
            : $"已被移除（不会被心跳加回）：{string.Join("、", store.RemovedNodeIds.Take(5))}" +
              (removed > 5 ? $" 等 {removed} 个" : "") + " —— 选中对应行后点“恢复所选移除”。";
    }

    private bool SetCenterHidden(string nodeId, bool hidden)
    {
        var changed = _store?.SetCenterHidden(nodeId, hidden) == true;
        if (!changed) return false;

        CenterChanged?.Invoke();
        return true;
    }

    // ---------------- 事件 ----------------

    private void RegisterNow_Click(object sender, RoutedEventArgs e) => RegisterRequested?.Invoke();

    private void SyncNow_Click(object sender, RoutedEventArgs e) => RegisterRequested?.Invoke();

    private void SaveRegistry_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();

    private void RemoveNode_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            Alert("注册中心未运行：请先点“启动测试”，并勾选“本机同时承担注册中心角色”。", "移除节点");
            return;
        }

        var selected = NodeGrid.SelectedItems.Cast<RegistryNodeRow>().Where(r => !r.IsRemoved).ToList();
        if (selected.Count == 0)
        {
            Alert("请先在节点表里选中要移除的节点（已移除的行请用“恢复所选移除”）。", "移除节点");
            return;
        }

        var what = selected.Count == 1 ? $"「{selected[0].Name}」" : $"选中的 {selected.Count} 个节点";
        var message = $"确定要从注册中心移除{what}吗？\n\n" +
                      "影响：该节点会从**所有子节点的链路列表**中消失；它继续心跳也不会被加回来，" +
                      "必须在“恢复所选移除”里恢复。\n\n" +
                      "不会删除对端程序与配置，也不影响本机自身的探测。";

        if (!Confirm(message, "移除节点（权威删除）")) return;

        var count = selected.Count(row => _store.RemoveNode(row.NodeId));
        ShowCenter(_store, DateTime.UtcNow, force: true);
        CenterChanged?.Invoke();
        Alert($"已移除 {count} 个节点。其他节点会在各自的同步周期内（默认 300 秒）删除对应链路。", "移除节点");
    }

    private void RestoreNode_Click(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;

        var selected = NodeGrid.SelectedItems.Cast<RegistryNodeRow>().Where(r => r.IsRemoved).ToList();
        if (selected.Count == 0)
        {
            Alert("请先选中状态为“已移除”的行（它们仍列在表里）。", "恢复节点");
            return;
        }

        var count = selected.Count(row => _store.RestoreNode(row.NodeId));
        ShowCenter(_store, DateTime.UtcNow, force: true);
        CenterChanged?.Invoke();
        Alert($"已恢复 {count} 个节点。它们会在下一次心跳后重新出现在清单里。", "恢复节点");
    }

    // ---------------- 小工具 ----------------

    private static Brush PickBrush(StatusNote? note)
    {
        if (note is null) return NormalBrush;
        return note.Warning ? WarningBrush : NormalBrush;
    }

    /// <summary>密钥在"密码框 / 文本框"之间同步，避免任何一边停留在旧值。</summary>
    private void SyncSecret(bool fromPasswordBox)
    {
        if (_syncingSecret) return;

        _syncingSecret = true;
        try
        {
            if (fromPasswordBox) SecretTextBox.Text = SecretBox.Password;
            else SecretBox.Password = SecretTextBox.Text;
        }
        finally
        {
            _syncingSecret = false;
        }
    }

    private void ApplySecretVisibility()
    {
        var show = ShowSecretCheck.IsChecked == true;

        if (show) SecretTextBox.Text = SecretBox.Password;

        SecretTextBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SecretBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }
}
