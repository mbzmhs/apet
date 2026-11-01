using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using APet.UI.ViewModels;

namespace APet.UI.Services;

/// <summary>
/// 把工具调用的参数/结果解析成可读的摘要与展开详情
/// (编辑类显示带行号的文件内容,终端类显示命令与输出),避免在界面上出现原始 JSON。
/// </summary>
internal static partial class ToolCallPresenter
{
    [GeneratedRegex(@"\r?\n")]
    private static partial Regex NewLineRegex();

    [GeneratedRegex(@"run #(\d+)")]
    private static partial Regex RunIdRegex();

    /// <summary>子代理结果文本的任务名称标记 (run #N, task: 名称):工具行据此显示任务名替代"运行 #N"。</summary>
    [GeneratedRegex(@"task:\s*([^\n,)]+)")]
    private static partial Regex TaskTitleRegex();

    private const int MaxDetailLines = 400;
    private const int MaxSummaryLength = 90;

    /// <summary>
    /// sub_wait/sub_kill 行启动时回填任务名称:运行此时已存在(sub_run 先返回),
    /// 按 id 查到标题即可在运行期间显示,不用等结果文本(否则运行中只能显示"运行 #N")。
    /// </summary>
    public static void AttachSubRunTitle(ToolCallViewModel row, Func<int, string?>? titleResolver)
    {
        if (row.SubRunId is not { } rid || row.SubRunTitle.Length > 0)
            return;
        if (titleResolver?.Invoke(rid) is { Length: > 0 } title)
        {
            row.SubRunTitle = title;
            if (row.ToolName != "sub_run")
                row.Summary = title; // 替换"运行 #N"式模糊摘要
        }
    }

