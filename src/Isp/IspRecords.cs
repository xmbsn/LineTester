using System.Text.Json.Serialization;

namespace LineTester.Isp;

/// <summary>运营商大类。判定"同运营商/跨运营商"时比的就是它。</summary>
public enum CarrierKind
{
    /// <summary>查不到、或数据自相矛盾，不敢下结论。</summary>
    Unknown,

    /// <summary>中国电信（含 CHINANET）。</summary>
    Telecom,

    /// <summary>中国联通（含老网通 CNC / CHINA169）。</summary>
    Unicom,

    /// <summary>中国移动（含 CMNET）。</summary>
    Mobile,

    /// <summary>中国广电。</summary>
    Cable,

    /// <summary>教育网 CERNET。</summary>
    Education,

    /// <summary>云厂商 / IDC（阿里云、腾讯云、AWS…）：不是三网之一，但同 AS 仍算同一张网。</summary>
    Cloud,

    /// <summary>境外运营商。</summary>
    Overseas,

    /// <summary>内网 / 保留地址（ip-api 会明确回 private range）。</summary>
    Private
}

/// <summary>跨网结论（决定状态灯颜色）。</summary>
public enum CrossNetState
{
    /// <summary>数据不足，不判定（灰灯）。</summary>
    Unknown,

    /// <summary>同运营商 / 同一张网（绿灯）。</summary>
    Same,

    /// <summary>跨运营商 / 跨网（红灯）。</summary>
    Cross,

    /// <summary>功能没启用（不显示灯）。</summary>
    Disabled
}

/// <summary>
/// 一个 IP 的运营商信息：ip-api 返回字段的子集 + 本地归类结果。
/// 只存必要的字段，缓存文件因此很小、也便于人工查看。
/// </summary>
public sealed class IspRecord
{
    public string Ip { get; set; } = "";

    /// <summary>success / fail（fail 时 <see cref="Message"/> 说明原因）。</summary>
    public string Status { get; set; } = "";

    public string Message { get; set; } = "";
    public string Country { get; set; } = "";
    public string Isp { get; set; } = "";
    public string Org { get; set; } = "";

    /// <summary>形如 <c>AS4812 China Telecom (Group)</c>。</summary>
    public string As { get; set; } = "";

    public string AsName { get; set; } = "";

    public DateTime FetchedUtc { get; set; }

    [JsonIgnore]
    public bool Ok => string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);

    /// <summary>AS 号（<c>AS4812 China Telecom</c> → <c>AS4812</c>）；取不到为空串。</summary>
    [JsonIgnore]
    public string AsNumber
    {
        get
        {
            var value = As?.Trim() ?? "";
            if (value.Length == 0) return "";

            var token = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return token is not null && token.StartsWith("AS", StringComparison.OrdinalIgnoreCase)
                ? token.ToUpperInvariant()
                : "";
        }
    }

    /// <summary>查不到时用的一句人话。</summary>
    [JsonIgnore]
    public string FailText => Ok
        ? ""
        : Message switch
        {
            "private range" => "内网地址",
            "reserved range" => "保留地址",
            "invalid query" => "地址无效",
            "" => "无数据",
            _ => Message
        };
}

/// <summary>跨网判定结果（文案 + 明细，供表格与提示框使用）。</summary>
public sealed record CrossNetVerdict(CrossNetState State, string Short, string Detail)
{
    public bool IsCross => State == CrossNetState.Cross;
    public bool IsSame => State == CrossNetState.Same;
}

/// <summary>一次抓取的返回：结果 + 限流状态。</summary>
public sealed class IspFetchResult
{
    public List<IspRecord> Records { get; init; } = new();

    /// <summary>请求是否成功发出并解析（false 时看 <see cref="Error"/>）。</summary>
    public bool Ok { get; init; } = true;

    public string? Error { get; init; }

    /// <summary>响应头 <c>X-Rl</c>：当前窗口剩余请求数；-1 表示未知。</summary>
    public int Remaining { get; init; } = -1;

    /// <summary>响应头 <c>X-Ttl</c>：窗口重置秒数；-1 表示未知。</summary>
    public int ResetAfterSeconds { get; init; } = -1;

    public static IspFetchResult Failure(string error) => new() { Ok = false, Error = error };
}

/// <summary>查询服务的对外状态（界面/状态栏用）。</summary>
public sealed class IspLookupStatus
{
    public bool Enabled { get; set; } = true;

    /// <summary>缓存里已有多少条记录。</summary>
    public int Cached { get; set; }

    /// <summary>还在等结果的地址数。</summary>
    public int Pending { get; set; }

    /// <summary>
    /// 累计登记过多少次查询（只增不减）。
    /// 「登记」和「拿到结果」之间隔着最多 1 秒的批次等待，
    /// 所以刚启动时 Pending 可能已归零、Cached 还是 0——只有这个数能回答"到底登记上没有"。
    /// </summary>
    public int Requested { get; set; }

    public DateTime? LastSuccessUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>被限流时的解除时刻（UTC）。</summary>
    public DateTime? ThrottledUntilUtc { get; set; }

    public bool Throttled => ThrottledUntilUtc is not null && ThrottledUntilUtc > DateTime.UtcNow;
}
