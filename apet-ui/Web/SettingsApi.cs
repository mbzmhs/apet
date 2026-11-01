using System.Collections;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using APet.Framework.Plugin;
using APet.UI.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace APet.UI.Web;

/// <summary>
/// 设置 API:整份读写 AgentConfig(反射值拷贝到现有实例,保持对象身份与 WPF 设置页同一条热更路径)、
/// 工具清单与启停、服务端目录浏览(替代 WPF 文件/文件夹选择器)。
/// 传输用 camelCase;落盘仍由 AgentConfig.Save() 走持久化格式,大小写不敏感反序列化保证互通。
/// </summary>
public static class SettingsApi
{
    /// <summary>文件选择器上次浏览的目录(服务端记忆;跨会话/跨 Agent 共享,进程内有效)。</summary>
    private static string _lastBrowseDir = "";

    /// <summary>捏表情编辑态:进入前的内建自动化状态(眨眼/呼吸/姿态),按 Agent 记忆,退出时还原。
    /// 仅在 UI 线程读写;断线未退出时下次 enter 直接覆盖,重进编辑器即可恢复。</summary>
    private static readonly Dictionary<string, (bool Eye, bool Breath, bool Pose)> _editorAutomation = new();

    /// <summary>把全部自定义表情持久化到模型同目录 sidecar(&lt;模型名&gt;.customexp.json;WPF PersistCustomExpressions 同路径)。</summary>
    private static void PersistExpressions(APet.Plugins.DesktopPet.DesktopPetPlugin? pet, APet.Renderer.Live2D.Live2DStage stage)
    {
        var modelPath = pet?.ModelPath ?? "";
        if (modelPath.Length == 0)
            return;
        stage.SaveCustomExpressions(APet.Renderer.Live2D.Live2DStage.CustomExpressionSidecarPath(modelPath));
    }