    /// <summary>创建工具调用行:解析参数生成摘要与初始详情。workdir 为会话工作目录,用于解析相对路径定位行号。</summary>
    public static ToolCallViewModel Create(string displayName, string toolName, string argumentsJson, string? workdir = null)
    {
        var row = new ToolCallViewModel(displayName, toolName);
        using var args = TryParse(argumentsJson);
        if (args is null)
        {
            row.Summary = SingleLine(argumentsJson);
            return row;
        }

        // MCP 代理工具:标题已是「MCP调用·{服务器}」;灰字取远端工具名——
        // 协议名 = mcp_ + Sanitize(服务器) + _ + Sanitize(工具),Sanitize 逐字符替换长度不变,
        // 故按 服务器名长度+1(分隔下划线)截断即得远端工具名
        if (toolName.StartsWith("mcp_", StringComparison.Ordinal))
        {
            const string mcpTitlePrefix = "MCP调用·";
            var rest = toolName["mcp_".Length..];
            var tool = rest;
            if (displayName.StartsWith(mcpTitlePrefix, StringComparison.Ordinal))
            {
                var server = displayName[mcpTitlePrefix.Length..];
                if (server.Length > 0 && rest.Length > server.Length + 1
                    && rest.StartsWith(server, StringComparison.Ordinal))
                    tool = rest[(server.Length + 1)..];
            }
            row.Summary = SingleLine(tool.Length > 0 ? tool : rest);
            row.DetailBody = PrettyJson(argumentsJson);
            return row;
        }

        switch (toolName)
        {
            case "write_file":
            {
                var path = GetString(args.RootElement, "path") ?? "";
                var content = GetString(args.RootElement, "content") ?? "";
                var append = args.RootElement.TryGetProperty("append", out var a) && a.ValueKind == JsonValueKind.True;
                var lines = content.Length == 0 ? 0 : NewLineRegex().Count(content) + 1;
                // 行数不再放前段灰文案,统一走末尾绿色统计列(与编辑文件一致);追加模式保留灰色"追加"说明
                SetPathLink(row, path, workdir, append ? "追加" : "");
                if (lines > 0)
                    row.StatAdd = $"+{lines}";
                row.DetailBody = FileDetail(path, content);
                row.SetDiff(BuildAddedDiff(content)); // 写入内容以全绿 diff 展示,行号 1..N 来自参数本身(无竞态)
                break;
            }
            case "read_file":
            {
                SetPathLink(row, GetString(args.RootElement, "path") ?? "", workdir);
                break;
            }
            case "view_image":
            {
                // 与截图同款:文件名做链接,图片本体由宿主挂到行上展开查看
                SetPathLink(row, GetString(args.RootElement, "path") ?? "", workdir);
                break;
            }
            case "bash":
            {
                var command = GetString(args.RootElement, "command") ?? "";
                row.Summary = SingleLine(command);
                row.DetailBody = "$ " + command;
                break;
            }
            case "edit_file":
            {
                var path = GetString(args.RootElement, "path") ?? "";
                var oldString = GetString(args.RootElement, "old_string") ?? "";
                var newString = GetString(args.RootElement, "new_string") ?? "";
                SetPathLink(row, path, workdir);
                // 行号:尽力在目标文件中定位 old_string 首行(读不到则留空)
                var baseLine = TryFindLine(path, oldString, workdir);
                row.SetDiff(BuildReplaceDiff(oldString, newString, baseLine));
                // 行创建跑在 UI 线程、晚于工具执行(竞态):预定位常读到改后内容而失败,
                // 记录 diff 输入,等结果阶段按 new_string 回填行号
                row.EditDiff = new EditDiffInfo(path, oldString, newString, workdir);
                row.DetailBody = "- " + PrefixLines(oldString, "- ")
                    + "\n+ " + PrefixLines(newString, "+ ");
                break;
            }
            case "apply_patch":
            {
                var path = GetString(args.RootElement, "path") ?? "";
                var patch = GetString(args.RootElement, "patch") ?? "";
                SetPathLink(row, path, workdir);
                row.SetDiff(BuildPatchDiff(patch));
                // 行数统计:遍历补丁行统计 +/-(+ +++/--- 头除外)
                var added = 0;
                var deleted = 0;
                foreach (var raw in SplitLines(patch))
                {
                    if (raw.Length == 0 || raw.StartsWith('\\') || raw.StartsWith("@@", StringComparison.Ordinal))
                        continue;
                    if (raw.StartsWith("+++", StringComparison.Ordinal) || raw.StartsWith("---", StringComparison.Ordinal))
                        continue;
                    if (raw.StartsWith('+')) added++;
                    else if (raw.StartsWith('-')) deleted++;
                }
                row.StatAdd = added > 0 ? $"+{added}" : "";
                row.StatDel = deleted > 0 ? $"-{deleted}" : "";
                row.DetailBody = TruncateLines(patch.Replace("\r", ""), MaxDetailLines);
                break;
            }
            case "web_fetch":
            {
                var url = GetString(args.RootElement, "url") ?? "";
                row.Summary = SingleLine(url);
                row.DetailBody = url;
                break;
            }
            case "screenshot":
            {
                // 截图参数只有可选的屏幕序号:摘要报"第 N 块屏幕/主屏幕",不输出原始 JSON(避免界面出现 {})
                var hasId = args.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number;
                row.Summary = hasId ? $"第 {idEl.GetInt32() + 1} 块屏幕" : "主屏幕";
                break;
            }
            case "skill":
            {
                // 技能加载:参数只有技能名,摘要报"加载技能 xxx",不输出原始 JSON
                var skillName = GetString(args.RootElement, "name") ?? "";
                row.Summary = skillName.Length > 0 ? "加载技能 " + SingleLine(skillName) : "加载技能";
                break;
            }
            // ───────── 插件类工具(纯文本参数,不输出原始 JSON) ─────────
            case "bg_run":
            {
                // 模型按 schema 必给 title(未给时结果阶段兜底名补上);给了作灰字,命令折进详情
                var command = GetString(args.RootElement, "command") ?? "";
                var title = (GetString(args.RootElement, "title") ?? "").Trim();
                row.Summary = title.Length > 0 ? SingleLine(title) : SingleLine(command);
                row.DetailBody = "$ " + command;
                break;
            }
            case "bg_output":
                row.Summary = $"查看任务 #{GetInt64(args.RootElement, "id")}";
                break;
            case "bg_kill":
                row.Summary = $"终止任务 #{GetInt64(args.RootElement, "id")}";
                break;
            case "bg_list":
            case "todo_read":
            case "mute":
            case "bili_live_events":
                break; // 无参:仅显示工具名
            case "mem_record":
            {
                var content = GetString(args.RootElement, "content") ?? "";
                row.Summary = SingleLine(content);
                row.DetailBody = content;
                break;
            }
            case "mem_recent":
                row.Summary = $"最近 {(args.RootElement.TryGetProperty("days", out var days) && days.ValueKind == JsonValueKind.Number ? days.GetInt64() : 0)} 天";
                break;
            case "mem_range":
                row.Summary = $"{SingleLine(GetString(args.RootElement, "from") ?? "")} ~ {SingleLine(GetString(args.RootElement, "to") ?? "")}";
                break;
            case "mem_search":
                row.Summary = DescribeKeywords(args.RootElement);
                break;
            case "mem_use":
                row.Summary = $"使用记忆 #{GetInt64(args.RootElement, "id")}";
                break;
            case "mem_edit":
                row.Summary = $"修改记忆 #{GetInt64(args.RootElement, "id")}";
                break;
            case "todo_write":
            {
                var count = args.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                    ? items.GetArrayLength()
                    : 0;
                row.Summary = $"{count} 项";
                break;
            }
            // ───────── 子代理类工具(sub_*:行可点击打开子代理模态框) ─────────
            case "sub_run":
            {
                // 运行在启动后才存在:参数阶段未知,结果阶段从返回文本提取 run id
                var name = GetString(args.RootElement, "name") ?? "";
                var title = (GetString(args.RootElement, "title") ?? "").Trim();
                var prompt = GetString(args.RootElement, "prompt") ?? "";
                if (title.Length > 0)
                {
                    // 任务名称作灰字(模型按 schema 必填;未给时取 prompt 首行兜底)
                    row.SubRunTitle = title;
                    row.Summary = name.Length > 0 ? $"{title} · {name}" : title;
                }
                else
                {
                    row.Summary = name.Length > 0
                        ? name + (prompt.Length > 0 ? " · " + SingleLine(prompt) : "")
                        : SingleLine(prompt);
                }
                row.DetailBody = prompt;
                break;
            }
            case "sub_wait":
                row.SubRunId = (int)GetInt64(args.RootElement, "id");
                row.Summary = $"运行 #{row.SubRunId}";
                break;
            case "sub_kill":
                row.SubRunId = (int)GetInt64(args.RootElement, "id");
                row.Summary = $"终止 #{row.SubRunId}";
                break;
            case "ask":
            {
                // 询问行:摘要=等待作答,详情=逐题问题与选项(运行中即可展开);完成后 ApplyResult 回显所选
                var lines = new List<string>();
                try
                {
                    using var doc = JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
                        foreach (var q in qs.EnumerateArray())
                        {
                            lines.Add("- " + (GetString(q, "title") ?? "?") + ": " + (GetString(q, "question") ?? ""));
                            if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                                foreach (var o in opts.EnumerateArray())
                                {
                                    var label = GetString(o, "label") ?? "";
                                    if (label.Length == 0) continue;
                                    lines.Add("  · " + label + (o.TryGetProperty("recommended", out var r) && r.ValueKind == JsonValueKind.True ? "(推荐)" : ""));
                                }
                        }
                }
                catch (JsonException) { }
                row.Summary = "等待用户作答…";
                row.DetailBody = string.Join("\n", lines); // HasDetail 由 DetailBody 派生(只读)
                break;
            }
            case "sub_list":
                break; // 无参:仅显示工具名
            default:
                // 未适配的工具:摘要留空(仅显示工具名),原始参数折叠进详情备查
                row.DetailBody = PrettyJson(argumentsJson);
                break;
        }
        return row;
    }

