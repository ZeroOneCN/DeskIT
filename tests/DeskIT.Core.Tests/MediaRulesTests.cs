using DeskIT.Core;
using Xunit;

namespace DeskIT.Core.Tests;

/// <summary>M8 介质安全纯规则（MediaRules 与 UnlockThrottle）的单元测试。</summary>
public class MediaRulesTests
{
    /// <summary>介质标识：默认常量应满足产品约定（自动锁定 10 分钟、最少 8 位、保留 365 天、失败阈值 5、最大冷却 120 秒）。</summary>
    [Fact]
    public void 常量_符合产品约定()
    {
        Assert.Equal(10, MediaRules.DefaultAutoLockMinutes);
        Assert.Equal(8, MediaRules.MinimumPasswordLength);
        Assert.Equal(365, MediaRules.DefaultAuditRetainDays);
        Assert.Equal(5, MediaRules.UnlockFailureThreshold);
        Assert.Equal(120, MediaRules.MaxUnlockCooldownSeconds);
    }

    /// <summary>DeriveMediaState：未初始化优先于其它条件。</summary>
    [Fact]
    public void DeriveMediaState_未初始化优先()
    {
        Assert.Equal(MediaState.NotInitialized, MediaRules.DeriveMediaState(false, false, true, true));
        Assert.Equal(MediaState.NotInitialized, MediaRules.DeriveMediaState(false, true, false, false));
    }

    /// <summary>DeriveMediaState：已初始化但完整性未通过时为“校验失败”，优先于只读与锁定。</summary>
    [Fact]
    public void DeriveMediaState_完整性失败优先于只读与锁定()
    {
        Assert.Equal(MediaState.IntegrityFailed, MediaRules.DeriveMediaState(true, true, true, false));
        Assert.Equal(MediaState.IntegrityFailed, MediaRules.DeriveMediaState(true, false, false, false));
    }

    /// <summary>DeriveMediaState：完整性通过但不可写时为“只读或不可写”，优先于锁定/解锁。</summary>
    [Fact]
    public void DeriveMediaState_不可写优先于锁定与解锁()
    {
        Assert.Equal(MediaState.ReadOnlyOrUnwritable, MediaRules.DeriveMediaState(true, false, false, true));
        Assert.Equal(MediaState.ReadOnlyOrUnwritable, MediaRules.DeriveMediaState(true, true, false, true));
    }

    /// <summary>DeriveMediaState：可写且完整性通过时，按解锁与否得到“已解锁”或“已锁定”。</summary>
    [Fact]
    public void DeriveMediaState_可写时按解锁状态区分()
    {
        Assert.Equal(MediaState.Unlocked, MediaRules.DeriveMediaState(true, true, true, true));
        Assert.Equal(MediaState.Locked, MediaRules.DeriveMediaState(true, false, true, true));
    }

    /// <summary>IsWriteAllowed：仅在“已解锁”时允许写入。</summary>
    [Fact]
    public void IsWriteAllowed_仅在已解锁时为真()
    {
        Assert.True(MediaRules.IsWriteAllowed(MediaState.Unlocked));
        Assert.False(MediaRules.IsWriteAllowed(MediaState.Locked));
        Assert.False(MediaRules.IsWriteAllowed(MediaState.NotInitialized));
        Assert.False(MediaRules.IsWriteAllowed(MediaState.ReadOnlyOrUnwritable));
        Assert.False(MediaRules.IsWriteAllowed(MediaState.IntegrityFailed));
    }

    /// <summary>ValidateMasterPassword：空、过短、含空白均不通过；恰好 8 位且无空白通过。</summary>
    [Fact]
    public void ValidateMasterPassword_边界()
    {
        Assert.False(MediaRules.ValidateMasterPassword(null).IsValid);
        Assert.False(MediaRules.ValidateMasterPassword(string.Empty).IsValid);
        Assert.False(MediaRules.ValidateMasterPassword("1234567").IsValid);
        Assert.False(MediaRules.ValidateMasterPassword("1234 567").IsValid);
        Assert.False(MediaRules.ValidateMasterPassword("1234\n567").IsValid);

        Assert.True(MediaRules.ValidateMasterPassword("12345678").IsValid);
        Assert.True(MediaRules.ValidateMasterPassword("DeskIT@2026").IsValid);
    }

