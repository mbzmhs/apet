using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using APet.Framework.Logging;
using APet.Framework.Models;

namespace APet.UI.Services;

// ───────── 会话持久化模型 ─────────

/// <summary>持久化的一步(正文段/思考段/工具调用行,按发生顺序;Kind 区分)。</summary>
public sealed class StoredStep
{
    /// <summary>content / thinking / tool。</summary>
    public string Kind { get; set; } = "";

    /// <summary>正文段或思考段文本;工具行未用。</summary>
    public string Text { get; set; } = "";

    /// <summary>思考段耗时(秒)。</summary>
    public double? Seconds { get; set; }

    public string ToolName { get; set; } = "";
    public string ToolDisplayName { get; set; } = "";
    public string Summary { get; set; } = "";
    public string DetailBody { get; set; } = "";
    public string ResultSummary { get; set; } = "";

    /// <summary>文件类工具的文件名链接(点击用默认应用打开);旧文件无此字段时为空(仅无链接)。</summary>
    public string PathLink { get; set; } = "";

    /// <summary>链接打开的目标路径(原始参数路径,点击时按会话工作目录再解析)。</summary>
    public string? PathOpenTarget { get; set; }

    /// <summary>解析打开目标所用的会话工作目录。</summary>
    public string? PathWorkdir { get; set; }

    /// <summary>写入/编辑/补丁类的行数统计(如 "+111" / "-3");旧文件无此字段时为空。</summary>
    public string StatAdd { get; set; } = "";
    public string StatDel { get; set; } = "";

    /// <summary>编辑/补丁类工具的彩色 diff 行。</summary>
    public List<StoredDiffLine> Diff { get; set; } = new();

    /// <summary>工具结果图片(如截图):该会话图片目录(images/{sessionId}/)下的本地文件名,重启后按名读回。</summary>
    public List<string> ImageFiles { get; set; } = new();

    /// <summary>工具调用失败(报错/非零退出/超时):结果摘要红色显示;旧文件无此字段时为 false。</summary>
    public bool IsError { get; set; }

    /// <summary>本步渲染自的 context 消息 id(正文段/思考段=该轮助手消息 id;旧文件无此字段时为 null)。</summary>
    public string? MsgId { get; set; }

    /// <summary>工具行发起调用的 tool_call_id(与 context 中 tool_calls[].id / tool_call_id 互链;旧文件无此字段时为 null)。</summary>
    public string? ToolCallId { get; set; }

    /// <summary>关联的子代理运行 id(sub_* 工具行;旧文件无此字段时为 null,行不可点击)。</summary>
    public int? SubRunId { get; set; }

    /// <summary>子代理任务名称(替代"运行 #N"式模糊标题;旧文件无此字段时为空)。</summary>
    public string SubRunTitle { get; set; } = "";
}

/// <summary>持久化的一行 diff(行号 + 内容 + add/del/ctx)。</summary>
public sealed class StoredDiffLine
{
    public string LineNo { get; set; } = "";
    public string Text { get; set; } = "";
    public string Kind { get; set; } = "";
}

/// <summary>持久化的一个文本文件附件(用户消息的文件卡片:文件名 + 全文,内容随会话 JSON 内联落盘)。</summary>
public sealed class StoredTextFile
{
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>持久化的一条消息(聊天区气泡)。</summary>
public sealed class StoredMessage
{
    /// <summary>User / Assistant / Status。</summary>
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Time { get; set; }

    /// <summary>宿主分配的用户消息标识(GUID,与 context 中同轮用户消息的 client_id 一致),供 /undo 精确匹配;旧文件无此字段时为 null。</summary>
    public string? ClientId { get; set; }

    /// <summary>助手气泡的回合关联:触发本轮的用户消息 id(= 该 context 用户消息的 id),
    /// 把回合粒度的 display 气泡定位到 context 消息区间;旧文件无此字段时为 null。</summary>
    public string? RunClientId { get; set; }

    /// <summary>本气泡对应的 context 消息 id 集合(与 messages 表 msg_key 同键):助手气泡 = 该轮全部
    /// 助手消息 id(按请求顺序,含中间工具调用消息);用户气泡 = 其 ClientId。
    /// UI 凭它取回原始消息(每条的用量/模型/思考/工具参数)显示详情;空集合 = 无关联。</summary>
    public List<string> MsgIds { get; set; } = new();

    /// <summary>用户消息携带的图片:该会话图片目录(images/{sessionId}/)下的本地文件名(内容哈希 + 原始扩展名),重启后按名读回。</summary>
    public List<string> ImageFiles { get; set; } = new();

    /// <summary>用户消息携带的文本文件附件(气泡内文件卡片,点击打开文本查看器)。</summary>
    public List<StoredTextFile> TextFiles { get; set; } = new();

    /// <summary>旧版字段(data URL 内联);新版不再读写,仅为兼容读取旧文件保留。</summary>
    public List<string> ImageDataUrls { get; set; } = new();

    /// <summary>错误状态(错误状态行/失败的工具调用,红色显示);旧文件无此字段时为 false。</summary>
    public bool IsError { get; set; }

    public List<StoredStep> Steps { get; set; } = new();
}

/// <summary>一个会话的完整持久化数据(SQLite 模式下:标量入 sessions 行,Context/Display 按行展开)。</summary>
public sealed class StoredSession
{
    /// <summary>会话 GUID(即文件名,重命名/归档不影响)。</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "新对话";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>归档标记:归档后不出现在左侧列表,可在归档窗口移出或永久删除。</summary>
    public bool Archived { get; set; }

    /// <summary>用户上一条消息发出后是否发生过上下文压缩(压缩不可撤销):/undo 需等再发送一条新消息后才能撤销。旧文件无此字段时反序列化为 false。</summary>
    public bool CompactedSinceLastSend { get; set; }

    /// <summary>框架上下文消息(直接对齐 wire 格式;重启后原样恢复,续聊不丢记忆)。</summary>
    public List<ChatMessage> Context { get; set; } = new();

    /// <summary>聊天界面渲染数据(气泡与步骤流,重启后还原显示)。</summary>
    public List<StoredMessage> Display { get; set; } = new();

    /// <summary>会话级工作目录(重启后恢复);空 = 未设置。旧文件无此字段时为空。</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>会话级工作目录权限:ReadOnly / ReadWrite / FullAccess / Ask。</summary>
    public string WorkingDirectoryAccess { get; set; } = "ReadWrite";

    /// <summary>会话内"信任此目录"积累的白名单(按工具权限类别),随会话恢复。</summary>
    public List<StoredDirectoryRule> WhitelistedDirectories { get; set; } = new();

    /// <summary>注入的情景描述($scene 指令):随会话持久化,拼进 system prompt(多次注入以最后一次为准)。旧文件无此字段时为空。</summary>
    public string Scene { get; set; } = "";

    /// <summary>会话级 LLM 选择:供应商名(会话各自独立,工具栏只改当前选中会话)。旧文件无此字段时为空 = 由用户重新选择。</summary>
    public string ChatProvider { get; set; } = "";

    /// <summary>会话级 LLM 选择:模型名。旧文件无此字段时为空 = 由用户重新选择。</summary>
    public string ChatModel { get; set; } = "";

    // ───────── 会话累计 LLM 用量(状态栏持久化;旧文件无此字段时反序列化为 0) ─────────

    /// <summary>会话累计 prompt tokens。</summary>
    public int UsagePromptTotal { get; set; }

    /// <summary>会话累计 Prompt 缓存命中 tokens。</summary>
    public int UsageCachedTotal { get; set; }

    /// <summary>会话累计输出 tokens。</summary>
    public int UsageCompletionTotal { get; set; }

    /// <summary>最近一次请求的输出速度(tokens/s)。</summary>
    public double UsageLastTps { get; set; }

    /// <summary>会话累计费用(元)。</summary>
    public double UsageCost { get; set; }

    /// <summary>会话级思考等级(normalize 后的规范值)。旧文件无此字段时为空 = 沿用 Agent 默认。</summary>
    public string ThinkingEffort { get; set; } = "";

    /// <summary>上一轮实际使用的供应商/模型(模型切换分割线的比对源;旧文件无此字段时反序列化为空
    /// = 重启后首轮不出分割线,第二轮起恢复正常)。</summary>
    public string LastRoundProvider { get; set; } = "";

