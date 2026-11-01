using Microsoft.Web.WebView2.Core;

namespace APet.UI.Web;

/// <summary>
/// WebView2 渲染模式 → 浏览器启动参数:
/// sw(默认)= --disable-gpu 全软件渲染,多显卡/虚拟显示器驱动的 GPU 进程黑屏与滚动合成残留最稳;
/// cpu = --disable-gpu-compositing 仅合成走 CPU;hw = 无参数(硬件加速)。
/// </summary>
public static class WebViewRender
{
    public static CoreWebView2EnvironmentOptions? OptionsFor(string? mode) =>
        (mode ?? "sw") switch
        {
            "hw" => null,
            "cpu" => new CoreWebView2EnvironmentOptions("--disable-gpu-compositing"),
            _ => new CoreWebView2EnvironmentOptions("--disable-gpu"),
        };
}
