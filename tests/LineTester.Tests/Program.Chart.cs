using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LineTester.Views;

namespace LineTester.Tests;

/// <summary>
/// RTT 曲线的自检。
///
/// 分三层：
/// ① 历史缓冲（<see cref="ChartHistory"/>）——谁收数据、取消勾选后历史还在不在；
/// ② 纯计算（<see cref="ChartMath"/>）——刻度、像素列聚合、缩放锚点、悬停吸附；
/// ③ 真实窗口——勾选统计行是否真的出曲线、悬停是否给得出值。
/// 渲染好不好看没有断言能替代眼睛，所以另有一张 `--render-ui` 出的 PNG 供人工看。
/// </summary>
internal static partial class Program
{
    private static ProbeResult ChartSampleResult(string link, string host, int port, string protocol, DateTime time, double? rtt, bool lost)
        => new()
        {
            LinkName = link,
            Host = host,
            Port = port,
            Protocol = protocol,
            Timestamp = time,
            RttMs = rtt,
            Lost = lost
        };

    // ---------------- ① 历史缓冲 ----------------

    private static void TestChartHistory()
    {
        Section("RTT 曲线：会话历史缓冲");

        var history = new ChartHistory();
        var t0 = new DateTime(2026, 9, 21, 10, 0, 0);
        const string key = "机房B|127.0.0.1|9001|TCP";

        // 现场反馈：**晚勾选的链路看不到勾选之前的数据**。
        // 记录与勾选必须解耦——先喂两条数据（此时还没勾选），再勾选，这两条必须在。
        history.Add(ChartSampleResult("机房B", "127.0.0.1", 9001, "TCP", t0, 12.5, false));
        history.Add(ChartSampleResult("机房B", "127.0.0.1", 9001, "TCP", t0.AddSeconds(2), 13.0, false));

        Check("没勾选也在记数据（晚勾选才有得看）", history.Find(key) is { Count: 2 },
            history.Find(key)?.Count.ToString() ?? "null");
        Check("没勾选时不显示在图上", history.SelectedSeries.Count == 0);
        Eq("记录条数（含未勾选）用于界面说清「没曲线 ≠ 没数据」", 1, history.RecordedCount);

        history.Select(key, "机房B", "TCP", "127.0.0.1:9001");
        var series = history.Find(key);
        Check("勾选后复用同一条曲线", series is not null);
        Eq("勾选后立刻能看到勾选之前的那两条数据", 2, series!.Count);
        Eq("曲线名 = 链路名称 + 协议名称", "机房B · TCP", series.Name);
        Eq("只勾一条时选中集合只有一条", 1, history.SelectedSeries.Count);

        // 结果按完成顺序回来，同一批里时间戳会轻微乱序；缓冲必须自己保持有序
        history.Add(ChartSampleResult("机房B", "127.0.0.1", 9001, "TCP", t0.AddSeconds(1), 11.0, false));
        Check("乱序到达的采样被按时间插入",
            series.Samples.Select(s => s.Time).SequenceEqual(new[] { t0, t0.AddSeconds(1), t0.AddSeconds(2) }),
            string.Join(",", series.Samples.Select(s => s.Time.ToString("HH:mm:ss"))));

        history.Add(ChartSampleResult("机房B", "127.0.0.1", 9001, "TCP", t0.AddSeconds(3), 0, true));
        Eq("丢包也记进曲线（不是丢掉不记）", 4, series.Count);
        Eq("丢包数统计正确", 1, series.LostCount);
        Eq("丢包点没有 RTT 值（不会算进最大延迟）", 13.0, series.MaxRtt(t0, t0.AddSeconds(10)));

        // 取消勾选：只是不画，**数据继续记、历史继续留**
        history.Deselect(key);
        history.Add(ChartSampleResult("机房B", "127.0.0.1", 9001, "TCP", t0.AddSeconds(4), 14.0, false));
        Eq("取消勾选后仍然继续记数据（勾选只管显示）", 5, series.Count);
        Check("取消勾选的曲线不再出现在选中集合", history.SelectedSeries.Count == 0);

        history.Select(key, "机房B", "TCP", "127.0.0.1:9001");
        history.Add(ChartSampleResult("机房B", "127.0.0.1", 9001, "TCP", t0.AddSeconds(5), 15.0, false));
        Eq("重新勾选后历史是连续的一整段", 6, series.Count);

        // 会话范围：多条曲线取并集
        var other = "机房C|192.0.2.9|9001|UDP";
        history.Select(other, "机房C", "UDP", "192.0.2.9:9001");
        history.Add(ChartSampleResult("机房C", "192.0.2.9", 9001, "UDP", t0.AddSeconds(-5), 40.0, false));
        var range = history.TimeRange;
        Check("会话时间范围取所有曲线的并集",
            range is not null && range.Value.Start == t0.AddSeconds(-5) && range.Value.End == t0.AddSeconds(5),
            range is null ? "null" : $"{range.Value.Start:HH:mm:ss}~{range.Value.End:HH:mm:ss}");

        // 开新会话：采样清零，但"勾了哪几条"保留
        history.Clear();
        Eq("清除后没有采样", 0, series.Count);
        Eq("清除后仍保留勾选（不用重新勾一遍）", 2, history.SelectedSeries.Count);

        // 上限：极长会话的安全阀
        var capped = new ChartHistory { MaxSamplesPerSeries = 5 };
        capped.Select(key, "机房B", "TCP", "127.0.0.1:9001");
        for (var i = 0; i < 8; i++) capped.Add(ChartSampleResult("机房B", "127.0.0.1", 9001, "TCP", t0.AddSeconds(i), i, false));
        var cappedSeries = capped.Find(key)!;
        Eq("超过上限后只保留最新 N 条", 5, cappedSeries.Count);
        Check("裁掉最早采样会被标记出来（界面上要提示）", cappedSeries.Trimmed);
        Eq("保留的是最新的那批", t0.AddSeconds(3), cappedSeries.Samples[0].Time);
    }

    // ---------------- ② 纯计算 ----------------

