using System.Security.Cryptography;
using System.Text;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>介质未解锁时的异常：需要先解锁才能解密或加密数据。</summary>
public sealed class MediaLockedException : InvalidOperationException
{
    /// <summary>以默认提示构造异常。</summary>
    public MediaLockedException()
        : base("介质尚未解锁：请先输入主密码解锁后再读写加密数据。")
    {
    }
}

/// <summary>加密信封校验失败（数据被篡改或损坏）时的异常。</summary>
public sealed class MediaIntegrityException : Exception
{
    /// <summary>以指定提示构造异常。</summary>
    /// <param name="message">提示文本。</param>
    public MediaIntegrityException(string message)
        : base(message)
    {
    }

    /// <summary>以指定提示与内部异常构造异常。</summary>
    /// <param name="message">提示文本。</param>
    /// <param name="innerException">内部异常。</param>
    public MediaIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 介质加密金库（M8）：以主密码经 KDF 派生密钥，对数据目录内的数据文件做认证加密。
/// <b>密钥只由主密码 + 随机盐派生，不使用任何与机器绑定的保护机制</b>（介质需跨机使用）；
/// 主密码只存在于内存，绝不写入介质或日志。
/// </summary>
public interface IMediaVault
{
    /// <summary>当前是否已解锁。</summary>
    bool IsUnlocked { get; }

    /// <summary>当前连续解锁失败次数。</summary>
    int ConsecutiveFailures { get; }

    /// <summary>当前冷却截止时间；无冷却时为 null。</summary>
    DateTimeOffset? CooldownUntil { get; }

    /// <summary>
    /// 读取介质头信息；未初始化时返回 null。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>介质头信息或 null。</returns>
    MediaDescriptor? ReadDescriptor(string dataDirectory);

    /// <summary>
    /// 初始化介质：设置主密码并登记处理人（工号 + 姓名），生成介质头信息（含盐与密码校验值）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="password">主密码（仅存在于内存）。</param>
    /// <param name="operator">处理人。</param>
    /// <returns>初始化结果；已初始化时拒绝重复初始化。</returns>
    MediaInitializationResult Initialize(string dataDirectory, string password, OperatorInfo @operator);

    /// <summary>
    /// 解锁介质：校验主密码并维护失败计数与冷却。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="password">主密码（仅存在于内存）。</param>
    /// <returns>解锁结果。</returns>
    MediaUnlockResult Unlock(string dataDirectory, string password);

    /// <summary>锁定介质：清除内存中的派生密钥。</summary>
    void Lock();

    /// <summary>
    /// 加密一段文本，返回带版本前缀的加密信封。
    /// </summary>
    /// <param name="plaintext">明文文本。</param>
    /// <returns>加密信封；未解锁时抛出 <see cref="MediaLockedException"/>。</returns>
    string Protect(string plaintext);

    /// <summary>
    /// 解密一个加密信封；非信封内容按原文返回（兼容既有明文数据与从备份恢复的数据）。
    /// </summary>
    /// <param name="envelopeOrPlain">加密信封或明文。</param>
    /// <returns>明文文本。</returns>
    /// <exception cref="MediaLockedException">介质未解锁时抛出。</exception>
    /// <exception cref="MediaIntegrityException">解密或校验失败（数据被篡改或损坏）时抛出。</exception>
    string Unprotect(string envelopeOrPlain);

    /// <summary>
    /// 把一个文件的内容整文件加密（已是信封则原样保留）；文件不存在时不做任何处理。
    /// </summary>
    /// <param name="path">文件绝对路径。</param>
    void ProtectFile(string path);
}

/// <inheritdoc cref="IMediaVault" />
public sealed class MediaVault : IMediaVault
{
    /// <summary>KDF 算法名（写入介质头信息）。</summary>
    public const string KdfAlgorithmName = "PBKDF2-HMAC-SHA256";

    /// <summary>KDF 迭代次数下限（≥ 200000）。</summary>
    public const int DefaultIterations = 200_000;

    /// <summary>盐长度（字节）。</summary>
    private const int SaltSize = 16;

    /// <summary>派生密钥长度（字节，AES-256）。</summary>
    private const int KeySize = 32;

    /// <summary>AES-GCM 随机 nonce 长度（字节）。</summary>
    private const int NonceSize = 12;

    /// <summary>AES-GCM 认证标签长度（字节）。</summary>
    private const int TagSize = 16;

    /// <summary>密码校验值的固定上下文（由派生密钥做 HMAC 得到校验值）。</summary>
    private static readonly byte[] VerifierContext = Encoding.UTF8.GetBytes("DESKIT-VERIFIER-v1");

