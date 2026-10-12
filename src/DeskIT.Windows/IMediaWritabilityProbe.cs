namespace DeskIT.Windows;

/// <summary>
/// 介质（U 盘）可写性探测。周期性写入并 flush 探测文件，失败即判定介质不可用，
/// 触发拔出保护流程（M8）。本阶段仅固定接口形态。
/// </summary>
public interface IMediaWritabilityProbe
{
    /// <summary>
    /// 探测指定目录当前是否可写。
    /// </summary>
    /// <param name="directoryPath">待探测的目录绝对路径（通常为 U 盘数据目录）。</param>
    /// <returns>可写返回 true；不可写或目录不存在返回 false。</returns>
    Task<bool> ProbeAsync(string directoryPath);
}