    private static void TestChartMath()
    {
        Section("RTT 曲线：刻度 / 聚合 / 缩放 / 悬停吸附");

        // ---- Y 轴：只按可见数据的最大值自适应（不拿阈值/固定值当上界） ----
        var scale = ChartMath.ComputeYScale(0);
        Check("没有数据时 Y 轴仍然给得出高度（不会除零）", scale.Max > 0 && scale.Step > 0, $"{scale.Max}/{scale.Step}");

        var big = ChartMath.ComputeYScale(1500);
        Check("Y 轴上界容得下可见范围最大值", big.Max >= 1500, big.Max.ToString());
        Check("Y 轴刻度是整数步长", big.Ticks().Count >= 3 && big.Ticks().All(v => Math.Abs(v % big.Step) < 1e-6),
            string.Join(",", big.Ticks()));
        Eq("Y 轴从 0 起（延迟看绝对量）", 0.0, big.Min);

        // 现场反馈："纵坐标应该按实际数据最大值自动调整，不该是个固定值"
        // （之前把 300ms 的告警阈值并进了上界，数据只有 20ms 时轴顶被顶到 400ms，曲线全挤在底部）
        var small = ChartMath.ComputeYScale(23);
        Check("数据小时 Y 轴跟着数据走（不会被告警阈值顶高）", small.Max <= 30, small.Max.ToString());
        Check("Y 轴仍留出余量（曲线不贴顶）", small.Max >= 23, small.Max.ToString());

        var subMs = ChartMath.ComputeYScale(0.6);
        Check("亚毫秒的数据也能自适应（回环 RTT 就是零点几毫秒）", subMs.Max <= 1.0 && subMs.Max >= 0.6, subMs.Max.ToString());
        Eq("步长 0.2 时刻度标签一位小数", "0.2", ChartMath.FormatTick(0.2, 0.2));
        Eq("步长 0.05 时刻度标签两位小数（否则相邻刻度标签会一模一样）", "0.05", ChartMath.FormatTick(0.05, 0.05));
        Eq("步长为整数时刻度标签不带小数", "500", ChartMath.FormatTick(500, 500));

        Eq("漂亮步长 1/2/5×10^n", 500.0, ChartMath.NiceStep(431.25));

        // ---- 时间轴 ----
        var start = new DateTime(2026, 9, 21, 10, 0, 0);
        Eq("10 分钟跨度用 2 分钟刻度", TimeSpan.FromMinutes(2), ChartMath.ChooseTimeStep(TimeSpan.FromMinutes(10), 6));
        Eq("1 小时跨度用 10 分钟刻度", TimeSpan.FromMinutes(10), ChartMath.ChooseTimeStep(TimeSpan.FromHours(1), 6));

        var ticks = ChartMath.TimeTicks(start, start.AddMinutes(10), 6);
        Check("刻度落在整分钟上（不是 10:00:47 这种）",
            ticks.All(t => t.Second == 0 && t.Millisecond == 0) && ticks.Count >= 5,
            string.Join(",", ticks.Select(t => t.ToString("HH:mm:ss"))));
        Eq("刻度首尾落在区间内", true, ticks.All(t => t >= start && t <= start.AddMinutes(10)));

        var oddStart = new DateTime(2026, 9, 21, 10, 0, 3);
        var oddTicks = ChartMath.TimeTicks(oddStart, oddStart.AddSeconds(60), 6);
        Check("起点不整齐时第一个刻度也对齐到步长", oddTicks.Count > 0 && oddTicks[0].Second % 10 == 0,
            oddTicks.Count > 0 ? oddTicks[0].ToString("HH:mm:ss") : "无刻度");

        Check("跨天时轴标签带日期", ChartMath.FormatAxisTime(start, TimeSpan.FromDays(2)).Contains("-"));
        Check("短会话标签只到秒", !ChartMath.FormatAxisTime(start, TimeSpan.FromSeconds(20)).Contains("-"));

        // ---- 坐标换算 ----
        var plotLeft = 56.0;
        var plotWidth = 600.0;
        var end = start.AddMinutes(10);
        Eq("时间→屏幕再换算回来是同一个时刻", start.AddMinutes(5),
            ChartMath.TimeOf(ChartMath.XOf(start.AddMinutes(5), start, end, plotLeft, plotWidth), start, end, plotLeft, plotWidth));

        Eq("鼠标移出左边界时夹在区间起点", start,
            ChartMath.TimeOf(-100, start, end, plotLeft, plotWidth));
        Eq("鼠标移出右边界时夹在区间终点", end,
            ChartMath.TimeOf(99999, start, end, plotLeft, plotWidth));

        // ---- 像素列聚合：长会话"所有数据都画了"靠它 ----
        var samples = new List<ChartSample>
        {
            new(start, 1, false),
            new(start.AddSeconds(3), 0, true),      // 只丢包的列
            new(start.AddSeconds(6), 9, false)
        };

        var buckets = new ChartMath.ChartBucket[3];
        ChartMath.Aggregate(samples, start, start.AddSeconds(6), 3, buckets);
        Check("丢包单独成列：有丢包、没有成功采样", buckets[1].HasLoss && !buckets[1].HasOk);
        Eq("成功列的最小值", 1.0, buckets[0].Min);
        Eq("成功列的最大值", 9.0, buckets[2].Max);

        var dense = Enumerable.Range(0, 100)
            .Select(i => new ChartSample(start.AddMilliseconds(i * 100), i % 2 == 0 ? 10 + i : 5 + i, false))
            .ToList();
        var wide = new ChartMath.ChartBucket[10];
        ChartMath.Aggregate(dense, start, start.AddSeconds(10), 10, wide);
        Eq("聚合后样本总数不丢（一条都没被采样掉）", 100, wide.Sum(b => b.OkCount));
        Eq("每列都记录了该列的最大值（尖刺不会被抽掉）", 108.0, wide.Max(b => b.Max));
        Eq("每列都记录了该列的最小值", 6.0, wide.Min(b => b.Min));

        var outside = new ChartMath.ChartBucket[4];
        ChartMath.Aggregate(dense, start.AddMinutes(5), start.AddMinutes(6), 4, outside);
        Eq("区间外没有采样时不产生任何列", 0, outside.Sum(b => b.OkCount + b.LostCount));

        // ---- 悬停吸附 ----
        var line = new List<ChartSample> { new(start, 1, false), new(start.AddSeconds(1), 2, false), new(start.AddSeconds(2), 3, false) };
        Eq("吸附到最近的采样点", 2, ChartMath.NearestIndex(line, start.AddSeconds(1.6), TimeSpan.FromSeconds(1)));
        Eq("超出容差就不硬指一个远处的点", -1, ChartMath.NearestIndex(line, start.AddSeconds(30), TimeSpan.FromSeconds(2)));
        Eq("空曲线返回 -1", -1, ChartMath.NearestIndex(Array.Empty<ChartSample>(), start, TimeSpan.FromSeconds(5)));
        Eq("二分定位：第一个不早于给定时刻的下标", 1, ChartMath.LowerBound(line, start.AddSeconds(1)));

        // ---- 缩放：以鼠标位置为锚点 ----
        var floor = start;
        var ceiling = start.AddHours(1);
        var anchor = start.AddMinutes(30);
        var zoomed = ChartMath.ZoomRange(floor, ceiling, anchor, 0.5, TimeSpan.FromSeconds(5), floor, ceiling);
        Near("缩放后跨度减半", 30.0, (zoomed.End - zoomed.Start).TotalMinutes, 0.01);
        Near("鼠标指着的时刻在缩放前后位置不变（不会跑掉）",
            0.5, (anchor - zoomed.Start).TotalMinutes / (zoomed.End - zoomed.Start).TotalMinutes, 0.01);

        var edge = ChartMath.ZoomRange(floor, ceiling, start.AddMinutes(2), 0.25, TimeSpan.FromSeconds(5), floor, ceiling);
        Near("靠边缩放时锚点位置同样保持（等于原来的相对位置）",
            2.0 / 60, (start.AddMinutes(2) - edge.Start).TotalMinutes / (edge.End - edge.Start).TotalMinutes, 0.02);
        Check("缩放不会越出会话范围", edge.Start >= floor && edge.End <= ceiling,
            $"{edge.Start:HH:mm:ss}~{edge.End:HH:mm:ss}");

        var tiny = ChartMath.ZoomRange(floor, ceiling, anchor, 0.00001, TimeSpan.FromSeconds(5), floor, ceiling);
        Check("缩放有下限（不会缩到 0 秒）", (tiny.End - tiny.Start) >= TimeSpan.FromSeconds(5),
            (tiny.End - tiny.Start).TotalSeconds.ToString("F1"));

        // 会话比"最小缩放粒度"还短时，最小粒度不能反过来把缩放锁死（短会话里滚轮必须还有反应）
        var shortSession = ChartMath.ZoomRange(
            floor, floor.AddSeconds(4), floor.AddSeconds(2), 0.5, TimeSpan.FromSeconds(2), floor, floor.AddSeconds(4));
        Check("短会话也能缩放（最小粒度不会锁死）",
            (shortSession.End - shortSession.Start) < TimeSpan.FromSeconds(4),
            (shortSession.End - shortSession.Start).TotalSeconds.ToString("F2"));

        var outZoom = ChartMath.ZoomRange(floor, ceiling, anchor, 1000, TimeSpan.FromSeconds(5), floor, ceiling);
        Check("缩到底就是全程（不会超出会话）", outZoom.Start == floor && outZoom.End == ceiling,
            $"{outZoom.Start:HH:mm:ss}~{outZoom.End:HH:mm:ss}");

        // ---- 平移：不越出会话 ----
        var viewStart = start.AddMinutes(10);
        var viewEnd = start.AddMinutes(20);
        var right = ChartMath.PanRange(viewStart, viewEnd, TimeSpan.FromHours(5), floor, ceiling);
        Check("向右拖到底会停在会话末尾", right.End == ceiling && (right.End - right.Start) == TimeSpan.FromMinutes(10),
            $"{right.Start:HH:mm:ss}~{right.End:HH:mm:ss}");

        var left = ChartMath.PanRange(viewStart, viewEnd, TimeSpan.FromHours(-5), floor, ceiling);
        Check("向左拖到底会停在会话开头", left.Start == floor && (left.End - left.Start) == TimeSpan.FromMinutes(10),
            $"{left.Start:HH:mm:ss}~{left.End:HH:mm:ss}");

        Eq("中间范围平移多少就是多少", start.AddMinutes(12),
            ChartMath.PanRange(viewStart, viewEnd, TimeSpan.FromMinutes(2), floor, ceiling).Start);
    }

