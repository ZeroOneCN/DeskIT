using System.Runtime.InteropServices;

namespace DeskIT.Windows;

/// <summary>目标系统版本信息。</summary>
/// <param name="Major">主版本号（Windows 10/11 均为 10）。</param>
/// <param name="Build">内部版本号。</param>
/// <param name="Is64Bit">是否为 64 位操作系统。</param>
/// <param name="DisplayName">面向 IT 人员的可读名称，如“Windows 11（内部版本 22621）”。</param>
public sealed record OsVersionInfo(int Major, int Build, bool Is64Bit, string DisplayName);

/// <summary>目标系统版本信息的提供者。</summary>
public interface IOsVersionInfoProvider
{
    /// <summary>读取当前系统版本信息（不使用远程协议，纯本地查询）。</summary>
    /// <returns>系统版本信息。</returns>
    OsVersionInfo GetVersion();
}

/// <summary>
/// 通过 ntdll!RtlGetVersion 读取真实系统版本。
/// 不使用 Environment.OSVersion，因其受应用清单影响可能误报为 6.2，从而错误判定“不受支持”。
/// </summary>
public sealed class WindowsOsVersionInfoProvider : IOsVersionInfoProvider
{
    /// <inheritdoc />
    public OsVersionInfo GetVersion()
    {
        var info = new RtlOsVersionInfo
        {
            dwOSVersionInfoSize = (uint)Marshal.SizeOf<RtlOsVersionInfo>()
        };

        var status = RtlGetVersion(ref info);
        var major = status == 0 ? (int)info.dwMajorVersion : Environment.OSVersion.Version.Major;
        var build = status == 0 ? (int)info.dwBuildNumber : Environment.OSVersion.Version.Build;
        var is64Bit = Environment.Is64BitOperatingSystem;

        return new OsVersionInfo(major, build, is64Bit, BuildDisplayName(major, build, is64Bit));
    }

    /// <summary>拼装可读的系统名称。</summary>
    /// <param name="major">主版本号。</param>
    /// <param name="build">内部版本号。</param>
    /// <param name="is64Bit">是否 64 位。</param>
    /// <returns>如“Windows 11（内部版本 22621，64 位）”；未知版本回退为“未知系统”。</returns>
    private static string BuildDisplayName(int major, int build, bool is64Bit)
    {
        var arch = is64Bit ? "64 位" : "32 位";
        if (major != 10)
        {
            return $"不支持的系统（主版本 {major}，{arch}）";
        }

        var product = build >= 22000 ? "Windows 11" : "Windows 10";
        return $"{product}（内部版本 {build}，{arch}）";
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int RtlGetVersion(ref RtlOsVersionInfo versionInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RtlOsVersionInfo
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }
}