using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 诊断会话的存储（M3）。落盘位置为 U 盘数据目录下的 data\sessions\，
/// 每个会话一个 JSON 文件，内含诊断结论与依据，作为处理记录与报告内容。
/// 自 M8 起，会话文件内容经介质金库<b>整体加密</b>落盘；未解锁时禁止写入。
/// </summary>
public interface IDiagnosisStore
{
    /// <summary>
    /// 保存一次诊断会话记录。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="session">诊断会话记录。</param>
    void Save(string dataDirectory, DiagnosisSession session);
}

/// <inheritdoc cref="IDiagnosisStore" />
public sealed class DiagnosisStore : IDiagnosisStore
{
    private const string RootFolderName = "sessions";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IMediaVault? _vault;

    /// <summary>
    /// 初始化会话存储。
    /// </summary>
    /// <param name="vault">介质金库；为 null 时以明文落盘（仅供测试或未加密场景）。</param>
    public DiagnosisStore(IMediaVault? vault = null)
    {
        _vault = vault;
    }

    /// <inheritdoc />
    public void Save(string dataDirectory, DiagnosisSession session)
    {
        var directory = Path.Combine(dataDirectory, RootFolderName);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{session.SessionId}.json");
        var content = JsonSerializer.Serialize(session, JsonOptions);
        File.WriteAllText(path, _vault is null ? content : _vault.Protect(content));
    }
}