    // ---------------- ③ CopyFrom 的漂移守卫 ----------------

    private static void TestStatRowCopyFrom()
    {
        Section("统计行复制：加了字段没同步搬就会被断言抓住");

        // CopyFrom 只搬"测试结果"字段：Plot 是界面状态；运营商那几个字段由 ApplyCarrier 单独写。
        // 这条用例的价值是防止以后新增统计列（例如抖动）时漏搬——ICMP 那次就是漏搬字段吃的亏。
        var skip = new HashSet<string>(StringComparer.Ordinal)
        {
            "Plot", "LocalCarrier", "LocalExitText", "RemoteCarrier", "CrossNetText", "CrossNetState", "CarrierDetail"
        };

        var source = new StatRow { Key = "机房B|127.0.0.1|9001|TCP", LinkName = "机房B", Name = "机房B / TCP", Target = "127.0.0.1:9001" };
        var props = typeof(StatRow).GetProperties()
            .Where(p => p is { CanRead: true, CanWrite: true } && !skip.Contains(p.Name))
            .ToList();

        foreach (var p in props)
        {
            object value = p.PropertyType == typeof(int) ? 42 : "v-" + p.Name;
            p.SetValue(source, value);
        }

        var target = new StatRow { Key = source.Key, LinkName = source.LinkName, Name = source.Name, Target = source.Target };
        target.CopyFrom(source);

        foreach (var p in props)
        {
            Eq($"CopyFrom 搬了 {p.Name}", p.GetValue(source)?.ToString() ?? "", p.GetValue(target)?.ToString() ?? "");
        }

        target.Plot = true;
        var again = new StatRow { Key = source.Key };
        again.CopyFrom(target);
        Check("CopyFrom 不会把界面勾选状态一起搬走", !again.Plot);
    }

