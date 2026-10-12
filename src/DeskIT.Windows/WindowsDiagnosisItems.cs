using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DeskIT.Core;
using Microsoft.Win32;

namespace DeskIT.Windows;

/// <summary>
/// M3 诊断项清单：为 7 个问题场景构造可独立执行、独立超时的诊断项。
/// 全部检查均为本机只读检查（WMI / 注册表 / 网络接口 / 系统内置只读命令 / PowerShell 只读查询），
/// 不发起任何外网调用；域名解析等外网连通性类检查一律不执行，避免违反离线约束。
/// </summary>
internal static class WindowsDiagnosisItems
{
    private const string InternetSettingsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string UacKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

    /// <summary>危险/异常打印机状态码（Win32_Printer.PrinterStatus）。</summary>
    private static readonly int[] BadPrinterStatus = { 3, 4, 5, 6 };

    /// <summary>
    /// 构造全部诊断项（按场景顺序排列）。
    /// </summary>
    /// <param name="context">诊断上下文（数据目录、历史快照、PowerShell 执行器）。</param>
    /// <returns>诊断项清单。</returns>
    public static IReadOnlyList<DiagnosisItem> Build(DiagnosisContext context)
    {
        return new List<DiagnosisItem>
        {
            // ── 卡慢 ──────────────────────────────────────────────
            new(DiagnosisScene.Slowness, "slowness.cpu-top", "CPU 占用前几名进程", false, () =>
            {
                var cores = Math.Max(1, Environment.ProcessorCount);
                var rows = WmiQuery.Run(
                    "SELECT Name, IDProcess, PercentProcessorTime FROM Win32_PerfFormattedData_PerfProc_Process");

                var top = rows
                    .Where(r => (WmiQuery.Num(r, "IDProcess") ?? 0) > 0)
                    .Select(r => (Name: WmiQuery.Str(r, "Name") ?? "未知", Cpu: (WmiQuery.Num(r, "PercentProcessorTime") ?? 0) / (double)cores))
                    .OrderByDescending(x => x.Cpu)
                    .Take(5)
                    .ToList();

                if (top.Count == 0)
                {
                    return DiagnosisItemOutput.Unknown("未知：未能读取到进程性能数据。");
                }

                var evidence = top
                    .Select(x => new DiagnosisEvidence(x.Name, $"{x.Cpu:0}%"))
                    .ToArray();
                var worst = top[0];

                if (worst.Cpu >= 80)
                {
                    return DiagnosisItemOutput.Abnormal($"存在进程持续占用 CPU：{worst.Name}（{worst.Cpu:0}%）。", evidence)
                        .WithActions("slowness.end-heavy-process");
                }

                if (worst.Cpu >= 50)
                {
                    return DiagnosisItemOutput.Hint($"CPU 占用偏高：{worst.Name}（{worst.Cpu:0}%），建议确认是否异常。", evidence);
                }

                return DiagnosisItemOutput.Normal($"CPU 占用正常，最高为 {worst.Name}（{worst.Cpu:0}%）。", evidence);
            }),

            new(DiagnosisScene.Slowness, "slowness.memory", "内存压力与占用前几名进程", false, () =>
            {
                var os = WmiQuery.First("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                var total = (WmiQuery.Num(os ?? Empty, "TotalVisibleMemorySize") ?? 0) * 1024;
                var free = (WmiQuery.Num(os ?? Empty, "FreePhysicalMemory") ?? 0) * 1024;
                if (total <= 0)
                {
                    return DiagnosisItemOutput.Unknown("未知：未能读取到内存容量。");
                }

                var usedPercent = (total - free) * 100.0 / total;

                var top = WmiQuery.Run("SELECT Name, IDProcess, WorkingSetPrivate FROM Win32_PerfFormattedData_PerfProc_Process")
                    .Where(r => (WmiQuery.Num(r, "IDProcess") ?? 0) > 0)
                    .Select(r => (Name: WmiQuery.Str(r, "Name") ?? "未知", Memory: WmiQuery.Num(r, "WorkingSetPrivate") ?? 0))
                    .OrderByDescending(x => x.Memory)
                    .Take(3)
                    .Select(x => new DiagnosisEvidence(x.Name, ByteFormat.Format(x.Memory)))
                    .ToList();

                var evidence = new List<DiagnosisEvidence>
                {
                    new("内存使用率", $"{usedPercent:0}%（总 {ByteFormat.Format(total)} / 可用 {ByteFormat.Format(free)}）")
                };
                evidence.AddRange(top);

                if (usedPercent >= 90)
                {
                    return DiagnosisItemOutput.Abnormal($"内存压力过大：已用 {usedPercent:0}%（可用 {ByteFormat.Format(free)}）。", evidence.ToArray())
                        .WithActions("slowness.end-heavy-process");
                }

                if (usedPercent >= 85)
                {
                    return DiagnosisItemOutput.Hint($"内存使用率偏高：{usedPercent:0}%，建议关注占用前列的进程。", evidence.ToArray());
                }

                return DiagnosisItemOutput.Normal($"内存使用正常：{usedPercent:0}%（可用 {ByteFormat.Format(free)}）。", evidence.ToArray());
            }),

            new(DiagnosisScene.Slowness, "slowness.system-drive-space", "系统盘可用空间", false, () =>
            {
                var drive = TryGetDrive(SystemDriveRoot);
                if (drive is null)
                {
                    return DiagnosisItemOutput.Unknown("未知：系统盘未就绪，无法读取容量。");
                }

                var percentFree = drive.AvailableFreeSpace * 100.0 / Math.Max(1, drive.TotalSize);
                var evidence = new[]
                {
                    new DiagnosisEvidence(drive.Name.TrimEnd('\\'), $"总 {ByteFormat.Format(drive.TotalSize)} / 可用 {ByteFormat.Format(drive.AvailableFreeSpace)}（{percentFree:0}%）")
                };

                if (percentFree < 10 || drive.AvailableFreeSpace < 5L * 1024 * 1024 * 1024)
                {
                    return DiagnosisItemOutput.Abnormal($"系统盘空间不足：可用 {ByteFormat.Format(drive.AvailableFreeSpace)}（{percentFree:0}%）。", evidence)
                        .WithActions("disk.clean-temp-files");
                }

                if (percentFree < 20)
                {
                    return DiagnosisItemOutput.Hint($"系统盘空间偏低：可用 {ByteFormat.Format(drive.AvailableFreeSpace)}（{percentFree:0}%），建议清理。", evidence);
                }

                return DiagnosisItemOutput.Normal($"系统盘空间充足：可用 {ByteFormat.Format(drive.AvailableFreeSpace)}（{percentFree:0}%）。", evidence);
            }),

            new(DiagnosisScene.Slowness, "slowness.startup-items", "开机启动项数量", false, () =>
            {
                var items = WmiQuery.Run("SELECT Name, Location FROM Win32_StartupCommand");
                var names = items
                    .Select(r => WmiQuery.Str(r, "Name"))
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var evidence = new List<DiagnosisEvidence> { new("启动项数量", names.Count.ToString(CultureInfo.InvariantCulture)) };
                evidence.AddRange(names.Take(10).Select(n => new DiagnosisEvidence(n, "开机启动")));

                if (names.Count > 15)
                {
                    return DiagnosisItemOutput.Hint($"开机启动项偏多（{names.Count} 项），可能拖慢开机速度。", evidence.ToArray())
                        .WithActions("startup.trim-startup-items");
                }

                return DiagnosisItemOutput.Normal($"开机启动项数量正常（{names.Count} 项）。", evidence.ToArray());
            }),

            new(DiagnosisScene.Slowness, "slowness.system-event-errors", "最近 7 天系统错误日志", false, () =>
            {
                var result = Script(
                    context,
                    @"
$s = (Get-Date).AddDays(-7)
$e = Get-WinEvent -FilterHashtable @{LogName='System';Level=1,2;StartTime=$s} -ErrorAction SilentlyContinue
@($e).Count
",
                    TimeSpan.FromSeconds(20));

                return FromScript(result, "未知：未能读取系统事件日志（无输出）。", stdout =>
                {
                    if (!int.TryParse(stdout, out var count))
                    {
                        return DiagnosisItemOutput.Unknown($"未知：系统事件日志返回内容无法解析（{Shorten(stdout)}）。");
                    }

                    var evidence = new[] { new DiagnosisEvidence("最近 7 天错误/严重事件数", count.ToString(CultureInfo.InvariantCulture)) };
                    if (count >= 20)
                    {
                        return DiagnosisItemOutput.Abnormal($"最近 7 天系统错误/严重事件较多（{count} 条），建议查看事件查看器。", evidence);
                    }

                    if (count >= 1)
                    {
                        return DiagnosisItemOutput.Hint($"最近 7 天存在系统错误/严重事件（{count} 条），可结合现象确认。", evidence);
                    }

                    return DiagnosisItemOutput.Normal("最近 7 天未发现系统错误/严重事件。", evidence);
                });
            }),

            new(DiagnosisScene.Slowness, "slowness.power-plan", "电源计划", false, () =>
            {
                var output = ProcessCapture.Run("powercfg.exe", "/getactivescheme", TimeSpan.FromSeconds(10));
                if (string.IsNullOrWhiteSpace(output))
                {
                    return DiagnosisItemOutput.Unknown("未知：未能读取当前电源计划。");
                }

                var text = output.Trim();
                var evidence = new[] { new DiagnosisEvidence("当前电源计划", Shorten(text)) };

                if (text.Contains("节能", StringComparison.OrdinalIgnoreCase) || text.Contains("Power saver", StringComparison.OrdinalIgnoreCase))
                {
                    return DiagnosisItemOutput.Hint("当前为“节能”电源计划，CPU 性能会被限制，可能表现为卡慢。", evidence)
                        .WithActions("slowness.set-high-performance");
                }

                return DiagnosisItemOutput.Normal($"当前电源计划：{Shorten(text)}", evidence);
            }),

            new(DiagnosisScene.Slowness, "slowness.sfc-verify", "系统文件完整性（需管理员）", true, () =>
            {
                var result = Script(
                    context,
                    "& sfc.exe /verifyonly | Select-Object -Last 20 | Out-String",
                    TimeSpan.FromMinutes(3));

                return FromScript(result, "未知：系统文件完整性检查未返回结果（可能被中断）。", stdout =>
                {
                    var text = stdout.Trim();
                    if (text.Contains("未找到任何完整性冲突", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("did not find any integrity violations", StringComparison.OrdinalIgnoreCase))
                    {
                        return DiagnosisItemOutput.Normal("系统文件完整性检查通过：未找到完整性冲突。",
                            new DiagnosisEvidence("检查结果", Shorten(text)));
                    }

                    if (text.Contains("找到损坏文件", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("found integrity violations", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("无法修复", StringComparison.OrdinalIgnoreCase))
                    {
                        return DiagnosisItemOutput.Abnormal("系统存在损坏的系统文件。",
                            new DiagnosisEvidence("检查结果", Shorten(text)))
                            .WithActions("system.repair-system-files");
                    }

                    return DiagnosisItemOutput.Unknown($"未知：无法判定系统文件完整性检查结果（{Shorten(text)}）。");
                });
            }, TimeSpan.FromMinutes(3)),

            // ── 网络 ──────────────────────────────────────────────
            new(DiagnosisScene.Network, "network.adapter", "网卡状态与连接", false, () =>
            {
                var adapters = WindowsCollectionItems.SafeNetworkInterfaces();
                if (adapters.Count == 0)
                {
                    return DiagnosisItemOutput.Abnormal("未检测到任何网络适配器。");
                }

                var up = adapters.Where(a => a.OperationalStatus == OperationalStatus.Up).ToList();
                var evidence = adapters
                    .Select(a => new DiagnosisEvidence(a.Name, WindowsCollectionItems.StatusText(a.OperationalStatus)))
                    .Take(10)
                    .ToArray();

                return up.Count == 0
                    ? DiagnosisItemOutput.Abnormal("所有网络适配器均未连接（无已连接网卡）。", evidence)
                    : DiagnosisItemOutput.Normal($"已连接网卡 {up.Count} 个（共 {adapters.Count} 个适配器）。", evidence);
            }),

            new(DiagnosisScene.Network, "network.dhcp-address", "IP 地址与 DHCP 租约", false, () =>
            {
                var rows = WmiQuery.Run(
                    "SELECT Description, IPAddress, DHCPEnabled, DHCPLeaseObtained, DHCPLeaseExpires, DefaultIPGateway FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=True");

                if (rows.Count == 0)
                {
                    return DiagnosisItemOutput.Abnormal("未检测到已启用 IP 的网络配置（可能未获取到地址）。");
                }

                var evidence = new List<DiagnosisEvidence>();
                var hasValid = false;
                var hasApipa = false;

                foreach (var row in rows)
                {
                    var addresses = AsStringList(row.TryGetValue("IPAddress", out var raw) ? raw : null)
                        .Where(a => a.Contains('.'))
                        .ToList();

                    foreach (var address in addresses)
                    {
                        if (address.StartsWith("169.254.", StringComparison.Ordinal))
                        {
                            hasApipa = true;
                        }
                        else
                        {
                            hasValid = true;
                        }
                    }

                    var dhcp = string.Equals(WmiQuery.Str(row, "DHCPEnabled"), "True", StringComparison.OrdinalIgnoreCase);
                    var lease = WmiQuery.Date(row, "DHCPLeaseExpires");
                    evidence.Add(new DiagnosisEvidence(
                        WmiQuery.Str(row, "Description") ?? "网络适配器",
                        $"{(addresses.Count == 0 ? "无 IPv4 地址" : string.Join("、", addresses))}；DHCP {(dhcp ? "已启用" : "未启用")}" +
                        (lease is null ? string.Empty : $"；租约到期 {lease.Value.LocalDateTime:yyyy-MM-dd HH:mm}")));
                }

                if (hasApipa && !hasValid)
                {
                    return DiagnosisItemOutput.Abnormal("网卡仅获得 169.254.x.x 自动私有地址（未成功获取 DHCP 地址）。", evidence.ToArray())
                        .WithActions("network.renew-dhcp");
                }

                return hasValid
                    ? DiagnosisItemOutput.Normal("已获取有效 IPv4 地址。", evidence.ToArray())
                    : DiagnosisItemOutput.Hint("未获取到有效 IPv4 地址，请确认网线/无线连接与 DHCP 服务。", evidence.ToArray());
            }),

            new(DiagnosisScene.Network, "network.dns-hosts", "DNS 配置与 hosts 异常", false, () =>
            {
                var dnsServers = WindowsCollectionItems.SafeNetworkInterfaces()
                    .Where(a => a.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(a => a.GetIPProperties().DnsAddresses)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .Distinct()
                    .ToList();

                var evidence = new List<DiagnosisEvidence>
                {
                    new("DNS 服务器", dnsServers.Count == 0 ? "未配置" : string.Join("、", dnsServers))
                };

                var customHosts = ReadCustomHostsEntries();
                evidence.Add(new DiagnosisEvidence("hosts 自定义映射", customHosts.Count == 0 ? "无" : string.Join("、", customHosts.Take(10))));

                if (dnsServers.Count == 0)
                {
                    return DiagnosisItemOutput.Abnormal("未配置 DNS 服务器，域名解析将失败。", evidence.ToArray())
                        .WithActions("network.reset-dns");
                }

                if (customHosts.Count > 0)
                {
                    return DiagnosisItemOutput.Hint($"hosts 文件存在 {customHosts.Count} 条自定义映射，可能导致解析异常。", evidence.ToArray());
                }

                return DiagnosisItemOutput.Normal("DNS 服务器已配置，hosts 无自定义映射。", evidence.ToArray());
            }),

            new(DiagnosisScene.Network, "network.gateway", "网关连通性", false, () =>
            {
                var gateway = WindowsCollectionItems.SafeNetworkInterfaces()
                    .Where(a => a.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(a => a.GetIPProperties().GatewayAddresses)
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?.ToString();

                if (gateway is null)
                {
                    return DiagnosisItemOutput.Abnormal("未获取到默认网关，无法进行连通性检查。")
                        .WithActions("network.renew-dhcp");
                }

                var output = ProcessCapture.Run("ping.exe", $"-n 1 -w 1500 {gateway}", TimeSpan.FromSeconds(6));
                var evidence = new[] { new DiagnosisEvidence("默认网关", gateway) };

                if (output is null)
                {
                    return DiagnosisItemOutput.Unknown("未知：ping 命令超时未返回结果。", evidence);
                }

                var reachable = output.Contains("TTL=", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("时间=", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("time=", StringComparison.OrdinalIgnoreCase);

                return reachable
                    ? DiagnosisItemOutput.Normal($"默认网关 {gateway} 可连通。", evidence)
                    : DiagnosisItemOutput.Hint($"默认网关 {gateway} 未响应 ping（可能被防火墙拦截，需结合实际情况判断）。", evidence);
            }),

            new(DiagnosisScene.Network, "network.proxy", "代理配置", false, () =>
            {
                var enabled = RegistryProbe.ReadInt(RegistryHive.CurrentUser, InternetSettingsKey, "ProxyEnable");
                var server = RegistryProbe.ReadCurrentUserString(InternetSettingsKey, "ProxyServer");
                var autoConfig = RegistryProbe.ReadCurrentUserString(InternetSettingsKey, "AutoConfigURL");

                var evidence = new List<DiagnosisEvidence>
                {
                    new("代理开关", enabled == 1 ? "已启用" : "未启用"),
                    new("代理服务器", server ?? "未配置")
                };
                if (autoConfig is not null)
                {
                    evidence.Add(new DiagnosisEvidence("自动配置脚本", autoConfig));
                }

                if (enabled == 1)
                {
                    return DiagnosisItemOutput.Hint(
                        server is null ? "已启用系统代理但未配置代理服务器地址，可能导致网络访问异常。" : $"已启用系统代理（{server}），可能影响内网访问。",
                        evidence.ToArray());
                }

                if (autoConfig is not null)
                {
                    return DiagnosisItemOutput.Hint($"已配置代理自动配置脚本（{autoConfig}），可能影响网络访问。", evidence.ToArray());
                }

                return DiagnosisItemOutput.Normal("未启用系统代理。", evidence.ToArray());
            }),

            new(DiagnosisScene.Network, "network.vpn-residue", "VPN 适配器残留", false, () =>
            {
                var vpn = WindowsCollectionItems.SafeNetworkInterfaces()
                    .Where(a => WindowsCollectionItems.ContainsVpnKeyword(a.Name) || WindowsCollectionItems.ContainsVpnKeyword(a.Description))
                    .ToList();

                if (vpn.Count == 0)
                {
                    return DiagnosisItemOutput.Normal("未检出 VPN 适配器。");
                }

                var evidence = vpn
                    .Select(a => new DiagnosisEvidence(a.Name, WindowsCollectionItems.StatusText(a.OperationalStatus)))
                    .ToArray();
                var anyUp = vpn.Any(a => a.OperationalStatus == OperationalStatus.Up);

                return anyUp
                    ? DiagnosisItemOutput.Normal("VPN 适配器使用中。", evidence)
                    : DiagnosisItemOutput.Hint($"存在 {vpn.Count} 个未使用的 VPN 适配器，可能是卸载残留。", evidence);
            }),

            // ── 打印与共享 ────────────────────────────────────────
            new(DiagnosisScene.PrintingSharing, "printing.spooler", "打印后台处理服务", false, () =>
            {
                var service = WmiQuery.First("SELECT Name, State, StartMode FROM Win32_Service WHERE Name='Spooler'");
                if (service is null)
                {
                    return DiagnosisItemOutput.Unknown("未知：未查询到打印后台处理服务（Spooler）。");
                }

                var state = WmiQuery.Str(service, "State") ?? "未知";
                var evidence = new[] { new DiagnosisEvidence("Spooler 服务", $"{state}（启动类型 {WmiQuery.Str(service, "StartMode") ?? "未知"}）") };

                return string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase)
                    ? DiagnosisItemOutput.Normal("打印后台处理服务运行正常。", evidence)
                    : DiagnosisItemOutput.Abnormal($"打印后台处理服务未运行（{state}），将无法打印。", evidence)
                        .WithActions("printing.start-spooler");
            }),

            new(DiagnosisScene.PrintingSharing, "printing.queue", "打印队列任务", false, () =>
            {
                var jobs = WmiQuery.Run("SELECT Document, JobStatus, TotalPages FROM Win32_PrintJob");
                if (jobs.Count == 0)
                {
                    return DiagnosisItemOutput.Normal("打印队列为空。");
                }

                var blocked = jobs.Where(j =>
                {
                    var status = WmiQuery.Str(j, "JobStatus") ?? string.Empty;
                    return status.Contains("Error", StringComparison.OrdinalIgnoreCase)
                        || status.Contains("Blocked", StringComparison.OrdinalIgnoreCase)
                        || status.Contains("Offline", StringComparison.OrdinalIgnoreCase);
                }).ToList();

                var evidence = jobs
                    .Take(10)
                    .Select(j => new DiagnosisEvidence(WmiQuery.Str(j, "Document") ?? "打印任务", WmiQuery.Str(j, "JobStatus") ?? "排队中"))
                    .ToArray();

                if (blocked.Count > 0)
                {
                    return DiagnosisItemOutput.Abnormal($"打印队列存在 {blocked.Count} 个错误/卡死任务，需清理后重试。", evidence)
                        .WithActions("printing.clear-queue");
                }

                return jobs.Count >= 10
                    ? DiagnosisItemOutput.Hint($"打印队列积压 {jobs.Count} 个任务，可能存在卡死。", evidence)
                    : DiagnosisItemOutput.Normal($"打印队列有 {jobs.Count} 个任务，状态正常。", evidence);
            }),

            new(DiagnosisScene.PrintingSharing, "printing.offline-printers", "打印机脱机记录", false, () =>
            {
                var printers = WmiQuery.Run("SELECT Name, Default, WorkOffline, PrinterStatus FROM Win32_Printer");
                if (printers.Count == 0)
                {
                    return DiagnosisItemOutput.Normal("未安装任何打印机。");
                }

                var offline = printers.Where(p =>
                {
                    var workOffline = string.Equals(WmiQuery.Str(p, "WorkOffline"), "True", StringComparison.OrdinalIgnoreCase);
                    var status = (int?)(WmiQuery.Num(p, "PrinterStatus") ?? 0);
                    return workOffline || (status is not null && BadPrinterStatus.Contains(status.Value));
                }).ToList();

                var evidence = printers.Take(10)
                    .Select(p => new DiagnosisEvidence(
                        WmiQuery.Str(p, "Name") ?? "打印机",
                        string.Equals(WmiQuery.Str(p, "Default"), "True", StringComparison.OrdinalIgnoreCase) ? "默认打印机" : "已安装"))
                    .ToArray();

                return offline.Count > 0
                    ? DiagnosisItemOutput.Abnormal($"存在 {offline.Count} 台脱机或状态异常的打印机：{string.Join("、", offline.Select(p => WmiQuery.Str(p, "Name") ?? "未知"))}。", evidence)
                        .WithActions("printing.set-online")
                    : DiagnosisItemOutput.Normal($"共 {printers.Count} 台打印机，均处于联机状态。", evidence);
            }),

            new(DiagnosisScene.PrintingSharing, "printing.default-printer", "默认打印机", false, () =>
            {
                var printers = WmiQuery.Run("SELECT Name, Default FROM Win32_Printer");
                if (printers.Count == 0)
                {
                    return DiagnosisItemOutput.Normal("未安装任何打印机。");
                }

                var defaultPrinter = printers.FirstOrDefault(p =>
                    string.Equals(WmiQuery.Str(p, "Default"), "True", StringComparison.OrdinalIgnoreCase));

                if (defaultPrinter is null)
                {
                    return DiagnosisItemOutput.Hint("未设置默认打印机，打印时需手动选择打印机。")
                        .WithActions("printing.set-default");
                }

                return DiagnosisItemOutput.Normal(
                    $"默认打印机：{WmiQuery.Str(defaultPrinter, "Name") ?? "未知"}",
                    new DiagnosisEvidence("打印机数量", printers.Count.ToString(CultureInfo.InvariantCulture)));
            }),

            new(DiagnosisScene.PrintingSharing, "printing.share-services", "共享映射与共享服务状态", false, () =>
            {
                var mappings = RegistryProbe.ReadSubKeyNames(RegistryHive.CurrentUser, @"Network");
                var evidence = new List<DiagnosisEvidence>
                {
                    new("已映射网络驱动器", mappings.Count == 0 ? "无" : string.Join("、", mappings.Take(10)))
                };

                var stopped = new List<string>();
                var missing = new List<string>();
                foreach (var (name, label) in new[] { ("lanmanserver", "Server（文件与打印共享）"), ("lanmanworkstation", "Workstation（网络客户端）") })
                {
                    var service = WmiQuery.First($"SELECT State FROM Win32_Service WHERE Name='{name}'");
                    if (service is null)
                    {
                        missing.Add(label);
                        continue;
                    }

                    var state = WmiQuery.Str(service, "State") ?? "未知";
                    evidence.Add(new DiagnosisEvidence(label, state));
                    if (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
                    {
                        stopped.Add($"{label}（{state}）");
                    }
                }

                if (stopped.Count > 0)
                {
                    return DiagnosisItemOutput.Hint($"文件与打印共享相关服务未运行：{string.Join("、", stopped)}。", evidence.ToArray())
                        .WithActions("printing.start-share-services");
                }

                if (missing.Count > 0)
                {
                    return DiagnosisItemOutput.Unknown($"未知：未查询到服务 {string.Join("、", missing)}。", evidence.ToArray());
                }

                return DiagnosisItemOutput.Normal("文件与打印共享相关服务运行正常。", evidence.ToArray());
            }),

            // ── 磁盘与存储 ────────────────────────────────────────
            new(DiagnosisScene.DiskStorage, "disk.free-space", "各分区可用空间", false, () =>
            {
                var drives = FixedDrives();
                if (drives.Count == 0)
                {
                    return DiagnosisItemOutput.Unknown("未知：未读取到固定磁盘分区。");
                }

                var evidence = drives
                    .Select(d =>
                    {
                        var percent = d.AvailableFreeSpace * 100.0 / Math.Max(1, d.TotalSize);
                        return new DiagnosisEvidence(d.Name.TrimEnd('\\'), $"可用 {ByteFormat.Format(d.AvailableFreeSpace)}（{percent:0}%）");
                    })
                    .ToArray();

                var worst = drives
                    .OrderBy(d => d.AvailableFreeSpace * 100.0 / Math.Max(1, d.TotalSize))
                    .First();
                var worstPercent = worst.AvailableFreeSpace * 100.0 / Math.Max(1, worst.TotalSize);

                if (worstPercent < 10 || worst.AvailableFreeSpace < 5L * 1024 * 1024 * 1024)
                {
                    return DiagnosisItemOutput.Abnormal($"分区 {worst.Name.TrimEnd('\\')} 空间不足：可用 {ByteFormat.Format(worst.AvailableFreeSpace)}（{worstPercent:0}%）。", evidence)
                        .WithActions("disk.clean-temp-files");
                }

                if (worstPercent < 20)
                {
                    return DiagnosisItemOutput.Hint($"分区 {worst.Name.TrimEnd('\\')} 空间偏低：可用 {ByteFormat.Format(worst.AvailableFreeSpace)}（{worstPercent:0}%）。", evidence);
                }

                return DiagnosisItemOutput.Normal($"共 {drives.Count} 个分区，可用空间充足。", evidence);
            }),

            new(DiagnosisScene.DiskStorage, "disk.space-trend", "系统盘空间增长趋势（基于历史快照）", false, () =>
                AnalyzeSpaceTrend(context)),

            new(DiagnosisScene.DiskStorage, "disk.temp-size", "临时文件与缓存体积", false, () =>
            {
                var userTemp = Path.GetTempPath();
                var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
                var userSize = DirectorySize(userTemp);
                var systemSize = DirectorySize(windowsTemp);
                var total = userSize + systemSize;

                var evidence = new[]
                {
                    new DiagnosisEvidence("用户临时目录", ByteFormat.Format(userSize)),
                    new DiagnosisEvidence("系统临时目录", ByteFormat.Format(systemSize))
                };

                if (total > 5L * 1024 * 1024 * 1024)
                {
                    return DiagnosisItemOutput.Hint($"临时文件体积偏大（合计 {ByteFormat.Format(total)}），建议清理。", evidence)
                        .WithActions("disk.clean-temp-files");
                }

                return DiagnosisItemOutput.Normal($"临时文件体积正常（合计 {ByteFormat.Format(total)}）。", evidence);
            }),

            new(DiagnosisScene.DiskStorage, "disk.recycle-bin", "回收站体积", false, () =>
            {
                var total = FixedDrives().Sum(d => DirectorySize(Path.Combine(d.Name, "$Recycle.Bin")));
                var evidence = new[] { new DiagnosisEvidence("回收站体积", ByteFormat.Format(total)) };

                return total > 5L * 1024 * 1024 * 1024
                    ? DiagnosisItemOutput.Hint($"回收站占用偏大（{ByteFormat.Format(total)}），建议清空。", evidence)
                        .WithActions("disk.empty-recycle-bin")
                    : DiagnosisItemOutput.Normal($"回收站占用正常（{ByteFormat.Format(total)}）。", evidence);
            }),

            new(DiagnosisScene.DiskStorage, "disk.errors", "最近 7 天磁盘错误日志", false, () =>
            {
                var result = Script(
                    context,
                    @"
$s = (Get-Date).AddDays(-7)
$e = Get-WinEvent -FilterHashtable @{LogName='System';StartTime=$s} -ErrorAction SilentlyContinue
@($e | Where-Object { $_.ProviderName -match 'disk|Ntfs|volmgr' }).Count
",
                    TimeSpan.FromSeconds(20));

                return FromScript(result, "未知：未能读取磁盘错误日志（无输出）。", stdout =>
                {
                    if (!int.TryParse(stdout, out var count))
                    {
                        return DiagnosisItemOutput.Unknown($"未知：磁盘错误日志返回内容无法解析（{Shorten(stdout)}）。");
                    }

                    var evidence = new[] { new DiagnosisEvidence("最近 7 天磁盘相关事件", count.ToString(CultureInfo.InvariantCulture)) };
                    return count >= 1
                        ? DiagnosisItemOutput.Hint($"最近 7 天存在 {count} 条磁盘或文件系统相关事件，建议检查磁盘健康。", evidence)
                        : DiagnosisItemOutput.Normal("最近 7 天未发现磁盘相关错误事件。", evidence);
                });
            }),

            // ── 启动与系统 ────────────────────────────────────────
            new(DiagnosisScene.StartupSystem, "startup.boot-time", "开机耗时记录", false, () =>
            {
                var result = Script(
                    context,
                    @"
$e = Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Diagnostics-Performance/Operational';Id=100} -MaxEvents 1 -ErrorAction SilentlyContinue
if ($e) { $x = [xml]$e[0].ToXml(); ($x.Event.EventData.Data | Where-Object { $_.Name -eq 'BootTime' }).'#text' } else { '' }
",
                    TimeSpan.FromSeconds(20));

                if (result.TimedOut)
                {
                    return DiagnosisItemOutput.Unknown("未知（超时）：开机耗时查询未在规定时限内返回。");
                }

                var stdout = result.StandardOutput.Trim();
                if (stdout.Length == 0 || !int.TryParse(stdout, out var milliseconds))
                {
                    return DiagnosisItemOutput.Unknown("未知：未找到开机耗时记录（该诊断性能日志可能未启用或已滚动）。");
                }

                var seconds = milliseconds / 1000.0;
                var evidence = new[] { new DiagnosisEvidence("最近一次开机耗时", $"{seconds:0.0} 秒") };
                return seconds >= 60
                    ? DiagnosisItemOutput.Hint($"最近一次开机耗时较长（{seconds:0.0} 秒），建议精简启动项。", evidence)
                        .WithActions("startup.trim-startup-items")
                    : DiagnosisItemOutput.Normal($"最近一次开机耗时正常（{seconds:0.0} 秒）。", evidence);
            }, TimeSpan.FromSeconds(25)),

            new(DiagnosisScene.StartupSystem, "startup.scheduled-tasks", "计划任务异常项", false, () =>
            {
                var result = Script(
                    context,
                    @"
$bad = 0
Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.State -ne 'Disabled' } | ForEach-Object {
  $i = $_ | Get-ScheduledTaskInfo -ErrorAction SilentlyContinue
  if ($i -and $i.LastTaskResult -ne 0 -and $i.LastTaskResult -ne 267011) { $bad++ }
}
$bad
",
                    TimeSpan.FromSeconds(40));

                return FromScript(result, "未知：未能读取计划任务状态（无输出）。", stdout =>
                {
                    if (!int.TryParse(stdout, out var count))
                    {
                        return DiagnosisItemOutput.Unknown($"未知：计划任务返回内容无法解析（{Shorten(stdout)}）。");
                    }

                    var evidence = new[] { new DiagnosisEvidence("上次运行结果为失败的已启用任务数", count.ToString(CultureInfo.InvariantCulture)) };
                    return count >= 3
                        ? DiagnosisItemOutput.Hint($"有 {count} 个已启用计划任务上次运行失败，建议核对。", evidence)
                        : DiagnosisItemOutput.Normal("已启用计划任务上次运行结果正常。", evidence);
                });
            }, TimeSpan.FromSeconds(45)),

            new(DiagnosisScene.StartupSystem, "startup.unexpected-shutdown", "异常关机与蓝屏记录（最近 30 天）", false, () =>
            {
                var result = Script(
                    context,
                    @"
$s = (Get-Date).AddDays(-30)
$b = @(Get-WinEvent -FilterHashtable @{LogName='System';Id=1001;StartTime=$s} -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -eq 'Microsoft-Windows-WER-SystemErrorReporting' })
$p = @(Get-WinEvent -FilterHashtable @{LogName='System';Id=41;StartTime=$s} -ErrorAction SilentlyContinue)
'{0}|{1}' -f $b.Count, $p.Count
",
                    TimeSpan.FromSeconds(20));

                return FromScript(result, "未知：未能读取异常关机记录（无输出）。", stdout =>
                {
                    var parts = stdout.Split('|');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out var bugcheck) || !int.TryParse(parts[1], out var power))
                    {
                        return DiagnosisItemOutput.Unknown($"未知：异常关机记录返回内容无法解析（{Shorten(stdout)}）。");
                    }

                    var evidence = new[]
                    {
                        new DiagnosisEvidence("蓝屏记录", $"{bugcheck} 次"),
                        new DiagnosisEvidence("非正常关机记录", $"{power} 次")
                    };

                    if (bugcheck > 0)
                    {
                        return DiagnosisItemOutput.Abnormal($"最近 30 天存在 {bugcheck} 次蓝屏记录，建议检查驱动与硬件。", evidence)
                            .WithActions("startup.check-drivers");
                    }

                    return power > 0
                        ? DiagnosisItemOutput.Hint($"最近 30 天存在 {power} 次非正常关机记录（如断电或强制关机）。", evidence)
                        : DiagnosisItemOutput.Normal("最近 30 天未发现异常关机或蓝屏记录。", evidence);
                });
            }),

            new(DiagnosisScene.StartupSystem, "startup.driver-devices", "驱动异常设备", false, () =>
            {
                var rows = WmiQuery.Run("SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
                if (rows.Count == 0)
                {
                    return DiagnosisItemOutput.Normal("未发现驱动异常（存在问题的）设备。");
                }

                var evidence = rows
                    .Take(10)
                    .Select(r => new DiagnosisEvidence(WmiQuery.Str(r, "Name") ?? "未知设备", $"错误码 {WmiQuery.Num(r, "ConfigManagerErrorCode")}"))
                    .ToArray();

                return DiagnosisItemOutput.Abnormal($"存在 {rows.Count} 个驱动异常设备，需重新安装或更新驱动。", evidence)
                    .WithActions("startup.check-drivers");
            }),

            new(DiagnosisScene.StartupSystem, "startup.restore-points", "系统还原配置与还原点", false, () =>
            {
                var disabled = RegistryProbe.ReadInt(RegistryHive.LocalMachine, SystemRestoreKey, "DisableSR");
                if (disabled == 1)
                {
                    return DiagnosisItemOutput.Hint("系统还原已关闭，出现故障时无法回滚到先前状态。",
                        new DiagnosisEvidence("DisableSR", "1（已关闭）"))
                        .WithActions("system.enable-restore");
                }

                var shadow = WmiQuery.Run("SELECT ID FROM Win32_ShadowCopy");
                var evidence = new List<DiagnosisEvidence> { new("还原点数量", shadow.Count.ToString(CultureInfo.InvariantCulture)) };

                return shadow.Count == 0
                    ? DiagnosisItemOutput.Hint("系统还原已启用，但未发现可用还原点。", evidence.ToArray())
                    : DiagnosisItemOutput.Normal($"系统还原已启用，可用还原点 {shadow.Count} 个。", evidence.ToArray());
            }),

            // ── 软件 ──────────────────────────────────────────────
            new(DiagnosisScene.Software, "software.unresponsive", "无响应进程", false, () =>
            {
                // Win32_Process 无 Responding 属性，改用 PowerShell 的 Get-Process（其 Responding 基于窗口是否无响应判定）。
                var result = Script(
                    context,
                    @"
$p = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Responding -eq $false } | Select-Object -ExpandProperty ProcessName -Unique)
if ($p.Count -eq 0) { '0' } else { $p -join ';' }
",
                    TimeSpan.FromSeconds(20));

                return FromScript(result, "未知：未能获取进程响应状态（无输出）。", stdout =>
                {
                    if (string.Equals(stdout, "0", StringComparison.Ordinal))
                    {
                        return DiagnosisItemOutput.Normal("未发现无响应进程。");
                    }

                    var names = stdout
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var evidence = names.Take(10).Select(n => new DiagnosisEvidence(n, "无响应")).ToArray();
                    return DiagnosisItemOutput.Abnormal($"存在 {names.Count} 个无响应进程，可能造成卡死或假死。", evidence)
                        .WithActions("slowness.end-heavy-process");
                });
            }),

            new(DiagnosisScene.Software, "software.version-conflict", "同功能多版本冲突", false, () =>
            {
                var entries = WindowsCollectionItems.EnumerateInstalledSoftware();
                var conflicts = entries
                    .Where(e => e.Version is not null)
                    .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Select(e => e.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    .Select(g => new DiagnosisEvidence(g.Key, string.Join(" / ", g.Select(e => e.Version).Distinct())))
                    .ToList();

                return conflicts.Count == 0
                    ? DiagnosisItemOutput.Normal($"未发现同一软件的多版本共存（已检查 {entries.Count} 项已安装软件）。")
                    : DiagnosisItemOutput.Hint($"发现 {conflicts.Count} 个软件存在多版本共存，可能导致冲突。", conflicts.ToArray())
                        .WithActions("software.remove-old-version");
            }),

            new(DiagnosisScene.Software, "software.recent-timeline", "最近安装时间线（最近 30 天）", false, () =>
            {
                var since = DateTime.Today.AddDays(-30);
                var recent = WindowsCollectionItems.EnumerateInstalledSoftware()
                    .Where(e => e.InstallDate is { Length: 10 } && DateTime.TryParse(e.InstallDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date >= since)
                    .OrderByDescending(e => e.InstallDate, StringComparer.Ordinal)
                    .ToList();

                if (recent.Count == 0)
                {
                    return DiagnosisItemOutput.Normal("最近 30 天未记录到新增安装的软件。",
                        new DiagnosisEvidence("说明", "本项基于软件安装日期判断；卸载记录不保留，故仅反映新增安装。"));
                }

                var evidence = recent
                    .Take(15)
                    .Select(e => new DiagnosisEvidence($"{e.Name} {e.Version}".Trim(), e.InstallDate))
                    .ToArray();
                return DiagnosisItemOutput.Normal($"最近 30 天新增安装 {recent.Count} 项，可结合故障出现时间核对。", evidence);
            }),

            new(DiagnosisScene.Software, "software.residue", "软件残留目录", false, () =>
            {
                var missing = WindowsCollectionItems.EnumerateInstalledSoftware()
                    .Where(e => !string.IsNullOrWhiteSpace(e.InstallLocation))
                    .Where(e => e.InstallLocation!.Contains(':') && !Directory.Exists(e.InstallLocation))
                    .Select(e => new DiagnosisEvidence(e.Name, e.InstallLocation!))
                    .ToList();

                return missing.Count == 0
                    ? DiagnosisItemOutput.Normal("未发现安装位置指向不存在目录的软件。")
                    : DiagnosisItemOutput.Hint($"发现 {missing.Count} 项软件的安装目录已不存在，可能是卸载残留。", missing.Take(10).ToArray())
                        .WithActions("software.clean-residue");
            }),

            // ── 账号与权限 ────────────────────────────────────────
            new(DiagnosisScene.AccountPrivilege, "account.lockout-events", "账号锁定事件（最近 7 天，需管理员）", true, () =>
            {
                var result = Script(
                    context,
                    @"
$s = (Get-Date).AddDays(-7)
@(Get-WinEvent -FilterHashtable @{LogName='Security';Id=4740;StartTime=$s} -ErrorAction SilentlyContinue).Count
",
                    TimeSpan.FromSeconds(25));

                return FromScript(result, "未知：未能读取安全日志（无输出）。", stdout =>
                {
                    if (!int.TryParse(stdout, out var count))
                    {
                        return DiagnosisItemOutput.Unknown($"未知：账号锁定事件返回内容无法解析（{Shorten(stdout)}）。");
                    }

                    var evidence = new[] { new DiagnosisEvidence("最近 7 天账号锁定次数", count.ToString(CultureInfo.InvariantCulture)) };
                    return count > 0
                        ? DiagnosisItemOutput.Hint($"最近 7 天发生 {count} 次账号锁定，需确认是否密码错误或存在暴力尝试。", evidence)
                        : DiagnosisItemOutput.Normal("最近 7 天未发现账号锁定事件。", evidence);
                });
            }, TimeSpan.FromSeconds(30)),

            new(DiagnosisScene.AccountPrivilege, "account.password-policy", "密码策略与锁定阈值", false, () =>
            {
                var output = ProcessCapture.Run("net.exe", "accounts", TimeSpan.FromSeconds(10));
                if (string.IsNullOrWhiteSpace(output))
                {
                    return DiagnosisItemOutput.Unknown("未知：未能读取密码策略（需要权限或命令不可用）。");
                }

                var text = output.Trim();
                var evidence = new[]
                {
                    new DiagnosisEvidence("密码策略输出", Shorten(text))
                };

                var lockout = ExtractNetAccountsValue(text, "锁定阈值", "Lockout threshold");
                if (lockout is not null && (lockout.StartsWith("0", StringComparison.Ordinal) || lockout.Contains("从不", StringComparison.OrdinalIgnoreCase)))
                {
                    return DiagnosisItemOutput.Hint("未配置账号锁定阈值（锁定阈值=0），密码可被无限次尝试。", evidence);
                }

                return DiagnosisItemOutput.Normal(lockout is null ? "密码策略已读取，锁定阈值未提供。" : $"账号锁定阈值：{lockout}。", evidence);
            }),

            new(DiagnosisScene.AccountPrivilege, "account.admin-members", "管理员组成员", false, () =>
            {
                var host = Environment.MachineName;
                var rows = WmiQuery.Run(
                    $"SELECT PartComponent FROM Win32_GroupUser WHERE GroupComponent=\"Win32_Group.Domain='{host}',Name='Administrators'\"");
                if (rows.Count == 0)
                {
                    return DiagnosisItemOutput.Unknown("未知：未能读取本地管理员组成员（可能需要权限）。");
                }

                var members = rows
                    .Select(r => Regex.Match(WmiQuery.Str(r, "PartComponent") ?? string.Empty, "Name=\"(?<n>[^\"]+)\""))
                    .Where(m => m.Success)
                    .Select(m => m.Groups["n"].Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var evidence = members.Select(m => new DiagnosisEvidence(m, "管理员组成员")).ToArray();
                return members.Count > 3
                    ? DiagnosisItemOutput.Hint($"本地管理员组成员偏多（{members.Count} 个），建议收敛权限。", evidence)
                    : DiagnosisItemOutput.Normal($"本地管理员组成员 {members.Count} 个（{string.Join("、", members)}）。", evidence);
            }),

            new(DiagnosisScene.AccountPrivilege, "account.uac", "UAC 设置", false, () =>
            {
                var enabledLua = RegistryProbe.ReadInt(RegistryHive.LocalMachine, UacKey, "EnableLUA");
                if (enabledLua is null)
                {
                    return DiagnosisItemOutput.Unknown("未知：未能读取 UAC 设置。");
                }

                var behavior = RegistryProbe.ReadInt(RegistryHive.LocalMachine, UacKey, "ConsentPromptBehaviorAdmin");
                var evidence = new[]
                {
                    new DiagnosisEvidence("EnableLUA", enabledLua.Value.ToString(CultureInfo.InvariantCulture)),
                    new DiagnosisEvidence("管理员提权行为", behavior?.ToString(CultureInfo.InvariantCulture) ?? "未配置")
                };

                return enabledLua == 0
                    ? DiagnosisItemOutput.Abnormal("UAC 已关闭，程序可静默提权，存在安全风险。", evidence)
                        .WithActions("account.enable-uac")
                    : DiagnosisItemOutput.Normal("UAC 已启用。", evidence);
            }),

            new(DiagnosisScene.AccountPrivilege, "account.cached-credentials", "共享凭据残留", false, () =>
            {
                var output = ProcessCapture.Run("cmdkey.exe", "/list", TimeSpan.FromSeconds(8));
                if (output is null)
                {
                    return DiagnosisItemOutput.Unknown("未知：未能读取已保存的凭据列表。");
                }

                var count = Regex.Matches(output, @"(?m)^\s*(Target|目标)\s*[:：]").Count;
                var evidence = new[] { new DiagnosisEvidence("已保存凭据条目", count.ToString(CultureInfo.InvariantCulture)) };

                return count > 0
                    ? DiagnosisItemOutput.Hint($"发现 {count} 条已保存的共享凭据，可能造成访问冲突或凭据残留。", evidence)
                        .WithActions("account.clear-cached-credentials")
                    : DiagnosisItemOutput.Normal("未发现已保存的共享凭据。", evidence);
            }),

            new(DiagnosisScene.AccountPrivilege, "account.time-source", "机器时间与时间源", false, () =>
            {
                var status = ProcessCapture.Run("w32tm.exe", "/query /status", TimeSpan.FromSeconds(10));
                var service = WmiQuery.First("SELECT State FROM Win32_Service WHERE Name='W32Time'");
                var serviceState = WmiQuery.Str(service ?? Empty, "State") ?? "未知";

                if (status is null)
                {
                    return DiagnosisItemOutput.Unknown("未知：未能读取时间服务状态（w32tm 未返回结果）。",
                        new DiagnosisEvidence("Windows 时间服务", serviceState));
                }

                var text = status.Trim();
                var source = ExtractW32tmValue(text, "源", "Source") ?? "未知";
                var lastSync = ExtractW32tmValue(text, "上次成功同步时间", "Last Successful Sync Time");
                var evidence = new[]
                {
                    new DiagnosisEvidence("Windows 时间服务", serviceState),
                    new DiagnosisEvidence("时间源", source),
                    new DiagnosisEvidence("上次成功同步", lastSync ?? "未提供")
                };

                if (!string.Equals(serviceState, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    return DiagnosisItemOutput.Hint($"Windows 时间服务未运行（{serviceState}），机器时间可能偏差。", evidence);
                }

                if (source.Contains("Local CMOS", StringComparison.OrdinalIgnoreCase) || source.Contains("Free-running", StringComparison.OrdinalIgnoreCase))
                {
                    return DiagnosisItemOutput.Hint("机器时间未与外部时间源同步（使用本机 CMOS 时钟）。", evidence);
                }

                return DiagnosisItemOutput.Normal($"时间源正常（{source}）。", evidence);
            })
        };
    }

    /// <summary>空字典，用于 WMI 查询无结果时的统一取值路径。</summary>
    private static IReadOnlyDictionary<string, object?> Empty { get; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>系统盘根目录（如 C:\）。</summary>
    private static string SystemDriveRoot => Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    /// <summary>
    /// 分析系统盘空间增长趋势：以保留的历史快照中最早与最近两份的系统盘可用空间做比较。
    /// </summary>
    /// <param name="context">诊断上下文。</param>
    /// <returns>诊断项输出。</returns>
    private static DiagnosisItemOutput AnalyzeSpaceTrend(DiagnosisContext context)
    {
        var snapshots = context.RecentSnapshots;
        if (snapshots.Count < 2)
        {
            return DiagnosisItemOutput.Unknown($"未知：历史快照不足 2 份（当前 {snapshots.Count} 份），无法比较空间趋势。",
                new DiagnosisEvidence("已保留快照", snapshots.Count.ToString(CultureInfo.InvariantCulture)));
        }

        var first = ReadSystemDriveFree(snapshots[0]);
        var last = ReadSystemDriveFree(snapshots[^1]);
        if (first is null || last is null)
        {
            return DiagnosisItemOutput.Unknown("未知：历史快照中未记录系统盘可用空间，无法比较趋势。");
        }

        var delta = first.Value - last.Value;
        var evidence = new[]
        {
            new DiagnosisEvidence("观察期", $"{snapshots[0].CollectedAt.LocalDateTime:yyyy-MM-dd HH:mm} → {snapshots[^1].CollectedAt.LocalDateTime:yyyy-MM-dd HH:mm}"),
            new DiagnosisEvidence("期初系统盘可用", ByteFormat.Format(first.Value)),
            new DiagnosisEvidence("最近系统盘可用", ByteFormat.Format(last.Value)),
            new DiagnosisEvidence("变化量", delta >= 0 ? $"减少 {ByteFormat.Format(delta)}" : $"增加 {ByteFormat.Format(-delta)}")
        };

        return delta > 5L * 1024 * 1024 * 1024
            ? DiagnosisItemOutput.Hint($"系统盘可用空间在观察期内减少 {ByteFormat.Format(delta)}，建议排查增长来源。", evidence)
            : DiagnosisItemOutput.Normal("系统盘可用空间在观察期内基本稳定。", evidence);
    }

    /// <summary>
    /// 从一份快照的分区明细中读取系统盘可用空间。
    /// </summary>
    /// <param name="snapshot">历史快照。</param>
    /// <returns>可用字节数；无法解析时返回 null。</returns>
    private static double? ReadSystemDriveFree(MachineSnapshot snapshot)
    {
        var storage = snapshot.Groups.FirstOrDefault(g => g.Group == CollectionGroup.Storage);
        if (storage is null)
        {
            return null;
        }

        var letter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        foreach (var line in storage.Details)
        {
            var match = Regex.Match(line, @"^(?<letter>[A-Za-z]:)\s+总\s+(?<total>[\d.]+\s*[A-Z]*B)\s*/\s+可用\s+(?<free>[\d.]+\s*[A-Z]*B)\s*/");
            if (match.Success && string.Equals(match.Groups["letter"].Value, letter, StringComparison.OrdinalIgnoreCase))
            {
                return ParseByteText(match.Groups["free"].Value);
            }
        }

        return null;
    }

    /// <summary>把“476.9 GB”一类的可读体积文本解析为字节数。</summary>
    /// <param name="text">体积文本。</param>
    /// <returns>字节数；无法解析时返回 null。</returns>
    private static double? ParseByteText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text.Trim(), @"^(?<v>\d+(?:\.\d+)?)\s*(?<u>B|KB|MB|GB|TB)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var value = double.Parse(match.Groups["v"].Value, CultureInfo.InvariantCulture);
        return match.Groups["u"].Value.ToUpperInvariant() switch
        {
            "TB" => value * 1024 * 1024 * 1024 * 1024,
            "GB" => value * 1024 * 1024 * 1024,
            "MB" => value * 1024 * 1024,
            "KB" => value * 1024,
            _ => value
        };
    }

    /// <summary>
    /// 读取 hosts 文件中的自定义映射（忽略注释与空行，忽略 localhost 默认项）。
    /// </summary>
    /// <returns>自定义映射文本列表。</returns>
    private static IReadOnlyList<string> ReadCustomHostsEntries()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers",
            "etc",
            "hosts");

        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }

            return File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .Where(line => !line.Contains("localhost", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 计算目录体积（仅统计文件大小，不读取任何文件内容）。逐目录容错，遇到无权限目录跳过。
    /// </summary>
    /// <param name="path">目录路径。</param>
    /// <param name="maxDepth">最大递归深度，避免极深目录拖慢诊断。</param>
    /// <returns>目录体积（字节）；目录不存在时返回 0。</returns>
    private static long DirectorySize(string path, int maxDepth = 6)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        var total = 0L;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((path, 0));

        while (stack.Count > 0)
        {
            var (directory, depth) = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 单个文件不可读：跳过。
                    }
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    stack.Push((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                // 目录不可读：跳过，不影响其他目录。
            }
        }

        return total;
    }

    /// <summary>枚举固定磁盘中已就绪的分区。</summary>
    /// <returns>分区列表。</returns>
    private static IReadOnlyList<DriveInfo> FixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<DriveInfo>();
        }
    }

    /// <summary>按根路径安全获取磁盘信息。</summary>
    /// <param name="root">根路径（如 C:\）。</param>
    /// <returns>磁盘信息；不可用时返回 null。</returns>
    private static DriveInfo? TryGetDrive(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>把 WMI 数组属性转换为文本列表。</summary>
    /// <param name="value">属性原始值。</param>
    /// <returns>文本列表。</returns>
    private static IReadOnlyList<string> AsStringList(object? value) => value switch
    {
        string[] array => array.Where(v => !string.IsNullOrWhiteSpace(v)).ToList(),
        string single when !string.IsNullOrWhiteSpace(single) => new List<string> { single },
        _ => Array.Empty<string>()
    };

    /// <summary>执行一段 PowerShell 只读查询脚本。</summary>
    /// <param name="context">诊断上下文。</param>
    /// <param name="script">脚本文本。</param>
    /// <param name="timeout">脚本超时。</param>
    /// <returns>执行结果。</returns>
    private static PowerShellExecutionResult Script(DiagnosisContext context, string script, TimeSpan timeout)
        => context.PowerShell.ExecuteAsync(script, timeout, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// 把脚本执行结果转换为诊断项输出：超时记“未知（超时）”，无输出按给定原因记“未知”，否则交由解析函数判定。
    /// </summary>
    /// <param name="result">脚本执行结果。</param>
    /// <param name="emptyReason">无输出时的原因说明。</param>
    /// <param name="parse">标准输出解析函数。</param>
    /// <returns>诊断项输出。</returns>
    private static DiagnosisItemOutput FromScript(
        PowerShellExecutionResult result,
        string emptyReason,
        Func<string, DiagnosisItemOutput> parse)
    {
        if (result.TimedOut)
        {
            return DiagnosisItemOutput.Unknown("未知（超时）：该检查未在规定时限内返回。");
        }

        var stdout = result.StandardOutput.Trim();
        if (stdout.Length == 0)
        {
            return DiagnosisItemOutput.Unknown(emptyReason);
        }

        return parse(stdout);
    }

    /// <summary>截断过长的证据文本，避免界面与报告被超长内容撑开。</summary>
    /// <param name="text">原始文本。</param>
    /// <returns>截断后的文本。</returns>
    private static string Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "—";
        }

        var single = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= 200 ? single : single[..200] + "…";
    }

    /// <summary>
    /// 从 net accounts 输出中提取指定项的值（兼容中文与英文系统）。
    /// </summary>
    /// <param name="text">命令输出。</param>
    /// <param name="chineseLabel">中文标签。</param>
    /// <param name="englishLabel">英文标签。</param>
    /// <returns>值文本；未找到时返回 null。</returns>
    private static string? ExtractNetAccountsValue(string text, string chineseLabel, string englishLabel)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(chineseLabel, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(englishLabel, StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 ? parts[^1] : null;
            }
        }

        return null;
    }

    /// <summary>
    /// 从 w32tm /query /status 输出中提取指定项的值（兼容中文与英文系统）。
    /// </summary>
    /// <param name="text">命令输出。</param>
    /// <param name="chineseLabel">中文标签。</param>
    /// <param name="englishLabel">英文标签。</param>
    /// <returns>值文本；未找到时返回 null。</returns>
    private static string? ExtractW32tmValue(string text, string chineseLabel, string englishLabel)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOfAny(new[] { ':', '：' });
            if (separator <= 0)
            {
                continue;
            }

            var label = trimmed[..separator].Trim();
            if (label.Equals(chineseLabel, StringComparison.OrdinalIgnoreCase)
                || label.Equals(englishLabel, StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[(separator + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }
}