    /// <summary>工具完成后:解析结果更新结果摘要与展开详情。</summary>
    public static void ApplyResult(ToolCallViewModel row, string resultJson)
    {
        row.IsRunning = false;
        if (row.ToolName.StartsWith("sub_", StringComparison.Ordinal))
        {
            // sub_* 工具返回纯文本:sub_run 成功时提取 run id 让行可点击;sub_wait/kill 按文本标注成败
            var text = (resultJson ?? "").Trim();
            if (text.Length == 0)
            {
                row.ResultSummary = "完成";
                return;
            }
            var firstLine = NewLineRegex().Split(text)[0];
            var match = RunIdRegex().Match(text);
            if (match.Success)
                row.SubRunId = int.Parse(match.Groups[1].Value);
            // 任务名称:sub_run 未显式给 title 时结果里的兜底名在此补上(点击入口据此显示);
            // sub_wait/sub_kill 拿到名称后替换"运行 #N"式模糊摘要
            var titleMatch = TaskTitleRegex().Match(text);
            if (titleMatch.Success)
            {
                row.SubRunTitle = titleMatch.Groups[1].Value.Trim();
                if (row.ToolName != "sub_run")
                    row.Summary = row.SubRunTitle;
            }
            if (row.ToolName == "sub_run")
            {
                if (text.Contains("started", StringComparison.Ordinal))
                    row.ResultSummary = "已启动";
                else
                {
                    row.IsError = true;
                    row.ResultSummary = SingleLine(firstLine);
                }
            }
            else if (row.ToolName == "sub_wait"
                && text.Contains("interrupted", StringComparison.Ordinal))
            {
                // 主轮停止导致的等待中断:非失败,灰字提示已中断(不标红)
                row.ResultSummary = "已中断";
            }
            else if (row.ToolName == "sub_wait"
                && (text.Contains("failed", StringComparison.Ordinal)
                    || text.Contains("cancelled", StringComparison.Ordinal)
                    || text.Contains("not found", StringComparison.Ordinal)))
            {
                row.IsError = true;
                row.ResultSummary = SingleLine(firstLine);
            }
            else
            {
                row.ResultSummary = "完成";
            }
            row.DetailBody = (row.DetailBody + "\n" + TruncateLines(text.Replace("\r", ""), MaxDetailLines)).Trim();
            return;
        }
        if (row.ToolName == "screenshot")
        {
            // 截图结果正文是英文说明句,界面只报"已截图";图片由宿主另行挂到行上展开查看
            row.ResultSummary = "已截图";
            return;
        }
        if (row.ToolName == "view_image")
        {
            // 查看图片:成功只报"已读图";失败(文件不存在/格式不支持/超限)结果正文是错误句
            var imgText = (resultJson ?? "").Trim();
            if (imgText.StartsWith("Image loaded", StringComparison.Ordinal))
            {
                row.ResultSummary = "已查看";
            }
            else if (imgText.Length > 0)
            {
                row.IsError = true;
                row.ResultSummary = SingleLine(imgText);
            }
            return;
        }
        if (row.ToolName == "skill")
        {
            // 技能结果:成功是技能正文(Markdown),失败是"技能不存在或已停用:…"提示句;均按纯文本处理
            var text = (resultJson ?? "").Trim();
            if (text.StartsWith("技能不存在", StringComparison.Ordinal))
            {
                row.IsError = true;
                row.ResultSummary = SingleLine(text);
                row.DetailBody = (row.DetailBody + "\n✗ " + text).Trim();
                return;
            }
            row.ResultSummary = text.Length == 0 ? "完成" : "已加载";
            row.DetailBody = (row.DetailBody + "\n" + TruncateLines(text.Replace("\r", ""), MaxDetailLines)).Trim();
            return;
        }
        if (row.ToolName == "ask")
        {
            // 询问结果:逐题回显用户所选(选项 label / 其他自填文本),明细入详情体;
            // 单题时行尾直接显示答案,多题报题数。跳过单独标注(不标红——跳过是正常交互)
            var askText = (resultJson ?? "").Trim();
            try
            {
                using var doc = JsonDocument.Parse(askText);
                var askRoot = doc.RootElement;
                if (askRoot.TryGetProperty("skipped", out var sk) && sk.ValueKind == JsonValueKind.True)
                {
                    row.Summary = "已跳过"; // 完成态摘要替换 Create 时的「等待用户作答…」
                    row.ResultSummary = "用户已跳过";
                    return;
                }
                if (askRoot.TryGetProperty("answers", out var ans) && ans.ValueKind == JsonValueKind.Array)
                {
                    var lines = new List<string>();
                    foreach (var a in ans.EnumerateArray())
                    {
                        var title = GetString(a, "title") ?? "?";
                        var parts = new List<string>();
                        if (a.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.Array)
                            foreach (var s in sel.EnumerateArray())
                                if (s.GetString() is { Length: > 0 } label)
                                    parts.Add(label);
                        if (GetString(a, "other") is { Length: > 0 } other)
                            parts.Add("其他: " + other);
                        lines.Add(title + ": " + (parts.Count > 0 ? string.Join("、", parts) : "(未选)"));
                    }
                    row.Summary = $"{ans.GetArrayLength()} 个问题";
                    row.DetailBody = string.Join("\n", lines);
                    row.ResultSummary = lines.Count == 1 ? SingleLine(lines[0]) : "已作答";
                }
            }
            catch (JsonException) { }
            return;
        }
        using var result = TryParse(resultJson);
        // 非对象结果(JSON 数组/标量等,如 ask 的答案列表)与纯文本同路处理:
        // 下方各分支按对象解析,直接喂数组会让 TryGetProperty 抛 InvalidOperationException
        if (result is null || result.RootElement.ValueKind != JsonValueKind.Object)
        {
            // 插件类工具返回纯文本(mem_*/todo_*/bg_* 等):摘要取首行,全文进详情
            var text = (resultJson ?? "").Trim();
            if (text.Length == 0)
            {
                row.ResultSummary = "完成";
                return;
            }
            var firstLine = NewLineRegex().Split(text)[0];
            row.ResultSummary = "完成 · " + SingleLine(firstLine);
            row.DetailBody = (row.DetailBody + "\n" + TruncateLines(text.Replace("\r", ""), MaxDetailLines)).Trim();
            return;
        }
        var root = result.RootElement;

        switch (row.ToolName)
        {
            case "write_file":
            {
                if (TryGetError(root, out var error))
                {
                    row.IsError = true;
                    row.ResultSummary = "写入失败";
                    row.DetailBody += "\n✗ " + error;
                    return;
                }
                var bytes = GetInt64(root, "bytes_written");
                var appended = root.TryGetProperty("appended", out var a) && a.ValueKind == JsonValueKind.True;
                row.ResultSummary = appended ? $"已追加{(bytes > 0 ? $" {bytes} 字节" : "")}" : $"已写入{(bytes > 0 ? $" {bytes} 字节" : "")}";
                break;
            }
            case "read_file":
            {
                if (TryGetError(root, out var error))
                {
                    row.IsError = true;
                    row.ResultSummary = "读取失败";
                    row.DetailBody += "\n✗ " + error;
                    return;
                }
                // 内容不回显到对话框,只报行数(详情无可展开内容)
                row.ResultSummary = $"{NewLineRegex().Count(GetString(root, "content") ?? "") + 1} 行";
                break;
            }
            case "edit_file":
            {
                if (TryGetError(root, out var error))
                {
                    row.IsError = true;
                    row.ResultSummary = "编辑失败";
                    row.DetailBody += "\n✗ " + error;
                    return;
                }
                var replaced = GetInt64(root, "replaced");
                row.ResultSummary = $"已替换 {replaced} 处";
                // 行数统计:+新增 -删除 = 替换处数 × 单次新旧行数
                if (row.EditDiff is { } edit)
                {
                    var added = replaced * SplitLines(edit.NewString).Length;
                    var deleted = replaced * SplitLines(edit.OldString).Length;
                    row.StatAdd = added > 0 ? $"+{added}" : "";
                    row.StatDel = deleted > 0 ? $"-{deleted}" : "";
                    // 结果阶段重建多段 hunk diff(全部替换点 + 周围上下文);定位退化时退回旧行号回填
                    if (BuildReplaceHunkDiff(edit.Path, edit.OldString, edit.NewString, replaced, edit.Workdir)
                        is { } hunks)
                    {
                        row.SetDiff(hunks);
                        break;
                    }
                }
                FillEditDiffLineNos(row);
                break;
            }
            case "apply_patch":
            {
                if (TryGetError(root, out var error))
                {
                    row.IsError = true;
                    row.ResultSummary = "补丁失败";
                    row.DetailBody += "\n✗ " + error;
                    return;
                }
                row.ResultSummary = $"已应用 {GetInt64(root, "hunks")} 个补丁块";
                break;
            }
            case "bash":
            {
                // 被拒绝/被策略拦截的调用:结果是无 exit_code 的错误 JSON,不检查会把缺省 0 当成功显示"完成"
                if (TryGetError(root, out var blocked))
                {
                    row.IsError = true;
                    var deniedByUser = blocked.Contains("denied by user", StringComparison.OrdinalIgnoreCase);
                    row.ResultSummary = deniedByUser ? "已拒绝" : "失败: " + SingleLine(blocked);
                    row.DetailBody = (row.DetailBody + "\n✗ " + blocked).Trim();
                    break;
                }
                var exitCode = GetInt64(root, "exit_code");
                var timedOut = root.TryGetProperty("timed_out", out var t) && t.ValueKind == JsonValueKind.True;
                // 输出未完全排空(命令留下了持有输出管道的后台子进程):摘要如实标注,避免"完成"误导
                var incomplete = root.TryGetProperty("output_incomplete", out var oi) && oi.ValueKind == JsonValueKind.True;
                var stdout = GetString(root, "stdout") ?? "";
                var stderr = GetString(root, "stderr") ?? "";
                row.ResultSummary = timedOut ? "超时"
                    : exitCode != 0 ? $"退出码 {exitCode}"
                    : incomplete ? "完成 · 输出未读完"
                    : "完成";
                // 详情显示完整命令:结果 JSON 带原文 command,Summary 是截断版不能复用
                var fullCmd = GetString(root, "command");
                row.DetailBody = BuildTerminalDetail(fullCmd.Length > 0 ? fullCmd : row.Summary, stdout, stderr, timedOut, exitCode);
                if (timedOut || exitCode != 0)
                    row.IsError = true; // 命令失败:结果摘要红色醒目
                break;
            }
            case "web_fetch":
            {
                if (root.TryGetProperty("error", out var fetchError))
                {
                    row.IsError = true;
                    row.ResultSummary = fetchError.GetString() switch
                    {
                        "http_error" => $"HTTP {GetInt64(root, "status")}",
                        "timeout" => "抓取超时",
                        _ => "抓取失败",
                    };
                    row.DetailBody = (row.DetailBody + "\n✗ " + SingleLine(resultJson)).Trim();
                    return;
                }
                var content = GetString(root, "content") ?? "";
                var truncated = root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
                // 正文不回显到对话框(太长且无阅读价值),只报摘要
                row.ResultSummary = truncated ? $"已截断 · {content.Length} 字符" : $"完成 · {content.Length} 字符";
                break;
            }
            default:
            {
                // 未适配的工具:错误结果标红,其余只报完成、原文折叠进详情
                if (TryGetError(root, out var error))
                {
                    row.IsError = true;
                    row.ResultSummary = "失败: " + SingleLine(error);
                    row.DetailBody = (row.DetailBody + "\n✗ " + PrettyJson(resultJson)).Trim();
                    break;
                }
                row.ResultSummary = "完成";
                row.DetailBody = (row.DetailBody + "\n→ " + PrettyJson(resultJson)).Trim();
                break;
            }
        }
    }

