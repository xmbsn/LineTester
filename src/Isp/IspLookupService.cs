using System.Net;
using System.Net.Sockets;

namespace LineTester.Isp;

/// <summary>
/// 运营商查询后台服务：去重 → 域名解析 → 只查公网地址 → 批量抓取 → 落盘缓存。
///
/// 三条硬性要求（都由"不能影响测试"推出来）：
/// ① **界面线程绝不做网络/DNS**：界面每半秒只调 <see cref="TryGet"/> 读缓存；
/// ② **不重复查询**：已缓存的地址不再请求，失败/内网地址也记下来，避免每半秒重试；
/// ③ **被限流就停手**：<c>X-Rl=0</c> 或 429 之后在 <c>X-Ttl</c> 秒内不再发请求（否则会被封 1 小时）。
/// </summary>
public sealed class IspLookupService : IDisposable
{
    /// <summary>域名解析结果的复用时长。</summary>
    private static readonly TimeSpan HostTtl = TimeSpan.FromMinutes(10);

    /// <summary>一次查询失败后，多久再试同一个地址。</summary>
    private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 自检用的抓取器工厂（返回 null 用真实 HTTP 实现）。
    /// 与 <see cref="ConfigManager.OverridePath"/> 同类：给测试留的显式替换点，生产代码不碰它。
    /// </summary>
    public static Func<IspConfig, IIspFetcher>? FetcherFactory;

