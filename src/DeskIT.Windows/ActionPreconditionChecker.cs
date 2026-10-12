using DeskIT.Core;
using Microsoft.Win32;

namespace DeskIT.Windows;

/// <summary>动作前置条件检查：不满足的条件使该项标记“已跳过”并给出原因，不阻断其他动作。</summary>
public interface IActionPreconditionChecker
{
    /// <summary>
    /// 列出动作未被满足的前置条件。
    /// </summary>
    /// <param name="action">动作定义。</param>
    /// <returns>未满足的前置条件清单；全部满足时为空。</returns>
    IReadOnlyList<ActionPrecondition> Unmet(ActionDescriptor action);
}

/// <inheritdoc cref="IActionPreconditionChecker" />
public sealed class ActionPreconditionChecker : IActionPreconditionChecker
{
    /// <inheritdoc />
    public IReadOnlyList<ActionPrecondition> Unmet(ActionDescriptor action)
        => ActionRules.UnmetPreconditions(action, IsMet);

    /// <summary>
    /// 判定单个前置条件是否满足。无法判定的情形按“满足”处理，交由脚本自身在运行时如实报错，避免误跳过。
    /// </summary>
    /// <param name="precondition">前置条件。</param>
    /// <returns>满足返回 true。</returns>
    private static bool IsMet(ActionPrecondition precondition) => precondition.Kind switch
    {
        ActionPreconditionKind.PathExists => PathExists(precondition.Value),
        ActionPreconditionKind.ServiceExists => ServiceExists(precondition.Value),
        _ => true
    };

    /// <summary>
    /// 判定路径（文件或目录）是否存在；支持环境变量展开。路径为空或展开失败时按“满足”处理。
    /// </summary>
    /// <param name="value">路径。</param>
    /// <returns>存在返回 true。</returns>
    private static bool PathExists(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value);
            return File.Exists(expanded) || Directory.Exists(expanded);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// 判定服务是否注册（读取 <c>HKLM\SYSTEM\CurrentControlSet\Services</c>）。
    /// 读取异常时按“满足”处理，避免因权限问题误跳过。
    /// </summary>
    /// <param name="value">服务名。</param>
    /// <returns>存在返回 true。</returns>
    private static bool ServiceExists(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + value);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }
}