namespace DeskIT.Windows;

/// <summary>
/// 目标机侧的单实例守卫（M1 单实例）：保证同一台机器同一登录会话内只有一个实例，
/// 重复启动时通知已有实例激活窗口，避免两个实例同时改动系统状态。
/// </summary>
public interface ISingleInstanceGuard : IDisposable
{
    /// <summary>当前进程是否为第一个（唯一）实例。</summary>
    bool IsFirstInstance { get; }

    /// <summary>当另一个实例请求激活本实例窗口时触发。</summary>
    event EventHandler? ActivationRequested;

    /// <summary>通知已存在的实例激活其窗口（由后启动的实例调用）。</summary>
    void SignalActivation();
}

/// <summary>
/// 基于命名互斥量与命名事件实现的单实例守卫。
/// 使用 Local\ 前缀（登录会话内生效），避免标准用户无权创建 Global\ 对象。
/// </summary>
public sealed class SingleInstanceGuard : ISingleInstanceGuard
{
    private const string MutexName = @"Local\DeskIT.SingleInstance.Mutex";
    private const string ActivateEventName = @"Local\DeskIT.SingleInstance.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activateEvent;
    private readonly Thread? _listener;
    private volatile bool _disposed;

    /// <summary>初始化守卫：创建或打开命名互斥量，并判定是否为唯一实例。</summary>
    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;

        if (createdNew)
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _listener = new Thread(ListenForActivation)
            {
                IsBackground = true,
                Name = "DeskIT.ActivateListener"
            };
            _listener.Start();
        }
    }

    /// <inheritdoc />
    public bool IsFirstInstance { get; }

    /// <inheritdoc />
    public event EventHandler? ActivationRequested;

    /// <inheritdoc />
    public void SignalActivation()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ActivateEventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // 已有实例尚未创建事件句柄，忽略即可。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _activateEvent?.Set();
        }
        catch (ObjectDisposedException)
        {
        }

        _listener?.Join(TimeSpan.FromMilliseconds(500));

        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 未持有互斥量时忽略。
            }
        }

        _activateEvent?.Dispose();
        _mutex.Dispose();
    }

    /// <summary>后台监听激活请求，收到后在工作线程上转发事件。</summary>
    private void ListenForActivation()
    {
        while (!_disposed)
        {
            if (_activateEvent is null || !_activateEvent.WaitOne())
            {
                continue;
            }

            if (_disposed)
            {
                break;
            }

            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}