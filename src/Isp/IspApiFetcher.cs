using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LineTester.Isp;

/// <summary>抓取器接口：真实实现走 ip-api，自检里替换成假实现（不联网）。</summary>
public interface IIspFetcher
{
    Task<IspFetchResult> FetchAsync(IReadOnlyList<string> ips, CancellationToken ct);
}

/// <summary>
/// ip-api 批量接口（规格见 https://ip-api.com/docs/api:batch）。
///
/// 免费额度的硬约束（决定了实现方式）：
/// ① **15 次/分钟**，超了会 429，持续超会封 1 小时 → 因此必须批量（一次最多 100 个 IP）+ 落盘缓存；
/// ② 响应头 <c>X-Rl</c>（本窗口剩余次数）与 <c>X-Ttl</c>（重置秒数）必须读：
///    <c>X-Rl=0</c> 时**不能再发请求**，否则就是主动把自己封掉；
/// ③ 免费版只有 HTTP（明文），官方声明不允许商用 —— 因此界面上给了开关，默认开启但可关闭。
/// </summary>
public sealed class IspApiFetcher : IIspFetcher, IDisposable
{
    private const string Fields = "status,message,query,country,isp,org,as,asname";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly string _endpoint;

    public IspApiFetcher(string endpoint, int timeoutMs)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://ip-api.com/batch" : endpoint.Trim();
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30000)) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "LineTester/4 (machine-room line tester)");
    }

    public async Task<IspFetchResult> FetchAsync(IReadOnlyList<string> ips, CancellationToken ct)
    {
        if (ips.Count == 0) return new IspFetchResult();

        var batch = ips.Take(100).ToList();

        // fields/lang 走 GET 参数；lang=zh-CN 让国家名也变中文（界面全中文）
        var separator = _endpoint.Contains('?') ? "&" : "?";
        var url = $"{_endpoint}{separator}fields={Fields}&lang=zh-CN";

        var body = new StringBuilder("[");
        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0) body.Append(',');
            body.Append("{\"query\":\"").Append(batch[i]).Append("\"}");
        }
        body.Append(']');

        try
        {
            using var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);

            var remaining = ReadHeader(response, "X-Rl");
            var resetAfter = ReadHeader(response, "X-Ttl");

            if ((int)response.StatusCode == 429)
            {
                return new IspFetchResult
                {
                    Ok = false,
                    Error = "已被 ip-api 限流（HTTP 429）",
                    Remaining = 0,
                    ResetAfterSeconds = resetAfter >= 0 ? resetAfter : 60
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new IspFetchResult
                {
                    Ok = false,
                    Error = $"HTTP {(int)response.StatusCode}",
                    Remaining = remaining,
                    ResetAfterSeconds = resetAfter
                };
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var records = JsonSerializer.Deserialize<List<IspRecord>>(json, JsonOptions) ?? new List<IspRecord>();

            var now = DateTime.UtcNow;
            foreach (var record in records)
            {
                record.FetchedUtc = now;
                if (string.IsNullOrWhiteSpace(record.Status)) record.Status = "fail";
            }

            return new IspFetchResult
            {
                Records = records,
                Remaining = remaining,
                ResetAfterSeconds = resetAfter
            };
        }
        catch (OperationCanceledException)
        {
            return IspFetchResult.Failure("查询被取消");
        }
        catch (Exception ex)
        {
            return IspFetchResult.Failure(ex.Message);
        }
    }

    private static int ReadHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) &&
           int.TryParse(values.FirstOrDefault(), out var value)
            ? value
            : -1;

    public void Dispose() => _http.Dispose();
}