    /// <summary>ValidateMasterPassword：不通过时给出非空中文说明。</summary>
    [Fact]
    public void ValidateMasterPassword_失败时给出说明()
    {
        Assert.False(string.IsNullOrWhiteSpace(MediaRules.ValidateMasterPassword("abc").Message));
        Assert.True(string.IsNullOrEmpty(MediaRules.ValidateMasterPassword("abcdefgh").Message));
    }

    /// <summary>EvaluateUnlockCooldown：0~3 次不等待；第 5/6/7/20 次分别为 30/60/120/120 秒。</summary>
    [Fact]
    public void EvaluateUnlockCooldown_按失败次数递增()
    {
        Assert.Equal(TimeSpan.Zero, MediaRules.EvaluateUnlockCooldown(0));
        Assert.Equal(TimeSpan.Zero, MediaRules.EvaluateUnlockCooldown(3));
        Assert.Equal(TimeSpan.Zero, MediaRules.EvaluateUnlockCooldown(4));
        Assert.Equal(TimeSpan.FromSeconds(30), MediaRules.EvaluateUnlockCooldown(5));
        Assert.Equal(TimeSpan.FromSeconds(60), MediaRules.EvaluateUnlockCooldown(6));
        Assert.Equal(TimeSpan.FromSeconds(120), MediaRules.EvaluateUnlockCooldown(7));
        Assert.Equal(TimeSpan.FromSeconds(120), MediaRules.EvaluateUnlockCooldown(20));
    }

    /// <summary>DescribeUnlockCooldown：未达阈值与已达阈值都给出非空中文提示，达阈值时含等待秒数。</summary>
    [Fact]
    public void DescribeUnlockCooldown_文案非空()
    {
        Assert.False(string.IsNullOrWhiteSpace(MediaRules.DescribeUnlockCooldown(1)));
        Assert.Contains("30", MediaRules.DescribeUnlockCooldown(5));
    }

    /// <summary>ShouldAutoLock：无活动不锁定、未超时不锁定、达到阈值锁定、阈值 0 不锁定。</summary>
    [Fact]
    public void ShouldAutoLock_边界()
    {
        var now = DateTimeOffset.Parse("2026-10-12T20:00:00+08:00");

        Assert.False(MediaRules.ShouldAutoLock(null, now));
        Assert.False(MediaRules.ShouldAutoLock(now.AddMinutes(-9), now));
        Assert.True(MediaRules.ShouldAutoLock(now.AddMinutes(-10), now));
        Assert.True(MediaRules.ShouldAutoLock(now.AddMinutes(-30), now));
        Assert.False(MediaRules.ShouldAutoLock(now.AddMinutes(-30), now, 0));
    }

    /// <summary>ShouldBlockSafeEject：非终态会话阻止移除，终态会话允许移除。</summary>
    [Fact]
    public void ShouldBlockSafeEject_按会话状态判定()
    {
        Assert.True(MediaRules.ShouldBlockSafeEject(SessionState.New));
        Assert.True(MediaRules.ShouldBlockSafeEject(SessionState.Collecting));
        Assert.True(MediaRules.ShouldBlockSafeEject(SessionState.Acting));

        Assert.False(MediaRules.ShouldBlockSafeEject(SessionState.Closed));
        Assert.False(MediaRules.ShouldBlockSafeEject(SessionState.Resolved));
        Assert.False(MediaRules.ShouldBlockSafeEject(SessionState.Interrupted));
    }

    /// <summary>SelectAuditEventsToTrim：保留期内的保留、早于 365 天的清理，恰好 365 天按边界保留。</summary>
    [Fact]
    public void SelectAuditEventsToTrim_365天边界()
    {
        var now = DateTimeOffset.Parse("2026-10-12T20:00:00+08:00");
        var events = new[]
        {
            NewEvent(now, "kept-now"),
            NewEvent(now.AddDays(-100), "kept-100"),
            NewEvent(now.AddDays(-365), "boundary-365"),
            NewEvent(now.AddDays(-366), "trimmed-366")
        };

        var trimmed = MediaRules.SelectAuditEventsToTrim(events, now);

        Assert.Single(trimmed);
        Assert.Equal("trimmed-366", trimmed[0].EventId);
    }

    /// <summary>SelectAuditEventsToTrim：自定义保留天数生效。</summary>
    [Fact]
    public void SelectAuditEventsToTrim_自定义保留天数()
    {
        var now = DateTimeOffset.Parse("2026-10-12T20:00:00+08:00");
        var events = new[] { NewEvent(now.AddDays(-31), "old") };

        Assert.Empty(MediaRules.SelectAuditEventsToTrim(events, now, retainDays: 90));
        Assert.Single(MediaRules.SelectAuditEventsToTrim(events, now, retainDays: 30));
    }

