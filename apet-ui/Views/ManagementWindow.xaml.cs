using System.Windows;
using APet.UI.Services;
using APet.UI.Web;
using Microsoft.Web.WebView2.Core;

namespace APet.UI.Views;

/// <summary>
/// Agent 管理窗体(托盘「设置」入口):独立壳窗口承载 Web 首页(Agent 增删/启停/设置入口)。
/// 与主窗体 WebShellWindow 同构:自定义标题栏 + WebView2,最大化钳制到工作区。
/// 关闭 = 隐藏(复用已初始化的实例),仅托盘「退出」结束应用。
/// </summary>
public partial class ManagementWindow : Window
{
    /// <summary>为 true 时关闭(X)/Close() 真正销毁窗口;默认仅隐藏,托盘「设置」可再次打开。</summary>
    public bool ForceClose { get; set; }

    public ManagementWindow()
    {
        InitializeComponent();
        // 页面加载前的底色与主题一致,避免白闪
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1B, 0x1B, 0x1F);
        WindowMaximizeFix.Attach(this);
        Loaded += async (_, _) => await InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        try
        {
            // 复用全进程唯一环境(与主窗同用户目录同参数),避免 0x800139F;首建者优先
            var environment = await WebView2Env.GetOrCreateAsync("sw");
            await Web.EnsureCoreWebView2Async(environment);
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            // 链接的 target=_blank → 交给系统默认浏览器
            Web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
                }
                catch
                {
                    // 无默认浏览器等系统级失败:静默
                }
            };
            Web.Source = new Uri(WebServer.Instance.BaseUrl + "/#/");
        }
        catch (Exception ex)
        {
            WebFailed.Text = "WebView2 初始化失败:" + ex.Message
                + "\n请安装 Microsoft Edge WebView2 Runtime(Evergreen)后重试。";
            WebFailed.Visibility = Visibility.Visible;
        }
    }

    // ───────── 标题栏按钮与窗口状态 ─────────

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeWindow(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        // 最大化时窗口贴满工作区,外轮廓描边会顶到屏幕边缘,隐藏掉;还原后恢复
        FrameBorder.Visibility = WindowState == WindowState.Maximized ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>关闭拦截:非强制关闭时改为隐藏(托盘「设置」再次打开时重新 Show),仅退出时真正销毁。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ForceClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
