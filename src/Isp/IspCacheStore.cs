using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LineTester.Isp;

/// <summary>
/// 运营商查询缓存（落盘 <c>ip-isp-cache.json</c>，与配置文件同目录）。
///
/// 必须缓存：ip-api 免费额度只有 15 次/分钟，而界面每半秒就要读一次结果；
/// 运营商归属也很少变（默认 30 天），重启程序不该重新查一遍。
///
/// **带 schema 版本号**：曾经踩过一个坑——解析 bug 把"无数据"当成事实写进了缓存，
/// TTL 30 天，于是修好解析后程序先读到旧坏数据、界面照旧显示未知。
/// 现在文件形如 <c>{"Schema":2,"Records":[…]}</c>；版本或形状不符就整份丢弃重新查，
/// 任何缓存格式变更都能自愈，不需要人工删文件。
/// </summary>
public sealed class IspCacheStore
{
    /// <summary>缓存文件格式版本：改了字段含义/解析口径就 +1（旧文件会被自动丢弃）。</summary>
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文不转义，缓存文件现场可读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private readonly Dictionary<string, IspRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public IspCacheStore(string path, TimeSpan ttl)
    {
        _path = path;
        _ttl = ttl > TimeSpan.Zero ? ttl : TimeSpan.FromDays(30);
        Load();
    }

    public string Path => _path;

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    /// <summary>取一条未过期的记录；过期/没有则返回 false（调用方据此重新查询）。</summary>
    public bool TryGet(string ip, out IspRecord record)
    {
        record = null!;
        var key = ip?.Trim() ?? "";
        if (key.Length == 0) return false;

        lock (_gate)
        {
            if (!_records.TryGetValue(key, out var found)) return false;
            if (DateTime.UtcNow - found.FetchedUtc > _ttl) return false;

            record = found;
            return true;
        }
    }

    /// <summary>写入/覆盖一条记录（不落盘，由 <see cref="Save"/> 统一写）。</summary>
    public void Put(IspRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Ip)) return;

        lock (_gate) _records[record.Ip.Trim()] = record;
    }

    /// <summary>清空缓存（连同文件），返回清掉的条数。「重查」按钮用它。</summary>
    public int Clear()
    {
        int count;
        lock (_gate)
        {
            count = _records.Count;
            _records.Clear();
        }

        try { if (File.Exists(_path)) File.Delete(_path); } catch { }

        return count;
    }

    public IReadOnlyList<IspRecord> Snapshot()
    {
        lock (_gate) return _records.Values.OrderBy(r => r.Ip, StringComparer.Ordinal).ToList();
    }

    /// <summary>原子写盘；失败不抛（缓存丢了只是下次多查一次，不该影响测试）。</summary>
    public bool Save()
    {
        try
        {
            CacheFile file;
            lock (_gate)
            {
                // 顺手清掉长期没用的记录（4×TTL），避免文件无限增长
                var cutoff = DateTime.UtcNow - _ttl * 4;
                foreach (var key in _records.Where(kv => kv.Value.FetchedUtc < cutoff).Select(kv => kv.Key).ToList())
                    _records.Remove(key);

                file = new CacheFile
                {
                    Schema = SchemaVersion,
                    Records = _records.Values.OrderBy(r => r.Ip, StringComparer.Ordinal).ToList()
                };
            }

            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断一条缓存记录是否可信。
    /// "success 但 isp/as 全空"只可能来自解析故障（正常响应一定有归属信息）；
    /// 「无数据」同理——那是"这次没查到"，不该当成 30 天的既定事实。
    /// </summary>
    public static bool IsUsable(IspRecord record)
        => !(record.Ok && string.IsNullOrWhiteSpace(record.Isp) && string.IsNullOrWhiteSpace(record.As))
           && record.Message != "无数据";

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var json = File.ReadAllText(_path, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<CacheFile>(json, JsonOptions);

            // 版本不符 / 形状不符（例如早期版本的裸数组）→ 整份丢弃，重新查
            if (file is null || file.Schema != SchemaVersion || file.Records is null)
            {
                Console.WriteLine($"[INFO] 运营商缓存格式已更新（期望 Schema={SchemaVersion}，实际 {file?.Schema.ToString() ?? "旧格式"}），已丢弃旧缓存重新查询");
                return;
            }

            lock (_gate)
            {
                foreach (var record in file.Records)
                {
                    if (string.IsNullOrWhiteSpace(record.Ip)) continue;
                    if (!IsUsable(record)) continue; // 丢掉历史上写坏的记录（例如解析 bug 留下的"无数据"）
                    _records[record.Ip.Trim()] = record;
                }
            }
        }
        catch
        {
            // 缓存文件损坏：从空开始（下次查询会重新写），不影响主流程
            lock (_gate) _records.Clear();
        }
    }

    private sealed class CacheFile
    {
        public int Schema { get; set; }
        public List<IspRecord>? Records { get; set; }
    }
}
