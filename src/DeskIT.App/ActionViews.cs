using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DeskIT.Core;

namespace DeskIT.App;

/// <summary>动作参数的界面视图：展示说明并承载 IT 人员填写的取值。</summary>
internal sealed class ActionParameterView : INotifyPropertyChanged
{
    private string _value;

    /// <summary>初始化参数视图。</summary>
    /// <param name="parameter">参数声明。</param>
    public ActionParameterView(ActionParameter parameter)
    {
        Name = parameter.Name;
        Description = parameter.Description;
        _value = parameter.DefaultValue ?? string.Empty;
    }

    /// <summary>参数名（脚本中以 {{名称}} 引用）。</summary>
    public string Name { get; }

    /// <summary>参数说明。</summary>
    public string Description { get; }

    /// <summary>当前取值。</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value != value)
            {
                _value = value;
                OnPropertyChanged();
            }
        }
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>触发属性变更通知。</summary>
    /// <param name="name">属性名。</param>
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一条修复动作的界面视图：勾选状态、风险与需重启标记、参数与禁用原因。</summary>
internal sealed class ActionItemView : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>初始化动作视图。</summary>
    /// <param name="action">动作定义。</param>
    /// <param name="canRun">当前模式下是否可执行。</param>
    /// <param name="disabledReason">不可执行时的原因。</param>
    public ActionItemView(ActionDescriptor action, bool canRun, string? disabledReason)
    {
        Action = action;
        ActionId = action.ActionId;
        Name = action.Name;
        Summary = action.Summary;
        SceneName = DiagnosisRules.DescribeScene(action.Scene);
        Risk = action.Risk;
        RiskText = "风险：" + ActionRules.DescribeRisk(action.Risk);
        RequiresRestart = action.RequiresRestart;
        RollbackText = action.CanRollback ? "可回滚（执行前保存原值）" : "不可回滚";
        ConfirmationText = ActionRules.DescribeConfirmation(ActionRules.ConfirmationFor(action));
        TimeoutText = $"超时 {action.TimeoutSeconds / 60.0:0.#} 分钟";
        Parameters = new ObservableCollection<ActionParameterView>(
            action.Parameters.Select(parameter => new ActionParameterView(parameter)));
        CanRun = canRun;
        DisabledReason = disabledReason;

        foreach (var parameter in Parameters)
        {
            parameter.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Parameters));
        }
    }

    /// <summary>动作定义。</summary>
    public ActionDescriptor Action { get; }

    /// <summary>动作标识。</summary>
    public string ActionId { get; }

    /// <summary>动作名称。</summary>
    public string Name { get; }

    /// <summary>影响说明。</summary>
    public string Summary { get; }

    /// <summary>所属场景中文名。</summary>
    public string SceneName { get; }

    /// <summary>风险级别（用于徽标配色）。</summary>
    public RiskLevel Risk { get; }

    /// <summary>风险徽标文本。</summary>
    public string RiskText { get; }

    /// <summary>是否需重启。</summary>
    public bool RequiresRestart { get; }

    /// <summary>回滚能力文本。</summary>
    public string RollbackText { get; }

    /// <summary>确认要求文本。</summary>
    public string ConfirmationText { get; }

    /// <summary>超时文本。</summary>
    public string TimeoutText { get; }

    /// <summary>是否含参数。</summary>
    public bool HasParameters => Parameters.Count > 0;

    /// <summary>参数视图集合。</summary>
    public ObservableCollection<ActionParameterView> Parameters { get; }

    /// <summary>当前模式下是否可执行。</summary>
    public bool CanRun { get; }

    /// <summary>不可执行时的原因；可执行时为空。</summary>
    public string? DisabledReason { get; }

    /// <summary>是否需要展示不可执行原因。</summary>
    public bool ShowDisabledReason => !CanRun && !string.IsNullOrEmpty(DisabledReason);

    /// <summary>是否为高风险及以上动作。</summary>
    public bool IsHighRisk => Action.IsHighRisk;

    /// <summary>是否为不可逆动作。</summary>
    public bool IsIrreversible => Action.IsIrreversible;

    /// <summary>是否被勾选。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 取本次填写的参数取值。
    /// </summary>
    /// <returns>参数名 → 取值。</returns>
    public IReadOnlyDictionary<string, string> ParameterValues()
        => Parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value, StringComparer.Ordinal);

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>触发属性变更通知。</summary>
    /// <param name="name">属性名。</param>
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一条动作执行结果的界面视图。</summary>
/// <param name="HeaderText">标题（动作名称）。</param>
/// <param name="Outcome">结果归类（用于徽标配色）。</param>
/// <param name="OutcomeText">结果中文文本。</param>
/// <param name="DetailText">风险 / 退出码 / 耗时 / 原因。</param>
/// <param name="OutputText">标准输出摘要。</param>
/// <param name="ErrorText">标准错误摘要。</param>
/// <param name="HasOutput">是否有标准输出。</param>
/// <param name="HasError">是否有标准错误。</param>
internal sealed record ActionResultView(
    string HeaderText,
    ActionOutcome Outcome,
    string OutcomeText,
    string DetailText,
    string OutputText,
    string ErrorText,
    bool HasOutput,
    bool HasError);