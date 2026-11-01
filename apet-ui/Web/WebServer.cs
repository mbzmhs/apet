using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using APet.Framework.Logging;
using APet.Framework.Models;
using APet.Framework.Plugin;
using APet.Framework.Session;
using APet.Plugins.BackgroundRun;
using APet.UI.Services;
using APet.UI.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace APet.UI.Web;

/// <summary>
/// 本地 Web 服务(v2 主窗 Webview 化):net8 自带 Kestrel 经 FrameworkReference 内进程运行,
/// 绑定 127.0.0.1(仅本机可达;全部端点无鉴权,绝不可暴露到局域网/公网——配置接口会回传明文密钥)。伺服 web/ 构建产物(输出目录 wwwroot)与 REST/SSE API,
/// 客户端(WebView2 内嵌)与外部浏览器访问同一地址;多 Agent/会话由 URL 路由(前端解析)。
/// 引擎(AgentManager/AgentHost/会话 VM)只在 WPF UI 线程变更:API 处理器一律经 Dispatcher 取快照/投递动作,
/// 流式数据(M1 起)走线程安全事件总线,不占用 UI 线程。
/// </summary>
public sealed class WebServer
{
    /// <summary>默认端口;被占用时向后顺延探测(桌面单机场景,探测窗口极小)。</summary>
    public const int DefaultPort = 17877;

    /// <summary>App 启动即创建(必须先于 AgentManager.Initialize:窗口导航依赖 BaseUrl)。</summary>
    public static WebServer Instance { get; private set; } = null!;

    public int Port { get; private set; }

    public string BaseUrl => "http://127.0.0.1:" + Port;

    private readonly AgentManager _manager;
    private WebApplication? _app;

    private WebServer(AgentManager manager) => _manager = manager;

    /// <summary>同步启动(Kestrel 绑定为毫秒级,启动期一次性的短暂阻塞可接受)。</summary>
    public static void Start(AgentManager manager)
    {
        var server = new WebServer(manager);
        server.StartCore();
        Instance = server;
    }

    private void StartCore()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders(); // WinExe 无控制台:去掉默认日志提供者
        builder.WebHost.UseWebRoot("wwwroot");
        // 仅绑定回环:本机(WebView2/浏览器)经 127.0.0.1 访问;局域网设备不可达(端点无鉴权,防密钥泄露)
        builder.WebHost.UseUrls("http://127.0.0.1:" + FindFreePort(DefaultPort));

