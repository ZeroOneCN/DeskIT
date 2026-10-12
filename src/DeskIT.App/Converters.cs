using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DeskIT.Core;

namespace DeskIT.App;

/// <summary>把自检项结果状态转换为徽标背景色。</summary>
public sealed class SelfCheckOutcomeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush PassBrush = new(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly SolidColorBrush WarnBrush = new(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush FailBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));

    /// <summary>按结果状态返回背景色：通过=绿、警示=橙、失败=红。</summary>
    /// <param name="value">SelfCheckOutcome 值。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="culture">区域信息。</param>
    /// <returns>对应的画刷。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            SelfCheckOutcome.Pass => PassBrush,
            SelfCheckOutcome.Warn => WarnBrush,
            SelfCheckOutcome.Fail => FailBrush,
            _ => WarnBrush
        };

    /// <summary>不支持反向转换。</summary>
    /// <param name="value">值。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">参数。</param>
    /// <param name="culture">区域信息。</param>
    /// <returns>不返回。</returns>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把采集分组状态转换为徽标背景色。</summary>
public sealed class CollectionGroupStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CollectedBrush = new(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly SolidColorBrush PartialBrush = new(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush TimeoutBrush = new(Color.FromRgb(0x7C, 0x3A, 0xED));
    private static readonly SolidColorBrush SkippedBrush = new(Color.FromRgb(0x9C, 0xA3, 0xAF));

    /// <summary>按分组状态返回背景色：已采集=绿、部分可用=橙、超时=紫、已跳过=灰。</summary>
    /// <param name="value">CollectionGroupStatus 值。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="culture">区域信息。</param>
    /// <returns>对应的画刷。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            CollectionGroupStatus.Collected => CollectedBrush,
            CollectionGroupStatus.Partial => PartialBrush,
            CollectionGroupStatus.Timeout => TimeoutBrush,
            CollectionGroupStatus.Skipped => SkippedBrush,
            _ => SkippedBrush
        };

    /// <summary>不支持反向转换。</summary>
    /// <param name="value">值。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">参数。</param>
    /// <param name="culture">区域信息。</param>
    /// <returns>不返回。</returns>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把自检项结果状态转换为中文徽标文本。</summary>
public sealed class SelfCheckOutcomeToTextConverter : IValueConverter
{
    /// <summary>按结果状态返回“通过/警示/失败”。</summary>
    /// <param name="value">SelfCheckOutcome 值。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="culture">区域信息。</param>
    /// <returns>中文状态文本。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            SelfCheckOutcome.Pass => "通过",
            SelfCheckOutcome.Warn => "警示",
            SelfCheckOutcome.Fail => "失败",
            _ => "未知"
        };

    /// <summary>不支持反向转换。</summary>
    /// <param name="value">值。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">参数。</param>
    /// <param name="culture">区域信息。</param>
    /// <returns>不返回。</returns>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}