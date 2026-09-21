namespace LineTester;

/// <summary>曲线上的一个采样点。<see cref="RttMs"/> 在丢包时无意义（由 <see cref="Lost"/> 标记）。</summary>
public readonly record struct ChartSample(DateTime Time, double RttMs, bool Lost);

/// <summary>
/// 一条曲线 = 一个「链路 + 协议」的**整个会话**的采样。
///
/// 为什么不用「实时RTT明细」那张表：它只保留最后 2000 条，
/// 现场 7 条链路 × 3 个协议 = 每秒 21 条 → 只够约 95 秒，横轴根本画不出一个会话。
/// 所以曲线单独留一份全量历史。
/// </summary>
public sealed class ChartSeries
{
    private readonly List<ChartSample> _samples = new();

    public ChartSeries(string key, string linkName, string protocol, string target, int maxSamples)
    {
        Key = key;
        LinkName = linkName;
        Protocol = protocol;
        Target = target;
        MaxSamples = maxSamples;
    }

    /// <summary>与 <see cref="ProbeResult.Key"/> 一致（链路名|主机|端口|协议），用于和统计行对上号。</summary>
    public string Key { get; }

    /// <summary>图例与提示框里的曲线名：**链路名称 + 协议名称**。</summary>
    public string Name => $"{LinkName} · {Protocol}";

    public string LinkName { get; }
    public string Protocol { get; }
    public string Target { get; }
    public int MaxSamples { get; }

    public IReadOnlyList<ChartSample> Samples => _samples;
    public int Count => _samples.Count;

    /// <summary>
    /// 当前是否**显示**在图上。
    ///
    /// 注意：它只管显示，**不影响记录**——所有链路的数据一直都在记，
    /// 否则"晚勾选的链路看不到勾选之前的数据"（现场反馈过这个毛病）。
    /// 取消勾选也不清历史：误点一下不该丢掉整段曲线，重新勾上还是完整的那条。
    /// </summary>
    public bool Selected { get; set; }

    /// <summary>超出上限被裁掉过（只有极长会话才会发生，界面上会提示）。</summary>
    public bool Trimmed { get; private set; }

    public DateTime? StartTime => _samples.Count == 0 ? null : _samples[0].Time;
    public DateTime? EndTime => _samples.Count == 0 ? null : _samples[^1].Time;

    public int LostCount
    {
        get
        {
            var lost = 0;
            foreach (var sample in _samples) if (sample.Lost) lost++;
            return lost;
        }
    }

    /// <summary>
    /// 追加一个采样。
    /// 时间戳是**发包时刻**，而结果按完成顺序回来，同一批里偶尔会轻微乱序；
    /// 这里保证列表始终按时间有序（二分插入），否则横轴取点、悬停吸附、像素聚合全会错位。
    /// </summary>
    public void Add(ProbeResult result)
    {
        var sample = new ChartSample(result.Timestamp, result.RttMs ?? 0, result.Lost);

        if (_samples.Count == 0 || sample.Time >= _samples[^1].Time)
        {
            _samples.Add(sample);
        }
        else
        {
            var at = ChartMath.LowerBound(_samples, sample.Time);
            _samples.Insert(at, sample);
        }

        // 安全阀：按 1 秒 1 条算，20 万条约 55 小时。真到这一步说明会话长得离谱，
        // 丢最旧的并标记出来——总比让程序卡死强。
        if (_samples.Count > MaxSamples)
        {
            _samples.RemoveRange(0, _samples.Count - MaxSamples);
            Trimmed = true;
        }
    }

    public void Clear()
    {
        _samples.Clear();
        Trimmed = false;
    }

    /// <summary>可见区间内的最大 RTT（Y 轴自适应用；丢包不参与）。</summary>
    public double MaxRtt(DateTime start, DateTime end)
    {
        var max = 0d;
        var from = ChartMath.LowerBound(_samples, start);
        for (var i = from; i < _samples.Count; i++)
        {
            var sample = _samples[i];
            if (sample.Time > end) break;
            if (!sample.Lost && sample.RttMs > max) max = sample.RttMs;
        }

        return max;
    }
}

