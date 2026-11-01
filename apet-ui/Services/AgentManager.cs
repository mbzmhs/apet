using System.IO;
using APet.Framework.Logging;
using System.Windows;
using APet.Framework.Plugin;
using APet.UI.ViewModels;
using APet.UI.Web;
using WinForms = System.Windows.Forms;

namespace APet.UI.Services;

/// <summary>Web 请求命中已停用(或不存在)的 Agent:端点抛出,由 WebServer 中间件统一转 404。</summary>
public sealed class AgentDisabledException(string agentId) : Exception("Agent 已停用或不存在: " + agentId);

/// <summary>每个已启用 Agent 的运行时:宿主 + 会话引擎(无头常驻)+ Web 壳窗口(可能尚未显示)。
/// v2:引擎与窗口解耦——外部浏览器可经本地 Web 服务操作会话,原生窗口只是其中一个前端。</summary>
public sealed class AgentRuntime
{
    public string Id { get; init; } = "";
    public AgentConfig Config { get; init; } = null!;
    public AgentHost Host { get; init; } = null!;
    public MainViewModel ViewModel { get; init; } = null!;
    public WebShellWindow? Window { get; set; }
}

/// <summary>
/// 常驻托盘的多 Agent 管理器:启动时初始化所有已启用 Agent(默认不弹窗),
/// 托盘菜单列出已启用 Agent(点击开窗/聚焦)+ 设置 + 退出。
/// 关闭某 Agent 窗口仅隐藏(宿主继续运行);仅托盘"退出"结束整个应用。
/// </summary>
public sealed class AgentManager : IDisposable
{
    private readonly UiConfig _config;
    private readonly Dictionary<string, AgentRuntime> _runtimes = new(StringComparer.Ordinal);
    private WinForms.NotifyIcon? _tray;
    private WinForms.ContextMenuStrip? _menu;
    private Window? _manageWindow;

    public AgentManager()
    {
        _config = UiConfig.Load();
    }

    /// <summary>启动:初始化所有已启用 Agent(仅 AutoOpenOnStartup 者弹窗),并建立托盘。</summary>
    public void Initialize()
    {
        WirePluginEvents();
        foreach (var agent in _config.Agents.Where(a => a.Enabled).ToList())
            CreateRuntime(agent, show: agent.AutoOpenOnStartup);
        BuildTray();
    }

    /// <summary>
    /// 插件 → 前端事件通道接线:插件经 <see cref="PluginEvents"/> 发布的事件转发到会话/Agent 级 SSE 总线。
    /// 会话属主查内存快照(不做惰性拉起);任何异常只吞掉并返回 false,不影响发布方。
    /// </summary>
    private void WirePluginEvents()
    {
        PluginEvents.SessionSink = (sessionId, type, data) =>
        {
            try
            {
                foreach (var rt in _runtimes.Values.ToArray())
                {
                    if (rt.ViewModel.Sessions.Any(s => s.SessionId == sessionId))
                    {
                        WebEventHub.Publish(rt.Id, sessionId, type, data);
                        return true;
                    }
                }
            }
            catch
            {
                // 字典并发变动等:事件丢弃即可
            }
            return false;
        };
        PluginEvents.AgentSink = (agentId, type, data) =>
        {
            try
            {
                if (_runtimes.ContainsKey(agentId))
                {
                    WebEventHub.PublishAgent(agentId, type, data);
                    return true;
                }
            }
            catch
            {
            }
            return false;
        };
    }

