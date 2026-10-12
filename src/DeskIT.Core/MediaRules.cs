namespace DeskIT.Core;

/// <summary>
/// M8 介质安全与数据保护的可单元测试纯规则：状态推导与说明、主密码校验、写入门禁、
/// 失败冷却、自动锁定、安全移除门禁、会话中断说明与审计事件保留清理、加密信封判定。
/// 本类不依赖任何 Windows API，具体探测与加解密由 DeskIT.Windows 提供。
/// </summary>
public static class MediaRules
{
    /// <summary>无操作自动锁定的默认分钟数。</summary>
    public const int DefaultAutoLockMinutes = 10;

    /// <summary>主密码的最少位数。</summary>
    public const int MinimumPasswordLength = 8;

    /// <summary>审计事件的默认保留天数（PRD 7.5：365 天）。</summary>
    public const int DefaultAuditRetainDays = 365;

    /// <summary>触发强制等待的连续失败次数阈值。</summary>
    public const int UnlockFailureThreshold = 5;

    /// <summary>连续失败后的最大强制等待秒数。</summary>
    public const int MaxUnlockCooldownSeconds = 120;

    /// <summary>加密信封的版本前缀（DESKIT1|nonce|tag|ciphertext）。</summary>
    public const string EnvelopePrefix = "DESKIT1|";

    /// <summary>生成一条审计事件标识：形如 aud-20261012-193000-ab12，含随机后缀保证同一秒内唯一。</summary>
    /// <param name="createdAt">事件创建时间。</param>
    /// <param name="suffix">随机后缀（4 位十六进制）；未提供时自动生成。</param>
    /// <returns>审计事件标识。</returns>
    public static string NewAuditEventId(DateTimeOffset createdAt, string? suffix = null)
    {
        var tail = suffix ?? Guid.NewGuid().ToString("N")[..4];
        return $"aud-{createdAt:yyyyMMdd-HHmmss}-{tail}";
    }

    /// <summary>介质状态的中文名称。</summary>
    /// <param name="state">介质状态。</param>
    /// <returns>中文名称。</returns>
    public static string Describe(MediaState state) => state switch
    {
        MediaState.NotInitialized => "未初始化",
        MediaState.Locked => "已锁定",
        MediaState.Unlocked => "已解锁",
        MediaState.ReadOnlyOrUnwritable => "只读或不可写",
        _ => "校验失败"
    };

    /// <summary>审计事件类型的中文名称。</summary>
    /// <param name="kind">事件类型。</param>
    /// <returns>中文名称。</returns>
    public static string DescribeAuditKind(AuditEventKind kind) => kind switch
    {
        AuditEventKind.MediaInitialized => "介质初始化",
        AuditEventKind.UnlockSucceeded => "解锁成功",
        AuditEventKind.UnlockFailed => "解锁失败",
        AuditEventKind.Export => "导出",
        AuditEventKind.ActionExecuted => "动作执行",
        AuditEventKind.CustomCommandExecuted => "自定义命令执行",
        AuditEventKind.SessionInterruptedOrRemoved => "异常移除 / 会话中断",
        AuditEventKind.AdvancedModeChanged => "高级模式开关变化",
        AuditEventKind.OperatorSwitched => "处理人切换",
        AuditEventKind.ReportProduced => "报告产出",
        AuditEventKind.TempCleanup => "临时文件清理",
        _ => "介质迁移"
    };

    /// <summary>
    /// 由“是否已初始化、是否已解锁、介质是否可写、完整性是否通过”推导介质状态。
    /// 优先级：未初始化 → 校验失败 → 只读或不可写 → 已锁定 → 已解锁。
    /// </summary>
    /// <param name="initialized">是否已完成初始化。</param>
    /// <param name="unlocked">是否已解锁。</param>
    /// <param name="writable">介质当前是否可写。</param>
    /// <param name="integrityOk">台账数据完整性是否通过。</param>
    /// <returns>介质状态。</returns>
    public static MediaState DeriveMediaState(bool initialized, bool unlocked, bool writable, bool integrityOk)
    {
        if (!initialized)
        {
            return MediaState.NotInitialized;
        }

        if (!integrityOk)
        {
            return MediaState.IntegrityFailed;
        }

        if (!writable)
        {
            return MediaState.ReadOnlyOrUnwritable;
        }

        return unlocked ? MediaState.Unlocked : MediaState.Locked;
    }

