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

/// <summary>把诊断结论四态转换为徽标背景色。</summary>
public sealed class DiagnosisConclusionToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush AbnormalBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush HintBrush = new(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush UnknownBrush = new(Color.FromRgb(0x7C, 0x3A, 0xED));
    private static readonly SolidColorBrush NormalBrush = new(Color.FromRgb(0x16, 0xA3, 0x4A));

    /// <summary>按诊断结论返回背景色：异常=红、提示=橙、未知=紫、正常=绿。</summary>
    /// <param name="value">DiagnosisConclusion 值。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="culture">区域信息。</param>
    /// <returns>对应的画刷。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            DiagnosisConclusion.Abnormal => AbnormalBrush,
            DiagnosisConclusion.Hint => HintBrush,
            DiagnosisConclusion.Unknown => UnknownBrush,
            DiagnosisConclusion.Normal => NormalBrush,
            _ => UnknownBrush
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

/// <summary>把动作风险级别转换为徽标背景色。</summary>
public sealed class RiskLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ReadOnlyBrush = new(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly SolidColorBrush LowBrush = new(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly SolidColorBrush MediumBrush = new(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush HighBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush IrreversibleBrush = new(Color.FromRgb(0x99, 0x1B, 0x1B));

    /// <summary>按风险级别返回背景色：只读=灰、低=绿、中=橙、高=红、不可逆=深红。</summary>
    /// <param name="value">RiskLevel 值。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="culture">区域信息。</param>
    /// <returns>对应的画刷。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            RiskLevel.ReadOnly => ReadOnlyBrush,
            RiskLevel.Low => LowBrush,
            RiskLevel.Medium => MediumBrush,
            RiskLevel.High => HighBrush,
            RiskLevel.Irreversible => IrreversibleBrush,
            _ => ReadOnlyBrush
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

/// <summary>把动作执行结果归类转换为徽标背景色。</summary>
public sealed class ActionOutcomeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly SolidColorBrush PendingRestartBrush = new(Color.FromRgb(0x7C, 0x3A, 0xED));
    private static readonly SolidColorBrush FailedBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush SkippedBrush = new(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly SolidColorBrush CancelledBrush = new(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly SolidColorBrush TimedOutBrush = new(Color.FromRgb(0xD9, 0x77, 0x06));

    /// <summary>按结果归类返回背景色：成功=绿、需重启=紫、失败=红、跳过=灰、取消=深灰、超时=橙。</summary>
    /// <param name="value">ActionOutcome 值。</param>
    /// <param name="targetType">目标类型。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="culture">区域信息。</param>
    /// <returns>对应的画刷。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            ActionOutcome.Success => SuccessBrush,
            ActionOutcome.SuccessPendingRestart => PendingRestartBrush,
            ActionOutcome.Failed => FailedBrush,
            ActionOutcome.Skipped => SkippedBrush,
            ActionOutcome.Cancelled => CancelledBrush,
            ActionOutcome.TimedOut => TimedOutBrush,
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