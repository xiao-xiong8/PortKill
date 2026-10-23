using System.Runtime.InteropServices;

namespace PortKiller.Native;

/// <summary>
/// 系统「显示」偏好的只读探测（user32 SystemParametersInfo）。
///
/// 目前只暴露一项：<see cref="AreAnimationsEnabled"/> —— 对应
/// 「设置 → 辅助功能 → 视觉效果 → 动画效果」开关（SPI_GETCLIENTAREAANIMATION）。
/// 用于给设置页的「减少界面动效」提供系统默认值，符合 WCAG 2.3
/// 「尊重用户对动态效果的偏好」：用户关掉系统动画，本工具首次运行即不再动。
///
/// 注意：只读查询，不写回系统。任何失败都返回 true（保持动画），
/// 因为「查不到」不等于「用户不想要动画」。
/// </summary>
internal static class SystemPreferences
{
    /// <summary>SPI_GETCLIENTAREAANIMATION：客户端区域动画是否开启。</summary>
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

    /// <summary>
    /// 系统是否启用界面动画。成功且返回 0 表示用户已关闭动画。
    /// </summary>
    public static bool AreAnimationsEnabled()
    {
        try
        {
            if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out int enabled, 0))
            {
                return enabled != 0;
            }
        }
        catch
        {
            // 探测失败按「允许动画」处理，绝不因为查询异常改变用户界面行为
        }

        return true;
    }
}
