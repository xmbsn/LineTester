using System.IO;
using System.Text;

namespace LineTester.Registry;

/// <summary>
/// 分块与位图（规格 §5.3.3 / §5.5）。
///
/// 大清单（几十个节点）装不进一个 UDP 数据报，因此按固定块拆分：
/// 每块原始 768 字节 → base64 后恰好 1024 字符（无填充），加帧头约 1100 ≤ 1200。
/// 接收方用位图确认，发送方只重发缺失块。
/// </summary>
public static class RegistryChunking
{
    /// <summary>每块原始字节数。选 768 是为了 base64 后长度整齐（768 = 3×256 → 1024 字符）。</summary>
    public const int RawChunkBytes = 768;

    /// <summary>块数上限（64 × 768 B = 48 KB 原始 JSON ≈ 370 个节点）。</summary>
    public const int MaxChunks = 64;

    public static List<string> Split(string rawPayload)
    {
        var bytes = Encoding.UTF8.GetBytes(rawPayload);
        var count = Math.Max(1, (bytes.Length + RawChunkBytes - 1) / RawChunkBytes);

        var chunks = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = i * RawChunkBytes;
            var size = Math.Min(RawChunkBytes, bytes.Length - offset);
            chunks.Add(Convert.ToBase64String(bytes, offset, size));
        }

        return chunks;
    }

    /// <summary>块数（与 <see cref="Split"/> 一致，供接收方在拿到 n 前预估）。</summary>
    public static int ChunkCount(string rawPayload)
        => Math.Max(1, (Encoding.UTF8.GetByteCount(rawPayload) + RawChunkBytes - 1) / RawChunkBytes);

    /// <summary>是否存在缺失块；全部就绪则返回拼接后的原始文本。</summary>
    public static bool TryJoin(IReadOnlyList<string?> chunks, out string rawPayload)
    {
        rawPayload = "";
        var buffer = new MemoryStream();

        foreach (var chunk in chunks)
        {
            if (chunk is null) return false;

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(chunk);
            }
            catch (FormatException)
            {
                return false;
            }

            buffer.Write(bytes, 0, bytes.Length);
        }

        rawPayload = Encoding.UTF8.GetString(buffer.ToArray());
        return true;
    }

    public static string EmptyBitmap(int chunkCount) => new('0', (chunkCount + 3) / 4);

    public static string SetBit(string bitmap, int index)
    {
        if (index < 0) return bitmap;

        var charIndex = index / 4;
        if (charIndex >= bitmap.Length) return bitmap;

        var value = HexValue(bitmap[charIndex]);
        if (value < 0) return bitmap;

        value |= 1 << (index % 4);

        var chars = bitmap.ToCharArray();
        chars[charIndex] = "0123456789abcdef"[value];
        return new string(chars);
    }

    public static bool HasBit(string bitmap, int index)
    {
        var charIndex = index / 4;
        if (index < 0 || charIndex >= bitmap.Length) return false;

        var value = HexValue(bitmap[charIndex]);
        return value >= 0 && (value & (1 << (index % 4))) != 0;
    }

    public static List<int> MissingChunks(string bitmap, int chunkCount)
    {
        var missing = new List<int>();
        for (var i = 0; i < chunkCount; i++)
        {
            if (!HasBit(bitmap, i)) missing.Add(i);
        }

        return missing;
    }

    public static bool IsComplete(string bitmap, int chunkCount) => MissingChunks(bitmap, chunkCount).Count == 0;

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}