    private AgentRuntime CreateRuntime(AgentConfig agent, bool show)
    {
        // 幂等:同一 Agent 至多一个运行时。OpenAgentSettings(先开设置页)与 SetAgentEnabled(再开启用开关)
        // 会对同一 id 先后触达;若此处无条件新建会覆盖字典项,留下一个未被释放的孤儿宿主与其窗口
        // (表现为"多出一个实例、禁用的 Agent 设置功能失效")
        if (_runtimes.TryGetValue(agent.Id, out var existing))
            return existing;

        AgentHost host;
        try
        {
            host = new AgentHost(agent);
        }
        catch (Exception ex)
        {
            // 构造中途异常曾在多 Agent 场景静默发生(无任何日志),表现为该 Agent 窗口打不开、
            // 相关接口 500;这里落一条 Error 留痕后原样上抛
            Log.Global.Error($"[AgentManager] AgentHost 构造失败 id={agent.Id} name={agent.Name}: {ex}");
            throw;
        }
        // 桌宠/气泡点击“打开聊天”:由宿主直接唤起本 Agent 的客户端窗口
        host.PetOpenChatRequested += () => OpenAgent(agent.Id);
        // 会话引擎:无头常驻于运行时,窗口与浏览器共同作为它的前端
        var viewModel = new MainViewModel(host, Application.Current.Dispatcher, agent, _config);
        // 桌宠窗口被拖动后回写该 Agent 的持久化坐标并落盘(下次启动恢复到上次位置)
        if (host.DesktopPet is { } pet)
            pet.PositionChanged += (x, y) =>
            {
                if (agent.DesktopPet.PosX == x && agent.DesktopPet.PosY == y)
                    return;   // 未变化不写盘
                agent.DesktopPet.PosX = x;
                agent.DesktopPet.PosY = y;
                agent.Save(); // 桌宠坐标在该 Agent 自己的设定文件里
            };
        var window = new WebShellWindow(agent);
        var rt = new AgentRuntime { Id = agent.Id, Config = agent, Host = host, ViewModel = viewModel, Window = window };
        // 启动兜底导航:直达该 Agent 上次打开的会话(与托盘点名 OpenAgent 行为一致);无会话时前端落到 /a/{id} 再解析
        window.StartupChatResolver = () => ResumeSessionId(rt);
        _runtimes[agent.Id] = rt;
        if (show)
            window.Show();
        return rt;
    }

