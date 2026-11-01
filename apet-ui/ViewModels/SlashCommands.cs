namespace APet.UI.ViewModels;

/// <summary>聊天输入框斜杠命令:名称(含 /)、说明、是否带参数(影响 Tab 补全后是否追加空格)。</summary>
public sealed record SlashCommand(string Name, string Description, bool TakesArgument);

/// <summary>内置斜杠命令与前缀匹配(按首个空白分隔的 token,大小写不敏感)。</summary>
public static class SlashCommands
{
    public static readonly SlashCommand[] All =
    {
        new("/new", "新建一个会话并切换过去", false),
        new("/compact", "立即压缩上下文;命令后可附加说明,接到摘要模板尾部", true),
        new("/summary", "查看当前会话的摘要", false),
        new("/undo", "撤销上一条消息:会话与显示回滚到该消息之前,原文返回输入框(压缩后需先发送一条新消息)", false),
        new("/fork", "分叉出当前会话的副本(可带新名字,缺省 Fork of 原名);上下文与内容相同,仅会话 Id 不同", true),
    };

    /// <summary>按前缀过滤;/ 开头且无匹配时返回空列表(面板据此隐藏)。</summary>
    public static List<SlashCommand> Match(string text)
    {
        var token = text.Split(' ', '\t').FirstOrDefault() ?? "";
        return All.Where(c => c.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}

/// <summary>命令面板行视图模型:包装斜杠命令 + 高亮状态(上下键导航驱动 hover 特效)。</summary>
public sealed class CommandPaletteItem : ObservableObject
{
    private bool _isSelected;

    public SlashCommand Command { get; }
    public string Name => Command.Name;
    public string Description => Command.Description;

    /// <summary>是否被上下键选中(模板据此切换行背景)。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value, nameof(IsSelected));
    }

    public CommandPaletteItem(SlashCommand command) => Command = command;
}