    public static void Map(WebApplication app, AgentManager manager)
    {
        app.MapGet("/api/agents/{agentId}/settings", (string agentId) =>
        {
            var json = Application.Current.Dispatcher.Invoke(() =>
            {
                var config = manager.RequireRuntime(agentId).Config;
                config.EnsureInitialized(); // 旧配置子对象可能为 null(如 DesktopPet.Live2D)
                return JsonSerializer.Serialize(config, Json.Web);
            });
            return Results.Text(json, "application/json; charset=utf-8");
        });

        // 整份保存:前端拿到什么就交回什么(增量编辑在 GUI 里做);Id 不可改
        app.MapPut("/api/agents/{agentId}/settings", (string agentId, JsonElement body) =>
        {
            var applied = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = manager.RequireRuntime(agentId);
                AgentConfig? fresh;
                try
                {
                    fresh = JsonSerializer.Deserialize<AgentConfig>(body.GetRawText(), Json.Web);
                }
                catch (JsonException)
                {
                    return (ok: false, petMessage: null);
                }
                if (fresh is null)
                    return (ok: false, petMessage: null);
                CopyObject(fresh, rt.Config, skipId: true);
                rt.Config.EnsureInitialized(); // 拷贝后再兜底一次(防历史 null 残留)

                // 桌宠启停:与 WPF OnPetEnabledChanged 同语义——SetPetEnabled 驱动悬浮窗创建/销毁;
                // 有会话正在运行时拒绝并回滚开关(并发轮次会争夺角色气泡/TTS)
                string? petMessage = null;
                var wantPet = rt.Config.DesktopPet.Enabled;
                if (wantPet != rt.Host.IsPetEnabled)
                {
                    if (rt.Host.SetPetEnabled(wantPet))
                    {
                        petMessage = wantPet ? "桌宠已启用" : "桌宠已停用";
                    }
                    else
                    {
                        rt.Config.DesktopPet.Enabled = !wantPet;
                        petMessage = "⏳ 有会话正在运行,暂不能启停桌宠;请等会话空闲后再试";
                    }
                }

                rt.Host.ApplyAgent(rt.Config); // 与 WPF 设置页 SaveAgent 相同:热更运行参数 + 落盘
                // 桌宠/气泡设置实时生效(引擎/模型/缩放/情绪映射/点击行为/气泡样式——WPF SavePet 同路径)
                rt.Host.ApplyPetSettings(rt.Config.DesktopPet);
                rt.Host.ApplyChatBubbleSettings(rt.Config.ChatBubble);
                // MCP 服务器清单/开关按差异重连;技能/MCP/记忆/直播/情景总开关热推
                rt.Host.ApplyMcpSettings(rt.Config.Mcp);
                rt.Host.ApplySkillsSettings(rt.Config.Skills);
                rt.Host.ApplyMemorySettings(rt.Config.Memory);
                rt.Host.ApplyBiliSettings(rt.Config.BiliLive);
                rt.Host.ApplySceneSettings(rt.Config.SceneEnabled);
                rt.Host.ApplySubAgentSettings(rt.Config.SubAgents);
                rt.Host.ApplyTtsSettings(rt.Config); // TTS/语言设置热推(与 WPF 设置页同路径)
                rt.Config.Save();
                return (ok: true, petMessage);
            });
            return applied.ok
                ? Results.Json(new { ok = true, petMessage = applied.petMessage })
                : Results.BadRequest(new { error = "设置解析失败" });
        });

        // 工具清单(名称/显示名/启用状态);归属插件的工具随插件启停隐藏(重开插件即恢复)
        app.MapGet("/api/agents/{agentId}/tools", (string agentId) =>
        {
            var tools = Application.Current.Dispatcher.Invoke(() =>
            {
                var host = manager.RequireRuntime(agentId).Host;
                return host.Agent.Tools.GetAllTools()
                    .Where(t => host.IsToolVisibleInSettings(t.Definition.Name))
                    .Select(t => new ToolDto(t.Definition.Name, t.Definition.GetDisplayName(), t.Enabled))
                    .ToList();
            });
            return Results.Json(tools);
        });

        // 工具功能组(设置页按组展示与开关,分组来自 ToolCatalog)
        app.MapGet("/api/agents/{agentId}/tool-groups", (string agentId) =>
        {
            var groups = Application.Current.Dispatcher.Invoke(() =>
            {
                var host = manager.RequireRuntime(agentId).Host;
                var registered = host.Agent.Tools.GetAllTools()
                    .Where(t => host.IsToolVisibleInSettings(t.Definition.Name))
                    .ToList();
                var result = new List<ToolGroupDto>();
                foreach (var info in ToolCatalog.Groups)
                {
                    var members = registered.Where(t => info.Tools.Contains(t.Definition.Name)).ToList();
                    if (members.Count > 0)
                        result.Add(new ToolGroupDto(info.Name, info.Description,
                            members.Select(t => new ToolDto(t.Definition.Name, t.Definition.GetDisplayName(), t.Enabled)).ToList()));
                }
                // MCP 工具在 MCP 插件页按服务器/工具管控,不在此重复展示
                var others = registered.Where(t => ToolCatalog.ShowOnToolPage(t.Definition.Name)
                    && ToolCatalog.GroupNameOf(t.Definition.Name) == ToolCatalog.OtherGroupName).ToList();
                if (others.Count > 0)
                    result.Add(new ToolGroupDto(ToolCatalog.OtherGroupName, "未归入上述分组的工具。",
                        others.Select(t => new ToolDto(t.Definition.Name, t.Definition.GetDisplayName(), t.Enabled)).ToList()));
                return result;
            });
            return Results.Json(groups);
        });

        // 工具页折叠「其他」卡片:在插件页单独管控而被主组清单隐藏的工具(MCP 工具/B站直播等),逐工具开关
        app.MapGet("/api/agents/{agentId}/tools/hidden", (string agentId) =>
        {
            var tools = Application.Current.Dispatcher.Invoke(() =>
            {
                var host = manager.RequireRuntime(agentId).Host;
                return host.Agent.Tools.GetAllTools()
                    .Where(t => host.IsToolVisibleInSettings(t.Definition.Name)
                        && !ToolCatalog.ShowOnToolPage(t.Definition.Name))
                    .Select(t => new ToolDto(t.Definition.Name, t.Definition.GetDisplayName(), t.Enabled))
                    .ToList();
            });
            return Results.Json(tools);
        });

        // 工具组批量启停(组开关 = 逐工具写入并落盘,与 WPF 同语义)
        app.MapPost("/api/agents/{agentId}/tool-groups/{group}/enabled", (string group, string agentId, ToggleRequest req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = manager.RequireRuntime(agentId);
                var info = ToolCatalog.Groups.FirstOrDefault(g => g.Name == group)
                    ?? (group == ToolCatalog.OtherGroupName
                        ? new ToolGroupInfo(group, "", rt.Host.Agent.Tools.GetAllTools()
                            .Where(t => ToolCatalog.ShowOnToolPage(t.Definition.Name)
                                && ToolCatalog.GroupNameOf(t.Definition.Name) == ToolCatalog.OtherGroupName)
                            .Select(t => t.Definition.Name).ToList())
                        : null);
                if (info is null || info.Tools.Count == 0)
                    return false;
                foreach (var toolName in info.Tools)
                {
                    rt.Host.SetToolEnabled(toolName, req.Enabled);
                    rt.Host.PersistToolEnabled(toolName, req.Enabled);
                }
                rt.Config.Save();
                return true;
            });
            return ok ? Results.Json(new { ok = true }) : Results.NotFound();
        });

        // 在线拉取供应商的模型列表(GET {baseUrl}/models,Bearer 鉴权;服务端代理避免浏览器 CORS)
        app.MapPost("/api/agents/{agentId}/providers/models-fetch", async (string agentId, FetchModelsRequest req) =>
        {
            _ = agentId;
            var baseUrl = (req.BaseUrl ?? "").Trim();
            if (baseUrl.Length == 0)
                return Results.BadRequest(new { error = "请先填写服务地址" });
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
                if (!string.IsNullOrWhiteSpace(req.ApiKey))
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", req.ApiKey.Trim());
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var response = await http.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    return Results.BadRequest(new { error = $"HTTP {(int)response.StatusCode}:{json[..Math.Min(json.Length, 200)]}" });
                using var doc = JsonDocument.Parse(json);
                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var entry in arr.EnumerateArray())
                        if (entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            models.Add(id.GetString()!);
                return models.Count == 0
                    ? Results.BadRequest(new { error = "服务端未返回模型列表" })
                    : Results.Json(models);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 单工具启停(与 WPF 设置页 SetToolEnabled 同路径:运行态 + 落盘)
        app.MapPost("/api/agents/{agentId}/tools/{name}/enabled", (string agentId, string name, ToggleRequest req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = manager.RequireRuntime(agentId);
                var tool = rt.Host.Agent.Tools.GetAllTools().FirstOrDefault(t => t.Definition.Name == name);
                if (tool is null)
                    return false;
                rt.Host.SetToolEnabled(name, req.Enabled);
                rt.Host.PersistToolEnabled(name, req.Enabled);
                rt.Config.Save();
                return true;
            });
            return ok ? Results.Json(new { ok = true }) : Results.NotFound();
        });

        // MCP 服务器连接状态:配置清单合并运行时连接状态(设置页状态徽标)
        app.MapGet("/api/agents/{agentId}/mcp-status", (string agentId) =>
        {
            var data = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = manager.RequireRuntime(agentId);
                var cfg = rt.Config.Mcp;
                var live = new Dictionary<string, (bool Alive, int Tools, string? Error)>(StringComparer.OrdinalIgnoreCase);
                var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (rt.Host.Mcp is { } mcp)
                {
                    foreach (var (server, alive, tools, error) in mcp.Status)
                        live[server] = (alive, tools, error);
                    foreach (var (server, error) in mcp.ConnectErrors)
                        errors[server] = error;
                }
                var servers = (cfg?.Servers ?? new List<McpServerConfig>()).Select(s =>
                {
                    var name = string.IsNullOrWhiteSpace(s.Name)
                        ? (string.IsNullOrWhiteSpace(s.Command) ? s.Url.Trim() : s.Command.Trim())
                        : s.Name.Trim();
                    var enabled = cfg.Enabled && s.Enabled;
                    live.TryGetValue(name, out var st);
                    errors.TryGetValue(name, out var error);
                    return new
                    {
                        name,
                        type = s.Type,
                        enabled,
                        alive = enabled && st.Alive,
                        tools = st.Tools,
                        error = enabled && !st.Alive ? (error ?? st.Error) : null,
                    };
                }).ToList();
                return new { pluginEnabled = cfg.Enabled, servers };
            });
            return Results.Json(data);
        });

        // 系统字体清单(与 WPF EnsureBubbleFontItems 同源:System.Drawing 已安装字体)
        app.MapGet("/api/system/fonts", () =>
            Results.Json(System.Drawing.FontFamily.Families
                .Select(f => f.Name)
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .ToList()));

        // TTS 服务角色拉取(GET {serverUrl}/v1/models;参考 WPF OnFetchTtsModels / fast-tts-server)
        app.MapPost("/api/agents/{agentId}/tts/models-fetch", async (string agentId, TtsModelsRequest req) =>
        {
            _ = agentId;
            var serverUrl = (req.ServerUrl ?? "").Trim();
            if (serverUrl.Length == 0)
                return Results.BadRequest(new { error = "请先填写服务地址。" });
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var response = await http.GetAsync(serverUrl.TrimEnd('/') + "/v1/models");
                var json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    return Results.BadRequest(new { error = $"HTTP {(int)response.StatusCode}:{json[..Math.Min(json.Length, 200)]}" });
                using var doc = JsonDocument.Parse(json);
                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var entry in arr.EnumerateArray())
                        if (entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            models.Add(id.GetString()!);
                return models.Count == 0
                    ? Results.BadRequest(new { error = "服务端未返回角色列表。" })
                    : Results.Json(models);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // GSV 模型目录扫描:模型包 voices/ 与 gpt/ 子目录名(声线编辑下拉用;WPF FillGsvDirCombo 同逻辑)
        app.MapGet("/api/agents/{agentId}/gsv-model-dirs", (string agentId, string? dir) =>
        {
            _ = agentId;
            static string[] Scan(string root) => Directory.Exists(root)
                ? Directory.EnumerateDirectories(root)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray()!
                : Array.Empty<string>();
            var modelDir = (dir ?? "").Trim();
            return Results.Json(new
            {
                sovits = Scan(Path.Combine(modelDir, "voices")),
                gpt = Scan(Path.Combine(modelDir, "gpt")),
            });
        });

        // B站直播工具白名单候选:目录组顺序 + 未收录工具 + 动态工具(如 bili_live_events),
        // 标注全局停用状态(与 WPF LoadBiliWhitelist 同逻辑)
        app.MapGet("/api/agents/{agentId}/bili-whitelist-candidates", (string agentId) =>
        {
            var candidates = Application.Current.Dispatcher.Invoke(() =>
            {
                var host = manager.RequireRuntime(agentId).Host;
                var registered = host.Agent.Tools.GetAllTools().ToDictionary(
                    t => t.Definition.Name, t => t);
                var selected = new HashSet<string>(host.Config.BiliLive.ToolWhitelist, StringComparer.Ordinal);
                var ordered = ToolCatalog.Groups.SelectMany(g => g.Tools)
                    .Concat(registered.Keys.Where(n => ToolCatalog.GroupNameOf(n) == ToolCatalog.OtherGroupName))
                    .Distinct();
                return ordered.Select(name =>
                {
                    var label = registered.TryGetValue(name, out var tool)
                        ? tool.Definition.GetDisplayName()
                        : ToolCatalog.DynamicToolDisplayNames.GetValueOrDefault(name, name);
                    var disabled = registered.TryGetValue(name, out var t) && !t.Enabled;
                    return new BiliWhitelistCandidateDto(
                        name,
                        $"{ToolCatalog.GroupNameOf(name)} · {label}({name})" + (disabled ? " — 已在工具页停用" : ""),
                        selected.Contains(name),
                        disabled);
                }).ToList();
            });
            return Results.Json(candidates);
        });

        // 气泡预览:在宠物旁边弹出真实预览气泡(WPF ShowOffsetPreview 同路径;仅预览,不落盘)
        app.MapPost("/api/agents/{agentId}/bubble/preview", (string agentId, BubblePreviewRequest req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke(() =>
            {
                var bubble = manager.RequireRuntime(agentId).Host.ChatBubble;
                if (bubble is null)
                    return false;
                var style = APet.Plugins.ChatBubble.BubbleStyle.From(
                    req.FontFamily, req.FontSize ?? 10, req.FontStyle,
                    req.TextColor, req.FillColor, req.BorderColor,
                    req.BorderWidth ?? 2, req.CornerRadius ?? 14, req.TailLength ?? 12,
                    req.MaxWidth ?? 320, req.OpacityPercent ?? 100);
                bubble.ShowOffsetPreview(req.TopOffset ?? 0, req.LeftOffset ?? 0, req.RightOffset ?? 0, style);
                return true;
            });
            return ok ? Results.Json(new { ok = true }) : Results.BadRequest(new { error = "桌宠气泡未启用" });
        });

        // 桌宠动作列表(来自运行中模型;未启用/未加载为空)
        app.MapGet("/api/agents/{agentId}/pet/motions", (string agentId) =>
        {
            var motions = Application.Current.Dispatcher.Invoke(() =>
                manager.RequireRuntime(agentId).Host.DesktopPet?.GetMotionInfos()
                    .Select(m => new PetMotionDto(m.Name, m.Detail)).ToList() ?? []);
            return Results.Json(motions);
        });

        // 测试播放一个动作(播完自动回待机)
        app.MapPost("/api/agents/{agentId}/pet/motions/play", (string agentId, PlayMotionRequest req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke(() =>
                manager.RequireRuntime(agentId).Host.DesktopPet?.PlayMotion(req.Name ?? "") ?? false);
            return ok ? Results.Json(new { ok = true }) : Results.BadRequest(new { error = "桌宠未启用或模型未加载" });
        });

        // ───────── Live2D 形态/捏表情编辑器(桌面端两个原生窗口的 Web 化) ─────────
        // 每次调用即时取 Stage;编辑器隔离态(暂停自动化)的还原信息按 Agent 记在内存。

        static APet.Renderer.Live2D.Live2DStage? Stage(AgentManager mgr, string id) =>
            mgr.RequireRuntime(id).Host.DesktopPet?.Live2DStage;

        // 形态编辑:顶层部件列表(当前不透明度 + setup 初始值 + 已存兜底形态)
        app.MapGet("/api/agents/{agentId}/pet/live2d/parts", (string agentId) =>
        {
            var parts = Application.Current.Dispatcher.Invoke(() =>
            {
                var stage = Stage(manager, agentId);
                if (stage is null || !stage.IsLoaded)
                    return null;
                var saved = manager.RequireRuntime(agentId).Config.DesktopPet.Live2D.PartOpacities;
                return new
                {
                    saved,
                    parts = stage.Model.Parts.Where(p => p.ParentIndex < 0)
                        .Select(p => new Live2dPartDto(p.Id, p.Opacity, stage.GetSetupPartOpacity(p.Id)))
                        .ToList(),
                };
            });
            return parts is null ? Results.BadRequest(new { error = "桌宠未启用或 Live2D 模型未加载" }) : Results.Json(parts);
        });

        app.MapPost("/api/agents/{agentId}/pet/live2d/parts/opacity", (string agentId, Live2dPartValueRequest req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke<bool>(() =>
            {
                var stage = Stage(manager, agentId);
                if (stage is null || !stage.IsLoaded) return false;
                stage.SetPartOpacity(req.Id ?? "", (float)req.Value);
                return true;
            });
            return ok ? Results.Json(new { ok = true }) : Results.BadRequest(new { error = "桌宠未启用或 Live2D 模型未加载" });
        });

        // 存为兜底形态:写入该 Agent 的 Live2D 配置并推给渲染器(立即生效;WPF OnSaveFallback 同路径)
        app.MapPost("/api/agents/{agentId}/pet/live2d/fallback/save", (string agentId, Live2dFallbackRequest req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke<bool>(() =>
            {
                var rt = manager.RequireRuntime(agentId);
                var pet = rt.Host.DesktopPet;
                var stage = pet?.Live2DStage;
                if (pet is null || stage is null || !stage.IsLoaded) return false;
                rt.Config.DesktopPet.Live2D.PartOpacities = req.Opacities ?? new Dictionary<string, float>();
                pet.SetSetting("partOpacities", System.Text.Json.JsonSerializer.Serialize(rt.Config.DesktopPet.Live2D.PartOpacities));
                rt.Config.Save();
                return true;
            });
            return ok ? Results.Json(new { ok = true }) : Results.BadRequest(new { error = "桌宠未启用或 Live2D 模型未加载" });
        });

        // 清空兜底记录并让部件回到 setup 初始可见度
        app.MapPost("/api/agents/{agentId}/pet/live2d/fallback/clear", (string agentId) =>
        {
            var ok = Application.Current.Dispatcher.Invoke<bool>(() =>
            {
                var rt = manager.RequireRuntime(agentId);
                var pet = rt.Host.DesktopPet;
                var stage = pet?.Live2DStage;
                if (pet is null || stage is null || !stage.IsLoaded) return false;
                rt.Config.DesktopPet.Live2D.PartOpacities = new Dictionary<string, float>();
                pet.SetSetting("partOpacities", "{}");
                rt.Config.Save();
                stage.ResetPartsToSetup();
                return true;
            });
            return ok ? Results.Json(new { ok = true }) : Results.BadRequest(new { error = "桌宠未启用或 Live2D 模型未加载" });
        });

        // 进入捏表情编辑态:暂停自动化(停动作/关眨眼呼吸姿态/复位参数),返回参数(按 cdi 分组)与部件全表
        app.MapPost("/api/agents/{agentId}/pet/live2d/editor/enter", (string agentId) =>
        {
            var result = Application.Current.Dispatcher.Invoke<object?>(() =>
            {
                var rt = manager.RequireRuntime(agentId);
                var pet = rt.Host.DesktopPet;
                var stage = pet?.Live2DStage;
                if (stage is null || !stage.IsLoaded) return null;
                pet.SuspendAutomation();
                _editorAutomation[agentId] = (stage.AutoEyeBlink, stage.BreathEnabled, stage.PoseEnabled);
                stage.AutoEyeBlink = false;
                stage.BreathEnabled = false;
                stage.PoseEnabled = false;
                stage.StopMotions();
                stage.ClearExpressionImmediate();
                stage.ResetAllParameters();

                // 参数按 cdi 分组(保持首现顺序),无分组信息归入"参数"
                var groups = new List<(string Id, List<Live2dParamDto> Rows)>();
                foreach (var p in stage.Model.Parameters)
                {
                    var gid = stage.Cdi?.Parameters.FirstOrDefault(x => x.Id == p.Id)?.GroupId ?? "";
                    var row = new Live2dParamDto(p.Id, stage.GetParameterDisplayName(p.Id), gid, p.Minimum, p.Maximum, p.Default, p.Value);
                    var g = groups.FirstOrDefault(x => x.Id == gid);
                    if (g.Rows is null) groups.Add((gid, new List<Live2dParamDto> { row }));
                    else g.Rows.Add(row);
                }
                var parts = stage.Model.Parts.Where(p => p.ParentIndex < 0)
                    .Select(p => new Live2dPartDto(p.Id, p.Opacity, stage.GetSetupPartOpacity(p.Id)))
                    .ToList();
                var custom = stage.GetCustomExpressionNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                var exprs = stage.GetExpressionNames()
                    .Select(n => new Live2dExprDto(n, custom.Contains(n)))
                    .ToList();
                return new
                {
                    parameters = groups.Select(g => new { id = g.Id, name = g.Id == "" ? "参数" : stage.Cdi?.GetGroupName(g.Id) ?? g.Id, rows = g.Rows }),
                    parts,
                    expressions = exprs,
                };
            });
            return result is null ? Results.BadRequest(new { error = "桌宠未启用或 Live2D 模型未加载" }) : Results.Json(result);
        });

        // 退出编辑态:恢复进入前的自动化状态(断线未退出时下次 enter 覆盖,重进即可)
        app.MapPost("/api/agents/{agentId}/pet/live2d/editor/exit", (string agentId) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var pet = manager.RequireRuntime(agentId).Host.DesktopPet;
                var stage = pet?.Live2DStage;
                if (stage is not null && _editorAutomation.Remove(agentId, out var prev))
                {
                    stage.SetExpression(null);
                    stage.AutoEyeBlink = prev.Eye;
                    stage.BreathEnabled = prev.Breath;
                    stage.PoseEnabled = prev.Pose;
                }
                pet?.ResumeAutomation();
            });
            return Results.Json(new { ok = true });
        });

        // 滑杆值实时写模型(参数/部件)
        app.MapPost("/api/agents/{agentId}/pet/live2d/editor/param", (string agentId, Live2dParamValueRequest req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke<bool>(() =>
            {
                var stage = Stage(manager, agentId);
                if (stage is null || !stage.IsLoaded) return false;
                stage.SetParameterValue(req.Id ?? "", (float)req.Value);
                return true;
            });
            return ok ? Results.Json(new { ok = true }) : Results.BadRequest(new { error = "Live2D 模型未加载" });
        });

        app.MapPost("/api/agents/{agentId}/pet/live2d/editor/part", (string agentId, Live2dPartValueRequest req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke<bool>(() =>
            {
                var stage = Stage(manager, agentId);
                if (stage is null || !stage.IsLoaded) return false;
                stage.SetPartOpacity(req.Id ?? "", (float)req.Value);
                return true;
            });
            return ok ? Results.Json(new { ok = true }) : Results.BadRequest(new { error = "Live2D 模型未加载" });
        });

        // 编辑态实时值轮询(内置表情预览经淡入逐帧应用,滑杆随当前值走)
        app.MapGet("/api/agents/{agentId}/pet/live2d/editor/values", (string agentId) =>
        {
            var values = Application.Current.Dispatcher.Invoke<object?>(() =>
            {
                var stage = Stage(manager, agentId);
                if (stage is null || !stage.IsLoaded) return null;
                return new
                {
                    parameters = stage.Model.Parameters.Select(p => new { id = p.Id, value = p.Value }),
                    parts = stage.Model.Parts.Where(p => p.ParentIndex < 0).Select(p => new { id = p.Id, value = p.Opacity }),
                };
            });
            return values is null ? Results.BadRequest(new { error = "Live2D 模型未加载" }) : Results.Json(values);
        });

        // 编辑态全部参数复位
        app.MapPost("/api/agents/{agentId}/pet/live2d/editor/reset", (string agentId) =>
        {
            var ok = Application.Current.Dispatcher.Invoke<bool>(() =>
            {
                var stage = Stage(manager, agentId);
                if (stage is null || !stage.IsLoaded) return false;
                stage.ResetAllParameters();
                return true;
            });
            return ok ? Results.Json(new { ok = true }) : Results.BadRequest(new { error = "Live2D 模型未加载" });
        });

        // 载入表情:自定义 → 应用存储值并返回(勾选行由前端标出);内置 → 只读预览(逐帧淡入)
        app.MapPost("/api/agents/{agentId}/pet/live2d/editor/expression/load", (string agentId, Live2dExprNameRequest req) =>
        {
            var result = Application.Current.Dispatcher.Invoke<object?>(() =>
            {
                var stage = Stage(manager, agentId);
                if (stage is null || !stage.IsLoaded) return null;
                var name = req.Name ?? "";
                var stored = stage.GetCustomExpression(name);
                stage.ClearExpressionImmediate();
                stage.ResetAllParameters();
                if (stored is not { } s)
                {
                    stage.SetExpression(name);
                    return new { isCustom = false, parameters = new Dictionary<string, float>(), parts = new Dictionary<string, float>() };
                }
                foreach (var kv in s.Targets)
                    stage.SetParameterValue(kv.Key, kv.Value);
                foreach (var kv in s.PartTargets)
                    stage.SetPartOpacity(kv.Key, kv.Value);
                return new { isCustom = true, parameters = s.Targets, parts = s.PartTargets };
            });
            return result is null ? Results.BadRequest(new { error = "Live2D 模型未加载" }) : Results.Json(result);
        });

        // 新建自定义表情(各项默认值、空差量)
        app.MapPost("/api/agents/{agentId}/pet/live2d/editor/expression/new", (string agentId) =>
        {
            var result = Application.Current.Dispatcher.Invoke<object?>(() =>
            {
                var rt = manager.RequireRuntime(agentId);
                var pet = rt.Host.DesktopPet;
                var stage = pet?.Live2DStage;
                if (stage is null || !stage.IsLoaded) return null;
                var taken = stage.GetExpressionNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                var name = "自定义表情1";
                for (var i = 2; !taken.Add(name); i++)
                    name = $"自定义表情{i}";
                stage.ClearExpressionImmediate();
                stage.StopMotions();
                stage.ResetAllParameters();
                stage.ResetPartsToSetup();
                stage.SaveCustomExpressionFrom(name, new Dictionary<string, float>(), new Dictionary<string, float>());
                PersistExpressions(pet, stage);
                return new { name };
            });
            return result is null ? Results.BadRequest(new { error = "Live2D 模型未加载" }) : Results.Json(result);
        });

        // 保存自定义表情:名字以名字框为准(改动即重命名),只保存勾选行(显式目标值)
        app.MapPost("/api/agents/{agentId}/pet/live2d/editor/expression/save", (string agentId, Live2dExprSaveRequest req) =>
        {
            var result = Application.Current.Dispatcher.Invoke<object?>(() =>
            {
                var rt = manager.RequireRuntime(agentId);
                var pet = rt.Host.DesktopPet;
                var stage = pet?.Live2DStage;
                if (stage is null || !stage.IsLoaded) return null;
                var name = (req.Name ?? "").Trim();
                if (name.Length == 0)
                    return new { error = "表情名字不能为空。" };
                var oldName = (req.OldName ?? "").Trim();
                if (oldName.Length > 0 && !string.Equals(oldName, name, StringComparison.Ordinal))
                {
                    if (stage.HasCustomExpression(name))
                        return new { error = $"名字「{name}」已存在,请换一个。" };
                    if (!stage.RenameCustomExpression(oldName, name))
                        return new { error = $"重命名为「{name}」失败。" };
                }
                stage.SaveCustomExpressionFrom(name, req.Params ?? new Dictionary<string, float>(), req.Parts ?? new Dictionary<string, float>());
                PersistExpressions(pet, stage);
                var custom = stage.GetCustomExpressionNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new
                {
                    ok = true,
                    name,
                    expressions = stage.GetExpressionNames().Select(n => new Live2dExprDto(n, custom.Contains(n))).ToList(),
                };
            });
            if (result is null)
                return Results.BadRequest(new { error = "Live2D 模型未加载" });
            var err = (string?)result.GetType().GetProperty("error")?.GetValue(result);
            return err is not null ? Results.BadRequest(new { error = err }) : Results.Json(result);
        });

        // 删除自定义表情(内置不可删,前端已拦截;此处再防一层)
        app.MapPost("/api/agents/{agentId}/pet/live2d/editor/expression/delete", (string agentId, Live2dExprNameRequest req) =>
        {
            var result = Application.Current.Dispatcher.Invoke<object?>(() =>
            {
                var rt = manager.RequireRuntime(agentId);
                var pet = rt.Host.DesktopPet;
                var stage = pet?.Live2DStage;
                if (stage is null || !stage.IsLoaded) return null;
                var name = req.Name ?? "";
                if (!stage.HasCustomExpression(name))
                    return new { error = "内置表情不可删除。" };
                stage.RemoveCustomExpression(name);
                PersistExpressions(pet, stage);
                var custom = stage.GetCustomExpressionNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new
                {
                    ok = true,
                    expressions = stage.GetExpressionNames().Select(n => new Live2dExprDto(n, custom.Contains(n))).ToList(),
                };
            });
            if (result is null)
                return Results.BadRequest(new { error = "Live2D 模型未加载" });
            var err = (string?)result.GetType().GetProperty("error")?.GetValue(result);
            return err is not null ? Results.BadRequest(new { error = err }) : Results.Json(result);
        });

        app.MapGet("/api/fs/list", (string? path) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    // 此电脑:常用文件夹 + 盘符(常用夹可直接进入)
                    var roots = new List<FsEntryDto>
                    {
                        new("桌面", true, null, null, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                        new("文档", true, null, null, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                        new("下载", true, null, null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
                        new("图片", true, null, null, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                        new("音乐", true, null, null, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
                        new("视频", true, null, null, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
                    };
                    roots.AddRange(Directory.GetLogicalDrives()
                        .Select(d => new FsEntryDto(d, true, null, null, d)));
                    return Results.Json(new FsListing("", null, roots));
                }
                var full = Path.GetFullPath(path);
                if (!Directory.Exists(full))
                    full = Path.GetDirectoryName(full) ?? "";
                if (full.Length == 0)
                    return Results.Json(new FsListing("", null,
                        Directory.GetLogicalDrives()
                            .Select(d => new FsEntryDto(d, true, null, null, d))
                            .ToList()));
                if (Directory.Exists(full))
                    _lastBrowseDir = full; // 服务端记住上次浏览位置
                var parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var entries = new DirectoryInfo(full).EnumerateFileSystemInfos()
                    .Select(i => new FsEntryDto(
                        i.Name,
                        i is DirectoryInfo,
                        i is FileInfo f ? f.Length : null,
                        i.LastWriteTime,
                        null))
                    .OrderByDescending(e => e.IsDir)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(2000)
                    .ToList();
                return Results.Json(new FsListing(full, parent, entries));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // 上次浏览位置(文件选择器打开时恢复)
        app.MapGet("/api/fs/last", () => Results.Json(new { path = _lastBrowseDir }));

        // Windows 系统语音清单(SAPI;供 TTS windows 引擎的"系统语音角色"下拉)
        app.MapGet("/api/agents/{agentId}/tts/windows-voices", (string agentId) =>
        {
            _ = agentId;
            var voices = Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    using var sapi = new System.Speech.Synthesis.SpeechSynthesizer();
                    return sapi.GetInstalledVoices()
                        .Where(v => v.Enabled)
                        .Select(v => v.VoiceInfo.Name)
                        .Distinct()
                        .ToList();
                }
                catch
                {
                    return new List<string>();
                }
            });
            return Results.Json(voices);
        });

        // ───────── 外部插件(plugins 目录 DLL):设置页按 [PluginSetting] 描述符自动生成 ─────────

        // 清单:名称 + 启停 + 全部设置项描述符(含当前值),前端据此渲染卡片与通用设置模态
        app.MapGet("/api/agents/{agentId}/external-plugins", (string agentId) =>
        {
            var list = Application.Current.Dispatcher.Invoke(() =>
                manager.RequireRuntime(agentId).Host.ExternalPlugins.Select(p => ExternalPluginDto(p)).ToList());
            return Results.Json(list);
        });

        // ───────── 技能(固定存于用户目录 .apet\skills):清单/读写/删除/启停 ─────────

        // 技能清单(名称/简介/停用标记)
        app.MapGet("/api/agents/{agentId}/skills", (string agentId) =>
        {
            var list = Application.Current.Dispatcher.Invoke(() =>
                manager.RequireRuntime(agentId).Host.Skills?.ListEntries().ToList() ?? []);
            return Results.Json(list);
        });

        // 技能全文(编辑用)
        app.MapGet("/api/agents/{agentId}/skills/{name}", (string agentId, string name) =>
        {
            var detail = Application.Current.Dispatcher.Invoke(() =>
                manager.RequireRuntime(agentId).Host.Skills?.ReadSkill(name));
            return detail is null ? Results.NotFound() : Results.Json(detail);
        });

        // 新建/覆盖技能(导入文件与手动填写共用:前端读文件后传内容)
        app.MapPost("/api/agents/{agentId}/skills", (string agentId, SkillWriteRequest req) =>
        {
            try
            {
                var name = Application.Current.Dispatcher.Invoke(() =>
                {
                    var skills = manager.RequireRuntime(agentId).Host.Skills
                        ?? throw new InvalidOperationException("技能插件不可用");
                    skills.WriteSkill(req.Name, req.Description ?? "", req.Content ?? "");
                    return APet.Plugins.Skills.SkillsPlugin.SanitizeName(req.Name);
                });
                return Results.Json(new { ok = true, name });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        // 删除技能(整目录)
        app.MapDelete("/api/agents/{agentId}/skills/{name}", (string agentId, string name) =>
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    APet.Plugins.Skills.SkillsPlugin.DeleteSkill(name);
                    var cfg = manager.RequireRuntime(agentId).Config;
                    cfg.Skills.DisabledSkills.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
                    cfg.Save();
                });
                return Results.Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        // 技能启停:写配置停用清单并热推插件(立即影响索引与 skill 工具)
        app.MapPost("/api/agents/{agentId}/skills/{name}/enabled", (string agentId, string name, ToggleRequest req) =>
        {
            var ok = Application.Current.Dispatcher.Invoke(() =>
            {
                var rt = manager.RequireRuntime(agentId);
                var changed = false;
                if (req.Enabled)
                    changed = rt.Config.Skills.DisabledSkills.RemoveAll(
                        s => s.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
                else if (!rt.Config.Skills.DisabledSkills.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    rt.Config.Skills.DisabledSkills.Add(name);
                    changed = true;
                }
                if (changed)
                {
                    rt.Host.ApplySkillsSettings(rt.Config.Skills);
                    rt.Config.Save();
                }
                return true;
            });
            return Results.Json(new { ok });
        });

        // 批量保存设置:TrySetSettings 先整体校验后应用(失败返回 400 与原因),快照回持久化袋
        app.MapPost("/api/agents/{agentId}/external-plugins/{pluginName}/settings",
            (string agentId, string pluginName, Dictionary<string, JsonElement> values) =>
        {
            try
            {
                var settings = Application.Current.Dispatcher.Invoke(() =>
                {
                    var host = manager.RequireRuntime(agentId).Host;
                    var latest = host.ApplyExternalPluginSettings(pluginName, values);
                    return latest.Select(ExternalSettingDto).ToList();
                });
                return Results.Json(new { ok = true, settings });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        // 启停:经注册对象官方开关(SetEnabled 驱动 Initialize/Dispose)并持久化;
        // 启用后若插件重写了异步初始化(InitializeAsync)在此 await,前端拿到的即"已就绪"状态
        app.MapPost("/api/agents/{agentId}/external-plugins/{pluginName}/enabled",
            async (string agentId, string pluginName, ToggleRequest req) =>
        {
            try
            {
                var ok = await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    var rt = manager.RequireRuntime(agentId);
                    rt.Host.SetExternalPluginEnabled(pluginName, req.Enabled);
                    if (req.Enabled)
                        await (rt.Host.Agent.Plugins.Find(pluginName)?.EnsureInitialized() ?? Task.CompletedTask);
                    return true;
                });
                return Results.Json(new { ok });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });

        // 重置:销毁实例 + 清空持久化设置袋,以默认值重建(保留启停状态);设置页"恢复默认"
        app.MapPost("/api/agents/{agentId}/external-plugins/{pluginName}/reset",
            async (string agentId, string pluginName) =>
        {
            try
            {
                var ok = await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    var rt = manager.RequireRuntime(agentId);
                    if (!rt.Host.ResetExternalPlugin(pluginName))
                        return false;
                    var reg = rt.Host.Agent.Plugins.Find(pluginName);
                    if (reg is { Enabled: true })
                        await reg.EnsureInitialized();
                    return true;
                });
                return Results.Json(new { ok });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message });
            }
        });
    }

    /// <summary>外部插件卡片元数据 + 全部设置项描述符(含当前值,枚举转名称字符串)。</summary>
    private static object ExternalPluginDto(AgentPluginBase plugin) => new
    {
        name = plugin.Name,
        enabled = plugin.Enabled,
        version = plugin.Version,
        author = plugin.Author,
        description = plugin.Description,
        loadOrder = plugin.LoadOrder,
        providesData = plugin.ProvidesData,
        hasUi = PluginUi.HasUi(plugin),
        uiSlots = plugin.UiSlots,
        settings = plugin.GetSettings().Select(ExternalSettingDto).ToList(),
    };

    private static object ExternalSettingDto(PluginSettingDescriptor s) => new
    {
        name = s.Name,
        label = s.Label,
        group = s.Group,
        uiHint = s.UiHint,
        valueType = s.ValueType,
        description = s.Description,
        min = s.Min,
        max = s.Max,
        options = s.Options,
        currentValue = ExternalPluginValues.Normalize(s.CurrentValue),
    };

    /// <summary>
    /// 反射值拷贝:简单值/集合整体搬运;复杂配置对象递归拷贝(保持现有实例身份,
    /// 宿主/插件可能长期持有嵌套配置的引用);JsonIgnore(计算属性)跳过。
    /// </summary>
    private static void CopyObject(object src, object dst, bool skipId = false)
    {
        foreach (var prop in dst.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || prop.GetMethod is null)
                continue;
            if (prop.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                continue;
            if (skipId && prop.Name == nameof(AgentConfig.Id))
                continue;
            object? value;
            try
            {
                value = prop.GetValue(src);
            }
            catch
            {
                continue; // 计算属性抛异常等:跳过
            }
            if (value is null)
                continue;
            var type = prop.PropertyType;
            var effective = value.GetType();
            if (effective.IsPrimitive || value is decimal or DateTime or string)
            {
                prop.SetValue(dst, value);
            }
            else if (type.IsClass && !typeof(IEnumerable).IsAssignableFrom(type) && type == value.GetType())
            {
                var dstChild = prop.GetValue(dst);
                if (dstChild is null)
                    prop.SetValue(dst, value);
                else
                    CopyObject(value, dstChild); // 递归:嵌套配置逐字段搬
            }
            else
            {
                prop.SetValue(dst, value); // 集合/字典/可空结构:整体替换(读取方都经属性访问)
            }
        }
    }

    private sealed record ToolDto(string Name, string DisplayName, bool Enabled);

    private sealed record ToolGroupDto(string Name, string Description, List<ToolDto> Tools);

    private sealed record ToggleRequest(bool Enabled);

    private sealed record FetchModelsRequest(string? BaseUrl, string? ApiKey);

    private sealed record TtsModelsRequest(string? ServerUrl);

    private sealed record BiliWhitelistCandidateDto(string Name, string Label, bool Selected, bool Disabled);

    private sealed record PetMotionDto(string Name, string Detail);

    private sealed record PlayMotionRequest(string? Name);

    private sealed record BubblePreviewRequest(
        int? TopOffset, int? LeftOffset, int? RightOffset,
        string? FontFamily, double? FontSize, string? FontStyle,
        string? TextColor, string? FillColor, string? BorderColor,
        double? BorderWidth, int? CornerRadius, int? TailLength, int? MaxWidth, int? OpacityPercent);

    private sealed record FsEntryDto(string Name, bool IsDir, long? Size, DateTime? Mtime, string? Target = null);

    private sealed record FsListing(string Path, string? Parent, List<FsEntryDto> Entries);

    private sealed record Live2dParamDto(string Id, string Name, string Group, double Min, double Max, double Default, double Value);

    private sealed record Live2dPartDto(string Id, double Opacity, double Setup);

    private sealed record Live2dExprDto(string Name, bool IsCustom);

    private sealed record Live2dParamValueRequest(string? Id, double Value);

    private sealed record Live2dPartValueRequest(string? Id, double Value);

    private sealed record Live2dFallbackRequest(Dictionary<string, float>? Opacities);

    private sealed record Live2dExprNameRequest(string? Name);

    private sealed record SkillWriteRequest(string Name, string? Description, string? Content);
    private sealed record Live2dExprSaveRequest(string? Name, string? OldName, Dictionary<string, float>? Params, Dictionary<string, float>? Parts);
}
