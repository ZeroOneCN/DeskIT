using System.Text;

namespace DeskIT.Core;

/// <summary>
/// M9 受控自定义命令与动作沉淀的可单元测试纯规则。
/// 本类不依赖任何 Windows API：涵盖入口可用性判定、命令与输出脱敏截断、结果归类、命令标识生成、
/// 持久化变更差异对比、沉淀请求校验、动作定义构建与沉淀脚本、沉淀文件名生成与来源说明。
/// </summary>
public static class CustomCommandRules
{
    /// <summary>沉淀动作的来源标记（写入动作定义的 <see cref="ActionDescriptor.Source"/>）。</summary>
    public const string SedimentSource = "自定义沉淀";

    /// <summary>自定义命令单项执行的默认超时秒数（10 分钟，与 M4 单项动作一致）。</summary>
    public const int DefaultTimeoutSeconds = ActionRules.DefaultTimeoutSeconds;

    /// <summary>输出摘要保留的最大行数。</summary>
    private const int OutputSummaryMaxLines = 40;

    /// <summary>输出摘要保留的最大字符数。</summary>
    private const int OutputSummaryMaxChars = 4000;

    /// <summary>
    /// 判定受控自定义命令入口是否可用，并给出不可用的中文原因。
    /// 判定顺序：管理员模式 → 启动自检 → 介质已解锁且可写 → 高级模式已开启。
    /// </summary>
    /// <param name="isAdministrator">当前是否为管理员模式。</param>
    /// <param name="advancedModeEnabled">高级模式是否已开启（介质级记住）。</param>
    /// <param name="mediaUnlocked">介质是否已解锁。</param>
    /// <param name="writable">介质是否可写（已解锁且非只读 / 未拔出）。</param>
    /// <param name="canStart">启动自检是否通过且允许开始新会话。</param>
    /// <returns>可用性判定结果。</returns>
    public static CustomCommandAvailability EvaluateAvailability(
        bool isAdministrator,
        bool advancedModeEnabled,
        bool mediaUnlocked,
        bool writable,
        bool canStart)
    {
        if (!isAdministrator)
        {
            return new CustomCommandAvailability(false, "仅管理员模式可用：请以管理员身份重新启动 DeskIT。");
        }

        if (!canStart)
        {
            return new CustomCommandAvailability(false, "启动自检未通过或介质可用空间不足：已禁用自定义命令入口。");
        }

        if (!mediaUnlocked)
        {
            return new CustomCommandAvailability(false, "介质尚未解锁：请先输入主密码解锁后再使用自定义命令。");
        }

        if (!writable)
        {
            return new CustomCommandAvailability(false, "介质不可写（写保护或已拔出）：禁止执行自定义命令（命令必须强制记录）。");
        }

        if (!advancedModeEnabled)
        {
            return new CustomCommandAvailability(false, "高级模式未开启：请在“介质与设置（M8）”开启高级模式（需主密码 + 二次确认）。");
        }

        return new CustomCommandAvailability(true, null);
    }

    /// <summary>
    /// 对命令原文做凭据脱敏（凭据类参数值替换为掩码），并去除首尾空白。
    /// 命令原文与输出一律先脱敏再落盘，保证凭证明文绝不写入目标机或介质。
    /// </summary>
    /// <param name="command">命令原文；可能为 null。</param>
    /// <returns>脱敏后的命令文本。</returns>
    public static string MaskCommand(string? command)
        => LedgerRules.MaskCredentials(command).Trim();

    /// <summary>
    /// 把命令输出整理为可落盘的摘要：统一换行、按行数与字符数上限截断，并在截断时追加说明。
    /// </summary>
    /// <param name="text">原始输出文本；可能为 null。</param>
    /// <returns>摘要文本；无输出时为空串。</returns>
    public static string SummarizeOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var truncated = lines.Length > OutputSummaryMaxLines;
        var kept = truncated ? lines.Take(OutputSummaryMaxLines).ToArray() : lines;
        var summary = string.Join("\n", kept).Trim();

        if (summary.Length > OutputSummaryMaxChars)
        {
            summary = summary[..OutputSummaryMaxChars];
            truncated = true;
        }