    /// <summary>上一轮实际使用的模型名(与 LastRoundProvider 成对)。</summary>
    public string LastRoundModel { get; set; } = "";

    /// <summary>服务端回报的真实上下文占用(KV tokens,最近一次 LLM 请求的用量):重启后上下文占用条按真实值还原,
    /// 压缩触发判断在首次新请求回报前也可据此决策。旧文件无此字段时反序列化为 0。</summary>
    public long LastPromptTokens { get; set; }
}

/// <summary>白名单条目的持久化形态(目录 + 限定工具类别,空 = 全部工具)。</summary>
public sealed class StoredDirectoryRule
{
    public string Directory { get; set; } = "";
    public string Tool { get; set; } = "";
}

/// <summary>会话列表文件夹的一行(folders 表):parent_id 为 null = 顶级;sort = 同级组内序号;
/// Color = 预设色板 id(见前端 FOLDER_COLORS),'' = 默认(不着色)。</summary>
public sealed record FolderRow(string Id, string Name, string? ParentId, long Sort, string Color);

// ───────── 存储 ─────────

/// <summary>
/// 会话持久化存储:目录为 agents/{agentId}/sessions(Agent 自己的文件夹内),SQLite 单库
/// sessions.db:sessions 表每会话一行标量字段,messages 表每条上下文消息一行、
/// display 表每条气泡一行,Save 走行级 diff(只写变化行,不整库重写)。SQLite 打开失败时
/// 会话仅存于内存不落盘(应用不因存储异常不可用)。Agent 的稳定 Id(新增时随机生成、
/// 改名不变)保证会话始终对应到自己的文件夹。库内同时保存框架上下文(续聊不丢)
/// 与界面渲染数据(重启还原气泡)。图片不内联进库:单独存放于 agents/{agentId}/images/{sessionId}/
/// (每图一个 {内容哈希}{原始扩展名} 文件,会话内同内容复用同一文件),会话数据只存文件名引用。
/// 图片目录按会话隔离:永久删除会话 = 删库行 + 递归删其图片目录,无需遍历引用,
/// 结构上保证不产生孤儿文件、也不会误删其他会话引用的图片。
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions s_rowJson = new(); // 行级紧凑序列化(无缩进)

    /// <summary>上下文图片外置引用前缀:保存时 data URL 换成本地文件名引用,加载时还原。</summary>
    internal const string ImageRefPrefix = "apetimg://";

    private readonly string _dir;
    private readonly string _imagesDir;
    private readonly string _todosDir;

    /// <summary>按 Agent 复用的共享实例(每 Agent 一条 SQLite 连接)。
    /// 此前图片端点每 GET、SseRelay 每轮、归属检查都 new 一个:各开一条连接并重跑 PRAGMA/全量 schema DDL,
    /// 类无 Dispose,句柄随轮次线性增长。DB 访问全程 _gate 串行,共享安全。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionStore> s_shared =
        new(StringComparer.Ordinal);

    public static SessionStore Shared(string agentId) => s_shared.GetOrAdd(agentId, id => new SessionStore(id));

    /// <summary>会话 SQLite 库(sessions/sessions.db):行级增量写。
    /// 打开失败时 _db 为 null,各方法空转:会话仅存于内存,不落盘(应用不因存储异常不可用)。</summary>
    private readonly SqliteConnection? _db;
    private readonly object _gate = new();

    public SessionStore(string agentId)
    {
        var agentDir = Path.Combine(AppPaths.AgentsRoot, SafeDir(agentId));
        _dir = Path.Combine(agentDir, "sessions");
        _imagesDir = Path.Combine(agentDir, "images"); // 图片根目录;每会话独占子目录 images/{sessionId}/(按会话隔离)
        _todosDir = Path.Combine(agentDir, "todos");   // TODO 清单根目录;每会话独占子目录 todos/{sessionId}/(与图片目录同生命周期)
        _db = TryOpenDatabase();
    }

    /// <summary>Agent 运行数据目录(agents/{agentId}/;头像等 Agent 级文件放这里)。</summary>
    public static string AgentDir(string agentId) =>
        Path.Combine(AppPaths.AgentsRoot, SafeDir(agentId));

    /// <summary>会话的图片目录(与 JSON 同生命周期:删会话即递归删此目录)。</summary>
    private string SessionImagesDir(string sessionId) => Path.Combine(_imagesDir, SafeDir(sessionId));

    /// <summary>会话的 TODO 清单目录(todo 插件按会话持久化):删会话即递归删此目录。</summary>
    private string SessionTodoDir(string sessionId) => Path.Combine(_todosDir, SafeDir(sessionId));

    // ───────── 图片文件管理(agents/{agentId}/images/{sessionId}/{内容哈希}{原始扩展名}) ─────────

    /// <summary>写入一张图片到该会话的图片目录,返回文件名(内容 SHA-256 + 原始扩展名)。
    /// 同名即同内容:重复保存/重启后重写都幂等复用同一文件,会话内天然去重。</summary>
    public string StoreImage(string sessionId, byte[] bytes, string ext)
    {
        var dir = SessionImagesDir(sessionId);
        Directory.CreateDirectory(dir);
        var name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + NormalizeExt(ext);
        var path = Path.Combine(dir, name);
        if (!File.Exists(path))
            File.WriteAllBytes(path, bytes);
        return name;
    }

    private static string NormalizeExt(string ext)
    {
        ext = (ext ?? "").Trim().ToLowerInvariant();
        if (ext.Length == 0) return ".png";
        return ext.StartsWith('.') ? ext : "." + ext;
    }

