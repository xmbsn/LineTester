using System.Globalization;
using System.IO;

namespace LineTester;

/// <summary>按日清理日志目录；0 天表示永久保留。</summary>
public static class LogRetention
{
    public static int Cleanup(string rootDirectory, int keepDays, DateTime today)
    {
        if (keepDays <= 0 || !Directory.Exists(rootDirectory)) return 0;

        var cutoff = today.Date.AddDays(1 - keepDays);
        var removed = 0;
        foreach (var path in Directory.EnumerateDirectories(rootDirectory))
        {
            var info = new DirectoryInfo(path);
            // 只处理本程序格式的真实日期目录；不要递归进入链接或用户创建的目录。
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !DateTime.TryParseExact(info.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) ||
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != info.Name ||
                date >= cutoff)
                continue;

            Directory.Delete(path, recursive: true);
            removed++;
        }

        return removed;
    }
}
