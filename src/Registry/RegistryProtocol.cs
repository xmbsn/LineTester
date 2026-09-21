using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LineTester.Registry;

/// <summary>
/// 注册协议帧编解码与消息 DTO（规格 docs/REGISTRY-PROTOCOL.md §5）。
///
/// 帧格式：<c>LT1|&lt;TYPE&gt;|&lt;MAC&gt;|&lt;rawPayload&gt;\n</c>（单行 ASCII，payload 为紧凑单行 JSON）。
///
/// MAC 之所以放在**帧里而不是 payload 里**：它是对 <c>TYPE|payload</c> 计算的，
/// 若把 mac 字段放进 payload，就变成"签名覆盖了签名自身"——接收方按原文重算必然不一致。
/// 放在帧里，双方对同一段原文计算，无需任何 JSON 规范化。
///
/// 端口复用判定：报文以 <c>LT1|</c> 开头即为协议消息，否则走既有探测回显路径。
/// 现有探测包以数字开头（<c>42|guid|v2</c>），老客户端以十六进制 GUID 开头，不会冲突。
/// </summary>
public static class RegistryProtocol
{
    public const string Magic = "LT1|";

    public const string TypeRegister = "REG";
    public const string TypeSync = "SYNC";
    public const string TypeAck = "ACK";
    public const string TypePart = "PART";
    public const string TypePartAck = "ACKP";
    public const string TypeError = "ERR";

    /// <summary>帧结束符。</summary>
    public const byte FrameTerminator = (byte)'\n';

    /// <summary>单个 UDP 数据报的安全上限（字节）。</summary>
    public const int MaxDatagramBytes = 1200;

    /// <summary>超过该长度的 payload 需要分块（留出帧头与结束符余量）。</summary>
    public const int SinglePayloadLimit = 900;

    /// <summary>与规格 §5.4 对齐的 JSON 约定（camelCase、紧凑单行）。</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>报文是否走注册协议路径（否则是探测包）。</summary>
    public static bool LooksLikeProtocolMessage(byte[] buffer, int length)
    {
        if (length < Magic.Length) return false;

        for (var i = 0; i < Magic.Length; i++)
        {
            if (buffer[i] != (byte)Magic[i]) return false;
        }

        return true;
    }

    public static byte[] Encode(string type, string mac, string rawPayload)
        => Encoding.UTF8.GetBytes($"{Magic}{type}|{mac}|{rawPayload}\n");

    /// <summary>用密钥签名后编码（正规路径）。</summary>
    public static byte[] EncodeSigned(string secret, string type, string rawPayload)
        => Encode(type, RegistryAuth.ComputeMac(secret, type, rawPayload), rawPayload);

    public static byte[] EncodeSigned<T>(string secret, string type, T message)
        => EncodeSigned(secret, type, Serialize(message));

    /// <summary>校验帧的签名（对收到的原文重算）。</summary>
    public static bool VerifyFrame(string secret, string type, string mac, string rawPayload)
        => RegistryAuth.VerifyMac(secret, type, rawPayload, mac);

