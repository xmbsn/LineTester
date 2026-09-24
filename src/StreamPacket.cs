using System.Buffers.Binary;
using System.Text;

namespace LineTester;

/// <summary>
/// 构造用于链路压测的“串流形态”探测包。
///
/// 这不是可播放的视频，也不会伪造某个厂商的协议；它保留可关联的探测头，
/// 后面放入与实时视频分片相近的 Annex-B 起始码、帧信息和高熵二进制数据。
/// 这样既能测到真实的包长/分片行为，又不依赖受版权或协议约束的媒体样本。
/// </summary>
public static class StreamPacket
{
    /// <summary>为 1200B 实时媒体分片留出公网链路 MTU 余量的默认下限。</summary>
    public const int DefaultMinBytes = 900;

    /// <summary>为 1200B 实时媒体分片留出公网链路 MTU 余量的默认上限。</summary>
    public const int DefaultMaxBytes = 1200;

    /// <summary>允许配置的最小包长；足够容纳关联头和模拟的视频分片头。</summary>
    public const int MinBytes = 128;

    /// <summary>
    /// UDP 的理论上限低于 65535；这里再留出协议/IP 头空间，避免配置能保存但发送必失败。
    /// 大于典型 MTU 的值可用于刻意测试 IP 分片。
    /// </summary>
    public const int MaxBytes = 65000;

    private const string Magic = "LT3";
    private const int HeaderSizeFieldWidth = 5;

    /// <summary>按闭区间随机选择一个包长并组装探测包。</summary>
    public static byte[] Create(long seq, string probeId, int minBytes, int maxBytes)
    {
        var targetSize = Random.Shared.Next(minBytes, maxBytes + 1);
        var header = Encoding.ASCII.GetBytes($"{Magic}|{seq}|{probeId}|v2|{targetSize.ToString($"D{HeaderSizeFieldWidth}")}|" );

        // 配置校验负责拦截；这一层仍明确报错，避免调用者无声发出不符合承诺的包。
        if (targetSize < header.Length + 16)
            throw new ArgumentOutOfRangeException(nameof(minBytes), "串流包大小不足以容纳探测头");

        var packet = new byte[targetSize];
        header.CopyTo(packet, 0);
        FillVideoLikePayload(packet.AsSpan(header.Length), seq, probeId);
        return packet;
    }

    /// <summary>
    /// 当 TCP 正在分段接收时，从已收到的开头识别 LT3 包并取得完整包长。
    /// 返回 false 代表尚未拿到完整头，或该数据不是本格式。
    /// </summary>
    public static bool TryGetExpectedLength(ReadOnlySpan<byte> received, out int expectedLength)
    {
        expectedLength = 0;
        if (received.Length < Magic.Length + 1 || !received[..Magic.Length].SequenceEqual(Encoding.ASCII.GetBytes(Magic)) ||
            received[Magic.Length] != (byte)'|')
            return false;

        // LT3|seq|probe-id|v2|NNNNN|。只读 ASCII 头，不把后面任意二进制负载转成文本。
        var separators = 0;
        var start = 0;
        for (var i = 0; i < received.Length; i++)
        {
            if (received[i] != (byte)'|') continue;
            separators++;
            if (separators == 5)
            {
                var sizeField = received[start..i];
                if (sizeField.Length != HeaderSizeFieldWidth) return false;

                var length = 0;
                foreach (var value in sizeField)
                {
                    if (value is < (byte)'0' or > (byte)'9') return false;
                    length = length * 10 + value - (byte)'0';
                }

                if (length is < MinBytes or > MaxBytes) return false;
                expectedLength = length;
                return true;
            }

            start = i + 1;
        }

        return false;
    }

    /// <summary>LT3 包同样请求被动端附上它观察到的源地址。</summary>
    public static bool IsStreamPacket(ReadOnlySpan<byte> packet)
        => packet.Length >= Magic.Length + 1 && packet[..Magic.Length].SequenceEqual(Encoding.ASCII.GetBytes(Magic)) &&
           packet[Magic.Length] == (byte)'|';

    /// <summary>已接收字节是否仍可能是 LT3 头的前缀（供 TCP 分段读取使用）。</summary>
    public static bool HasStreamPacketPrefix(ReadOnlySpan<byte> received)
    {
        ReadOnlySpan<byte> prefix = "LT3|"u8;
        if (received.Length == 0 || received.Length > prefix.Length) return IsStreamPacket(received);
        return received.SequenceEqual(prefix[..received.Length]);
    }

    private static void FillVideoLikePayload(Span<byte> payload, long seq, string probeId)
    {
        // Annex-B H.264/H.265 风格的起始码 + 一个非 IDR slice 标记；仅用于抓包辨识。
        payload[0] = 0x00;
        payload[1] = 0x00;
        payload[2] = 0x00;
        payload[3] = 0x01;
        payload[4] = 0x41;
        BinaryPrimitives.WriteInt64BigEndian(payload[5..13], seq);

        // 用 Probe ID 作为种子产生高熵、每包不同的填充，避免全零/重复字节被压缩或特殊优化。
        var seed = 2166136261u;
        foreach (var ch in probeId) seed = (seed ^ ch) * 16777619u;
        for (var i = 13; i < payload.Length; i++)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            payload[i] = (byte)seed;
        }
    }
}