    // ---------------- ⑤ 真的画出来了吗（像素级） ----------------

    /// <summary>
    /// 没有视觉模型可看，就用**像素**证明曲线确实画到了图上：
    /// 按配色去数像素，数得到才说明曲线/刻度/阈值线/丢包标记真的落到了画面上。
    /// （好不好看只能靠人看 `--render-ui` 出的 PNG，这部分断言替代不了。）
    /// </summary>
    private static void TestChartRendersCurves()
    {
        Section("RTT 曲线：真实渲染（按像素颜色验证曲线/刻度/阈值线/丢包标记都画出来了）");

        var history = new ChartHistory();
        var t0 = new DateTime(2026, 9, 21, 10, 0, 0);
        var rnd = new Random(7);

        // 注意：登记键必须和 ProbeResult 自己算出来的键一致（链路名|主机|端口|协议）。
        // 不一致时 Add 找不到曲线，图上只剩一句"请先勾选链路"的提示——
        // 而"没有曲线"和"画错了"在截图上长得一模一样，所以下面先断言数据真的进去了。
        history.Select("机房A|127.0.0.1|1|TCP", "机房A", "TCP", "127.0.0.1:1");
        history.Select("机房B|127.0.0.1|1|UDP", "机房B", "UDP", "127.0.0.1:1");

        for (var i = 0; i < 40; i++)
        {
            var time = t0.AddSeconds(i);
            history.Add(ChartSampleResult("机房A", "127.0.0.1", 1, "TCP", time, 20 + rnd.NextDouble() * 30, i is 10 or 11 or 12));
            history.Add(ChartSampleResult("机房B", "127.0.0.1", 1, "UDP", time, 200 + rnd.NextDouble() * 60, false));
        }

        Eq("两条曲线都收到了采样", 40, history.SelectedSeries[0].Count);

        RunOnSta(() =>
        {
            const int width = 700;
            const int height = 320;

            byte[] Grab(RttChart target, out int stride)
            {
                target.Measure(new Size(width, height));
                target.Arrange(new Rect(0, 0, width, height));
                target.UpdateLayout();

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(target);

                stride = width * 4;
                var buffer = new byte[stride * height];
                bitmap.CopyPixels(buffer, stride, 0);
                return buffer;
            }

            int CountNear(byte[] buffer, Color target, int tolerance)
            {
                var count = 0;
                for (var i = 0; i + 3 < buffer.Length; i += 4)
                {
                    var alpha = buffer[i + 3];
                    if (alpha < 40) continue;

                    // RenderTargetBitmap 是**预乘 alpha**：先还原成原色再比。
                    // 不还原的话，抗锯齿的细线（1.4px 的曲线）像素会被整体拉暗，
                    // 看起来像"根本没画"——这条差点把我自己骗过去。
                    var b = buffer[i] * 255 / alpha;
                    var g = buffer[i + 1] * 255 / alpha;
                    var r = buffer[i + 2] * 255 / alpha;

                    if (Math.Abs(r - target.R) <= tolerance &&
                        Math.Abs(g - target.G) <= tolerance &&
                        Math.Abs(b - target.B) <= tolerance) count++;
                }

                return count;
            }

            var chart = new RttChart { Width = width, Height = height, WarningMs = 150 };
            chart.SetSeries(history.SelectedSeries);
            chart.Refresh();

            var pixels = Grab(chart, out _);

            var first = CountNear(pixels, RttChart.SeriesColor(0), 20);
            var second = CountNear(pixels, RttChart.SeriesColor(1), 20);

            // 曲线为空时先分清是"没数据"还是"没画"：这一步把聚合单独跑一遍
            var view = chart.ViewRange!.Value;
            var probe = new ChartMath.ChartBucket[600];
            ChartMath.Aggregate(chart.Series[0].Samples, view.Start, view.End, 600, probe);
            Check("视图范围内聚合得出成功采样（否则曲线必然是空的）",
                probe.Sum(x => x.OkCount) > 0,
                $"ok={probe.Sum(x => x.OkCount)} lost={probe.Sum(x => x.LostCount)} " +
                $"view={view.Start:HH:mm:ss.fff}~{view.End:HH:mm:ss.fff} " +
                $"span={(view.End - view.Start).TotalSeconds:F1}s 采样={chart.Series[0].Count}");

            Check("第 1 条曲线画到了图上（按配色数得到像素）", first > 100, first + " 像素");
            Check("第 2 条曲线画到了图上（两条颜色不同，不会混成一条）", second > 100, second + " 像素");

            var ticks = CountNear(pixels, Color.FromRgb(0x5F, 0x63, 0x68), 16);      // Y/X 轴刻度文字
            Check("坐标轴刻度文字画出来了", ticks > 200, ticks + " 像素");

            var grid = CountNear(pixels, Color.FromRgb(0xEC, 0xEE, 0xF1), 3);
            Check("网格线画出来了", grid > 500, grid + " 像素");

            var warn = CountNear(pixels, Color.FromRgb(0xB0, 0x60, 0x10), 16);       // 阈值标注
            Check("阈值标注画出来了", warn > 20, warn + " 像素");

            var loss = CountNear(pixels, Color.FromRgb(0xC5, 0x22, 0x1F), 20);       // 丢包空心红点
            Check("丢包标记画出来了（红点）", loss > 5, loss + " 像素");

            // ---- Y 轴必须跟着数据走：这是现场反馈（"纵坐标不该是个固定值"）的渲染级回归 ----
            // 单独一张图：只有一条峰值 23ms 的曲线、阈值设 150ms。
            // 轴若被阈值/固定值顶高，曲线会被压成底部一条细带（约 30px）；
            // 按数据自适应则占掉大半个绘图区（约 200px）。
            var solo = new ChartHistory();
            solo.Select("机房D|127.0.0.1|1|TCP", "机房D", "TCP", "127.0.0.1:1");
            for (var i = 0; i < 40; i++)
                solo.Add(ChartSampleResult("机房D", "127.0.0.1", 1, "TCP", t0.AddSeconds(i), 20 + rnd.NextDouble() * 3, false));

            var soloChart = new RttChart { Width = width, Height = height, WarningMs = 150 };
            soloChart.SetSeries(solo.SelectedSeries);
            soloChart.Refresh();

            var soloPixels = Grab(soloChart, out var soloStride);
            var soloColor = RttChart.SeriesColor(0);

            var top = int.MaxValue;
            var bottom = int.MinValue;
            for (var i = 0; i + 3 < soloPixels.Length; i += 4)
            {
                var alpha = soloPixels[i + 3];
                if (alpha < 40) continue;
                if (Math.Abs(soloPixels[i + 2] * 255 / alpha - soloColor.R) > 20) continue;
                if (Math.Abs(soloPixels[i + 1] * 255 / alpha - soloColor.G) > 20) continue;
                if (Math.Abs(soloPixels[i] * 255 / alpha - soloColor.B) > 20) continue;

                var row = i / soloStride;
                if (row < top) top = row;
                if (row > bottom) bottom = row;
            }

            // 23ms 的数据在 0~30ms 的刻度上应该落在绘图区的**上半部分**；
            // 若轴还按 150ms 阈值/固定值走（0~200ms），同样的数据会贴在底部约 30 像素处。
            var spanned = bottom - top;
            Check("Y 轴按实际数据自适应：23ms 的曲线落在绘图区上半部分（没被固定刻度压到底部）",
                top < 153, $"曲线最高点在第 {top} 行（绘图区约第 15~292 行，中线 153）");
            Check("曲线自身的波动也能看清（不是压成一条 1 像素的细线）",
                spanned > 15, $"曲线纵向跨度 {spanned} 像素");
        }, "曲线渲染断言");
    }

