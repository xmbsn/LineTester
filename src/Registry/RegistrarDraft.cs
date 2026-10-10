using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace LineTester.Registry;

/// <summary>
/// 「注册中心」页的编辑草稿（规格 §11.1）。
///
/// 与 <see cref="LinkRow"/> 同一个套路：数字字段用**字符串**承载，非法输入留在界面上被标红，
/// 而不是"绑定失败 → 静默丢弃 → 保存时把旧值写回去"。
/// 校验规则与 <see cref="ConfigManager.ValidateRegistrar"/> 保持一致（未启用时不校验空字段），
/// 但错误消息面向现场运维，且一次列出全部问题。
///
/// 这个类**不引用任何 WPF 类型**：界面只是它的外壳，因此可以在零依赖自检里直接断言往返与校验。
/// </summary>
public sealed class RegistrarDraft
{
    // ---- 本节点 ----
    public bool Enabled { get; set; }
    public bool ServeAsCenter { get; set; }
    public string NodeName { get; set; } = "";

    /// <summary>主外网地址（界面上第一个）。</summary>
    public string PublicHost { get; set; } = "";

    /// <summary>其余外网地址（同一机房多个出口 IP）。</summary>
    public List<string> PublicHosts { get; set; } = new();

    /// <summary>其余外网端点；旧配置里的 PublicHosts 会按本端主端口迁移。</summary>
    public List<ExternalEndpointDraft> AdditionalEndpoints { get; set; } = new();

    private IEnumerable<ExternalEndpointDraft> EffectiveAdditionalEndpoints
        => AdditionalEndpoints.Count > 0
            ? AdditionalEndpoints
            : PublicHosts.Select(host => new ExternalEndpointDraft(host, PublicPortText));

