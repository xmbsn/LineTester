namespace LineTester;

/// <summary>
/// 一次“统计会话”的全部聚合状态：滑动窗口历史、连续丢包、会话号。
/// 不依赖 WPF，可在测试中直接驱动。
///
/// 会话号（<see cref="Epoch"/>）在 <see cref="Clear"/> 时自增：
/// 变更之前发起、变更之后才返回的探测结果会被 <see cref="Accept"/> 拒绝，
/// 因此“停止/重启/清除”不会再往新会话里掺入陈旧数据或假丢包。
/// </summary>
public sealed class StatSession
{
    private readonly Dictionary<string, List<ProbeResult>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _consecutiveLoss = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastSeenFrom = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failure = new(StringComparer.Ordinal);

    public int WindowMinutes { get; private set; } = 60;
    public int WarningRttMs { get; private set; } = 300;
    public double LossWarnPercent { get; private set; } = 1.0;
    public int ConsecutiveLossWarn { get; private set; } = 3;

    public long Epoch { get; private set; }
    public DateTime ClearedAt { get; private set; } = DateTime.Now;

    /// <summary>已纳入统计的链路/协议组合数。</summary>
    public int TrackedKeys => _history.Count;

    /// <summary>当前窗口内的样本总数（用于界面上的“重新开始统计”反馈）。</summary>
    public int TotalSamples
    {
        get
        {
            var total = 0;
            foreach (var list in _history.Values) total += list.Count;
            return total;
        }
    }

    public void Configure(AppConfig config)
    {
        WindowMinutes = config.StatWindowMinutes < 1 ? 1 : config.StatWindowMinutes;
        WarningRttMs = config.WarningRttMs < 0 ? 0 : config.WarningRttMs;
        LossWarnPercent = config.LossWarnPercent < 0 ? 0 : config.LossWarnPercent;
        ConsecutiveLossWarn = config.ConsecutiveLossWarn < 1 ? 1 : config.ConsecutiveLossWarn;
    }

    /// <summary>清空统计并开启新会话。运行中可反复调用。</summary>
    public void Clear()
    {
        Epoch++;
        _history.Clear();
        _consecutiveLoss.Clear();
        _lastSeenFrom.Clear();
        _failure.Clear();
        ClearedAt = DateTime.Now;
    }

    /// <summary>该结果是否属于当前会话。</summary>
    public bool Accept(long epoch) => epoch == Epoch;

    public bool IsAnomaly(ProbeResult result)
        => result.Lost || (result.RttMs.HasValue && result.RttMs.Value >= WarningRttMs);

    /// <summary>纳入一条结果，返回它是否为异常。调用方须保证在 UI 线程串行调用。</summary>
    public bool Add(ProbeResult result)
    {
        var key = result.Key;
        if (!_history.TryGetValue(key, out var list))
        {
            list = new List<ProbeResult>();
            _history[key] = list;
        }

        list.Add(result);

        var cutoff = result.Timestamp.AddMinutes(-WindowMinutes);
        list.RemoveAll(x => x.Timestamp < cutoff);

        _consecutiveLoss[key] = result.Lost ? GetConsecutiveLoss(key) + 1 : 0;

        if (!string.IsNullOrEmpty(result.SeenFrom)) _lastSeenFrom[key] = result.SeenFrom;

        // 只反映“当前”的失败原因：一旦成功就清掉，避免历史偶发失败长期误导
        if (result.Lost) _failure[key] = result.FailureText;
        else _failure.Remove(key);

        return IsAnomaly(result);
    }

    /// <summary>该链路当前的失败原因说明（成功过则为空串）。</summary>
    public string LastFailure(string key) => _failure.GetValueOrDefault(key, "");

    /// <summary>该链路最近一次成功探测中，对端看到的本端源地址（没有则返回空串）。</summary>
    public string LastSeenFrom(string key) => _lastSeenFrom.GetValueOrDefault(key, "");

    public int GetConsecutiveLoss(string key) => _consecutiveLoss.GetValueOrDefault(key);

    public IReadOnlyList<ProbeResult> History(string key)
        => _history.TryGetValue(key, out var list) ? list : Array.Empty<ProbeResult>();

    /// <summary>生成统计表格快照，按链路/协议排序，顺序稳定。</summary>
    public List<StatRow> BuildRows()
    {
        var rows = new List<StatRow>(_history.Count);

        foreach (var (key, samples) in _history)
        {
            if (samples.Count == 0) continue;

            var first = samples[0];
            var valid = new List<double>(samples.Count);
            var lost = 0;
            foreach (var s in samples)
            {
                if (s.Lost) lost++;
                else if (s.RttMs.HasValue) valid.Add(s.RttMs.Value);
            }

            var sent = samples.Count;
            var lossRate = sent == 0 ? 0 : lost * 100.0 / sent;
            double? avg = valid.Count == 0 ? null : valid.Average();
            var consecutive = GetConsecutiveLoss(key);

            rows.Add(new StatRow
            {
                Key = key,
                Name = first.LinkName + " / " + first.Protocol,
                Target = first.Target,
                Avg = avg.HasValue ? $"{avg.Value:F1} ms" : "-",
                P50 = Format(Statistics.Percentile(valid, 0.50)),
                P95 = Format(Statistics.Percentile(valid, 0.95)),
                P99 = Format(Statistics.Percentile(valid, 0.99)),
                Sent = sent,
                Lost = lost,
                LossRate = $"{lossRate:F2}%",
                ConsecutiveLoss = consecutive,
                SeenFrom = LastSeenFrom(key) is { Length: > 0 } seen ? seen : "-",
                LastFailure = LastFailure(key) is { Length: > 0 } failure ? failure : "-",
                Status = StatusRules.Compute(sent, lost, consecutive, avg,
                    WarningRttMs, LossWarnPercent, ConsecutiveLossWarn)
            });
        }

        rows.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return rows;
    }

    private static string Format(double? value) => value.HasValue ? $"{value.Value:F1} ms" : "-";
}