    /// <summary>托盘点开/聚焦某 Agent 窗口(尚未创建则先初始化再显示),并回到其上次打开的会话。</summary>
    public void OpenAgent(string id)
    {
        if (_config.Agents.FirstOrDefault(a => a.Id == id)?.Enabled != true)
            return; // 已停用的 Agent 不因任何入口被重新拉起(桌宠/TTS 复活 bug 的兜底)
        var rt = GetOrCreate(id);
        if (rt.Window is not { } window)
            return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            if (!window.IsVisible)
                window.Show();
            window.Activate();
            window.OpenChat(ResumeSessionId(rt));
        });
    }

    /// <summary>开窗应落到的会话:优先上次打开的(前端上屏时上报、随配置持久化);
    /// 无记忆或该会话已删/归档时回落到最近活跃的会话(无会话返回 null,窗口回退到会话列表页)。</summary>
    private static string? ResumeSessionId(AgentRuntime rt)
    {
        var last = rt.Config.LastSessionId;
        if (!string.IsNullOrEmpty(last) && rt.ViewModel.Sessions.Any(s => s.SessionId == last))
            return last;
        return rt.ViewModel.Sessions.OrderByDescending(s => s.LastMessageTime).FirstOrDefault()?.SessionId;
    }

    /// <summary>打开某 Agent 的设置页:确保其窗口存在并显示,然后切到设置视图。</summary>
    public void OpenAgentSettings(string id)
    {
        if (_config.Agents.FirstOrDefault(a => a.Id == id)?.Enabled != true)
            return; // 已停用的 Agent 不可进设置(与 Web 端 404 守卫同规则)
        var rt = GetOrCreate(id);
        if (rt.Window is not { } window)
            return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            if (!window.IsVisible)
                window.Show();
            window.Activate();
            window.OpenSettings();
        });
    }

    private AgentRuntime GetOrCreate(string id)
    {
        if (_runtimes.TryGetValue(id, out var rt))
            return rt;
        var agent = _config.Agents.FirstOrDefault(a => a.Id == id)
            ?? throw new InvalidOperationException("Unknown agent: " + id);
        return CreateRuntime(agent, show: false);
    }

    // ───────── Web 服务(WebServer)使用:惰性拉起运行时与会话归属定位 ─────────

    /// <summary>取某 Agent 的运行时(不存在则初始化,不弹窗):Web REST 操作会话的入口。
    /// 已停用的 Agent 不惰性拉起——残留页面(外部浏览器标签/旧窗口)的 SSE 重连与快照轮询
    /// 不得把已停用 Agent 的引擎(TTS/直播/桌宠)复活;此情形返回 null,由端点转 404。</summary>
    public AgentRuntime? GetOrCreateRuntime(string id)
    {
        if (_runtimes.TryGetValue(id, out var existing))
            return existing;
        var agent = AgentsSnapshot().FirstOrDefault(a => a.Id == id);
        return agent is { Enabled: true } ? CreateRuntime(agent, show: false) : null;
    }

    /// <summary>Web 端点用:取运行时,Agent 已停用/不存在时抛 <see cref="AgentDisabledException"/>
    /// (WebServer 中间件统一转 404,前端 EventSource 收到非 2xx 即停止重连)。</summary>
    public AgentRuntime RequireRuntime(string id) =>
        GetOrCreateRuntime(id) ?? throw new AgentDisabledException(id);

    /// <summary>该 Agent 是否启用(未知 Id 为 false)。</summary>
    public bool IsAgentEnabled(string id) =>
        AgentsSnapshot().Any(a => a.Id == id && a.Enabled);

    /// <summary>定位会话归属的 Agent Id:先查已初始化运行时的内存会话,再按磁盘会话存储判断
    /// (SQLite 库行或遗留 JSON 文件;含归档——URL 只带 sessionId 时前端经此解析路由目标)。
    /// 未初始化的 Agent 命中存储时惰性拉起。</summary>
    public string? FindSessionOwner(string sessionId)
    {
        foreach (var rt in _runtimes.Values)
            if (rt.ViewModel.Sessions.Any(s => s.SessionId == sessionId))
                return rt.Id;
        foreach (var agent in AgentsSnapshot().Where(a => a.Enabled))
        {
            var file = Path.Combine(AppPaths.AgentsRoot, agent.Id, "sessions", sessionId + ".json");
            var dbFile = Path.Combine(AppPaths.AgentsRoot, agent.Id, "sessions", "sessions.db");
            if (!File.Exists(file) && !File.Exists(dbFile))
                continue; // 该 Agent 无任何会话存储,不必开库
            // 数据迁到 SQLite 后归档会话不再落 JSON 文件:按库行判断,归档深链(只读预览)重启后仍可解析归属
            if (File.Exists(file) || SessionStore.Shared(agent.Id).Exists(sessionId))
            {
                GetOrCreate(agent.Id);
                return agent.Id;
            }
        }
        return null;
    }

    // ───────── 托盘 ─────────

    private void BuildTray()
    {
        _menu = new WinForms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5f),
        };
        RebuildMenuItems();
        _tray = new WinForms.NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "APet",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => OpenFirstEnabled();
    }

    private void RebuildMenuItems()
    {
        if (_menu is null)
            return;
        _menu.Items.Clear();
        var enabled = _config.Agents.Where(a => a.Enabled).ToList();
        foreach (var agent in enabled)
        {
            var id = agent.Id;
            var item = new WinForms.ToolStripMenuItem(agent.Title) { ForeColor = DarkMenuRenderer.Text };
            item.Click += (_, _) => OpenAgent(id);
            _menu.Items.Add(item);
        }
        if (enabled.Count > 0)
            _menu.Items.Add(new WinForms.ToolStripSeparator());
        var settings = new WinForms.ToolStripMenuItem("设置") { ForeColor = DarkMenuRenderer.Text };
        settings.Click += (_, _) => OpenManagement();
        _menu.Items.Add(settings);
        var exit = new WinForms.ToolStripMenuItem("退出") { ForeColor = DarkMenuRenderer.Text };
        exit.Click += (_, _) => ShutdownAll();
        _menu.Items.Add(exit);
    }

    private void OpenFirstEnabled()
    {
        var first = _config.Agents.FirstOrDefault(a => a.Enabled);
        if (first is not null)
            OpenAgent(first.Id);
    }

    /// <summary>打开 Agent 管理:独立的管理窗体承载 Web 首页(增删/启停/头像/各 Agent 设置入口),单例。</summary>
    public void OpenManagement()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_manageWindow is not null && _manageWindow.IsLoaded)
            {
                if (_manageWindow.WindowState == WindowState.Minimized)
                    _manageWindow.WindowState = WindowState.Normal;
                // 关闭 = 隐藏:Hide 后仅 Activate 不会重新显示,必须先 Show
                _manageWindow.Show();
                _manageWindow.Activate();
                return;
            }
            _manageWindow = new Views.ManagementWindow();
            _manageWindow.Show();
            _manageWindow.Activate();
        });
    }

    // ───────── Agent 增删/启停(由管理窗口调用) ─────────

    private readonly object _agentsGate = new();

    /// <summary>Agent 清单快照:清单是普通 List,UI 线程增删(创建/删除)、Kestrel/托盘线程枚举并发,
    /// 直接暴露会在枚举中途被改("Collection was modified")甚至内部损坏——一律发快照。</summary>
    private List<AgentConfig> AgentsSnapshot()
    {
        lock (_agentsGate)
            return [.. _config.Agents];
    }

    public IReadOnlyList<AgentConfig> Agents => AgentsSnapshot();

    /// <summary>新增 Agent(必须命名):立即初始化(隐藏),托盘可见。</summary>
    public void AddAgent(string name)
    {
        var agent = new AgentConfig { Id = AgentConfig.NewId(), Name = name, Enabled = true };
        lock (_agentsGate)
            _config.Agents.Add(agent);
        agent.Save(); // Agent 设定存自己的目录
        _config.Save(); // 全局注册表(顺序)
        CreateRuntime(agent, show: false);
        RebuildMenuItems();
    }

    public void DeleteAgent(string id)
    {
        AgentConfig? agent;
        lock (_agentsGate)
        {
            if (_config.Agents.Count <= 1)
                return; // 至少保留一个
            agent = _config.Agents.FirstOrDefault(a => a.Id == id);
            if (agent is null)
                return;
            _config.Agents.Remove(agent);
            if (string.Equals(_config.ActiveAgentId, id, StringComparison.Ordinal))
                _config.ActiveAgentId = _config.Agents[0].Id;
        }
        _ = DisposeRuntime(id); // 插件异步清理后台执行
        _config.Save();
        SessionStore.DeleteAgentSessions(id); // 该 Agent 的整个文件夹(设定 + 对话记录)一并清除
        RebuildMenuItems();
    }

    public void SetAgentEnabled(string id, bool enabled)
    {
        var agent = AgentsSnapshot().FirstOrDefault(a => a.Id == id);
        if (agent is null || agent.Enabled == enabled)
            return;
        agent.Enabled = enabled;
        agent.Save(); // 启用状态在该 Agent 自己的设定文件里
        if (enabled)
            CreateRuntime(agent, show: false);
        else
            _ = DisposeRuntime(id); // 插件异步清理后台执行
        RebuildMenuItems();
    }

    /// <summary>重命名(必须非空):刷新已开窗口标题与托盘菜单项文本。</summary>
    public void SetAgentName(string id, string name)
    {
        var agent = AgentsSnapshot().FirstOrDefault(a => a.Id == id);
        if (agent is null || string.IsNullOrWhiteSpace(name))
            return;
        agent.Name = name.Trim();
        agent.Save(); // 名称在该 Agent 自己的设定文件里
        if (_runtimes.TryGetValue(id, out var rt) && rt.Window is { } window)
            Application.Current.Dispatcher.Invoke(() => window.SetAgentTitle(agent.Title));
        RebuildMenuItems();
    }

    /// <summary>
    /// 拆除运行时:同步部分(关窗/落盘/插件同步 Dispose)立即执行,
    /// 插件异步清理以 Task 返回——应用退出路径 await 它,删除/禁用路径后台执行。
    /// </summary>
    private Task DisposeRuntime(string id)
    {
        if (!_runtimes.TryGetValue(id, out var rt))
            return Task.CompletedTask;
        _runtimes.Remove(id);
        if (rt.Window is { } window)
            Application.Current.Dispatcher.Invoke(() =>
            {
                window.ForceClose = true;
                window.Close();
            });
        rt.ViewModel.SaveAllSessions(); // 关窗收尾:运行时负责落盘全部会话
        rt.Host.Dispose();
        return rt.Host.DisposePluginsAsync();
    }

    /// <summary>托盘"退出":先让各窗口取消运行中的请求并等待收尾(中断路径的增量写回+落盘),
    /// 再关窗口与宿主,移除托盘图标,结束应用。收尾续延要回 UI 线程执行,
    /// 因此全程 await 让出调度器而不是阻塞等待(阻塞会让收尾代码永远跑不到)。</summary>
    public void ShutdownAll()
    {
        _ = ShutdownAllAsync(); // 托盘点击线程即 UI 线程:异步收尾期间调度器继续泵消息,收尾续延才能执行
    }

    private async Task ShutdownAllAsync()
    {
        try
        {
            // 引擎收尾(取消运行中请求并等待落盘)不再经由窗口:Web 形态下窗口只是可选前端
            var exits = _runtimes.Values
                .Select(rt => rt.ViewModel.ShutdownAsync())
                .ToList();
            if (exits.Count > 0)
                await Task.WhenAll(exits);
            // 插件异步清理(断远程连接等)也要等到退出前完成
            var pluginExits = _runtimes.Keys.ToList().Select(DisposeRuntime).ToList();
            if (pluginExits.Count > 0)
                await Task.WhenAll(pluginExits);
            // 兜底看门狗只在保存阶段完成后启动:此前 2.5 秒即 Environment.Exit(0),
            // 而 ShutdownAsync 的在途等待上限是 5 秒——退出被卡 2.5 秒就强杀,
            // 兜底落盘还没执行,会话用量(等内存状态)整段丢失
            Task.Delay(2500).ContinueWith(_ => Environment.Exit(0));
            if (_manageWindow is { } mw && mw.IsLoaded)
                Application.Current.Dispatcher.Invoke(() => mw.Close());
            RemoveTray();
            Application.Current.Shutdown();
        }
        catch
        {
            // 收尾异常不阻止退出
        }
        Environment.Exit(0);
    }

    private void RemoveTray()
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _menu?.Dispose();
        _menu = null;
    }

    public void Dispose() => RemoveTray();
}

