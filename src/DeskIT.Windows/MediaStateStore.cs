using System.Text.Json;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 介质级状态存储（M1 高级模式开关）：状态随介质保存、跨机跨启动有效。
/// 存放于数据目录的 operators.json。
/// </summary>
public interface IMediaStateStore
{
    /// <summary>
    /// 读取高级模式开关状态（介质级记住）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>已开启返回 true；未初始化、读取失败或已关闭返回 false（默认关闭）。</returns>
    bool GetAdvancedModeEnabled(string dataDirectory);

    /// <summary>
    /// 写入高级模式开关状态，并立即落盘。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="enabled">是否开启高级模式。</param>
    void SetAdvancedModeEnabled(string dataDirectory, bool enabled);

    /// <summary>
    /// 读取介质当前登记的处理人（工号 + 姓名）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>当前处理人；未登记时返回 null。</returns>
    OperatorInfo? GetCurrentOperator(string dataDirectory);

    /// <summary>
    /// 设置介质当前处理人（工号 + 姓名），并登记到处理人清单（按工号去重），立即落盘。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="operator">处理人。</param>
    void SetCurrentOperator(string dataDirectory, OperatorInfo @operator);

    /// <summary>
    /// 读取介质上已登记的全部处理人。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>处理人清单；未登记时返回空列表。</returns>
    IReadOnlyList<OperatorInfo> ListOperators(string dataDirectory);
}

/// <summary>
/// operators.json 的实现。
/// 说明：本阶段（步骤 2）以明文 JSON 落盘；步骤 8（M8）会将该文件纳入主密码派生的
/// 认证加密存储，届时保持相同对外接口不变。
/// </summary>
public sealed class MediaStateStore : IMediaStateStore
{
    private const string FileName = "operators.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <inheritdoc />
    public bool GetAdvancedModeEnabled(string dataDirectory)
    {
        return ReadDocument(dataDirectory).AdvancedMode;
    }

    /// <inheritdoc />
    public void SetAdvancedModeEnabled(string dataDirectory, bool enabled)
    {
        var document = ReadDocument(dataDirectory);
        document.AdvancedMode = enabled;
        WriteDocument(dataDirectory, document);
    }

    /// <inheritdoc />
    public OperatorInfo? GetCurrentOperator(string dataDirectory)
    {
        var current = ReadDocument(dataDirectory).CurrentOperator;
        return LedgerRules.IsCompleteOperator(current) ? current : null;
    }

    /// <inheritdoc />
    public void SetCurrentOperator(string dataDirectory, OperatorInfo @operator)
    {
        var document = ReadDocument(dataDirectory);
        document.CurrentOperator = @operator;

        // 登记到处理人清单：按工号去重，已存在则更新姓名。
        var existing = document.Operators
            .FindIndex(item => string.Equals(item.EmployeeId, @operator.EmployeeId, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            document.Operators[existing] = @operator;
        }
        else
        {
            document.Operators.Add(@operator);
        }

        WriteDocument(dataDirectory, document);
    }

    /// <inheritdoc />
    public IReadOnlyList<OperatorInfo> ListOperators(string dataDirectory)
        => ReadDocument(dataDirectory).Operators;

    /// <summary>读取 operators.json；文件缺失或损坏时返回默认文档（高级模式关闭）。</summary>
    /// <param name="dataDirectory">数据目录。</param>
    /// <returns>介质状态文档。</returns>
    private static MediaStateDocument ReadDocument(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path))
        {
            return new MediaStateDocument();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MediaStateDocument>(json, JsonOptions) ?? new MediaStateDocument();
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            return new MediaStateDocument();
        }
    }

    /// <summary>写入 operators.json（自动创建数据目录）。</summary>
    /// <param name="dataDirectory">数据目录。</param>
    /// <param name="document">待写入的介质状态文档。</param>
    private static void WriteDocument(string dataDirectory, MediaStateDocument document)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));
    }

    /// <summary>operators.json 的文档结构。operators 登记处理人（工号 + 姓名）。</summary>
    private sealed class MediaStateDocument
    {
        /// <summary>高级模式开关状态，默认关闭。</summary>
        public bool AdvancedMode { get; set; }

        /// <summary>当前处理人（工号 + 姓名）；未登记时为 null。</summary>
        public OperatorInfo? CurrentOperator { get; set; }

        /// <summary>处理人（工号 + 姓名）登记清单。旧文件中为空数组，可正常反序列化。</summary>
        public List<OperatorInfo> Operators { get; set; } = new();
    }
}