    // ---------------- ⑥ 统计计数器：会话累计 ----------------

    /// <summary>
    /// 现场反馈："发送包数最大只到 3599 或 3598 就不再涨了，但实际还在发"。
    /// 真因：那一列显示的是**滑动窗口内**的样本数（1 秒 1 个包 × 窗口 60 分钟 = 3600）。
    /// 这条用 1 分钟的窗口喂 150 条样本，把旧行为（停在 60）与新行为（150）区分开。
    /// </summary>
    private static void TestSentCounterIsCumulative()
    {
        Section("统计：发送/丢包是会话累计（不再卡在窗口上限）");

        var session = new StatSession();
        session.Configure(new AppConfig
        {
            StatWindowMinutes = 1,
            WarningRttMs = 300,
            LossWarnPercent = 1.0,
            ConsecutiveLossWarn = 3
        });

        var t0 = new DateTime(2026, 9, 21, 10, 0, 0);

        for (var i = 0; i < 150; i++)
        {
            var lost = i % 50 == 0;      // 150 条里丢 3 条
            session.Add(ChartSampleResult("机房B", "127.0.0.1", 9001, "TCP", t0.AddSeconds(i), 10 + i, lost));
        }

        var rows = session.BuildRows();
        Eq("统计行存在", 1, rows.Count);

        var row = rows[0];
        Eq("发送数是会话累计（旧行为会停在窗口内的 60）", 150, row.Sent);
        Eq("丢包数也是会话累计", 3, row.Lost);
        Eq("「发送/丢包」显示累计值", "150 / 3", row.SentLost);
        Eq("丢包率按累计算", "2.00%", row.LossRate);
        Check("状态仍按窗口判定（故障要能恢复，不该被历史稀释）", row.Status.Length > 0, row.Status);

        // 继续发 → 继续涨
        for (var i = 150; i < 200; i++)
            session.Add(ChartSampleResult("机房B", "127.0.0.1", 9001, "TCP", t0.AddSeconds(i), 12, false));

        Eq("继续发包时计数继续增长", 200, session.BuildRows()[0].Sent);

        // 「清除」才是归零点
        session.Clear();
        Check("清除后统计行消失（累计计数一并归零）", session.BuildRows().Count == 0);
    }

    // ---------------- ⑦ 历史日志读取 ----------------