    /// <summary>信封的附加认证数据（绑定版本前缀）。</summary>
    private static readonly byte[] EnvelopeAad = Encoding.UTF8.GetBytes("DESKIT1");

    private readonly IMediaStateStore _stateStore;
    private readonly Func<DateTimeOffset> _clock;
    private readonly UnlockThrottle _throttle = new();

    private byte[]? _key;
    private bool _throttleRestored;

    /// <summary>
    /// 初始化介质金库。
    /// </summary>
    /// <param name="stateStore">介质头信息与状态存储；为 null 时使用 operators.json 实现。</param>
    /// <param name="clock">时间源，便于测试；为 null 时使用本地当前时间。</param>
    public MediaVault(IMediaStateStore? stateStore = null, Func<DateTimeOffset>? clock = null)
    {
        _stateStore = stateStore ?? new MediaStateStore();
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public bool IsUnlocked => _key is not null;

    /// <inheritdoc />
    public int ConsecutiveFailures => _throttle.ConsecutiveFailures;

    /// <inheritdoc />
    public DateTimeOffset? CooldownUntil => _throttle.CooldownUntil;

    /// <inheritdoc />
    public MediaDescriptor? ReadDescriptor(string dataDirectory) => _stateStore.GetDescriptor(dataDirectory);

    /// <inheritdoc />
    public MediaInitializationResult Initialize(string dataDirectory, string password, OperatorInfo @operator)
    {
        if (_stateStore.GetDescriptor(dataDirectory) is not null)
        {
            return new MediaInitializationResult(false, null, "该介质已完成初始化：如需使用请直接输入主密码解锁。");
        }

        var validation = MediaRules.ValidateMasterPassword(password);
        if (!validation.IsValid)
        {
            return new MediaInitializationResult(false, null, validation.Message);
        }

        if (!LedgerRules.IsCompleteOperator(@operator))
        {
            return new MediaInitializationResult(false, null, "初始化必须登记处理人：工号与姓名两项均需填写。");
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = DeriveKey(password, salt, DefaultIterations);
        var verifier = ComputeVerifier(key);

        var descriptor = new MediaDescriptor(
            MediaMigration.ResolveMediaId(dataDirectory),
            _clock(),
            KdfAlgorithmName,
            DefaultIterations,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(verifier));

        _stateStore.SaveDescriptor(dataDirectory, descriptor);
        _stateStore.SetCurrentOperator(dataDirectory, @operator);
        _stateStore.SetUnlockFailures(dataDirectory, 0);
        _stateStore.SetCooldownUntil(dataDirectory, null);

        _throttle.Reset();
        _throttleRestored = true;
        SetKey(key);

        return new MediaInitializationResult(true, descriptor, "介质初始化完成，已解锁并登记处理人。");
    }

    /// <inheritdoc />
    public MediaUnlockResult Unlock(string dataDirectory, string password)
    {
        var descriptor = _stateStore.GetDescriptor(dataDirectory);
        if (descriptor is null)
        {
            return new MediaUnlockResult(false, MediaState.NotInitialized, "该介质尚未初始化：请先完成初始化并设置主密码。", 0, TimeSpan.Zero);
        }

        EnsureThrottleRestored(dataDirectory);
        var now = _clock();

        // 冷却期间不提供任何绕过方式。
        if (_throttle.CooldownUntil is { } until && until > now)
        {
            var remaining = until - now;
            return new MediaUnlockResult(
                false,
                MediaState.Locked,
                $"连续失败次数过多：请在 {remaining.TotalSeconds:0} 秒后再试（期间不提供绕过方式）。",
                _throttle.ConsecutiveFailures,
                remaining);
        }

        if (string.IsNullOrEmpty(password))
        {
            return RegisterFailureAndBuildResult(dataDirectory, now, "主密码不能为空。");
        }

        var salt = Convert.FromBase64String(descriptor.SaltBase64);
        var key = DeriveKey(password, salt, descriptor.Iterations);
        var verifier = ComputeVerifier(key);
        var expected = Convert.FromBase64String(descriptor.VerifierBase64);

        if (!CryptographicOperations.FixedTimeEquals(verifier, expected))
        {
            zeroKey(key);
            return RegisterFailureAndBuildResult(dataDirectory, now, "主密码错误，无法解锁介质。");
        }

        _throttle.Reset();
        _stateStore.SetUnlockFailures(dataDirectory, 0);
        _stateStore.SetCooldownUntil(dataDirectory, null);
        SetKey(key);

        return new MediaUnlockResult(true, MediaState.Unlocked, "解锁成功。", 0, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public void Lock() => SetKey(null);

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        if (_key is null)
        {
            throw new MediaLockedException();
        }

        var payload = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[payload.Length];

        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(nonce, payload, ciphertext, tag, EnvelopeAad);

        return string.Concat(
            MediaRules.EnvelopePrefix,
            Convert.ToBase64String(nonce),
            "|",
            Convert.ToBase64String(tag),
            "|",
            Convert.ToBase64String(ciphertext));
    }

    /// <inheritdoc />
    public string Unprotect(string envelopeOrPlain)
    {
        // 非信封内容按原文返回：兼容步骤 7 的既有明文数据与“从备份恢复”的明文。
        if (!MediaRules.IsEnvelope(envelopeOrPlain))
        {
            return envelopeOrPlain;
        }

        if (_key is null)
        {
            throw new MediaLockedException();
        }

        try
        {
            var parts = envelopeOrPlain.Split('|');
            var nonce = Convert.FromBase64String(parts[1]);
            var tag = Convert.FromBase64String(parts[2]);
            var ciphertext = Convert.FromBase64String(parts[3]);
            var plaintext = new byte[ciphertext.Length];

            using var gcm = new AesGcm(_key, TagSize);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext, EnvelopeAad);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new MediaIntegrityException("加密数据校验失败：数据可能被篡改或损坏。", ex);
        }
    }

    /// <summary>
    /// 加密一个文件的内容（若已是信封则原样保留）；文件不存在时不做任何处理。
    /// </summary>
    /// <param name="path">文件绝对路径。</param>
    public void ProtectFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var content = File.ReadAllText(path);
        if (MediaRules.IsEnvelope(content))
        {
            return;
        }

        File.WriteAllText(path, Protect(content));
    }

    /// <summary>
    /// 记录一次失败：递增计数、落盘非敏感字段并返回失败结果。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="now">失败时间。</param>
    /// <param name="message">失败提示。</param>
    /// <returns>失败结果（含当前失败次数与冷却时长）。</returns>
    private MediaUnlockResult RegisterFailureAndBuildResult(string dataDirectory, DateTimeOffset now, string message)
    {
        var cooldown = _throttle.RegisterFailure(now);
        _stateStore.SetUnlockFailures(dataDirectory, _throttle.ConsecutiveFailures);
        _stateStore.SetCooldownUntil(dataDirectory, _throttle.CooldownUntil);

        var text = cooldown > TimeSpan.Zero
            ? $"{message}已连续失败 {_throttle.ConsecutiveFailures} 次，需等待 {cooldown.TotalSeconds:0} 秒后才能再次尝试。"
            : $"{message}已连续失败 {_throttle.ConsecutiveFailures} 次。";

        return new MediaUnlockResult(false, MediaState.Locked, text, _throttle.ConsecutiveFailures, cooldown);
    }

    /// <summary>首次访问失败计数时，从介质头文件恢复已落盘的失败计数与冷却。</summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    private void EnsureThrottleRestored(string dataDirectory)
    {
        if (_throttleRestored)
        {
            return;
        }

        _throttle.Restore(_stateStore.GetUnlockFailures(dataDirectory), _stateStore.GetCooldownUntil(dataDirectory));
        _throttleRestored = true;
    }

    /// <summary>把派生密钥放入内存缓存（传 null 表示锁定并清零旧密钥）。</summary>
    /// <param name="key">派生密钥或 null。</param>
    private void SetKey(byte[]? key)
    {
        if (_key is not null)
        {
            zeroKey(_key);
        }

        _key = key;
    }

    /// <summary>用主密码、随机盐与迭代次数派生 32 字节密钥（PBKDF2-HMAC-SHA256）。</summary>
    /// <param name="password">主密码。</param>
    /// <param name="salt">随机盐。</param>
    /// <param name="iterations">迭代次数。</param>
    /// <returns>派生密钥。</returns>
    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    /// <summary>由派生密钥计算密码校验值（HMAC-SHA256，仅用于校验，非密码本身）。</summary>
    /// <param name="key">派生密钥。</param>
    /// <returns>32 字节校验值。</returns>
    private static byte[] ComputeVerifier(byte[] key)
        => HMACSHA256.HashData(key, VerifierContext);

    /// <summary>把密钥字节清零。</summary>
    /// <param name="key">待清零的密钥。</param>
    private static void zeroKey(byte[] key) => CryptographicOperations.ZeroMemory(key);
}