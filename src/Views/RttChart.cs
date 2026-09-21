using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LineTester.Views;

/// <summary>
/// RTT 曲线（自绘，零依赖）。
///
/// 为什么不用 LiveCharts / OxyPlot / MvvmChart 这类现成库：本项目**零 NuGet 依赖**
/// （本机 NuGet 不可达，还靠这条保证自检宿主能跑），引库会破坏底线。
/// 交互模式则照搬成熟图表的做法：滚轮**以鼠标为锚点**缩放、左键拖拽平移、
/// 双击回到全程、悬停竖线吸附到最近采样时刻并联动显示所有曲线的值。
///
/// 分两层画：
/// <list type="bullet">
/// <item><b>曲线层</b>（<see cref="_baseLayer"/>）：网格、坐标轴、阈值线、曲线、丢包标记。
/// 只在数据变化 / 尺寸变化 / 缩放平移时重画。</item>
/// <item><b>交互层</b>（<see cref="_hoverLayer"/>）：竖线、吸附点、提示框。
/// 鼠标每次移动只重画这一层——否则长会话下鼠标一动就要重算几万个点。</item>
/// </list>
///
/// 横轴默认是**会话全程**（不是滚动窗口）：现场要先看"整段时间什么样"，需要细节再缩进去。
/// 长会话用**像素列聚合**（每列画最小/最大包络 + 均值折线）保证数据一条不丢又不会糊成一团。
/// </summary>
public sealed class RttChart : Control
{
    // 颜色跟界面上其它部分同一套（窗口是固定浅色主题，不跟随系统深色）
    private static readonly Brush PanelBrush = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF), 0xF2);
    private static readonly Brush LossBrush = Frozen(Color.FromRgb(0xC5, 0x22, 0x1F), 0x30);
    private static readonly Brush LossDotBrush = Frozen(Color.FromRgb(0xC5, 0x22, 0x1F), 0xFF);
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xE6, 0x7E, 0x22), 0xA0);
    private static readonly Brush WarnTextBrush = Frozen(Color.FromRgb(0xB0, 0x60, 0x10), 0xFF);

    /// <summary>
    /// 曲线配色：Okabe-Ito 色盲友好色板 + 补充色。
    /// 这不是审美问题——现场是 7 条链路 × 3 个协议，颜色必须一眼分得开。
    /// </summary>
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x00, 0x72, 0xB2), // 蓝
        Color.FromRgb(0xD5, 0x5E, 0x00), // 朱红
        Color.FromRgb(0x00, 0x9E, 0x73), // 蓝绿
        Color.FromRgb(0xCC, 0x79, 0xA7), // 紫红
        Color.FromRgb(0xE6, 0x9F, 0x00), // 橙
        Color.FromRgb(0x56, 0xB4, 0xE9), // 天蓝
        Color.FromRgb(0x8C, 0x56, 0x4B), // 棕
        Color.FromRgb(0x5D, 0x3F, 0xD3), // 紫
        Color.FromRgb(0xB7, 0x9F, 0x00), // 暗黄
        Color.FromRgb(0x7F, 0x7F, 0x7F)  // 灰
    };

    private const double LeftPad = 56;
    private const double RightPad = 18;
    private const double TopPad = 14;
    private const double BottomPad = 28;

    /// <summary>最小缩放范围：再细就没意义了（数据本来就是 1 秒 1 个点）。</summary>
    private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(2);

    private readonly VisualCollection _layers;
    private readonly DrawingVisual _baseLayer = new();
    private readonly DrawingVisual _hoverLayer = new();

    private readonly List<ChartSeries> _series = new();
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    private readonly ChartMath.ChartBucket[] _buckets = new ChartMath.ChartBucket[8192];

    private Size _size;
    private DateTime _viewStart;
    private DateTime _viewEnd;
    private bool _hasView;

    /// <summary>
    /// 用户是否手动缩放过。**不能**用"当前视图比会话范围小"来推断：
    /// 数据每秒都在长，视图天然落后一个批次，那样会被误判成"已缩放"从而不再跟随，
    /// 曲线会停在选定那一刻的几秒里不动（这个坑被自检抓住过）。
    /// </summary>
    private bool _userZoomed;

    private bool _dragging;
    private bool _panned;
    private Point _dragOrigin;
    private DateTime _dragViewStart;
    private DateTime _dragViewEnd;

    private Point? _mouse;
    private int _lastSampleCount = -1;
    private string _lastShape = "";

    public RttChart()
    {
        _layers = new VisualCollection(this) { _baseLayer, _hoverLayer };
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>告警阈值：画一条虚线，并保证 Y 轴上界容得下它（否则"有没有超阈值"就看不到了）。</summary>
    public double WarningMs { get; set; } = 300;

    /// <summary>视图（缩放/平移）变化时触发，宿主页用它刷新"当前范围"文案。</summary>
    public event EventHandler? ViewChanged;

    /// <summary>图例配色：宿主页用同一下标取色，保证图例与曲线颜色一致。</summary>
    public static Color SeriesColor(int index) => Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];

    public static int PaletteSize => Palette.Length;

    public IReadOnlyList<ChartSeries> Series => _series;

    protected override int VisualChildrenCount => _layers.Count;

    protected override Visual GetVisualChild(int index) => _layers[index];

    /// <summary>
    /// 自绘控件必须自己做命中测试：没画可见背景时 WPF 认为这里"什么都没有"，
    /// 收不到鼠标事件——那样缩放、平移、悬停会整个失效。
    /// </summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width,
               double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Math.Abs(_size.Width - finalSize.Width) >= 0.5 || Math.Abs(_size.Height - finalSize.Height) >= 0.5)
        {
            _size = finalSize;
            RedrawBase();
            RedrawHover();
        }

        return finalSize;
    }

    // ---------------- 对外接口 ----------------

    /// <summary>设置要画的曲线。顺序即图例顺序，也决定配色下标——顺序稳定颜色才不会乱跳。</summary>
    public void SetSeries(IReadOnlyList<ChartSeries> series)
    {
        _series.Clear();
        _series.AddRange(series);

        // 取消勾选的曲线会连同"隐藏"状态一起失效；重新勾选应当照常出现
        _hidden.RemoveWhere(key => !_series.Any(s => s.Key == key));

        _lastSampleCount = -1;
        SyncView();
        RedrawBase();
        RedrawHover();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetHidden(string key, bool hidden)
    {
        if (hidden) _hidden.Add(key);
        else _hidden.Remove(key);

        _lastSampleCount = -1;
        RedrawBase();
        RedrawHover();
    }

    public bool IsHidden(string key) => _hidden.Contains(key);

    /// <summary>可见曲线的条数（图例里"眼睛"关掉的算隐藏）。</summary>
    public int VisibleCount => _series.Count(s => !_hidden.Contains(s.Key));

    /// <summary>整个会话的时间范围。</summary>
    public (DateTime Start, DateTime End)? SessionRange => FullRange();

    /// <summary>当前视图范围。</summary>
    public (DateTime Start, DateTime End)? ViewRange => _hasView ? (_viewStart, _viewEnd) : null;

    public bool IsZoomed
    {
        get
        {
            if (!_hasView || !_userZoomed) return false;

            var session = FullRange();
            if (session is null) return false;

            return _viewStart > session.Value.Start.AddMilliseconds(1)
                || _viewEnd < session.Value.End.AddMilliseconds(-1);
        }
    }

    /// <summary>
    /// 数据变了（界面每 500 毫秒取一次待处理结果后调用）。
    /// 没有新数据就直接返回，不做无谓重画。
    /// </summary>
    public void Refresh()
    {
        var count = 0;
        foreach (var series in _series) count += series.Count;

        var shape = string.Join(",", _series.Select(s => s.Key)) + "|" + _hidden.Count;
        if (count == _lastSampleCount && shape == _lastShape) return;

        _lastSampleCount = count;
        _lastShape = shape;

        SyncView();
        RedrawBase();
        RedrawHover();
    }

    /// <summary>回到全程（「全览」按钮与双击）。</summary>
    public void ResetView()
    {
        var session = FullRange();
        if (session is not null) (_viewStart, _viewEnd) = session.Value;
        _hasView = session is not null;
        _userZoomed = false;

        ViewChanged?.Invoke(this, EventArgs.Empty);
        RedrawBase();
        RedrawHover();
    }

    /// <summary>
    /// 让视图跟上数据：
    /// 没缩放过（默认"全程"）就跟着会话一起长；用户缩进去了就别动他的视野，
    /// 只把范围夹回会话边界——否则新数据一到，正在看的那一段就被推走了。
    /// </summary>
    private void SyncView()
    {
        var session = FullRange();

        if (session is null)
        {
            _hasView = false;
            return;
        }

        if (!_hasView || !_userZoomed)
        {
            (_viewStart, _viewEnd) = session.Value;
            _hasView = true;
            return;
        }

        (_viewStart, _viewEnd) = ChartMath.PanRange(_viewStart, _viewEnd, TimeSpan.Zero, session.Value.Start, session.Value.End);
    }

    private (DateTime Start, DateTime End)? FullRange()
    {
        DateTime? start = null;
        DateTime? end = null;

        foreach (var series in _series)
        {
            var s = series.StartTime;
            var e = series.EndTime;
            if (s is null || e is null) continue;
            if (start is null || s < start) start = s;
            if (end is null || e > end) end = e;
        }

        if (start is null || end is null) return null;

        // 只有一个采样点时区间为零，横轴会退化：给 1 秒的最小宽度
        if (end <= start) end = start.Value.AddSeconds(1);
        return (start.Value, end.Value);
    }

    // ---------------- 缩放与平移 ----------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        var session = FullRange();
        if (!_hasView || session is null) return;

        var plot = PlotRect();
        if (plot.Width <= 0) return;

        var anchor = ChartMath.TimeOf(e.GetPosition(this).X, _viewStart, _viewEnd, plot.Left, plot.Width);
        var factor = e.Delta > 0 ? 1 / 1.3 : 1.3;

        (_viewStart, _viewEnd) = ChartMath.ZoomRange(
            _viewStart, _viewEnd, anchor, factor, MinSpan, session.Value.Start, session.Value.End);

        _userZoomed = true;
        AfterViewChange();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }

        if (!_hasView) return;

        _dragging = true;
        _panned = false;
        _dragOrigin = e.GetPosition(this);
        _dragViewStart = _viewStart;
        _dragViewEnd = _viewEnd;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var position = e.GetPosition(this);
        _mouse = position;

        if (!_dragging)
        {
            RedrawHover();
            return;
        }

        var dx = position.X - _dragOrigin.X;
        if (Math.Abs(dx) > 2) _panned = true;

        var plot = PlotRect();
        if (_panned && plot.Width > 0)
        {
            Cursor = Cursors.SizeWE;

            var span = _dragViewEnd - _dragViewStart;
            var delta = TimeSpan.FromMilliseconds(-dx / plot.Width * span.TotalMilliseconds);
            var session = FullRange();

            if (session is not null)
            {
                (_viewStart, _viewEnd) = ChartMath.PanRange(
                    _dragViewStart, _dragViewEnd, delta, session.Value.Start, session.Value.End);

                _userZoomed = true;
            }

            AfterViewChange();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_dragging) return;

        _dragging = false;
        Cursor = Cursors.Arrow;
        ReleaseMouseCapture();
        RedrawHover();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        RedrawHover();
    }

    private void AfterViewChange()
    {
        ViewChanged?.Invoke(this, EventArgs.Empty);
        RedrawBase();
        RedrawHover();
    }

    /// <summary>绘图区（去掉坐标轴标签占的位置）。</summary>
    private Rect PlotRect()
        => new(LeftPad + 0.5, TopPad + 0.5,
               Math.Max(0, _size.Width - LeftPad - RightPad),
               Math.Max(0, _size.Height - TopPad - BottomPad));

    // ---------------- 取色与文本 ----------------

    private static Brush Frozen(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush Ink = Frozen(Color.FromRgb(0x20, 0x21, 0x24), 0xFF);
    private static readonly Brush Subtle = Frozen(Color.FromRgb(0x5F, 0x63, 0x68), 0xFF);
    private static readonly Brush Axis = Frozen(Color.FromRgb(0x9A, 0xA0, 0xA6), 0xFF);
    private static readonly Brush Grid = Frozen(Color.FromRgb(0xEC, 0xEE, 0xF1), 0xFF);
    private static readonly Brush Crosshair = Frozen(Color.FromRgb(0x9A, 0xA0, 0xA6), 0xCC);

    private FormattedText Text(string text, double size, Brush brush, bool bold = false)
        => new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI, Segoe UI"),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ---------------- 曲线层 ----------------

    private void RedrawBase()
    {
        using var dc = _baseLayer.RenderOpen();

        var bounds = new Rect(0, 0, Math.Max(0, _size.Width), Math.Max(0, _size.Height));
        if (bounds.Width < 60 || bounds.Height < 60) return;

        var plot = PlotRect();
        if (plot.Width <= 4 || plot.Height <= 4) return;

        var gridPen = new Pen(Grid, 1); gridPen.Freeze();
        var axisPen = new Pen(Axis, 1); axisPen.Freeze();

        var visible = _series.Where(s => !_hidden.Contains(s.Key)).ToList();

        if (visible.Count == 0 || !_hasView)
        {
            var hint = Text("在「链路实时统计」页勾选链路（最左侧那一列的勾选框），这里按会话全程画 RTT 曲线", 12, Subtle);
            dc.DrawText(hint, new Point(Math.Max(8, (bounds.Width - hint.Width) / 2),
                                        Math.Max(8, (bounds.Height - hint.Height) / 2)));
            return;
        }

        var columns = (int)Math.Max(1, Math.Floor(plot.Width));
        if (columns > _buckets.Length) columns = _buckets.Length;

        // Y 轴按**可见范围**自适应：全程看不出的细节，缩进去才看得到
        var maxRtt = 0d;
        foreach (var series in visible)
        {
            var value = series.MaxRtt(_viewStart, _viewEnd);
            if (value > maxRtt) maxRtt = value;
        }

        var scale = ChartMath.ComputeYScale(maxRtt, WarningMs);
        double YOf(double value) => plot.Bottom - Math.Clamp(value / scale.Max, 0, 1) * plot.Height;

        // ---- 横向网格 + Y 轴刻度 ----
        foreach (var tick in scale.Ticks())
        {
            var y = Math.Round(YOf(tick)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var text = Text(tick.ToString("0.#"), 10.5, Subtle);
            dc.DrawText(text, new Point(plot.Left - text.Width - 7, y - text.Height / 2));
        }

        var unit = Text("ms", 10.5, Subtle);
        dc.DrawText(unit, new Point(plot.Left - unit.Width - 7, plot.Top - unit.Height - 2));

        // ---- 纵向网格 + X 轴刻度 ----
        var span = _viewEnd - _viewStart;
        foreach (var tick in ChartMath.TimeTicks(_viewStart, _viewEnd, Math.Max(3, (int)(plot.Width / 110))))
        {
            var x = Math.Round(ChartMath.XOf(tick, _viewStart, _viewEnd, plot.Left, plot.Width)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            var text = Text(ChartMath.FormatAxisTime(tick, span), 10.5, Subtle);
            dc.DrawText(text, new Point(x - text.Width / 2, plot.Bottom + 6));
        }

        // ---- 阈值虚线 ----
        if (WarningMs > 0 && WarningMs <= scale.Max)
        {
            var warnPen = new Pen(WarnBrush, 1) { DashStyle = new DashStyle(new double[] { 5, 4 }, 0) };
            warnPen.Freeze();

            var y = Math.Round(YOf(WarningMs)) + 0.5;
            dc.DrawLine(warnPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var text = Text($"阈值 {WarningMs:0.#}ms", 10, WarnTextBrush);
            dc.DrawText(text, new Point(plot.Right - text.Width - 4, y - text.Height - 2));
        }

        // ---- 曲线 ----
        dc.PushClip(new RectangleGeometry(plot));

        for (var i = 0; i < visible.Count; i++)
        {
            var series = visible[i];
            var color = SeriesColor(_series.IndexOf(series));
            var brush = Frozen(color, 0xFF);
            var band = Frozen(color, 0x38);
            var bandPen = new Pen(band, 1); bandPen.Freeze();
            var barPen = new Pen(LossBrush, 1.5); barPen.Freeze();

            ChartMath.Aggregate(series.Samples, _viewStart, _viewEnd, columns, _buckets);

            // 断口判定：**空像素列不等于断口**——那一列只是没落进采样点而已。
            // 把空列当断口会出事：会话刚开始时采样点(1 秒 1 个)比像素列稀疏得多，
            // 每个点都会单独成一个"单点图形"，而单点图形是画不出任何东西的 → 整条曲线看不见。
            // （这个缺陷是像素级渲染断言抓出来的，肉眼只会以为"没数据"。）
            var msPerColumn = span.TotalMilliseconds / columns;
            var visibleSamples = ChartMath.LowerBound(series.Samples, _viewEnd)
                               - ChartMath.LowerBound(series.Samples, _viewStart);
            var gapMs = Math.Max(2000, 3 * span.TotalMilliseconds / Math.Max(1, visibleSamples));
            DateTime? lastDrawn = null;

            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                var open = false;

                for (var column = 0; column < columns; column++)
                {
                    var bucket = _buckets[column];
                    var x = plot.Left + column + 0.5;

                    // 丢包：底部淡红竖条（SmokePing 的做法）——断口处一眼能看出是丢包，而不是"没数据"
                    if (bucket.HasLoss)
                        dc.DrawLine(barPen, new Point(x, plot.Bottom - 7), new Point(x, plot.Bottom));

                    if (!bucket.HasOk) continue;

                    var columnTime = _viewStart.AddMilliseconds(column * msPerColumn);
                    if (lastDrawn is not null && (columnTime - lastDrawn.Value).TotalMilliseconds > gapMs)
                        open = false;   // 真的断了一段（对端离线等），不能用直线糊过去

                    // 一列里有多个点时画出最小/最大包络：长会话"所有数据都画了"靠的就是它
                    if (bucket.Max - bucket.Min > 0.01)
                        dc.DrawLine(bandPen, new Point(x, YOf(bucket.Max)), new Point(x, YOf(bucket.Min)));

                    var y = YOf(bucket.Mean);
                    if (!open)
                    {
                        ctx.BeginFigure(new Point(x, y), false, false);
                        open = true;
                    }
                    else
                    {
                        ctx.LineTo(new Point(x, y), true, false);
                    }

                    lastDrawn = columnTime;
                }
            }

            line.Freeze();
            var strokePen = new Pen(brush, 1.4) { LineJoin = PenLineJoin.Round };
            strokePen.Freeze();
            dc.DrawGeometry(null, strokePen, line);

            // 丢包的空心红点：画在断口处（高度取上一次成功的 RTT），可以判断丢包落在哪一段
            var lastY = double.NaN;
            for (var column = 0; column < columns; column++)
            {
                var bucket = _buckets[column];
                var x = plot.Left + column + 0.5;

                if (bucket.HasOk) { lastY = YOf(bucket.Mean); continue; }
                if (!bucket.HasLoss) continue;

                var y = double.IsNaN(lastY) ? plot.Bottom - 9 : lastY;
                dc.DrawEllipse(PanelBrush, new Pen(LossDotBrush, 1.6), new Point(x, y), 3.0, 3.0);
                lastY = double.NaN;   // 一组连续丢包只标一次，避免糊成一片红
            }
        }

        dc.Pop();

        // ---- 坐标轴 ----
        dc.DrawLine(axisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom + 4));
        dc.DrawLine(axisPen, new Point(plot.Left - 4, plot.Bottom + 0.5), new Point(plot.Right, plot.Bottom + 0.5));

        // ---- 右上角：条数与范围 ----
        var info = Text($"{visible.Count} 条曲线 · {FormatSpan(span)}", 10.5, Subtle);
        dc.DrawText(info, new Point(plot.Right - info.Width, plot.Top + 2));

        if (visible.Any(s => s.Trimmed))
        {
            var trimmed = Text("会话过长，最早的采样已按上限裁掉", 10, WarnTextBrush);
            dc.DrawText(trimmed, new Point(plot.Right - trimmed.Width, plot.Top + 16));
        }
    }

    private static string FormatSpan(TimeSpan span)
    {
        if (span.TotalHours >= 1) return $"{span.TotalHours:F1} 小时";
        if (span.TotalMinutes >= 1) return $"{span.TotalMinutes:F1} 分钟";
        return $"{span.TotalSeconds:F0} 秒";
    }

    // ---------------- 交互层（悬停） ----------------

    private void RedrawHover()
    {
        using var dc = _hoverLayer.RenderOpen();

        if (_mouse is null || _dragging || !_hasView) return;

        var plot = PlotRect();
        if (plot.Width <= 4 || plot.Height <= 4) return;
        if (!plot.Contains(_mouse.Value)) return;

        var visible = _series.Where(s => !_hidden.Contains(s.Key)).ToList();
        if (visible.Count == 0) return;

        var hovered = ChartMath.TimeOf(_mouse.Value.X, _viewStart, _viewEnd, plot.Left, plot.Width);

        // 容差取"约 8 像素"的时间且不小于 1.5 秒：缩到很远时鼠标也能吸附到点上
        var span = _viewEnd - _viewStart;
        var tolerance = TimeSpan.FromMilliseconds(Math.Max(1500, span.TotalMilliseconds / Math.Max(1, plot.Width) * 8));

        // 所有曲线共用**同一个时刻**：先取离鼠标最近的采样时刻，再让每条曲线取各自在该时刻的最近值
        DateTime? snapped = null;
        var best = TimeSpan.MaxValue;

        foreach (var series in visible)
        {
            var index = ChartMath.NearestIndex(series.Samples, hovered, tolerance);
            if (index < 0) continue;

            var delta = (series.Samples[index].Time - hovered).Duration();
            if (delta < best) { best = delta; snapped = series.Samples[index].Time; }
        }

        if (snapped is null) return;

        var time = snapped.Value;
        var x = Math.Round(ChartMath.XOf(time, _viewStart, _viewEnd, plot.Left, plot.Width)) + 0.5;

        var crosshairPen = new Pen(Crosshair, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        crosshairPen.Freeze();
        dc.DrawLine(crosshairPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

        var maxRtt = 0d;
        foreach (var series in visible)
        {
            var value = series.MaxRtt(_viewStart, _viewEnd);
            if (value > maxRtt) maxRtt = value;
        }

        var scale = ChartMath.ComputeYScale(maxRtt, WarningMs);
        double YOf(double value) => plot.Bottom - Math.Clamp(value / scale.Max, 0, 1) * plot.Height;

        const double fontSize = 11;
        var header = Text(ChartMath.FormatHoverTime(time), fontSize + 0.5, Ink, bold: true);

        var names = new List<FormattedText>();
        var values = new List<FormattedText>();
        var dots = new List<(Brush Brush, double? Y)>();

        foreach (var series in visible)
        {
            var brush = Frozen(SeriesColor(_series.IndexOf(series)), 0xFF);
            var index = ChartMath.NearestIndex(series.Samples, time, tolerance);

            names.Add(Text(series.Name, fontSize, Ink));

            if (index < 0)
            {
                values.Add(Text("无数据", fontSize, Subtle));
                dots.Add((brush, null));
                continue;
            }

            var sample = series.Samples[index];
            if (sample.Lost)
            {
                values.Add(Text("丢包", fontSize, LossDotBrush));
                dots.Add((brush, null));
            }
            else
            {
                values.Add(Text($"{sample.RttMs:F1} ms", fontSize, Ink));
                dots.Add((brush, YOf(sample.RttMs)));
            }
        }

        const double pad = 10;
        const double gap = 12;
        const double swatch = 9;
        var lineHeight = fontSize + 6;
        var nameWidth = names.Count == 0 ? 0 : names.Max(t => t.Width);
        var valueWidth = values.Count == 0 ? 0 : values.Max(t => t.Width);
        var boxWidth = pad * 2 + swatch + 6 + nameWidth + gap + valueWidth;
        var boxHeight = pad * 2 + header.Height + 4 + names.Count * lineHeight;

        var boxX = x + 14;
        if (boxX + boxWidth > plot.Right) boxX = x - 14 - boxWidth;
        boxX = Math.Clamp(boxX, plot.Left + 1, Math.Max(plot.Left + 1, plot.Right - boxWidth - 1));

        var boxY = Math.Clamp(_mouse.Value.Y - boxHeight / 2, plot.Top + 1,
            Math.Max(plot.Top + 1, plot.Bottom - boxHeight - 1));
        var box = new Rect(boxX, boxY, boxWidth, boxHeight);

        var borderPen = new Pen(Axis, 1); borderPen.Freeze();
        dc.DrawRoundedRectangle(PanelBrush, borderPen, box, 5, 5);
        dc.DrawText(header, new Point(box.Left + pad, box.Top + pad));

        var rowY = box.Top + pad + header.Height + 4;
        for (var i = 0; i < names.Count; i++)
        {
            var center = rowY + lineHeight / 2;

            // 在曲线上标出该时刻的实际取值
            if (dots[i].Y is { } y)
            {
                var dotPen = new Pen(dots[i].Brush, 1.6); dotPen.Freeze();
                dc.DrawEllipse(PanelBrush, dotPen, new Point(x, y), 3.2, 3.2);
            }

            dc.DrawRectangle(dots[i].Brush, null, new Rect(box.Left + pad, center - swatch / 2, swatch, swatch));
            dc.DrawText(names[i], new Point(box.Left + pad + swatch + 6, center - names[i].Height / 2));
            dc.DrawText(values[i], new Point(box.Right - pad - values[i].Width, center - values[i].Height / 2));

            rowY += lineHeight;
        }

        // 轴上的时刻（悬停时替换该位置的刻度标签）
        var timeText = Text(ChartMath.FormatHoverTime(time), 10.5, Ink);
        var labelX = Math.Clamp(x - timeText.Width / 2, plot.Left, Math.Max(plot.Left, plot.Right - timeText.Width));
        dc.DrawText(timeText, new Point(labelX, plot.Bottom + 6));
    }
}
