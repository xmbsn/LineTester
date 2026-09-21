using System.Globalization;

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
    public string PublicHost { get; set; } = "";
    public string PublicPortText { get; set; } = "";
    public bool Distributable { get; set; } = true;
    public bool AdvertiseTcp { get; set; } = true;
    public bool AdvertiseUdp { get; set; } = true;

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
        PublicPortText = config.PublicPort > 0 ? config.PublicPort.ToString(CultureInfo.InvariantCulture) : "",
        Distributable = config.Distributable,
        AdvertiseTcp = config.AdvertiseTcp,
        AdvertiseUdp = config.AdvertiseUdp,
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
        config.PublicPort = ParseInt(PublicPortText);
        config.Distributable = Distributable;
        config.AdvertiseTcp = AdvertiseTcp;
        config.AdvertiseUdp = AdvertiseUdp;
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

        if (string.IsNullOrWhiteSpace(PublicHost))
            problems.Add("请填写本端外网地址：出方向 NAT 的源端口与入方向映射端口通常不同，" +
                         "程序无法自动推断，必须手工填写");
        else if (!IsValidPort(PublicPortText))
            problems.Add("本端外网端口必须是 1~65535 的整数");

        if (string.IsNullOrWhiteSpace(CenterHost))
            problems.Add("请填写注册中心地址");
        else if (!IsValidPort(CenterPortText))
            problems.Add("注册中心端口必须是 1~65535 的整数");

        if (!IsInRange(HeartbeatText, RegistrarConfig.MinIntervalSeconds, RegistrarConfig.MaxIntervalSeconds))
            problems.Add($"心跳周期必须是 {RegistrarConfig.MinIntervalSeconds}~{RegistrarConfig.MaxIntervalSeconds} 秒的整数");
        if (!IsInRange(SyncText, RegistrarConfig.MinIntervalSeconds, RegistrarConfig.MaxIntervalSeconds))
            problems.Add($"同步周期必须是 {RegistrarConfig.MinIntervalSeconds}~{RegistrarConfig.MaxIntervalSeconds} 秒的整数");
        if (!IsInRange(RetryText, RegistrarConfig.MinRetryMs, RegistrarConfig.MaxRetryMs))
            problems.Add($"重传间隔必须是 {RegistrarConfig.MinRetryMs}~{RegistrarConfig.MaxRetryMs} 毫秒的整数");
        if (!IsInRange(MaxRetriesText, 0, RegistrarConfig.MaxRetriesLimit))
            problems.Add($"最大重传次数必须是 0~{RegistrarConfig.MaxRetriesLimit} 的整数");

        return string.Join("；", problems);
    }

    private static int ParseInt(string? text)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static bool IsValidPort(string? text)
        => IsInRange(text, 1, 65535);

    private static bool IsInRange(string? text, int min, int max)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return false;
        return value >= min && value <= max;
    }
}
