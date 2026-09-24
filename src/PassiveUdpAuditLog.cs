using System.IO;
using System.Text;
using System.Threading.Channels;

namespace LineTester;

/// <summary>
/// 被动 UDP 应答审计。与发起端 CSV 共用 <c>seq</c>/<c>probe_id</c> 关联键，
/// 用来区分“请求未到服务器”“收到了但没有成功回包”和“回包已提交给 UDP 套接字”。
///
/// 审计落盘必须在后台完成，避免逐包同步写盘反过来制造 UDP 丢包。
/// </summary>
public sealed class PassiveUdpAuditLog : IDisposable
{
    public const string FileName = "udp-passive-audit.csv";
    public const string Header = "timestamp,remote_endpoint,seq,probe_id,received,reply_sent,error";

    private readonly string _rootDirectory;
    private readonly Channel<Record> _channel;
    private readonly Task _worker;
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private int _pendingCount;
    private bool _disposed;

    private sealed record Record(DateTime Timestamp, string RemoteEndpoint, string Seq, string ProbeId, bool ReplySent, string Error);

    public PassiveUdpAuditLog(string rootDirectory)
    {
        _rootDirectory = rootDirectory;
        _channel = Channel.CreateUnbounded<Record>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _worker = Task.Run(WorkerLoopAsync);
    }

    public string CurrentPath => Path.Combine(_rootDirectory, CsvLogWriter.FolderNameFor(DateTime.Now), FileName);

    public void Enqueue(DateTime timestamp, string remoteEndpoint, long? seq, string? probeId, bool replySent, string? error = null)
    {
        if (_disposed) return;
        var record = new Record(timestamp, remoteEndpoint, seq?.ToString() ?? "", probeId ?? "", replySent, error ?? "");
        Interlocked.Increment(ref _pendingCount);
        if (!_channel.Writer.TryWrite(record)) Interlocked.Decrement(ref _pendingCount);
    }

    /// <summary>供自检确认后台审计已经真正落盘。</summary>
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

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new List<Record>();
                while (_channel.Reader.TryRead(out var record)) batch.Add(record);

                foreach (var record in batch)
                {
                    try { Write(record); }
                    catch { /* 审计失败不得影响监听/回包 */ }
                }

                FlushAll();
                Interlocked.Add(ref _pendingCount, -batch.Count);
            }
        }
        finally
        {
            FlushAll();
            CloseAll();
        }
    }

    private void Write(Record record)
    {
        var day = CsvLogWriter.FolderNameFor(record.Timestamp);
        if (!_writers.TryGetValue(day, out var writer))
        {
            var directory = Path.Combine(_rootDirectory, day);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
            // 允许 Excel/分析脚本在程序运行期间读取；写入仍由本类的单一后台任务串行完成。
            writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            _writers[day] = writer;
            if (isNew) writer.WriteLine(Header);
        }

        writer.WriteLine($"{record.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{Csv(record.RemoteEndpoint)},{record.Seq},{record.ProbeId},1,{(record.ReplySent ? 1 : 0)},{Csv(record.Error)}");
    }

    private static string Csv(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : '"' + value.Replace("\"", "\"\"") + '"';

    private void FlushAll()
    {
        foreach (var writer in _writers.Values)
        {
            try { writer.Flush(); } catch { }
        }
    }

    private void CloseAll()
    {
        foreach (var writer in _writers.Values)
        {
            try { writer.Dispose(); } catch { }
        }

        _writers.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Writer.TryComplete();
        try { _worker.GetAwaiter().GetResult(); } catch { }
    }
}