/// <summary>
/// 托盘菜单暗黑渲染(WinForms 无 XAML 主题,手动对齐 Themes/Dark.xaml 配色):
/// BgPanelHover 背景 / SelectionBrush 悬停 / FgPrimary 文字 / BorderBrush 边框与分隔线。
/// </summary>
internal sealed class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
{
    public static readonly System.Drawing.Color Back = System.Drawing.Color.FromArgb(0x2B, 0x2B, 0x31);   // BgPanelHover
    public static readonly System.Drawing.Color Text = System.Drawing.Color.FromArgb(0xE6, 0xE6, 0xEA);   // FgPrimary
    private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x3A, 0x3A, 0x44); // SelectionBrush
    private static readonly System.Drawing.Color DisabledText = System.Drawing.Color.FromArgb(0x9B, 0x9B, 0xA4); // FgSecondary
    private static readonly System.Drawing.Color Line = System.Drawing.Color.FromArgb(0x33, 0x33, 0x3B);  // BorderBrush

    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Selected || e.Item.Pressed)
        {
            using var brush = new System.Drawing.SolidBrush(Hover);
            e.Graphics.FillRectangle(brush, 2, 1, e.Item.Width - 4, e.Item.Height - 2);
        }
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : DisabledText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
    {
        using var brush = new System.Drawing.SolidBrush(Line);
        e.Graphics.FillRectangle(brush, 6, e.Item.Height / 2, e.Item.Width - 12, 1);
    }

    private sealed class DarkColorTable : WinForms.ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground => Back;
        public override System.Drawing.Color ImageMarginGradientBegin => Back;
        public override System.Drawing.Color ImageMarginGradientMiddle => Back;
        public override System.Drawing.Color ImageMarginGradientEnd => Back;
        public override System.Drawing.Color MenuBorder => Line;
        public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.Transparent;
        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override System.Drawing.Color MenuItemPressedGradientBegin => Back;
        public override System.Drawing.Color MenuItemPressedGradientEnd => Back;
        public override System.Drawing.Color SeparatorDark => Line;
        public override System.Drawing.Color SeparatorLight => Line;
    }
}

/// <summary>托盘图标:优先加载嵌入的 ds.ico(与程序/窗口图标一致);失败回退运行时绘制。</summary>
internal static class TrayIconFactory
{
    public static System.Drawing.Icon Create()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/ds.ico"));
            using var ms = new System.IO.MemoryStream();
            resource.Stream.CopyTo(ms);
            ms.Position = 0;
            return new System.Drawing.Icon(ms);
        }
        catch
        {
            return CreateFallback();
        }
    }

    private static System.Drawing.Icon CreateFallback()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x4F, 0x7F, 0xE0));
            g.FillRectangle(bg, 1, 1, 30, 30);
            using var font = new System.Drawing.Font("Segoe UI", 18f, System.Drawing.FontStyle.Bold);
            using var fg = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            var sf = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
            };
            g.DrawString("A", font, fg, new System.Drawing.RectangleF(0, 0, 32, 32), sf);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
