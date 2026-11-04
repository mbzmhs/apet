using APet.Framework.Logging;

namespace APet.Framework.LLM;

/// <summary>OpenAI 兼容客户端的连接配置。</summary>
public sealed class LlmClientOptions
{
    /// <summary>LLM 通讯日志。留空时使用所属 Agent 的日志,再退回 Log.Global。</summary>
    public ILog? Log { get; set; }
    /// <summary>服务地址,例如 https://api.openai.com/v1 或 http://localhost:8000/v1。</summary>
    public string BaseUrl { get; set; } = "";

    public string? ApiKey { get; set; }

    /// <summary>供应商显示名(宿主建连接时填入;不参与请求,仅用于给助手消息盖模型归因落盘)。</summary>
    public string? Provider { get; set; }

    /// <summary>默认模型名称,请求未指定 Model 时使用。</summary>
    public string? Model { get; set; }

    /// <summary>
    /// 思考等级:none(关闭)/ default(开启,按 high 发送)/ minimal / low / medium / high / xhigh / max。
    /// none 按各 ThinkingStyle 的关闭表达发送(thinking→disabled、enable_thinking→false、reasoning_effort→不发送);
    /// 兼容值 "off" 视同 none。
    /// </summary>
    public string ReasoningEffort { get; set; } = "off";

    /// <summary>
    /// 是否在请求体中携带 thinking 字段(GLM 等端点以它控制思考开关)。
    /// false = 完全不发送该字段:Gemini 的 OpenAI 兼容端点等不支持该字段,携带会报 400。
    /// 旧版开关,由 ThinkingStyle 取代;ThinkingStyle 为空时按其映射(true→thinking,false→off)。
    /// </summary>
    public bool SendThinkingField { get; set; } = true;

    /// <summary>
    /// 思考字段样式(端点异构,同一开关各家字段不同):
    /// "thinking" = thinking.type enabled/disabled(GLM、阿里百炼等,默认);
    /// "enable_thinking" = Qwen 原生布尔开关;
    /// "reasoning_effort" = OpenAI/llama.cpp 风格,携带具体强度(minimal/low/medium/high;关闭思考时不发送);
    /// "all" = 全兼容样式,同时发送 thinking/enable_thinking/reasoning_effort/reasoning.effort(仅宽容端点);
    /// "off" = 完全不发送思考字段(Gemini 兼容等严格端点,携带未知字段会 400)。
    /// 空 = 按 SendThinkingField 旧开关映射。
    /// </summary>
    public string ThinkingStyle { get; set; } = "";

    /// <summary>
    /// 单次回复的 max_tokens 上限;0 = 不发送(由服务端默认决定)。来自所选模型的独立配置。
    /// </summary>
    public int MaxTokens { get; set; }

    /// <summary>
    /// 代理模式:"system"(默认,跟随系统代理设置)/ "none"(不使用代理)/
    /// 其他值视为自定义代理地址(如 http://127.0.0.1:7890)。
    /// </summary>
    public string ProxyMode { get; set; } = "system";

    /// <summary>自定义代理地址,ProxyMode 为自定义时使用。</summary>
    public string? ProxyUrl { get; set; }
}
