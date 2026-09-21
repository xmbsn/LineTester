namespace LineTester.Isp;

/// <summary>
/// 运营商归类与"同/跨运营商"判定（规格见 README「运营商与跨网判定」）。
///
/// 为什么不能只比字符串、也不能只看单个字段——实测（2026-09，ip-api 免费接口）：
/// <list type="bullet">
/// <item>联通的老网通资源 <c>221.5.88.88</c> 的 isp 是 <c>CNC Group CHINA169 …</c>，
/// 里面既没有 "unicom" 也没有"联通" → 只按关键词"联通/Unicom"匹配会误判成"未知"。</item>
/// <item><c>114.114.114.114</c> 的 isp 说 <c>China Unicom</c>，而 as 说 <c>CHINATELECOM</c>——
/// **同一份返回里两个字段互相矛盾**，这种只能给"未知"，不能硬猜。</item>
/// <item>阿里/腾讯/百度是<b>云厂商自己的 AS</b>，归到"电信/联通/移动"里是错的。</item>
/// </list>
///
/// 因此规则是：
/// ① 按字段优先（isp → org → as → asname）取**唯一**能确定大类的那一个字段；
/// ② 大类相同 = 同运营商（绿灯）；都确定且不同 = 跨运营商（红灯）；
/// ③ 一侧是云/IDC/海外、或某侧大类不确定时，退化为比 **AS 号**（同 AS = 同一张网）；
/// ④ 都判不出来 = 未知（灰灯）+ 提示里给出原始字段，交给人判断。
/// </summary>
public static class IspCarrier
{
    private static readonly (CarrierKind Kind, string[] Tokens)[] Brands =
    {
        (CarrierKind.Telecom, new[] { "china telecom", "chinatelecom", "chinanet", "中国电信", "电信" }),
        (CarrierKind.Unicom, new[] { "china unicom", "unicom", "china169", "cnc group", "中国联通", "联通", "网通" }),
        (CarrierKind.Mobile, new[] { "china mobile", "cmnet", "cmcc", "中国移动", "移动" }),
        (CarrierKind.Cable, new[] { "radio and television", "china broadcasting", "cbn", "中国广电", "广电" }),
        (CarrierKind.Education, new[] { "cernet", "cerrnet", "china education", "教育网" })
    };

    private static readonly (string Vendor, string[] Tokens)[] Clouds =
    {
        ("阿里云", new[] { "alibaba", "aliyun" }),
        ("腾讯云", new[] { "tencent" }),
        ("百度云", new[] { "baidu" }),
        ("华为云", new[] { "huawei cloud", "huaweicloud" }),
        ("金山云", new[] { "kingsoft", "ksyun" }),
        ("AWS", new[] { "amazon", "aws" }),
        ("Google", new[] { "google" }),
        ("Azure", new[] { "microsoft", "azure" }),
        ("Cloudflare", new[] { "cloudflare" }),
        ("DigitalOcean", new[] { "digitalocean" }),
        ("Vultr", new[] { "vultr", "choopa" }),
        ("Linode", new[] { "linode", "akamai" }),
        ("OVH", new[] { "ovh" }),
        ("Hetzner", new[] { "hetzner" }),
        ("Oracle 云", new[] { "oracle" })
    };

