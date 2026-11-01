using System.Text.Json;
using System.Text.Json.Nodes;
using APet.Framework.Models;
using APet.Framework.Security;

namespace APet.Framework.Tools.BuiltIn;

/// <summary>询问选项:标题 + 可选说明;Recommended 由界面标注"(Recommended)"。</summary>
public sealed record AskOption(string Label, string? Description, bool Recommended);

/// <summary>
/// 一个询问问题:短标题 + 完整问题文本 + 1~4 个选项 + 单选/多选。
/// 界面固定追加"其他(自行输入)"项,不占用选项名额。
/// </summary>
public sealed record AskQuestion(
    string Title,
    string Question,
    bool MultiSelect,
    IReadOnlyList<AskOption> Options);

/// <summary>
/// 宿主询问委托:把问题呈现给用户并等待作答。返回值为序列化后的用户答案 JSON
/// (原样回传给模型);返回 null 表示用户跳过或执行被中断。
/// </summary>
public delegate Task<string?> AskUserHandler(IReadOnlyList<AskQuestion> questions, CancellationToken cancellationToken);

/// <summary>
/// ask 内置工具:让模型对不明确的决定向用户发起结构化询问(可一次多问,逐题翻页作答)。
/// 纯 UI 交互、无文件系统影响(NoEffect),不触发鉴权。挂起-应答由宿主委托实现。
/// </summary>
public sealed class AskUserTool : ToolBase
{
    /// <summary>一次询问最多的问题数。</summary>
    public const int MaxQuestions = 4;

    /// <summary>每个问题最多的选项数(不含界面固定的"其他"输入项)。</summary>
    public const int MaxOptions = 4;

    private readonly AskUserHandler _ask;

    public AskUserTool(AskUserHandler ask) => _ask = ask;

    public override ToolDefinition Definition => ToolDefinition.Create(
        "ask",
        "Ask the user one or more structured questions when a decision genuinely needs their input "
        + "(ambiguous requirements, multiple valid approaches, destructive/scope-changing actions). "
        + $"Up to {MaxQuestions} questions per call; each question has a short title, the full question text, "
        + $"1-{MaxOptions} options (each with a label and an optional description) and is single-select or multi-select. "
        + "The UI always offers a free-text \"Other\" answer. "
        + "The result mirrors your questions: {\"answers\":[{\"title\",\"question\",\"selected\":[labels],\"other\"}]}, "
        + "where \"selected\" lists the labels the user picked and \"other\" (present only when filled) "
        + "holds their own typed text when they answered outside your options — treat \"other\" as the answer "
        + "whenever it is present, even if \"selected\" is empty. "
        + "Answer with the user's exact choices. "
        + "The user may skip: never re-ask a skipped question — make a reasonable assumption and state it in your reply. "
        + "Do not use this tool for things you can verify yourself or for trivial preferences.",
        $$"""
        {
          "type": "object",
          "properties": {
            "questions": {
              "type": "array",
              "minItems": 1,
              "maxItems": {{MaxQuestions}},
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string", "description": "Very short label shown as the question header (e.g. \"Auth method\")" },
                  "question": { "type": "string", "description": "The complete question, ending with a question mark" },
                  "multiSelect": { "type": "boolean", "description": "true = user may pick several options; false (default) = exactly one" },
                  "options": {
                    "type": "array",
                    "minItems": 1,
                    "maxItems": {{MaxOptions}},
                    "items": {
                      "type": "object",
                      "properties": {
                        "label": { "type": "string", "description": "Option title shown to the user" },
                        "description": { "type": "string", "description": "One sentence explaining what choosing this option means" },
                        "recommended": { "type": "boolean", "description": "Mark as the suggested choice (rendered as \"(Recommended)\")" }
                      },
                      "required": ["label"]
                    }
                  }
                },
                "required": ["title", "question", "options"]
              }
            }
          },
          "required": ["questions"]
        }
        """,
        displayName: "询问用户");

    /// <summary>纯 UI 交互询问,无副作用,无需鉴权。</summary>
    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        List<AskQuestion> questions;
        try
        {
            questions = Parse(argumentsJson);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"invalid ask arguments: {ex.Message}" });
        }

        var answer = await _ask(questions, cancellationToken);
        if (answer is null)
            return """{"skipped":true,"note":"The user skipped or dismissed the questions. Do not ask again; proceed with a reasonable assumption and state it in your reply."}""";
        // 统一包成对象回传:宿主的结果展示按对象解析,模型也得到带字段名的清晰结构
        try
        {
            var node = JsonNode.Parse(answer);
            return new JsonObject { ["answers"] = node }.ToJsonString();
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { answersText = answer });
        }
    }

    /// <summary>解析并校验问题结构;不合法时抛异常(调用方转成错误文本回给模型)。</summary>
    private static List<AskQuestion> Parse(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        if (!doc.RootElement.TryGetProperty("questions", out var qs) ||
            qs.ValueKind != JsonValueKind.Array || qs.GetArrayLength() == 0)
            throw new ArgumentException("questions must be a non-empty array");
        if (qs.GetArrayLength() > MaxQuestions)
            throw new ArgumentException($"at most {MaxQuestions} questions per call");

        var result = new List<AskQuestion>();
        var i = 0;
        foreach (var q in qs.EnumerateArray())
        {
            i++;
            var title = GetString(q, "title");
            var question = GetString(q, "question");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(question))
                throw new ArgumentException($"question #{i} needs non-empty title and question");
            if (!q.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array)
                throw new ArgumentException($"question #{i} is missing options");
            var n = opts.GetArrayLength();
            if (n < 1 || n > MaxOptions)
                throw new ArgumentException($"question #{i} must have 1-{MaxOptions} options");

            var options = new List<AskOption>();
            foreach (var o in opts.EnumerateArray())
            {
                var label = GetString(o, "label");
                if (string.IsNullOrWhiteSpace(label))
                    throw new ArgumentException($"question #{i} has an option without label");
                options.Add(new AskOption(label,
                    GetString(o, "description"),
                    o.TryGetProperty("recommended", out var r) && r.ValueKind == JsonValueKind.True));
            }

            var multi = q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;
            result.Add(new AskQuestion(title, question, multi, options));
        }
        return result;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
