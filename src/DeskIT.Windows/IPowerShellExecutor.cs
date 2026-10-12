namespace DeskIT.Windows;

/// <summary>PowerShell 脚本的执行结果。</summary>
/// <param name="ExitCode">脚本退出码；超时被终止时为 -1。</param>
/// <param name="StandardOutput">标准输出（已按行数上限截断）。</param>
/// <param name="StandardError">标准错误。</param>
/// <param name="Duration">执行耗时。</param>
/// <param name="TimedOut">是否因超出时限而被终止。</param>
public sealed record PowerShellExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut);

/// <summary>
/// Windows PowerShell 5.1 执行器。所有诊断与修复脚本都必须经由此接口执行，
/// 不得依赖 PowerShell 7+（目标机大概率未安装）。
/// </summary>
public interface IPowerShellExecutor
{
    /// <summary>
    /// 在 Windows PowerShell 5.1 下执行一段脚本。
    /// </summary>
    /// <param name="script">待执行的 PowerShell 脚本文本（须兼容 5.1）。</param>
    /// <param name="timeout">执行超时时间；超时即终止进程并置 TimedOut。</param>
    /// <param name="cancellationToken">取消标记；取消时终止进程。</param>
    /// <returns>脚本执行结果（退出码、标准输出、标准错误、耗时、是否超时）。</returns>
    Task<PowerShellExecutionResult> ExecuteAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}