    private readonly IspConfig _config;
    private readonly IspCacheStore _cache;
    private readonly IIspFetcher _fetcher;
    private readonly object _gate = new();

    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Ip, DateTime AtUtc)> _resolvedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _loop;

    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private int _backoffSeconds = 15;

    public IspLookupService(IspConfig config, IspCacheStore cache, IIspFetcher? fetcher = null)
    {
        _config = config;
        _cache = cache;
        _fetcher = fetcher ?? FetcherFactory?.Invoke(config) ?? new IspApiFetcher(config.Endpoint, config.TimeoutMs);

        Status.Enabled = config.Enabled;
        Status.Cached = _cache.Count;

        if (config.Enabled) _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public IspLookupStatus Status { get; } = new();

    public bool Enabled => _config.Enabled;

    public IspCacheStore Cache => _cache;

    /// <summary>登记"我需要这个地址（IP 或域名）的运营商信息"。廉价、可重复调用；内网地址会被忽略。</summary>
    public void Request(string? hostOrIp)
    {
        if (!_config.Enabled) return;

        var value = hostOrIp?.Trim() ?? "";
        if (value.Length == 0 || value == "-") return;

        lock (_gate)
        {
            if (_retryAfter.TryGetValue(value, out var until) && until > DateTime.UtcNow) return;

            // 已经是 IP：缓存里有就直接跳过
            if (IPAddress.TryParse(value, out _))
            {
                if (_cache.TryGet(value, out _)) return;
            }
            else if (_resolvedHosts.TryGetValue(value, out var resolved) &&
                     resolved.AtUtc > DateTime.UtcNow - HostTtl &&
                     _cache.TryGet(resolved.Ip, out _))
            {
                return; // 域名：解析过且结果已缓存
            }

            _pending.Add(value);
            Status.Pending = _pending.Count;
        }
    }

    /// <summary>读缓存（界面线程调用；只读，不触发任何查询）。</summary>
    public IspRecord? TryGet(string? ip)
    {
        var value = ip?.Trim() ?? "";
        return value.Length == 0 ? null : _cache.TryGet(value, out var record) ? record : null;
    }

    /// <summary>把缓存里已知的地址记为"已尝试"，用于界面上仍为未知的地址。</summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Status.Throttled && DateTime.UtcNow >= _nextAttemptUtc)
                    await PumpAsync(ct).ConfigureAwait(false);

                Status.Cached = _cache.Count;
                Status.Pending = PendingCount();
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 循环绝不因异常退出（否则运营商信息会静默停更）
                Status.LastError = "查询循环异常：" + ex.Message;
                try { await Task.Delay(2000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        List<string> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;

            batch = _pending.Take(100).ToList();
            foreach (var item in batch) _pending.Remove(item);
            Status.Pending = _pending.Count;
        }

        // ① 域名 → IP（解析在后台线程；解析不出来就按失败处理，10 分钟后再说）
        var byIp = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();

        foreach (var host in batch)
        {
            var ip = await ResolveAsync(host, ct).ConfigureAwait(false);
            if (ip.Length == 0)
            {
                unresolved.Add(host);
                continue;
            }

            if (!byIp.TryGetValue(ip, out var hosts))
            {
                hosts = new List<string>();
                byIp[ip] = hosts;
            }

            hosts.Add(host);
        }

        MarkRetry(unresolved);

        // ② 只查公网地址：内网/保留地址本地就能判定，不浪费额度
        var toFetch = new List<string>();
        foreach (var ip in byIp.Keys)
        {
            if (_cache.TryGet(ip, out _)) continue;

            if (!IspAddressRules.IsPublicIp(ip))
            {
                // 记成"内网地址"，这样不会每半秒重试一次
                _cache.Put(new IspRecord
                {
                    Ip = ip,
                    Status = "fail",
                    Message = "private range",
                    FetchedUtc = DateTime.UtcNow
                });
                continue;
            }

            toFetch.Add(ip);
        }

        if (toFetch.Count == 0)
        {
            if (unresolved.Count > 0) Status.LastError = $"无法解析：{string.Join("、", unresolved.Take(3))}";
            else _cache.Save();
            return;
        }

        // ③ 批量抓取
        var result = await _fetcher.FetchAsync(toFetch, ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            Status.LastError = result.Error ?? "查询失败";
            MarkRetry(batch);

            var delay = result.ResetAfterSeconds > 0 ? result.ResetAfterSeconds : _backoffSeconds;
            if (result.Remaining == 0 || result.ResetAfterSeconds > 0)
                Status.ThrottledUntilUtc = DateTime.UtcNow.AddSeconds(delay);

            _nextAttemptUtc = DateTime.UtcNow.AddSeconds(delay);
            _backoffSeconds = Math.Min(_backoffSeconds * 2, 300);
            return;
        }

        foreach (var record in result.Records) _cache.Put(record);

        // 中心返回的记录里没有的地址（理论上不该发生）：按失败处理，避免卡在"查询中"
        var returned = result.Records.Select(r => r.Ip).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var ip in toFetch.Where(ip => !returned.Contains(ip)))
        {
            _cache.Put(new IspRecord { Ip = ip, Status = "fail", Message = "无数据", FetchedUtc = DateTime.UtcNow });
        }

        Status.LastSuccessUtc = DateTime.UtcNow;
        Status.LastError = null;
        Status.Cached = _cache.Count;

        // 额度用尽 → 按 X-Ttl 停手（免费版超限会被封 1 小时）
        if (result.Remaining == 0 && result.ResetAfterSeconds > 0)
        {
            Status.ThrottledUntilUtc = DateTime.UtcNow.AddSeconds(result.ResetAfterSeconds);
            _nextAttemptUtc = Status.ThrottledUntilUtc.Value;
        }
        else
        {
            Status.ThrottledUntilUtc = null;
            _backoffSeconds = 15;
            _nextAttemptUtc = DateTime.MinValue;
        }

        _cache.Save();
    }

    private async Task<string> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out _)) return host;

        lock (_gate)
        {
            if (_resolvedHosts.TryGetValue(host, out var cached) && cached.AtUtc > DateTime.UtcNow - HostTtl)
                return cached.Ip;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            var picked = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString()
                         ?? addresses.FirstOrDefault()?.ToString()
                         ?? "";

            if (picked.Length == 0) return "";

            lock (_gate) _resolvedHosts[host] = (picked, DateTime.UtcNow);
            return picked;
        }
        catch
        {
            return "";
        }
    }

    private void MarkRetry(IEnumerable<string> hosts)
    {
        var until = DateTime.UtcNow + FailureRetry;
        lock (_gate)
        {
            foreach (var host in hosts) _retryAfter[host] = until;
        }
    }

    private int PendingCount()
    {
        lock (_gate) return _pending.Count;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        try { (_fetcher as IDisposable)?.Dispose(); } catch { }
    }
}
