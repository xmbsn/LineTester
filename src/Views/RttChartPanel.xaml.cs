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

/// <summary>
/// 「RTT 曲线」页：工具条 + 自绘曲线 + 图例。
///
/// 页面只做装配，取点/缩放/悬停全在 <see cref="RttChart"/> 与 <see cref="ChartMath"/> 里；
/// 这样"图画得对不对"能用零依赖自检验证（没有视觉模型可看，只能靠断言）。
/// </summary>
public partial class RttChartPanel : UserControl
{
    private readonly ObservableCollection<ChartLegendItem> _legend = new();
    private string _signature = "";

    public RttChartPanel()
    {
        InitializeComponent();

        Legend.ItemsSource = _legend;
        Chart.ViewChanged += (_, _) => UpdateRangeText();
    }

    /// <summary>给自检用的入口：直接拿到曲线控件。</summary>
    public RttChart ChartControl => Chart;

    /// <summary>
    /// 界面每 500 毫秒调用一次：同步曲线集合、补画新数据、刷新范围文案。
    /// 只有"勾选集合"变化时才重建图例，避免每 500 毫秒重建一次控件树。
    /// </summary>
    public void Update(IReadOnlyList<ChartSeries> series, double warningMs, int recordedCount = 0)
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
        UpdateRangeText();
    }

    private int _recordedCount;

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

    /// <summary>
    /// 范围文案：这条直接回答"我现在看到的是哪一段"——缩进去以后最容易迷失的就是这个。
    /// </summary>
    private void UpdateRangeText()
    {
        ResetViewButton.IsEnabled = Chart.IsZoomed;

        var view = Chart.ViewRange;
        var session = Chart.SessionRange;

        if (view is null || session is null)
        {
            // 区分"没数据"和"没勾选"：数据一直在记，只是没显示
            RangeText.Text = _recordedCount > 0
                ? $"已记录 {_recordedCount} 条链路的数据（勾选后在图上显示，勾选之前的整段也在）"
                : "尚无数据（启动测试后开始记录）";
            return;
        }

        var span = view.Value.End - view.Value.Start;
        var text = Chart.IsZoomed
            ? $"缩放视图 {view.Value.Start:HH:mm:ss.fff} ~ {view.Value.End:HH:mm:ss.fff}（{FormatSpan(span)}）"
              + $"　全程 {session.Value.Start:HH:mm:ss} ~ {session.Value.End:HH:mm:ss}"
            : $"会话全程 {session.Value.Start:HH:mm:ss} ~ {session.Value.End:HH:mm:ss}（{FormatSpan(span)}）";

        RangeText.Text = text + $"　可见曲线 {Chart.VisibleCount} 条";
    }

    private static string FormatSpan(TimeSpan span)
    {
        if (span.TotalHours >= 1) return $"{span.TotalHours:F2} 小时";
        if (span.TotalMinutes >= 1) return $"{span.TotalMinutes:F2} 分钟";
        return $"{span.TotalSeconds:F1} 秒";
    }
}