    private static async Task TestChartLogReaderAsync()
    {
        Section("RTT 曲线：从 logs 日志读历史（跨会话/跨重启）");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "chartlog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            // 文件名规则必须与 CsvLogWriter 完全一致，否则历史永远是空的：这里用真实的写入器落盘再读回来
            var logs = Path.Combine(root, "logs");
            var yesterday = DateTime.Today.AddDays(-1);

            using (var writer = new CsvLogWriter(logs))
            {
                for (var i = 0; i < 5; i++)
                {
                    writer.Enqueue(ChartSampleResult("机房B", "127.0.0.1", 9001, "TCP",
                        yesterday.AddHours(10).AddSeconds(i), 20 + i, false));
                }

                writer.Enqueue(ChartSampleResult("机房B", "127.0.0.1", 9001, "UDP",
                    yesterday.AddHours(10), 99, false));

                await writer.WaitForIdleAsync(TimeSpan.FromSeconds(5));
            }

            var reader = new ChartLogReader(logs);
            var key = "机房B|127.0.0.1|9001|TCP";

            var split = ChartLogReader.SplitKey(key);
            Check("曲线键能映射到日志文件与协议", split is not null);
            Eq("文件名规则与日志写入器一致", "机房B_127.0.0.1_9001.csv", split!.Value.FileName);
            Eq("协议取自键的末段", "TCP", split.Value.Protocol);

            Check("列出的可用日期包含昨天", reader.AvailableDays().Contains(yesterday),
                string.Join(",", reader.AvailableDays().Select(d => d.ToString("MM-dd"))));

            var loaded = reader.Load(new[] { key }, yesterday, yesterday.AddDays(1));
            Eq("读到昨天的 5 条 TCP 采样", 5, loaded[key].Count);
            Eq("按协议过滤（UDP 那条不算进 TCP）", 20.0, loaded[key][0].RttMs);
            Check("采样按时间有序", loaded[key].Select(s => s.Time).SequenceEqual(
                Enumerable.Range(0, 5).Select(i => yesterday.AddHours(10).AddSeconds(i))));

            var udp = reader.Load(new[] { "机房B|127.0.0.1|9001|UDP" }, yesterday, yesterday.AddDays(1));
            Eq("同一条链路的另一个协议也读得到", 1, udp["机房B|127.0.0.1|9001|UDP"].Count);

            // 时间范围过滤：只要前 2 秒
            var window = reader.Load(new[] { key }, yesterday.AddHours(10), yesterday.AddHours(10).AddSeconds(1.5));
            Eq("按时间范围过滤", 2, window[key].Count);

            // 增量：日志又追加了内容 → 再读只补新增，不重复
            File.AppendAllText(reader.PathFor(yesterday, split.Value.FileName),
                CsvLogWriter.FormatRow(ChartSampleResult("机房B", "127.0.0.1", 9001, "TCP",
                    yesterday.AddHours(10).AddSeconds(10), 42, false)) + Environment.NewLine);

            var again = reader.Load(new[] { key }, yesterday, yesterday.AddDays(1));
            Eq("追加后读到 6 条（增量读取，不重复）", 6, again[key].Count);
            Eq("新增的就是那一行", 42.0, again[key][^1].RttMs);

            // 半行安全：日志是边写边读的，末尾那行可能还没写完——不能吃掉它
            // （日期必须由 yesterday 推出来，不能写死：写死的话跨零点跑就会落在查询范围之外）
            var partialStamp = yesterday.AddHours(10).AddSeconds(11);
            File.AppendAllText(reader.PathFor(yesterday, split.Value.FileName),
                $"{partialStamp:yyyy-MM-dd HH:mm:ss.fff},127.0.0");
            var halfWritten = reader.Load(new[] { key }, yesterday, yesterday.AddDays(1));
            Eq("写到一半的行先不算", 6, halfWritten[key].Count);

            File.AppendAllText(reader.PathFor(yesterday, split.Value.FileName), ".1,9001,TCP,7.000,0,1.000,9,pid,-," + Environment.NewLine);
            var completed = reader.Load(new[] { key }, yesterday, yesterday.AddDays(1));
            Check("补齐之后那一行被读到（没有丢）", completed[key].Count == 7,
                $"实际 {completed[key].Count}，最后一条 RTT={completed[key][^1].RttMs}");

            // 坏行不该让整份历史读不出来
            File.AppendAllText(reader.PathFor(yesterday, split.Value.FileName),
                "这不是一行日志" + Environment.NewLine + ",,,," + Environment.NewLine);
            var withJunk = reader.Load(new[] { key }, yesterday, yesterday.AddDays(1));
            Eq("坏行被跳过，好行照读", 7, withJunk[key].Count);

            // 文件被清空/替换 → 缓存要自愈
            File.WriteAllText(reader.PathFor(yesterday, split.Value.FileName), CsvLogWriter.Header + Environment.NewLine);
            var truncated = reader.Load(new[] { key }, yesterday, yesterday.AddDays(1));
            Eq("文件被截断后能自愈（不会读出旧数据）", 0, truncated[key].Count);

            // 不存在的日期：返回空而不是抛异常
            var missing = reader.Load(new[] { key }, yesterday.AddDays(-10), yesterday.AddDays(-9));
            Eq("没有日志的日期返回空", 0, missing[key].Count);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ---------------- ⑧ 历史范围（真实窗口） ----------------

    /// <summary>
    /// 现场要求：**重启测试之后，RTT 曲线还能查看上一个会话的数据**；
    /// 默认看最近 24 小时，也可以自己选日期。数据来自 logs 里的 CSV（逐包落盘的原始记录）。
    /// </summary>
    private static void TestChartHistoryUi()
    {
        Section("RTT 曲线：历史范围（默认最近 24 小时 / 可选日期 / 跨会话）");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "charthist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        var echoServer = StartEchoServer(out var echoPort);
        if (echoServer is null)
        {
            Check("回显服务启动", false, "找不到可绑定端口");
            ConfigManager.OverridePath = previousOverride;
            return;
        }

        // 造两份"上一个会话"的日志：
        // ① 12 小时前（一定落在"最近 24 小时"里，用来验证 24 小时视图包含历史数据）
        // ② 3 天前（用来验证"选日期"）
        // 注意日期必须由 DateTime.Now 推出来：写死日期会在跨零点/跨时段跑时落到范围之外
        var logs = Path.Combine(AppContext.BaseDirectory, "logs");
        var recentTime = DateTime.Now.AddHours(-12);
        var olderDay = DateTime.Today.AddDays(-3);

        var historyFiles = new List<(string Path, bool DirectoryExisted)>
        {
            (Path.Combine(logs, CsvLogWriter.FolderNameFor(recentTime), $"本机_127.0.0.1_{echoPort}.csv"),
                Directory.Exists(Path.Combine(logs, CsvLogWriter.FolderNameFor(recentTime)))),
            (Path.Combine(logs, CsvLogWriter.FolderNameFor(olderDay), $"本机_127.0.0.1_{echoPort}.csv"),
                Directory.Exists(Path.Combine(logs, CsvLogWriter.FolderNameFor(olderDay))))
        };

        void WriteFakeLog(string path, IReadOnlyList<DateTime> stamps, double baseRtt)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var rows = new List<string> { CsvLogWriter.Header };
            for (var i = 0; i < stamps.Count; i++)
            {
                rows.Add(CsvLogWriter.FormatRow(ChartSampleResult("本机", "127.0.0.1", echoPort, "TCP",
                    stamps[i], baseRtt + i, false)));
            }

            File.WriteAllLines(path, rows);
        }

        WriteFakeLog(historyFiles[0].Path,
            Enumerable.Range(0, 5).Select(i => recentTime.AddSeconds(i)).ToList(), 30);
        WriteFakeLog(historyFiles[1].Path,
            Enumerable.Range(0, 6).Select(i => olderDay.AddHours(9).AddSeconds(i)).ToList(), 50);

        ConfigManager.Save(new AppConfig
        {
            ListenPort = FindBindablePort(),
            IntervalMs = 300,
            TimeoutMs = 1000,
            EnableTcp = true,
            EnableUdp = false,
            EnableIcmp = false,
            Isp = new IspConfig { Enabled = false },
            Links = { new LinkConfig { Name = "本机", Host = "127.0.0.1", Port = echoPort, Tcp = true, Udp = false } }
        });

        try
        {
            RunOnSta(() =>
            {
                var window = new MainWindow();
                var tabs = (TabControl)window.FindName("MainTabs")!;
                var panel = (RttChartPanel)window.FindName("ChartPanel")!;
                var chart = panel.ChartControl;

                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1360, 900));
                content.Arrange(new Rect(0, 0, 1360, 900));
                content.UpdateLayout();

                Eq("默认范围是「最近 24 小时」", ChartRange.Last24Hours, panel.Range);

                InvokePrivateHandler(window, "Start_Click");
                Pump(2000);

                var stats = (System.Collections.IList)GetPrivateField(window, "_stats")!;
                var tcpRow = stats.Cast<StatRow>().First(r => r.Protocol == "TCP");
                tcpRow.Plot = true;
                Pump(600);

                tabs.SelectedItem = window.FindName("ChartTab");
                Pump(1500);

                // 默认的 24 小时视图里应该同时有 12 小时前那份"上一个会话"的数据
                var rangeText = (TextBlock)panel.FindName("RangeText")!;
                Check("24 小时视图读的是 logs 日志", rangeText.Text.Contains("来源：logs 日志"), rangeText.Text);
                Check("默认 24 小时视图里包含上一个会话的历史数据",
                    chart.Series.Count == 1 && chart.Series[0].Samples.Any(s => s.Time < DateTime.Now.AddHours(-11)),
                    $"{(chart.Series.Count > 0 ? chart.Series[0].Count : 0)} 条采样");

                // 时间窗计算（不依赖数据，纯逻辑也该对）
                var windows = panel.RangeWindow();
                Check("「最近 24 小时」的时间窗是 [现在-24h, 现在]",
                    panel.Range == ChartRange.Last24Hours &&
                    Math.Abs((windows.To - DateTime.Now).TotalMinutes) < 1 &&
                    Math.Abs((windows.From - DateTime.Now.AddHours(-24)).TotalMinutes) < 1,
                    $"{windows.From:MM-dd HH:mm} ~ {windows.To:MM-dd HH:mm}");

                // 选具体日期 = 3 天前 → 只看到那天那 6 条
                var rangeBox = (ComboBox)panel.FindName("RangeBox")!;
                var dayPicker = (DatePicker)panel.FindName("DayPicker")!;

                rangeBox.SelectedIndex = (int)ChartRange.SpecificDay;
                dayPicker.SelectedDate = olderDay;
                Pump(800);

                Eq("选日期后图上只有一条曲线", 1, chart.Series.Count);
                Eq("读到了那天的全部 6 条采样", 6, chart.Series[0].Count);
                Check("读到的确实是那天的数据",
                    chart.Series[0].Samples.All(s => s.Time.Date == olderDay && s.RttMs >= 50),
                    chart.Series[0].Samples.Count > 0 ? chart.Series[0].Samples[0].Time.ToString("MM-dd HH:mm:ss") : "无");

                // 「昨天」快捷项走的是另一套时间窗计算，也要给对范围
                rangeBox.SelectedIndex = (int)ChartRange.Yesterday;
                Pump(500);
                var yesterdayWindow = panel.RangeWindow();
                Check("「昨天」的时间窗是 [昨天 00:00, 今天 00:00]",
                    yesterdayWindow.From == DateTime.Today.AddDays(-1) && yesterdayWindow.To == DateTime.Today,
                    $"{yesterdayWindow.From:MM-dd HH:mm} ~ {yesterdayWindow.To:MM-dd HH:mm}");

                // 没有日志的日期：明确说"没有记录"，而不是画一张空图让人猜
                rangeBox.SelectedIndex = (int)ChartRange.SpecificDay;
                dayPicker.SelectedDate = DateTime.Today.AddDays(-10);
                Pump(800);
                Check("没有日志的日期上没有任何采样", chart.Series.All(s => s.Count == 0),
                    string.Join(",", chart.Series.Select(s => s.Count)));
                Check("并说明为什么是空的", rangeText.Text.Contains("没有") && rangeText.Text.Contains("logs"), rangeText.Text);

                // 切回本会话：看到的是内存里正在记的实时数据
                rangeBox.SelectedIndex = (int)ChartRange.Session;
                Pump(1200);
                Check("切回「本会话」后显示的是实时采样（今天 的）",
                    chart.Series.Count == 1 && chart.Series[0].Samples.Any(s => s.Time >= DateTime.Today),
                    $"采样 {(chart.Series.Count > 0 ? chart.Series[0].Count : 0)} 条");
                Check("本会话来源标注正确", rangeText.Text.Contains("来源：本会话"), rangeText.Text);

                InvokePrivateHandler(window, "Stop_Click");
                window.Close();
            }, "RTT 曲线历史范围断言");
        }
        finally
        {
            echoServer.Dispose();

            foreach (var (path, directoryExisted) in historyFiles)
            {
                try { File.Delete(path); } catch { }

                var directory = Path.GetDirectoryName(path)!;
                if (!directoryExisted) { try { Directory.Delete(directory, true); } catch { } }
            }

            ConfigManager.OverridePath = previousOverride;
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ---------------- ④ 真实窗口 ----------------

    private static void TestChartUi()
    {
        Section("RTT 曲线：勾选链路出图、缩放与悬停");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "chart_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousOverride = ConfigManager.OverridePath;
        ConfigManager.OverridePath = Path.Combine(root, "appsettings.json");

        var echoServer = StartEchoServer(out var echoPort);
        if (echoServer is null)
        {
            Check("回显服务启动", false, "找不到可绑定端口");
            ConfigManager.OverridePath = previousOverride;
            return;
        }

        ConfigManager.Save(new AppConfig
        {
            ListenPort = FindBindablePort(),
            IntervalMs = 300,
            TimeoutMs = 1000,
            EnableTcp = true,
            EnableUdp = true,
            EnableIcmp = false,
            WarningRttMs = 300,
            Isp = new IspConfig { Enabled = false },
            Links = { new LinkConfig { Name = "本机", Host = "127.0.0.1", Port = echoPort, Tcp = true, Udp = true } }
        });

        try
        {
            RunOnSta(() =>
            {
                var window = new MainWindow();
                var tabs = (TabControl)window.FindName("MainTabs")!;

                Check("有一个独立的「RTT 曲线」页", window.FindName("ChartTab") is TabItem { Header: "RTT 曲线" });

                var panel = (RttChartPanel)window.FindName("ChartPanel")!;
                var chart = panel.ChartControl;

                // 窗口没有 Show()，布局不会自动跑；曲线的命中区（绘图区）依赖实际尺寸，
                // 不先强制一次布局，控件的宽高还是 0，缩放与悬停会直接跳过（这条踩过）
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1360, 900));
                content.Arrange(new Rect(0, 0, 1360, 900));
                content.UpdateLayout();

                var statsGrid = (DataGrid)window.FindName("StatsGrid")!;
                Check("统计表第一列是「曲线」勾选框",
                    statsGrid.Columns.Count > 0 && Equals(statsGrid.Columns[0].Header, "曲线"),
                    statsGrid.Columns.Count > 0 ? statsGrid.Columns[0].Header?.ToString() ?? "" : "无列");
                Check("勾选列是复选框列", statsGrid.Columns[0] is DataGridCheckBoxColumn);

                InvokePrivateHandler(window, "Start_Click");
                Pump(2500);

                var stats = (System.Collections.IList)GetPrivateField(window, "_stats")!;
                Check("统计行已经出现", stats.Count >= 2, "行数=" + stats.Count);

                var tcpRow = stats.Cast<StatRow>().First(r => r.Protocol == "TCP");
                Eq("统计行能解析出协议名（曲线名要用）", "TCP", tcpRow.Protocol);

                tabs.SelectedItem = window.FindName("ChartTab");
                Pump(300);
                Check("还没勾选时提示怎么用", chart.Series.Count == 0);

                // 现场反馈的回归：**晚勾选的链路必须能看到勾选之前的数据**
                // （记录与显示必须解耦：勾选只管画不画）
                var history = (ChartHistory)GetPrivateField(window, "_chartHistory")!;
                var recorded = history.Find(tcpRow.Key)?.Count ?? 0;
                Check("勾选之前就已经在记录这条链路了", recorded >= 3, recorded + " 条");
                Check("没勾选的数据不在图上显示（但已经记着）",
                    chart.Series.Count == 0 && history.RecordedCount > 0,
                    $"记录 {history.RecordedCount} 条");

                tcpRow.Plot = true;      // 等价于用户点了勾选框
                Pump(3500);

                Eq("勾选后出现一条曲线", 1, chart.Series.Count);
                Check("曲线名 = 链路名称 + 协议名称", chart.Series[0].Name.StartsWith("本机 · "), chart.Series[0].Name);
                Check("勾选后立刻包含勾选之前记录的那整段数据",
                    chart.Series[0].Count >= recorded, $"{recorded} 条（勾选前）→ {chart.Series[0].Count} 条");
                Check("曲线按会话全程展示（默认不缩放）", !chart.IsZoomed);
                Check("已经开始收采样", chart.Series[0].Count >= 2, "采样数=" + chart.Series[0].Count);

                var range = chart.ViewRange;
                var session = chart.SessionRange;
                Check("视图范围等于会话范围（全程）",
                    range is not null && session is not null && range.Value.Start == session.Value.Start,
                    range is null ? "无视图" : $"{range.Value.Start:HH:mm:ss.fff}~{range.Value.End:HH:mm:ss.fff}");

                // 滚轮缩放：真实事件走一遍控件的处理路径
                var before = chart.ViewRange!.Value;
                var spanBefore = before.End - before.Start;
                chart.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.MouseWheelEvent });
                Pump(200);

                var after = chart.ViewRange!.Value;
                var spanAfter = after.End - after.Start;
                Check("滚轮向上 = 放大（视图范围变小）", spanAfter < spanBefore,
                    $"{spanBefore.TotalSeconds:F1} 秒 → {spanAfter.TotalSeconds:F1} 秒");
                Check("缩放后进入「已缩放」状态（全览按钮可用）", chart.IsZoomed);
                Check("缩放不会越出会话范围", after.Start >= session!.Value.Start && after.End <= session.Value.End,
                    $"{after.Start:HH:mm:ss.fff}~{after.End:HH:mm:ss.fff}");

                chart.ResetView();
                Pump(200);
                Check("「全览」回到会话全程", !chart.IsZoomed);

                // 图例：一个条目，点击可隐藏
                // （注意：图例在 UserControl 自己的命名域里，要用面板去 FindName，不能从窗口找）
                var legend = (ItemsControl)panel.FindName("Legend")!;
                Check("图例里有一条曲线", legend.Items.Count == 1, "条目数=" + legend.Items.Count);
                var legendItem = (ChartLegendItem)legend.Items[0]!;
                Eq("图例文字就是曲线名", chart.Series[0].Name, legendItem.Name);

                chart.SetHidden(legendItem.Key, true);
                Eq("隐藏后可见曲线数为 0", 0, chart.VisibleCount);
                chart.SetHidden(legendItem.Key, false);
                Eq("再点一次恢复显示", 1, chart.VisibleCount);

                // 取消勾选：曲线从图上消失，但历史留着（重新勾选仍是完整一条）
                var kept = chart.Series[0].Count;
                tcpRow.Plot = false;
                Pump(600);
                Eq("取消勾选后图上不再有这条曲线", 0, chart.Series.Count);

                tcpRow.Plot = true;
                Pump(300);
                Eq("重新勾选后曲线回来", 1, chart.Series.Count);
                Check("历史没有因为取消勾选而清空", chart.Series[0].Count >= kept, $"{kept} → {chart.Series[0].Count}");

                InvokePrivateHandler(window, "Stop_Click");
                window.Close();
            }, "RTT 曲线界面断言");
        }
        finally
        {
            echoServer.Dispose();
            ConfigManager.OverridePath = previousOverride;
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
