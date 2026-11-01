using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Tools;

namespace APet.Framework.Plugin;

/// <summary>
/// 把启用了 IsTool 的插件适配为 IApetTool,挂进 ToolRegistry 的动态工具管线:
/// Enabled 直接映射插件自身的 Enabled(禁用插件即同时下线其工具)。
/// 实现 IApetToolMultimodal:执行走插件的 ExecuteToolResultAsync(默认包装字符串版,
/// 插件重写富入口即可返回带图片附件的结果)。
/// </summary>
internal sealed class PluginToolAdapter : IApetTool, IApetToolMultimodal
{
    private readonly AgentPluginBase _plugin;

    public PluginToolAdapter(AgentPluginBase plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(_plugin));
    }

    public ToolDefinition Definition => _plugin.ToolDefinition;

    /// <summary>与插件自身的 Enabled 联动。注册中心只读取该值;开关变更请走插件注册对象(会驱动生命周期)。</summary>
    public bool Enabled
    {
        get => _plugin.Enabled;
        set { /* 只读语义:生命周期由 PluginRegistration.SetEnabled 统一管理 */ }
    }

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default) =>
        _plugin.ExecuteToolAsync(argumentsJson, cancellationToken);

    public Task<ToolResult> ExecuteRichAsync(string argumentsJson, CancellationToken cancellationToken = default) =>
        _plugin.ExecuteToolResultAsync(argumentsJson, cancellationToken);

    // 方法名必须与 IApetTool.AnalyzeEffect 完全一致:接口自带默认实现(Unknown),
    // 名字对不上时编译不报错、但插件声明的效应(如 skill 的 NoEffect)会被默认实现吞掉,全部弹鉴权卡
    public ToolEffect AnalyzeEffect(string argumentsJson) => _plugin.AnalyzeToolEffect(argumentsJson);
}
