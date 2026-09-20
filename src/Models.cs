using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LineTester;

public sealed class AppConfig
{
    public int ListenPort { get; set; } = 9000;
    public int IntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 1000;
    public int WarningRttMs { get; set; } = 300;
    public int StatWindowMinutes { get; set; } = 60;

    /// <summary>窗口内丢包率达到该百分比即判定为“有丢包”。</summary>
    public double LossWarnPercent { get; set; } = 1.0;

    /// <summary>连续丢包达到该次数即判定为“中断”。</summary>
    public int ConsecutiveLossWarn { get; set; } = 3;

    /// <summary>
    /// 全局协议开关：只有勾选的协议才会发出探测包（界面上的 TCP/UDP 勾选框）。
    /// 与每条链路自身的 Tcp/Udp 是“与”关系：两者都开才会真正发包。
    /// </summary>
    public bool EnableTcp { get; set; } = true;

    public bool EnableUdp { get; set; } = true;

    public List<LinkConfig> Links { get; set; } = new();

    public static AppConfig Default() => new()
    {
        Links =
        {
            new LinkConfig { Name = "示例-机房B", Host = "127.0.0.1", Port = 9001, Tcp = true, Udp = true }
        }
    };
}

public sealed class LinkConfig
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool Tcp { get; set; } = true;
    public bool Udp { get; set; } = true;

    // 注意：不要在这里添加只读的派生属性（例如 Key / Target）。
    // System.Text.Json 会把公开的只读属性一起序列化，导致配置文件里出现
    // 程序自己都不读、现场看不懂的字段（保存一次就被写进去）。
    // 统计分组用的复合键由 ProbeResult.Key 提供。
}

public sealed class ProbeResult
{
    public DateTime Timestamp { get; init; }
    public string LinkName { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Protocol { get; init; } = "";

    /// <summary>
    /// 纯往返时延：从发出首个探测字节到收齐对端回包。
    /// 不含 DNS 解析与 TCP 建连，因此 TCP 与 UDP 两行可直接比较。
    /// </summary>
    public double? RttMs { get; init; }

    /// <summary>建连开销：DNS/地址解析 + TCP 三次握手（UDP 仅地址解析）。</summary>
    public double? SetupMs { get; init; }

    public bool Lost { get; init; }

    /// <summary>
    /// 失败原因（见 <see cref="FailureKinds"/> 的稳定 token）；成功时为空串。
    /// 它区分了“被拒绝/无响应/不可达”等不同性质，是现场排查端口映射与防火墙的关键。
    /// </summary>
    public string Failure { get; init; } = "";

    public string FailureText => FailureKinds.Describe(Failure);

    /// <summary>所属统计会话号。点击“启动测试”或“清除”都会 +1，
    /// 用于丢弃变更之前发起、变更之后才返回的陈旧结果。</summary>
    public long Epoch { get; set; }

    /// <summary>
    /// 探测序号：按“链路 + 协议”单调递增，从 1 开始。
    /// 该序号会随探测包一起发到对端（报文格式 <c>序号|探测ID</c>），
    /// 因此界面详单、CSV 日志与抓包可以一一对应。
    /// 序号出现跳号 = 该次探测的结果未被记录（通常是停止/清除时被丢弃）。
    /// </summary>
    public long Seq { get; init; }

    /// <summary>探测 ID（32 位十六进制 GUID），随探测包发出，用于校验回包是否属于本次探测。</summary>
    public string ProbeId { get; init; } = "";

    /// <summary>
    /// 对端回显的“它看到的本端源地址”（ip:port）。
    /// 跨公网时这就是本端出口的公网 IP 与 NAT 映射后的源端口；
    /// 对端是老版本、或该次探测丢包时为空字符串。
    /// 注意：它可能随每次探测变化（多 WAN 出口/运营商 NAT 池），因此逐条记录才有意义。
    /// </summary>
    public string SeenFrom { get; init; } = "";

    public string SeenFromText => string.IsNullOrEmpty(SeenFrom) ? "-" : SeenFrom;

    public string Key => $"{LinkName}|{Host}|{Port}|{Protocol}";
    public string Target => $"{Host}:{Port}";
    public string Info => Lost ? FailureText : $"{RttMs ?? 0:F1} ms";
    public string TimeText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string RttText => RttMs.HasValue ? RttMs.Value.ToString("F1") : "";
    public string SetupText => SetupMs.HasValue ? SetupMs.Value.ToString("F1") : "";
}

/// <summary>
/// 链路编辑器的一行。任何字段变化都会重新校验，界面据此标红并显示原因。
///
/// 端口用字符串承载：这样非法输入（例如 "abc"）也能进入模型并被校验标红，
/// 不会出现"绑定转换失败、界面留着旧值、保存时静默写回旧端口"的情况。
/// </summary>
public sealed class LinkRow : INotifyPropertyChanged
{
    private string _name = "";
    private string _host = "";
    private string _portText = "";
    private bool _tcp = true;
    private bool _udp = true;
    private bool _globalTcp = true;
    private bool _globalUdp = true;
    private string _problem = "";
    private bool _isValid = true;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public string PortText { get => _portText; set => Set(ref _portText, value); }

