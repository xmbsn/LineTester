using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LineTester.Views;

/// <summary>
/// 标准 IPv4 输入控件：四段数字 + 固定小圆点，自动跳段、粘贴整段地址可自动铺开。
///
/// 存在的理由（现场反馈）：地址框与端口框原来长得一模一样，运维把 IP 敲进了端口框，
/// 得到的是"外网端口必须是 1~65535"这种看不懂的报错。现在：
/// ① IP 框是四段式，一眼可辨；② 每段只收数字、最多 3 位，且超过 255 标红；
/// ③ 端口框只收数字（连 '.' 都输不进去）——两边都不可能再填错。
///
/// 域名/主机名仍支持（配置里手写过域名的场景不会因为这次改动而失效）：
/// 值不是 IPv4 字面量时自动切到主机名输入框，也可以用右下角的链接手动切换。
/// </summary>
public partial class Ipv4Box : UserControl
{
    // 注意：静态画刷必须 Freeze()。不冻结的 Freezable 属于"创建它的那个线程"，
    // 而自检里每个界面用例都在新的 STA 线程上跑 → 第二次使用就会抛
    // "调用线程无法访问此对象，因为另一个线程拥有该对象"（实测踩过）。
    private static readonly Brush NormalBorder = Frozen(0xDA, 0xDC, 0xE0);
    private static readonly Brush ProblemBorder = Frozen(0xC5, 0x22, 0x1F);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private bool _hostMode;

    public Ipv4Box()
    {
        InitializeComponent();

        foreach (var octet in Octets)
        {
            octet.PreviewTextInput += OnOctetPreviewTextInput;
            octet.PreviewKeyDown += OnOctetPreviewKeyDown;
            octet.TextChanged += OnOctetTextChanged;
            DataObject.AddPastingHandler(octet, OnOctetPaste);
            octet.GotKeyboardFocus += (_, _) => octet.SelectAll();
        }

        HostBox.TextChanged += (_, _) =>
        {
            UpdateOctetBorders();
            TextChanged?.Invoke(this, EventArgs.Empty);
            RaiseAddressChanged();
        };
    }

    /// <summary>地址内容变化（绑定方据此重新校验）。</summary>
    public event EventHandler? TextChanged;

    /// <summary>当前地址：IPv4 模式下是 <c>a.b.c.d</c>，主机名模式下是原文。</summary>
    public string Text
    {
        // 空段不补点：清空时要读回空串，填了一半时读回"203.0.113"（外层据此报"不是合法 IPv4"）
        get => _hostMode
            ? HostBox.Text.Trim()
            : string.Join(".", Octets.Select(o => o.Text.Trim())).TrimEnd('.');
        set => SetText(value);
    }

    /// <summary>当前是否处于主机名模式。</summary>
    public bool IsHostMode => _hostMode;

    /// <summary>IPv4 是否四段齐全且每段都在 0~255。</summary>
    public bool IsCompleteIpv4 => !_hostMode && TryParseOctets(Text, out _);

