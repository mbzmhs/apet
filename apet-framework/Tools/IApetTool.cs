using APet.Framework.Models;
using APet.Framework.Security;

namespace APet.Framework.Tools;

/// <summary>Agent 可调用的工具,手动注册到 ToolRegistry。</summary>
public interface IApetTool
{
    /// <summary>工具定义,将原样提供给 LLM。</summary>
    ToolDefinition Definition { get; }

    /// <summary>
    /// 启用开关。禁用后注册中心不再向 LLM 导出该工具的定义,也不会执行它;
    /// 宿主可在任意时刻修改此值,下一次请求即生效。
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>执行工具。argumentsJson 为 LLM 生成的 JSON 参数字符串;返回值作为 Tool 消息回传给 LLM。</summary>
    Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// 安全鉴权:从参数中解析本次调用的影响路径与影响方式(read/write/none)。
    /// 无法解析时必须返回 Unknown,由 Agent 的安全策略决定放行或拒绝。
    /// </summary>
    ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.UnknownEffect;
}
