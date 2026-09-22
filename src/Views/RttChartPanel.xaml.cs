using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LineTester.Views;

/// <summary>图例里的一行（一条曲线）。</summary>
public sealed class ChartLegendItem : INotifyPropertyChanged
{
    private bool _hidden;

    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public Brush Brush { get; init; } = Brushes.Gray;

    /// <summary>被图例点击关掉（曲线还在数据里，只是不画）。</summary>
    public bool Hidden
    {
        get => _hidden;
        set
        {
            if (_hidden == value) return;
            _hidden = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hidden)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>曲线看的是哪一段时间。</summary>
public enum ChartRange
{
    /// <summary>最近 24 小时（默认）：跨会话、跨重启，数据来自 logs 里的 CSV。</summary>
    Last24Hours,

    /// <summary>本会话（内存里的实时采样，含刚落盘之前的那几条）。</summary>
    Session,

    Today,

    Yesterday,

    /// <summary>指定日期（用日期选择器选的那一天）。</summary>
    SpecificDay
}

/// <summary>
/// 「RTT 曲线」页：时间范围 + 曲线 + 图例。
///
/// 页面只做装配：取点/缩放/悬停全在 <see cref="RttChart"/> 与 <see cref="ChartMath"/> 里，
/// "数据从哪来"由宿主（MainWindow）决定——本会话走内存，历史范围走 CSV 日志。
/// </summary>
public partial class RttChartPanel : UserControl
{
    private readonly ObservableCollection<ChartLegendItem> _legend = new();
    private string _signature = "";
    private int _recordedCount;
    private int _loadedSamples;
    private string _loadNote = "";

    public RttChartPanel()
    {
        InitializeComponent();

        Legend.ItemsSource = _legend;
        Chart.ViewChanged += (_, _) => UpdateRangeText();
        RangeBox.SelectedIndex = 0;      // 默认「最近 24 小时」
        DayPicker.SelectedDate = DateTime.Today;
    }

    /// <summary>用户改了时间范围（宿主据此重新取数）。</summary>
    public event Action? RangeChanged;

    /// <summary>给自检用的入口：直接拿到曲线控件。</summary>
    public RttChart ChartControl => Chart;

    public ChartRange Range => (ChartRange)Math.Max(0, RangeBox.SelectedIndex);

    /// <summary>「指定日期」选的那一天（其它范围下也返回日期控件里的值）。</summary>
    public DateTime SelectedDay => (DayPicker.SelectedDate ?? DateTime.Today).Date;

    /// <summary>当前范围对应的 [开始, 结束]。历史范围跨天时由这里算清楚，宿主不再各算一遍。</summary>
    public (DateTime From, DateTime To) RangeWindow()
    {
        var now = DateTime.Now;

        return Range switch
        {
            ChartRange.Today => (now.Date, now),
            ChartRange.Yesterday => (now.Date.AddDays(-1), now.Date),
            ChartRange.SpecificDay => (SelectedDay, SelectedDay.AddDays(1)),
            _ => (now.AddHours(-24), now)
        };
    }

    /// <summary>这个范围是否包含"现在"（包含就要跟着实时数据刷新，不含则是个静态历史视图）。</summary>
    public bool RangeIncludesNow => Range is ChartRange.Last24Hours or ChartRange.Today or ChartRange.Session;

    /// <summary>
    /// 界面每 500 毫秒调用一次：同步曲线集合、重画、刷新范围文案。
    /// 只有"勾选集合"变化时才重建图例，避免每 500 毫秒重建一次控件树。
    /// </summary>
    public void Update(IReadOnlyList<ChartSeries> series, double warningMs, int recordedCount = 0,
                       int loadedSamples = 0, string loadNote = "")
    {
        Chart.WarningMs = warningMs;
        Chart.RecordedCount = recordedCount;

        var signature = string.Join("\u0001", series.Select(s => s.Key));
        if (signature != _signature)
        {
            _signature = signature;
            RebuildLegend(series);
        }

        Chart.SetSeries(series);
        Chart.Refresh();

        _recordedCount = recordedCount;
        _loadedSamples = loadedSamples;
        _loadNote = loadNote;
        UpdateRangeText();
    }

    private void RebuildLegend(IReadOnlyList<ChartSeries> series)
    {
        _legend.Clear();

        for (var i = 0; i < series.Count; i++)
        {
            var item = series[i];
            _legend.Add(new ChartLegendItem
            {
                Key = item.Key,
                Name = item.Name,
                Brush = new SolidColorBrush(RttChart.SeriesColor(i)),
                Hidden = Chart.IsHidden(item.Key)
            });
        }
    }

    private void LegendItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChartLegendItem item }) return;

        item.Hidden = !item.Hidden;
        Chart.SetHidden(item.Key, item.Hidden);
        UpdateRangeText();
    }

    private void ResetView_Click(object sender, RoutedEventArgs e) => Chart.ResetView();

    private void Reload_Click(object sender, RoutedEventArgs e) => RangeChanged?.Invoke();

    private void RangeBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;

        DayPicker.IsEnabled = Range == ChartRange.SpecificDay;
        RangeChanged?.Invoke();
    }

    private void DayPicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        RangeChanged?.Invoke();
    }

    /// <summary>
    /// 范围文案：直接回答"我现在看到的是哪一段、数据从哪来"——
    /// 切到历史范围后最容易迷失的就是这两件事。
    /// 这里报的是**图上真实画出来的**区间（不是请求的范围），两者不一致时才是实话。
    /// </summary>
    private void UpdateRangeText()
    {
        ResetViewButton.IsEnabled = Chart.IsZoomed;

        var source = Range == ChartRange.Session ? "本会话（内存）" : "logs 日志";
        var view = Chart.ViewRange;
        var session = Chart.SessionRange;

        if (view is null || session is null)
        {
            var note = _recordedCount > 0
                ? $"已记录 {_recordedCount} 条链路的数据（勾选后在图上显示，勾选之前的整段也在）"
                : "尚无数据（启动测试后开始记录）";

            if (Range != ChartRange.Session) note = _loadNote.Length > 0 ? _loadNote : "这个范围里没有读到数据";

            RangeText.Text = note;
            return;
        }

        var span = view.Value.End - view.Value.Start;
        var text = $"{view.Value.Start:MM-dd HH:mm:ss} ~ {view.Value.End:MM-dd HH:mm:ss}（{FormatSpan(span)}）" +
                   (Chart.IsZoomed ? "　[已缩放，双击全览]" : "") +
                   $"　来源：{source}　加载 {_loadedSamples} 条采样　可见曲线 {Chart.VisibleCount} 条";

        if (_loadNote.Length > 0) text += "　" + _loadNote;
        RangeText.Text = text;
    }

    private static string FormatSpan(TimeSpan span)
    {
        if (span.TotalHours >= 1) return $"{span.TotalHours:F2} 小时";
        if (span.TotalMinutes >= 1) return $"{span.TotalMinutes:F2} 分钟";
        return $"{span.TotalSeconds:F1} 秒";
    }
}
