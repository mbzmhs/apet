namespace APet.Framework.Plugin;

/// <summary>
/// 插件注册的聊天框快捷指令(输入 $ 弹出补全面板,Tab/Enter 补全后回车执行)。
/// Execute 的第一个参数由宿主注入:当前会话 Id(UI 侧会话概念,框架仅透传;可能为 null);
/// 第二个参数是命令名之后的参数文本(Trim 过)。返回值是给用户看的反馈文本,
/// 宿主展示为该会话的状态行;返回 null 表示成功且无反馈。命令内不得直接操作 UI。
/// </summary>
public sealed record PluginChatCommand(
    string Name,
    string Description,
    bool TakesArgument,
    Func<string?, string, string?> Execute);
