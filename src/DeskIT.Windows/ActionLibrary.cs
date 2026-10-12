using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>动作库加载结果：可用动作清单与定义问题清单（个别定义有问题不影响其余动作可用）。</summary>
/// <param name="Actions">通过校验的动作定义。</param>
/// <param name="Problems">定义问题说明（缺文件、解析失败、重复标识、占位符无来源等）。</param>
public sealed record ActionLibrary(
    IReadOnlyList<ActionDescriptor> Actions,
    IReadOnlyList<string> Problems);

/// <summary>
/// 修复动作库加载器：从 U 盘 <c>actions/</c> 目录读取声明式动作定义（JSON）。
/// 动作定义为可读文件，便于 IT 管理员现场增补与审计；M9 的“动作沉淀”也写入该目录。
/// </summary>
public interface IActionLibraryLoader
{
    /// <summary>
    /// 从指定基准目录下的 <c>actions/</c> 加载动作库。
    /// </summary>
    /// <param name="baseDirectory">程序所在目录（U 盘根）；读取 <c>actions/*.json</c>。</param>
    /// <returns>动作库加载结果。</returns>
    ActionLibrary Load(string baseDirectory);
}

/// <inheritdoc cref="IActionLibraryLoader" />
public sealed class ActionLibraryLoader : IActionLibraryLoader
{
    /// <summary>动作定义所在目录名。</summary>
    private const string ActionsFolderName = "actions";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    public ActionLibrary Load(string baseDirectory)
    {
        var root = Path.Combine(baseDirectory, ActionsFolderName);
        var problems = new List<string>();

        if (!Directory.Exists(root))
        {
            return new ActionLibrary(
                Array.Empty<ActionDescriptor>(),
                new[] { $"未找到动作定义目录：{ActionsFolderName}（应位于程序所在目录下）。" });
        }

        var actions = new List<ActionDescriptor>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(root, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file);
            IReadOnlyList<ActionDescriptor> parsed;

            try
            {
                var text = File.ReadAllText(file);
                parsed = JsonSerializer.Deserialize<List<ActionDescriptor>>(text, JsonOptions) ?? new List<ActionDescriptor>();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{fileName}：解析失败（{ex.Message}）。");
                continue;
            }

            foreach (var action in parsed)
            {
                var problem = Validate(action, seenIds);
                if (problem is not null)
                {
                    problems.Add($"{fileName}：{problem}");
                    continue;
                }

                seenIds.Add(action.ActionId);
                actions.Add(action);
            }
        }

        return new ActionLibrary(actions, problems);
    }

    /// <summary>
    /// 校验单个动作定义；通过返回 null，否则返回问题说明。
    /// </summary>
    /// <param name="action">动作定义。</param>
    /// <param name="seenIds">已登记的标识集合（用于查重）。</param>
    /// <returns>问题说明；无问题返回 null。</returns>
    private static string? Validate(ActionDescriptor action, ISet<string> seenIds)
    {
        if (string.IsNullOrWhiteSpace(action.ActionId))
        {
            return "动作标识为空。";
        }

        if (seenIds.Contains(action.ActionId))
        {
            return $"动作标识重复：{action.ActionId}。";
        }

        if (string.IsNullOrWhiteSpace(action.Name))
        {
            return $"{action.ActionId}：动作名称为空。";
        }

        if (string.IsNullOrWhiteSpace(action.Script))
        {
            return $"{action.ActionId}：缺少脚本入口。";
        }

        if (action.TimeoutSeconds <= 0)
        {
            return $"{action.ActionId}：超时必须为正数秒。";
        }

        var unresolved = ActionRules.UnresolvedTokens(action);
        if (unresolved.Count > 0)
        {
            return $"{action.ActionId}：脚本占位符无来源：{string.Join("、", unresolved)}。";
        }

        return null;
    }
}