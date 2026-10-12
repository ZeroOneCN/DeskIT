namespace DeskIT.Windows;

/// <summary>介质不可用（不可写或已消失）事件参数。</summary>
public sealed class MediaUnavailableEventArgs : EventArgs
{
    /// <summary>
    /// 构造介质不可用事件参数。
    /// </summary>
    /// <param name="message">面向 IT 人员的中文提示。</param>
    public MediaUnavailableEventArgs(string message) => Message = message;

    /// <summary>面向 IT 人员的中文提示。</summary>
    public string Message { get; }
}

/// <summary>
/// 介质看门狗（M8 拔出保护）：会话期间按固定间隔（默认 10 秒）探测介质可写性，
/// 检测到不可写或介质消失时触发 <see cref="MediaUnavailable"/> 事件，供界面立即停止后续动作、
/// 保留已完成结果、把当前会话标记为“已中断”并明确提示。
/// </summary>
public sealed class MediaWatchdog : IDisposable
{
    /// <summary>默认探测间隔（10 秒）。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly IMediaWritabilityProbe _probe;
    private readonly Action? _flushSession;

    private Timer? _timer;
    private string _dataDirectory = string.Empty;
    private int _unavailableReported;

    /// <summary>
    /// 初始化介质看门狗。
    /// </summary>
    /// <param name="probe">介质可写性探测器；为 null 时使用内置实现。</param>
    /// <param name="flushSession">探测前执行的会话文件 flush 回调；可为 null。</param>
    public MediaWatchdog(IMediaWritabilityProbe? probe = null, Action? flushSession = null)
    {
        _probe = probe ?? new MediaWritabilityProbe();
        _flushSession = flushSession;
    }

    /// <summary>介质不可写或已消失时触发（可能在线程池线程上触发，订阅方需自行切回界面线程）。</summary>
    public event EventHandler<MediaUnavailableEventArgs>? MediaUnavailable;

    /// <summary>当前是否正在探测。</summary>
    public bool IsRunning => _timer is not null;

    /// <summary>
    /// 启动探测：每 <paramref name="interval"/> 探测一次介质可写性。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="interval">探测间隔；未提供时使用默认 10 秒。</param>
    public void Start(string dataDirectory, TimeSpan? interval = null)
    {
        Stop();

        _dataDirectory = dataDirectory;
        _unavailableReported = 0;
        var period = interval ?? DefaultInterval;
        _timer = new Timer(_ => _ = ProbeAndNotifyAsync(), null, period, period);
    }

    /// <summary>停止探测并释放计时器。</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// 立即探测一次介质可写性，并在探测前 flush 会话文件。
    /// </summary>
    /// <returns>可写返回 true。</returns>
    public async Task<bool> ProbeOnceAsync()
    {
        try
        {
            _flushSession?.Invoke();
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            // flush 失败不影响可写性判定本身。
        }

        return await _probe.ProbeAsync(_dataDirectory).ConfigureAwait(false);
    }

    /// <summary>释放计时器资源。</summary>
    public void Dispose() => Stop();

    /// <summary>探测一次并在不可写时触发事件（同一不可用状态只触发一次，恢复后可再次触发）。</summary>
    /// <returns>无返回值的异步任务。</returns>
    private async Task ProbeAndNotifyAsync()
    {
        var writable = await ProbeOnceAsync().ConfigureAwait(false);
        if (writable)
        {
            Interlocked.Exchange(ref _unavailableReported, 0);
            return;
        }

        if (Interlocked.Exchange(ref _unavailableReported, 1) == 1)
        {
            return;
        }

        MediaUnavailable?.Invoke(
            this,
            new MediaUnavailableEventArgs(
                "检测到介质不可写或已被移除：已立即停止后续动作，已完成结果予以保留，当前会话将标记为“已中断”。"));
    }
}