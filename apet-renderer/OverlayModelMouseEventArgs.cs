using System.Windows.Forms;

namespace APet.Renderer;

/// <summary>
/// 桌宠透明悬浮窗的模型鼠标事件参数:模型坐标为引擎自身口径
/// (Spine 骨架坐标系 y 向上 / Live2D 模型单位 / VRM 未使用),HitArea 仅 Live2D 提供。
/// 将 <see cref="Handled"/> 置 true 可阻止该次输入触发窗口默认行为(拖动/点击判定)。
/// </summary>
public class OverlayModelMouseEventArgs
{
    /// <summary>按下的鼠标键。</summary>
    public MouseButtons Button { get; }
    /// <summary>模型坐标 x(引擎口径)。</summary>
    public float ModelX { get; }
    /// <summary>模型坐标 y(引擎口径)。</summary>
    public float ModelY { get; }
    /// <summary>命中区域(Live2D 的 HitAreas;其他引擎 null)。</summary>
    public string? HitArea { get; }

    public OverlayModelMouseEventArgs(MouseButtons button, float modelX, float modelY, string? hitArea)
    {
        Button = button;
        ModelX = modelX;
        ModelY = modelY;
        HitArea = hitArea;
    }

    /// <summary>内容已处理该输入,窗口不再执行默认拖动/点击判定。</summary>
    public bool Handled { get; set; }
}
