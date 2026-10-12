using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 目标机临时工作目录（M8 残留清理）：在目标机临时目录（<see cref="Path.GetTempPath"/>）下创建
/// 本次运行的工作目录 <c>DeskIT\&lt;runId&gt;</c>，程序退出时删除；不写目标机其他位置。
/// </summary>
public interface ITempWorkspace
{
    /// <summary>本次运行的工作目录绝对路径；尚未创建时为空串。</summary>
    string RunDirectory { get; }

    /// <summary>
    /// 创建（或复用）本次运行的工作目录。
    /// </summary>
    /// <param name="runId">本次运行的标识（如会话标识），用作目录名。</param>
    /// <returns>工作目录绝对路径。</returns>
    string Create(string runId);

    /// <summary>
    /// 删除本次运行的工作目录；删除失败时返回失败路径清单与原因。
    /// </summary>
    /// <returns>清理结果。</returns>
    TempCleanupResult Cleanup();
}

/// <inheritdoc cref="ITempWorkspace" />
public sealed class TempWorkspace : ITempWorkspace
{
    /// <summary>临时目录下的产品子目录名（仅此一处，位于目标机临时目录内）。</summary>
    private const string RootFolderName = "DeskIT";

    /// <inheritdoc />
    public string RunDirectory { get; private set; } = string.Empty;

    /// <inheritdoc />
    public string Create(string runId)
    {
        var safeRunId = SanitizeRunId(runId);
        var root = Path.Combine(Path.GetTempPath(), RootFolderName);
        RunDirectory = Path.Combine(root, safeRunId);
        Directory.CreateDirectory(RunDirectory);
        return RunDirectory;
    }

    /// <inheritdoc />
    public TempCleanupResult Cleanup()
    {
        var leftovers = new List<string>();
        var reasons = new List<string>();

        if (string.IsNullOrEmpty(RunDirectory) || !Directory.Exists(RunDirectory))
        {
            RunDirectory = string.Empty;
            return new TempCleanupResult(true, leftovers, reasons);
        }

        try
        {
            Directory.Delete(RunDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            leftovers.Add(RunDirectory);
            reasons.Add(ex.Message);
        }

        // 尝试删除空的 DeskIT 根目录；非空或删除失败不影响结果。
        var root = Path.GetDirectoryName(RunDirectory);
        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(root).Any())
                {
                    Directory.Delete(root);
                }
            }
            catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
            {
                // 根目录删除失败无需提示（不影响本次运行目录的清理判定）。
            }
        }

        RunDirectory = string.Empty;
        return new TempCleanupResult(leftovers.Count == 0, leftovers, reasons);
    }

    /// <summary>把运行标识安全化为合法的目录名片段。</summary>
    /// <param name="runId">运行标识。</param>
    /// <returns>安全化的目录名。</returns>
    private static string SanitizeRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return "run-" + Guid.NewGuid().ToString("N")[..8];
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(runId.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return sanitized.Length > 64 ? sanitized[..64] : sanitized;
    }
}