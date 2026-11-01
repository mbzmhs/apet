using APet.Framework.Models;
using APet.Framework.Security;

namespace APet.Framework.Tools;

/// <summary>
/// 自定义工具的推荐基类:提供 Enabled 启用开关(默认开启)与 Unknown 的效果解析兜底,
/// 子类只需实现 Definition 与 ExecuteAsync;需要精确鉴权时重写 AnalyzeEffect。
/// </summary>
public abstract class ToolBase : IApetTool
{
    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    public abstract ToolDefinition Definition { get; }

    public abstract Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.UnknownEffect;
}
