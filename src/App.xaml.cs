using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace LineTester;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 同一目录共用一份 appsettings.json 与 logs 目录，重复启动会互相覆盖，
        // 因此按“程序目录”做单实例限制（不同目录仍可各跑一个实例）。
        if (!TryAcquireSingleInstance())
        {
            MessageBox.Show(
                "该目录下的 LineTester 已经在运行：\n" + AppContext.BaseDirectory +
                "\n\n同一目录共用一份 appsettings.json 和 logs 目录，请勿重复启动。",
                "已在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            var name = "Local\\LineTester_" + ShortHash(AppContext.BaseDirectory);
            _singleInstance = new Mutex(initiallyOwned: true, name, out var createdNew);
            if (createdNew) return true;

            _singleInstance.Dispose();
            _singleInstance = null;
            return false;
        }
        catch
        {
            return true; // 无法创建互斥体时不阻止启动
        }
    }

    private static string ShortHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            "发生未处理的异常，程序会继续运行，但建议重启后重新开始测试：\n\n" + e.Exception.Message,
            "运行异常", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteCrashLog(Exception? ex)
    {
        if (ex is null) return;

        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var line = $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} =====\r\n{ex}\r\n";
            File.AppendAllText(Path.Combine(dir, "error.log"), line, new UTF8Encoding(false));
        }
        catch
        {
            // 记录失败时不再抛出，避免二次崩溃
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
