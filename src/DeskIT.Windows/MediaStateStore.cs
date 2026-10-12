using System.Text.Json;

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

    /// <summary>operators.json 的文档结构。operators 用于步骤 7 登记处理人，本阶段保留空集。</summary>
    private sealed class MediaStateDocument
    {
        /// <summary>高级模式开关状态，默认关闭。</summary>
        public bool AdvancedMode { get; set; }

        /// <summary>处理人（工号 + 姓名）登记，步骤 7 起使用。</summary>
        public List<JsonElement> Operators { get; set; } = new();
    }
}