    /// <summary>全部对外地址（主地址在前，去重）。</summary>
    public List<string> AllPublicHosts
    {
        get
        {
            var list = new List<string>(4);
            foreach (var host in new[] { PublicHost }
                .Concat(EffectiveAdditionalEndpoints.Select(endpoint => endpoint.Ip))
                .Concat(PublicHosts))
            {
                var value = host?.Trim() ?? "";
                if (value.Length > 0 && !list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
            }
            return list;
        }
    }

    public string PublicPortText { get; set; } = "";
    public bool Distributable { get; set; } = true;
    public bool AdvertiseTcp { get; set; } = true;
    public bool AdvertiseUdp { get; set; } = true;
    public bool AdvertiseIcmp { get; set; }

    // ---- 注册中心 ----
    public string CenterHost { get; set; } = "";
    public string CenterPortText { get; set; } = "";
    public string Secret { get; set; } = "";
    public string HeartbeatText { get; set; } = "";
    public string SyncText { get; set; } = "";
    public string RetryText { get; set; } = "";
    public string MaxRetriesText { get; set; } = "";

    /// <summary>节点身份（界面上只读显示）。写回时以它为准：为空且已启用才生成新的。</summary>
    public string NodeId { get; set; } = "";

    public static RegistrarDraft From(RegistrarConfig config) => new()
    {
        Enabled = config.Enabled,
        ServeAsCenter = config.ServeAsCenter,
        NodeName = config.NodeName,
        PublicHost = config.PublicHost,
        PublicHosts = config.PublicHosts?.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).ToList() ?? new List<string>(),
        PublicPortText = config.PublicPort > 0 ? config.PublicPort.ToString(CultureInfo.InvariantCulture) : "",
        AdditionalEndpoints = (config.AdditionalEndpoints is { Count: > 0 }
                ? config.AdditionalEndpoints
                : (config.PublicHosts ?? new List<string>())
                    .Select(host => new ExternalEndpoint { Ip = host, Port = config.PublicPort }).ToList())
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Ip))
            .Select(endpoint => new ExternalEndpointDraft(endpoint.Ip.Trim(),
                endpoint.Port > 0 ? endpoint.Port.ToString(CultureInfo.InvariantCulture) : ""))
            .ToList(),
        Distributable = config.Distributable,
        AdvertiseTcp = config.AdvertiseTcp,
        AdvertiseUdp = config.AdvertiseUdp,
        AdvertiseIcmp = config.AdvertiseIcmp,
        CenterHost = config.CenterHost,
        CenterPortText = config.CenterPort > 0 ? config.CenterPort.ToString(CultureInfo.InvariantCulture) : "",
        Secret = config.Secret,
        HeartbeatText = config.HeartbeatSeconds.ToString(CultureInfo.InvariantCulture),
        SyncText = config.SyncSeconds.ToString(CultureInfo.InvariantCulture),
        RetryText = config.RetryMs.ToString(CultureInfo.InvariantCulture),
        MaxRetriesText = config.MaxRetries.ToString(CultureInfo.InvariantCulture),
        NodeId = config.NodeId
    };

    /// <summary>
    /// 写回配置。校验不通过返回 false 且不改动任何字段（避免"改了一半"的配置被落盘）。
    /// 首次启用时生成节点身份：身份丢失会被中心当成一台新节点。
    /// </summary>
    public bool TryApply(RegistrarConfig config, out string error)
    {
        error = Validate();
        if (error.Length > 0) return false;

        config.Enabled = Enabled;
        config.ServeAsCenter = ServeAsCenter;
        config.NodeName = NodeName.Trim();
        config.PublicHost = PublicHost.Trim();
        var endpoints = EffectiveAdditionalEndpoints.ToList();
        config.PublicHosts = endpoints
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Ip))
            .Select(endpoint => endpoint.Ip.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.AdditionalEndpoints = endpoints
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Ip))
            .Select(endpoint => new ExternalEndpoint { Ip = endpoint.Ip.Trim(), Port = ParseInt(endpoint.PortText) })
            .ToList();
        config.PublicPort = ParseInt(PublicPortText);
        config.Distributable = Distributable;
        config.AdvertiseTcp = AdvertiseTcp;
        config.AdvertiseUdp = AdvertiseUdp;
        config.AdvertiseIcmp = AdvertiseIcmp;
        config.CenterHost = CenterHost.Trim();
        config.CenterPort = ParseInt(CenterPortText);
        config.Secret = Secret;
        config.HeartbeatSeconds = ParseInt(HeartbeatText);
        config.SyncSeconds = ParseInt(SyncText);
        config.RetryMs = ParseInt(RetryText);
        config.MaxRetries = ParseInt(MaxRetriesText);

        // 节点身份：草稿里有就用草稿的（它来自被编辑的那份配置），绝不因为是"新对象"就重新生成
        // —— 身份一变，中心就会把同一台机器当成新节点，旧链路要等 TTL 才消失。
        var nodeId = string.IsNullOrWhiteSpace(NodeId) ? config.NodeId : NodeId.Trim();
        if (config.Enabled && string.IsNullOrWhiteSpace(nodeId)) nodeId = RegistrarConfig.NewNodeId();

        config.NodeId = nodeId;
        NodeId = nodeId;
        return true;
    }

    /// <summary>返回全部问题的中文说明（空字符串 = 通过）。未启用注册时不做任何校验。</summary>
    public string Validate()
    {
        if (!Enabled) return "";

        var problems = new List<string>(8);

        if (string.IsNullOrWhiteSpace(NodeName))
            problems.Add("请填写节点名称（也是其他节点上自动生成的链路名）");

        // 本端外网地址/端口只有"参与网状"（会被其他节点探测）时才必须提供：
        // 它描述的是**其他节点怎么连到本机**（机房映射进来的那个地址与端口），
        // 与"注册中心在哪里"是两回事——现场最容易把这两个端口填混。
        if (Distributable)
        {
            var hosts = AllPublicHosts;
            if (hosts.Count == 0)
            {
                problems.Add("请填写本端外网地址（其他节点连本机用的地址，出方向 NAT 源端口 ≠ 入方向映射端口，" +
                             "必须手工填写）。如果本机只想测别人、不需要被别人测，请取消勾选「参与网状」");
            }
            else
            {
                // 每个地址都要合法：其中一个写错，其他节点就会照着错地址建链路
                foreach (var host in hosts)
                {
                    var hostProblem = DescribeHostProblem("本端外网地址", host, required: true);
                    if (hostProblem.Length > 0) problems.Add(hostProblem);
                }
            }

            var portProblem = DescribePortProblem("本端外网端口", PublicPortText);
            if (portProblem.Length > 0) problems.Add(portProblem);
            ValidateAdditionalEndpoints(problems, required: true);
        }
        else if (AllPublicHosts.Count > 0 || !string.IsNullOrWhiteSpace(PublicPortText))
        {
            // 不参与网状时地址可以留空；但填了就要填对，否则会让人以为"改了已经生效"
            foreach (var host in AllPublicHosts)
            {
                var hostProblem = DescribeHostProblem("本端外网地址", host, required: false);
                if (hostProblem.Length > 0) problems.Add(hostProblem);
            }

            var portProblem = DescribePortProblem("本端外网端口", PublicPortText, required: false);
            if (portProblem.Length > 0) problems.Add(portProblem);
            ValidateAdditionalEndpoints(problems, required: false);
        }

        var centerHostProblem = DescribeHostProblem("注册中心地址", CenterHost, required: false);
        if (CenterHost.Trim().Length == 0)
            problems.Add("请填写注册中心地址（注册中心映射出来的那个地址）");
        else if (centerHostProblem.Length > 0)
            problems.Add(centerHostProblem);
        else
        {
            var centerProblem = DescribePortProblem("注册中心端口", CenterPortText);
            if (centerProblem.Length > 0) problems.Add(centerProblem);
        }

        if (!IsInRange(HeartbeatText, RegistrarConfig.MinIntervalSeconds, RegistrarConfig.MaxIntervalSeconds))
            problems.Add($"心跳周期必须是 {RegistrarConfig.MinIntervalSeconds}~{RegistrarConfig.MaxIntervalSeconds} 秒的整数（当前填写的是“{Text(HeartbeatText)}”）");
        if (!IsInRange(SyncText, RegistrarConfig.MinIntervalSeconds, RegistrarConfig.MaxIntervalSeconds))
            problems.Add($"同步周期必须是 {RegistrarConfig.MinIntervalSeconds}~{RegistrarConfig.MaxIntervalSeconds} 秒的整数（当前填写的是“{Text(SyncText)}”）");
        if (!IsInRange(RetryText, RegistrarConfig.MinRetryMs, RegistrarConfig.MaxRetryMs))
            problems.Add($"重传间隔必须是 {RegistrarConfig.MinRetryMs}~{RegistrarConfig.MaxRetryMs} 毫秒的整数（当前填写的是“{Text(RetryText)}”）");
        if (!IsInRange(MaxRetriesText, 0, RegistrarConfig.MaxRetriesLimit))
            problems.Add($"最大重传次数必须是 0~{RegistrarConfig.MaxRetriesLimit} 的整数（当前填写的是“{Text(MaxRetriesText)}”）");

        return string.Join("；", problems);
    }

    private void ValidateAdditionalEndpoints(List<string> problems, bool required)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primary = PublicHost?.Trim() ?? "";
        if (primary.Length > 0) seen.Add(primary);
        foreach (var endpoint in EffectiveAdditionalEndpoints)
        {
            var ip = endpoint.Ip?.Trim() ?? "";
            if (ip.Length == 0 && string.IsNullOrWhiteSpace(endpoint.PortText)) continue;

            var hostProblem = DescribeHostProblem("已添加外网地址", ip, required);
            if (hostProblem.Length > 0) problems.Add(hostProblem);

            var portProblem = DescribePortProblem($"外网地址 {ip} 的端口", endpoint.PortText, required);
            if (portProblem.Length > 0) problems.Add(portProblem);

            if (ip.Length > 0 && !seen.Add(ip))
                problems.Add($"外网地址 {ip} 重复；一个 IP 只能添加一次");
        }
    }

    /// <summary>
    /// 地址问题的可读说明（空字符串 = 通过）。
    ///
    /// 光判"非空"是不够的：`203.0.113`（少一段）、`203.0.113.999`（越界）、`203,0,113,9`（逗号）
    /// 都会被当成"填好了"，直到运行时解析失败才暴露，而且现象是"链路全部不通"，极难定位。
    /// IPv4 形状的串必须真的是合法 IPv4；域名按"标签.标签"的形状校验。
    /// </summary>
    private static string DescribeHostProblem(string field, string? host, bool required)
    {
        var value = host?.Trim() ?? "";
        if (value.Length == 0) return required ? $"{field}未填写" : "";

        // 只由数字和点组成 → 本来想填 IPv4，必须严格是四段（0~255）
        //
        // 注意：这里**不能**用 IPAddress.TryParse 判定。.NET 会把 "203.0.113" 解析成
        // 203.0.0.113、"0x7f.1" 解析成 127.0.0.1（历史遗留的宽松形式）——用它判定等于放行
        // "少一段"的地址，而那在运行时的表现是"链路全部不通"，最难查。
        if (value.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            var parts = value.Split('.');
            var ok = parts.Length == 4 && parts.All(part =>
                part.Length is > 0 and <= 3 &&
                part.All(char.IsAsciiDigit) &&
                int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var octet) &&
                octet is >= 0 and <= 255);

            return ok
                ? ""
                : $"{field}不是合法的 IPv4 地址（当前填写的是“{Preview(value)}”，" +
                  "需要四段且每段 0~255，例如 203.0.113.9）";
        }

        if (value.Contains(',') || value.Contains('：') || value.Contains('。') || value.Contains(' '))
            return $"{field}含有非法字符（当前填写的是“{Preview(value)}”，请用半角英文的 . 与 : ）";

        // IPv6 字面量：协议层允许（双栈），此处不拦
        if (IPAddress.TryParse(value, out var literal) &&
            literal.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return "";
        }

        // 全角数字/字母混在里面（"２０３.0.113.9"）→ 明确说出来
        if (value.Any(c => char.IsDigit(c) && !char.IsAsciiDigit(c)))
            return $"{field}含全角数字（当前填写的是“{Preview(value)}”，请切到半角输入）";

        // 域名形状：标签由字母/数字/连字符组成，允许点分多段
        var labels = value.Split('.');
        var hostnameOk = labels.Length >= 1 && labels.All(label =>
            label.Length is > 0 and <= 63 &&
            label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'));

        return hostnameOk
            ? ""
            : $"{field}格式不正确（当前填写的是“{Preview(value)}”）";
    }

    /// <summary>
    /// 端口问题的可读说明（空字符串 = 通过）。
    ///
    /// 必须把**当前填的内容**回显出来：现场最容易出现的不是"填了 16298 被判错"，
    /// 而是"这个框其实是空的/混进了全角字符"，而原来的提示只会说"必须是 1~65535"，
    /// 让人以为程序算错了。
    /// </summary>
    private static string DescribePortProblem(string field, string? text, bool required = true)
    {
        var raw = text ?? "";
        var trimmed = raw.Trim();

        if (trimmed.Length == 0)
            return required ? $"{field}未填写（必须是 1~65535 的整数）" : "";

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return $"{field}必须是 1~65535 的整数（当前填写的是“{Preview(trimmed)}”，" +
                   "请检查是否混入了空格、全角数字或其它字符）";
        }

        return value is < 1 or > 65535
            ? $"{field}必须在 1~65535 之间（当前填写的是 {value}）"
            : "";
    }

    private static string Text(string? text) => Preview(text?.Trim() ?? "");

    private static string Preview(string text) => text.Length <= 12 ? text : text[..12] + "…";

    private static int ParseInt(string? text)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static bool IsInRange(string? text, int min, int max)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return false;
        return value >= min && value <= max;
    }
}

/// <summary>注册页外网地址列表中的可编辑行。</summary>
public sealed class ExternalEndpointDraft
{
    public ExternalEndpointDraft() { }

    public ExternalEndpointDraft(string ip, string portText)
    {
        Ip = ip;
        PortText = portText;
    }

    public string Ip { get; set; } = "";
    public string PortText { get; set; } = "";
}