    /// <summary>鉴权卡片用的参数描述(可读,不输出原始 JSON)。</summary>
    public static string DescribeArguments(string toolName, string argumentsJson)
    {
        using var args = TryParse(argumentsJson);
        if (args is null)
            return SingleLine(argumentsJson);
        if (toolName.StartsWith("mcp_", StringComparison.Ordinal))
            return "调用 MCP 工具 " + SingleLine(toolName["mcp_".Length..]);
        switch (toolName)
        {
            case "write_file":
                return "写入 " + DescribePath(GetString(args.RootElement, "path") ?? "");
            case "read_file":
                return "读取 " + DescribePath(GetString(args.RootElement, "path") ?? "");
            case "bash":
                return "运行命令: " + SingleLine(GetString(args.RootElement, "command") ?? "");
            case "edit_file":
                return "编辑 " + DescribePath(GetString(args.RootElement, "path") ?? "");
            case "apply_patch":
                return "对 " + DescribePath(GetString(args.RootElement, "path") ?? "") + " 应用补丁";
            case "web_fetch":
                return "抓取网页: " + SingleLine(GetString(args.RootElement, "url") ?? "");
            case "skill":
            {
                var skillName = GetString(args.RootElement, "name") ?? "";
                return skillName.Length > 0 ? "加载技能: " + SingleLine(skillName) : "加载技能";
            }
            // ───────── 插件类工具(纯文本参数,不输出原始 JSON) ─────────
            case "bg_run":
                return "后台运行: " + SingleLine(GetString(args.RootElement, "command") ?? "");
            case "bg_output":
                return $"查看后台任务 #{GetInt64(args.RootElement, "id")}";
            case "bg_kill":
                return $"终止后台任务 #{GetInt64(args.RootElement, "id")}";
            case "bg_list":
            case "todo_read":
            case "mute":
            case "bili_live_events":
                return "";
            case "mem_record":
                return SingleLine(GetString(args.RootElement, "content") ?? "");
            case "mem_recent":
                return $"查看最近 {(args.RootElement.TryGetProperty("days", out var days) && days.ValueKind == JsonValueKind.Number ? days.GetInt64() : 0)} 天的记忆";
            case "mem_range":
                return $"查看 {SingleLine(GetString(args.RootElement, "from") ?? "")} ~ {SingleLine(GetString(args.RootElement, "to") ?? "")} 的记忆";
            case "mem_search":
                return "搜索记忆关键词: " + DescribeKeywords(args.RootElement);
            case "mem_use":
                return $"使用记忆 #{GetInt64(args.RootElement, "id")}";
            case "mem_edit":
                return $"修改记忆 #{GetInt64(args.RootElement, "id")}";
            case "todo_write":
            {
                var count = args.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                    ? items.GetArrayLength()
                    : 0;
                return $"更新任务清单({count} 项)";
            }
            // ───────── 子代理类工具 ─────────
            case "sub_run":
            {
                var name = GetString(args.RootElement, "name") ?? "";
                return name.Length > 0 ? "委托子代理 " + SingleLine(name) : "委托子代理";
            }
            case "sub_wait":
                return $"等待子代理运行 #{GetInt64(args.RootElement, "id")}";
            case "sub_kill":
                return $"终止子代理运行 #{GetInt64(args.RootElement, "id")}";
            case "sub_list":
                return "";
            default:
                return SingleLine(argumentsJson);
        }
    }

