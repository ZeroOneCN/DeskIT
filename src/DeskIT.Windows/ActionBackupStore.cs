using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 动作备份区存储（M4）。落盘位置为 U 盘数据目录下的 <c>data\backup\actions\</c>，
/// 每次动作执行写一份审计记录，并保留脚本产出的回滚原始数据文件。
/// 自 M8 起，该目录内容统一经介质金库<b>加密</b>落盘；未解锁时禁止写入。
/// </summary>
public interface IActionBackupStore
{
    /// <summary>
    /// 保存一条动作执行记录。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="record">动作审计与回滚信息记录。</param>
    void Save(string dataDirectory, ActionBackupRecord record);

    /// <summary>
    /// 取得动作备份区目录（供执行器在其中创建回滚数据文件）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>备份区目录绝对路径（已确保存在）。</returns>
    string PrepareDirectory(string dataDirectory);
}

/// <inheritdoc cref="IActionBackupStore" />
public sealed class ActionBackupStore : IActionBackupStore
{
    private const string RootFolderName = "backup";
    private const string ActionsFolderName = "actions";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IMediaVault? _vault;

    /// <summary>
    /// 初始化动作备份区存储。
    /// </summary>
    /// <param name="vault">介质金库；为 null 时以明文落盘（仅供测试或未加密场景）。</param>
    public ActionBackupStore(IMediaVault? vault = null)
    {
        _vault = vault;
    }

    /// <inheritdoc />
    public string PrepareDirectory(string dataDirectory)
    {
        var directory = Path.Combine(dataDirectory, RootFolderName, ActionsFolderName);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <inheritdoc />
    public void Save(string dataDirectory, ActionBackupRecord record)
    {
        var directory = PrepareDirectory(dataDirectory);
        var path = Path.Combine(directory, $"{record.RunId}.json");
        var content = JsonSerializer.Serialize(record, JsonOptions);
        File.WriteAllText(path, _vault is null ? content : _vault.Protect(content));
    }
}