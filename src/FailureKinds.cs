namespace LineTester;

/// <summary>
/// 一次探测的失败原因。
///
/// token 是稳定的英文标识，写入 CSV（便于脚本聚合）；
/// 界面显示 <see cref="Describe"/> 的中文说明。
///
/// 这些区别在现场排查时是关键信息，例如：
/// <list type="bullet">
/// <item><c>refused</c>：对端有响应（回了 RST）→ 网络路径与端口映射都是通的，只是该端口没有程序在监听。</item>
/// <item><c>timeout</c>：完全没有响应 → 包在到达监听者之前就被丢弃（映射缺失/ACL/防火墙/运营商）。</item>
/// <item><c>icmp_unreachable</c>：UDP 收到 ICMP 端口不可达 → 包已到达对端主机，只是该端口没人监听。</item>
/// </list>
/// </summary>
public static class FailureKinds
{
    /// <summary>成功（没有失败原因）。</summary>
    public const string None = "";

    /// <summary>无响应（超时）。</summary>
    public const string Timeout = "timeout";

    /// <summary>连接被拒绝（收到 RST）。</summary>
    public const string Refused = "refused";

    /// <summary>网络或主机不可达。</summary>
    public const string Unreachable = "unreachable";

    /// <summary>连接被重置。</summary>
    public const string Reset = "reset";

    /// <summary>UDP 收到 ICMP 端口不可达。</summary>
    public const string IcmpUnreachable = "icmp_unreachable";

    /// <summary>主机名解析失败。</summary>
    public const string HostNotFound = "host_not_found";

    /// <summary>对端提前关闭连接。</summary>
    public const string ClosedEarly = "closed";

    /// <summary>回包内容不匹配。</summary>
    public const string PayloadMismatch = "mismatch";

    /// <summary>其他错误。</summary>
    public const string Other = "error";

    public static string Describe(string? kind) => kind switch
    {
        Timeout => "无响应（超时）",
        Refused => "连接被拒绝（端口无监听）",
        Unreachable => "网络/主机不可达",
        Reset => "连接被重置",
        IcmpUnreachable => "UDP 端口不可达（收到 ICMP）",
        HostNotFound => "主机名解析失败",
        ClosedEarly => "对端提前关闭连接",
        PayloadMismatch => "回包内容不匹配",
        Other => "其他错误",
        _ => "超时/丢包"
    };

    /// <summary>
    /// 带上协议上下文的说明。ICMP 需要区分一件在现场极常见、但语义完全不同的事：
    /// **对端防火墙禁 ping** 与**线路真的不通**在协议层面都表现为"无响应"，
    /// 所以 ICMP 的超时要额外提示"对端可能禁 ping"，避免把安全策略判成线路故障。
    /// </summary>
    public static string Describe(string? kind, string? protocol)
    {
        if (string.Equals(protocol, "ICMP", StringComparison.OrdinalIgnoreCase))
        {
            return kind switch
            {
                Timeout => "无响应（超时；对端可能禁 ping 或线路不通）",
                IcmpUnreachable => "目标不可达（收到 ICMP 不可达）",
                Unreachable => "网络/主机不可达",
                _ => Describe(kind)
            };
        }

        return Describe(kind);
    }
}