    /// <summary>
    /// 解析帧。成功时 <paramref name="rawPayload"/> 是**原样文本**——
    /// HMAC 就是对这段原文计算的，因此绝不能重新序列化（字段顺序/转义会变）。
    /// </summary>
    public static bool TryDecode(byte[] buffer, int length, out string type, out string mac, out string rawPayload)
    {
        type = "";
        mac = "";
        rawPayload = "";

        if (!LooksLikeProtocolMessage(buffer, length)) return false;

        var text = Encoding.UTF8.GetString(buffer, 0, length).TrimEnd('\n', '\r');

        // Magic 本身以 '|' 结尾（"LT1|"），所以类型从 Magic.Length 开始，
        // 到下一个 '|' 结束；再下一个 '|' 之后全部是 payload（payload 内部可含 '|'）。
        var typeEnd = text.IndexOf('|', Magic.Length);
        if (typeEnd < 0) return false;

        var macEnd = text.IndexOf('|', typeEnd + 1);
        if (macEnd < 0) return false;

        type = text[Magic.Length..typeEnd];
        mac = text[(typeEnd + 1)..macEnd];
        rawPayload = text[(macEnd + 1)..];
        return type.Length > 0;
    }

    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Json);

    /// <summary>
    /// 只取请求 ID。ERR 应答要带上它，发起方才能对上号（取不到就回空串，客户端也接受）。
    /// </summary>
    public static string ExtractRequestId(string rawPayload)
        => Deserialize<RequestIdOnly>(rawPayload)?.ReqId ?? "";

    private sealed class RequestIdOnly
    {
        public string ReqId { get; set; } = "";
    }

    public static T? Deserialize<T>(string rawPayload) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(rawPayload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>错误码（规格 §5.3.4）。</summary>
public static class RegistryErrorCodes
{
    public const string Auth = "auth";
    public const string BadRequest = "badrequest";
    public const string Loopback = "loopback";
    public const string Unspecified = "unspecified";
    public const string LinkLocal = "linklocal";
    public const string Throttled = "throttled";

    /// <summary>该 nodeId 已被中心移除（不会被心跳加回来，需中心侧恢复）。</summary>
    public const string Removed = "removed";

    public const string Unsupported = "unsupported";
    public const string Internal = "internal";
}

public sealed class RegisterMessage
{
    public string ReqId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>主外网地址（老中心/老客户端只认这个字段）。</summary>
    public string Ip { get; set; } = "";

    public int Port { get; set; }
    public bool Tcp { get; set; }
    public bool Udp { get; set; }

    /// <summary>是否宣告"本端可被 ICMP(ping) 探测"。</summary>
    public bool Icmp { get; set; }

    public bool Distributable { get; set; } = true;
    public string Instance { get; set; } = "";
    public ulong Rev { get; set; }

    /// <summary>
    /// 本节点的**全部**外网地址（含主地址，端口相同）。同一机房有多个出口 IP 时用得上。
    /// 只有一个地址时可以不带（老中心会忽略这个字段，仍按 <see cref="Ip"/> 处理）。
    /// </summary>
    public List<string>? Ips { get; set; }

    public long Ts { get; set; }
    public string Nonce { get; set; } = "";
    public string Mac { get; set; } = "";

    /// <summary>有效地址列表：带了 Ips 就用它，否则退化为单个 Ip。</summary>
    [JsonIgnore]
    public List<string> AllIps
    {
        get
        {
            var list = new List<string>(4);
            if (Ips is not null)
            {
                foreach (var ip in Ips)
                {
                    var value = ip?.Trim() ?? "";
                    if (value.Length > 0 && !list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
                }
            }

            var primary = Ip?.Trim() ?? "";
            if (primary.Length > 0)
            {
                // 主地址必须排第一：老中心/老客户端只读 Ip 字段，顺序不一致会让"主地址"与 Ips[0] 打架
                list.RemoveAll(item => string.Equals(item, primary, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, primary);
            }

            return list;
        }
    }
}

public sealed class SyncMessage
{
    public string ReqId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public ulong Since { get; set; }
    public long Ts { get; set; }
    public string Nonce { get; set; } = "";
    public string Mac { get; set; } = "";
}

/// <summary>清单里的一条节点快照（下发给客户端）。</summary>
public sealed class NodeSnapshot
{
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>主外网地址。</summary>
    public string Ip { get; set; } = "";

    public int Port { get; set; }
    public bool Tcp { get; set; }
    public bool Udp { get; set; }

    /// <summary>对端宣告"可被 ICMP(ping) 探测"。</summary>
    public bool Icmp { get; set; }

    public bool Online { get; set; } = true;

    /// <summary>全部外网地址（含主地址）；只有一个地址时可不带。</summary>
    public List<string>? Ips { get; set; }

    /// <summary>有效地址列表：带了 Ips 就用它，否则退化为单个 Ip。</summary>
    [JsonIgnore]
    public List<string> AllIps
    {
        get
        {
            var list = new List<string>(4);
            if (Ips is not null)
            {
                foreach (var ip in Ips)
                {
                    var value = ip?.Trim() ?? "";
                    if (value.Length > 0 && !list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
                }
            }

            var primary = Ip?.Trim() ?? "";
            if (primary.Length > 0)
            {
                // 主地址必须排第一：老中心/老客户端只读 Ip 字段，顺序不一致会让"主地址"与 Ips[0] 打架
                list.RemoveAll(item => string.Equals(item, primary, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, primary);
            }

            return list;
        }
    }
}

/// <summary>中心回显"它看到的请求源地址"，供客户端与手填的 PublicHost 交叉核对。</summary>
public sealed class SelfCheckInfo
{
    public string SeenIp { get; set; } = "";
    public int SeenPort { get; set; }
}

public sealed class AckMessage
{
    public string ReqId { get; set; } = "";
    public ulong Rev { get; set; }

    /// <summary>false 表示客户端 rev 已是最新，<see cref="Nodes"/> 省略（稳态只有 1 个包）。</summary>
    public bool Changed { get; set; }

    /// <summary>中心建议的下次注册间隔（秒）；客户端只允许用它缩短间隔。</summary>
    public int ReannounceAfter { get; set; }

    public SelfCheckInfo? SelfCheck { get; set; }

    /// <summary>当 <see cref="Changed"/> 为 true：当前全部可分发节点的快照。</summary>
    public List<NodeSnapshot>? Nodes { get; set; }

    /// <summary>保留字段，v1 恒为空（快照语义下不需要墓碑）。</summary>
    public List<string>? Deleted { get; set; }
}

public sealed class PartMessage
{
    public string XferId { get; set; } = "";

    /// <summary>关联的请求号：让客户端知道这批分块拼出来的是哪个请求的应答。</summary>
    public string ReqId { get; set; } = "";

    public int I { get; set; }
    public int N { get; set; }
    public string Data { get; set; } = "";
}

public sealed class PartAckMessage
{
    public string XferId { get; set; } = "";

    /// <summary>位图：每个十六进制字符表示 4 个块，最低位对应块 0。</summary>
    public string Bitmap { get; set; } = "";
}

public sealed class ErrorMessage
{
    public string ReqId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public int RetryAfter { get; set; }
}
