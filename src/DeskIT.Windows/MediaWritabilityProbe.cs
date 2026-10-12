using System.Security.Cryptography;

namespace DeskIT.Windows;

/// <summary>
/// 介质可写性探测的实现（M8 拔出保护）：在数据目录写入临时探测文件并 flush，随后删除。
/// 失败返回 false，不抛出异常（IOException / UnauthorizedAccessException 等一律视为不可写）。
/// </summary>
public sealed class MediaWritabilityProbe : IMediaWritabilityProbe
{
    /// <inheritdoc />
    public async Task<bool> ProbeAsync(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return false;
            }

            var probePath = Path.Combine(directoryPath, $".deskit-media-probe-{Guid.NewGuid():N}.tmp");
            var payload = RandomNumberGenerator.GetBytes(32);

            await using (var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            File.Delete(probePath);
            return true;
        }
        catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
        {
            return false;
        }
    }
}