using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 诊断会话的存储（M3）。落盘位置为 U 盘数据目录下的 data\sessions\，
/// 每个会话一个 JSON 文件，内含诊断结论与依据，作为处理记录与报告内容。
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

    /// <inheritdoc />
    public void Save(string dataDirectory, DiagnosisSession session)
    {
        var directory = Path.Combine(dataDirectory, RootFolderName);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{session.SessionId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(session, JsonOptions));
    }
}