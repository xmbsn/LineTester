using System.IO;
using System.Text;
using System.Threading.Channels;

namespace LineTester;

/// <summary>
/// 后台 CSV 日志写入器。
///
/// 结果进入有界通道后由单个后台任务顺序落盘，按批 Flush，
/// 文件保持打开因而不会每条都产生系统调用，也不会阻塞界面。
///
/// 日志**按天滚动**：写入 <c>logs\YYYY-MM-DD\&lt;链路&gt;_&lt;IP&gt;_&lt;端口&gt;.csv</c>。
/// 同一天内持续追加；一旦写入的日期变化（跨天），会先把上一天落盘并关闭其文件句柄，
/// 避免长期无人值守运行时句柄无限累积。
/// 默认永久保留历史日志；启用保留期后只清理过期的日期目录（与“清除”无关）。
/// </summary>
public sealed class CsvLogWriter : IDisposable
{
    public const int MaxPendingResults = 65536;
    private const int MaxWriteBatch = 1024;
    public const string Header =
        "timestamp,peer_ip,peer_port,protocol,rtt_ms,packet_loss,setup_ms,seq,probe_id,seen_from,failure";

    private readonly string _rootDirectory;
    private readonly Channel<ProbeResult> _channel;
    private readonly Task _worker;
    private readonly object _retentionGate = new();
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _activeDate = DateTime.MinValue;
    private int _pendingCount;
    private long _droppedCount;
    private int _retentionDays;
    private string? _lastError;
    private bool _disposed;

    public CsvLogWriter(string directory)
    {
        _rootDirectory = directory;
        _channel = Channel.CreateBounded<ProbeResult>(new BoundedChannelOptions(MaxPendingResults)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(WorkerLoopAsync);
    }

    public string? LastError => _lastError;

    /// <summary>磁盘持续写不动导致缓冲区满时被拒收的结果数。</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>当前打开的日志文件数（按天滚动后会回落，用于确认句柄没有无限累积）。</summary>
    public int OpenFileCount => _writers.Count;

    /// <summary>日志根目录（各天的子目录都在它下面）。</summary>
    public string RootDirectory => _rootDirectory;

    public event Action<string>? ErrorOccurred;

    /// <summary>设置日志保留天数，并在后台立即清理一次；0 表示不清理。</summary>
    public void ConfigureRetention(int keepDays)
    {
        Volatile.Write(ref _retentionDays, keepDays);
        if (keepDays <= 0) return;
        _ = Task.Run(() =>
        {
            try { lock (_retentionGate) LogRetention.Cleanup(_rootDirectory, keepDays, DateTime.Today); }
            catch (Exception ex) { Report(ex); }
        });
    }

    public bool Enqueue(ProbeResult result)
    {
        if (_disposed) return false;
        // 先加计数再入队，避免后台线程先取走结果导致计数短暂变负。
        Interlocked.Increment(ref _pendingCount);
        if (_channel.Writer.TryWrite(result)) return true;
        Interlocked.Decrement(ref _pendingCount);
        if (Interlocked.Increment(ref _droppedCount) == 1)
            Report(new IOException($"CSV 日志缓冲区已满（{MaxPendingResults} 条），部分结果未落盘；请检查磁盘速度或空间。"));
        return false;
    }

    /// <summary>
    /// 等待已入队的结果全部写完并落盘。
    /// “待写计数归零”发生在 Flush 之后，因此返回 true 时日志已经在磁盘上。
    /// </summary>
    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);

        while (Volatile.Read(ref _pendingCount) > 0)
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(5).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>某一天对应的子目录名。</summary>
    public static string FolderNameFor(DateTime date) => date.ToString("yyyy-MM-dd");

    /// <summary>某一天日志所在的目录。</summary>
    public string DirectoryFor(DateTime date) => Path.Combine(_rootDirectory, FolderNameFor(date));

    /// <summary>某条结果最终写入的文件完整路径。</summary>
    public string FilePathFor(ProbeResult result)
        => Path.Combine(DirectoryFor(result.Timestamp), FileNameFor(result));

    public static string FileNameFor(ProbeResult result)
    {
        var safe = string.Join("_", result.LinkName.Split(Path.GetInvalidFileNameChars()));
        safe = safe.Replace(' ', '_');
        if (safe.Length == 0) safe = "link";
        return $"{safe}_{result.Host}_{result.Port}.csv";
    }

    public static string FormatRow(ProbeResult result)
    {
        var rtt = result.RttMs.HasValue ? result.RttMs.Value.ToString("F3") : "";
        var setup = result.SetupMs.HasValue ? result.SetupMs.Value.ToString("F3") : "";
        return $"{result.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{result.Host},{result.Port}," +
               $"{result.Protocol},{rtt},{(result.Lost ? 1 : 0)},{setup}," +
               $"{result.Seq},{result.ProbeId},{result.SeenFrom},{result.Failure}";
    }

    private async Task WorkerLoopAsync()
    {
        var reader = _channel.Reader;

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batch = new List<ProbeResult>();
            while (batch.Count < MaxWriteBatch && reader.TryRead(out var result)) batch.Add(result);
            if (batch.Count == 0) continue;

            foreach (var result in batch)
            {
                try
                {
                    Write(result);
                }
                catch (Exception ex)
                {
                    Report(ex);
                }
            }

            // 先落盘、再扣减待写计数，这样“待写为 0”就等于“已经在磁盘上”
            FlushAll();
            Interlocked.Add(ref _pendingCount, -batch.Count);
        }

        CloseAll();
    }

    private void Write(ProbeResult result)
    {
        var date = result.Timestamp.Date;
        if (date != _activeDate)
        {
            // 跨天滚动：先把上一天的数据落盘、关闭句柄，再开新一天的文件。
            // （结果的时间戳可能比当前时间略早，所以这里以结果自身的日期为准。）
            FlushAll();
            CloseAll();
            _activeDate = date;
            var keepDays = Volatile.Read(ref _retentionDays);
            if (keepDays > 0)
            {
                try { lock (_retentionGate) LogRetention.Cleanup(_rootDirectory, keepDays, date); }
                catch (Exception ex) { Report(ex); }
            }
        }

        var name = FileNameFor(result);
        if (!_writers.TryGetValue(name, out var writer))
        {
            var directory = DirectoryFor(result.Timestamp);
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, name);
            var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

            // FileShare.Read：写日志的同时允许用 Excel/记事本查看
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
            _writers[name] = writer;

            if (isNew) writer.WriteLine(Header);
        }

        writer.WriteLine(FormatRow(result));
    }

    private void FlushAll()
    {
        foreach (var writer in _writers.Values)
        {
            try { writer.Flush(); }
            catch (Exception ex) { Report(ex); }
        }
    }

    private void CloseAll()
    {
        foreach (var writer in _writers.Values)
        {
            try { writer.Flush(); } catch { }
            try { writer.Dispose(); } catch { }
        }
        _writers.Clear();
    }

    private void Report(Exception ex)
    {
        _lastError = ex.Message;
        try { ErrorOccurred?.Invoke(ex.Message); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.Writer.TryComplete();
        try { _worker.Wait(TimeSpan.FromSeconds(30)); } catch { }
        // 文件句柄由唯一的后台写线程关闭；超时后不能在这里并发关闭它正在写的文件。
    }
}
