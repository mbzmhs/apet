using System.Windows;
using APet.UI.Services;
using APet.UI.Web;
using Microsoft.Web.WebView2.Core;

namespace APet.UI;

/// <summary>
/// v2 主窗壳:自实现标题栏 + WebView2 承载 Web 前端。
/// 前端由 WebServer(127.0.0.1)伺服,本窗口只是它的一个"浏览器实例",
/// 按 URL 的 agent 参数打开对应 Agent;外部浏览器打开同一地址得到等价界面。
/// 关闭 = 隐藏到托盘(与原窗口一致),仅托盘"退出"结束应用。
/// </summary>
public partial class WebShellWindow : Window
{
    private readonly AgentConfig _agentConfig;

    /// <summary>启动兜底导航用的"上次打开会话"解析器(由 AgentManager 注入);无会话时返回 null 走会话页回落。</summary>
    public Func<string?>? StartupChatResolver { get; set; }

    public WebShellWindow(AgentConfig agentConfig)
    {
        InitializeComponent();
        _agentConfig = agentConfig;
        AgentTitleText.Text = agentConfig.Title;
        // 页面加载前的底色与主题一致,避免白闪
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1B, 0x1B, 0x1F);
        WindowMaximizeFix.Attach(this);
        Loaded += async (_, _) => await InitWebAsync();
        RestoreWindowPosition();
        LocationChanged += OnWindowLocationChangedForPos;
        SizeChanged += (_, _) => OnWindowLocationChangedForPos(this, EventArgs.Empty);
    }

    /// <summary>为 true 时关闭(X)/Close() 真正销毁窗口;默认仅隐藏到托盘,Agent 继续运行。</summary>
    public bool ForceClose { get; set; }

    // ───────── WebView2 初始化与导航 ─────────

    private async Task InitWebAsync()
    {
        try
        {
            // 全进程唯一环境(首窗按设置页渲染模式创建,其余窗复用);渲染模式窗口重开生效
            var environment = await WebView2Env.GetOrCreateAsync(_agentConfig.WebViewRenderMode);
            await Web.EnsureCoreWebView2Async(environment);
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            // Markdown 等链接的 target=_blank → 交给系统默认浏览器
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
            // 初始化前已被外部(OpenChat/OpenSettings)指定过目标页时不重复兜底,
            // 否则异步初始化完成后的兜底导航会把外部目标覆盖掉。
            // 兜底目标 = 本 Agent 自己的聊天页(启动自动开窗应直达该 Agent,而非全局管理首页);
            // 无会话时 OpenChat(null) 落到 /a/{id},前端再解析为最新会话或新建会话
            if (!_externalNav)
                OpenChat(StartupChatResolver?.Invoke());
        }
        catch (Exception ex)
        {
            WebFailed.Text = "WebView2 初始化失败:" + ex.Message
                + "\n请安装 Microsoft Edge WebView2 Runtime(Evergreen)后重试。";
            WebFailed.Visibility = Visibility.Visible;
        }
    }

    /// <summary>打开本窗口所属 Agent 的首页(Agent 管理已并入 Web 首页;托盘「设置」入口)。</summary>
    public void NavigateHome() =>
        NavigateHash("/");

    /// <summary>切换到本 Agent 的会话列表页(无最近会话时的回落)。</summary>
    public void OpenAgentPage() =>
        NavigateHash("/a/" + _agentConfig.Id);

    /// <summary>直达某会话的聊天页(托盘点名/宠物点击/桌面壳内网页请求)。</summary>
    public void OpenChat(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            OpenAgentPage();
        else
            NavigateHash("/s/" + sessionId);
    }

    /// <summary>切换到设置页(托盘/管理窗口入口)。</summary>
    public void OpenSettings() =>
        NavigateHash("/a/" + _agentConfig.Id + "/settings");

    /// <summary>地址 = 基址 + ?agent= + hash 路由;目标与当前一致时不重复导航(避免整页重载)。
    /// 任一次外部指定导航后置位,初始化兜底回首页据此跳过。</summary>
    private bool _externalNav;

    private void NavigateHash(string hash)
    {
        var target = new Uri(WebServer.Instance.BaseUrl + "/?agent="
                             + Uri.EscapeDataString(_agentConfig.Id) + "#" + hash);
        if (Web.Source != target)
        {
            Web.Source = target;
            _externalNav = true;
        }
    }

    /// <summary>托盘重命名 Agent 后刷新标题栏文本。</summary>
    public void SetAgentTitle(string title) => AgentTitleText.Text = title;

    // ───────── 标题栏按钮与窗口状态 ─────────

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeWindow(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        // 最大化时窗口贴满工作区,外轮廓描边会顶到屏幕边缘,隐藏掉;还原后恢复
        FrameBorder.Visibility = WindowState == WindowState.Maximized ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>关闭拦截:非强制关闭时改为隐藏(宿主继续运行,托盘点名可再次打开);关闭前保存窗口位置。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowPosition();
        if (!ForceClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    // ───────── 窗口位置持久化(上次拖到哪,下次开在哪) ─────────

    private void RestoreWindowPosition()
    {
        if (_agentConfig.ChatWidth is { } w && _agentConfig.ChatHeight is { } h)
        {
            Width = Math.Max(w, MinWidth);
            Height = Math.Max(h, MinHeight);
        }
        else
        {
            // 默认 1440×900:小屏下夹紧到工作区,避免开窗即超屏
            Width = Math.Min(1440, SystemParameters.WorkArea.Width);
            Height = Math.Min(900, SystemParameters.WorkArea.Height);
        }
        if (_agentConfig.ChatPosX is not { } x || _agentConfig.ChatPosY is not { } y)
            return;
        if (!IsOnAnyScreen(x, y))
            return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = x;
        Top = y;
    }

    private static bool IsOnAnyScreen(double x, double y)
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        return x >= left && x < left + SystemParameters.VirtualScreenWidth
            && y >= top && y < top + SystemParameters.VirtualScreenHeight;
    }

    private System.Windows.Threading.DispatcherTimer? _posSaveTimer;

    /// <summary>拖动/移动中连续触发:停 400ms 后才落盘,避免拖动过程频繁写配置。</summary>
    private void OnWindowLocationChangedForPos(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Normal)
            return;
        _posSaveTimer ??= CreatePosSaveTimer();
        _posSaveTimer.Stop();
        _posSaveTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreatePosSaveTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveWindowPosition();
        };
        return timer;
    }

    private void SaveWindowPosition()
    {
        if (!IsLoaded || WindowState != WindowState.Normal)
            return;
        var changed = _agentConfig.ChatPosX != Left || _agentConfig.ChatPosY != Top
            || _agentConfig.ChatWidth != Width || _agentConfig.ChatHeight != Height;
        if (!changed)
            return;
        _agentConfig.ChatPosX = Left;
        _agentConfig.ChatPosY = Top;
        _agentConfig.ChatWidth = Width;
        _agentConfig.ChatHeight = Height;
        _agentConfig.Save();
    }
}
