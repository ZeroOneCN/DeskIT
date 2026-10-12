using System.Text;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// reports 目录中的一个报告文件信息（供界面“报告查看器”使用）。
/// </summary>
/// <param name="FileName">文件名（含扩展名）。</param>
/// <param name="FullPath">文件绝对路径。</param>
/// <param name="SizeBytes">文件大小（字节）。</param>
/// <param name="ModifiedAt">最近修改时间。</param>
public sealed record ReportFileInfo(string FileName, string FullPath, long SizeBytes, DateTimeOffset ModifiedAt);

/// <summary>
/// 报告写出（M7）。在 U 盘 reports 目录下按命名规则生成自包含 HTML 与同名 Markdown
/// （默认脱敏版本）；任何 IO 异常都转为 <see cref="ReportWriteResult.Success"/> = false，不抛出（“生成失败不阻断”）。
/// </summary>
public interface IReportWriter
{
    /// <summary>
    /// 为一条处理记录生成报告（HTML + Markdown，脱敏版本）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录（reports 目录取其同级）。</param>
    /// <param name="record">处理记录。</param>
    /// <returns>报告产出结果；失败时 Success=false 且给出 Error。</returns>
    ReportWriteResult WriteReports(string dataDirectory, HandlingRecord record);

    /// <summary>
    /// 读取 reports 目录下的报告文件清单（HTML 与 Markdown），供界面报告查看器使用。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录（reports 目录取其同级）。</param>
    /// <returns>报告文件清单（按修改时间倒序）；目录不存在时返回空列表。</returns>
    IReadOnlyList<ReportFileInfo> ListReports(string dataDirectory);
}

/// <inheritdoc cref="IReportWriter" />
public sealed class ReportWriter : IReportWriter
{
    /// <summary>报告目录名（与 data 目录同级）。</summary>
    private const string ReportsFolderName = "reports";

    /// <inheritdoc />
    public ReportWriteResult WriteReports(string dataDirectory, HandlingRecord record)
    {
        try
        {
            var root = ResolveReportsRoot(dataDirectory);
            Directory.CreateDirectory(root);

            var stem = LedgerRules.BuildReportFileName(record);

            // 重复生成同一记录时以最新一次为准，并保留历史版本：
            // 若已存在同名文件则按 -v2、-v3 … 递增，HTML 与 Markdown 使用同一版本号，保证成对。
            var version = 1;
            string Candidate() => version <= 1 ? stem : $"{stem}-v{version}";
            while (File.Exists(Path.Combine(root, Candidate() + ".html")) || File.Exists(Path.Combine(root, Candidate() + ".md")))
            {
                version++;
            }

            var name = Candidate();
            var htmlName = name + ".html";
            var markdownName = name + ".md";

            // 静默产出固定脱敏；写出 UTF-8（带 BOM 便于直接用浏览器/编辑器正确识别中文）。
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(Path.Combine(root, htmlName), LedgerReportBuilder.BuildHtml(record, desensitized: true), encoding);
            File.WriteAllText(Path.Combine(root, markdownName), LedgerReportBuilder.BuildMarkdown(record, desensitized: true), encoding);

            return new ReportWriteResult(true, htmlName, markdownName, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new ReportWriteResult(false, null, null, $"报告生成失败：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ReportFileInfo> ListReports(string dataDirectory)
    {
        var root = ResolveReportsRoot(dataDirectory);
        if (!Directory.Exists(root))
        {
            return Array.Empty<ReportFileInfo>();
        }

        var files = new List<ReportFileInfo>();
        foreach (var path in Directory.EnumerateFiles(root))
        {
            var extension = Path.GetExtension(path);
            if (!string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                files.Add(new ReportFileInfo(info.Name, info.FullName, info.Length, info.LastWriteTime));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件信息读取失败不影响其他文件。
            }
        }

        return files.OrderByDescending(file => file.ModifiedAt).ToList();
    }

    /// <summary>
    /// 由数据目录解析报告目录（reports 与 data 目录同级）。
    /// </summary>
    /// <param name="dataDirectory">数据目录。</param>
    /// <returns>报告目录绝对路径。</returns>
    private static string ResolveReportsRoot(string dataDirectory)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(dataDirectory);
        var parent = Path.GetDirectoryName(trimmed);
        var baseDirectory = string.IsNullOrEmpty(parent) ? trimmed : parent;
        return Path.Combine(baseDirectory, ReportsFolderName);
    }
}