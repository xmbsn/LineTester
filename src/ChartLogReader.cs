using System.Globalization;
using System.IO;

namespace LineTester;

/// <summary>
/// 从**已有的 CSV 日志**里读曲线历史。
///
/// 为什么不另存一份"曲线数据"：`logs\YYYY-MM-DD\&lt;链路&gt;_&lt;IP&gt;_&lt;端口&gt;.csv`
/// 本来就是逐包落盘的原始记录（含时间戳/RTT/丢包），再存一份只会出现两套记录不一致的问题
/// （现场"日志里有、曲线上没有"这类毛病最难查）。所以历史曲线 = 读日志。
///
/// 增量读取（尾部续读）是关键：24 小时视图要跟随实时更新，如果每 500 毫秒把整个日志重读一遍，
/// 几万行 × 每半秒 = 界面直接卡死。这里按"已读字节偏移"只读新增部分。
///
/// 不引用 WPF，可进零依赖自检。
/// </summary>
public sealed class ChartLogReader
{
    /// <summary>一个文件的读取缓存：已读到的字节偏移 + 按协议分好的采样。</summary>
    private sealed class FileCache
    {
        public long Offset;
        public readonly Dictionary<string, List<ChartSample>> ByProtocol = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, FileCache> _files = new(StringComparer.OrdinalIgnoreCase);

    public ChartLogReader(string rootDirectory) => RootDirectory = rootDirectory;

    /// <summary>日志根目录（各天的子目录都在它下面）。</summary>
    public string RootDirectory { get; }

    /// <summary>累计读进来的行数（自检与界面诊断用）。</summary>
    public int RowsRead { get; private set; }

    /// <summary>
    /// 把曲线键（<c>链路名|主机|端口|协议</c>）映射到它对应的日志文件与协议。
    /// 文件名规则必须与 <see cref="CsvLogWriter.FileNameFor"/> 完全一致，否则历史会是空的。
    /// </summary>
    public static (string FileName, string Protocol)? SplitKey(string key)
    {
        var parts = key.Split('|');
        if (parts.Length < 4) return null;

        var protocol = parts[^1];
        var port = parts[^2];
        var host = parts[^3];
        var link = string.Join("|", parts.Take(parts.Length - 3));

        var safe = string.Join("_", link.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        if (safe.Length == 0) safe = "link";

        return ($"{safe}_{host}_{port}.csv", protocol);
    }

    /// <summary>某天、某个文件在磁盘上的路径。</summary>
    public string PathFor(DateTime day, string fileName)
        => Path.Combine(RootDirectory, CsvLogWriter.FolderNameFor(day), fileName);

    /// <summary>日志里出现过的日期（新到旧）。界面用它填"选日期"的下拉。</summary>
    public List<DateTime> AvailableDays()
    {
        var days = new List<DateTime>();
        if (!Directory.Exists(RootDirectory)) return days;

        foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
        {
            var name = Path.GetFileName(directory);
            if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day)
                && Directory.EnumerateFiles(directory, "*.csv").Any())
            {
                days.Add(day);
            }
        }

        days.Sort();
        days.Reverse();
        return days;
    }

    /// <summary>
    /// 读取 [from, to] 范围内这些曲线的采样。返回 key → 采样（按时间有序）。
    /// 只有文件新增了内容才会解析新行——可以每几百毫秒调用一次。
    /// </summary>
    public Dictionary<string, List<ChartSample>> Load(IEnumerable<string> keys, DateTime from, DateTime to)
    {
        var result = new Dictionary<string, List<ChartSample>>(StringComparer.Ordinal);

        // 同一天内的多个曲线常常来自同一个文件，这里先把"要读哪些文件的哪些天"收成集合
        var jobs = new List<(string Key, string FileName, string Protocol, List<DateTime> Days)>();

        foreach (var key in keys)
        {
            var split = SplitKey(key);
            if (split is null) continue;

            var days = new List<DateTime>();
            for (var day = from.Date; day <= to.Date; day = day.AddDays(1)) days.Add(day);

            jobs.Add((key, split.Value.FileName, split.Value.Protocol, days));
        }

        foreach (var (key, fileName, protocol, days) in jobs)
        {
            var merged = new List<ChartSample>();

            foreach (var day in days)
            {
                var cache = ReadFile(PathFor(day, fileName));
                if (cache is null) continue;
                if (!cache.ByProtocol.TryGetValue(protocol, out var samples)) continue;

                // 日志按天分目录、文件内按时间追加，因此按天升序拼起来就是有序的
                foreach (var sample in samples)
                {
                    if (sample.Time < from || sample.Time > to) continue;
                    merged.Add(sample);
                }
            }

            result[key] = merged;
        }

        return result;
    }

    /// <summary>单次最多读这么多字节：一天 21 条曲线约 10MB，分几次读完比一次卡住界面好。</summary>
    private const int ReadChunkBytes = 16 * 1024 * 1024;

    /// <summary>读一个文件的新增部分（首次则从头读）。返回缓存；文件不存在返回 null。</summary>
    private FileCache? ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            // 文件被删/被移走（例如按天目录被清理）：丢掉缓存，下次重建
            _files.Remove(path);
            return null;
        }

        if (!_files.TryGetValue(path, out var cache))
        {
            cache = new FileCache();
            _files[path] = cache;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // 文件被截断或替换过（长度比已读偏移还小）→ 从头重来
            if (stream.Length < cache.Offset)
            {
                cache.Offset = 0;
                cache.ByProtocol.Clear();
            }

            var remaining = stream.Length - cache.Offset;
            if (remaining <= 0) return cache;

            var want = (int)Math.Min(remaining, ReadChunkBytes);
            var buffer = new byte[want];
            stream.Seek(cache.Offset, SeekOrigin.Begin);

            var read = 0;
            while (read < want)
            {
                var got = stream.Read(buffer, read, want - read);
                if (got <= 0) break;
                read += got;
            }

            // 只消费到最后一个换行为止：日志是边写边读的，末尾那行可能还没写完。
            // （用 StreamReader 按行读会把半行也吃掉，那一行就永远丢了）
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline < 0) return cache;

            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, lastNewline);
            foreach (var line in text.Split('\n')) Parse(line.TrimEnd('\r'), cache);

            cache.Offset += lastNewline + 1;
            return cache;
        }
        catch (IOException)
        {
            // 正在被写入/被占用：这次读不到就算了，下一轮再补（日志写入方会 Flush）
            return cache;
        }
        catch (UnauthorizedAccessException)
        {
            return cache;
        }
    }

    private void Parse(string line, FileCache cache)
    {
        if (line.Length == 0) return;

        var fields = line.Split(',');
        if (fields.Length < 6) return;
        if (fields[0] == "timestamp") return;                      // 表头（新文件才有）

        if (!DateTime.TryParseExact(fields[0], "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return;

        var protocol = fields[3];
        if (protocol.Length == 0) return;

        var lost = fields[5] == "1";
        var rtt = 0d;
        if (!lost && !double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out rtt)) return;

        if (!cache.ByProtocol.TryGetValue(protocol, out var samples))
        {
            samples = new List<ChartSample>();
            cache.ByProtocol[protocol] = samples;
        }

        samples.Add(new ChartSample(time, rtt, lost));
        RowsRead++;
    }
}
