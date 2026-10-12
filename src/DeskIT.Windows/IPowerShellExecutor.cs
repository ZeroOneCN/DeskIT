namespace DeskIT.Windows;

/// <summary>PowerShell 脚本的执行结果。</summary>
/// <param name="ExitCode">脚本退出码。</param>
/// <param name="StandardOutput">标准输出（可能已被截断）。</param>
/// <param name="StandardError">标准错误。</param>
/// <param name="Duration">执行耗时。</param>
public sealed record PowerShellExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration);

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
    /// <param name="timeout">执行超时时间。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>脚本执行结果（退出码、标准输出、标准错误、耗时）。</returns>
    Task<PowerShellExecutionResult> ExecuteAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>IPowerShellExecutor 的占位实现：本阶段仅固定接口形态，尚未实现真实逻辑。</summary>
public sealed class PowerShell51Executor : IPowerShellExecutor
{
    /// <inheritdoc />
    public Task<PowerShellExecutionResult> ExecuteAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException("PowerShell 5.1 执行器将在后续步骤（M3/M4）实现。");
}