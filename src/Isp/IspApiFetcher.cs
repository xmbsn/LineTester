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
/// ③ 免费版只有 HTTP（明文），官方声明不允许商用 —— 因此界面上给了开关，默认关闭。
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
            var records = TryParseBatchResponse(json);

            // 内容不是我们的 JSON（被代理/门户劫持返回网页等）：按查询失败处理，
            // **不能**当成"每个地址都无数据"缓存起来——那会把真问题掩盖成"没数据"
            if (records is null)
                return IspFetchResult.Failure("响应不是合法 JSON（可能被代理或上网门户劫持）");

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

    /// <summary>
    /// 解析批量响应。**单独抽出来是有血的教训的**：ip-api 返回的地址字段叫 <c>query</c>，
    /// 而本地模型里叫 <c>Ip</c>——直接反序列化成 IspRecord 会得到"每条记录地址为空"，
    /// 于是所有地址都被判成"无数据"（现场表现就是运营商信息一直显示未知）。
    /// 假抓取器测试不经过 JSON，压根发现不了，只能靠真实接口复现 + 这个解析用例守住。
    ///
    /// 返回 <c>null</c> 表示这段内容不是合法 JSON（例如被劫持返回了网页），
    /// 调用方应把它当成"查询失败"，而不是"每个地址都没数据"。
    /// </summary>
    public static List<IspRecord>? TryParseBatchResponse(string json)
    {
        List<ApiRecord>? items;

        try
        {
            items = JsonSerializer.Deserialize<List<ApiRecord>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (items is null) return new List<IspRecord>();

        var now = DateTime.UtcNow;
        var records = new List<IspRecord>(items.Count);

        foreach (var item in items)
        {
            if (item is null) continue;

            records.Add(new IspRecord
            {
                Ip = (item.Query ?? "").Trim(),
                Status = string.IsNullOrWhiteSpace(item.Status) ? "fail" : item.Status,
                Message = item.Message ?? "",
                Country = item.Country ?? "",
                Isp = item.Isp ?? "",
                Org = item.Org ?? "",
                As = item.As ?? "",
                AsName = item.AsName ?? "",
                FetchedUtc = now
            });
        }

        return records;
    }

    /// <summary>ip-api 响应的字段名（原样对齐官方文档，别改成我们自己的命名）。</summary>
    private sealed class ApiRecord
    {
        public string? Query { get; set; }
        public string? Status { get; set; }
        public string? Message { get; set; }
        public string? Country { get; set; }
        public string? Isp { get; set; }
        public string? Org { get; set; }
        public string? As { get; set; }
        public string? AsName { get; set; }
    }

    public void Dispose() => _http.Dispose();
}
