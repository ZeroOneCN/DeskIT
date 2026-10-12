using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DeskIT.Windows;

namespace DeskIT.App;

/// <summary>
/// 应用入口。负责单实例守卫的建立：非首个实例直接通知已有实例并退出，
/// 首个实例创建主窗口并接管守卫生命周期。
/// </summary>
public partial class App : Application
{
    private ISingleInstanceGuard? _singleInstanceGuard;

    /// <summary>
    /// 应用启动：建立单实例守卫，非首个实例则激活已有窗口后退出。
    /// 启动阶段的任何异常都会写入崩溃日志并弹窗提示，避免“双击后毫无反应”。
    /// </summary>
    /// <param name="e">启动参数。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            _singleInstanceGuard = new SingleInstanceGuard();

            if (!_singleInstanceGuard.IsFirstInstance)
            {
                // 已有实例在运行：请求其激活窗口，然后本实例退出。
                _singleInstanceGuard.SignalActivation();
                _singleInstanceGuard.Dispose();
                _singleInstanceGuard = null;
                Shutdown();
                return;
            }

            var window = new MainWindow(_singleInstanceGuard);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ReportFatal("启动阶段发生异常", ex);
            Shutdown(-1);
        }
    }

    /// <summary>界面线程未处理异常：记录日志并提示，随后结束进程（避免静默退出）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">未处理异常参数。</param>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportFatal("运行时发生未处理异常", e.Exception);
        e.Handled = true;
        Shutdown(-1);
    }

    /// <summary>
    /// 记录致命异常到目标机临时目录的崩溃日志（不影响业务数据），并以对话框告知 IT 人员日志位置。
    /// </summary>
    /// <param name="title">提示标题。</param>
    /// <param name="exception">异常对象。</param>
    private static void ReportFatal(string title, Exception exception)
    {
        string? logPath = null;
        try
        {
            var fileName = $"DeskIT-crash-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            logPath = Path.Combine(Path.GetTempPath(), fileName);
            var builder = new StringBuilder();
            builder.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"程序：{AppContext.BaseDirectory}");
            builder.AppendLine($"系统：{Environment.OSVersion} / .NET {Environment.Version}");
            builder.AppendLine(new string('-', 60));
            builder.AppendLine(exception.ToString());
            File.WriteAllText(logPath, builder.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 崩溃日志写入失败时不再抛出，以保证仍能弹出提示。
        }

        var message = $"{title}，程序已停止。{Environment.NewLine}{Environment.NewLine}{exception.Message}";
        if (logPath is not null)
        {
            message += $"{Environment.NewLine}{Environment.NewLine}诊断日志：{logPath}";
        }

        MessageBox.Show(message, "DeskIT 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>应用退出：释放单实例守卫（互斥量与命名事件）。</summary>
    /// <param name="e">退出参数。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;
        base.OnExit(e);
    }
}