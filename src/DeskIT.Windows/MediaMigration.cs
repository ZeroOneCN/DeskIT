using System.Runtime.InteropServices;
using System.Text;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 介质迁移检测（M8）：允许把数据目录整体复制到新介质继续使用（主密码不变）；
/// 程序检测到介质标识（基于卷序列号等本机可得信息）与登记值变化时提示“已更换介质”。
/// 介质标识<b>不参与密钥派生、不绑定机器</b>，只用于识别介质本身。
/// </summary>
public interface IMediaMigration
{
    /// <summary>
    /// 读取当前数据目录所在介质的标识。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>介质标识；无法读取时返回空串。</returns>
    string ResolveCurrentMediaId(string dataDirectory);

    /// <summary>
    /// 检测介质是否已更换：当前介质标识与登记值不一致即为已更换。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="currentMediaId">当前介质标识。</param>
    /// <returns>检测结果（含对旧介质数据安全处置的建议）。</returns>
    MediaMigrationResult DetectMediaChange(string dataDirectory, string currentMediaId);
}

/// <inheritdoc cref="IMediaMigration" />
public sealed class MediaMigration : IMediaMigration
{
    private readonly IMediaStateStore _stateStore;

    /// <summary>
    /// 初始化介质迁移检测器。
    /// </summary>
    /// <param name="stateStore">介质头信息存储；为 null 时使用 operators.json 实现。</param>
    public MediaMigration(IMediaStateStore? stateStore = null)
    {
        _stateStore = stateStore ?? new MediaStateStore();
    }

    /// <inheritdoc />
    public string ResolveCurrentMediaId(string dataDirectory) => ResolveMediaId(dataDirectory);

    /// <inheritdoc />
    public MediaMigrationResult DetectMediaChange(string dataDirectory, string currentMediaId)
    {
        var descriptor = _stateStore.GetDescriptor(dataDirectory);
        if (descriptor is null)
        {
            return new MediaMigrationResult(false, "介质尚未初始化。");
        }

        if (string.IsNullOrEmpty(currentMediaId))
        {
            return new MediaMigrationResult(false, "无法读取当前介质标识，暂不判定是否更换介质。");
        }

        if (string.Equals(descriptor.MediaId, currentMediaId, StringComparison.Ordinal))
        {
            return new MediaMigrationResult(false, "介质标识与登记值一致，未检测到更换介质。");
        }

        return new MediaMigrationResult(
            true,
            "已更换介质：数据目录被复制到了新的介质（主密码保持不变，可继续使用）。"
            + "建议对旧介质上的数据做安全处置（本产品不提供擦除工具，请按公司流程销毁或加密保管）。");
    }

    /// <summary>
    /// 由数据目录所在卷的序列号生成介质标识（形如 <c>vol-1A2B3C4D</c>）；无法读取时返回空串。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>介质标识或空串。</returns>
    public static string ResolveMediaId(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            return string.Empty;
        }

        try
        {
            var fullPath = Path.GetFullPath(dataDirectory);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return string.Empty;
            }

            if (!GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0))
            {
                return string.Empty;
            }

            return $"vol-{serial:X8}";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 读取卷序列号（Win32 GetVolumeInformationW）。序列号仅用于识别介质，不参与密钥派生。
    /// </summary>
    /// <param name="rootPathName">卷根路径（如 "E:\"）。</param>
    /// <param name="volumeNameBuffer">卷标接收缓冲区；无需时传 null。</param>
    /// <param name="volumeNameSize">卷标缓冲区长度。</param>
    /// <param name="volumeSerialNumber">输出：卷序列号。</param>
    /// <param name="maximumComponentLength">输出：最大文件名长度。</param>
    /// <param name="fileSystemFlags">输出：文件系统标志。</param>
    /// <param name="fileSystemNameBuffer">文件系统名接收缓冲区；无需时传 null。</param>
    /// <param name="fileSystemNameSize">文件系统名缓冲区长度。</param>
    /// <returns>成功返回 true。</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);
}