    /// <summary>
    /// 校验主密码是否符合规则：非空、去掉首尾空白后不少于 8 位、且不含空白字符。
    /// </summary>
    /// <param name="password">待校验的主密码。</param>
    /// <returns>校验结果（不符合时给出中文错误说明）。</returns>
    public static MasterPasswordValidation ValidateMasterPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return new MasterPasswordValidation(false, "主密码不能为空。");
        }

        if (password.Length < MinimumPasswordLength)
        {
            return new MasterPasswordValidation(false, $"主密码至少需要 {MinimumPasswordLength} 位。");
        }

        if (password.Any(char.IsWhiteSpace))
        {
            return new MasterPasswordValidation(false, "主密码不能包含空格或换行等空白字符。");
        }

        return new MasterPasswordValidation(true, string.Empty);
    }

    /// <summary>
    /// 判断在给定介质状态下是否允许写入加密数据。
    /// </summary>
    /// <param name="state">介质状态。</param>
    /// <returns>仅在“已解锁”时允许写入。</returns>
    public static bool IsWriteAllowed(MediaState state) => state == MediaState.Unlocked;

    /// <summary>
    /// 忘记主密码的警示文案（初始化界面必须明示）。
    /// </summary>
    /// <returns>明确“忘记主密码无法恢复数据”的中文警示。</returns>
    public static string DescribeForgetWarning()
        => "请务必牢记主密码：主密码只存在于内存，不写入介质也不写入日志。一旦忘记主密码，将无法解密台账、快照、会话与审计数据，且无法恢复。";

    /// <summary>
    /// 计算连续解锁失败后的强制等待时长。
    /// 连续失败不足 5 次不等待；第 5 / 6 次分别等待 30 / 60 秒；第 7 次起固定等待 120 秒。
    /// </summary>
    /// <param name="consecutiveFailures">当前连续失败次数（负值按 0 处理）。</param>
    /// <returns>需强制等待的时长；无需等待时为 <see cref="TimeSpan.Zero"/>。</returns>
    public static TimeSpan EvaluateUnlockCooldown(int consecutiveFailures)
    {
        if (consecutiveFailures < UnlockFailureThreshold)
        {
            return TimeSpan.Zero;
        }

        var seconds = consecutiveFailures switch
        {
            5 => 30,
            6 => 60,
            _ => MaxUnlockCooldownSeconds
        };

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// 生成连续失败后的中文提示文案（含强制等待时长）。
    /// </summary>
    /// <param name="consecutiveFailures">当前连续失败次数。</param>
    /// <returns>面向 IT 人员的中文提示。</returns>
    public static string DescribeUnlockCooldown(int consecutiveFailures)
    {
        var cooldown = EvaluateUnlockCooldown(consecutiveFailures);
        return cooldown <= TimeSpan.Zero
            ? $"主密码错误（已连续失败 {consecutiveFailures} 次）。"
            : $"主密码错误（已连续失败 {consecutiveFailures} 次），需等待 {cooldown.TotalSeconds:0} 秒后才能再次尝试。";
    }

    /// <summary>
    /// 判断是否应因无操作而自动锁定。
    /// </summary>
    /// <param name="lastActivityAt">最近一次活动时间；为 null 表示尚未记录活动，不触发锁定。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="autoLockMinutes">自动锁定分钟数。</param>
    /// <returns>已超过自动锁定时间返回 true。</returns>
    public static bool ShouldAutoLock(DateTimeOffset? lastActivityAt, DateTimeOffset now, int autoLockMinutes = DefaultAutoLockMinutes)
    {
        if (lastActivityAt is null)
        {
            return false;
        }

        if (autoLockMinutes <= 0)
        {
            return false;
        }

        return now - lastActivityAt.Value >= TimeSpan.FromMinutes(autoLockMinutes);
    }

    /// <summary>
    /// 判断是否应阻止“安全移除介质”：存在未结束的会话时必须先处理会话状态。
    /// </summary>
    /// <param name="state">当前会话状态。</param>
    /// <returns>会话未结束（非终态）返回 true。</returns>
    public static bool ShouldBlockSafeEject(SessionState state) => !SessionStateRules.IsTerminal(state);

    /// <summary>
    /// 生成会话被异常中断时的中文提示文案。
    /// </summary>
    /// <param name="state">中断前一刻的会话状态。</param>
    /// <returns>面向 IT 人员的中文提示。</returns>
    public static string DescribeSessionInterruption(SessionState state)
        => $"检测到介质不可写或已被移除：本次会话（中断前状态：{SessionStateRules.Describe(state)}）已标记为“已中断”，"
           + "已完成的采集、诊断与动作结果已保留；请重新插入并解锁介质后，选择“查看已保留内容”“续做并补充结论”或“标记放弃”。";

    /// <summary>
    /// 从审计事件中挑选出保留期外、应被清理的事件。
    /// </summary>
    /// <param name="events">全部审计事件。</param>
    /// <param name="now">当前时间。</param>
    /// <param name="retainDays">保留天数，默认 365 天。</param>
    /// <returns>应被清理的事件（早于 now - retainDays 的事件）。</returns>
    public static IReadOnlyList<AuditEvent> SelectAuditEventsToTrim(
        IEnumerable<AuditEvent> events,
        DateTimeOffset now,
        int retainDays = DefaultAuditRetainDays)
    {
        ArgumentNullException.ThrowIfNull(events);

        var cutoff = now.AddDays(-retainDays);
        return events.Where(evt => evt.At < cutoff).ToList();
    }

    /// <summary>
    /// 判断一段文本是否为加密信封（<c>DESKIT1|nonce|tag|ciphertext</c> 四段式）。
    /// 用于区分“需要解密的数据”与“步骤 7 遗留的明文数据 / 从备份恢复的明文”。
    /// </summary>
    /// <param name="text">待判定的文本；可能为 null 或空。</param>
    /// <returns>是加密信封返回 true。</returns>
    public static bool IsEnvelope(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = text.Split('|');
        return parts.Length == 4 && parts[0] == "DESKIT1";
    }
}

