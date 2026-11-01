using System.IO;
using Microsoft.Web.WebView2.Core;

namespace APet.UI.Web;

/// <summary>
/// 全进程唯一的 CoreWebView2Environment:主窗与管理窗共用同一 webview2 用户数据目录,
/// 若各自 CreateAsync(尤其浏览器参数不一致)会抛 0x800139F(资源状态不正确)导致设置窗黑屏。
/// 首个窗口按自己的渲染模式创建环境,其余窗口复用(管理窗无独立渲染模式,跟随首建者)。
/// </summary>
public static class WebView2Env
{
    private static CoreWebView2Environment? _env;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<CoreWebView2Environment> GetOrCreateAsync(string? mode)
    {
        if (_env is not null) return _env;
        await Gate.WaitAsync();
        try
        {
            if (_env is null)
            {
                // WebView2 用户数据目录放本地缓存根(Chromium profile 属机器本地可再生数据,不随用户漫游)
                _env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: Services.AppPaths.WebView2Root,
                    options: WebViewRender.OptionsFor(mode));
            }
            return _env;
        }
        finally
        {
            Gate.Release();
        }
    }
}
