using System.Windows;
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
    /// </summary>
    /// <param name="e">启动参数。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

    /// <summary>应用退出：释放单实例守卫（互斥量与命名事件）。</summary>
    /// <param name="e">退出参数。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;
        base.OnExit(e);
    }
}