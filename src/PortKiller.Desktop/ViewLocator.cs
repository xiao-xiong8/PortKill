using Avalonia.Controls;
using Avalonia.Controls.Templates;
using PortKiller.ViewModels;

namespace PortKiller;

/// <summary>
/// 视图定位器（AOT 洁净版）。
///
/// 本应用是单窗口架构：MainWindow / HudWindow 都在代码中直接构造并赋 DataContext，
/// 不存在任何「ContentControl 绑定 VM 后按名字反射找 View」的场景。
/// 旧模板实现用 Type.GetType + Activator.CreateInstance 反射定位，是全工程
/// 唯一的 AOT IL2026 剪裁警告来源，且从未被真正命中 —— 故移除反射，保留空壳。
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null) return null;

        // 不会走到这里；若未来引入 VM-first 导航，请在此写显式类型映射（switch），禁止反射
        return new TextBlock { Text = "未注册的视图模型: " + param.GetType().FullName };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
