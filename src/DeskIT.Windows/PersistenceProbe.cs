using System.Management;
using System.Security;
using DeskIT.Core;
using Microsoft.Win32;

namespace DeskIT.Windows;

/// <summary>
/// 持久化项只读探测（M9）：在自定义命令执行前后分别采集“系统服务 / 计划任务 / 开机自启动项”，供差异对比。
/// <b>本探测不修改任何持久化项</b>；任一类采集失败（权限拒绝、命名空间缺失、超时等）都返回“不可用”，
/// 由差异对比如实标注“未知”，绝不假装“无变化”。
/// </summary>
public interface IPersistenceProbe
{
    /// <summary>采集系统服务名集合（注册表 HKLM\SYSTEM\CurrentControlSet\Services 的子键名）。</summary>
    /// <returns>服务快照。</returns>
    PersistenceSnapshot CaptureServices();

    /// <summary>采集计划任务集合（任务路径 + 名称，经 PowerShell 5.1 只读查询）。</summary>
    /// <returns>计划任务快照。</returns>
    PersistenceSnapshot CaptureScheduledTasks();

    /// <summary>采集开机自启动项名称集合（Win32_StartupCommand）。</summary>
    /// <returns>自启动项快照。</returns>
    PersistenceSnapshot CaptureStartupItems();
}

/// <inheritdoc cref="IPersistenceProbe" />
public sealed class PersistenceProbe : IPersistenceProbe
{
    /// <summary>计划任务查询的超时秒数（只读枚举，取较大余量以避免误判超时）。</summary>
    private const int TaskQueryTimeoutSeconds = 30;

    /// <summary>只读枚举计划任务的脚本（输出“任务路径 + 任务名”，去重）。</summary>
    private const string TaskQueryScript
        = "Get-ScheduledTask | ForEach-Object { \"$($_.TaskPath)$($_.TaskName)\" } | Sort-Object -Unique\r\n";

    private readonly IPowerShellExecutor _powerShell;

    /// <summary>
    /// 初始化持久化项探测。
    /// </summary>
    /// <param name="powerShell">PowerShell 5.1 执行器（计划任务枚举用）；为 null 时使用内置实现。</param>
    public PersistenceProbe(IPowerShellExecutor? powerShell = null)
    {
        _powerShell = powerShell ?? new PowerShell51Executor();
    }

    /// <inheritdoc />
    public PersistenceSnapshot CaptureServices()
    {
        try
        {
            var names = RegistryProbe.ReadSubKeyNames(
                RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Services",
                RegistryView.Registry64);
            return new PersistenceSnapshot(PersistenceChangeKind.Service, names, true, string.Empty);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return new PersistenceSnapshot(PersistenceChangeKind.Service, Array.Empty<string>(), false, ex.Message);
        }
    }

    /// <inheritdoc />
    public PersistenceSnapshot CaptureScheduledTasks()
    {
        try
        {
            var execution = _powerShell
                .ExecuteAsync(TaskQueryScript, TimeSpan.FromSeconds(TaskQueryTimeoutSeconds), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (execution.TimedOut)
            {
                return new PersistenceSnapshot(PersistenceChangeKind.ScheduledTask, Array.Empty<string>(), false, "查询超时");
            }

            if (execution.ExitCode != 0)
            {
                var reason = string.IsNullOrWhiteSpace(execution.StandardError)
                    ? $"退出码 {execution.ExitCode}"
                    : execution.StandardError.Trim();
                return new PersistenceSnapshot(PersistenceChangeKind.ScheduledTask, Array.Empty<string>(), false, reason);
            }

            var names = execution.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new PersistenceSnapshot(PersistenceChangeKind.ScheduledTask, names, true, string.Empty);
        }
        catch (Exception ex)
        {
            return new PersistenceSnapshot(PersistenceChangeKind.ScheduledTask, Array.Empty<string>(), false, ex.Message);
        }
    }

    /// <inheritdoc />
    public PersistenceSnapshot CaptureStartupItems()
    {
        try
        {
            var rows = WmiQuery.Run("SELECT Name FROM Win32_StartupCommand");
            var names = rows
                .Select(row => WmiQuery.Str(row, "Name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new PersistenceSnapshot(PersistenceChangeKind.StartupItem, names, true, string.Empty);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or SecurityException)
        {
            return new PersistenceSnapshot(PersistenceChangeKind.StartupItem, Array.Empty<string>(), false, ex.Message);
        }
    }
}