    public bool Tcp
    {
        get => _tcp;
        set { if (Set(ref _tcp, value)) Raise(nameof(Effective)); }
    }

    public bool Udp
    {
        get => _udp;
        set { if (Set(ref _udp, value)) Raise(nameof(Effective)); }
    }

    /// <summary>
    /// 本行**实际会发出**的协议 = 界面上的全局协议勾选 ∩ 本行自己的勾选。
    /// 全局开关只能收紧、不能放宽：全局取消勾选的协议，本行勾了也不会发。
    /// </summary>
    public string Effective
    {
        get
        {
            var parts = new List<string>(2);
            if (_globalTcp && Tcp) parts.Add("TCP");
            if (_globalUdp && Udp) parts.Add("UDP");
            return parts.Count == 0 ? "不发包" : string.Join(" + ", parts);
        }
    }

    /// <summary>全局协议勾选变化时由界面推送进来，用于刷新 <see cref="Effective"/>。</summary>
    public void SetGlobalProtocols(bool tcp, bool udp)
    {
        if (_globalTcp == tcp && _globalUdp == udp) return;
        _globalTcp = tcp;
        _globalUdp = udp;
        Raise(nameof(Effective));
    }

    /// <summary>校验失败的原因（空字符串 = 通过）。</summary>
    public string Problem { get => _problem; private set => Set(ref _problem, value); }

    public bool IsValid { get => _isValid; private set => Set(ref _isValid, value); }

    public int Port => int.TryParse(PortText?.Trim(), out var port) ? port : 0;

    public string Target => $"{Host.Trim()}:{PortText?.Trim()}";

    public string Display => string.IsNullOrWhiteSpace(Name) ? Target : $"{Name.Trim()}（{Target}）";

    public LinkConfig ToConfig() => new()
    {
        Name = Name.Trim(),
        Host = Host.Trim(),
        Port = Port,
        Tcp = Tcp,
        Udp = Udp
    };

    public static LinkRow FromConfig(LinkConfig link) => new()
    {
        Name = link.Name,
        Host = link.Host,
        PortText = link.Port.ToString(),
        Tcp = link.Tcp,
        Udp = link.Udp
    };

    /// <summary>用与配置文件校验完全相同的规则校验本行。</summary>
    public bool Revalidate()
    {
        string problem;

        if (!int.TryParse(PortText?.Trim(), out var port))
            problem = "端口必须是整数";
        else
            problem = ConfigManager.ValidateLink(ToConfig(), out var message) ? "" : message;

        Problem = problem;
        IsValid = problem.Length == 0;
        return IsValid;
    }

    /// <summary>供跨行校验（例如重复链路）写入额外问题。</summary>
    public void SetProblem(string problem)
    {
        Problem = problem;
        IsValid = problem.Length == 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 统计表格的一行。属性可通知变更，使界面可以就地刷新数值，
/// 而不必清空集合重建（避免 DataGrid 闪烁与选中项丢失）。
/// </summary>
public sealed class StatRow : INotifyPropertyChanged
{
    private string _avg = "";
    private string _p50 = "";
    private string _p95 = "";
    private string _p99 = "";
    private string _lossRate = "";
    private string _status = "";
    private string _seenFrom = "";
    private string _lastFailure = "-";
    private int _sent;
    private int _lost;
    private int _consecutiveLoss;

    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Target { get; init; } = "";

    public string Avg { get => _avg; set => Set(ref _avg, value); }
    public string P50 { get => _p50; set => Set(ref _p50, value); }
    public string P95 { get => _p95; set => Set(ref _p95, value); }
    public string P99 { get => _p99; set => Set(ref _p99, value); }
    public int Sent { get => _sent; set => Set(ref _sent, value); }
    public int Lost { get => _lost; set => Set(ref _lost, value); }
    public string LossRate { get => _lossRate; set => Set(ref _lossRate, value); }
    public int ConsecutiveLoss { get => _consecutiveLoss; set => Set(ref _consecutiveLoss, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>最近一次成功探测里，对端看到的本端源地址。</summary>
    public string SeenFrom { get => _seenFrom; set => Set(ref _seenFrom, value); }

    /// <summary>当前（最近一次）失败原因；一旦探测成功就恢复为 “-”。</summary>
    public string LastFailure { get => _lastFailure; set => Set(ref _lastFailure, value); }

    public void CopyFrom(StatRow other)
    {
        Avg = other.Avg;
        P50 = other.P50;
        P95 = other.P95;
        P99 = other.P99;
        Sent = other.Sent;
        Lost = other.Lost;
        LossRate = other.LossRate;
        ConsecutiveLoss = other.ConsecutiveLoss;
        Status = other.Status;
        SeenFrom = other.SeenFrom;
        LastFailure = other.LastFailure;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
