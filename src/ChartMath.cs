namespace LineTester;

/// <summary>
/// RTT 曲线用到的**纯计算**：坐标换算、刻度、像素列聚合、缩放/平移、悬停吸附。
///
/// 刻意不引用 WPF：这些是曲线的真正逻辑（错了就是"图画得不对"），
/// 放进零依赖自检宿主才验得动——渲染本身没有视觉模型可看，只能靠这些断言守住。
/// </summary>
public static class ChartMath
{
    /// <summary>时间轴候选步长（从 1 秒到 1 天）：刻度必须落在"人能一眼读懂"的时间点上。</summary>
    private static readonly TimeSpan[] TimeSteps =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(6),
        TimeSpan.FromHours(12), TimeSpan.FromDays(1)
    };

    /// <summary>选一个"整"的时间步长：让刻度数落在目标附近，且步长本身是整秒/整分/整时。</summary>
    public static TimeSpan ChooseTimeStep(TimeSpan span, int targetTicks)
    {
        if (targetTicks < 2) targetTicks = 2;
        if (span <= TimeSpan.Zero) return TimeSteps[0];

        var ideal = span.TotalMilliseconds / targetTicks;
        foreach (var step in TimeSteps)
        {
            if (step.TotalMilliseconds >= ideal) return step;
        }

        return TimeSteps[^1];
    }

    /// <summary>
    /// 时间轴刻度：按步长对齐到整点/整分，再裁进 [start, end]。
    /// 不对齐的话刻度会是 12:03:47 这种，看图的人要在脑子里做减法。
    /// </summary>
    public static List<DateTime> TimeTicks(DateTime start, DateTime end, int targetTicks)
    {
        var ticks = new List<DateTime>();
        if (end <= start) return ticks;

        var step = ChooseTimeStep(end - start, targetTicks);

        // 对齐：秒级步长对齐到"分钟的整数倍"，分钟级对齐到"整分钟的整数倍"，小时级对齐到"整小时"
        var first = AlignUp(start, step);
        for (var t = first; t <= end; t = t.Add(step)) ticks.Add(t);
        return ticks;
    }

    private static DateTime AlignUp(DateTime start, TimeSpan step)
    {
        var day = start.Date;

        if (step >= TimeSpan.FromDays(1))
        {
            var days = (int)Math.Ceiling(step.TotalDays);
            var offset = (int)(start - day).TotalDays;
            var aligned = day.AddDays(Math.Ceiling((double)offset / days) * days);
            return aligned < start ? aligned.AddDays(days) : aligned;
        }

        if (step >= TimeSpan.FromHours(1))
        {
            var hours = (int)Math.Round(step.TotalHours);
            var aligned = day.AddHours(Math.Ceiling((double)start.Hour / hours) * hours);
            return aligned < start ? aligned.AddHours(hours) : aligned;
        }

        if (step >= TimeSpan.FromMinutes(1))
        {
            var minutes = (int)Math.Round(step.TotalMinutes);
            var aligned = day.AddMinutes(Math.Ceiling((double)(start.Hour * 60 + start.Minute) / minutes) * minutes);
            return aligned < start ? aligned.AddMinutes(minutes) : aligned;
        }

        var seconds = (int)Math.Round(step.TotalSeconds);
        var alignedSeconds = day.AddSeconds(Math.Ceiling((double)(start.Hour * 3600 + start.Minute * 60 + start.Second) / seconds) * seconds);
        return alignedSeconds < start ? alignedSeconds.AddSeconds(seconds) : alignedSeconds;
    }

    /// <summary>时间轴标签：会话很短时给到秒，跨天时补上日期。</summary>
    public static string FormatAxisTime(DateTime time, TimeSpan span)
    {
        if (span >= TimeSpan.FromDays(1)) return time.ToString("MM-dd HH:mm");
        if (span >= TimeSpan.FromMinutes(10)) return time.ToString("HH:mm");
        if (span >= TimeSpan.FromMinutes(1)) return time.ToString("HH:mm:ss");
        return time.ToString("mm:ss");
    }

    /// <summary>悬停提示里的完整时刻（精确到毫秒，便于和 CSV 日志对齐）。</summary>
    public static string FormatHoverTime(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss.fff");

    /// <summary>Y 轴比例：从 0 起（延迟看的是绝对量），上界取"整"数。</summary>
    public readonly record struct YScale(double Min, double Max, double Step)
    {
        public IReadOnlyList<double> Ticks()
        {
            var ticks = new List<double>();
            if (Step <= 0) return ticks;
            for (var v = Min; v <= Max + Step / 2; v += Step) ticks.Add(Math.Round(v, 6));
            return ticks;
        }
    }

    /// <summary>
    /// 算 Y 轴范围：**只按当前可见数据的最大值**自适应。
    ///
    /// 曾经把告警阈值也并进来当上界，结果是"数据只有 20ms、轴顶却被 300ms 的阈值顶到 400ms"，
    /// 曲线全挤在底部——现场看到的就是"纵坐标是个固定值"。阈值超出刻度时另有提示（见 RttChart），
    /// **不能为了画阈值线而牺牲数据本身的可读性**。
    /// </summary>
    public static YScale ComputeYScale(double visibleMaxMs, int targetTicks = 5)
    {
        var top = visibleMaxMs > 0 ? visibleMaxMs : 10;   // 全是丢包/没数据时给个固定高度，避免除零和贴地
        top *= 1.15;                                      // 顶部留白，曲线不贴边

        var step = NiceStep(top / Math.Max(2, targetTicks - 1));
        var max = Math.Ceiling(top / step) * step;
        if (max <= 0) max = step;
        return new YScale(0, max, step);
    }

    /// <summary>
    /// 刻度标签的小数位跟着步长走：
    /// 本机回环的 RTT 只有零点几毫秒，固定一位小数会出现两个一模一样的刻度标签。
    /// </summary>
    public static string FormatTick(double value, double step)
        => value.ToString(step >= 1 ? "F0" : step >= 0.1 ? "F1" : "F2");

    /// <summary>取"好看"的步长：1/2/5 × 10^n。</summary>
    public static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;

        var exp = Math.Floor(Math.Log10(raw));
        var pow = Math.Pow(10, exp);
        var frac = raw / pow;

        var nice = frac <= 1 ? 1 : frac <= 2 ? 2 : frac <= 5 ? 5 : 10;
        return nice * pow;
    }

    /// <summary>时间 → 屏幕 X。区间为空时退化为左边界，避免除零。</summary>
    public static double XOf(DateTime time, DateTime start, DateTime end, double left, double width)
    {
        var span = (end - start).TotalMilliseconds;
        if (span <= 0) return left;
        return left + (time - start).TotalMilliseconds / span * width;
    }

    /// <summary>屏幕 X → 时间（悬停用）。会夹在区间内，鼠标移出边界也不会算出区间外的时间。</summary>
    public static DateTime TimeOf(double x, DateTime start, DateTime end, double left, double width)
    {
        if (width <= 0) return start;
        var ratio = Math.Clamp((x - left) / width, 0, 1);
        return start.AddMilliseconds((end - start).TotalMilliseconds * ratio);
    }

    /// <summary>第一个时间 ≥ t 的下标（二分；采样已按时间有序）。</summary>
    public static int LowerBound(IReadOnlyList<ChartSample> samples, DateTime t)
    {
        var lo = 0;
        var hi = samples.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (samples[mid].Time < t) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>
    /// 悬停吸附：找时间上离 <paramref name="t"/> 最近的采样点。
    /// 返回 -1 表示容差内没有点（鼠标落在数据间隙时不该硬指一个远处的时间点）。
    /// </summary>
    public static int NearestIndex(IReadOnlyList<ChartSample> samples, DateTime t, TimeSpan tolerance)
    {
        if (samples.Count == 0) return -1;

        var index = LowerBound(samples, t);
        var best = -1;
        var bestDelta = TimeSpan.MaxValue;

        if (index < samples.Count) { best = index; bestDelta = samples[index].Time - t; }
        if (index > 0)
        {
            var delta = t - samples[index - 1].Time;
            if (delta < bestDelta) { best = index - 1; bestDelta = delta; }
        }

        return bestDelta <= tolerance ? best : -1;
    }

    /// <summary>一个像素列的聚合结果（长会话时横轴按像素列压缩，但数据一条都不丢）。</summary>
    public readonly record struct ChartBucket(double Min, double Max, double Sum, int OkCount, int LostCount)
    {
        public bool Has => OkCount + LostCount > 0;
        public bool HasOk => OkCount > 0;
        public bool HasLoss => LostCount > 0;
        public double Mean => OkCount > 0 ? Sum / OkCount : 0;
    }

    /// <summary>
    /// 把 [start, end] 内的采样压进 <paramref name="columns"/> 个像素列。
    /// 每列保留最小/最大/均值/丢包数——这就是"展现会话所有数据"的做法：
    /// 不做隔点抽样（会丢掉尖刺），而是把一列里所有点的包络画出来。
    /// </summary>
    public static void Aggregate(
        IReadOnlyList<ChartSample> samples, DateTime start, DateTime end, int columns, ChartBucket[] buckets)
    {
        Array.Clear(buckets);

        var span = (end - start).TotalMilliseconds;
        if (span <= 0 || columns <= 0 || samples.Count == 0) return;

        var from = LowerBound(samples, start);

        for (var i = from; i < samples.Count; i++)
        {
            var sample = samples[i];
            var offset = (sample.Time - start).TotalMilliseconds;
            if (offset > span) break;

            var column = (int)(offset / span * columns);
            if (column < 0) column = 0;
            if (column >= columns) column = columns - 1;

            ref var bucket = ref buckets[column];
            if (sample.Lost)
            {
                bucket = bucket with { LostCount = bucket.LostCount + 1 };
            }
            else if (bucket.OkCount == 0)
            {
                bucket = bucket with { Min = sample.RttMs, Max = sample.RttMs, Sum = sample.RttMs, OkCount = 1 };
            }
            else
            {
                bucket = bucket with
                {
                    Min = Math.Min(bucket.Min, sample.RttMs),
                    Max = Math.Max(bucket.Max, sample.RttMs),
                    Sum = bucket.Sum + sample.RttMs,
                    OkCount = bucket.OkCount + 1
                };
            }
        }
    }

    /// <summary>
    /// 缩放：以 <paramref name="anchor"/>（鼠标所在时刻）为不动点缩放，
    /// 这样"盯着看的那一段"不会跑掉——以区间中心缩放是图表工具里最常见的失手。
    /// </summary>
    public static (DateTime Start, DateTime End) ZoomRange(
        DateTime start, DateTime end, DateTime anchor, double factor,
        TimeSpan minSpan, DateTime floor, DateTime ceiling)
    {
        var span = end - start;
        if (span <= TimeSpan.Zero) return (start, end);

        var total = ceiling - floor;

        // 会话本来就比"最小缩放粒度"还短时，最小粒度不能反过来把缩放锁死
        // （否则短会话里滚轮完全没反应）
        var floorSpan = Math.Min(minSpan.Ticks, Math.Max(1, total.Ticks));
        var target = TimeSpan.FromTicks((long)Math.Clamp(span.Ticks * factor, floorSpan, Math.Max(floorSpan, total.Ticks)));

        var ratio = span.Ticks <= 0 ? 0.5 : Math.Clamp((anchor - start).Ticks / (double)span.Ticks, 0, 1);
        var newStart = anchor.AddTicks(-(long)(target.Ticks * ratio));

        return ClampRange(newStart, newStart + target, floor, ceiling);
    }

    /// <summary>平移：整体挪动一段时间，并保证不越出会话范围（越界会让横轴出现空白段）。</summary>
    public static (DateTime Start, DateTime End) PanRange(
        DateTime start, DateTime end, TimeSpan delta, DateTime floor, DateTime ceiling)
    {
        return ClampRange(start + delta, end + delta, floor, ceiling);
    }

    private static (DateTime Start, DateTime End) ClampRange(DateTime start, DateTime end, DateTime floor, DateTime ceiling)
    {
        var span = end - start;
        var total = ceiling - floor;

        if (span >= total) return (floor, ceiling);
        if (span <= TimeSpan.Zero) span = TimeSpan.FromSeconds(1);

        if (start < floor) start = floor;
        if (start + span > ceiling) start = ceiling - span;
        return (start, start + span);
    }
}
