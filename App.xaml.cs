using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DeskWidgets;

public partial class App : System.Windows.Application
{
    private static readonly object LogLock = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true; // 尽量保住进程，避免无声退出
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash("UnhandledException", args.ExceptionObject as Exception
                ?? new Exception(args.ExceptionObject?.ToString() ?? "unknown"));
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    internal static void LogCrash(string kind, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeskWidgets");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            var sb = new StringBuilder();
            sb.AppendLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} {kind} ----");
            sb.AppendLine(ex?.ToString() ?? "(null)");
            sb.AppendLine();
            lock (LogLock)
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch { /* ignore */ }
    }
}
