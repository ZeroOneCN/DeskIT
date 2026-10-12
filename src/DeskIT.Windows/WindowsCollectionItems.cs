using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DeskIT.Core;
using Microsoft.Win32;

namespace DeskIT.Windows;

/// <summary>
/// M2 采集项清单：为 7 个分组构造可独立执行、独立超时的采集项。
/// 所有查询均为本机只读查询（WMI / 注册表 / 网络接口 / 卷信息 / 固定参数的 netsh 查询），不使用远程协议，
/// 也不读取个人文件内容、浏览器历史、聊天记录、屏幕内容与键盘输入。
/// </summary>
internal static class WindowsCollectionItems
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string UacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string InternetSettingsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKeyWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string PhysicalDiskScope = @"root\Microsoft\Windows\Storage";
    private const string SecurityCenterScope = @"root\SecurityCenter2";
    private const string BitLockerScope = @"root\CIMV2\Security\MicrosoftVolumeEncryption";

    /// <summary>许可服务应用标识（Windows 客户端激活状态查询用）。</summary>
    private const string WindowsLicensingAppId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    /// <summary>
    /// 构造全部采集项（按分组顺序排列）。
    /// </summary>
    /// <param name="context">采集上下文（用于回填硬件序列号等机器标识事实）。</param>
    /// <param name="privilege">当前权限级别。</param>
    /// <returns>采集项清单。</returns>
    public static IReadOnlyList<CollectionItem> Build(CollectionContext context, PrivilegeLevel privilege)
    {
        return new List<CollectionItem>
        {
            // ── 系统 ──────────────────────────────────────────────
            new(CollectionGroup.System, "计算机名与系统架构", new[] { "计算机名", "系统架构" }, () =>
            {
                return CollectionItemOutput.Of(
                    Field("计算机名", Environment.MachineName),
                    Field("系统架构", RuntimeInformation.OSArchitecture.ToString()));
            }),
            new(CollectionGroup.System, "系统版本与内部版本号", new[] { "系统版本", "内部版本号" }, () =>
            {
                var product = RegistryProbe.ReadLocalMachineString(CurrentVersionKey, "ProductName");
                var display = RegistryProbe.ReadLocalMachineString(CurrentVersionKey, "DisplayVersion");
                var build = RegistryProbe.ReadLocalMachineString(CurrentVersionKey, "CurrentBuild");
                var ubr = RegistryProbe.ReadInt(RegistryHive.LocalMachine, CurrentVersionKey, "UBR");

                var version = string.Join(" ", new[] { product, display }.Where(x => x is not null));
                var buildText = build is null ? null : (ubr is null ? build : $"{build}.{ubr}");

                return CollectionItemOutput.Of(Field("系统版本", version), Field("内部版本号", buildText));
            }),
            new(CollectionGroup.System, "系统安装日期", new[] { "安装日期" }, () =>
            {
                var raw = RegistryProbe.ReadValue(RegistryHive.LocalMachine, CurrentVersionKey, "InstallDate");
                if (raw is int seconds && seconds > 0)
                {
                    return CollectionItemOutput.Value(
                        "安装日期",
                        DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                }

                var os = WmiQuery.First("SELECT InstallDate FROM Win32_OperatingSystem");
                return CollectionItemOutput.Value("安装日期", WmiQuery.Date(os ?? Empty, "InstallDate")?.ToString("yyyy-MM-dd HH:mm"));
            }),
            new(CollectionGroup.System, "时区", new[] { "时区" }, () =>
            {
                var zone = TimeZoneInfo.Local;
                return CollectionItemOutput.Value("时区", $"{zone.Id}（{zone.DisplayName}）");
            }),
            new(CollectionGroup.System, "系统激活状态", new[] { "激活状态" }, () =>
            {
                var row = WmiQuery.First(
                    $"SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL AND ApplicationID='{WindowsLicensingAppId}'");
                var status = WmiQuery.Num(row ?? Empty, "LicenseStatus");
                if (status is null)
                {
                    return CollectionItemOutput.Status("激活状态", null, FieldStatus.NotApplicable);
                }

                return CollectionItemOutput.Value("激活状态", status.Value switch
                {
                    0 => "未激活",
                    1 => "已激活",
                    2 => "宽限期",
                    3 => "额外宽限期",
                    4 => "非正版宽限期",
                    5 => "通知状态",
                    6 => "扩展宽限期",
                    _ => $"其他状态（{status.Value}）"
                });
            }),
            new(CollectionGroup.System, "最近一次系统更新", new[] { "最近更新日期" }, () =>
            {
                var last = RegistryProbe.ReadLocalMachineString(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install", "LastSuccessTime");
                if (last is not null)
                {
                    return CollectionItemOutput.Value("最近更新日期", last);
                }

                var latest = WmiQuery.Run("SELECT InstalledOn FROM Win32_QuickFixEngineering")
                    .Select(r => WmiQuery.Date(r, "InstalledOn"))
                    .Where(d => d is not null)
                    .Select(d => d!.Value)
                    .OrderByDescending(d => d)
                    .FirstOrDefault();

                return latest == default
                    ? CollectionItemOutput.Status("最近更新日期", null, FieldStatus.NotApplicable)
                    : CollectionItemOutput.Value("最近更新日期", latest.ToString("yyyy-MM-dd"));
            }),

            // ── 硬件 ──────────────────────────────────────────────
            new(CollectionGroup.Hardware, "整机标识（厂商 / 型号 / 序列号）", new[] { "厂商", "型号", "序列号" }, () =>
            {
                var bios = WmiQuery.First("SELECT SerialNumber FROM Win32_BIOS") ?? Empty;
                var serial = WmiQuery.Str(bios, "SerialNumber");
                context.HardwareSerial = serial;

                var computer = WmiQuery.First("SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem") ?? Empty;
                var manufacturer = RegistryProbe.ReadLocalMachineString(BiosKey, "SystemManufacturer")
                    ?? WmiQuery.Str(computer, "Manufacturer");
                var model = RegistryProbe.ReadLocalMachineString(BiosKey, "SystemProductName")
                    ?? WmiQuery.Str(computer, "Model");

                return CollectionItemOutput.Of(
                    Field("厂商", manufacturer),
                    Field("型号", model),
                    Field("序列号", serial));
            }),
            new(CollectionGroup.Hardware, "处理器", new[] { "处理器" }, () =>
            {
                var rows = WmiQuery.Run("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
                if (rows.Count == 0)
                {
                    return CollectionItemOutput.Status("处理器", null, FieldStatus.NotApplicable);
                }

                var parts = rows.Select(row =>
                {
                    var name = WmiQuery.Str(row, "Name") ?? "未知处理器";
                    var cores = WmiQuery.Num(row, "NumberOfCores");
                    var threads = WmiQuery.Num(row, "NumberOfLogicalProcessors");
                    var clock = WmiQuery.Num(row, "MaxClockSpeed");
                    var detail = new List<string>();
                    if (cores is not null)
                    {
                        detail.Add($"{cores} 核");
                    }

                    if (threads is not null)
                    {
                        detail.Add($"{threads} 线程");
                    }

                    if (clock is not null)
                    {
                        detail.Add($"{clock.Value / 1000.0:0.0} GHz");
                    }

                    return detail.Count == 0 ? name : $"{name}（{string.Join(" / ", detail)}）";
                });

                return CollectionItemOutput.Value("处理器", string.Join("；", parts));
            }),
            new(CollectionGroup.Hardware, "内存容量与插槽占用", new[] { "内存容量" }, () =>
            {
                var modules = WmiQuery.Run("SELECT Capacity FROM Win32_PhysicalMemory");
                if (modules.Count == 0)
                {
                    return CollectionItemOutput.Status("内存容量", null, FieldStatus.NotApplicable);
                }

                var total = modules.Sum(m => WmiQuery.Num(m, "Capacity") ?? 0);
                var array = WmiQuery.First("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
                var slots = WmiQuery.Num(array ?? Empty, "MemoryDevices");

                var text = ByteFormat.Format(total);
                text += slots is null
                    ? $"（已用 {modules.Count} 个插槽）"
                    : $"（已用 {modules.Count} / {slots} 个插槽）";

                return CollectionItemOutput.Value("内存容量", text);
            }),
            new(CollectionGroup.Hardware, "显卡", new[] { "显卡" }, () =>
            {
                var names = WmiQuery.Run("SELECT Name FROM Win32_VideoController")
                    .Select(row => WmiQuery.Str(row, "Name"))
                    .Where(name => name is not null)
                    .Select(name => name!)
                    .ToList();

                return names.Count == 0
                    ? CollectionItemOutput.Status("显卡", null, FieldStatus.NotApplicable)
                    : CollectionItemOutput.Value("显卡", string.Join("；", names));
            }),
            new(CollectionGroup.Hardware, "电池健康", new[] { "电池健康" }, () =>
            {
                var batteries = WmiQuery.Run("SELECT DesignCapacity, FullChargeCapacity FROM Win32_Battery");
                if (batteries.Count == 0)
                {
                    return CollectionItemOutput.Status("电池健康", null, FieldStatus.NotApplicable);
                }

                var design = batteries.Sum(b => WmiQuery.Num(b, "DesignCapacity") ?? 0);
                var full = batteries.Sum(b => WmiQuery.Num(b, "FullChargeCapacity") ?? 0);
                if (design <= 0 || full <= 0)
                {
                    return CollectionItemOutput.Status("电池健康", "已检出电池，但设计/满充容量不可得", FieldStatus.NotApplicable);
                }

                var health = full * 100.0 / design;
                return CollectionItemOutput.Value(
                    "电池健康",
                    $"设计容量 {design} mWh / 满充容量 {full} mWh（健康度 {health:0}%）");
            }),

            // ── 存储 ──────────────────────────────────────────────
            new(CollectionGroup.Storage, "分区容量与文件系统", new[] { "分区与文件系统" }, () =>
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed)
                    .ToList();

                if (drives.Count == 0)
                {
                    return CollectionItemOutput.Status("分区与文件系统", null, FieldStatus.NotApplicable);
                }

                var lines = drives.Select(d =>
                {
                    var letter = d.Name.TrimEnd('\\');
                    if (!d.IsReady)
                    {
                        return $"{letter} 未就绪";
                    }

                    return $"{letter} 总 {ByteFormat.Format(d.TotalSize)} / 可用 {ByteFormat.Format(d.AvailableFreeSpace)} / {d.DriveFormat}";
                });

                return CollectionItemOutput.List("分区与文件系统", $"共 {drives.Count} 个固定卷", lines);
            }),
            new(CollectionGroup.Storage, "磁盘类型与 SMART 健康", new[] { "磁盘类型", "SMART 健康摘要" }, () =>
            {
                var disks = WmiQuery.Run(
                    "SELECT FriendlyName, MediaType, Size, HealthStatus FROM MSFT_PhysicalDisk",
                    PhysicalDiskScope);

                if (disks.Count == 0)
                {
                    return CollectionItemOutput.Of(
                        CollectionItemOutput.Status("磁盘类型", null, FieldStatus.NotApplicable).Fields[0],
                        CollectionItemOutput.Status("SMART 健康摘要", null, FieldStatus.NotApplicable).Fields[0]);
                }

                var types = disks
                    .Select(d => MediaTypeText(WmiQuery.Num(d, "MediaType")))
                    .Distinct()
                    .ToList();

                var health = disks
                    .Select(d => HealthStatusText(WmiQuery.Num(d, "HealthStatus")))
                    .Distinct()
                    .ToList();

                var lines = disks.Select(d =>
                {
                    var name = WmiQuery.Str(d, "FriendlyName") ?? "未知磁盘";
                    var size = WmiQuery.Num(d, "Size");
                    var sizeText = size is null ? string.Empty : $" {ByteFormat.Format(size.Value)}";
                    return $"{name}{sizeText} · {MediaTypeText(WmiQuery.Num(d, "MediaType"))} · {HealthStatusText(WmiQuery.Num(d, "HealthStatus"))}";
                });

                var output = CollectionItemOutput.Of(
                    Field("磁盘类型", string.Join("、", types)),
                    Field("SMART 健康摘要", string.Join("、", health)));
                return output with { Details = lines.ToList() };
            }),

            // ── 网络 ──────────────────────────────────────────────
            new(CollectionGroup.Network, "网卡列表与状态", new[] { "网卡状态" }, () =>
            {
                var adapters = SafeNetworkInterfaces();
                if (adapters.Count == 0)
                {
                    return CollectionItemOutput.Status("网卡状态", null, FieldStatus.NotApplicable);
                }

                var lines = adapters.Select(a =>
                {
                    var speed = a.Speed > 0 ? $" · {a.Speed / 1_000_000} Mbps" : string.Empty;
                    return $"{a.Name}：{StatusText(a.OperationalStatus)}{speed}（{a.NetworkInterfaceType}）";
                });

                var up = adapters.Count(a => a.OperationalStatus == OperationalStatus.Up);
                return CollectionItemOutput.List("网卡状态", $"共 {adapters.Count} 个适配器，{up} 个已连接", lines);
            }),
            new(CollectionGroup.Network, "IP / 网关 / DNS 配置", new[] { "IPv4 地址", "默认网关", "DNS 服务器" }, () =>
            {
                var active = SafeNetworkInterfaces()
                    .Where(a => a.OperationalStatus == OperationalStatus.Up)
                    .ToList();

                if (active.Count == 0)
                {
                    return CollectionItemOutput.Of(
                        CollectionItemOutput.Status("IPv4 地址", null, FieldStatus.NotApplicable).Fields[0],
                        CollectionItemOutput.Status("默认网关", null, FieldStatus.NotApplicable).Fields[0],
                        CollectionItemOutput.Status("DNS 服务器", null, FieldStatus.NotApplicable).Fields[0]);
                }

                var addresses = new List<string>();
                var gateways = new List<string>();
                var dnsServers = new List<string>();

                foreach (var adapter in active)
                {
                    var properties = adapter.GetIPProperties();

                    addresses.AddRange(properties.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString()));

                    gateways.AddRange(properties.GatewayAddresses
                        .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(g => g.Address.ToString()));

                    dnsServers.AddRange(properties.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString()));
                }

                return CollectionItemOutput.Of(
                    Field("IPv4 地址", Join(addresses)),
                    Field("默认网关", Join(gateways)),
                    Field("DNS 服务器", Join(dnsServers)));
            }),
            new(CollectionGroup.Network, "代理配置", new[] { "代理" }, () =>
            {
                var enabled = RegistryProbe.ReadInt(RegistryHive.CurrentUser, InternetSettingsKey, "ProxyEnable");
                var server = RegistryProbe.ReadCurrentUserString(InternetSettingsKey, "ProxyServer");
                var autoConfig = RegistryProbe.ReadCurrentUserString(InternetSettingsKey, "AutoConfigURL");

                if (enabled == 1)
                {
                    return CollectionItemOutput.Value("代理", server is null ? "已启用（未配置代理服务器地址）" : $"已启用：{server}");
                }

                if (autoConfig is not null)
                {
                    return CollectionItemOutput.Value("代理", $"未启用固定代理，已配置自动配置脚本：{autoConfig}");
                }

                return CollectionItemOutput.Value("代理", "未启用");
            }),
            new(CollectionGroup.Network, "VPN 适配器", new[] { "VPN 适配器" }, () =>
            {
                var vpn = SafeNetworkInterfaces()
                    .Where(a => ContainsVpnKeyword(a.Name) || ContainsVpnKeyword(a.Description))
                    .Select(a => $"{a.Name}：{StatusText(a.OperationalStatus)}")
                    .ToList();

                return vpn.Count == 0
                    ? CollectionItemOutput.Value("VPN 适配器", "未检出")
                    : CollectionItemOutput.Value("VPN 适配器", string.Join("；", vpn));
            }),
            new(CollectionGroup.Network, "域或工作组", new[] { "域 / 工作组" }, () =>
            {
                var computer = WmiQuery.First("SELECT Domain, PartOfDomain, Workgroup FROM Win32_ComputerSystem") ?? Empty;
                var partOfDomain = WmiQuery.Str(computer, "PartOfDomain");
                var domain = WmiQuery.Str(computer, "Domain");
                var workgroup = WmiQuery.Str(computer, "Workgroup");

                if (string.Equals(partOfDomain, "True", StringComparison.OrdinalIgnoreCase))
                {
                    return CollectionItemOutput.Value("域 / 工作组", $"已加入域：{domain}");
                }

                return CollectionItemOutput.Value("域 / 工作组", workgroup is null ? "工作组（名称未知）" : $"工作组：{workgroup}");
            }),
            new(CollectionGroup.Network, "Wi-Fi 已连网络", new[] { "Wi-Fi 网络" }, () =>
            {
                var output = ProcessCapture.Run("netsh.exe", "wlan show interfaces", TimeSpan.FromSeconds(6));
                if (output is null)
                {
                    return CollectionItemOutput.Status("Wi-Fi 网络", null, FieldStatus.Timeout);
                }

                if (output.Contains("无线自动配置服务", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("Wireless AutoConfig Service", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("没有无线接口", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("There is no wireless interface", StringComparison.OrdinalIgnoreCase))
                {
                    return CollectionItemOutput.Status("Wi-Fi 网络", "未检出无线接口", FieldStatus.NotApplicable);
                }

                var ssid = output
                    .Split('\n')
                    .Select(line => Regex.Match(line, @"^\s*SSID\s*:\s*(?<v>.+?)\s*$", RegexOptions.IgnoreCase))
                    .FirstOrDefault(m => m.Success);
                var bssid = output
                    .Split('\n')
                    .Select(line => Regex.Match(line, @"^\s*BSSID\s*:\s*(?<v>.+?)\s*$", RegexOptions.IgnoreCase))
                    .FirstOrDefault(m => m.Success);

                return ssid is null
                    ? CollectionItemOutput.Value("Wi-Fi 网络", "未连接无线网络")
                    : CollectionItemOutput.Value("Wi-Fi 网络", $"{ssid.Groups["v"].Value}（BSSID：{bssid?.Groups["v"].Value ?? "未知"}）");
            }),

            // ── 账号与环境 ────────────────────────────────────────
            new(CollectionGroup.AccountEnvironment, "当前登录用户", new[] { "当前用户" }, () =>
            {
                var name = $"{Environment.UserDomainName}\\{Environment.UserName}";
                return CollectionItemOutput.Value("当前用户", name);
            }),
            new(CollectionGroup.AccountEnvironment, "本地账号列表", new[] { "本地账号" }, () =>
            {
                var accounts = WmiQuery.Run("SELECT Name, Disabled FROM Win32_UserAccount WHERE LocalAccount=True");
                if (accounts.Count == 0)
                {
                    return CollectionItemOutput.Status("本地账号", null, FieldStatus.NotApplicable);
                }

                var lines = accounts.Select(a =>
                {
                    var name = WmiQuery.Str(a, "Name") ?? "未知";
                    var disabled = string.Equals(WmiQuery.Str(a, "Disabled"), "True", StringComparison.OrdinalIgnoreCase);
                    return $"{name}（{(disabled ? "已禁用" : "已启用")}）";
                });

                return CollectionItemOutput.List("本地账号", $"共 {accounts.Count} 个本地账号", lines);
            }),
            new(CollectionGroup.AccountEnvironment, "本地管理员组成员", new[] { "管理员组成员" }, () =>
            {
                var host = Environment.MachineName;
                var rows = WmiQuery.Run(
                    $"SELECT PartComponent FROM Win32_GroupUser WHERE GroupComponent=\"Win32_Group.Domain='{host}',Name='Administrators'\"");
                if (rows.Count == 0)
                {
                    return CollectionItemOutput.Status("管理员组成员", null, FieldStatus.NotApplicable);
                }

                var members = rows
                    .Select(r => Regex.Match(WmiQuery.Str(r, "PartComponent") ?? string.Empty, "Name=\"(?<n>[^\"]+)\""))
                    .Where(m => m.Success)
                    .Select(m => m.Groups["n"].Value)
                    .ToList();

                return members.Count == 0
                    ? CollectionItemOutput.Status("管理员组成员", null, FieldStatus.NotApplicable)
                    : CollectionItemOutput.Value("管理员组成员", string.Join("、", members));
            }),
            new(CollectionGroup.AccountEnvironment, "UAC 级别", new[] { "UAC 级别" }, () =>
            {
                var enabledLua = RegistryProbe.ReadInt(RegistryHive.LocalMachine, UacKey, "EnableLUA");
                if (enabledLua is null)
                {
                    return CollectionItemOutput.Status("UAC 级别", null, FieldStatus.NotApplicable);
                }

                if (enabledLua == 0)
                {
                    return CollectionItemOutput.Value("UAC 级别", "已关闭（EnableLUA=0）");
                }

                var behavior = RegistryProbe.ReadInt(RegistryHive.LocalMachine, UacKey, "ConsentPromptBehaviorAdmin");
                var behaviorText = behavior switch
                {
                    0 => "从不通知（最高权限静默提权）",
                    1 => "提示输入凭据（安全桌面）",
                    2 => "提示同意（安全桌面）",
                    3 => "提示输入凭据",
                    4 => "提示同意",
                    5 => "提示同意（仅非 Windows 二进制）",
                    _ => "未配置（默认）"
                };

                return CollectionItemOutput.Value("UAC 级别", $"已启用；管理员提权行为：{behaviorText}");
            }),
            new(CollectionGroup.AccountEnvironment, "是否加入域", new[] { "是否加入域" }, () =>
            {
                var computer = WmiQuery.First("SELECT Domain, PartOfDomain FROM Win32_ComputerSystem") ?? Empty;
                var joined = string.Equals(WmiQuery.Str(computer, "PartOfDomain"), "True", StringComparison.OrdinalIgnoreCase);
                var domain = WmiQuery.Str(computer, "Domain");
                return CollectionItemOutput.Value("是否加入域", joined ? $"是（{domain}）" : "否（工作组环境）");
            }),

            // ── 已装软件 ──────────────────────────────────────────
            new(CollectionGroup.InstalledSoftware, "已安装软件清单", new[] { "已装软件" }, () =>
            {
                var entries = EnumerateInstalledSoftware();
                if (entries.Count == 0)
                {
                    return CollectionItemOutput.Status("已装软件", "未读取到已安装软件清单", FieldStatus.NotApplicable);
                }

                var lines = entries
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e =>
                    {
                        var parts = new List<string> { e.Name };
                        if (e.Version is not null)
                        {
                            parts.Add($"版本 {e.Version}");
                        }

                        if (e.Publisher is not null)
                        {
                            parts.Add(e.Publisher);
                        }

                        if (e.InstallDate is not null)
                        {
                            parts.Add($"安装于 {e.InstallDate}");
                        }

                        if (e.InstallLocation is not null)
                        {
                            parts.Add(e.InstallLocation);
                        }

                        return string.Join(" · ", parts);
                    });

                return CollectionItemOutput.List("已装软件", $"共 {entries.Count} 项", lines);
            }),

            // ── 安全与更新 ────────────────────────────────────────
            new(CollectionGroup.SecurityUpdate, "防病毒与实时防护", new[] { "防病毒产品", "实时防护" }, () =>
            {
                var products = WmiQuery.Run("SELECT displayName, productState FROM AntiVirusProduct", SecurityCenterScope);
                if (products.Count == 0)
                {
                    return CollectionItemOutput.Of(
                        CollectionItemOutput.Status("防病毒产品", "未检出（该系统未注册安全中心产品）", FieldStatus.NotApplicable).Fields[0],
                        CollectionItemOutput.Status("实时防护", null, FieldStatus.NotApplicable).Fields[0]);
                }

                var names = products
                    .Select(p => WmiQuery.Str(p, "displayName"))
                    .Where(n => n is not null)
                    .Select(n => n!)
                    .ToList();

                var realTime = products
                    .Select(p => WmiQuery.Num(p, "productState"))
                    .Where(s => s is not null)
                    .Select(s => (s!.Value & 0x1000) != 0 ? "已开启" : "已关闭")
                    .Distinct()
                    .ToList();

                return CollectionItemOutput.Of(
                    Field("防病毒产品", Join(names)),
                    Field("实时防护", realTime.Count == 0 ? "无法判定（productState 未提供状态位）" : string.Join("、", realTime)));
            }),
            new(CollectionGroup.SecurityUpdate, "BitLocker 卷加密状态", new[] { "BitLocker" }, () =>
            {
                var volumes = WmiQuery.Run(
                    "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume",
                    BitLockerScope);

                if (volumes.Count == 0)
                {
                    return CollectionItemOutput.Status("BitLocker", null, FieldStatus.NotApplicable);
                }

                var lines = volumes.Select(v =>
                {
                    var letter = WmiQuery.Str(v, "DriveLetter") ?? "?";
                    var status = WmiQuery.Num(v, "ProtectionStatus");
                    var text = status switch
                    {
                        0 => "未启用保护",
                        1 => "已启用保护",
                        2 => "未知",
                        _ => "未知"
                    };
                    return $"{letter}: {text}";
                });

                return CollectionItemOutput.List("BitLocker", $"共 {volumes.Count} 个卷", lines);
            }),
            new(CollectionGroup.SecurityUpdate, "防火墙状态", new[] { "域网络防火墙", "专用网络防火墙", "公用网络防火墙" }, () =>
            {
                return CollectionItemOutput.Of(
                    Field("域网络防火墙", FirewallText("DomainProfile")),
                    Field("专用网络防火墙", FirewallText("StandardProfile")),
                    Field("公用网络防火墙", FirewallText("PublicProfile")));
            }),
            new(CollectionGroup.SecurityUpdate, "Windows 更新服务状态", new[] { "更新服务状态" }, () =>
            {
                var service = WmiQuery.First("SELECT State, StartMode FROM Win32_Service WHERE Name='wuauserv'");
                if (service is null)
                {
                    return CollectionItemOutput.Status("更新服务状态", null, FieldStatus.NotApplicable);
                }

                return CollectionItemOutput.Value(
                    "更新服务状态",
                    $"Windows Update（wuauserv）：{WmiQuery.Str(service, "State") ?? "未知"}，启动类型 {WmiQuery.Str(service, "StartMode") ?? "未知"}");
            }),
            new(CollectionGroup.SecurityUpdate, "待安装更新数量", new[] { "待安装更新" }, () =>
            {
                // 待安装更新需访问更新服务（在线）方能准确获取；本产品为离线工具，如实标注为不适用。
                return CollectionItemOutput.Status("待安装更新", "不适用（离线工具无法查询更新服务）", FieldStatus.NotApplicable);
            })
        };
    }

    /// <summary>空字典，用于 WMI 查询无结果时的统一取值路径。</summary>
    private static IReadOnlyDictionary<string, object?> Empty { get; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>磁盘介质类型的中文说明。</summary>
    /// <param name="mediaType">MSFT_PhysicalDisk.MediaType 数值。</param>
    /// <returns>中文说明。</returns>
    private static string MediaTypeText(long? mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "类型未知"
    };

    /// <summary>磁盘健康状态的中文说明。</summary>
    /// <param name="healthStatus">MSFT_PhysicalDisk.HealthStatus 数值。</param>
    /// <returns>中文说明。</returns>
    private static string HealthStatusText(long? healthStatus) => healthStatus switch
    {
        0 => "健康",
        1 => "警告",
        2 => "不健康",
        _ => "未知"
    };

    /// <summary>读取某个防火墙配置文件的启用状态。</summary>
    /// <param name="profile">配置文件名称（DomainProfile / StandardProfile / PublicProfile）。</param>
    /// <returns>中文状态文本。</returns>
    private static string? FirewallText(string profile)
    {
        var value = RegistryProbe.ReadInt(
            RegistryHive.LocalMachine,
            $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profile}",
            "EnableFirewall");

        return value switch
        {
            1 => "已启用",
            0 => "已关闭",
            _ => null
        };
    }

    /// <summary>网络接口操作状态的中文说明。</summary>
    /// <param name="status">网络接口状态。</param>
    /// <returns>中文说明。</returns>
    internal static string StatusText(OperationalStatus status) => status switch
    {
        OperationalStatus.Up => "已连接",
        OperationalStatus.Down => "已断开",
        OperationalStatus.Dormant => "休眠",
        OperationalStatus.NotPresent => "不存在",
        OperationalStatus.LowerLayerDown => "下层断开",
        _ => status.ToString()
    };

    /// <summary>判断名称或描述是否含 VPN 关键字。</summary>
    /// <param name="text">名称或描述。</param>
    /// <returns>含 VPN 关键字返回 true。</returns>
    internal static bool ContainsVpnKeyword(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] keywords = { "vpn", "tap-", "ppp", "wan miniport", "sstp", "l2tp", "ikev2", "wireguard" };
        return keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>安全地枚举网络接口；查询异常时按空集合处理，不中断整个分组。</summary>
    /// <returns>网络接口列表。</returns>
    internal static IReadOnlyList<NetworkInterface> SafeNetworkInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<NetworkInterface>();
        }
    }

    /// <summary>以顿号连接非空文本集合；全空时返回 null（记为不适用）。</summary>
    /// <param name="values">文本集合。</param>
    /// <returns>连接后的文本或 null。</returns>
    private static string? Join(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
        return list.Count == 0 ? null : string.Join("、", list);
    }

    /// <summary>构造一个字段：有值记为已采集，无值记为不适用。</summary>
    /// <param name="name">字段名。</param>
    /// <param name="value">字段值。</param>
    /// <returns>字段结果。</returns>
    private static CollectedField Field(string name, string? value)
        => value is null ? new CollectedField(name, null, FieldStatus.NotApplicable) : new CollectedField(name, value, FieldStatus.Collected);

    /// <summary>
    /// 枚举已安装软件（注册表 Uninstall 键，含 64 位、32 位与当前用户三处）。
    /// 仅读取名称、版本、发布者、安装日期与安装位置，不读取任何个人数据。
    /// </summary>
    /// <returns>已安装软件条目（已去重）。</returns>
    internal static IReadOnlyList<SoftwareEntry> EnumerateInstalledSoftware()
    {
        var results = new List<SoftwareEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var locations = new (RegistryHive Hive, string Key, RegistryView View)[]
        {
            (RegistryHive.LocalMachine, UninstallKey, RegistryView.Registry64),
            (RegistryHive.LocalMachine, UninstallKeyWow, RegistryView.Registry32),
            (RegistryHive.CurrentUser, UninstallKey, RegistryView.Default)
        };

        foreach (var (hive, key, view) in locations)
        {
            foreach (var subKeyName in RegistryProbe.ReadSubKeyNames(hive, key, view))
            {
                var path = $@"{key}\{subKeyName}";
                var name = RegistryProbe.ReadString(hive, path, "DisplayName", view);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // 跳过系统组件与更新补丁，仅保留 IT 人员关注的“已安装软件”。
                if (RegistryProbe.ReadInt(hive, path, "SystemComponent", view) == 1)
                {
                    continue;
                }

                if (RegistryProbe.ReadString(hive, path, "ParentKeyName", view) is not null)
                {
                    continue;
                }

                var version = RegistryProbe.ReadString(hive, path, "DisplayVersion", view);
                var keyId = $"{name}|{version}";
                if (!seen.Add(keyId))
                {
                    continue;
                }

                results.Add(new SoftwareEntry(
                    name,
                    version,
                    RegistryProbe.ReadString(hive, path, "Publisher", view),
                    RegistryProbe.ReadString(hive, path, "InstallDate", view) is { Length: 8 } raw
                        ? $"{raw[..4]}-{raw[4..6]}-{raw[6..8]}"
                        : null,
                    RegistryProbe.ReadString(hive, path, "InstallLocation", view)));
            }
        }

        return results;
    }

    /// <summary>已安装软件条目。</summary>
    /// <param name="Name">软件名称。</param>
    /// <param name="Version">版本。</param>
    /// <param name="Publisher">发布者。</param>
    /// <param name="InstallDate">安装日期。</param>
    /// <param name="InstallLocation">安装位置。</param>
    internal sealed record SoftwareEntry(
        string Name,
        string? Version,
        string? Publisher,
        string? InstallDate,
        string? InstallLocation);
}