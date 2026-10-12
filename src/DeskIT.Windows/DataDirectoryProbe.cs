using System.Security.Cryptography;

namespace DeskIT.Windows;

/// <summary>数据目录一次性读写探测（M1 自检项：数据目录可读写）。</summary>
public interface IDataDirectoryProbe
{
    /// <summary>
    /// 在目标目录写入探测文件并立即读回校验，随后删除，判定目录当前是否可读写。
    /// </summary>
    /// <param name="directoryPath">待探测目录的绝对路径（通常为 U 盘数据目录）。</param>
    /// <returns>可读写返回 true；目录不存在、无权限或写入失败返回 false。</returns>
    Task<bool> CanReadWriteAsync(string directoryPath);
}

/// <summary>数据目录读写探测的实现：不落任何持久数据，探测文件在结束时删除。</summary>
public sealed class DataDirectoryProbe : IDataDirectoryProbe
{
    /// <inheritdoc />
    public async Task<bool> CanReadWriteAsync(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(directoryPath);

            var probePath = Path.Combine(directoryPath, $".deskit-probe-{Guid.NewGuid():N}.tmp");
            var payload = RandomNumberGenerator.GetBytes(64);

            await File.WriteAllBytesAsync(probePath, payload);

            var readBack = await File.ReadAllBytesAsync(probePath);
            var ok = readBack.AsSpan().SequenceEqual(payload);

            File.Delete(probePath);
            return ok;
        }
        catch (Exception) when (
            !System.Diagnostics.Debugger.IsAttached)
        {
            // IOException / UnauthorizedAccessException / NotSupportedException 等一律视为不可写。
            return false;
        }
    }
}

/// <summary>介质可用空间提供者（M1 自检项：介质可用空间）。</summary>
public interface IDriveSpaceProvider
{
    /// <summary>
    /// 读取指定路径所在卷的剩余可用字节数。
    /// </summary>
    /// <param name="path">卷内任意路径（通常为数据目录）。</param>
    /// <returns>剩余可用字节数；无法解析卷时返回 0。</returns>
    long GetAvailableFreeBytes(string path);
}

/// <summary>基于 DriveInfo 的可用空间提供者。</summary>
public sealed class DriveSpaceProvider : IDriveSpaceProvider
{
    /// <inheritdoc />
    public long GetAvailableFreeBytes(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return 0;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : 0;
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            return 0;
        }
    }
}