using APet.Framework.LLM;
using APet.Framework.Logging;

namespace APet.Framework.Agent;

/// <summary>Agent 配置:LLM 连接信息与运行参数。Agent 本身只保存配置,不保存会话状态。</summary>
public sealed class AgentOptions
{
    public LlmClientOptions Llm { get; set; } = new();

    /// <summary>Agent 运行日志,默认使用 Log.Global。</summary>
    public ILog Log { get; set; } = Logging.Log.Global;

    /// <summary>该 Agent 的安全鉴权策略(每个 Agent 独立)。为空时使用默认安全策略(拒绝未知效果与其他目录访问)。</summary>
    public Security.AgentSecurityPolicy? Security { get; set; }

    /// <summary>系统提示词,Session 中不存在 System 消息时会在运行开始时自动注入。</summary>
    public string? SystemPrompt { get; set; }

    public double? Temperature { get; set; }

    /// <summary>单轮运行中允许的最大"模型请求→工具执行"循环次数,防止死循环;0 表示不限制。</summary>
    public int MaxToolIterations { get; set; } = 0;
}
