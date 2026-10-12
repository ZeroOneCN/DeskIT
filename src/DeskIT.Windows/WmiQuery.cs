using System.Globalization;
using System.Management;

namespace DeskIT.Windows;

/// <summary>
/// WMI / CIM 只读查询的最小封装（M2 采集用）。
/// 所有查询均在目标机本地执行，不使用远程协议；查询异常（含权限拒绝、命名空间缺失）一律交由调用方处理，
/// 以便按“无权限读取 / 不适用 / 未知”如实归因。
/// </summary>
internal static class WmiQuery
{
    /// <summary>默认命名空间。</summary>
    public const string DefaultScope = @"root\cimv2";

    /// <summary>
    /// 执行 WQL 查询，返回每行的属性字典（属性名不区分大小写）。
    /// </summary>
    /// <param name="wql">WQL 查询语句。</param>
    /// <param name="scope">命名空间，默认 root\cimv2。</param>
    /// <returns>结果行集合。</returns>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Run(string wql, string scope = DefaultScope)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using var searcher = new ManagementObjectSearcher(NormalizeScope(scope), wql);
        using var results = searcher.Get();

        foreach (ManagementBaseObject item in results)
        {
            using (item)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData property in item.Properties)
                {
                    row[property.Name] = property.Value;
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>
    /// 取查询结果的第一行。
    /// </summary>
    /// <param name="wql">WQL 查询语句。</param>
    /// <param name="scope">命名空间。</param>
    /// <returns>首行字典；无结果时返回 null。</returns>
    public static IReadOnlyDictionary<string, object?>? First(string wql, string scope = DefaultScope)
    {
        var rows = Run(wql, scope);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// 读取字符串属性。
    /// </summary>
    /// <param name="row">结果行。</param>
    /// <param name="property">属性名。</param>
    /// <returns>去空白后的字符串；空值或缺失时返回 null。</returns>
    public static string? Str(IReadOnlyDictionary<string, object?> row, string property)
    {
        if (!row.TryGetValue(property, out var value) || value is null)
        {
            return null;
        }

        var text = value switch
        {
            string s => s,
            string[] array => string.Join("；", array.Where(x => !string.IsNullOrWhiteSpace(x))),
            _ => value.ToString()
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// 读取 64 位整数属性。
    /// </summary>
    /// <param name="row">结果行。</param>
    /// <param name="property">属性名。</param>
    /// <returns>数值；无法转换时返回 null。</returns>
    public static long? Num(IReadOnlyDictionary<string, object?> row, string property)
    {
        if (!row.TryGetValue(property, out var value) || value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// 读取日期时间属性。WMI 的时间可能以 DMTF 原始串（20251101000000.000000+480）返回，
    /// 也可能已被 System.Management 转换为按当前区域格式化的日期串（如 11/1/2025），两种形式都需兼容。
    /// </summary>
    /// <param name="row">结果行。</param>
    /// <param name="property">属性名。</param>
    /// <returns>时间；无法解析时返回 null。</returns>
    public static DateTimeOffset? Date(IReadOnlyDictionary<string, object?> row, string property)
    {
        if (!row.TryGetValue(property, out var value) || value is null)
        {
            return null;
        }

        return value is DateTime dateTime
            ? new DateTimeOffset(dateTime)
            : ParseDate(value.ToString());
    }

    /// <summary>
    /// 解析 WMI 日期时间文本。
    /// </summary>
    /// <param name="text">日期时间文本。</param>
    /// <returns>时间；无法解析时返回 null。</returns>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();

        // 形式一：DMTF 原始串，取前 14 位 yyyyMMddHHmmss。
        if (text.Length >= 14
            && text[..14].All(char.IsAsciiDigit)
            && DateTime.TryParseExact(text[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dmtf))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dmtf, DateTimeKind.Local));
        }

        // 形式二：已按区域格式化的日期串。
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local));
        }

        return null;
    }

    /// <summary>把命名空间规范为指向本机的完整路径，避免因相对路径解析差异导致查询失败。</summary>
    /// <param name="scope">命名空间（如 root\cimv2）。</param>
    /// <returns>形如 \\.\root\cimv2 的完整路径。</returns>
    private static string NormalizeScope(string scope)
        => scope.StartsWith(@"\\", StringComparison.Ordinal) ? scope : $@"\\.\{scope}";
}

/// <summary>字节数的可读格式化。</summary>
internal static class ByteFormat
{
    /// <summary>
    /// 把字节数格式化为便于阅读的字符串。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>如“476.9 GB”“512 MB”。</returns>
    public static string Format(double bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        if (bytes >= gb)
        {
            return $"{bytes / gb:0.0} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:0} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:0} KB";
        }

        return $"{bytes:0} B";
    }
}