/// <summary>
/// 会话级的曲线数据：按「链路 + 协议」分组保存全部采样。
/// 与统计会话同生命周期——点「清除」就是开一个新会话，曲线也应该从零开始。
/// </summary>
public sealed class ChartHistory
{
    /// <summary>单条曲线的采样上限（见 ChartSeries.Add 的说明）。</summary>
    public const int DefaultMaxSamplesPerSeries = 200_000;

    private readonly Dictionary<string, ChartSeries> _byKey = new(StringComparer.Ordinal);
    private readonly List<ChartSeries> _ordered = new();

    public int MaxSamplesPerSeries { get; init; } = DefaultMaxSamplesPerSeries;

    /// <summary>按登记顺序排列的**全部**曲线（含未勾选的，它们的数据照样在记）。</summary>
    public IReadOnlyList<ChartSeries> Series => _ordered;

    /// <summary>当前勾选的曲线（图例顺序、配色下标都按这个顺序，保证颜色稳定不跳）。</summary>
    public IReadOnlyList<ChartSeries> SelectedSeries => _ordered.Where(s => s.Selected).ToList();

    /// <summary>已经记录到的链路条数（含没勾选的）：界面用它解释"没曲线不等于没数据"。</summary>
    public int RecordedCount => _ordered.Count;

    public ChartSeries? Find(string key) => _byKey.TryGetValue(key, out var series) ? series : null;

    /// <summary>整段会话的时间范围（用于横轴默认的"全程"）。没有数据时返回 null。</summary>
    public (DateTime Start, DateTime End)? TimeRange
    {
        get
        {
            DateTime? start = null;
            DateTime? end = null;

            foreach (var series in _ordered)
            {
                var s = series.StartTime;
                var e = series.EndTime;
                if (s is null || e is null) continue;
                if (start is null || s < start) start = s;
                if (end is null || e > end) end = e;
            }

            return start is null || end is null ? null : (start.Value, end.Value);
        }
    }

    /// <summary>勾选一条链路（只影响**显示**；数据本来就在记）。重复勾选无副作用。</summary>
    public ChartSeries Select(string key, string linkName, string protocol, string target)
    {
        if (!_byKey.TryGetValue(key, out var series))
        {
            series = new ChartSeries(key, linkName, protocol, target, MaxSamplesPerSeries);
            _byKey[key] = series;
            _ordered.Add(series);
        }

        series.Selected = true;
        return series;
    }

    /// <summary>取消勾选：只是不再画，**数据继续记、历史照留**（重新勾上就是完整一条）。</summary>
    public bool Deselect(string key)
    {
        if (!_byKey.TryGetValue(key, out var series)) return false;
        series.Selected = false;
        return true;
    }

    /// <summary>
    /// 记一个采样。**与勾选无关**：没勾选的链路也照样记，
    /// 这样"后来才勾选"的链路也能看到勾选之前的整段数据。
    /// 第一次见到某条链路时自动建立曲线。
    /// </summary>
    public void Add(ProbeResult result)
    {
        GetOrCreate(result).Add(result);
    }

    private ChartSeries GetOrCreate(ProbeResult result)
    {
        if (_byKey.TryGetValue(result.Key, out var series)) return series;

        series = new ChartSeries(result.Key, result.LinkName, result.Protocol, result.Target, MaxSamplesPerSeries);
        _byKey[result.Key] = series;
        _ordered.Add(series);
        return series;
    }

    /// <summary>批量写入（界面每 500 毫秒取一次待处理结果时调用）。</summary>
    public void AddRange(IEnumerable<ProbeResult> results)
    {
        foreach (var result in results) Add(result);
    }

    /// <summary>开新会话：清空采样，但保留"勾了哪些链路"（清除后通常还要继续看同样几条）。</summary>
    public void Clear()
    {
        foreach (var series in _ordered) series.Clear();
    }
}
