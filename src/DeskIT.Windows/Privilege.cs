using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace DeskIT.Windows;

/// <summary>当前进程权限级别的检测器（M1 权限判定）。</summary>
public interface IPrivilegeInspector
{
    /// <summary>
    /// 检测当前进程是否以管理员身份运行。
    /// </summary>
    /// <returns>管理员返回 true；标准用户返回 false。</returns>
    bool IsAdministrator();
}

/// <summary>基于 Windows 令牌角色的权限检测器。</summary>
public sealed class WindowsPrivilegeInspector : IPrivilegeInspector
{
    /// <inheritdoc />
    public bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>以管理员身份重新启动自身的提权启动器（走系统标准 UAC，不尝试绕过）。</summary>
public interface IElevationLauncher
{
    /// <summary>
    /// 通过系统标准提权接口以管理员身份重新启动当前程序。
    /// </summary>
    /// <returns>成功发起提权返回 true；用户取消 UAC 或提权失败返回 false（调用方应回退到只读诊断模式）。</returns>
    bool TryRelaunchElevated();
}

/// <summary>提权启动器的实现：使用 ShellExecute 的 runas 动词，取消 UAC 不视为错误。</summary>
public sealed class WindowsElevationLauncher : IElevationLauncher
{
    /// <summary>提权后传给新实例的参数，用于标记“本进程已经过提权”，避免循环提示。</summary>
    public const string ElevatedArgument = "--elevated";

    /// <inheritdoc />
    public bool TryRelaunchElevated()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = ElevatedArgument,
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception)
        {
            // 用户在 UAC 对话框点击“否”，或系统策略禁止标准用户提权。
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}