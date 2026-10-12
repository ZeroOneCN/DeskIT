using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 台账存储（M7）。落盘位置为 U 盘数据目录下的 data\ledger.db，采用追加式（append-only）
/// NDJSON：每行一条 JSON，绝不重写历史行；单行损坏不影响其他行。
/// </summary>
public interface ILedgerStore
{
    /// <summary>
    /// 读取全部处理记录（补充说明按记录标识归并到对应记录）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>处理记录列表（按写入顺序）；台账不存在时返回空列表。</returns>
    IReadOnlyList<HandlingRecord> LoadAll(string dataDirectory);

    /// <summary>
    /// 追加一条处理记录（只追加，不改写历史行）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="record">处理记录。</param>
    void Append(string dataDirectory, HandlingRecord record);

    /// <summary>
    /// 为指定记录追加一条补充说明（作为独立行写入，加载时归并到对应记录）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="recordId">记录标识。</param>
    /// <param name="supplement">补充说明。</param>
    void AppendSupplement(string dataDirectory, string recordId, RecordSupplement supplement);
}

/// <inheritdoc cref="ILedgerStore" />
public sealed class LedgerStore : ILedgerStore
{
    private const string FileName = "ledger.db";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <inheritdoc />
    public IReadOnlyList<HandlingRecord> LoadAll(string dataDirectory)
    {
        var path = GetPath(dataDirectory);
        if (!File.Exists(path))
        {
            return Array.Empty<HandlingRecord>();
        }

        var records = new List<HandlingRecord>();
        var indexByRecordId = new Dictionary<string, int>(StringComparer.Ordinal);
        var supplements = new Dictionary<string, List<RecordSupplement>>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            LedgerLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<LedgerLine>(line, JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // 单行损坏不影响其他行。
                continue;
            }

            if (parsed is null)
            {
                continue;
            }

            if (string.Equals(parsed.Kind, LedgerLine.RecordKind, StringComparison.Ordinal) && parsed.Record is not null)
            {
                indexByRecordId[parsed.Record.RecordId] = records.Count;
                records.Add(parsed.Record);
            }
            else if (string.Equals(parsed.Kind, LedgerLine.SupplementKind, StringComparison.Ordinal)
                     && !string.IsNullOrWhiteSpace(parsed.RecordId)
                     && parsed.Supplement is not null)
            {
                if (!supplements.TryGetValue(parsed.RecordId!, out var list))
                {
                    list = new List<RecordSupplement>();
                    supplements[parsed.RecordId!] = list;
                }

                list.Add(parsed.Supplement);
            }
        }

        // 把补充说明归并到对应记录（只追加、按写入顺序）。
        for (var i = 0; i < records.Count; i++)
        {
            if (supplements.TryGetValue(records[i].RecordId, out var list))
            {
                records[i] = records[i] with { Supplements = list };
            }
        }

        return records;
    }

    /// <inheritdoc />
    public void Append(string dataDirectory, HandlingRecord record)
    {
        var path = GetPath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(new LedgerLine
        {
            Kind = LedgerLine.RecordKind,
            Record = record
        }, JsonOptions);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    /// <inheritdoc />
    public void AppendSupplement(string dataDirectory, string recordId, RecordSupplement supplement)
    {
        var path = GetPath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(new LedgerLine
        {
            Kind = LedgerLine.SupplementKind,
            RecordId = recordId,
            Supplement = supplement
        }, JsonOptions);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    /// <summary>台账文件路径（data\ledger.db）。</summary>
    /// <param name="dataDirectory">数据目录。</param>
    /// <returns>台账文件绝对路径。</returns>
    private static string GetPath(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>
    /// NDJSON 的一行：或是一条处理记录（Kind=record），或是一条补充说明（Kind=supplement）。
    /// </summary>
    private sealed class LedgerLine
    {
        /// <summary>记录行的类型标识。</summary>
        public const string RecordKind = "record";

        /// <summary>补充说明行的类型标识。</summary>
        public const string SupplementKind = "supplement";

        /// <summary>行类型（record / supplement）。</summary>
        public string Kind { get; set; } = RecordKind;

        /// <summary>处理记录；仅记录行有值。</summary>
        public HandlingRecord? Record { get; set; }

        /// <summary>补充说明所属记录标识；仅补充说明行有值。</summary>
        public string? RecordId { get; set; }

        /// <summary>补充说明；仅补充说明行有值。</summary>
        public RecordSupplement? Supplement { get; set; }
    }
}