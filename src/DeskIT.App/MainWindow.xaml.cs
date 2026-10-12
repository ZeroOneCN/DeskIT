using System.Windows;

namespace DeskIT.App;

/// <summary>
/// 主窗口。当前仅作为可运行外壳，展示骨架就绪状态与权限提示占位。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 初始化主窗口，并填充权限提示占位文本。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        PermissionHintText.Text = "当前权限：待检测（骨架阶段占位，后续由 M1 便携启动与运行环境填充）。";
    }
}