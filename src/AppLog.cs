using System.IO;
using System.Text;

namespace LineTester;

/// <summary>
/// 关键流程文本日志（注册 / 同步 / 链路配置）。按天分目录，与 CSV 日志同级：
///
/// <code>
/// logs\2026-09-21\registry-center.log   中心侧：收到的每个 REG/SYNC/ACKP 与应答结论
/// logs\2026-09-21\registry-client.log   非中心侧：注册/同步请求、收到的快照、应用到本地的链路变化
/// logs\2026-09-21\config.log            配置文件读写、链路增删改、同步落盘
/// </code>
///
/// 现场排查用：把这三份（中心端 + 非中心端各一套）拿过来，就能看到"谁在什么时候注册了什么、
/// 中心回了什么、本地把哪些链路加了/改了/删了"。
///
/// 三条硬性约束：
/// ① **绝不写密钥与 mac**（预共享密钥是唯一凭据）；
/// ② 写不进去也不抛——日志失败绝不能影响测试；
/// ③ 同步写（每次一行、立即落盘）：排障时最怕"日志还差最后几行"。
/// </summary>
public sealed class AppLog : IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _fileName;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private bool _failed;

    public AppLog(string rootDirectory, string fileName, Func<DateTime>? clock = null)
    {
        _rootDirectory = rootDirectory;
        _fileName = fileName;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>当前正在写的文件路径（按天变化）。</summary>
    public string CurrentPath
    {
        get
        {
            var now = _clock();
            return Path.Combine(_rootDirectory, CsvLogWriter.FolderNameFor(now), _fileName);
        }
    }

    /// <summary>目录（供"打开日志目录"用）。</summary>
    public string CurrentDirectory
    {
        get
        {
            var now = _clock();
            return Path.Combine(_rootDirectory, CsvLogWriter.FolderNameFor(now));
        }
    }

    /// <summary>写一行（自动加时间戳与线程号）。失败只会静默降级一次，不会重复尝试拖慢主流程。</summary>
    public void Write(string message)
    {
        if (_failed) return;

        try
        {
            lock (_gate)
            {
                var now = _clock();
                var directory = Path.Combine(_rootDirectory, CsvLogWriter.FolderNameFor(now));
                Directory.CreateDirectory(directory);

                var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId:00}] {message}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(directory, _fileName), line, new UTF8Encoding(false));
            }
        }
        catch
        {
            _failed = true; // 目录不可写等：记一次就放弃，别每次事件都撞一次异常
        }
    }

    /// <summary>写一行"分节标题"，便于在日志里分段读。</summary>
    public void Section(string title)
        => Write(new string('-', 20) + " " + title + " " + new string('-', 20));

    /// <summary>
    /// 打码：IP 全量记录（排障必须看到具体地址），但密钥/mac 一类凭据永不落盘。
    /// 这个方法是给调用方一个显式的"这里不能写凭据"的提醒。
    /// </summary>
    public static string Redact(string? secret)
        => string.IsNullOrEmpty(secret) ? "(空)" : $"已配置({secret.Length} 字符，内容不落盘)";

    public void Dispose()
    {
        // 同步写，无缓冲需要冲刷；保留接口以便将来改成异步写
    }
}
