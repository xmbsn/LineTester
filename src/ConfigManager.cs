using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LineTester;

public static class ConfigManager
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 不把中文转义成 \uXXXX，保证现场可以直接用记事本编辑
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>仅测试使用：覆盖配置文件路径。</summary>
    public static string? OverridePath { get; set; }

    public static string ConfigPath =>
        OverridePath ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>文件指纹，用于检测被外部程序修改。</summary>
    public static (DateTime WriteUtc, long Length)? Stamp()
    {
        try
        {
            var info = new FileInfo(ConfigPath);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 读取配置。不会抛异常，也不会因为解析失败而覆盖原文件。
    /// 返回 false 时 <paramref name="error"/> 说明原因，界面应提示用户且不要自动保存。
    /// </summary>
    public static AppConfig Load(out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(ConfigPath))
            {
                var fresh = AppConfig.Default();
                try
                {
                    Save(fresh);
                }
                catch (Exception ex)
                {
                    error = $"无法创建配置文件：{ex.Message}";
                }
                return fresh;
            }

            var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "配置文件内容为空。";
                return AppConfig.Default();
            }

            AppConfig? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<AppConfig>(json, Options);
            }
            catch (JsonException ex)
            {
                error = $"配置文件 JSON 格式错误（第 {ex.LineNumber + 1} 行）：{ex.Message}";
                return AppConfig.Default();
            }

            if (parsed is null)
            {
                error = "配置文件反序列化结果为空。";
                return AppConfig.Default();
            }

            parsed.Links ??= new List<LinkConfig>();

            if (!Validate(parsed, out var detail))
                error = "配置参数无效：" + detail;

            return parsed;
        }
        catch (Exception ex)
        {
            error = $"读取配置文件失败：{ex.Message}";
            return AppConfig.Default();
        }
    }

    /// <summary>原子写入：先写临时文件再替换，避免写一半崩溃导致配置损坏。</summary>
    public static void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, Options);
        AtomicFile.WriteAllText(ConfigPath, json);
    }

    /// <summary>
    /// 单条链路的校验规则。链路编辑器与配置文件校验共用这一份规则，
    /// 避免界面和文件两条路径的判定不一致。
    ///
    /// 注意：TCP 与 UDP **都未勾选是合法的**——表示这条链路不主动探测，
    /// 常用于纯服务端节点（只监听应答），或临时把某条链路停测而不删除配置。
    /// </summary>
    public static bool ValidateLink(LinkConfig link, out string error)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(link.Name)) problems.Add("未填写名称");
        if (string.IsNullOrWhiteSpace(link.Host)) problems.Add("未填写主机/IP");
        if (link.Port < 1 || link.Port > 65535) problems.Add("端口必须在 1~65535 之间");

        error = string.Join("；", problems);
        return problems.Count == 0;
    }

    public static bool Validate(AppConfig config, out string error)
    {
        var problems = new List<string>();

        if (config.ListenPort < 1 || config.ListenPort > 65535)
            problems.Add("ListenPort 必须在 1~65535 之间");
        if (config.IntervalMs < 50)
            problems.Add("IntervalMs 不能小于 50");
        if (config.TimeoutMs < 1)
            problems.Add("TimeoutMs 必须大于 0");
        if (config.MaxConcurrentProbes is < 1 or > 1024)
            problems.Add("MaxConcurrentProbes 必须在 1~1024 之间");
        if (config.LogRetentionDays is < 0 or > 3650)
            problems.Add("LogRetentionDays 必须在 0~3650 之间（0 表示永久保留）");
        if (config.StreamPacketMinBytes < StreamPacket.MinBytes || config.StreamPacketMinBytes > StreamPacket.MaxBytes)
            problems.Add($"StreamPacketMinBytes 必须在 {StreamPacket.MinBytes}~{StreamPacket.MaxBytes} 之间");
        if (config.StreamPacketMaxBytes < StreamPacket.MinBytes || config.StreamPacketMaxBytes > StreamPacket.MaxBytes)
            problems.Add($"StreamPacketMaxBytes 必须在 {StreamPacket.MinBytes}~{StreamPacket.MaxBytes} 之间");
        if (config.StreamPacketMinBytes > config.StreamPacketMaxBytes)
            problems.Add("StreamPacketMinBytes 不能大于 StreamPacketMaxBytes");
        if (config.WarningRttMs < 0)
            problems.Add("WarningRttMs 不能为负数");
        if (config.StatWindowMinutes < 1)
            problems.Add("StatWindowMinutes 不能小于 1");
        if (config.LossWarnPercent < 0)
            problems.Add("LossWarnPercent 不能为负数");
        if (config.ConsecutiveLossWarn < 1)
            problems.Add("ConsecutiveLossWarn 不能小于 1");

        ValidateRegistrar(config.Registrar, problems);
        ValidateIsp(config.Isp, problems);

        if (config.Links is not null)
        {
            for (var i = 0; i < config.Links.Count; i++)
            {
                var link = config.Links[i];
                var label = string.IsNullOrWhiteSpace(link.Name) ? $"Links[{i}]" : $"Links[{i}]({link.Name})";

                if (!LinkSources.IsKnown(link.Source))
                    problems.Add($"{label} 的 Source 只能是 {LinkSources.Manual} 或 {LinkSources.Registry}");

                if (!ValidateLink(link, out var linkError))
                    problems.Add($"{label}：{linkError}");
            }
        }

        error = string.Join("；", problems);
        return problems.Count == 0;
    }

    /// <summary>
    /// 运营商查询校验：只在启用时校验（未启用时字段留空/默认值都不该拦住保存）。
    /// </summary>
    private static void ValidateIsp(IspConfig? isp, List<string> problems)
    {
        if (isp is null || !isp.Enabled) return;

        if (string.IsNullOrWhiteSpace(isp.Endpoint) ||
            !(isp.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              isp.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("Isp.Endpoint 必须是 http:// 或 https:// 开头的地址");
        }

        if (isp.CacheDays < IspConfig.MinCacheDays || isp.CacheDays > IspConfig.MaxCacheDays)
            problems.Add($"Isp.CacheDays 必须在 {IspConfig.MinCacheDays}~{IspConfig.MaxCacheDays} 之间（当前是 {isp.CacheDays}）");

        if (isp.TimeoutMs < IspConfig.MinTimeoutMs || isp.TimeoutMs > IspConfig.MaxTimeoutMs)
            problems.Add($"Isp.TimeoutMs 必须在 {IspConfig.MinTimeoutMs}~{IspConfig.MaxTimeoutMs} 之间（当前是 {isp.TimeoutMs}）");
    }

    /// <summary>
    /// 注册配置校验（规格 §4.1）。只在 Enabled 时校验：
    /// 未启用注册的节点不该因为空字段被拒绝保存。
    ///
    /// 本端外网地址/端口**只在参与网状时必须填**（它描述"其他节点怎么连本机"）：
    /// 纯测别人的客户端（<see cref="RegistrarConfig.Distributable"/>=false）不会被分发，
    /// 没有可被访问的地址也不影响它工作——不该被一条用不上的规则挡住。
    /// </summary>
    private static void ValidateRegistrar(RegistrarConfig? registrar, List<string> problems)
    {
        if (registrar is null || !registrar.Enabled) return;

        if (string.IsNullOrWhiteSpace(registrar.NodeName))
            problems.Add("Registrar.NodeName 不能为空");

        if (registrar.Distributable)
        {
            if (string.IsNullOrWhiteSpace(registrar.PublicHost))
                problems.Add("Registrar.PublicHost 不能为空（本端外网地址需手工填写；只想测别人可关闭 Distributable / 界面上的“参与网状”）");

            if (registrar.PublicPort < 1 || registrar.PublicPort > 65535)
                problems.Add($"Registrar.PublicPort 必须在 1~65535 之间（当前是 {registrar.PublicPort}）");

            // 多地址：每一个都要能分发（写错一个，其他节点就会照着错地址建链路）
            foreach (var host in registrar.PublicHosts ?? new List<string>())
            {
                var value = host?.Trim() ?? "";
                if (value.Length == 0) continue;

                if (!Registry.RegistryAddressRules.IsDistributable(value, out _, out var reason))
                    problems.Add($"Registrar.PublicHosts 里的 {value} 不可分发：{reason}");
            }

            foreach (var endpoint in registrar.AdditionalEndpoints ?? new List<Registry.ExternalEndpoint>())
            {
                var value = endpoint.Ip?.Trim() ?? "";
                if (value.Length > 0 && !Registry.RegistryAddressRules.IsDistributable(value, out _, out var reason))
                {
                    problems.Add($"Registrar.AdditionalEndpoints 里的 {value} 不可分发：{reason}");
                }
                if (endpoint.Port < 1 || endpoint.Port > 65535)
                    problems.Add($"Registrar.AdditionalEndpoints[{value}].Port 必须在 1~65535 之间（当前是 {endpoint.Port}）");
            }
        }
        else if (registrar.PublicPort != 0 && (registrar.PublicPort < 1 || registrar.PublicPort > 65535))
        {
            // 不参与网状时允许整段留空（0），但填了就要填对
            problems.Add($"Registrar.PublicPort 必须在 1~65535 之间（当前是 {registrar.PublicPort}）");
        }

        if (string.IsNullOrWhiteSpace(registrar.CenterHost))
            problems.Add("Registrar.CenterHost 不能为空");
        if (registrar.CenterPort < 1 || registrar.CenterPort > 65535)
            problems.Add($"Registrar.CenterPort 必须在 1~65535 之间（当前是 {registrar.CenterPort}）");
        if (string.IsNullOrWhiteSpace(registrar.Secret))
            problems.Add("Registrar.Secret 不能为空：启用注册同步必须配置相同的预共享密钥");

        if (registrar.HeartbeatSeconds < RegistrarConfig.MinIntervalSeconds ||
            registrar.HeartbeatSeconds > RegistrarConfig.MaxIntervalSeconds)
            problems.Add($"Registrar.HeartbeatSeconds 必须在 {RegistrarConfig.MinIntervalSeconds}~{RegistrarConfig.MaxIntervalSeconds} 之间");

        if (registrar.SyncSeconds < RegistrarConfig.MinIntervalSeconds ||
            registrar.SyncSeconds > RegistrarConfig.MaxIntervalSeconds)
            problems.Add($"Registrar.SyncSeconds 必须在 {RegistrarConfig.MinIntervalSeconds}~{RegistrarConfig.MaxIntervalSeconds} 之间");

        if (registrar.RetryMs < RegistrarConfig.MinRetryMs || registrar.RetryMs > RegistrarConfig.MaxRetryMs)
            problems.Add($"Registrar.RetryMs 必须在 {RegistrarConfig.MinRetryMs}~{RegistrarConfig.MaxRetryMs} 之间");

        if (registrar.MaxRetries < 0 || registrar.MaxRetries > RegistrarConfig.MaxRetriesLimit)
            problems.Add($"Registrar.MaxRetries 必须在 0~{RegistrarConfig.MaxRetriesLimit} 之间");
    }
}
