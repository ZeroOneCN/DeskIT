using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 介质头信息与状态存储（M8）。该文件（数据目录的 operators.json）为<b>明文</b>介质头文件，
/// 只承载识别介质所需的非敏感信息（介质标识、KDF 参数、密码校验值）与介质级设置（自动锁定、脱敏开关、
/// 高级模式开关、处理人登记、失败计数与冷却），<b>绝不保存主密码或任何凭证明文</b>。
/// 跨机跨启动有效；旧的仅含 advancedMode / currentOperator / operators 的文件可正常反序列化。
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

    /// <summary>
    /// 读取介质级设置（自动锁定分钟数、界面脱敏开关）；未写入时返回默认值。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>介质级设置。</returns>
    MediaSettings GetSettings(string dataDirectory);

    /// <summary>
    /// 写入介质级设置并立即落盘。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="settings">介质级设置。</param>
    void SetSettings(string dataDirectory, MediaSettings settings);

    /// <summary>
    /// 读取介质头信息（介质标识、KDF 参数、密码校验值）；未初始化时返回 null。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>介质头信息；未初始化时返回 null。</returns>
    MediaDescriptor? GetDescriptor(string dataDirectory);

    /// <summary>
    /// 保存介质头信息并立即落盘（初始化或迁移时调用）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="descriptor">介质头信息。</param>
    void SaveDescriptor(string dataDirectory, MediaDescriptor descriptor);

    /// <summary>
    /// 读取已落盘的连续解锁失败次数（解锁节流用）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>连续失败次数；未记录时为 0。</returns>
    int GetUnlockFailures(string dataDirectory);

    /// <summary>
    /// 写入连续解锁失败次数并立即落盘（非敏感字段）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="failures">连续失败次数。</param>
    void SetUnlockFailures(string dataDirectory, int failures);

    /// <summary>
    /// 读取已落盘的冷却截止时间。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>冷却截止时间；无冷却时为 null。</returns>
    DateTimeOffset? GetCooldownUntil(string dataDirectory);

    /// <summary>
    /// 写入冷却截止时间并立即落盘（非敏感字段）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="cooldownUntil">冷却截止时间；无冷却时传 null。</param>
    void SetCooldownUntil(string dataDirectory, DateTimeOffset? cooldownUntil);
}

/// <summary>
/// operators.json 的实现。文件以明文保存介质头信息与介质级设置，作为 M8 的介质头文件。
/// </summary>
public sealed class MediaStateStore : IMediaStateStore
{
    private const string FileName = "operators.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
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

    /// <inheritdoc />
    public MediaSettings GetSettings(string dataDirectory)
    {
        var settings = ReadDocument(dataDirectory).Settings;
        return settings ?? new MediaSettings();
    }

    /// <inheritdoc />
    public void SetSettings(string dataDirectory, MediaSettings settings)
    {
        var document = ReadDocument(dataDirectory);
        document.Settings = settings;
        WriteDocument(dataDirectory, document);
    }

    /// <inheritdoc />
    public MediaDescriptor? GetDescriptor(string dataDirectory)
    {
        var descriptor = ReadDocument(dataDirectory).Media;

        // 缺少关键字段视为未初始化，避免以不完整头信息进入解锁流程。
        if (descriptor is null
            || string.IsNullOrWhiteSpace(descriptor.SaltBase64)
            || string.IsNullOrWhiteSpace(descriptor.VerifierBase64)
            || descriptor.Iterations <= 0)
        {
            return null;
        }

        return descriptor;
    }

    /// <inheritdoc />
    public void SaveDescriptor(string dataDirectory, MediaDescriptor descriptor)
    {
        var document = ReadDocument(dataDirectory);
        document.Media = descriptor;
        WriteDocument(dataDirectory, document);
    }

    /// <inheritdoc />
    public int GetUnlockFailures(string dataDirectory) => ReadDocument(dataDirectory).UnlockFailures;

    /// <inheritdoc />
    public void SetUnlockFailures(string dataDirectory, int failures)
    {
        var document = ReadDocument(dataDirectory);
        document.UnlockFailures = failures < 0 ? 0 : failures;
        WriteDocument(dataDirectory, document);
    }

    /// <inheritdoc />
    public DateTimeOffset? GetCooldownUntil(string dataDirectory) => ReadDocument(dataDirectory).CooldownUntil;

    /// <inheritdoc />
    public void SetCooldownUntil(string dataDirectory, DateTimeOffset? cooldownUntil)
    {
        var document = ReadDocument(dataDirectory);
        document.CooldownUntil = cooldownUntil;
        WriteDocument(dataDirectory, document);
    }

    /// <summary>读取 operators.json；文件缺失或损坏时返回默认文档。</summary>
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

    /// <summary>
    /// operators.json 的文档结构：介质头信息（media）、介质级设置（settings）、
    /// 高级模式开关、处理人登记与解锁节流（非敏感）。
    /// </summary>
    private sealed class MediaStateDocument
    {
        /// <summary>介质头信息；未初始化时为 null。</summary>
        public MediaDescriptor? Media { get; set; }

        /// <summary>介质级设置（自动锁定分钟数、界面脱敏开关）。</summary>
        public MediaSettings? Settings { get; set; }

        /// <summary>高级模式开关状态，默认关闭。</summary>
        public bool AdvancedMode { get; set; }

        /// <summary>当前处理人（工号 + 姓名）；未登记时为 null。</summary>
        public OperatorInfo? CurrentOperator { get; set; }

        /// <summary>处理人（工号 + 姓名）登记清单。旧文件中为空数组，可正常反序列化。</summary>
        public List<OperatorInfo> Operators { get; set; } = new();

        /// <summary>连续解锁失败次数（非敏感，用于重启后仍生效的失败延迟）。</summary>
        public int UnlockFailures { get; set; }

        /// <summary>冷却截止时间（非敏感）。</summary>
        public DateTimeOffset? CooldownUntil { get; set; }
    }
}