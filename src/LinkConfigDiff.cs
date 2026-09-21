namespace LineTester;

/// <summary>
/// 配置文件里的链路 ↔ 界面上的链路 的比对。
///
/// 为什么需要：现场多次出现"appsettings.json 里明明有，界面里却没有"（或反过来），
/// 而原因可能是①程序读的不是这份文件②文件被外部改过③同步/删除动过它——
/// 光看界面无法区分。这里把差异算清楚，让界面直接说出来。
/// </summary>
public static class LinkConfigDiff
{
    public sealed record Result(
        int FileCount,
        int RowCount,
        List<string> OnlyInFile,
        List<string> OnlyInRows)
    {
        public bool Same => OnlyInFile.Count == 0 && OnlyInRows.Count == 0;
    }

    /// <summary>按「名称 + 主机 + 端口 + 来源」比对（协议勾选差异不算结构差异，避免噪音）。</summary>
    public static Result Compare(IEnumerable<LinkConfig> fileLinks, IEnumerable<LinkConfig> rowLinks)
    {
        var file = fileLinks?.ToList() ?? new List<LinkConfig>();
        var rows = rowLinks?.ToList() ?? new List<LinkConfig>();

        var fileKeys = new List<string>(file.Count);
        foreach (var link in file) fileKeys.Add(Key(link));

        var rowKeys = new List<string>(rows.Count);
        foreach (var link in rows) rowKeys.Add(Key(link));

        var fileSet = new HashSet<string>(fileKeys, StringComparer.Ordinal);
        var rowSet = new HashSet<string>(rowKeys, StringComparer.Ordinal);

        // 用列表而不是集合：同一目标出现多次时也要如实报出来
        var onlyInFile = fileKeys.Where(k => !rowSet.Contains(k)).Distinct(StringComparer.Ordinal).ToList();
        var onlyInRows = rowKeys.Where(k => !fileSet.Contains(k)).Distinct(StringComparer.Ordinal).ToList();

        return new Result(file.Count, rows.Count, onlyInFile, onlyInRows);
    }

    public static string Key(LinkConfig link)
        => $"{link.Name.Trim()}｜{link.Host.Trim()}:{link.Port}｜{SourceText(link.Source)}";

    public static string SourceText(string? source)
        => string.Equals(source, LinkSources.Registry, StringComparison.Ordinal) ? "注册中心" : "本地";

    /// <summary>给人看的一段话（多行）。</summary>
    public static string Describe(Result result, string configPath, DateTime? fileTime)
    {
        var lines = new List<string>
        {
            $"配置文件：{configPath}",
            fileTime is null ? "（读取不到文件时间）" : $"文件最后修改：{fileTime:yyyy-MM-dd HH:mm:ss}",
            "",
            $"文件里 {result.FileCount} 条链路；界面当前 {result.RowCount} 条。"
        };

        if (result.Same)
        {
            lines.Add("");
            lines.Add("✓ 两者一致（名称/主机/端口/来源都对得上）。");
            lines.Add("提示：若你看到界面里少了某个机房，请确认看的是同一份 appsettings.json" +
                      "（启动时状态栏会显示完整路径），并且该机房的多 IP 是以多条链路的形式存在" +
                      "（注册来源的会带 #2/#3 后缀）。");
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add("");
        if (result.OnlyInFile.Count > 0)
        {
            lines.Add($"只在文件里（界面没有）{result.OnlyInFile.Count} 条：");
            foreach (var key in result.OnlyInFile.Take(10)) lines.Add("  · " + key);
        }

        if (result.OnlyInRows.Count > 0)
        {
            lines.Add($"只在界面里（文件没有）{result.OnlyInRows.Count} 条：");
            foreach (var key in result.OnlyInRows.Take(10)) lines.Add("  · " + key);
        }

        lines.Add("");
        lines.Add("可能原因：① 程序读的不是这份文件（启动时状态栏显示的是实际路径）；" +
                  "② 文件被其它程序/另一个实例改过（点「重新加载配置」读取新内容）；" +
                  "③ 注册中心的同步正在维护这些链路（注册来源的会被中心覆盖/重建）。");

        return string.Join(Environment.NewLine, lines);
    }
}
