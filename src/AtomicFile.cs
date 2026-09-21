using System.IO;
using System.Text;
using System.Threading;

namespace LineTester;

/// <summary>
/// 原子写文件：先写同目录临时文件，再整体替换目标文件。
///
/// 替换带退避重试，因为 Windows 上目标文件可能被索引器/杀毒软件/记事本瞬时占用，
/// 表现为共享冲突或"拒绝访问"（实测会偶发）。原先这段逻辑内嵌在
/// <see cref="ConfigManager"/> 里，注册中心的 registry.json 也需要同一保证，故抽出共用。
/// </summary>
public static class AtomicFile
{
    /// <summary>退避间隔为 40/80/120/160/200ms，最坏约 600ms。</summary>
    private const int MaxAttempts = 6;

    public static void WriteAllText(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    File.Move(temporary, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsTransientWriteFailure(ex))
                {
                    Thread.Sleep(40 * attempt);
                }
            }
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static bool IsTransientWriteFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException;
}