    // ───────── 详情构造 ─────────

    /// <summary>文件详情:路径 + 带行号内容(超长截断)。</summary>
    private static string FileDetail(string path, string content)
    {
        var sb = new System.Text.StringBuilder();
        if (path.Length > 0)
            sb.AppendLine(path);
        var lines = NewLineRegex().Split(content);
        var shown = 0;
        foreach (var line in lines)
        {
            if (shown >= MaxDetailLines)
            {
                sb.Append("… 共 ").Append(lines.Length).AppendLine(" 行,已截断");
                break;
            }
            sb.Append((shown + 1).ToString().PadLeft(4)).Append("  ").AppendLine(line);
            shown++;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>多行文本每行加前缀(编辑工具的新旧内容对照)。</summary>
    private static string PrefixLines(string text, string prefix)
    {
        var lines = NewLineRegex().Split(text.Replace("\r", ""));
        var limited = lines.Length > MaxDetailLines;
        var shown = limited ? lines.Take(MaxDetailLines) : lines;
        var body = string.Join("\n" + prefix, shown);
        return limited ? body + $"\n… 共 {lines.Length} 行,已截断" : body;
    }

    // ───────── 彩色 diff 行 ─────────

    /// <summary>
    /// edit_file 的替换 diff:旧行(红,-)在前,新行(绿,+)在后,
    /// 行号从目标文件中 old_string 首次出现的位置推算(定位失败则留空)。
    /// </summary>
    private static IReadOnlyList<DiffLineViewModel> BuildReplaceDiff(string oldString, string newString, int? baseLine)
    {
        var lines = new List<DiffLineViewModel>();
        var oldNo = baseLine;
        foreach (var line in SplitLines(oldString))
        {
            lines.Add(new DiffLineViewModel(oldNo?.ToString() ?? "", line, "del"));
            if (oldNo.HasValue)
                oldNo++;
        }
        var newNo = baseLine;
        foreach (var line in SplitLines(newString))
        {
            lines.Add(new DiffLineViewModel(newNo?.ToString() ?? "", line, "add"));
            if (newNo.HasValue)
                newNo++;
        }
        return lines;
    }

    /// <summary>write_file 的全绿 diff:写入内容逐行为新增行,行号 1..N 直接由参数内容决定;超长截断留省略行。</summary>
    private static IReadOnlyList<DiffLineViewModel> BuildAddedDiff(string content)
    {
        var raw = SplitLines(content);
        var lines = new List<DiffLineViewModel>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (i >= MaxDetailLines)
            {
                lines.Add(new DiffLineViewModel("", $"… 共 {raw.Length} 行,后续省略", "ctx"));
                break;
            }
            lines.Add(new DiffLineViewModel((i + 1).ToString(), raw[i], "add"));
        }
        return lines;
    }

    /// <summary>edit_file 每处替换前后带的上下文行数(标准 diff 惯例)。</summary>
    private const int DiffContextLines = 3;

    /// <summary>
    /// edit_file 结果阶段重建的多段 hunk diff:改后文件里顺序定位每处 new_string,
    /// 每处展开为 前置上下文 + 旧行(红)+ 新行(绿)+ 后置上下文(各 3 行),多处替换全部呈现;
    /// 跳过的区间以「···」省略行标记。行号:上下文/新增行用新文件行号,删除行用旧文件行号
    /// (旧行号 = 新行号 − 此前 hunk 累计行数差)。
    /// 定位失败、锚点数与 replaced 不符(new_string 在文件中天然出现/嵌套自含,位置不可信)时返回 null,
    /// 调用方保留参数期 diff 并退回旧行号回填逻辑。
    /// </summary>
    private static IReadOnlyList<DiffLineViewModel>? BuildReplaceHunkDiff(
        string path, string oldString, string newString, long replaced, string? workdir)
    {
        try
        {
            if (path.Length == 0 || oldString.Length == 0 || newString.Length == 0 || replaced <= 0)
                return null;
            var content = TryReadAllText(path)
                ?? (workdir is { Length: > 0 } ? TryReadAllText(Path.Combine(workdir, path)) : null);
            if (content is null)
                return null;

            // 顺序定位每处替换点(与工具的顺序替换语义一致);锚点数必须与 replaced 精确一致,
            // 否则说明 new_string 在文件中天然出现/嵌套自含,位置不可信
            var sites = new List<int>();
            for (var index = content.IndexOf(newString, StringComparison.Ordinal);
                 index >= 0;
                 index = content.IndexOf(newString, index + newString.Length, StringComparison.Ordinal))
            {
                if (sites.Count == replaced)
                    return null; // 还有多余锚点
                sites.Add(index);
            }
            if (sites.Count != replaced)
                return null;

            var allLines = SplitLines(content);
            var oldLines = SplitLines(oldString);
            var newLines = SplitLines(newString);
            var positions = sites.Select(s => NewLineRegex().Count(content[..s]) + 1).ToList();

            var lines = new List<DiffLineViewModel>();
            var shownThrough = 1; // 已展示到的新文件行号(下一 hunk 从此续接,避免重复展示)
            var preAdjust = 0;    // 此前 hunk 累计行数差(新-旧),换算旧文件行号用
            for (var i = 0; i < positions.Count; i++)
            {
                var postLine = positions[i];
                var preLine = postLine - preAdjust;

                // 前置上下文:最多 3 行,不与已展示范围重叠;中段跳行(非开头)补省略标记
                var ctxStart = Math.Max(shownThrough, postLine - DiffContextLines);
                if (ctxStart > shownThrough && shownThrough > 1)
                    lines.Add(new DiffLineViewModel("", "···", "ctx"));
                for (var no = ctxStart; no < postLine; no++)
                    lines.Add(new DiffLineViewModel(no.ToString(), allLines[no - 1], "ctx"));

                foreach (var t in oldLines)
                    lines.Add(new DiffLineViewModel((preLine++).ToString(), t, "del"));
                foreach (var t in newLines)
                    lines.Add(new DiffLineViewModel(postLine++.ToString(), t, "add"));

                // 后置上下文:最多 3 行,且不越过下一处替换的起始行(那里由下一 hunk 的前置上下文接续)
                var limit = i + 1 < positions.Count ? positions[i + 1] - 1 : allLines.Length;
                var ctxEnd = Math.Min(postLine + DiffContextLines - 1, limit);
                for (var no = postLine; no <= ctxEnd; no++)
                    lines.Add(new DiffLineViewModel(no.ToString(), allLines[no - 1], "ctx"));
                shownThrough = ctxEnd + 1;
                preAdjust += newLines.Length - oldLines.Length;
            }

            if (lines.Count > MaxDetailLines)
            {
                var truncated = lines.Take(MaxDetailLines).ToList();
                truncated.Add(new DiffLineViewModel("", $"… 共 {lines.Count} 行,已截断", "ctx"));
                return truncated;
            }
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>apply_patch 的 hunk diff:按头信息维护旧/新行号,上下文行显示旧行号。</summary>
    private static IReadOnlyList<DiffLineViewModel> BuildPatchDiff(string patch)
    {
        var lines = new List<DiffLineViewModel>();
        var oldNo = 1; // 无 @@ 头的退化补丁从第 1 行起算;有头时由头信息覆盖
        var newNo = 1;
        foreach (var raw in SplitLines(patch))
        {
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = System.Text.RegularExpressions.Regex.Match(raw, @"^@@ -(\d+)(?:,(\d+))? \+(\d+)");
                if (match.Success)
                {
                    oldNo = int.Parse(match.Groups[1].Value);
                    newNo = int.Parse(match.Groups[3].Value);
                }
                continue;
            }
            if (raw.StartsWith('\\') || raw.StartsWith("---") || raw.StartsWith("+++"))
                continue;
            if (raw.StartsWith('+'))
            {
                lines.Add(new DiffLineViewModel(newNo.ToString(), raw[1..], "add"));
                newNo++;
            }
            else if (raw.StartsWith('-'))
            {
                lines.Add(new DiffLineViewModel(oldNo.ToString(), raw[1..], "del"));
                oldNo++;
            }
            else
            {
                var text = raw.Length > 0 ? raw[1..] : "";
                // 上下文行显示新文件行号(与编辑文件 diff 的口径一致)
                lines.Add(new DiffLineViewModel(newNo.ToString(), text, "ctx"));
                oldNo++;
                newNo++;
            }
        }
        return lines;
    }

    /// <summary>在目标文件中找 text 首次出现的行号(1-based);文件不可读或未找到返回 null。
    /// 模型常传相对路径:先按原样读,失败再按会话工作目录解析(与工具执行时的路径基准一致)。</summary>
    private static int? TryFindLine(string path, string text, string? workdir)
    {
        try
        {
            if (path.Length == 0 || text.Length == 0)
                return null;
            var content = TryReadAllText(path)
                ?? (workdir is { Length: > 0 } ? TryReadAllText(Path.Combine(workdir, path)) : null);
            if (content is null)
                return null;
            var index = content.IndexOf(text, StringComparison.Ordinal);
            if (index < 0)
                return null;
            return NewLineRegex().Count(content[..index]) + 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>读文件全文;任何读取失败返回 null(定位行号是尽力而为,不影响主流程)。</summary>
    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>edit_file 成功后回填 diff 行号:此时文件内容已更新,new_string 必能定位到替换后的位置;
    /// 删除行与新增行都从该位置起算(replace diff 结构为旧行在前、新行在后)。纯删除(new_string 为空)
    /// 改后内容中无锚点,保持行号为空。已有行号(预定位成功)不覆盖。</summary>
    private static void FillEditDiffLineNos(ToolCallViewModel row)
    {
        if (row.EditDiff is not { } edit || row.DiffLines.Count == 0)
            return;
        if (row.DiffLines.All(l => !string.IsNullOrEmpty(l.LineNo)))
            return; // 预定位已成功,无需回填
        var anchor = edit.NewString.Length > 0 ? edit.NewString : null;
        if (anchor is null)
            return;
        var baseLine = TryFindLine(edit.Path, anchor, edit.Workdir);
        if (baseLine is null)
            return;
        var oldNo = baseLine.Value;
        var newNo = baseLine.Value;
        var filled = new List<DiffLineViewModel>(row.DiffLines.Count);
        foreach (var line in row.DiffLines)
        {
            if (string.Equals(line.Kind, "del", StringComparison.Ordinal))
                filled.Add(line with { LineNo = (oldNo++).ToString() });
            else if (string.Equals(line.Kind, "add", StringComparison.Ordinal))
                filled.Add(line with { LineNo = (newNo++).ToString() });
            else
                filled.Add(line);
        }
        row.SetDiff(filled);
    }

    private static string[] SplitLines(string text) =>
        NewLineRegex().Split(text.Replace("\r", ""));

    private static string TruncateLines(string text, int maxLines)
    {
        var lines = NewLineRegex().Split(text);
        if (lines.Length <= maxLines)
            return text;
        return string.Join("\n", lines.Take(maxLines)) + $"\n… 共 {lines.Length} 行,已截断";
    }

    private static string BuildTerminalDetail(string command, string stdout, string stderr, bool timedOut, long exitCode)
    {
        var sb = new System.Text.StringBuilder("$ ").AppendLine(command);
        var stdoutLines = TakeLines(stdout, out var stdoutTruncated);
        foreach (var line in stdoutLines)
            sb.AppendLine(line);
        if (stderr.Length > 0)
        {
            foreach (var line in TakeLines(stderr, out _))
                sb.Append("[stderr] ").AppendLine(line);
        }
        var footer = timedOut ? "退出: 超时(强制终止)" : $"退出码 {exitCode}";
        if (stdoutTruncated)
            footer += "(输出过长已截断)";
        sb.Append(footer);
        return sb.ToString();
    }

    private static List<string> TakeLines(string text, out bool truncated)
    {
        truncated = false;
        var lines = NewLineRegex().Split(text.Replace("\r", "")).ToList();
        if (lines.Count > MaxDetailLines)
        {
            lines = lines.Take(MaxDetailLines).ToList();
            truncated = true;
        }
        return lines;
    }

    /// <summary>文件类工具的文件名链接:链接只显示文件名,摘要段为显示路径(工作目录下=相对路径,
    /// 其余=全路径,前端按「文件名亮 + 目录暗」双色渲染),剩余文案(如"追加")接在其后。</summary>
    private static void SetPathLink(ToolCallViewModel row, string path, string? workdir, string summaryRest = "")
    {
        row.PathLink = Path.GetFileName(path);
        var full = path.Replace('\\', '/');
        var display = RelativizeUnderWorkdir(full, workdir);
        row.Summary = (display.Length > 0 ? display + "  " : "") + summaryRest;
        row.PathOpenTarget = path.Length > 0 ? path : null;
        row.PathWorkdir = workdir;
    }

    /// <summary>显示用路径归一:在会话工作目录下 → 相对路径;否则原样(全路径)。正斜杠归一。</summary>
    private static string RelativizeUnderWorkdir(string full, string? workdir)
    {
        if (full.Length == 0 || string.IsNullOrWhiteSpace(workdir))
            return full;
        var wd = workdir.Replace('\\', '/').TrimEnd('/');
        if (wd.Length == 0)
            return full;
        if (full.StartsWith(wd + "/", StringComparison.OrdinalIgnoreCase))
            return full[(wd.Length + 1)..];
        return full;
    }

    private static string DescribePath(string path)
    {
        if (path.Length == 0)
            return "(未指定路径)";
        var name = Path.GetFileName(path);
        var dir = Path.GetDirectoryName(path);
        // 压缩过长的目录前缀,保留末端两级
        if (!string.IsNullOrEmpty(dir))
        {
            var parts = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var tail = string.Join("/", parts.TakeLast(2));
            return $"{name}  {tail}{Path.DirectorySeparatorChar}".Replace("\\", "/");
        }
        return name.Length > 0 ? name : path;
    }

    // ───────── JSON 辅助 ─────────

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // TryGetProperty 对非对象元素会抛 InvalidOperationException:所有按名取值的辅助方法统一先验类型
    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>mem_search 的关键词参数(数组为标准格式,兼容旧的单个字符串):合并成单行摘要。</summary>
    private static string DescribeKeywords(JsonElement root)
    {
        if (!root.TryGetProperty("keywords", out var kw))
            return "";
        return SingleLine(kw.ValueKind switch
        {
            JsonValueKind.Array => string.Join(" ", kw.EnumerateArray()
                .Select(k => k.ValueKind == JsonValueKind.String ? k.GetString() : k.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))),
            JsonValueKind.String => kw.GetString() ?? "",
            _ => kw.ToString(),
        });
    }

    private static long GetInt64(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static bool TryGetError(JsonElement root, out string error)
    {
        if (GetString(root, "error") is { Length: > 0 } direct)
        {
            error = direct;
            return true;
        }
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            error = GetString(value, "message") ?? GetString(value, "code") ?? value.ToString();
            return true;
        }
        error = "";
        return false;
    }

    private static string SingleLine(string text)
    {
        var line = (text ?? "").Replace("\r", "").Replace("\n", " ").Trim();
        return line.Length > MaxSummaryLength ? line[..MaxSummaryLength] + "…" : line;
    }

    private static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