        return truncated ? summary + $"\n……（输出已截断，共 {lines.Length} 行）" : summary;
    }

    /// <summary>
    /// 结果归类：取消优先于超时，超时优先于退出码；退出码非零即失败，否则成功。
    /// 语义与 M4 执行器一致（自定义命令不设“需重启”语义）。
    /// </summary>
    /// <param name="canceled">是否被主动取消。</param>
    /// <param name="timedOut">是否超时被终止。</param>
    /// <param name="exitCode">脚本退出码。</param>
    /// <returns>结果归类。</returns>
    public static ActionOutcome ClassifyOutcome(bool canceled, bool timedOut, int exitCode)
        => ActionRules.ClassifyOutcome(canceled, timedOut, exitCode, requiresRestart: false);

    /// <summary>
    /// 生成一条命令标识：形如 cmd-20261012-193000-ab12，含随机后缀保证同一秒内唯一。
    /// </summary>
    /// <param name="executedAt">执行时间。</param>
    /// <param name="suffix">随机后缀（4 位十六进制）；未提供时自动生成。</param>
    /// <returns>命令标识。</returns>
    public static string BuildCommandId(DateTimeOffset executedAt, string? suffix = null)
    {
        var tail = suffix ?? Guid.NewGuid().ToString("N")[..4];
        return $"cmd-{executedAt:yyyyMMdd-HHmmss}-{tail}";
    }

    /// <summary>执行身份的中文名称（管理员 / 标准用户）。</summary>
    /// <param name="privilege">权限级别。</param>
    /// <returns>中文名称。</returns>
    public static string DescribeExecutionAs(PrivilegeLevel privilege)
        => privilege == PrivilegeLevel.Administrator ? "管理员" : "标准用户";

    /// <summary>动作来源的中文说明（内置动作库 / 自定义沉淀）。</summary>
    /// <param name="source">来源标记；为空表示内置动作库。</param>
    /// <returns>来源说明。</returns>
    public static string DescribeOrigin(string? source)
        => string.IsNullOrWhiteSpace(source) ? "内置动作库" : source!;

    /// <summary>持久化变更类型的中文名称。</summary>
    /// <param name="kind">持久化变更类型。</param>
    /// <returns>中文名称。</returns>
    public static string DescribePersistenceKind(PersistenceChangeKind kind) => kind switch
    {
        PersistenceChangeKind.Service => "系统服务",
        PersistenceChangeKind.ScheduledTask => "计划任务",
        _ => "开机自启动项"
    };

    /// <summary>
    /// 对比同一类持久化项在执行前 / 后的快照，得出变更明细。
    /// 任一侧采集失败时返回“未知”（<see cref="PersistenceChange.IsUnknown"/> 为 true），不假装“无变化”。
    /// </summary>
    /// <param name="before">执行前快照。</param>
    /// <param name="after">执行后快照。</param>
    /// <returns>该类持久化项的变更明细（单元素列表）。</returns>
    public static IReadOnlyList<PersistenceChange> DetectPersistenceChanges(
        PersistenceSnapshot before,
        PersistenceSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (!before.IsAvailable || !after.IsAvailable)
        {
            var note = !before.IsAvailable
                ? $"（未知：执行前采集失败{FormatMessage(before.Message)}）"
                : $"（未知：执行后采集失败{FormatMessage(after.Message)}）";
            return new[] { new PersistenceChange(before.Kind, Array.Empty<string>(), Array.Empty<string>(), true, note) };
        }

        var beforeSet = new HashSet<string>(before.Names, StringComparer.OrdinalIgnoreCase);
        var afterSet = new HashSet<string>(after.Names, StringComparer.OrdinalIgnoreCase);

        var added = after.Names
            .Where(name => !beforeSet.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var removed = before.Names
            .Where(name => !afterSet.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        return new[] { new PersistenceChange(before.Kind, added, removed, false, string.Empty) };
    }

    /// <summary>
    /// 校验动作沉淀请求：名称 / 说明 / 源命令必填、风险不得低于“高”、需重启与是否可回滚必须明确、
    /// 可回滚时必须填写回滚说明、源命令不得含 <c>{{ }}</c> 占位符（会被动作库当作参数占位符）。
    /// </summary>
    /// <param name="request">沉淀请求。</param>
    /// <returns>校验结果（是否通过与字段级问题清单）。</returns>
    public static ActionSedimentValidation ValidateSediment(ActionSedimentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            problems.Add("动作名称必填。");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            problems.Add("动作说明必填（用于向 IT 人员明示影响）。");
        }

        if (string.IsNullOrWhiteSpace(request.SourceCommand))
        {
            problems.Add("源命令不能为空。");
        }
        else if (request.SourceCommand.Contains("{{", StringComparison.Ordinal)
                 || request.SourceCommand.Contains("}}", StringComparison.Ordinal))
        {
            problems.Add("源命令包含 {{ }} 占位符：会被动作库误判为参数占位符，请改用不含 {{ }} 的命令。");
        }

        if (!Enum.IsDefined(request.Risk))
        {
            problems.Add("风险级别无效。");
        }
        else if (request.Risk < RiskLevel.High)
        {
            problems.Add("沉淀动作的风险级别不得低于“高”（自定义命令无法静态评估影响，一律按高风险对待）。");
        }

        if (request.RequiresRestart is null)
        {
            problems.Add("请选择“是否需重启生效”。");
        }

        if (request.CanRollback is null)
        {
            problems.Add("请选择“是否可回滚”。");
        }

        if (request.CanRollback == true && string.IsNullOrWhiteSpace(request.RollbackNote))
        {
            problems.Add("选择“可回滚”时必须填写回滚说明。");
        }

        if (request.TimeoutSeconds <= 0)
        {
            problems.Add("超时必须为正数秒。");
        }

        return new ActionSedimentValidation(problems.Count == 0, problems);
    }

    /// <summary>
    /// 依据沉淀请求与序号构建正式动作定义：标识形如 <c>custom.001</c>，风险不低于“高”，
    /// 脚本为源命令包装后的带退出码约定脚本，来源标记为“自定义沉淀”。
    /// </summary>
    /// <param name="request">沉淀请求。</param>
    /// <param name="sequence">动作序号（用于生成唯一标识与文件名）。</param>
    /// <returns>动作定义。</returns>
    public static ActionDescriptor BuildActionDescriptor(ActionSedimentRequest request, int sequence)
    {
        ArgumentNullException.ThrowIfNull(request);

        var risk = request.Risk < RiskLevel.High ? RiskLevel.High : request.Risk;
        var canRollback = request.CanRollback == true;
        var rollbackNote = canRollback
            ? request.RollbackNote.Trim()
            : string.IsNullOrWhiteSpace(request.RollbackNote)
                ? "自定义沉淀动作不可回滚；如需回退请按该命令自身语义处理。"
                : request.RollbackNote.Trim();

        return new ActionDescriptor
        {
            ActionId = $"custom.{sequence:000}",
            Name = request.Name.Trim(),
            Risk = risk,
            Scene = request.Scene,
            Summary = request.Description.Trim(),
            RequiresRestart = request.RequiresRestart == true,
            CanRollback = canRollback,
            RollbackNote = rollbackNote,
            RequiresAdministrator = true,
            TimeoutSeconds = request.TimeoutSeconds,
            MayInterruptSession = false,
            Script = BuildSedimentScript(request.SourceCommand),
            Parameters = request.Parameters,
            Source = SedimentSource
        };
    }

    /// <summary>
    /// 把源命令包装为带统一退出码约定的脚本：脚本未显式退出时，最后一个外部命令的非零退出码视为失败；
    /// 终止性错误一律记为失败（退出码 1）；其余记为成功（退出码 0）。
    /// 该脚本既用于沉淀后的动作，也用于本次受控执行，保证二者退出码语义完全一致。
    /// </summary>
    /// <param name="commandText">源命令文本。</param>
    /// <returns>包装后的脚本文本。</returns>
    public static string BuildSedimentScript(string commandText)
        => "try {\n"
           + commandText.Trim() + "\n"
           + "  if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\n"
           + "  exit 0\n"
           + "} catch {\n"
           + "  Write-Error $_.Exception.Message\n"
           + "  exit 1\n"
           + "}\n";

    /// <summary>
    /// 生成沉淀动作的文件名：<c>custom-&lt;slug&gt;.json</c>，其中 slug 由名称生成 ASCII 片段并附加序号，
    /// 保证同一名称也能得到唯一文件名（避免覆盖既有沉淀文件）。
    /// </summary>
    /// <param name="name">动作名称。</param>
    /// <param name="sequence">动作序号。</param>
    /// <returns>文件名（不含目录）。</returns>
    public static string BuildSedimentFileName(string? name, int sequence)
    {
        var slug = Slugify(name);
        return $"custom-{slug}-{sequence:000}.json";
    }

    /// <summary>
    /// 把名称安全化为 ASCII 文件名片段：仅保留字母与数字（转小写），其余字符折叠为单个连字符。
    /// 名称不含 ASCII 字母数字时返回空串（由调用方以“action”回退）。
    /// </summary>
    /// <param name="name">原始名称。</param>
    /// <returns>ASCII 安全片段；无可用字符时为空串。</returns>
    private static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "action";
        }

        var builder = new StringBuilder(name.Length);
        var lastHyphen = false;
        foreach (var ch in name)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastHyphen = false;
            }
            else if (builder.Length > 0 && !lastHyphen)
            {
                builder.Append('-');
                lastHyphen = true;
            }
        }

        var result = builder.ToString().Trim('-');
        return result.Length == 0 ? "action" : result;
    }

    /// <summary>把采集失败原因格式化为补充说明片段（空原因时不追加内容）。</summary>
    /// <param name="message">采集失败原因。</param>
    /// <returns>形如“：原因”的片段，或空串。</returns>
    private static string FormatMessage(string? message)
        => string.IsNullOrWhiteSpace(message) ? string.Empty : "：" + message.Trim();
}