using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 动作沉淀结果（M9）。
/// </summary>
/// <param name="Success">是否沉淀成功。</param>
/// <param name="FileName">写入的动作文件名（含扩展名）；失败时为 null。</param>
/// <param name="ActionId">生成的动作标识；失败时为 null。</param>
/// <param name="Problems">字段级问题清单（校验失败时）。</param>
/// <param name="Message">面向 IT 人员的中文结果说明。</param>
public sealed record ActionSedimentResult(
    bool Success,
    string? FileName,
    string? ActionId,
    IReadOnlyList<string> Problems,
    string Message);

/// <summary>
/// 动作沉淀存储（M9）：把一条自定义命令沉淀为正式修复动作，写入 U 盘 <c>actions/</c> 目录。
/// 先校验请求，通过后生成动作定义并写入 <c>actions/custom-&lt;slug&gt;.json</c>（JSON 数组）；
/// <b>不覆盖已存在文件</b>；写入后不自行重载动作库（由调用方决定何时刷新）。
/// </summary>
public interface IActionSedimentStore
{
    /// <summary>
    /// 把沉淀请求保存为动作定义文件。
    /// </summary>
    /// <param name="baseDirectory">程序所在目录（U 盘根）；写入其下的 <c>actions/</c>。</param>
    /// <param name="request">沉淀请求。</param>
    /// <returns>沉淀结果。</returns>
    ActionSedimentResult Save(string baseDirectory, ActionSedimentRequest request);
}

/// <inheritdoc cref="IActionSedimentStore" />
public sealed class ActionSedimentStore : IActionSedimentStore
{
    /// <summary>动作定义所在目录名。</summary>
    private const string ActionsFolderName = "actions";

    /// <summary>沉淀文件的名称前缀。</summary>
    private const string SedimentFilePrefix = "custom-";

    /// <summary>动作标识前缀。</summary>
    private const string SedimentIdPrefix = "custom.";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    public ActionSedimentResult Save(string baseDirectory, ActionSedimentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = CustomCommandRules.ValidateSediment(request);
        if (!validation.IsValid)
        {
            return new ActionSedimentResult(false, null, null, validation.Problems, "沉淀请求未通过校验，未写入任何文件。");
        }

        var root = Path.Combine(baseDirectory, ActionsFolderName);
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ActionSedimentResult(false, null, null, Array.Empty<string>(), $"无法创建动作目录（{ActionsFolderName}）：{ex.Message}");
        }

        var sequence = NextSequence(root);
        var descriptor = CustomCommandRules.BuildActionDescriptor(request, sequence);
        var fileName = CustomCommandRules.BuildSedimentFileName(request.Name, sequence);
        var path = Path.Combine(root, fileName);

        // 不覆盖已存在文件。
        if (File.Exists(path))
        {
            return new ActionSedimentResult(false, fileName, descriptor.ActionId, Array.Empty<string>(), $"目标文件已存在，未覆盖：{fileName}");
        }

        try
        {
            var json = JsonSerializer.Serialize(new List<ActionDescriptor> { descriptor }, WriteOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ActionSedimentResult(false, fileName, descriptor.ActionId, Array.Empty<string>(), $"写入沉淀文件失败：{ex.Message}");
        }

        return new ActionSedimentResult(
            true,
            fileName,
            descriptor.ActionId,
            Array.Empty<string>(),
            $"已沉淀为动作 {descriptor.ActionId}（{fileName}）；返回“修复动作（M4）”页重新加载后即可复用。");
    }

    /// <summary>
    /// 扫描既有 <c>custom-*.json</c> 中的动作标识数字后缀，取最大值 + 1 作为新动作序号，保证标识唯一。
    /// </summary>
    /// <param name="root">动作目录。</param>
    /// <returns>下一个可用序号（从 1 开始）。</returns>
    private static int NextSequence(string root)
    {
        var max = 0;
        foreach (var file in Directory.GetFiles(root, SedimentFilePrefix + "*.json"))
        {
            IReadOnlyList<ActionDescriptor>? parsed;
            try
            {
                var text = File.ReadAllText(file);
                parsed = JsonSerializer.Deserialize<List<ActionDescriptor>>(text, ReadOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 单个文件解析失败不影响序号推进。
                continue;
            }

            if (parsed is null)
            {
                continue;
            }

            foreach (var action in parsed)
            {
                if (TryParseSequence(action.ActionId, out var sequence) && sequence > max)
                {
                    max = sequence;
                }
            }
        }

        return max + 1;
    }

    /// <summary>解析动作标识中的数字后缀（形如 custom.001 → 1）。</summary>
    /// <param name="actionId">动作标识。</param>
    /// <param name="sequence">解析出的序号。</param>
    /// <returns>解析成功返回 true。</returns>
    private static bool TryParseSequence(string? actionId, out int sequence)
    {
        sequence = 0;
        if (string.IsNullOrWhiteSpace(actionId)
            || !actionId.StartsWith(SedimentIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(actionId[SedimentIdPrefix.Length..], out sequence) && sequence > 0;
    }
}