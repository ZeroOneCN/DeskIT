using DeskIT.Core;

namespace DeskIT.App;

/// <summary>采集字段的界面视图：字段名与可直接展示的取值文本。</summary>
/// <param name="Name">字段名。</param>
/// <param name="Display">取值文本（正常取值显示值，其余显示状态说明）。</param>
internal sealed record CollectionFieldView(string Name, string Display);

/// <summary>采集分组的界面视图，对应一张摘要卡片。</summary>
/// <param name="GroupName">分组中文名称。</param>
/// <param name="Status">分组状态（用于徽标配色）。</param>
/// <param name="StatusText">分组状态文本。</param>
/// <param name="ElapsedText">分组采集耗时文本。</param>
/// <param name="Fields">字段清单。</param>
/// <param name="Details">明细行（如已装软件清单）。</param>
internal sealed record CollectionGroupView(
    string GroupName,
    CollectionGroupStatus Status,
    string StatusText,
    string ElapsedText,
    IReadOnlyList<CollectionFieldView> Fields,
    IReadOnlyList<string> Details);