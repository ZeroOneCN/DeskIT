using System.Diagnostics;
using System.Management;
using System.Security;
using Microsoft.Win32;

namespace DeskIT.Windows;

/// <summary>
/// 注册表只读探测（M2 采集用）。不写入任何注册表项；读取失败时返回 null，由调用方归因。
/// </summary>
internal static class RegistryProbe
{
    /// <summary>
    /// 读取 HKLM 下某个字符串值。
    /// </summary>
    /// <param name="subKey">子键路径。</param>
    /// <param name="name">值名称。</param>
    /// <param name="view">注册表视图（默认 64 位视图）。</param>
    /// <returns>值文本；不存在或为空时返回 null。</returns>
    public static string? ReadLocalMachineString(string subKey, string name, RegistryView view = RegistryView.Registry64)
        => ReadString(RegistryHive.LocalMachine, subKey, name, view);

    /// <summary>
    /// 读取 HKCU 下某个字符串值。
    /// </summary>
    /// <param name="subKey">子键路径。</param>
    /// <param name="name">值名称。</param>
    /// <returns>值文本；不存在或为空时返回 null。</returns>
    public static string? ReadCurrentUserString(string subKey, string name)
        => ReadString(RegistryHive.CurrentUser, subKey, name, RegistryView.Default);

    /// <summary>
    /// 读取注册表字符串值。
    /// </summary>
    /// <param name="hive">注册表根。</param>
    /// <param name="subKey">子键路径。</param>
    /// <param name="name">值名称。</param>
    /// <param name="view">注册表视图。</param>
    /// <returns>值文本；不存在或为空时返回 null。</returns>
    public static string? ReadString(RegistryHive hive, string subKey, string name, RegistryView view = RegistryView.Default)
    {
        var value = ReadValue(hive, subKey, name, view);
        if (value is null)
        {
            return null;
        }

        var text = value is string[] array
            ? string.Join("；", array.Where(x => !string.IsNullOrWhiteSpace(x)))
            : value.ToString();

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// 读取注册表原始值。
    /// </summary>
    /// <param name="hive">注册表根。</param>
    /// <param name="subKey">子键路径。</param>
    /// <param name="name">值名称。</param>
    /// <param name="view">注册表视图。</param>
    /// <returns>原始值；不存在时返回 null。</returns>
    public static object? ReadValue(RegistryHive hive, string subKey, string name, RegistryView view = RegistryView.Default)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(subKey);
        return key?.GetValue(name);
    }

    /// <summary>
    /// 读取注册表 DWORD 值。
    /// </summary>
    /// <param name="hive">注册表根。</param>
    /// <param name="subKey">子键路径。</param>
    /// <param name="name">值名称。</param>
    /// <param name="view">注册表视图。</param>
    /// <returns>数值；不存在或类型不符时返回 null。</returns>
    public static int? ReadInt(RegistryHive hive, string subKey, string name, RegistryView view = RegistryView.Default)
    {
        var value = ReadValue(hive, subKey, name, view);
        return value switch
        {
            int i => i,
            long l => (int)l,
            _ => null
        };
    }

    /// <summary>
    /// 枚举某个注册表键下的子键名称。
    /// </summary>
    /// <param name="hive">注册表根。</param>
    /// <param name="subKey">子键路径。</param>
    /// <param name="view">注册表视图。</param>
    /// <returns>子键名称列表；键不存在时返回空列表。</returns>
    public static IReadOnlyList<string> ReadSubKeyNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(subKey);
        return key?.GetSubKeyNames() ?? Array.Empty<string>();
    }
}

/// <summary>
/// 只读外部命令输出捕获（M2 采集用，如 netsh 查询 Wi-Fi 状态）。
/// 仅用于系统内置、参数固定的只读查询命令；修复类脚本一律走 PowerShell 5.1 执行器。
/// </summary>
internal static class ProcessCapture
{
    /// <summary>
    /// 只读命令输出所用的解码编码。
    /// Windows 内置命令行工具（powercfg、netsh、w32tm、cmdkey 等）按控制台 OEM 代码页输出，
    /// 因此必须按 OEM 代码页解码，否则中文输出会乱码。
    /// </summary>
    private static readonly System.Text.Encoding NativeOutputEncoding = ResolveNativeOutputEncoding();

    /// <summary>
    /// 解析本机 OEM 代码页编码；代码页不可用（如非中文系统缺少注册项）时回退 UTF-8，保证不抛错。
    /// </summary>
    /// <returns>用于解码本机命令行工具输出的编码。</returns>
    private static System.Text.Encoding ResolveNativeOutputEncoding()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PlatformNotSupportedException)
        {
            return System.Text.Encoding.UTF8;
        }
    }

    /// <summary>
    /// 运行一条只读命令并捕获标准输出（不显示窗口，超时即终止）。
    /// </summary>
    /// <param name="fileName">可执行文件（如 netsh.exe）。</param>
    /// <param name="arguments">固定参数。</param>
    /// <param name="timeout">超时时间。</param>
    /// <returns>标准输出文本；失败或超时返回 null。</returns>
    public static string? Run(string fileName, string arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = NativeOutputEncoding,
            StandardErrorEncoding = NativeOutputEncoding
        };

        using var process = Process.Start(info);
        if (process is null)
        {
            return null;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // 进程已退出或无法终止：忽略，按超时归因。
            }

            return null;
        }

        return outputTask.GetAwaiter().GetResult();
    }
}

/// <summary>异常归因：区分“无权限读取”与“其他失败”，用于 M2 分组字段的状态判定。</summary>
internal static class AccessDenial
{
    /// <summary>WMI 的 WBEM_E_ACCESS_DENIED。</summary>
    private const int WbemAccessDenied = unchecked((int)0x80041003);

    /// <summary>
    /// 判断异常是否属于权限拒绝（策略或权限不足导致无法读取）。
    /// </summary>
    /// <param name="exception">采集过程中捕获的异常。</param>
    /// <returns>属于权限拒绝返回 true。</returns>
    public static bool IsDenied(Exception exception) => exception switch
    {
        UnauthorizedAccessException => true,
        SecurityException => true,
        ManagementException management => management.HResult == WbemAccessDenied
            || management.Message.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase)
            || management.Message.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
            || management.Message.Contains("access denied", StringComparison.OrdinalIgnoreCase),
        _ => exception.InnerException is not null && IsDenied(exception.InnerException)
    };

    /// <summary>
    /// 判断异常是否表示对象/命名空间不存在，即该采集项在目标机上“不适用”。
    /// </summary>
    /// <param name="exception">采集过程中捕获的异常。</param>
    /// <returns>不适用返回 true。</returns>
    public static bool IsNotApplicable(Exception exception) => exception switch
    {
        PlatformNotSupportedException => true,
        NotSupportedException => true,
        ManagementException management => management.HResult == unchecked((int)0x8004100E)
            || management.Message.Contains("无效的命名空间", StringComparison.OrdinalIgnoreCase)
            || management.Message.Contains("invalid namespace", StringComparison.OrdinalIgnoreCase)
            || management.Message.Contains("invalid class", StringComparison.OrdinalIgnoreCase),
        _ => exception.InnerException is not null && IsNotApplicable(exception.InnerException)
    };
}