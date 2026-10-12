using System.Diagnostics;
using System.Text;

namespace DeskIT.Windows;

/// <summary>
/// Windows PowerShell 5.1 执行器实现：以 -EncodedCommand 传入脚本文本，避免转义与引号问题；
/// 强制 UTF-8 输出以便中文正确返回；超时或取消即终止整个进程树；输出按行数上限截断。
/// </summary>
public sealed class PowerShell51Executor : IPowerShellExecutor
{
    /// <summary>标准输出保留的最大行数（PRD：输出摘要 200 行）。</summary>
    private const int MaxOutputLines = 200;

    /// <summary>在脚本前强制 UTF-8 输出编码，保证中文在重定向场景下不乱码。</summary>
    private const string EncodingPreamble =
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8;\r\n" +
        "$OutputEncoding = [System.Text.Encoding]::UTF8;\r\n";

    /// <inheritdoc />
    public Task<PowerShellExecutionResult> ExecuteAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Run(script, timeout, cancellationToken), cancellationToken);

    /// <summary>
    /// 同步执行脚本并捕获结果。不向调用方抛出异常：任何启动失败都归类为“执行失败”。
    /// </summary>
    /// <param name="script">PowerShell 脚本文本。</param>
    /// <param name="timeout">执行超时。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>执行结果。</returns>
    private static PowerShellExecutionResult Run(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(EncodingPreamble + script));

        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(encoded);

        using var process = new Process { StartInfo = info };

        try
        {
            if (!process.Start())
            {
                return new PowerShellExecutionResult(-1, string.Empty, "无法启动 Windows PowerShell 5.1。", stopwatch.Elapsed, false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new PowerShellExecutionResult(-1, string.Empty, ex.Message, stopwatch.Elapsed, false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var canceled = false;
        var timedOut = false;

        if (!process.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds)))
        {
            timedOut = true;
            Kill(process);
        }

        // 取消标记在等待期间被触发的情况：等待结束后补判一次，避免漏杀进程。
        if (!timedOut && cancellationToken.IsCancellationRequested)
        {
            canceled = true;
            Kill(process);
        }

        try
        {
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // 进程句柄已释放：忽略。
        }

        stopwatch.Stop();

        var stdout = SafeResult(stdoutTask);
        var stderr = SafeResult(stderrTask);
        var exitCode = timedOut || canceled || !TryGetExitCode(process, out var code) ? -1 : code;

        return new PowerShellExecutionResult(exitCode, Truncate(stdout), stderr, stopwatch.Elapsed, timedOut || canceled);
    }

    /// <summary>终止整个进程树；失败时忽略（视为已退出）。</summary>
    /// <param name="process">目标进程。</param>
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 进程已退出或无法终止：按超时/取消归因。
        }
    }

    /// <summary>安全获取读取任务的文本结果；读取异常时返回空串。</summary>
    /// <param name="task">读取任务。</param>
    /// <returns>文本结果。</returns>
    private static string SafeResult(Task<string> task)
    {
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>尝试获取退出码。</summary>
    /// <param name="process">进程。</param>
    /// <param name="exitCode">退出码。</param>
    /// <returns>取到返回 true。</returns>
    private static bool TryGetExitCode(Process process, out int exitCode)
    {
        try
        {
            exitCode = process.ExitCode;
            return true;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
            return false;
        }
    }

    /// <summary>
    /// 按保留行数上限截断输出：保留前 N 行，并在末尾追加截断提示（不静默丢弃）。
    /// </summary>
    /// <param name="text">原始输出。</param>
    /// <returns>截断后的输出。</returns>
    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= MaxOutputLines)
        {
            return text;
        }

        var kept = string.Join(Environment.NewLine, lines.Take(MaxOutputLines));
        return $"{kept}{Environment.NewLine}……（输出已截断，共 {lines.Length} 行，仅保留前 {MaxOutputLines} 行）";
    }
}