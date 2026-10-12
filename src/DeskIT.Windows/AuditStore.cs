using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 审计事件存储（M8）。落盘位置为 U 盘数据目录下的 <c>data\audit\audit.db</c>，
/// 采用仅追加（append-only）NDJSON：每行一条 JSON，经介质金库加密后写入；单行损坏不影响其他行。
/// </summary>
public interface IAuditStore
{
    /// <summary>
    /// 追加一条审计事件（只追加，不改写历史行）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="auditEvent">审计事件。</param>
    void Append(string dataDirectory, AuditEvent auditEvent);

    /// <summary>
    /// 读取全部审计事件（按写入顺序）；文件不存在时返回空列表。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>审计事件列表。</returns>
    IReadOnlyList<AuditEvent> LoadAll(string dataDirectory);

    /// <summary>
    /// 清理保留期外的审计事件（PRD 7.5：默认保留 365 天，到期自动清理，界面不可编辑）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="retainDays">保留天数，默认 365 天。</param>
    /// <returns>被清理的事件数量。</returns>
    int Trim(string dataDirectory, DateTimeOffset now, int retainDays = MediaRules.DefaultAuditRetainDays);
}

/// <inheritdoc cref="IAuditStore" />
public sealed class AuditStore : IAuditStore
{
    private const string FolderName = "audit";
    private const string FileName = "audit.db";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IMediaVault? _vault;

    /// <summary>
    /// 初始化审计存储。
    /// </summary>
    /// <param name="vault">介质金库；为 null 时以明文落盘（仅供测试或未加密场景）。</param>
    public AuditStore(IMediaVault? vault = null)
    {
        _vault = vault;
    }

    /// <inheritdoc />
    public void Append(string dataDirectory, AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var path = GetPath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var line = JsonSerializer.Serialize(auditEvent, JsonOptions);
        var stored = _vault is null ? line : _vault.Protect(line);
        File.AppendAllText(path, stored + Environment.NewLine);
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditEvent> LoadAll(string dataDirectory)
    {
        var path = GetPath(dataDirectory);
        if (!File.Exists(path))
        {
            return Array.Empty<AuditEvent>();
        }

        var events = new List<AuditEvent>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var plain = TryUnprotect(line);
            if (plain is null)
            {
                // 单行损坏或校验失败不影响其他行。
                continue;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<AuditEvent>(plain, JsonOptions);
                if (parsed is not null)
                {
                    events.Add(parsed);
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // 单行损坏不影响其他行。
            }
        }

        return events;
    }

    /// <inheritdoc />
    public int Trim(string dataDirectory, DateTimeOffset now, int retainDays = MediaRules.DefaultAuditRetainDays)
    {
        var path = GetPath(dataDirectory);
        if (!File.Exists(path))
        {
            return 0;
        }

        var all = LoadAll(dataDirectory);
        var expired = MediaRules.SelectAuditEventsToTrim(all, now, retainDays)
            .Select(evt => evt.EventId)
            .ToHashSet(StringComparer.Ordinal);

        if (expired.Count == 0)
        {
            return 0;
        }

        var retained = all.Where(evt => !expired.Contains(evt.EventId)).ToList();
        var builder = new System.Text.StringBuilder();
        foreach (var evt in retained)
        {
            var line = JsonSerializer.Serialize(evt, JsonOptions);
            builder.Append(_vault is null ? line : _vault.Protect(line)).Append(Environment.NewLine);
        }

        File.WriteAllText(path, builder.ToString());
        return expired.Count;
    }

    /// <summary>解密一行（若为信封）；未解锁、校验失败或解析异常时返回 null。</summary>
    /// <param name="line">文件中的一行。</param>
    /// <returns>明文 JSON 或 null。</returns>
    private string? TryUnprotect(string line)
    {
        if (_vault is null || !MediaRules.IsEnvelope(line))
        {
            return line;
        }

        try
        {
            return _vault.Unprotect(line);
        }
        catch (Exception ex) when (ex is MediaLockedException or MediaIntegrityException)
        {
            return null;
        }
    }

    /// <summary>审计文件路径（data\audit\audit.db）。</summary>
    /// <param name="dataDirectory">数据目录。</param>
    /// <returns>审计文件绝对路径。</returns>
    private static string GetPath(string dataDirectory) => Path.Combine(dataDirectory, FolderName, FileName);
}