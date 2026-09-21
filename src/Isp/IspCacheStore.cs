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
/// </summary>
public sealed class IspCacheStore
{
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

    public IReadOnlyList<IspRecord> Snapshot()
    {
        lock (_gate) return _records.Values.OrderBy(r => r.Ip, StringComparer.Ordinal).ToList();
    }

    /// <summary>原子写盘；失败不抛（缓存丢了只是下次多查一次，不该影响测试）。</summary>
    public bool Save()
    {
        try
        {
            List<IspRecord> snapshot;
            lock (_gate)
            {
                // 顺手清掉长期没用的记录（4×TTL），避免文件无限增长
                var cutoff = DateTime.UtcNow - _ttl * 4;
                foreach (var key in _records.Where(kv => kv.Value.FetchedUtc < cutoff).Select(kv => kv.Key).ToList())
                    _records.Remove(key);

                snapshot = _records.Values.OrderBy(r => r.Ip, StringComparer.Ordinal).ToList();
            }

            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(snapshot, JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var json = File.ReadAllText(_path, Encoding.UTF8);
            var records = JsonSerializer.Deserialize<List<IspRecord>>(json, JsonOptions);
            if (records is null) return;

            lock (_gate)
            {
                foreach (var record in records)
                {
                    if (!string.IsNullOrWhiteSpace(record.Ip)) _records[record.Ip.Trim()] = record;
                }
            }
        }
        catch
        {
            // 缓存文件损坏：从空开始（下次查询会重新写），不影响主流程
            lock (_gate) _records.Clear();
        }
    }
}