    /// <summary>把 <c>a.b.c.d</c> 拆成四段（每段 0~255 的十进制数字）——不合法返回 false。</summary>
    public static bool TryParseOctets(string? text, out string[] parts)
    {
        parts = Array.Empty<string>();

        var value = text?.Trim() ?? "";
        var split = value.Split('.');
        if (split.Length != 4) return false;

        foreach (var part in split)
        {
            if (part.Length == 0 || part.Length > 3 || !part.All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(part, out var number) || number is < 0 or > 255) return false;
        }

        // 与系统解析保持一致：必须是 IPv4 字面量（"1.2.3.4" 这类）
        if (!IPAddress.TryParse(value, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        parts = split;
        return true;
    }

    private TextBox[] Octets => new[] { Octet1, Octet2, Octet3, Octet4 };

    private void SetText(string? value)
    {
        var text = value?.Trim() ?? "";

        if (text.Length == 0)
        {
            SwitchToIpv4();
            foreach (var octet in Octets) octet.Text = "";
            return;
        }

        if (TryParseOctets(text, out var parts))
        {
            SwitchToIpv4();
            for (var i = 0; i < Octets.Length; i++) Octets[i].Text = parts[i];
            return;
        }

        // 不是 IPv4 字面量（域名、IPv6、或用户敲了一半）→ 主机名框承载原文
        SwitchToHost();
        HostBox.Text = text;
    }

    private void SwitchToIpv4()
    {
        if (!_hostMode) return;

        _hostMode = false;
        HostBox.Visibility = Visibility.Collapsed;
        Ipv4Panel.Visibility = Visibility.Visible;
        ModeButton.Content = "填主机名";
        RaiseAddressChanged();
    }

    private void SwitchToHost()
    {
        if (_hostMode) return;

        _hostMode = true;
        // 切过去时把当前能拼出来的内容带过去，避免"切一次就丢值"
        HostBox.Text = string.Join(".", Octets.Select(o => o.Text.Trim())).Trim('.');
        Ipv4Panel.Visibility = Visibility.Collapsed;
        HostBox.Visibility = Visibility.Visible;
        ModeButton.Content = "改填 IPv4";
        RaiseAddressChanged();
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_hostMode)
        {
            var text = HostBox.Text;
            SwitchToIpv4();

            if (TryParseOctets(text, out var parts))
            {
                for (var i = 0; i < Octets.Length; i++) Octets[i].Text = parts[i];
            }
            else
            {
                foreach (var octet in Octets) octet.Text = "";
                Octets[0].Focus();
            }

            return;
        }

        SwitchToHost();
        HostBox.Focus();
    }

    // ---------------- 分段输入行为 ----------------

    private void OnOctetPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;

        // 输入 '.' 视为"跳到下一段"（而不是把点塞进这一段的文本里）
        if (e.Text is "." or "。" or "·")
        {
            e.Handled = true;
            MoveNext(box);
            return;
        }

        // 只收半角数字：全角数字、字母、符号一律挡掉（这是"填错框"的最后一道闸）
        if (e.Text.Length == 0 || !e.Text.All(char.IsAsciiDigit)) e.Handled = true;
    }

    private void OnOctetPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;

        switch (e.Key)
        {
            case Key.OemPeriod or Key.Decimal or Key.Oem2:
                MoveNext(box);
                e.Handled = true;
                break;

            case Key.Space:
                e.Handled = true;
                break;

            case Key.Back when box.Text.Length == 0:
                MovePrevious(box);
                e.Handled = true;
                break;

            case Key.Left when box.SelectionStart == 0 && box.SelectionLength == 0:
                MovePrevious(box);
                e.Handled = true;
                break;
        }
    }

    private void OnOctetTextChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;

        // 敲满 3 位自动跳到下一段（"203" → 下一段），与常规 IP 输入框一致
        if (box.Text.Length >= 3 && box.IsKeyboardFocusWithin) MoveNext(box);

        UpdateOctetBorders();
        TextChanged?.Invoke(this, EventArgs.Empty);
        RaiseAddressChanged();
    }

    private void OnOctetPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)) return;

        var text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
        if (text is null) return;

        // 粘贴一整段 "203.0.113.9" → 自动铺到四段里
        if (TryParseOctets(text, out var parts))
        {
            e.CancelCommand();
            for (var i = 0; i < Octets.Length; i++) Octets[i].Text = parts[i];
        }
    }

    private void MoveNext(TextBox box)
    {
        var octets = Octets;
        var index = Array.IndexOf(octets, box);
        if (index < 0 || index >= octets.Length - 1) return;

        octets[index + 1].Focus();
        octets[index + 1].SelectAll();
    }

    private void MovePrevious(TextBox box)
    {
        var octets = Octets;
        var index = Array.IndexOf(octets, box);
        if (index <= 0) return;

        octets[index - 1].Focus();
        octets[index - 1].SelectAll();
    }

    /// <summary>超过 255 的那一段标红（其余保持正常），空段不标红（正在输入）。</summary>
    private void UpdateOctetBorders()
    {
        foreach (var octet in Octets)
        {
            var text = octet.Text.Trim();
            var bad = text.Length > 0 &&
                      (!int.TryParse(text, out var value) || value is < 0 or > 255);

            octet.BorderBrush = bad ? ProblemBorder : NormalBorder;
            octet.ToolTip = bad ? $"「{text}」不是 0~255 之间的数字" : null;
        }
    }

    private void RaiseAddressChanged()
    {
        // 便于外层（例如状态区）在地址变化时刷新
        AddressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>地址文本变化（含模式切换）。</summary>
    public event EventHandler? AddressChanged;
}