    /// <summary>IsEnvelope：合法四段式信封为真；明文、空值与段数不符为假。</summary>
    [Fact]
    public void IsEnvelope_判定()
    {
        Assert.True(MediaRules.IsEnvelope("DESKIT1|nonce|tag|cipher"));
        Assert.False(MediaRules.IsEnvelope("DESKIT1|nonce|tag"));
        Assert.False(MediaRules.IsEnvelope("DESKIT2|nonce|tag|cipher"));
        Assert.False(MediaRules.IsEnvelope("{\"plain\":true}"));
        Assert.False(MediaRules.IsEnvelope(string.Empty));
        Assert.False(MediaRules.IsEnvelope(null));
    }

    /// <summary>Describe：五态均给出非空中文名称。</summary>
    [Fact]
    public void Describe_五态文案非空()
    {
        foreach (var state in Enum.GetValues<MediaState>())
        {
            Assert.False(string.IsNullOrWhiteSpace(MediaRules.Describe(state)));
        }
    }

    /// <summary>DescribeAuditKind：全部事件类型均给出非空中文名称。</summary>
    [Fact]
    public void DescribeAuditKind_全部文案非空()
    {
        foreach (var kind in Enum.GetValues<AuditEventKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(MediaRules.DescribeAuditKind(kind)));
        }
    }

    /// <summary>DescribeForgetWarning：必须明示“忘记主密码无法恢复”。</summary>
    [Fact]
    public void DescribeForgetWarning_含关键提示()
    {
        var text = MediaRules.DescribeForgetWarning();
        Assert.Contains("忘记主密码", text);
        Assert.Contains("无法恢复", text);
    }

    /// <summary>DescribeSessionInterruption：含“已中断”与保留内容提示。</summary>
    [Fact]
    public void DescribeSessionInterruption_含中断提示()
    {
        var text = MediaRules.DescribeSessionInterruption(SessionState.Diagnosing);
        Assert.Contains("已中断", text);
        Assert.Contains("诊断中", text);
    }

    /// <summary>UnlockThrottle：失败累加、冷却截止时间与成功清零。</summary>
    [Fact]
    public void UnlockThrottle_失败累加与清零()
    {
        var throttle = new UnlockThrottle();
        var now = DateTimeOffset.Parse("2026-10-12T20:00:00+08:00");

        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal(TimeSpan.Zero, throttle.RegisterFailure(now));
        }

        Assert.Equal(4, throttle.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(30), throttle.RegisterFailure(now));
        Assert.Equal(5, throttle.ConsecutiveFailures);
        Assert.Equal(now.AddSeconds(30), throttle.CooldownUntil);

        throttle.Reset();
        Assert.Equal(0, throttle.ConsecutiveFailures);
        Assert.Null(throttle.CooldownUntil);
    }

    /// <summary>UnlockThrottle：用已落盘状态恢复（负值按 0 处理）。</summary>
    [Fact]
    public void UnlockThrottle_恢复落盘状态()
    {
        var throttle = new UnlockThrottle();
        var until = DateTimeOffset.Parse("2026-10-12T20:01:00+08:00");

        throttle.Restore(6, until);
        Assert.Equal(6, throttle.ConsecutiveFailures);
        Assert.Equal(until, throttle.CooldownUntil);

        throttle.Restore(-3, null);
        Assert.Equal(0, throttle.ConsecutiveFailures);
        Assert.Null(throttle.CooldownUntil);
    }

    /// <summary>NewAuditEventId：形如 aud-yyyyMMdd-HHmmss-xxxx，含指定后缀。</summary>
    [Fact]
    public void NewAuditEventId_格式()
    {
        var at = DateTimeOffset.Parse("2026-10-12T19:30:00+08:00");
        Assert.Equal("aud-20261012-193000-ab12", MediaRules.NewAuditEventId(at, "ab12"));
    }

    /// <summary>构造一条测试用审计事件。</summary>
    /// <param name="at">事件时间。</param>
    /// <param name="eventId">事件标识。</param>
    /// <returns>审计事件。</returns>
    private static AuditEvent NewEvent(DateTimeOffset at, string eventId) => new()
    {
        EventId = eventId,
        Kind = AuditEventKind.UnlockSucceeded,
        At = at,
        ComputerName = "TEST-PC",
        Summary = "测试事件"
    };
}