    /// <summary>归类一个 IP 的运营商大类与展示名（如"电信"、"阿里云"、"海外·美国"）。</summary>
    public static CarrierKind Classify(IspRecord? record, out string display)
    {
        display = "未知";
        if (record is null) return CarrierKind.Unknown;

        if (!record.Ok)
        {
            var text = record.FailText;
            display = text.Length == 0 ? "未知" : text;
            return record.Message == "private range" ? CarrierKind.Private : CarrierKind.Unknown;
        }

        // ① 按字段优先取"唯一能确定"的那个字段（isp 最贴近接入网络，as 最贴近路由归属）
        foreach (var field in new[] { record.Isp, record.Org, record.As, record.AsName })
        {
            var kind = MatchUniqueBrand(field);
            if (kind == CarrierKind.Unknown) continue;

            display = BrandName(kind);
            return kind;
        }

        // ② 云厂商 / IDC：不是三网之一，但同 AS 仍是同一张网
        var all = $"{record.Isp} {record.Org} {record.As} {record.AsName}";
        foreach (var (vendor, tokens) in Clouds)
        {
            if (!tokens.Any(t => all.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;

            display = vendor;
            return CarrierKind.Cloud;
        }

        // ③ 境外
        if (record.Country.Length > 0 && !IsChina(record.Country))
        {
            display = $"海外·{record.Country}";
            return CarrierKind.Overseas;
        }

        // ④ 国内但既不是三网、也不是已知云：给出 ISP 原文，方便人工判断
        var isp = record.Isp.Trim();
        if (isp.Length > 0)
        {
            display = isp.Length <= 24 ? isp : isp[..24] + "…";
            return CarrierKind.Cloud; // 有明确 ISP 但不是三网：按"其它网络"处理，比较时看 AS
        }

        return CarrierKind.Unknown;
    }

    /// <summary>
    /// 判定同/跨运营商。返回结构里带"灯色 + 一句话 + 明细（AS 号等）"，
    /// 明细进提示框，出问题时运维能看到原始依据。
    /// </summary>
    public static CrossNetVerdict Compare(IspRecord? local, IspRecord? remote, bool enabled = true)
    {
        if (!enabled) return new CrossNetVerdict(CrossNetState.Disabled, "未启用", "「显示运营商信息」未勾选");

        var localKind = Classify(local, out var localName);
        var remoteKind = Classify(remote, out var remoteName);

        var localAs = local?.AsNumber ?? "";
        var remoteAs = remote?.AsNumber ?? "";

        var detail =
            $"本端 {Describe(local, localName, localAs)}\n目标 {Describe(remote, remoteName, remoteAs)}";

        // 任何一侧没有数据 → 不猜
        if (local is null || !local.Ok || remote is null || !remote.Ok)
        {
            var missing = new List<string>(2);
            if (local is null) missing.Add("本端地址未知（首次探测成功后才拿得到出口 IP）");
            else if (!local.Ok) missing.Add("本端：" + local.FailText);
            if (remote is null) missing.Add("目标地址未知");
            else if (!remote.Ok) missing.Add("目标：" + remote.FailText);

            return new CrossNetVerdict(CrossNetState.Unknown, "未知", detail + "\n" + string.Join("；", missing));
        }

        // 内网地址（本端出口 IP 不可能是内网，出现即视为拿不到有效数据）
        if (localKind == CarrierKind.Private || remoteKind == CarrierKind.Private)
        {
            return new CrossNetVerdict(CrossNetState.Unknown, "内网", detail);
        }

        var localBrand = IsBrand(localKind);
        var remoteBrand = IsBrand(remoteKind);

        // ① 两侧都是三网/教育网/广电：先比大类（上海电信 ↔ 广东电信 = 同运营商）
        if (localBrand && remoteBrand)
        {
            return localKind == remoteKind
                ? new CrossNetVerdict(CrossNetState.Same, $"同运营商（{localName}）", detail)
                : new CrossNetVerdict(CrossNetState.Cross, $"跨运营商（{localName} → {remoteName}）", detail);
        }

        // ② 有一侧是云/IDC/海外、或某侧大类不确定：退化为比 AS 号（同 AS = 同一张网）
        if (localAs.Length > 0 && remoteAs.Length > 0)
        {
            return string.Equals(localAs, remoteAs, StringComparison.OrdinalIgnoreCase)
                ? new CrossNetVerdict(CrossNetState.Same, $"同网（{localAs}）", detail)
                : new CrossNetVerdict(CrossNetState.Cross,
                    $"跨网（{localName} → {remoteName}）", detail);
        }

        // ③ 连 AS 都比不了：只给出两侧归属，不下结论
        return new CrossNetVerdict(CrossNetState.Unknown,
            $"{localName} → {remoteName}", detail + "\n（缺少 AS 号，无法判定是否同一张网）");
    }

    /// <summary>大类的中文名（比较结果里用）。</summary>
    public static string BrandName(CarrierKind kind) => kind switch
    {
        CarrierKind.Telecom => "电信",
        CarrierKind.Unicom => "联通",
        CarrierKind.Mobile => "移动",
        CarrierKind.Cable => "广电",
        CarrierKind.Education => "教育网",
        CarrierKind.Private => "内网",
        CarrierKind.Cloud => "云/IDC",
        CarrierKind.Overseas => "海外",
        _ => "未知"
    };

    /// <summary>三网 + 广电 + 教育网：这些之间可以直接比大类。</summary>
    private static bool IsBrand(CarrierKind kind)
        => kind is CarrierKind.Telecom or CarrierKind.Unicom or CarrierKind.Mobile
            or CarrierKind.Cable or CarrierKind.Education;

    /// <summary>某个字段里能唯一确定一个大类时返回它；匹配到多个大类（数据矛盾）时返回 Unknown。</summary>
    private static CarrierKind MatchUniqueBrand(string? field)
    {
        var text = field?.Trim() ?? "";
        if (text.Length == 0) return CarrierKind.Unknown;

        var matched = CarrierKind.Unknown;
        foreach (var (kind, tokens) in Brands)
        {
            if (!tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
            if (matched != CarrierKind.Unknown) return CarrierKind.Unknown; // 同一字段里出现两个大类 → 不可判
            matched = kind;
        }

        return matched;
    }

    private static bool IsChina(string country)
    {
        var value = country.Trim();
        return value.Equals("China", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("中国", StringComparison.Ordinal) ||
               value.Equals("CN", StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(IspRecord? record, string name, string asNumber)
    {
        if (record is null) return "（无数据）";

        if (!record.Ok) return $"{record.Ip}：{record.FailText}";

        var asText = asNumber.Length > 0 ? asNumber : (record.As.Length > 0 ? record.As : "-");
        return $"{record.Ip}：{name}（{record.Isp}｜{asText}）";
    }
}
