namespace LineTester;

public static class Statistics
{
    /// <summary>线性插值百分位；空集合返回 null。</summary>
    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var arr = values.OrderBy(x => x).ToArray();
        if (arr.Length == 0) return null;
        if (arr.Length == 1) return arr[0];

        if (p <= 0) return arr[0];
        if (p >= 1) return arr[^1];

        var pos = (arr.Length - 1) * p;
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        if (lo == hi) return arr[lo];
        return arr[lo] + (arr[hi] - arr[lo]) * (pos - lo);
    }
}

/// <summary>
/// 链路状态判定。抽成纯函数以便测试，并修正“窗口内出现过一次丢包就永久显示有丢包”的问题。
/// </summary>
public static class StatusRules
{
    public const string Normal = "正常";
    public const string HighLatency = "高延迟";
    public const string PacketLoss = "有丢包";
    public const string Interrupted = "中断";

    public static string Compute(
        int sent,
        int lost,
        int consecutiveLoss,
        double? avgRttMs,
        int warningRttMs,
        double lossWarnPercent,
        int consecutiveLossWarn)
    {
        if (consecutiveLoss >= consecutiveLossWarn) return Interrupted;

        var lossRate = sent == 0 ? 0 : lost * 100.0 / sent;
        if (lost > 0 && lossRate >= lossWarnPercent) return PacketLoss;

        if (avgRttMs.HasValue && avgRttMs.Value >= warningRttMs) return HighLatency;

        return Normal;
    }
}