        var app = builder.Build();
        // /api 请求含空路径段(如前端 agentId 未就绪拼出 /api/agents//…)一律 404 JSON:
        // 这类 URL 匹配不到路由会落进 SPA 回退返回 HTML,任何 r.json() 都会炸
        // ("Unexpected token '<' ... not valid JSON" 横幅即由此来)
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api") &&
                ctx.Request.Path.Value!.Contains("//"))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"malformed path\"}");
                return;
            }
            await next();
        });
        // 本地回环应用禁缓存策略:SPA 入口 no-store(每次部署后浏览器必须拿到新 index.html,
        // 防 WebView 缓存旧入口引用已更替的 hash 资源;hash 化的静态资源可长缓存);
        // /api/* 与插件桥 no-store(数据面/桥契约随宿主更新,启发式缓存会让插件页读到旧契约:
        // 如旧版页面渲染新版 camelCase 数据、旧桥不拆信封等,均为部署后窗口期实踩问题)
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value;
            if (HttpMethods.IsGet(ctx.Request.Method) &&
                (path == "/" ||
                 path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                 path == "/apet-bridge.js"))
            {
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers.Pragma = "no-cache";
            }
            await next();
        });
        // 已停用 Agent 的 Web 请求统一 404:残留页面的重连/轮询不得复活其引擎(中间件兜住
        // Dispatcher.Invoke 透传出的异常;响应未开始才能改写状态码)
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (AgentDisabledException) when (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"Agent 已停用或不存在\"}");
            }
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        MapApi(app);
        SettingsApi.Map(app, _manager);
        app.MapFallbackToFile("index.html"); // SPA 客户端路由:未知路径回落 index
        app.StartAsync().GetAwaiter().GetResult();

        _app = app;
        Port = ParsePort(app);
    }

    /// <summary>双总线合并泵:会话总线 + Agent 总线帧经同一条 SSE 连接下发。页面由此单连接
    /// 获得全部事件;此前会话流与 Agent 流两条 SSE 常驻,多 Agent 多窗口把 HTTP/1.1
    /// 每主机 6 连接的上限吃满,后续所有请求在浏览器内排队 pending、整个前端假死。</summary>
    private static async Task SsePumpDualAsync(ChannelReader<string> sessionReader, ChannelReader<string> agentReader, HttpContext ctx)
    {
        var res = ctx.Response;
        res.Headers.ContentType = "text/event-stream";
        res.Headers.CacheControl = "no-cache";
        res.Headers["X-Accel-Buffering"] = "no";
        await res.StartAsync(ctx.RequestAborted);
        var ping = new PeriodicTimer(TimeSpan.FromSeconds(15));
        Task<bool>? readTask = null;  // 跨循环保活同一个等待,避免多等待者抢占
        Task<bool>? agentTask = null;
        Task<bool>? pingTask = null;  // PeriodicTimer 同时只允许一个 WaitForNextTickAsync 等待者
        try
        {
            while (true)
            {
                readTask ??= sessionReader.WaitToReadAsync(ctx.RequestAborted).AsTask();
                agentTask ??= agentReader.WaitToReadAsync(ctx.RequestAborted).AsTask();
                pingTask ??= ping.WaitForNextTickAsync(ctx.RequestAborted).AsTask();
                var done = await Task.WhenAny(readTask, agentTask, pingTask);
                if (done == readTask)
                {
                    if (!await readTask)
                        break;
                    readTask = null;
                    while (sessionReader.TryRead(out var frame))
                        await res.WriteAsync("data: " + frame + "\n\n", ctx.RequestAborted);
                }
                else if (done == agentTask)
                {
                    if (!await agentTask)
                        break;
                    agentTask = null;
                    while (agentReader.TryRead(out var frame))
                        await res.WriteAsync("data: " + frame + "\n\n", ctx.RequestAborted);
                }
                else
                {
                    await pingTask;
                    pingTask = null;
                    await res.WriteAsync(": ping\n\n", ctx.RequestAborted);
                }
                await res.Body.FlushAsync(ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开:正常退出
        }
        catch (IOException)
        {
            // 连接管道中断:同上
        }
    }

    /// <summary>单总线 SSE 泵:把订阅通道里的帧逐条写入响应直至断开,期间 15s ping 保活。</summary>
    private static async Task SsePumpAsync(ChannelReader<string> reader, HttpContext ctx)
    {
        var res = ctx.Response;
        res.Headers.ContentType = "text/event-stream";
        res.Headers.CacheControl = "no-cache";
        res.Headers["X-Accel-Buffering"] = "no";
        await res.StartAsync(ctx.RequestAborted);
        var ping = new PeriodicTimer(TimeSpan.FromSeconds(15));
        Task<bool>? readTask = null;  // 跨循环保活同一个等待,避免多等待者抢占
        Task<bool>? pingTask = null;  // PeriodicTimer 同时只允许一个 WaitForNextTickAsync 等待者:
                                      // 旧 ping 任务未完成前再取会抛 InvalidOperationException,连接即断
        try
        {
            while (true)
            {
                readTask ??= reader.WaitToReadAsync(ctx.RequestAborted).AsTask();
                pingTask ??= ping.WaitForNextTickAsync(ctx.RequestAborted).AsTask();
                var done = await Task.WhenAny(readTask, pingTask);
                if (done == readTask)
                {
                    if (!await readTask)
                        break; // 通道关闭(不会发生:总线常驻)
                    readTask = null;
                    while (reader.TryRead(out var frame))
                        await res.WriteAsync("data: " + frame + "\n\n", ctx.RequestAborted);
                }
                else
                {
                    await pingTask; // 该 tick 已触发:消费后重新起等
                    pingTask = null;
                    await res.WriteAsync(": ping\n\n", ctx.RequestAborted); // 保活注释帧
                }
                await res.Body.FlushAsync(ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开:正常退出
        }
        catch (IOException)
        {
            // 连接管道中断:同上
        }
    }

    /// <summary>从 Kestrel 实际绑定地址取端口(探测端口全占用时回退 0 = 系统随机分配)。</summary>
    private static int ParsePort(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (address is not null && int.TryParse(address.Substring(address.LastIndexOf(':') + 1), out var port))
            return port;
        return DefaultPort;
    }

    /// <summary>默认端口;被占用时向后顺延探测(桌面单机场景,探测-绑定窗口极小,TOCTOU 仅理论存在)。
    /// 全部被占时回落默认端口而非 0:端口 0 = 系统随机分配,Port/BaseUrl 的报告值会与实际监听端口失真;
    /// 回落后的绑定失败以启动异常诚实暴露,好过静默漂移。</summary>
    private static int FindFreePort(int start)
    {
        for (var port = start; port < start + 20; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // 被占用:探测下一个
            }
        }
        return start;
    }

    /// <summary>REST API:会话列表/快照/新建、发送/停止、SSE 流、鉴权解析。
    /// 引擎(AgentManager/会话 VM)只在 UI 线程变更:一律经 Dispatcher 编组,快照在 UI 线程拍平。</summary>
    private void MapApi(WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Json(new
        {
            ok = true,
            port = Port,
            agents = _manager.Agents.Count(a => a.Enabled),
        }));

        // Agent 列表:引擎状态只在 UI 线程变更,经 Dispatcher 取快照后再序列化
        // (含停用的 Agent,管理页需要展示启停状态)
        app.MapGet("/api/agents", () =>
        {
            var agents = Application.Current.Dispatcher.Invoke(() =>
                _manager.Agents
                    .Select(a => new AgentInfoDto(a.Id, a.Name, a.Title, a.Enabled, AvatarRev(a.Id)))
                    .ToList());
            return Results.Json(agents);
        });

        // 头像:GET 取图(无则 404,前端回落首字母);POST 上传(data URL,≤5MB 位图);
        // DELETE 移除。文件存 agents/{agentId}/avatar.{ext},rev 用文件修改时间(浏览器缓存随 ?v= 失效)。
        app.MapGet("/api/agents/{agentId}/avatar", (string agentId) =>
        {
            var file = FindAvatarFile(agentId);
            return file is null
                ? Results.NotFound()
                : Results.File(File.ReadAllBytes(file), ImageContentType(Path.GetFileName(file)));
        });

        app.MapPost("/api/agents/{agentId}/avatar", (string agentId, AvatarRequest req) =>
        {
            var dataUrl = req.DataUrl ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    dataUrl, @"^data:image/(png|jpeg|jpg|webp|gif);base64,",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return Results.BadRequest(new { error = "头像仅支持 PNG/JPG/WebP/GIF 图片" });
            byte[] bytes;
            try
            {
                bytes = DecodeDataUrlBytes(dataUrl);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { error = "图片数据无法解析" });
            }
            if (bytes.Length > 5L * 1024 * 1024)
                return Results.BadRequest(new { error = "头像图片超过 5 MB 上限" });

            var ext = "." + dataUrl[5..dataUrl.IndexOf(';')].Split('/')[1].ToLowerInvariant();
            if (ext is ".jpg") ext = ".jpeg";
            var agent = _manager.Agents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null)
                return Results.NotFound();
            var dir = Services.SessionStore.AgentDir(agentId);
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, "avatar.*"))
                File.Delete(old);
            File.WriteAllBytes(Path.Combine(dir, "avatar" + ext), bytes);
            return Results.Json(new { rev = AvatarRev(agentId) });
        });

        app.MapDelete("/api/agents/{agentId}/avatar", (string agentId) =>
        {
            var dir = Services.SessionStore.AgentDir(agentId);
            // 目录不存在 = 本就没有头像:幂等成功而非 DirectoryNotFoundException → 500
            if (Directory.Exists(dir))
                foreach (var old in Directory.GetFiles(dir, "avatar.*"))
                    File.Delete(old);
            return Results.Json(new { ok = true });
        });

        // 界面背景图(聊天区/窗体):上传到 agents/{agentId}/backgrounds/{which}.{ext} 并写回配置的 imagePath
        // (桌面端与网页共用该路径);GET 取图供设置页预览;DELETE 清除文件并恢复默认。
        app.MapGet("/api/agents/{agentId}/backgrounds/{which}", (string agentId, string which) =>
        {
            if (which is not ("chat" or "window"))
                return Results.BadRequest();
            var dir = Path.Combine(Services.SessionStore.AgentDir(agentId), "backgrounds");
            var file = new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" }
                .Select(ext => Path.Combine(dir, which + ext))
                .FirstOrDefault(File.Exists);
            return file is null
                ? Results.NotFound()
                : Results.File(File.ReadAllBytes(file), ImageContentType(Path.GetFileName(file)));
        });

        app.MapPost("/api/agents/{agentId}/backgrounds/{which}", (string agentId, string which, AvatarRequest req) =>
        {
            if (which is not ("chat" or "window"))
                return Results.BadRequest(new { error = "未知背景类型" });
            var dataUrl = req.DataUrl ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    dataUrl, @"^data:image/(png|jpeg|jpg|webp|gif|bmp);base64,",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return Results.BadRequest(new { error = "背景仅支持 PNG/JPG/WebP/GIF/BMP 图片" });
            byte[] bytes;
            try
            {
                bytes = DecodeDataUrlBytes(dataUrl);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { error = "图片数据无法解析" });
            }
            if (bytes.Length > 10L * 1024 * 1024)
                return Results.BadRequest(new { error = "背景图片超过 10 MB 上限" });
            var agent = _manager.Agents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null)
                return Results.NotFound();
            var ext = ExtFromMime(dataUrl);
            var dir = Path.Combine(Services.SessionStore.AgentDir(agentId), "backgrounds");
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, which + ".*"))
                File.Delete(old);
            var file = Path.Combine(dir, which + ext);
            File.WriteAllBytes(file, bytes);
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (which == "chat")
                    agent.ChatBackground.ImagePath = file;
                else
                    agent.WindowBackground.ImagePath = file;
                agent.Save();
            });
            return Results.Json(new { path = file });
        });

        app.MapDelete("/api/agents/{agentId}/backgrounds/{which}", (string agentId, string which) =>
        {
            if (which is not ("chat" or "window"))
                return Results.BadRequest();
            var agent = _manager.Agents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null)
                return Results.NotFound();
            var dir = Path.Combine(Services.SessionStore.AgentDir(agentId), "backgrounds");
            // 目录不存在 = 本就没有背景:幂等成功而非 500
            if (Directory.Exists(dir))
                foreach (var old in Directory.GetFiles(dir, which + ".*"))
                    File.Delete(old);
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (which == "chat")
                    agent.ChatBackground.ImagePath = "";
                else
                    agent.WindowBackground.ImagePath = "";
                agent.Save();
            });
            return Results.Json(new { ok = true });
        });

        // 桌面客户端(WebView2 壳)请求打开某 Agent 的原生窗体:打开/聚焦并直达其最新会话的聊天页
        // (每个 Agent 同时至多一个客户端窗体;纯网页环境不受限,直接走本页路由)
        app.MapPost("/api/agents/{agentId}/open-window", (string agentId) =>
        {
            // 已停用的 Agent 不可开窗(否则 OpenAgent 的 GetOrCreate 会把引擎重新拉起)
            var enabled = Application.Current.Dispatcher.Invoke(() => _manager.IsAgentEnabled(agentId));
            if (!enabled)
                return Results.NotFound();
            _manager.OpenAgent(agentId);
            return Results.Json(new { ok = true });
        });

        // 桌面客户端(WebView2 壳)请求打开某 Agent 的设置页:在其自己的独立窗体里切换,
        // 管理窗(承载 Web 首页)保持不动,避免切走后回不到首页
        app.MapPost("/api/agents/{agentId}/open-settings", (string agentId) =>
        {
            // 已停用的 Agent 不可进设置(与前端菜单禁用规则一致;否则会把引擎重新拉起)
            var enabled = Application.Current.Dispatcher.Invoke(() => _manager.IsAgentEnabled(agentId));
            if (!enabled)
                return Results.NotFound();
            _manager.OpenAgentSettings(agentId);
            return Results.Json(new { ok = true });
        });

        // Agent 管理(管理窗口同路径:增删/启停/重命名,统一在 UI 线程执行)
        app.MapPost("/api/agents", (AgentCreateRequest req) =>
        {
            var name = (req.Name ?? "").Trim();
            if (name.Length == 0)
                return Results.BadRequest(new { error = "请填写 Agent 名称。" });
            var id = Application.Current.Dispatcher.Invoke(() =>
            {
                _manager.AddAgent(name);
                return _manager.Agents.Last().Id;
            });
            return Results.Json(new { id });
        });

        app.MapDelete("/api/agents/{agentId}", (string agentId) =>
        {
            if (_manager.Agents.Count <= 1)
                return Results.BadRequest(new { error = "至少需保留一个 Agent。" });
            if (_manager.Agents.All(a => a.Id != agentId))
                return Results.NotFound();
            Application.Current.Dispatcher.Invoke(() => _manager.DeleteAgent(agentId));
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/agents/{agentId}/enabled", (string agentId, AgentEnabledRequest req) =>
        {
            if (_manager.Agents.All(a => a.Id != agentId))
                return Results.NotFound();
            Application.Current.Dispatcher.Invoke(() => _manager.SetAgentEnabled(agentId, req.Enabled));
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/agents/{agentId}/rename", (string agentId, AgentRenameRequest req) =>
        {
            var name = (req.Name ?? "").Trim();
            if (name.Length == 0)
                return Results.BadRequest(new { error = "名称不能为空。" });
            if (_manager.Agents.All(a => a.Id != agentId))
                return Results.NotFound();
            Application.Current.Dispatcher.Invoke(() => _manager.SetAgentName(agentId, name));
            return Results.Json(new { ok = true });
        });

        // 会话归属定位(URL 只带 sessionId 时,前端经此解析路由目标)
        app.MapGet("/api/session-owner/{sessionId}", (string sessionId) =>
        {
            var agentId = Application.Current.Dispatcher.Invoke(() => _manager.FindSessionOwner(sessionId));
            return agentId is null ? Results.NotFound() : Results.Json(new { agentId });
        });

        app.MapGet("/api/agents/{agentId}/sessions", (string agentId) =>
        {
            var list = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                var pinned = rt.Config.PinnedSessions;
                var slots = rt.ViewModel.Store.ListSessionSlots(); // 列表布局:组内顺序由槽位序号决定
                var rows = rt.ViewModel.Sessions
                    .Where(s => !s.IsArchived) // 归档只读预览实例只在聊天区可见,不进会话列表
                    .DistinctBy(s => s.SessionId) // 防御:同一会话出现两行会让前端 v-for 重复 key 错配(右键打到错行)
                    .Select(s => new
                    {
                        S = s,
                        Pinned = pinned.Contains(s.SessionId),
                        // 无槽的按 -1 回带(启动补槽后正常不会发生)→ 前端排组尾
                        Slot = slots.TryGetValue(s.SessionId, out var sl) ? sl : (FolderId: (string?)(null), Sort: -1L),
                    })
                    .ToList();
                var dto = new List<SessionListItemDto>(rows.Count);
                // 置顶区保持 VM 顺序;其余按(组, 组内序号)排——前端按 folderId 重新分组,
                // 组内把会话与文件夹槽位按同一序号合并排序(拖拽排序的结果),跨组相对顺序无意义
                foreach (var r in rows.Where(r => r.Pinned))
                    dto.Add(new SessionListItemDto(r.S.SessionId, r.S.Title, r.S.LastMessageTimeText,
                        r.S.IsStreaming, r.S.IsCompressing, r.S.HasError, r.S.Unread, true, r.Slot.FolderId, r.Slot.Sort));
                foreach (var r in rows.Where(r => !r.Pinned)
                             .OrderBy(r => r.Slot.FolderId is null ? "" : r.Slot.FolderId)
                             .ThenBy(r => r.Slot.Sort))
                    dto.Add(new SessionListItemDto(r.S.SessionId, r.S.Title, r.S.LastMessageTimeText,
                        r.S.IsStreaming, r.S.IsCompressing, r.S.HasError, r.S.Unread, false, r.Slot.FolderId, r.Slot.Sort));
                return dto;
            });
            return Results.Json(list);
        });

        // 会话列表置顶(存该 Agent 的配置文件,跨重启保留)
        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/pin", (string agentId, string sessionId, PinRequest req) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                var changed = false;
                if (req.Pinned)
                {
                    if (!rt.Config.PinnedSessions.Contains(sessionId))
                    {
                        rt.Config.PinnedSessions.Add(sessionId);
                        changed = true;
                    }
                }
                else
                    changed = rt.Config.PinnedSessions.Remove(sessionId);
                if (changed)
                    rt.Config.Save();
            });
            return Results.Json(new { ok = true });
        });

        // ───────── 会话列表文件夹(嵌套)+ 组内排序:存 sessions.db 的 folders / session_slots 表 ─────────
        // 布局变更一律广播 sessions_changed:其他打开的窗口(网页/其他 Agent 视图)经 SSE 即时刷新

        app.MapGet("/api/agents/{agentId}/folders", (string agentId) =>
        {
            var rows = Application.Current.Dispatcher.Invoke(() =>
            {
                var store = _manager.RequireRuntime(agentId).ViewModel.Store;
                var slots = store.ListSessionSlots(); // 列表位置以槽位序号为准(folders.sort 仅作兄弟相对次序)
                return store.ListFolders().Select(f => new FolderDto(f.Id, f.Name, f.ParentId,
                    slots.TryGetValue(f.Id, out var sl) ? sl.Sort : -1, f.Color)).ToList();
            });
            return Results.Json(rows);
        });

        app.MapPost("/api/agents/{agentId}/folders", (string agentId, FolderCreateRequest? req) =>
        {
            var name = (req?.Name ?? "").Trim();
            if (name.Length == 0)
                return Results.BadRequest(new { error = "文件夹名称不能为空。" });
            string? err = Application.Current.Dispatcher.Invoke(() =>
            {
                var store = _manager.RequireRuntime(agentId).ViewModel.Store;
                if (req!.ParentId is not null && !store.FolderExists(req.ParentId))
                    return "父文件夹不存在。";
                store.CreateFolder(name, req.ParentId);
                return null;
            });
            if (err is not null)
                return Results.BadRequest(new { error = err });
            WebEventHub.PublishAgent(agentId, "sessions_changed");
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/agents/{agentId}/folders/{folderId}/rename", (string agentId, string folderId, FolderRenameRequest? req) =>
        {
            var name = (req?.Name ?? "").Trim();
            if (name.Length == 0)
                return Results.BadRequest(new { error = "文件夹名称不能为空。" });
            var ok = Application.Current.Dispatcher.Invoke(() =>
                _manager.RequireRuntime(agentId).ViewModel.Store.RenameFolder(folderId, name));
            if (!ok)
                return Results.NotFound();
            WebEventHub.PublishAgent(agentId, "sessions_changed");
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/agents/{agentId}/folders/{folderId}/move", (string agentId, string folderId, FolderMoveRequest? req) =>
        {
            var parentId = req?.ParentId;
            var err = Application.Current.Dispatcher.Invoke(() =>
            {
                var store = _manager.RequireRuntime(agentId).ViewModel.Store;
                if (parentId is not null && !store.FolderExists(parentId))
                    return "目标文件夹不存在。";
                if (!store.MoveFolder(folderId, parentId))
                    return "不能把文件夹移到自身或它的子文件夹里。";
                return null;
            });
            if (err is not null)
                return Results.BadRequest(new { error = err });
            WebEventHub.PublishAgent(agentId, "sessions_changed");
            return Results.Json(new { ok = true });
        });

        // 删除文件夹:子文件夹与组内会话降级到其父级(不丢数据);顺手清掉它的折叠状态
        app.MapDelete("/api/agents/{agentId}/folders/{folderId}", (string agentId, string folderId) =>
        {
            var ok = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                if (!rt.ViewModel.Store.DeleteFolder(folderId))
                    return false;
                if (rt.Config.CollapsedFolderIds.Remove(folderId))
                    rt.Config.Save();
                return true;
            });
            if (!ok)
                return Results.NotFound();
            WebEventHub.PublishAgent(agentId, "sessions_changed");
            return Results.Json(new { ok = true });
        });

        // 把会话移入某组末尾(null = 根组);配合 reorder 覆盖"拖到具体位置"
        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/move", (string agentId, string sessionId, SessionMoveRequest? req) =>
        {
            var err = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                if (rt.ViewModel.Sessions.All(s => s.SessionId != sessionId))
                    return "会话不存在。";
                var store = rt.ViewModel.Store;
                if (req?.FolderId is not null && !store.FolderExists(req.FolderId))
                    return "目标文件夹不存在。";
                store.MoveSessionToFolder(sessionId, req?.FolderId);
                return null;
            });
            if (err is not null)
                return Results.BadRequest(new { error = err });
            WebEventHub.PublishAgent(agentId, "sessions_changed");
            return Results.Json(new { ok = true });
        });

        // 整组重排(拖拽落点后的整序提交;未知 id 服务端忽略)
        app.MapPost("/api/agents/{agentId}/sessions/reorder", (string agentId, SessionsReorderRequest? req) =>
        {
            var folderId = req?.FolderId;
            var err = Application.Current.Dispatcher.Invoke(() =>
            {
                var store = _manager.RequireRuntime(agentId).ViewModel.Store;
                if (folderId is not null && !store.FolderExists(folderId))
                    return "目标文件夹不存在。";
                store.SetSessionOrder(folderId, req?.OrderedIds ?? new List<string>());
                return null;
            });
            if (err is not null)
                return Results.BadRequest(new { error = err });
            WebEventHub.PublishAgent(agentId, "sessions_changed");
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/agents/{agentId}/folders/reorder", (string agentId, FoldersReorderRequest? req) =>
        {
            var parentId = req?.ParentId;
            var err = Application.Current.Dispatcher.Invoke(() =>
            {
                var store = _manager.RequireRuntime(agentId).ViewModel.Store;
                if (parentId is not null && !store.FolderExists(parentId))
                    return "目标文件夹不存在。";
                store.SetFolderOrder(parentId, req?.OrderedIds ?? new List<string>());
                return null;
            });
            if (err is not null)
                return Results.BadRequest(new { error = err });
            WebEventHub.PublishAgent(agentId, "sessions_changed");
            return Results.Json(new { ok = true });
        });

        // 文件夹预设色(color = 前端色板 id,'' = 默认不着色);文件夹不存在 404
        app.MapPost("/api/agents/{agentId}/folders/{folderId}/color", (string agentId, string folderId, FolderColorRequest? req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke(() =>
                _manager.RequireRuntime(agentId).ViewModel.Store.SetFolderColor(folderId, req?.Color ?? ""));
            if (!ok)
                return Results.NotFound();
            WebEventHub.PublishAgent(agentId, "sessions_changed");
            return Results.Json(new { ok = true });
        });

        // 文件夹折叠状态(存该 Agent 配置文件,跨重启恢复;前端展开/收起切换时整表上报;
        // 字面量段 /folders/collapse-state 与参数路由 /folders/{folderId}/… 段数不同,互不干扰)
        app.MapGet("/api/agents/{agentId}/folders/collapse-state", (string agentId) =>
        {
            var ids = Application.Current.Dispatcher.Invoke(() =>
                _manager.RequireRuntime(agentId).Config.CollapsedFolderIds.ToList());
            return Results.Json(ids);
        });

        app.MapPost("/api/agents/{agentId}/folders/collapse-state", (string agentId, CollapseStateRequest? req) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                var ids = req?.Ids ?? new List<string>();
                if (rt.Config.CollapsedFolderIds.Count == ids.Count && ids.All(rt.Config.CollapsedFolderIds.Contains))
                    return; // 无变化:不落盘
                rt.Config.CollapsedFolderIds = ids;
                rt.Config.Save();
            });
            return Results.Json(new { ok = true });
        });

        // 记录"最近打开的会话"(前端会话上屏时上报):聊天窗下次开窗/托盘点名优先回到它,
        // 而非按消息时间猜的"最新会话"——用户可能一直开着旧会话,只是没再发消息
        app.MapPost("/api/agents/{agentId}/last-session", (string agentId, LastSessionRequest req) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                if (string.IsNullOrEmpty(req.SessionId))
                    return;
                if (rt.Config.LastSessionId != req.SessionId)
                {
                    rt.Config.LastSessionId = req.SessionId;
                    rt.Config.Save();
                }
                // 每次上报都清未读(蓝)/出错(红)点 —— 同会话重复上报也清:覆盖"回复收完置位
                // 与上报赛跑""视图复用漏报后补报"等时序;属性变化经 sessions_changed 推送即时熄灭
                var viewed = rt.ViewModel.Sessions.FirstOrDefault(s => s.SessionId == req.SessionId);
                if (viewed is not null)
                {
                    viewed.Unread = false;
                    viewed.HasError = false;
                }
            });
            return Results.Json(new { ok = true });
        });

        // 读取"最近打开的会话"(网页开窗解析用;与桌面壳 StartupChatResolver 同源,跨端回到同一处)
        app.MapGet("/api/agents/{agentId}/last-session", (string agentId) =>
        {
            var id = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                return string.IsNullOrEmpty(rt.Config.LastSessionId) ? null : rt.Config.LastSessionId;
            });
            return Results.Json(new { sessionId = id });
        });

        app.MapPost("/api/agents/{agentId}/sessions", async (string agentId, SessionCreateRequest? req) =>
        {
            // fromSessionId:Web 侧当前浏览的会话 → 新会话继承其供应商/模型/思考等级
            // (WPF 的"当前选中会话"与 Web 浏览会话不同步,不显式传会继承到错误的选择);
            // folderId:新对话直接建在该文件夹里(缺省根组)
            var folderId = req?.FolderId;
            if (folderId is not null && !Application.Current.Dispatcher.Invoke(
                    () => _manager.RequireRuntime(agentId).ViewModel.Store.FolderExists(folderId)))
                return Results.BadRequest(new { error = "目标文件夹不存在。" });
            var snapshot = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                return SessionSnapshot.Of(rt.ViewModel.NewSession(req?.FromSessionId, folderId),
                    chatBg: rt.Config.ChatBackground, windowBg: rt.Config.WindowBackground);
            }).Task;
            return Results.Json(snapshot);
        });

        // 历史分页(均可选,全不传 = 全量快照,兼容旧调用):limit = 取末尾 N 行(首屏窗口);
        // after = seq > after 的全部行(刷新已加载窗口);before = seq < before 的末尾 N 行(向上翻页)
        app.MapGet("/api/agents/{agentId}/sessions/{sessionId}",
            (string agentId, string sessionId, int? limit, int? after, int? before) =>
            {
                var snapshot = Application.Current.Dispatcher.Invoke(() =>
                {
                    var rt = _manager.RequireRuntime(agentId);
                    // 不在内存里的归档会话:懒恢复只读预览实例(Web 归档列表点开 → 聊天区回看)
                    var session = FindSession(agentId, sessionId) ?? rt.ViewModel.RestoreArchivedPreview(sessionId);
                    if (session is null)
                        return null;
                    return SessionSnapshot.Of(session, rt.ViewModel.ContextLimitOf(session),
                        rt.Config.ChatBackground, rt.Config.WindowBackground,
                        limit: limit, after: after, before: before);
                });
                return snapshot is null ? Results.NotFound() : Results.Json(snapshot);
            });

        // context 原始消息(display 气泡 MsgIds 回链入口):前端凭气泡的 msgIds 按 id 展开详情
        // (用量/模型/思考/工具参数);读 messages 表,含被压缩留档行——已删轮次的历史消息也可取回
        app.MapGet("/api/agents/{agentId}/sessions/{sessionId}/messages/{messageId}",
            (string agentId, string sessionId, string messageId) =>
            {
                var msg = Application.Current.Dispatcher.Invoke(() =>
                {
                    var rt = _manager.RequireRuntime(agentId);
                    // 归档预览的气泡悬停/详情回链也要能取到原始消息
                    var session = FindSession(agentId, sessionId) ?? rt.ViewModel.RestoreArchivedPreview(sessionId);
                    if (session is null)
                        return null;
                    return rt.ViewModel.Store.GetContextMessage(sessionId, messageId);
                });
                return msg is null ? Results.NotFound() : Results.Json(msg);
            });

        // display 气泡悬停详情:按 msgIds 聚合原始消息(生成时间/服务端真实用量/所用模型,逐消息归因);
        // 一次气泡至多几个 id,读 messages 表(含 compacted=1 留档行);id 全缺失(如旧会话无关联)404
        app.MapGet("/api/agents/{agentId}/sessions/{sessionId}/messages-summary",
            (string agentId, string sessionId, [FromQuery] string[]? ids) =>
            {
                if (ids is null || ids.Length == 0)
                    return Results.BadRequest();
                var messages = Application.Current.Dispatcher.Invoke(() =>
                {
                    var rt = _manager.RequireRuntime(agentId);
                    var session = FindSession(agentId, sessionId) ?? rt.ViewModel.RestoreArchivedPreview(sessionId);
                    if (session is null)
                        return new List<ChatMessage>();
                    return rt.ViewModel.Store.GetContextMessages(sessionId, ids);
                });
                if (messages.Count == 0)
                    return Results.NotFound();
                var times = messages.Select(m => m.TimeCreated).Where(t => t is not null).ToList();
                var completed = messages.Select(m => m.TimeCompleted).Where(t => t is not null).ToList();
                var usage = messages.Where(m => m.Usage is not null).Select(m => m.Usage!).ToArray();
                return Results.Json(new MessageSummaryDto(
                    times.Count > 0 ? times.Min() : null,
                    completed.Count > 0 ? completed.Max() : null,
                    usage.Sum(u => u.PromptTokens),
                    usage.Sum(u => u.CompletionTokens),
                    usage.Sum(u => u.TotalTokens),
                    usage.Sum(u => u.CachedTokens),
                    usage.Sum(u => u.ReasoningTokens),
                    messages.LastOrDefault(m => m.Provider is not null)?.Provider,
                    messages.LastOrDefault(m => m.Model is not null)?.Model));
            });

        // 发送即返回:流式结果经 SSE 推送;会话忙 / 不存在以状态码区分(引擎内部还有二次校验)
        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/message",
            (string agentId, string sessionId, SendMessageRequest req) =>
            {
                // 与 WPF 加附件时的同款上限(20MB 图片 / 512KB 文本)
                const long maxImageBytes = 20L * 1024 * 1024;
                const long maxTextBytes = 512L * 1024;
                const int maxQueuedMessages = 50; // 中途消息队列容量(防失控堆积)

                var outcome = Application.Current.Dispatcher.Invoke(() =>
                {
                    var vm = RequireVm(agentId);
                    var session = FindSession(agentId, sessionId);
                    if (session is null)
                        return SendResult.NotFound;
                    if (session.IsArchived)
                        return SendResult.Archived; // 归档预览只读:发送/入队一律拒绝

                    // 先校验并收集附件(不动组合器状态):闲时进发送管线、忙时进中途队列,两条路共用同一份校验
                    var collected = new List<AttachmentViewModel>();
                    try
                    {
                        foreach (var dataUrl in req.Images ?? [])
                        {
                            // 位图走多模态图片;PDF/音频/视频走 file 内容片段(模型需声明支持对应媒体类型)
                            var isImage = System.Text.RegularExpressions.Regex.IsMatch(
                                dataUrl, @"^data:image/(png|jpeg|gif|webp|bmp);base64,",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            var isMedia = System.Text.RegularExpressions.Regex.IsMatch(
                                dataUrl, @"^data:(application/pdf|audio/[\w.+-]+|video/[\w.+-]+);base64,",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (!isImage && !isMedia)
                                return SendResult.BadAttachment;
                            var bytes = DecodeDataUrlBytes(dataUrl);
                            if (bytes.Length > maxImageBytes)
                                return SendResult.TooLarge($"附件超过 {FormatSize(maxImageBytes)} 上限");
                            collected.Add(new AttachmentViewModel(
                                AttachmentKind.Image, isImage ? "图片" + ExtFromMime(dataUrl) : "附件" + ExtFromMime(dataUrl), null)
                            {
                                DataUrl = dataUrl,
                            });
                        }
                        foreach (var file in req.TextFiles ?? [])
                        {
                            if (Encoding.UTF8.GetByteCount(file.Content) > maxTextBytes)
                                return SendResult.TooLarge($"文本文件「{file.Name}」超过 {FormatSize(maxTextBytes)} 上限");
                            collected.Add(new AttachmentViewModel(
                                AttachmentKind.TextFile, file.Name, null)
                            {
                                TextContent = file.Content,
                                SizeText = FormatSize(Encoding.UTF8.GetByteCount(file.Content)),
                            });
                        }
                    }
                    catch (FormatException)
                    {
                        return SendResult.BadAttachment;
                    }

                    if (session.IsStreaming || session.Session.IsRunning)
                    {
                        // 运行中发送 = 中途插话:进会话队列,下一次请求发出前按 FIFO 拼进上下文(不打断当前轮)
                        var entry = new QueuedUserMessage
                        {
                            Text = req.Text ?? "",
                            Images = [.. collected.Where(a => a.DataUrl is not null).Select(a => a.DataUrl!)],
                            TextFiles = [.. collected.Where(a => a.Kind == AttachmentKind.TextFile)
                                .Select(a => new QueuedTextFile(a.Name, a.TextContent ?? ""))],
                            Defer = req.Defer, // Ctrl+Enter 入队:标记"本轮后发送",不拼进进行中请求
                        };
                        if (string.IsNullOrWhiteSpace(entry.Text)
                            && entry.Images is not { Count: > 0 }
                            && entry.TextFiles is not { Count: > 0 })
                            return SendResult.Started; // 空消息:与闲时一致,静默忽略
                        if (session.Session.QueuedMessages.Count >= maxQueuedMessages)
                            return SendResult.TooLarge($"消息队列已满(上限 {maxQueuedMessages} 条)");
                        session.Session.Enqueue(entry);
                        return SendResult.Queued; // 响应体区分入队/开跑:前端据此决定是否推本地气泡(以服务端为准)
                    }

                    // 附件随请求传给目标会话,不经 VM 级 Attachments(= SelectedSession 草稿)中转:
                    // Web 切会话不改后端选中,经中转会挂错会话、误发/误清桌面正在编辑的草稿
                    _ = vm.SendAsync(req.Text ?? "", session, collected).ContinueWith(
                        t => Log.Global.Error("Web 发送失败", t.Exception?.GetBaseException()),
                        TaskContinuationOptions.OnlyOnFaulted);
                    return SendResult.Started;
                });
                return outcome.Outcome switch
                {
                    // 202 + 响应体标注入队去向(Accepted 无载荷重载,经 Json 指定状态码)
                    SendOutcome.Started => Results.Json(new { queued = false }, statusCode: StatusCodes.Status202Accepted),
                    SendOutcome.Queued => Results.Json(new { queued = true }, statusCode: StatusCodes.Status202Accepted),
                    SendOutcome.Archived => Results.Json(new { error = "会话已归档,无法发送新消息" }, statusCode: StatusCodes.Status403Forbidden),
                    SendOutcome.TooLarge => Results.BadRequest(new { error = outcome.Error }),
                    SendOutcome.BadAttachment => Results.BadRequest(new { error = "附件格式无法解析" }),
                    _ => Results.NotFound(),
                };
            });

        // 中途消息队列:取出即移除——取消 = 丢弃返回值;重新编辑 = 用返回的原文/附件回填输入框,再发送时回到队尾。
        // 移除经 QueueChanged 事件自动广播 queue_changed,输入框上方的队列条随之刷新
        app.MapDelete("/api/agents/{agentId}/sessions/{sessionId}/queue/{queueId}",
            (string agentId, string sessionId, string queueId) =>
            {
                var removed = Application.Current.Dispatcher.Invoke(() =>
                {
                    var session = FindSession(agentId, sessionId);
                    return session?.Session.RemoveQueued(queueId);
                });
                return removed is null
                    ? Results.NotFound(new { error = "队列项不存在" })
                    : Results.Json(new QueuedEditDto(removed.Id, removed.Text,
                        removed.Images is { Count: > 0 } ? [.. removed.Images] : null,
                        removed.TextFiles is { Count: > 0 }
                            ? [.. removed.TextFiles.Select(f => new QueuedEditFileDto(f.Name, f.Content))]
                            : null));
            });

        // 中途消息队列:切换某条"本轮后发送"标记(队列条按钮);404 = 已被消费或移除。
        // 标记经 QueueChanged 自动广播,队列条随全量快照刷新
        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/queue/{queueId}",
            (string agentId, string sessionId, string queueId, QueueDeferRequest req) =>
            {
                var ok = Application.Current.Dispatcher.Invoke(() =>
                {
                    var session = FindSession(agentId, sessionId);
                    return session?.Session.SetQueuedDefer(queueId, req.Defer) ?? false;
                });
                return ok ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "队列项不存在" });
            });

        // 中途消息队列:拖拽把手调整顺序(index = 移除自身后的插入位置);404 = 已被消费或移除。
        // 新序经 QueueChanged 自动广播,队列条随全量快照刷新
        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/queue/{queueId}/move",
            (string agentId, string sessionId, string queueId, QueueMoveRequest req) =>
            {
                var ok = Application.Current.Dispatcher.Invoke(() =>
                {
                    var session = FindSession(agentId, sessionId);
                    return session?.Session.MoveQueued(queueId, req.Index) ?? false;
                });
                return ok ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "队列项不存在" });
            });

        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/stop", (string agentId, string sessionId) =>
        {
            var stopped = Application.Current.Dispatcher.Invoke(() =>
            {
                var vm = _manager.RequireRuntime(agentId).ViewModel;
                var session = vm.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
                if (session is null || session.IsArchived) // 归档预览永远不会运行,终止一并拒绝
                    return false;
                vm.StopSession(session);
                return true;
            });
            return stopped ? Results.Json(new { ok = true }) : Results.NotFound();
        });

        // SSE:每连接一个订阅者;多标签/多客户端同会话互不影响。data 帧为 {"type":..,"data":{..}}
        // withAgent=1:同时并入 Agent 级总线(sessions_changed 等跨会话事件)——页面单连接获得全部
        // 事件;此前会话+Agent 两条 SSE 常驻,多 Agent 多窗口把 HTTP/1.1 每主机 6 连接上限吃满,
        // 后续所有请求在浏览器内排队 pending、整个前端假死(后端无痕,curl 永远正常)
        app.MapGet("/api/agents/{agentId}/sessions/{sessionId}/stream",
            async (string agentId, string sessionId, string? withAgent, HttpContext ctx) =>
            {
                // 已停用 Agent 的流直接 404:前端 EventSource 收到非 2xx 即关闭且不再重连,
                // 断流-重连-拉快照的循环也就不会复活其引擎
                if (!Application.Current.Dispatcher.Invoke(() => _manager.IsAgentEnabled(agentId)))
                    return Results.NotFound();
                // 手动解析而非 bool? 绑定:bool? 只认 true/false,前端传 1 时绑定失败=裸 400,
                // 页面 SSE 会因此永久断开(所有帧收不到,转圈到死)
                var merged = withAgent is "1" or "true";
                var (reader, subscription) = WebEventHub.Bus(agentId, sessionId).Subscribe();
                using (subscription)
                {
                    if (merged)
                    {
                        var (agentReader, agentSub) = WebEventHub.AgentBus(agentId).Subscribe();
                        using (agentSub)
                            await SsePumpDualAsync(reader, agentReader, ctx);
                    }
                    else
                    {
                        await SsePumpAsync(reader, ctx);
                    }
                }
                return Results.NoContent(); // 流式响应已写完,返回值仅为让 lambda 各路径一致(停用守卫走 404 早退)
            });

        // Agent 级 SSE 流:会话列表状态(运行/压缩/出错/未读小点)变化即推 sessions_changed,
        // 所有打开的窗口立即强刷列表,不再干等 5 秒轮询(轮询保留,兜底相对时间文本与边角遗漏)
        app.MapGet("/api/agents/{agentId}/stream",
            async (string agentId, HttpContext ctx) =>
            {
                if (!Application.Current.Dispatcher.Invoke(() => _manager.IsAgentEnabled(agentId)))
                    return Results.NotFound();
                var (reader, subscription) = WebEventHub.AgentBus(agentId).Subscribe();
                using (subscription)
                    await SsePumpAsync(reader, ctx);
                return Results.NoContent();
            });

        // 子代理运行流:与主会话同 SSE 协议(事件总线按 runId 分键);模态框订阅它看子代理的流式过程
        app.MapGet("/api/agents/{agentId}/sub-agents/runs/{runId}/stream",
            async (string agentId, string runId, HttpContext ctx) =>
            {
                if (!Application.Current.Dispatcher.Invoke(() => _manager.IsAgentEnabled(agentId)))
                    return Results.NotFound();
                var (reader, subscription) = WebEventHub.Bus(agentId, runId).Subscribe();
                using (subscription)
                    await SsePumpAsync(reader, ctx);
                return Results.NoContent();
            });

        // 子代理运行列表(侧边栏「智能体」页签):id/名称/状态/提示词摘要/起止时间;
        // 按会话过滤——子代理运行与 session 绑定,其他对话看不到本会话之外的运行(与 bg_run 任务一致)
        app.MapGet("/api/agents/{agentId}/sessions/{sessionId}/sub-agents/runs", (string agentId, string sessionId) =>
        {
            var runs = Application.Current.Dispatcher.Invoke(() =>
                _manager.RequireRuntime(agentId).Host.SubAgent.Manager.Snapshot(sessionId)
                    .Select(r => new
                    {
                        id = r.Id,
                        name = r.Name,
                        title = r.Title,
                        status = r.Status.ToString().ToLowerInvariant(),
                        prompt = r.Prompt.Length > 120 ? r.Prompt[..117] + "…" : r.Prompt,
                        startedAt = r.StartedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                        endedAt = r.EndedAt?.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    }).ToList());
            return Results.Json(runs);
        });

        // 子代理运行详情 + 事件帧快照(模态框重放):frames 与 SSE 帧同协议({type,data})
        app.MapGet("/api/agents/{agentId}/sub-agents/runs/{runId}", (string agentId, string runId) =>
        {
            if (!int.TryParse(runId, out var runIdInt))
                return Results.NotFound();
            var data = Application.Current.Dispatcher.Invoke(() =>
            {
                var run = _manager.RequireRuntime(agentId).Host.SubAgent.Manager.Find(runIdInt);
                if (run is null)
                    return null;
                return new
                {
                    id = run.Id,
                    name = run.Name,
                    title = run.Title,
                    status = run.Status.ToString().ToLowerInvariant(),
                    prompt = run.Prompt,
                    startedAt = run.StartedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    endedAt = run.EndedAt?.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    finalContent = run.FinalContent,
                    error = run.Error,
                    framesTruncated = run.FramesTruncated,
                    frames = run.Frames.Select(f => new { type = f.Type, data = f.Data }).ToList(),
                };
            });
            return data is null ? Results.NotFound() : Results.Json(data);
        });

        // 侧边栏「智能体」清除:移除已结束的子代理运行(运行中需先 sub_kill;不存在/运行中 → 404)
        app.MapPost("/api/agents/{agentId}/sub-agents/runs/{runId}/delete", (string agentId, string runId) =>
        {
            if (!int.TryParse(runId, out var runIdInt))
                return Results.NotFound();
            var ok = Application.Current.Dispatcher.Invoke(() =>
                _manager.RequireRuntime(agentId).Host.SubAgent.Manager.Remove(runIdInt));
            return ok ? Results.Json(new { ok = true }) : Results.NotFound();
        });

        // 工具授权解析:UiCallbacks 的鉴权卡片在 WebEventHub 登记,浏览器经此放行/拒绝/信任。
        // ResolveAuth → card.Trust/Resolve → ApplyTrust 改运行策略+会话白名单 List 与 VM 状态:
        // 必须编组到 UI 线程(Kestrel 线程直呼会与 UI 线程的落盘枚举并发改普通 List 互踩,白名单丢失)
        app.MapPost("/api/auth/{authId}", (string authId, AuthRequest req) =>
            Application.Current.Dispatcher.Invoke(() => WebEventHub.ResolveAuth(authId, req.Allow, req.Trust))
                ? Results.Json(new { ok = true })
                : Results.NotFound());

        // ask 工具应答:答案 JSON 原样回传给模型;skipped = 用户忽略(模型自行合理假设)
        app.MapPost("/api/ask/{askId}", (string askId, AskResolveRequest req) =>
        {
            string? answers = req.Skipped || req.Answers is null
                ? null
                : req.Answers.Value.GetRawText();
            return WebEventHub.ResolveAsk(askId, answers)
                ? Results.Json(new { ok = true })
                : Results.NotFound();
        });

        // 待决询问清单:页面刷新/重开后重建询问面板(登记簿线程安全,无需 Dispatcher)
        app.MapGet("/api/agents/{agentId}/sessions/{sessionId}/asks", (string agentId, string sessionId) =>
            Results.Json(WebEventHub.PendingAsksFor(agentId, sessionId)));

        // ───────── M2a:会话管理 / 模型切换 / 斜杠命令 / 图片 ─────────

        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/rename", (string agentId, string sessionId, RenameRequest req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke(() =>
            {
                var session = FindSession(agentId, sessionId);
                if (session is null || string.IsNullOrWhiteSpace(req.Title))
                    return false;
                session.Title = req.Title.Trim();
                RequireVm(agentId).SaveSession(session); // 空会话改名也要落盘
                return true;
            });
            return ok ? Results.Json(new { ok = true }) : Results.NotFound();
        });

        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/archive", (string agentId, string sessionId) =>
        {
            string? error = null;
            var ok = Application.Current.Dispatcher.Invoke(() =>
            {
                var vm = RequireVm(agentId);
                var session = FindSession(agentId, sessionId);
                if (session is null)
                    return false;
                if (session.IsStreaming || session.Session.IsRunning)
                {
                    // 运行态归档:轮末落盘会把 archived=1 覆写回 0(会话"复活"),必须拒绝
                    error = "会话正在运行,请先停止再归档";
                    return false;
                }
                vm.ArchiveSession(session);
                return true;
            });
            return ok ? Results.Json(new { ok = true })
                : error is not null ? Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict)
                : Results.NotFound();
        });

        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/unarchive", (string agentId, string sessionId) =>
        {
            var ok = Application.Current.Dispatcher.Invoke(() =>
            {
                var vm = RequireVm(agentId);
                // 只读预览实例也在 Sessions 里:必须排除,否则"移出归档"会被当幂等直接放过
                if (vm.Sessions.Any(s => s.SessionId == sessionId && !s.IsArchived))
                    return true; // 已在列表:幂等
                vm.UnarchiveSession(sessionId, select: false); // Web 移出不改后端选中:前端停在归档面板,不跳转
                return vm.Sessions.Any(s => s.SessionId == sessionId && !s.IsArchived);
            });
            return ok ? Results.Json(new { ok = true }) : Results.NotFound();
        });

        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/fork", (string agentId, string sessionId, ForkRequest req) =>
        {
            var forkedId = Application.Current.Dispatcher.Invoke(() =>
            {
                var vm = RequireVm(agentId);
                var forked = vm.ForkSession(FindSession(agentId, sessionId),
                    string.IsNullOrWhiteSpace(req.Title) ? null : req.Title.Trim());
                return forked?.SessionId;
            });
            return forkedId is null ? Results.NotFound() : Results.Json(new { sessionId = forkedId });
        });

        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/delete", (string agentId, string sessionId) =>
        {
            string? error = null;
            var ok = Application.Current.Dispatcher.Invoke(() =>
            {
                var vm = RequireVm(agentId);
                // 运行态删除:轮末 finally 落盘会把会话行重插回来(僵尸复活),必须先停后删
                if (vm.Sessions.Any(s => s.SessionId == sessionId && (s.IsStreaming || s.Session.IsRunning)))
                {
                    error = "会话正在运行,请先停止再删除";
                    return false;
                }
                if (vm.Sessions.Any(s => s.SessionId == sessionId) ||
                    vm.ArchivedSessions().Any(s => s.Id == sessionId))
                {
                    vm.DeleteSessionForever(sessionId);
                    return true;
                }
                return false;
            });
            return ok ? Results.Json(new { ok = true })
                : error is not null ? Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict)
                : Results.NotFound();
        });

        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/compact",
            async (string agentId, string sessionId, CompactRequest req) =>
            {
                var task = Application.Current.Dispatcher.Invoke(() =>
                {
                    var session = FindSession(agentId, sessionId);
                    if (session is null || session.IsArchived) // 归档预览只读:压缩会改写 context,拒绝
                        return null;
                    return RequireVm(agentId).CompactSession(session,
                        string.IsNullOrWhiteSpace(req.Extra) ? null : req.Extra.Trim());
                });
                if (task is null)
                    return Results.NotFound();
                var reason = await task; // 压缩占运行锁,完成(或中断/失败落对应分割线)后再返回
                return Results.Json(new { ok = true, reason }); // reason: "empty"=无可压缩前端toast;"cancelled"=中断;"failed"=失败;null=成功
            });

        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/undo", (string agentId, string sessionId) =>
        {
            var restored = Application.Current.Dispatcher.Invoke(() =>
            {
                var session = FindSession(agentId, sessionId);
                return session is null || session.IsArchived ? null : RequireVm(agentId).UndoSession(session); // 归档预览只读:撤销会截断 context,拒绝
            });
            return restored is null ? Results.Json(new { restored = "" }) : Results.Json(new { restored });
        });

        app.MapGet("/api/agents/{agentId}/sessions/{sessionId}/summary", (string agentId, string sessionId) =>
        {
            var summary = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                var session = FindSession(agentId, sessionId) ?? rt.ViewModel.RestoreArchivedPreview(sessionId); // 归档预览也能看摘要(只读)
                return session is null ? null : Plugins.ContextCompression.ContextCompressionPlugin.GetSummaryText(session.Session);
            });
            return summary is null ? Results.Json(new { text = "" }) : Results.Json(new { text = summary });
        });

        // 当前会话的系统提示词:Agent 配置 + 各启用插件片段实时组装(与下一轮请求发给 LLM 的完全一致);
        // 附带本轮实际提供给模型的启用工具清单(含工具原始描述,便于评估 Agent 当前能力)
        app.MapGet("/api/agents/{agentId}/sessions/{sessionId}/system-prompt", (string agentId, string sessionId) =>
        {
            var result = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                var session = FindSession(agentId, sessionId) ?? rt.ViewModel.RestoreArchivedPreview(sessionId); // 归档预览也能看系统提示词(只读)
                if (session is null) return null;
                var host = rt.Host;
                return new
                {
                    text = host.Agent.BuildSystemPrompt(session.Session),
                    tools = host.Agent.Tools.GetDefinitions()
                        .Select(d => new { name = d.Name, description = d.Description })
                        .ToList(),
                };
            });
            return result is null ? Results.NotFound() : Results.Json(result);
        });

        app.MapGet("/api/agents/{agentId}/archived", (string agentId) =>
        {
            var list = Application.Current.Dispatcher.Invoke(() =>
                RequireVm(agentId).ArchivedSessions()
                    .Select(s => new ArchivedSessionDto(s.Id, s.Title, s.UpdatedAt))
                    .ToList());
            return Results.Json(list);
        });

        app.MapPost("/api/agents/{agentId}/archived/clear", (string agentId) =>
        {
            Application.Current.Dispatcher.Invoke(() => RequireVm(agentId).ClearAllArchivedSessions());
            return Results.Json(new { ok = true });
        });

        // 压缩摘要模板预设(与 ContextCompressionPlugin 内置一致;前端内置只读展示、自定义可编辑)
        app.MapGet("/api/agents/{agentId}/compress-presets", () => Results.Json(new
        {
            customTag = APet.Plugins.ContextCompression.ContextCompressionPlugin.CustomPresetTag,
            defaultTemplate = APet.Plugins.ContextCompression.ContextCompressionPlugin.DefaultTemplate,
            presets = APet.Plugins.ContextCompression.ContextCompressionPlugin.Presets
                .Select(p => new { name = p.Name, text = p.Text }).ToList(),
        }));

        // 供应商清单(会话级模型切换的候选;模型清单来自配置,在线刷新走 VM 的 RefreshModelsAsync)
        app.MapGet("/api/agents/{agentId}/providers", (string agentId) =>
        {
            var providers = Application.Current.Dispatcher.Invoke(() =>
                _manager.RequireRuntime(agentId).Config.Llm.Providers
                    .Select(p => new ProviderDto(p.Name, p.Model, p.ReasoningEffort,
                        p.Models.Select(m => new ProviderModelDto(
                            m.Name, m.ContextTokens,
                            m.ThinkingLevels.Count > 0 ? m.ThinkingLevels : null,
                            m.MediaTypes.Count > 0 ? m.MediaTypes : null,
                            m.PriceInput, m.PriceInputCached, m.PriceOutput)).ToList()))
                    .ToList());
            return Results.Json(providers);
        });

        // 会话级模型切换(provider/model/thinking 任选其一或组合;语义与工具栏一致:换供应商回落其默认模型)
        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/model", (string agentId, string sessionId, ModelRequest req) =>
        {
            var snapshot = Application.Current.Dispatcher.Invoke(() =>
            {
                var vm = RequireVm(agentId);
                var session = FindSession(agentId, sessionId);
                if (session is null)
                    return null;
                vm.SelectedSession = session; // 会话级选择器只作用于选中会话:先选中再设值
                if (!string.IsNullOrWhiteSpace(req.Provider))
                    vm.SelectedProvider = req.Provider.Trim();
                if (!string.IsNullOrWhiteSpace(req.Model))
                    vm.SelectedModel = req.Model.Trim();
                if (!string.IsNullOrWhiteSpace(req.ThinkingEffort))
                    vm.ThinkingEffort = req.ThinkingEffort.Trim();
                var rt = _manager.RequireRuntime(agentId);
                return SessionSnapshot.Of(session, rt.ViewModel.ContextLimitOf(session),
                    rt.Config.ChatBackground, rt.Config.WindowBackground);
            });
            return snapshot is null ? Results.NotFound() : Results.Json(snapshot);
        });

        // 斜杠命令(/ 内置 + $ 插件):Name 自带前缀
        app.MapGet("/api/agents/{agentId}/commands", (string agentId) =>
        {
            var commands = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                var builtIn = SlashCommands.All.Select(c => new CommandDto(c.Name, c.Description, c.TakesArgument));
                var plugin = rt.Host.Agent.Plugins.EnabledPlugins
                    .SelectMany(r =>
                    {
                        try
                        {
                            return r.Plugin.GetChatCommands();
                        }
                        catch (Exception ex)
                        {
                            Log.Global.Error($"[Commands] {r.Name} GetChatCommands failed: {ex.Message}");
                            return (IReadOnlyList<APet.Framework.Plugin.PluginChatCommand>)[];
                        }
                    })
                    .Select(c => new CommandDto("$" + c.Name, c.Description, c.TakesArgument));
                return builtIn.Concat(plugin).ToList();
            });
            return Results.Json(commands);
        });

        // 斜杠命令执行:handled=false 时前端按普通消息发送(核心在 MainViewModel,与中途队列派发共用)
        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/command",
            async (string agentId, string sessionId, CommandRequest req) =>
            {
                var task = Application.Current.Dispatcher.Invoke(() =>
                {
                    var rt = _manager.RequireRuntime(agentId);
                    var session = FindSession(agentId, sessionId);
                    if (session is null || session.IsArchived) // 归档预览只读:斜杠命令($warmup 等)同样拒绝
                        return null;
                    return rt.ViewModel.ExecuteChatCommandAsync(session, req.Text ?? "");
                });
                if (task is null)
                    return Results.NotFound();
                var result = await task;
                return Results.Json(result);
            });

        // 会话图片(apetimg:// 与用户消息/工具结果图片的 HTTP 出口)
        app.MapGet("/api/agents/{agentId}/sessions/{sessionId}/images/{fileName}",
            (string agentId, string sessionId, string fileName) =>
            {
                var bytes = Services.SessionStore.Shared(agentId).ReadImage(sessionId, Path.GetFileName(fileName));
                return bytes is null ? Results.NotFound() : Results.File(bytes, ImageContentType(fileName));
            });

        // 会话工作目录/权限(首次对话后目录锁定,与聊天窗胶囊按钮同规则;权限不受锁定限制)
        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/workdir", (string agentId, string sessionId, WorkdirRequest req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke(() =>
            {
                var session = FindSession(agentId, sessionId);
                if (session is null || session.IsArchived) // 归档预览只读:工作目录/权限不可改
                    return false;
                var newDir = Path.TrimEndingDirectorySeparator((req.Directory ?? "").Trim());
                if (session.IsWorkDirLocked)
                {
                    // 锁定只冻结目录本身;权限随时可改(WPF SessionWorkingDirectoryAccess setter 无锁检查)
                    if (!string.Equals(newDir, session.WorkingDirectory ?? "", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(req.Access))
                        return false;
                    session.WorkingDirectoryAccess = req.Access.Trim();
                    return true;
                }
                session.WorkingDirectory = newDir;
                if (!string.IsNullOrWhiteSpace(req.Access))
                    session.WorkingDirectoryAccess = req.Access.Trim();
                return true;
            });
            return ok ? Results.Json(new { ok = true }) : Results.Conflict(new { error = "会话已产生对话,工作目录已锁定" });
        });

        // ───────── 侧边栏:任务清单 / 后台任务 / 直播事件(轻量轮询) ─────────

        app.MapGet("/api/agents/{agentId}/sessions/{sessionId}/sidebar", (string agentId, string sessionId) =>
        {
            var sidebar = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = _manager.RequireRuntime(agentId);
                var todos = rt.Host.Todo.StoreFor(sessionId).Snapshot()
                    .Select(t => new TodoDto(t.Id, t.Content, t.Status))
                    .ToList();
                var tasks = rt.Host.Background.ManagerFor(sessionId).Snapshot()
                    .Select(t => new TaskDto(t.Id, t.Title, t.Command, t.StatusText(),
                        t.Status == BackgroundTaskStatus.Failed, t.StartedAt, t.EndedAt))
                    .ToList();
                var liveBound = rt.Host.BiliLive.BoundSessionId == sessionId;
                var live = liveBound
                    ? rt.Host.BiliLive.RecentEventsSnapshot()
                        .Select(e => new LiveDto(e.Event.Kind.ToString(), e.Event.User, e.Event.Text, e.Event.PriceYuan, e.At.ToString("HH:mm:ss")))
                        .ToList()
                    : [];
                var pluginData = rt.Host.PluginDataSnapshots(sessionId);
                var uiPlugins = rt.Host.SidebarUiPlugins();
                var statusPlugins = rt.Host.StatusPluginEntries(sessionId);
                var bili = rt.Host.BiliLive.ConnectionStatus;
                var biliDto = new BiliConnDto(bili.StateKey, bili.Host, bili.HostIndex, bili.HostCount,
                    bili.Round, rt.Host.BiliLive.RoomCode, bili.InternalRoomId, bili.RoomLive,
                    bili.Error, bili.ChangedAt, bili.NextRetryAt, rt.Host.BiliLive.ReceivedCount);
                return new SidebarDto(todos, tasks, liveBound, live, pluginData, uiPlugins, statusPlugins, biliDto);
            });
            return Results.Json(sidebar);
        });

        // 插件数据面:GET 单插件数据(内置/外部插件均可实现;sessionId 可选)
        app.MapGet("/api/agents/{agentId}/plugins/{name}/data", async (string agentId, string name, string? sessionId) =>
        {
            // 插件解析(UI 线程,快)与数据提供器调用(后台线程,可能很慢)分离:
            // NvidiaMonitor 每次 nvidia-smi、GPU 繁忙时数秒,曾同步跑在 Dispatcher.Invoke 里堵死 UI 线程
            // (表现为该端点与 sidebar/sessions 等全部 pending、整个应用冻结)
            AgentHost rt;
            AgentPluginBase plugin;
            try
            {
                (rt, plugin) = Application.Current.Dispatcher.Invoke(() =>
                {
                    var runtime = _manager.RequireRuntime(agentId);
                    return (runtime.Host, runtime.Host.ResolveSinglePlugin(name));
                });
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound();
            }
            var data = await Task.Run(() => rt.InvokePluginData(plugin, sessionId));
            return data is null ? Results.NotFound() : Results.Json(new { name, data });
        });

        // 插件数据面:POST 单插件自定义动作(body 为插件约定的任意 JSON)
        app.MapPost("/api/agents/{agentId}/plugins/{name}/data", async (string agentId, string name, string? sessionId, HttpRequest request) =>
        {
            var body = await new StreamReader(request.Body).ReadToEndAsync();
            AgentHost rt;
            AgentPluginBase plugin;
            try
            {
                (rt, plugin) = Application.Current.Dispatcher.Invoke(() =>
                {
                    var runtime = _manager.RequireRuntime(agentId);
                    return (runtime.Host, runtime.Host.ResolveSinglePlugin(name));
                });
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound();
            }
            var result = await Task.Run(() => rt.InvokePluginAction(plugin, sessionId, body.Length > 0 ? body : null));
            return result is null ? Results.NotFound() : Results.Json(new { name, result });
        });

        // 插件 UI 资产:DLL 内嵌 ui.* 资源(自包含);空路径回落 index.html;
        // 插件页引用同源静态 /apet-bridge.js?plugin=… 获得桥(数据面/事件/宿主 DOM)
        app.MapGet("/api/agents/{agentId}/external-plugins/{pluginName}/ui/{**path}",
            (string agentId, string pluginName, string? path) =>
        {
            if (path is not null && (path.Contains("..") || path.Contains('\\')))
                return Results.NotFound();
            var bytes = Application.Current.Dispatcher.Invoke(() =>
            {
                var host = _manager.RequireRuntime(agentId).Host;
                var plugin = host.FindExternalPlugin(pluginName);
                return plugin is null ? null : PluginUi.TryRead(plugin, path);
            });
            if (bytes is null)
                return Results.NotFound();
            return Results.File(bytes, UiContentType(Path.GetExtension(string.IsNullOrEmpty(path) ? "html" : path!)));
        });

        // 后台任务完整输出(列表只给状态,展开时拉全文)
        app.MapGet("/api/agents/{agentId}/sessions/{sessionId}/tasks/{taskId}/output",
            (string agentId, string sessionId, int taskId) =>
            {
                var output = Application.Current.Dispatcher.Invoke(() =>
                {
                    var task = _manager.RequireRuntime(agentId).Host.Background
                        .ManagerFor(sessionId).Snapshot().FirstOrDefault(t => t.Id == taskId);
                    return task?.FullOutput();
                });
                return output is null ? Results.NotFound() : Results.Json(new { output });
            });

        // 侧边栏「后台任务」停止:终止运行中的任务(杀整棵进程树);已结束/不存在 → 404
        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/tasks/{taskId}/kill",
            (string agentId, string sessionId, int taskId) =>
            {
                var ok = Application.Current.Dispatcher.Invoke(() =>
                    _manager.RequireRuntime(agentId).Host.Background.ManagerFor(sessionId).Kill(taskId));
                return ok ? Results.Json(new { ok = true }) : Results.NotFound();
            });

        // 侧边栏「后台任务」清除:移除已结束的任务(运行中需先停止;不存在/运行中 → 404)
        app.MapPost("/api/agents/{agentId}/sessions/{sessionId}/tasks/{taskId}/delete",
            (string agentId, string sessionId, int taskId) =>
            {
                var ok = Application.Current.Dispatcher.Invoke(() =>
                    _manager.RequireRuntime(agentId).Host.Background.ManagerFor(sessionId).Remove(taskId));
                return ok ? Results.Json(new { ok = true }) : Results.NotFound();
            });
    }

    private static string ImageContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream",
        };

    /// <summary>插件 UI 资产伺服的 Content-Type(按扩展名;未知回落 octet-stream)。</summary>
    private static string UiContentType(string ext) => ext.ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private MainViewModel RequireVm(string agentId) => _manager.RequireRuntime(agentId).ViewModel;

    private SessionViewModel? FindSession(string agentId, string sessionId) =>
        _manager.RequireRuntime(agentId).ViewModel.Sessions.FirstOrDefault(s => s.SessionId == sessionId);

    private enum SendOutcome
    {
        Started,
        Queued,
        NotFound,
        BadAttachment,
        TooLarge,
        Archived,
    }

    private sealed record SendResult(SendOutcome Outcome, string? Error = null)
    {
        public static SendResult Started => new(SendOutcome.Started);
        public static SendResult Queued => new(SendOutcome.Queued);
        public static SendResult NotFound => new(SendOutcome.NotFound);
        public static SendResult BadAttachment => new(SendOutcome.BadAttachment);
        public static SendResult TooLarge(string message) => new(SendOutcome.TooLarge, message);
        public static SendResult Archived => new(SendOutcome.Archived);
    }

    private static byte[] DecodeDataUrlBytes(string dataUrl)
    {
        var sep = dataUrl.IndexOf(',');
        if (sep < 0)
            throw new FormatException();
        return Convert.FromBase64String(dataUrl[(sep + 1)..]);
    }

    private static string ExtFromMime(string dataUrl)
    {
        if (!dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return ".png";
        var mime = dataUrl[5..].Split(';')[0].Split(',')[0].ToLowerInvariant();
        return mime switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "application/pdf" => ".pdf",
            "audio/mpeg" => ".mp3",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/ogg" => ".ogg",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            _ => ".png",
        };
    }

    // ───────── Agent 头像(agents/{agentId}/avatar.{ext};rev = 文件修改时间,供浏览器缓存失效) ─────────

    private static readonly string[] AvatarExtensions = [".png", ".jpeg", ".jpg", ".webp", ".gif"];

    private static string? FindAvatarFile(string agentId)
    {
        var dir = Services.SessionStore.AgentDir(agentId);
        return AvatarExtensions
            .Select(ext => Path.Combine(dir, "avatar" + ext))
            .FirstOrDefault(File.Exists);
    }

    private static long AvatarRev(string agentId)
    {
        var file = FindAvatarFile(agentId);
        return file is null ? 0 : new FileInfo(file).LastWriteTimeUtc.Ticks;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F1} KB"
        : $"{bytes} B";

    private sealed record AgentInfoDto(string Id, string Name, string Title, bool Enabled, long AvatarRev);

    private sealed record AvatarRequest(string? DataUrl);

    private sealed record AgentCreateRequest(string? Name);

    private sealed record AgentEnabledRequest(bool Enabled);

    private sealed record AgentRenameRequest(string? Name);

    private sealed record PinRequest(bool Pinned);

    private sealed record LastSessionRequest(string? SessionId);

    private sealed record SendMessageRequest(string? Text, List<string>? Images, List<WebTextFile>? TextFiles, bool Defer);

    /// <summary>队列条目"本轮后发送"标记切换请求。</summary>
    private sealed record QueueDeferRequest(bool Defer);

    /// <summary>队列条目拖拽排序请求:目标插入位置(移除自身后)。</summary>
    private sealed record QueueMoveRequest(int Index);

    /// <summary>新建文件夹请求:parentId 为 null = 顶级。</summary>
    private sealed record FolderCreateRequest(string? Name, string? ParentId);

    private sealed record FolderRenameRequest(string? Name);

    /// <summary>文件夹移动请求:parentId 为 null = 移到顶级(根)。</summary>
    private sealed record FolderMoveRequest(string? ParentId);

    /// <summary>会话移组请求:folderId 为 null = 根组。</summary>
    private sealed record SessionMoveRequest(string? FolderId);

    /// <summary>文件夹预设色请求:color = 色板 id(空串 = 默认不着色)。</summary>
    private sealed record FolderColorRequest(string? Color);

    /// <summary>组整序重排请求(拖拽落点后整序提交,orderedIds 可混排会话与文件夹 id):
    /// folderId 为 null = 根组。</summary>
    private sealed record SessionsReorderRequest(string? FolderId, List<string>? OrderedIds);

    /// <summary>文件夹整组重排请求(同级,仅文件夹 id):parentId 为 null = 顶级。</summary>
    private sealed record FoldersReorderRequest(string? ParentId, List<string>? OrderedIds);

    /// <summary>文件夹折叠状态整表上报:ids = 当前收起的全部文件夹 id(空表 = 全展开)。</summary>
    private sealed record CollapseStateRequest(List<string>? Ids);

    private sealed record WebTextFile(string Name, string Content);

    private sealed record AuthRequest(bool Allow, bool Trust);

    private sealed record AskResolveRequest(JsonElement? Answers, bool Skipped);

    private sealed record RenameRequest(string? Title);

    private sealed record ForkRequest(string? Title);

    private sealed record CompactRequest(string? Extra);

    private sealed record ModelRequest(string? Provider, string? Model, string? ThinkingEffort);

    /// <summary>新建会话请求:fromSessionId = 分叉源,folderId = 目标文件夹(null/缺省 = 根组)。</summary>
    private sealed record SessionCreateRequest(string? FromSessionId, string? FolderId = null);

    private sealed record CommandRequest(string? Text);

    private sealed record WorkdirRequest(string? Directory, string? Access);

    /// <summary>应用退出时停机(阻塞至多 3s;退出竞态的异常直接吞掉)。</summary>
    public void Stop()
    {
        if (_app is null)
            return;
        try
        {
            _app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).GetAwaiter().GetResult();
        }
        catch
        {
            // 进程退出路径:尽力而为
        }
        _app = null;
    }
}