/// <summary>
/// 连续解锁失败节流器（M8 失败延迟）：在内存中维护连续失败次数并给出强制等待时长。
/// 失败计数与冷却可落盘到 <c>operators.json</c> 的非敏感字段，便于重启后仍生效。
/// </summary>
public sealed class UnlockThrottle
{
    /// <summary>当前连续失败次数。</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>冷却截止时间；无冷却时为 null。</summary>
    public DateTimeOffset? CooldownUntil { get; private set; }

    /// <summary>
    /// 记录一次失败：递增失败计数并计算新的冷却截止时间。
    /// </summary>
    /// <param name="now">失败发生的时间。</param>
    /// <returns>本次失败后的强制等待时长。</returns>
    public TimeSpan RegisterFailure(DateTimeOffset now)
    {
        ConsecutiveFailures++;
        var cooldown = Evaluate(ConsecutiveFailures);
        CooldownUntil = cooldown > TimeSpan.Zero ? now + cooldown : null;
        return cooldown;
    }

    /// <summary>记录一次成功：清零失败计数与冷却。</summary>
    public void Reset()
    {
        ConsecutiveFailures = 0;
        CooldownUntil = null;
    }

    /// <summary>用已落盘的失败计数恢复节流状态（重启后仍生效）。</summary>
    /// <param name="consecutiveFailures">已落盘的连续失败次数。</param>
    /// <param name="cooldownUntil">已落盘的冷却截止时间。</param>
    public void Restore(int consecutiveFailures, DateTimeOffset? cooldownUntil)
    {
        ConsecutiveFailures = consecutiveFailures < 0 ? 0 : consecutiveFailures;
        CooldownUntil = cooldownUntil;
    }

    /// <summary>计算指定连续失败次数对应的强制等待时长。</summary>
    /// <param name="consecutiveFailures">连续失败次数。</param>
    /// <returns>需强制等待的时长。</returns>
    public static TimeSpan Evaluate(int consecutiveFailures) => MediaRules.EvaluateUnlockCooldown(consecutiveFailures);
}