    /// <summary>按文件名读回该会话的图片字节;文件不存在或读取失败返回 null。</summary>
    public byte[]? ReadImage(string sessionId, string fileName)
    {
        try
        {
            var path = Path.Combine(SessionImagesDir(sessionId), Path.GetFileName(fileName)); // GetFileName 防路径逃逸
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把一个会话的图片目录整体复制到另一个(内容哈希同名,已存在则跳过;分叉会话时让新 Id 立即可读回原图)。</summary>
    public void CopySessionImages(string fromSessionId, string toSessionId)
    {
        CopyDirectory(SessionImagesDir(fromSessionId), SessionImagesDir(toSessionId));
    }

    /// <summary>把一个会话的 TODO 清单复制到另一个(分叉会话时新会话继承原任务清单)。</summary>
    public void CopySessionTodo(string fromSessionId, string toSessionId)
    {
        CopyDirectory(SessionTodoDir(fromSessionId), SessionTodoDir(toSessionId));
    }

    /// <summary>目录整体复制(源不存在为空操作;文件同名跳过)。</summary>
    private static void CopyDirectory(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from))
            {
                var dest = Path.Combine(to, Path.GetFileName(file));
                if (!File.Exists(dest)) File.Copy(file, dest);
            }
        }
        catch (Exception ex)
        {
            Log.Global.Error("[SessionStore] copy directory failed: " + ex.Message);
        }
    }

    // ───────── 上下文图片外置/还原(保存时 data URL → 本地文件引用;加载时反向) ─────────

    private void ExternalizeContextImages(StoredSession session)
    {
        // vm.Session.Messages 与框架内存共享同一批 ChatMessage 实例:必须先深拷贝再改写,
        // 否则把 data URL 换成文件引用会污染内存上下文——下一次请求会把 apetimg:// 当图片地址发给模型(HTTP 400)
        session.Context = session.Context.Select(CloneMessage).ToList();

        var cache = new Dictionary<string, string>(); // 同一次保存内相同 data URL 去重
        foreach (var msg in session.Context)
        {
            if (msg.ContentParts is not { Count: > 0 } parts) continue;
            var changed = false;
            for (var i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Type != "image_url" || p.ImageUrl?.Url is not { Length: > 5 } url
                    || !url.StartsWith("data:", StringComparison.Ordinal))
                    continue;
                if (!cache.TryGetValue(url, out var name))
                {
                    name = StoreImageFromDataUrl(session.Id, url);
                    if (name is null) continue; // 解码失败保留原 data URL,不丢数据
                    cache[url] = name;
                }
                parts[i] = ChatContentPart.ImagePart(ImageRefPrefix + name);
                changed = true;
            }
            if (changed) msg.ContentParts = parts;
        }
    }

    /// <summary>深拷贝一条框架消息(落盘/分叉用,避免改写共享实例)。</summary>
    internal static ChatMessage CloneMessage(ChatMessage m) => new()
    {
        Role = m.Role,
        Content = m.Content,
        ReasoningContent = m.ReasoningContent,
        ToolCalls = m.ToolCalls is null ? null : m.ToolCalls.Select(t => new ToolCall
        {
            Id = t.Id,
            Type = t.Type,
            Function = new ToolCallFunction { Name = t.Function.Name, Arguments = t.Function.Arguments },
        }).ToList(),
        ToolCallId = m.ToolCallId,
        ClientId = m.ClientId, // 宿主标识随克隆保留:落盘/分叉后 /undo 仍能按 Id 精确匹配 context(丢失会导致重启后无法回滚)
        Id = m.Id, // 消息标识随克隆保留:分叉会话的消息仍指向原 context 记录,display 关联不断链
        TimeCreated = m.TimeCreated,
        TimeCompleted = m.TimeCompleted,
        Usage = m.Usage is null ? null : new MessageUsage(m.Usage.PromptTokens, m.Usage.CompletionTokens, m.Usage.TotalTokens, m.Usage.CachedTokens, m.Usage.ReasoningTokens),
        Finish = m.Finish,
        Provider = m.Provider,
        Model = m.Model,
        ContentParts = m.ContentParts is null ? null : m.ContentParts.Select(p => p.Type == "image_url"
            ? ChatContentPart.ImagePart(p.ImageUrl?.Url ?? "")
            : ChatContentPart.TextPart(p.Text ?? "")).ToList(),
    };

    private string? StoreImageFromDataUrl(string sessionId, string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 8 || !dataUrl.StartsWith("data:", StringComparison.Ordinal)) return null;
            var mime = dataUrl[5..comma].Split(';')[0];
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            return StoreImage(sessionId, bytes, MimeToExt(mime));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把一张 data URL 图片写入该会话的图片目录并返回文件名(内容哈希去重;解码失败返回 null)。供工具结果图片(如截图)落盘。</summary>
    public string? StoreImageDataUrl(string sessionId, string dataUrl) => StoreImageFromDataUrl(sessionId, dataUrl);

    private void RestoreContextImages(StoredSession session)
    {
        foreach (var msg in session.Context)
        {
            if (msg.ContentParts is not { Count: > 0 } parts) continue;
            var changed = false;
            for (var i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Type != "image_url" || p.ImageUrl?.Url is not { } url
                    || url.Length <= ImageRefPrefix.Length
                    || !url.StartsWith(ImageRefPrefix, StringComparison.Ordinal))
                    continue;
                var bytes = ReadImage(session.Id, url.Substring(ImageRefPrefix.Length));
                if (bytes is null)
                {
                    parts.RemoveAt(i); // 图片文件缺失:丢弃该片段,避免把无效 URL 发给模型
                    i--;
                    changed = true;
                    continue;
                }
                var mime = ExtToMime(Path.GetExtension(url));
                parts[i] = ChatContentPart.ImagePart("data:" + mime + ";base64," + Convert.ToBase64String(bytes));
                changed = true;
            }
            if (changed) msg.ContentParts = parts;
        }
    }

    internal static string MimeToExt(string mime) => mime.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => ".bin",
    };

    private static string ExtToMime(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    /// <summary>agentId/sessionId 作为目录与文件名,过滤非法字符。</summary>
    private static string SafeDir(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            id = id.Replace(c, '_');
        return string.IsNullOrWhiteSpace(id) ? "default" : id.Trim();
    }

    /// <summary>读取本 Agent 全部会话(含归档),按 UpdatedAt 升序。DB 不可用时返回空列表。</summary>
    public List<StoredSession> LoadAll()
    {
        if (_db is not null)
        {
            try
            {
                lock (_gate)
                {
                    var result = new List<StoredSession>();
                    using (var cmd = _db.CreateCommand())
                    {
                        cmd.CommandText = "SELECT id FROM sessions ORDER BY updated_at";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            var s = LoadOneDb(reader.GetString(0));
                            if (s is not null) result.Add(s);
                        }
                    }
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] db loadall failed: " + ex.Message);
            }
        }
        return new List<StoredSession>();
    }

    public StoredSession? Load(string id)
    {
        if (_db is not null)
        {
            try
            {
                lock (_gate) return LoadOneDb(id);
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] db load failed: " + ex.Message);
            }
        }
        return null;
    }

    /// <summary>会话是否存在(DB 行或遗留 JSON 文件;含归档):深链归属解析用,不加载内容。</summary>
    public bool Exists(string id)
    {
        if (File.Exists(Path.Combine(_dir, id + ".json")))
            return true;
        if (_db is not null)
        {
            try
            {
                lock (_gate)
                {
                    using var cmd = _db.CreateCommand();
                    cmd.CommandText = "SELECT 1 FROM sessions WHERE id = $id";
                    cmd.Parameters.AddWithValue("$id", id);
                    return cmd.ExecuteScalar() is not null;
                }
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] db exists check failed: " + ex.Message);
            }
        }
        return false;
    }

    /// <summary>写入会话(自动刷新 UpdatedAt)。行级 diff 只写变化行(单事务);DB 不可用时空转不落盘。</summary>
    public void Save(StoredSession session)
    {
        if (_db is not null)
        {
            lock (_gate)
            {
                try
                {
                    using var tx = _db.BeginTransaction();
                    UpsertSessionRow(session, tx);
                    SyncMessages(session, tx);
                    SyncDisplay(session, tx);
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    Log.Global.Error("[SessionStore] db save failed: " + ex.Message);
                }
            }
        }
    }

    /// <summary>永久删除单个会话:库行 + 其整个图片目录 + TODO 清单目录(按会话隔离,结构上无孤儿)。</summary>
    public void Delete(string id)
    {
        if (_db is not null)
        {
            lock (_gate)
            {
                try
                {
                    using var cmd = _db.CreateCommand();
                    cmd.CommandText = "DELETE FROM sessions WHERE id=$id; DELETE FROM messages WHERE session_id=$id; DELETE FROM display WHERE session_id=$id; DELETE FROM session_slots WHERE session_id=$id;";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Log.Global.Error("[SessionStore] db delete failed: " + ex.Message);
                }
            }
        }
        try
        {
            var imgDir = SessionImagesDir(id);
            if (Directory.Exists(imgDir)) Directory.Delete(imgDir, recursive: true);
            var todoDir = SessionTodoDir(id);
            if (Directory.Exists(todoDir)) Directory.Delete(todoDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Global.Error("[SessionStore] delete failed: " + ex.Message);
        }
    }

    /// <summary>按稳定 id 取一条 context 原始消息(display 气泡的 MsgIds 回链入口):
    /// 含被压缩留档(compacted=1)的行,被压缩删掉的历史消息也能取回;不存在或 DB 不可用返回 null。</summary>
    public ChatMessage? GetContextMessage(string sessionId, string msgId)
    {
        if (_db is null) return null;
        try
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT json FROM messages WHERE session_id=$sid AND msg_key=$mid;";
                cmd.Parameters.AddWithValue("$sid", sessionId);
                cmd.Parameters.AddWithValue("$mid", msgId);
                using var r = cmd.ExecuteReader();
                return r.Read()
                    ? JsonSerializer.Deserialize<ChatMessage>(r.GetString(0), s_rowJson)
                    : null;
            }
        }
        catch (Exception ex)
        {
            Log.Global.Error("[SessionStore] get context message failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>批量按稳定 id 取 context 原始消息(气泡 MsgIds 悬停详情聚合):
    /// 含 compacted=1 留档行,按 seq 顺序返回;缺失的 id 跳过,全缺失或 DB 不可用返回空表。</summary>
    public List<ChatMessage> GetContextMessages(string sessionId, IEnumerable<string> msgIds)
    {
        if (_db is null) return new List<ChatMessage>();
        var ids = msgIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        if (ids.Count == 0) return new List<ChatMessage>();
        try
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT json FROM messages WHERE session_id=$sid AND msg_key IN ("
                    + string.Join(",", ids.Select((_, i) => "$id" + i)) + ") ORDER BY seq;";
                cmd.Parameters.AddWithValue("$sid", sessionId);
                for (var i = 0; i < ids.Count; i++)
                    cmd.Parameters.AddWithValue("$id" + i, ids[i]);
                using var r = cmd.ExecuteReader();
                var list = new List<ChatMessage>();
                while (r.Read())
                {
                    var msg = JsonSerializer.Deserialize<ChatMessage>(r.GetString(0), s_rowJson);
                    if (msg is not null) list.Add(msg);
                }
                return list;
            }
        }
        catch (Exception ex)
        {
            Log.Global.Error("[SessionStore] get context messages failed: " + ex.Message);
            return new List<ChatMessage>();
        }
    }

    // ==================== 列表布局:文件夹(嵌套)+ 会话组内排序 ====================
    // 独立于 Save/Load 的会话数据:folders 表存文件夹树(parent_id NULL = 顶级),
    // session_slots 表存组内槽位——会话与文件夹共用同一张表(session_id 列同时容纳两种 id):
    // 每个会话的归属(根/文件夹)、每个文件夹在其父组里的位置,同一组共享一个序号空间,
    // 因此会话与文件夹可以任意交错摆放。布局变更走这里专用方法,
    // 会话保存绝不触碰;DB 不可用时空转(布局仅存于内存/不落盘,与其余数据同策略)。

    /// <summary>全部文件夹(按组内序号,含各级)。DB 不可用返回空表。</summary>
    public List<FolderRow> ListFolders()
    {
        if (_db is null) return new List<FolderRow>();
        try
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT id, name, parent_id, sort, color FROM folders ORDER BY sort;";
                using var r = cmd.ExecuteReader();
                var list = new List<FolderRow>();
                while (r.Read())
                    list.Add(new FolderRow(r.GetString(0), r.GetString(1),
                        r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3), r.GetString(4)));
                return list;
            }
        }
        catch (Exception ex)
        {
            Log.Global.Error("[SessionStore] list folders failed: " + ex.Message);
        }
        return new List<FolderRow>();
    }

    /// <summary>全部会话槽位:session_id → (归属文件夹, 组内序号)。DB 不可用返回空表。</summary>
    public Dictionary<string, (string? FolderId, long Sort)> ListSessionSlots()
    {
        if (_db is null) return new Dictionary<string, (string?, long)>();
        try
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT session_id, folder_id, sort FROM session_slots;";
                using var r = cmd.ExecuteReader();
                var map = new Dictionary<string, (string?, long)>();
                while (r.Read())
                    map[r.GetString(0)] = (r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2));
                return map;
            }
        }
        catch (Exception ex)
        {
            Log.Global.Error("[SessionStore] list session slots failed: " + ex.Message);
        }
        return new Dictionary<string, (string?, long)>();
    }

    /// <summary>文件夹是否存在(归档恢复时判"原文件夹还在不在")。</summary>
    public bool FolderExists(string id)
    {
        if (_db is null) return false;
        try
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM folders WHERE id=$id;";
                cmd.Parameters.AddWithValue("$id", id);
                return cmd.ExecuteScalar() is not null;
            }
        }
        catch (Exception ex)
        {
            Log.Global.Error("[SessionStore] folder exists check failed: " + ex.Message);
        }
        return false;
    }

    /// <summary>设置文件夹预设色(color 为色板 id,'' = 默认不着色)。文件夹不存在返回 false。</summary>
    public bool SetFolderColor(string id, string color)
    {
        if (_db is null) return false;
        lock (_gate)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE folders SET color=$color WHERE id=$id;";
                cmd.Parameters.AddWithValue("$color", color ?? "");
                cmd.Parameters.AddWithValue("$id", id);
                return cmd.ExecuteNonQuery() == 1;
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] set folder color failed: " + ex.Message);
            }
        }
        return false;
    }

    /// <summary>补槽(启动初始化):已有槽位的原样保留,缺失的按给定顺序依次 append 到所属组末尾。</summary>
    public void EnsureSessionSlots(IEnumerable<(string Id, string? FolderId)> rows)
    {
        if (_db is null) return;
        lock (_gate)
        {
            try
            {
                using var tx = _db.BeginTransaction();
                // 各组 next 序号预读(根组键用哨兵值):无行的组从 0 起
                const string RootKey = "\0root";
                var nextSort = new Dictionary<string, long>();
                using (var cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT folder_id, COALESCE(MAX(sort), -1) + 1 FROM session_slots GROUP BY folder_id;";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        nextSort[r.IsDBNull(0) ? RootKey : r.GetString(0)] = r.GetInt64(1);
                }
                foreach (var (id, folderId) in rows)
                {
                    using (var chk = _db.CreateCommand())
                    {
                        chk.Transaction = tx;
                        chk.CommandText = "SELECT 1 FROM session_slots WHERE session_id=$id;";
                        chk.Parameters.AddWithValue("$id", id);
                        if (chk.ExecuteScalar() is not null) continue; // 已有槽:保留原归属/顺序
                    }
                    var scopeKey = folderId ?? RootKey;
                    if (!nextSort.TryGetValue(scopeKey, out var sort)) sort = 0;
                    nextSort[scopeKey] = sort + 1;
                    using var ins = _db.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO session_slots (session_id, folder_id, sort) VALUES ($id, $fid, $sort);";
                    ins.Parameters.AddWithValue("$id", id);
                    if (folderId is null) ins.Parameters.AddWithValue("$fid", DBNull.Value);
                    else ins.Parameters.AddWithValue("$fid", folderId);
                    ins.Parameters.AddWithValue("$sort", sort);
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] ensure session slots failed: " + ex.Message);
            }
        }
    }

    /// <summary>文件夹补槽(启动初始化/旧库升级):每个文件夹都在其父组占一个槽(= 列表位置)。
    /// 已有槽位的原样保留;缺失的 append 到父组末尾。正常流程文件夹行与槽位同事务写入、不会缺,
    /// 这里只是防御性兜底,任何情况下都不触碰既有条目的顺序。</summary>
    public void EnsureFolderSlots()
    {
        if (_db is null) return;
        lock (_gate)
        {
            try
            {
                using var tx = _db.BeginTransaction();
                // 各组 next 序号预读(根组键用哨兵值):无行的组从 0 起
                const string RootKey = "\0root";
                var nextSort = new Dictionary<string, long>();
                using (var cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT folder_id, COALESCE(MAX(sort), -1) + 1 FROM session_slots GROUP BY folder_id;";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        nextSort[r.IsDBNull(0) ? RootKey : r.GetString(0)] = r.GetInt64(1);
                }
                var pending = new List<(string Id, string? Parent)>();
                using (var cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT id, parent_id FROM folders ORDER BY sort;";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        pending.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
                }
                foreach (var (id, parent) in pending)
                {
                    using (var chk = _db.CreateCommand())
                    {
                        chk.Transaction = tx;
                        chk.CommandText = "SELECT 1 FROM session_slots WHERE session_id=$id;";
                        chk.Parameters.AddWithValue("$id", id);
                        if (chk.ExecuteScalar() is not null) continue; // 已有槽:保留原位置
                    }
                    var scopeKey = parent ?? RootKey;
                    if (!nextSort.TryGetValue(scopeKey, out var sort)) sort = 0;
                    nextSort[scopeKey] = sort + 1;
                    using var ins = _db.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO session_slots (session_id, folder_id, sort) VALUES ($id, $fid, $sort);";
                    ins.Parameters.AddWithValue("$id", id);
                    if (parent is null) ins.Parameters.AddWithValue("$fid", DBNull.Value);
                    else ins.Parameters.AddWithValue("$fid", parent);
                    ins.Parameters.AddWithValue("$sort", sort);
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] ensure folder slots failed: " + ex.Message);
            }
        }
    }

    /// <summary>把会话 append 到指定组末尾(新对话/分叉/移出归档补槽);已有槽位原样不动。folderId 为 null = 根组。</summary>
    public void AppendSessionSlot(string sessionId, string? folderId)
    {
        if (_db is null) return;
        lock (_gate)
        {
            try
            {
                using var tx = _db.BeginTransaction();
                long sort = NextSort(tx, "session_slots", "folder_id", folderId);
                using var ins = _db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO session_slots (session_id, folder_id, sort) VALUES ($id, $fid, $sort);";
                ins.Parameters.AddWithValue("$id", sessionId);
                if (folderId is null) ins.Parameters.AddWithValue("$fid", DBNull.Value);
                else ins.Parameters.AddWithValue("$fid", folderId);
                ins.Parameters.AddWithValue("$sort", sort);
                ins.ExecuteNonQuery();
                tx.Commit();
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] append session slot failed: " + ex.Message);
            }
        }
    }

    /// <summary>把会话移到另一组末尾(null = 根组):槽缺失则补建(幂等 upsert)。</summary>
    public void MoveSessionToFolder(string sessionId, string? folderId)
    {
        if (_db is null) return;
        lock (_gate)
        {
            try
            {
                using var tx = _db.BeginTransaction();
                long sort = NextSort(tx, "session_slots", "folder_id", folderId);
                using var up = _db.CreateCommand();
                up.Transaction = tx;
                up.CommandText = """
                    INSERT INTO session_slots (session_id, folder_id, sort) VALUES ($id, $fid, $sort)
                    ON CONFLICT(session_id) DO UPDATE SET folder_id=excluded.folder_id, sort=excluded.sort;
                    """;
                up.Parameters.AddWithValue("$id", sessionId);
                if (folderId is null) up.Parameters.AddWithValue("$fid", DBNull.Value);
                else up.Parameters.AddWithValue("$fid", folderId);
                up.Parameters.AddWithValue("$sort", sort);
                up.ExecuteNonQuery();
                tx.Commit();
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] move session to folder failed: " + ex.Message);
            }
        }
    }

    /// <summary>整组重排(会话+文件夹的混合槽位共用,可按任意交错顺序提交):
    /// 按给定顺序编号 0..N(拖拽落点后整序提交);不属于该组/不存在的 id 忽略。</summary>
    public void SetSessionOrder(string? folderId, IReadOnlyList<string> orderedIds)
    {
        if (_db is null) return;
        lock (_gate)
        {
            try
            {
                using var tx = _db.BeginTransaction();
                var inScope = new HashSet<string>();
                using (var cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    if (folderId is null)
                        cmd.CommandText = "SELECT session_id FROM session_slots WHERE folder_id IS NULL;";
                    else
                    {
                        cmd.CommandText = "SELECT session_id FROM session_slots WHERE folder_id=$fid;";
                        cmd.Parameters.AddWithValue("$fid", folderId);
                    }
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) inScope.Add(r.GetString(0));
                }
                var seq = 0;
                foreach (var id in orderedIds)
                {
                    if (!inScope.Contains(id)) continue;
                    using var up = _db.CreateCommand();
                    up.Transaction = tx;
                    up.CommandText = "UPDATE session_slots SET sort=$sort WHERE session_id=$id;";
                    up.Parameters.AddWithValue("$id", id);
                    up.Parameters.AddWithValue("$sort", (long)seq++);
                    up.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] set session order failed: " + ex.Message);
            }
        }
    }

    /// <summary>新建文件夹,返回新 id;父文件夹不存在抛 ArgumentException。
    /// 组内位置:append 到父组槽位末尾(与会话混排同一序号轴),既有条目位置一律不动。</summary>
    public string CreateFolder(string name, string? parentId)
    {
        if (_db is null) throw new InvalidOperationException("会话数据库不可用");
        if (parentId is not null && !FolderExists(parentId))
            throw new ArgumentException("父文件夹不存在", nameof(parentId));
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            long sort = NextSort(tx, "folders", "parent_id", parentId);
            var id = Guid.NewGuid().ToString("N");
            using var ins = _db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO folders (id, name, parent_id, sort) VALUES ($id, $name, $pid, $sort);";
            ins.Parameters.AddWithValue("$id", id);
            ins.Parameters.AddWithValue("$name", name);
            if (parentId is null) ins.Parameters.AddWithValue("$pid", DBNull.Value);
            else ins.Parameters.AddWithValue("$pid", parentId);
            ins.Parameters.AddWithValue("$sort", sort);
            ins.ExecuteNonQuery();
            // 列表槽位:与 folders 行同一事务落库,append 父组末尾(不触碰既有条目位置)
            long slotSort = NextSort(tx, "session_slots", "folder_id", parentId);
            using var insSlot = _db.CreateCommand();
            insSlot.Transaction = tx;
            insSlot.CommandText = "INSERT INTO session_slots (session_id, folder_id, sort) VALUES ($id, $fid, $sort);";
            insSlot.Parameters.AddWithValue("$id", id);
            if (parentId is null) insSlot.Parameters.AddWithValue("$fid", DBNull.Value);
            else insSlot.Parameters.AddWithValue("$fid", parentId);
            insSlot.Parameters.AddWithValue("$sort", slotSort);
            insSlot.ExecuteNonQuery();
            tx.Commit();
            return id;
        }
    }

    /// <summary>重命名文件夹;不存在返回 false。</summary>
    public bool RenameFolder(string id, string name)
    {
        if (_db is null) return false;
        lock (_gate)
        {
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE folders SET name=$name WHERE id=$id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$name", name);
                return cmd.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] rename folder failed: " + ex.Message);
            }
        }
        return false;
    }

    /// <summary>整组重排文件夹(同级):按给定顺序编号,未知 id 忽略。</summary>
    public void SetFolderOrder(string? parentId, IReadOnlyList<string> orderedIds)
    {
        if (_db is null) return;
        lock (_gate)
        {
            try
            {
                using var tx = _db.BeginTransaction();
                var inScope = new HashSet<string>();
                using (var cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    if (parentId is null)
                        cmd.CommandText = "SELECT id FROM folders WHERE parent_id IS NULL;";
                    else
                    {
                        cmd.CommandText = "SELECT id FROM folders WHERE parent_id=$pid;";
                        cmd.Parameters.AddWithValue("$pid", parentId);
                    }
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) inScope.Add(r.GetString(0));
                }
                var seq = 0;
                foreach (var id in orderedIds)
                {
                    if (!inScope.Contains(id)) continue;
                    using var up = _db.CreateCommand();
                    up.Transaction = tx;
                    up.CommandText = "UPDATE folders SET sort=$sort WHERE id=$id;";
                    up.Parameters.AddWithValue("$id", id);
                    up.Parameters.AddWithValue("$sort", (long)seq++);
                    up.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] set folder order failed: " + ex.Message);
            }
        }
    }

    /// <summary>把文件夹移到另一父级(null = 顶级),append 到该级末尾。
    /// 目标为自身或其后代(会成环)返回 false;文件夹不存在返回 false。</summary>
    public bool MoveFolder(string id, string? parentId)
    {
        if (_db is null) return false;
        lock (_gate)
        {
            try
            {
                if (parentId == id) return false;
                // 环保护:从目标父级一路往上走,走回 id = 目标是自己的后代
                var seen = new HashSet<string> { id };
                var cur = parentId;
                while (cur is not null)
                {
                    if (!seen.Add(cur)) return false;
                    using (var cmd = _db.CreateCommand())
                    {
                        cmd.CommandText = "SELECT parent_id FROM folders WHERE id=$id;";
                        cmd.Parameters.AddWithValue("$id", cur);
                        cur = cmd.ExecuteScalar() as string;
                    }
                }
                using var tx = _db.BeginTransaction();
                long sort = NextSort(tx, "folders", "parent_id", parentId);
                using var up = _db.CreateCommand();
                up.Transaction = tx;
                up.CommandText = "UPDATE folders SET parent_id=$pid, sort=$sort WHERE id=$id;";
                if (parentId is null) up.Parameters.AddWithValue("$pid", DBNull.Value);
                else up.Parameters.AddWithValue("$pid", parentId);
                up.Parameters.AddWithValue("$sort", sort);
                up.Parameters.AddWithValue("$id", id);
                if (up.ExecuteNonQuery() == 0) return false; // 文件夹不存在
                // 列表槽位跟着进目标组末尾(精确落点由前端随后的 reorder 精调);同组移动只是槽位重排
                long slotSort = NextSort(tx, "session_slots", "folder_id", parentId);
                using var upSlot = _db.CreateCommand();
                upSlot.Transaction = tx;
                upSlot.CommandText = """
                    INSERT INTO session_slots (session_id, folder_id, sort) VALUES ($id, $fid, $sort)
                    ON CONFLICT(session_id) DO UPDATE SET folder_id=excluded.folder_id, sort=excluded.sort;
                    """;
                upSlot.Parameters.AddWithValue("$id", id);
                if (parentId is null) upSlot.Parameters.AddWithValue("$fid", DBNull.Value);
                else upSlot.Parameters.AddWithValue("$fid", parentId);
                upSlot.Parameters.AddWithValue("$sort", slotSort);
                upSlot.ExecuteNonQuery();
                tx.Commit();
                return true;
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] move folder failed: " + ex.Message);
            }
        }
        return false;
    }

    /// <summary>删除文件夹:其子文件夹与组内会话(槽位一并)降级到被删文件夹的父级
    /// (相对顺序保持,append 到目标级末尾,不丢数据),同时删掉它自己在父组的槽位。不存在返回 false。</summary>
    public bool DeleteFolder(string id)
    {
        if (_db is null) return false;
        lock (_gate)
        {
            try
            {
                string? parent;
                using (var chk = _db.CreateCommand())
                {
                    chk.CommandText = "SELECT parent_id FROM folders WHERE id=$id;";
                    chk.Parameters.AddWithValue("$id", id);
                    var v = chk.ExecuteScalar();
                    if (v is null) return false; // 文件夹不存在
                    parent = v as string;
                }
                using var tx = _db.BeginTransaction();
                // 降级目标级的 next 序号必须先于移动预读(否则会把即将移入的子级也算进 MAX)
                long folderNext = NextSort(tx, "folders", "parent_id", parent);
                long sessionNext = NextSort(tx, "session_slots", "folder_id", parent);

                var childFolders = new List<string>();
                using (var cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT id FROM folders WHERE parent_id=$id ORDER BY sort;";
                    cmd.Parameters.AddWithValue("$id", id);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) childFolders.Add(r.GetString(0));
                }
                if (childFolders.Count > 0)
                {
                    using (var mv = _db.CreateCommand())
                    {
                        mv.Transaction = tx;
                        mv.CommandText = "UPDATE folders SET parent_id=$pid WHERE parent_id=$id;";
                        if (parent is null) mv.Parameters.AddWithValue("$pid", DBNull.Value);
                        else mv.Parameters.AddWithValue("$pid", parent);
                        mv.Parameters.AddWithValue("$id", id);
                        mv.ExecuteNonQuery();
                    }
                    for (var i = 0; i < childFolders.Count; i++)
                    {
                        using var re = _db.CreateCommand();
                        re.Transaction = tx;
                        re.CommandText = "UPDATE folders SET sort=$sort WHERE id=$id;";
                        re.Parameters.AddWithValue("$id", childFolders[i]);
                        re.Parameters.AddWithValue("$sort", folderNext + i);
                        re.ExecuteNonQuery();
                    }
                }

                // 组内全部槽位(会话 + 子文件夹,槽位表两物种共用)一并降级到父组
                var childSlots = new List<string>();
                using (var cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT session_id FROM session_slots WHERE folder_id=$id ORDER BY sort;";
                    cmd.Parameters.AddWithValue("$id", id);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) childSlots.Add(r.GetString(0));
                }
                if (childSlots.Count > 0)
                {
                    using (var mv = _db.CreateCommand())
                    {
                        mv.Transaction = tx;
                        mv.CommandText = "UPDATE session_slots SET folder_id=$fid WHERE folder_id=$id;";
                        if (parent is null) mv.Parameters.AddWithValue("$fid", DBNull.Value);
                        else mv.Parameters.AddWithValue("$fid", parent);
                        mv.Parameters.AddWithValue("$id", id);
                        mv.ExecuteNonQuery();
                    }
                    for (var i = 0; i < childSlots.Count; i++)
                    {
                        using var re = _db.CreateCommand();
                        re.Transaction = tx;
                        re.CommandText = "UPDATE session_slots SET sort=$sort WHERE session_id=$id;";
                        re.Parameters.AddWithValue("$id", childSlots[i]);
                        re.Parameters.AddWithValue("$sort", sessionNext + i);
                        re.ExecuteNonQuery();
                    }
                }

                using (var del = _db.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM folders WHERE id=$id;";
                    del.Parameters.AddWithValue("$id", id);
                    del.ExecuteNonQuery();
                }
                using (var delSlot = _db.CreateCommand())
                {
                    delSlot.Transaction = tx;
                    delSlot.CommandText = "DELETE FROM session_slots WHERE session_id=$id;";
                    delSlot.Parameters.AddWithValue("$id", id);
                    delSlot.ExecuteNonQuery();
                }
                tx.Commit();
                return true;
            }
            catch (Exception ex)
            {
                Log.Global.Error("[SessionStore] delete folder failed: " + ex.Message);
            }
        }
        return false;
    }

    /// <summary>指定组的下一个排序号(folders 组 = parent_id / session_slots 组 = folder_id,null = 根组)。</summary>
    private long NextSort(SqliteTransaction tx, string table, string scopeCol, string? scopeKey)
    {
        using var cmd = _db!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COALESCE(MAX(sort), -1) + 1 FROM {table} WHERE {scopeCol} IS $pid;";
        if (scopeKey is null) cmd.Parameters.AddWithValue("$pid", DBNull.Value);
        else cmd.Parameters.AddWithValue("$pid", scopeKey);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ==================== SQLite 存储层(增量写) ====================
    // sessions 表:每会话一行(标量字段);messages 表:每条上下文消息一行(json + 用量列),
    // 被压缩从内存删除的消息仅置 compacted=1 留档,不物理删除;display 表:每条气泡一行。
    // Save = 行级 diff(只写变化行,单事务);Load = 按会话取行重组。

    private static readonly string SchemaSql = """
        CREATE TABLE IF NOT EXISTS sessions (
          id TEXT PRIMARY KEY,
          title TEXT NOT NULL DEFAULT '',
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL,
          archived INTEGER NOT NULL DEFAULT 0,
          compacted_since_last_send INTEGER NOT NULL DEFAULT 0,
          working_dir TEXT NOT NULL DEFAULT '',
          working_dir_access TEXT NOT NULL DEFAULT 'ReadWrite',
          whitelist_json TEXT NOT NULL DEFAULT '[]',
          scene TEXT NOT NULL DEFAULT '',
          chat_provider TEXT NOT NULL DEFAULT '',
          chat_model TEXT NOT NULL DEFAULT '',
          thinking_effort TEXT NOT NULL DEFAULT '',
          usage_prompt INTEGER NOT NULL DEFAULT 0,
          usage_cached INTEGER NOT NULL DEFAULT 0,
          usage_completion INTEGER NOT NULL DEFAULT 0,
          usage_cost REAL NOT NULL DEFAULT 0,
          usage_last_tps REAL NOT NULL DEFAULT 0,
          last_round_provider TEXT NOT NULL DEFAULT '',
          last_round_model TEXT NOT NULL DEFAULT '',
          last_prompt_tokens INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS messages (
          session_id TEXT NOT NULL,
          msg_key TEXT NOT NULL,
          seq INTEGER NOT NULL,
          compacted INTEGER NOT NULL DEFAULT 0,
          json TEXT NOT NULL,
          usage_prompt INTEGER NOT NULL DEFAULT 0,
          usage_cached INTEGER NOT NULL DEFAULT 0,
          usage_completion INTEGER NOT NULL DEFAULT 0,
          usage_reasoning INTEGER NOT NULL DEFAULT 0,
          model TEXT NOT NULL DEFAULT '',
          PRIMARY KEY (session_id, msg_key)
        );
        CREATE INDEX IF NOT EXISTS idx_messages_seq ON messages(session_id, seq);
        CREATE TABLE IF NOT EXISTS display (
          session_id TEXT NOT NULL,
          seq INTEGER NOT NULL,
          json TEXT NOT NULL,
          PRIMARY KEY (session_id, seq)
        );
        -- 列表布局(与 Save/Load 的会话数据完全解耦,不会被会话保存覆盖):
        -- folders = 文件夹树(parent_id NULL = 顶级);session_slots = 组内槽位表,
        -- session_id 列同时容纳会话 id 与文件夹 id —— 同组会话与文件夹共享一个序号空间,可任意交错。
        -- CREATE IF NOT EXISTS 对旧库天然兼容,无需迁移。
        CREATE TABLE IF NOT EXISTS folders (
          id TEXT PRIMARY KEY,
          name TEXT NOT NULL,
          parent_id TEXT,
          sort INTEGER NOT NULL DEFAULT 0,
          color TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_folders_parent ON folders(parent_id);
        CREATE TABLE IF NOT EXISTS session_slots (
          session_id TEXT PRIMARY KEY,
          folder_id TEXT,
          sort INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_slots_folder ON session_slots(folder_id);
        """;

    private SqliteConnection? TryOpenDatabase()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "sessions.db")}");
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }
            using (var schema = conn.CreateCommand())
            {
                schema.CommandText = SchemaSql;
                schema.ExecuteNonQuery();
            }
            // 旧库迁移:folders 表补 color 列(CREATE IF NOT EXISTS 不会改已有表结构;新库已含该列,报重复列名则忽略)
            using (var migrate = conn.CreateCommand())
            {
                migrate.CommandText = "ALTER TABLE folders ADD COLUMN color TEXT NOT NULL DEFAULT '';";
                try { migrate.ExecuteNonQuery(); }
                catch (SqliteException) { /* duplicate column:新库 */ }
            }
            return conn;
        }
        catch (Exception ex)
        {
            Log.Global.Error("[SessionStore] SQLite 打开失败,会话将不落盘: " + ex.Message);
            return null;
        }
    }

    /// <summary>会话标量列(不含消息/气泡内容):LoadOneDb 与 LoadSummaries 共用同一列序与映射。</summary>
    private const string ScalarColumns =
        "title, created_at, updated_at, archived, compacted_since_last_send, " +
        "working_dir, working_dir_access, whitelist_json, scene, chat_provider, " +
        "chat_model, thinking_effort, usage_prompt, usage_cached, usage_completion, " +
        "usage_last_tps, usage_cost, last_round_provider, last_round_model, last_prompt_tokens";

    private static StoredSession ReadScalars(SqliteDataReader r, string id) => new()
    {
        Id = id,
        Title = r.GetString(0),
        CreatedAt = new DateTime(r.GetInt64(1), DateTimeKind.Local),
        UpdatedAt = new DateTime(r.GetInt64(2), DateTimeKind.Local),
        Archived = r.GetInt64(3) != 0,
        CompactedSinceLastSend = r.GetInt64(4) != 0,
        WorkingDirectory = r.GetString(5),
        WorkingDirectoryAccess = r.GetString(6),
        WhitelistedDirectories = ParseWhitelist(r.GetString(7)),
        Scene = r.GetString(8),
        ChatProvider = r.GetString(9),
        ChatModel = r.GetString(10),
        ThinkingEffort = r.GetString(11),
        UsagePromptTotal = (int)r.GetInt64(12),
        UsageCachedTotal = (int)r.GetInt64(13),
        UsageCompletionTotal = (int)r.GetInt64(14),
        UsageLastTps = r.GetDouble(15),
        UsageCost = r.GetDouble(16),
        LastRoundProvider = r.GetString(17),
        LastRoundModel = r.GetString(18),
        LastPromptTokens = r.GetInt64(19),
    };

    /// <summary>whitelist_json 容错解析:异常行回退为空(重新信任目录即可),不拖垮整条标量查询。</summary>
    private static List<StoredDirectoryRule> ParseWhitelist(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<StoredDirectoryRule>>(json, s_rowJson) ?? new();
        }
        catch (Exception ex)
        {
            Log.Global.Error("[SessionStore] whitelist_json 解析失败,按空处理: " + ex.Message);
            return new();
        }
    }

    /// <summary>会话标量行列表(不加载上下文与气泡):归档列表等只看标量列的场景。
    /// 此前经 LoadAll 把全部会话的消息/气泡全量读回,归档多的 Agent 每次打开归档面板都全库扫一遍。</summary>
    public List<StoredSession> LoadSummaries()
    {
        var result = new List<StoredSession>();
        if (_db is null)
            return result;
        try
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                // id 必须放最后:ReadScalars 按 0..19 固定序号读标量列(与 LoadOneDb 的无 id SELECT 共用)。
                // id 放最前会把全部标量后移一位——第 7 位变成 working_dir_access,反序列化必炸,
                // 一行异常让整个归档列表返回空("归档对话全不见")
                cmd.CommandText = $"SELECT {ScalarColumns}, id FROM sessions ORDER BY updated_at";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add(ReadScalars(r, r.GetString(r.FieldCount - 1)));
            }
        }
        catch (Exception ex)
        {
            Log.Global.Error("[SessionStore] db summaries failed: " + ex.Message);
        }
        return result;
    }

    private StoredSession? LoadOneDb(string id)
    {
        StoredSession s;
        using (var cmd = _db!.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ScalarColumns} FROM sessions WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            s = ReadScalars(r, id);
        }
        s.Context = new List<ChatMessage>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT json FROM messages WHERE session_id=$id AND compacted=0 ORDER BY seq;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var msg = JsonSerializer.Deserialize<ChatMessage>(r.GetString(0), s_rowJson);
                if (msg is not null) s.Context.Add(msg);
            }
        }
        s.Display = new List<StoredMessage>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT json FROM display WHERE session_id=$id ORDER BY seq;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var m = JsonSerializer.Deserialize<StoredMessage>(r.GetString(0), s_rowJson);
                if (m is not null) s.Display.Add(m);
            }
        }
        RestoreContextImages(s); // 行内是引用形式,还原 data URL 供框架使用
        return s;
    }

    private void UpsertSessionRow(StoredSession s, SqliteTransaction tx)
    {
        s.UpdatedAt = DateTime.Now;
        using var cmd = _db!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sessions (id, title, created_at, updated_at, archived, compacted_since_last_send,
              working_dir, working_dir_access, whitelist_json, scene, chat_provider, chat_model,
              thinking_effort, usage_prompt, usage_cached, usage_completion, usage_last_tps,
              usage_cost, last_round_provider, last_round_model, last_prompt_tokens)
            VALUES ($id, $title, $created, $updated, $archived, $csls, $wd, $wda, $wl, $scene,
              $cp, $cm, $te, $up, $uc, $uct, $tps, $cost, $lrp, $lrm, $lpt)
            ON CONFLICT(id) DO UPDATE SET
              title=excluded.title, created_at=excluded.created_at, updated_at=excluded.updated_at,
              archived=excluded.archived, compacted_since_last_send=excluded.compacted_since_last_send,
              working_dir=excluded.working_dir, working_dir_access=excluded.working_dir_access,
              whitelist_json=excluded.whitelist_json, scene=excluded.scene,
              chat_provider=excluded.chat_provider, chat_model=excluded.chat_model,
              thinking_effort=excluded.thinking_effort,
              usage_prompt=excluded.usage_prompt, usage_cached=excluded.usage_cached,
              usage_completion=excluded.usage_completion, usage_last_tps=excluded.usage_last_tps,
              usage_cost=excluded.usage_cost,
              last_round_provider=excluded.last_round_provider, last_round_model=excluded.last_round_model,
              last_prompt_tokens=excluded.last_prompt_tokens;
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$title", s.Title ?? "");
        cmd.Parameters.AddWithValue("$created", s.CreatedAt.Ticks);
        cmd.Parameters.AddWithValue("$updated", s.UpdatedAt.Ticks);
        cmd.Parameters.AddWithValue("$archived", s.Archived ? 1L : 0L);
        cmd.Parameters.AddWithValue("$csls", s.CompactedSinceLastSend ? 1L : 0L);
        cmd.Parameters.AddWithValue("$wd", s.WorkingDirectory ?? "");
        cmd.Parameters.AddWithValue("$wda", s.WorkingDirectoryAccess ?? "");
        cmd.Parameters.AddWithValue("$wl", JsonSerializer.Serialize(s.WhitelistedDirectories, s_rowJson));
        cmd.Parameters.AddWithValue("$scene", s.Scene ?? "");
        cmd.Parameters.AddWithValue("$cp", s.ChatProvider ?? "");
        cmd.Parameters.AddWithValue("$cm", s.ChatModel ?? "");
        cmd.Parameters.AddWithValue("$te", s.ThinkingEffort ?? "");
        cmd.Parameters.AddWithValue("$up", (long)s.UsagePromptTotal);
        cmd.Parameters.AddWithValue("$uc", (long)s.UsageCachedTotal);
        cmd.Parameters.AddWithValue("$uct", (long)s.UsageCompletionTotal);
        cmd.Parameters.AddWithValue("$tps", s.UsageLastTps);
        cmd.Parameters.AddWithValue("$cost", s.UsageCost);
        cmd.Parameters.AddWithValue("$lrp", s.LastRoundProvider ?? "");
        cmd.Parameters.AddWithValue("$lrm", s.LastRoundModel ?? "");
        cmd.Parameters.AddWithValue("$lpt", s.LastPromptTokens);
        cmd.ExecuteNonQuery();
    }

    private int _saveCount; // 定期维护计数(compacted 物理清理节流用)

    /// <summary>上下文消息行级 diff:新消息插入、变化行更新、内存已删(压缩等)置 compacted=1 留档。
    /// seq 每次保存按内存顺序重编(1..N),压缩删前、/undo 插中等任何顺序变化都正确;
    /// 未变且 seq 未变的行不产生 I/O。SELECT 限定 compacted=0:留档行无需逐次读回比对。</summary>
    private void SyncMessages(StoredSession s, SqliteTransaction tx)
    {
        ExternalizeContextImages(s); // 深拷贝 + data URL → 文件引用(与 JSON 路径同一行为)
        var existingJson = new Dictionary<string, string>(); // key → 库中 json
        var existingSeq = new Dictionary<string, long>();   // key → 库中 seq
        using (var cmd = _db!.CreateCommand())
        {
            cmd.Transaction = tx;
            // 留档行(compacted=1)不参与比对也不需要重标记,不读:长会话保存成本不再随留档数线性涨
            cmd.CommandText = "SELECT msg_key, json, seq FROM messages WHERE session_id=$id AND compacted=0;";
            cmd.Parameters.AddWithValue("$id", s.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                existingJson[r.GetString(0)] = r.GetString(1);
                existingSeq[r.GetString(0)] = r.GetInt64(2);
            }
        }
        var inMemory = new HashSet<string>();
        var updateSql = """
            UPDATE messages SET seq=$seq, compacted=0, json=$json,
              usage_prompt=$up, usage_cached=$uc, usage_completion=$uct, usage_reasoning=$ur, model=$model
            WHERE session_id=$sid AND msg_key=$key;
            """;
        var insertSql = """
            INSERT INTO messages (session_id, msg_key, seq, compacted, json,
              usage_prompt, usage_cached, usage_completion, usage_reasoning, model)
            VALUES ($sid, $key, $seq, 0, $json, $up, $uc, $uct, $ur, $model);
            """;
        for (var i = 0; i < s.Context.Count; i++)
        {
            var msg = s.Context[i];
            var key = StableKey(msg);
            inMemory.Add(key);
            var json = JsonSerializer.Serialize(msg, s_rowJson);
            long seq = i + 1;
            if (existingJson.TryGetValue(key, out var oldJson) && existingSeq.TryGetValue(key, out var oldSeq))
            {
                if (oldJson == json && oldSeq == seq) continue; // 无变化:不产生 I/O
                using var up = _db.CreateCommand();
                up.Transaction = tx;
                up.CommandText = updateSql;
                FillMessageParams(up, s.Id, key, seq, json, msg.Usage, msg);
                up.ExecuteNonQuery();
            }
            else
            {
                using var ins = _db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = insertSql;
                FillMessageParams(ins, s.Id, key, seq, json, msg.Usage, msg);
                ins.ExecuteNonQuery();
            }
        }
        // 内存已不存在的行(压缩/回退删除)→ 留档不删,原始用量保留
        var markSql = "UPDATE messages SET compacted=1 WHERE session_id=$sid AND msg_key=$key AND compacted=0;";
        foreach (var key in existingJson.Keys.Where(k => !inMemory.Contains(k)))
        {
            using var mk = _db.CreateCommand();
            mk.Transaction = tx;
            mk.CommandText = markSql;
            mk.Parameters.AddWithValue("$sid", s.Id);
            mk.Parameters.AddWithValue("$key", key);
            mk.ExecuteNonQuery();
        }

        // 定期物理清理留档行:只标记不删会让库随压缩次数无限膨胀。每 20 次保存跑一次,
        // 每会话保留最近 1000 条留档(可取回的压缩历史足够回溯,更老的物理删除)
        if (_db is not null && ++_saveCount % 20 == 0)
        {
            using var prune = _db.CreateCommand();
            prune.Transaction = tx;
            prune.CommandText = """
                DELETE FROM messages WHERE session_id=$id AND compacted=1 AND msg_key NOT IN (
                    SELECT msg_key FROM messages WHERE session_id=$id AND compacted=1
                    ORDER BY seq DESC LIMIT 1000);
                """;
            prune.Parameters.AddWithValue("$id", s.Id);
            prune.ExecuteNonQuery();
        }
    }

    private static void FillMessageParams(SqliteCommand cmd, string sid, string key, long seq, string json,
        MessageUsage? u, ChatMessage msg)
    {
        cmd.Parameters.AddWithValue("$sid", sid);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$up", (long)(u?.PromptTokens ?? 0));
        cmd.Parameters.AddWithValue("$uc", (long)(u?.CachedTokens ?? 0));
        cmd.Parameters.AddWithValue("$uct", (long)(u?.CompletionTokens ?? 0));
        cmd.Parameters.AddWithValue("$ur", (long)(u?.ReasoningTokens ?? 0));
        cmd.Parameters.AddWithValue("$model", msg.Model ?? "");
    }

    /// <summary>气泡行 diff:按 seq 对齐,内容未变的行不写;超出当前数量的旧行删除。</summary>
    private void SyncDisplay(StoredSession s, SqliteTransaction tx)
    {
        var existing = new Dictionary<long, string>(); // seq → 库中 json
        using (var cmd = _db!.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT seq, json FROM display WHERE session_id=$sid;";
            cmd.Parameters.AddWithValue("$sid", s.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing[r.GetInt64(0)] = r.GetString(1);
        }
        var upsertSql = "INSERT OR REPLACE INTO display (session_id, seq, json) VALUES ($sid, $seq, $json);";
        for (var i = 0; i < s.Display.Count; i++)
        {
            var json = JsonSerializer.Serialize(s.Display[i], s_rowJson);
            if (existing.TryGetValue(i, out var old) && old == json) continue; // 无变化:不产生 I/O
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = upsertSql;
            cmd.Parameters.AddWithValue("$sid", s.Id);
            cmd.Parameters.AddWithValue("$seq", (long)i);
            cmd.Parameters.AddWithValue("$json", json);
            cmd.ExecuteNonQuery();
        }
        using (var del = _db.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM display WHERE session_id=$sid AND seq >= $max;";
            del.Parameters.AddWithValue("$sid", s.Id);
            del.Parameters.AddWithValue("$max", (long)s.Display.Count);
            del.ExecuteNonQuery();
        }
    }

    /// <summary>消息稳定键:优先 Id;Id 为空时按全量内容哈希(多次保存键不变)。</summary>
    private static string StableKey(ChatMessage msg)
    {
        if (!string.IsNullOrEmpty(msg.Id)) return msg.Id;
        var raw = msg.Role.ToString() + "\u0001" + (msg.Content ?? "") + "\u0001" + (msg.ReasoningContent ?? "") + "\u0001" +
                  (msg.ToolCallId ?? "") + "\u0001" +
                  string.Join("\u0002", (msg.ToolCalls ?? new List<ToolCall>()).Select(t =>
                      (t.Id ?? "") + "\u0003" + (t.Name ?? "") + "\u0003" + (t.ArgumentsJson ?? "")));
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return "h:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>删除某 Agent 的整个文件夹(agents/{Id}:设定 + 会话记录;Agent 被删除时调用)。</summary>
    public static void DeleteAgentSessions(string agentId)
    {
        try
        {
            var dir = Path.Combine(AppPaths.AgentsRoot, SafeDir(agentId));
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Global.Error("[SessionStore] delete agent sessions failed: " + ex.Message);
        }
    }
}
