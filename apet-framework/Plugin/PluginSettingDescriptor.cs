namespace APet.Framework.Plugin;

/// <summary>设置项的描述信息,供宿主读取与渲染设置界面。</summary>
public sealed class PluginSettingDescriptor
{
    /// <summary>设置名称(注解指定或属性名)。</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>值类型的友好名称,如 string / int / double / bool / 枚举名 / List&lt;string&gt;。</summary>
    public required string ValueType { get; init; }

    /// <summary>数字类型的最小值。</summary>
    public double Min { get; init; }

    /// <summary>数字类型的最大值。</summary>
    public double Max { get; init; }

    /// <summary>文本最小长度 / 列表最小元素个数。</summary>
    public int MinLength { get; init; }

    /// <summary>文本最大长度 / 列表最大元素个数。</summary>
    public int MaxLength { get; init; }

    /// <summary>组合框可选项(string 设置来自注解 Options,枚举设置为枚举名列表);空表示自由输入。</summary>
    public string[] Options { get; init; } = Array.Empty<string>();

    /// <summary>设置项显示名(注解 Label);空表示宿主以 Name 充当。</summary>
    public string Label { get; init; } = "";

    /// <summary>分组名(注解 Group);空表示默认组。</summary>
    public string Group { get; init; } = "";

    /// <summary>控件类型提示(注解 UiHint):slider / color / password / textarea;空由宿主推断。</summary>
    public string UiHint { get; init; } = "";

    /// <summary>当前值。</summary>
    public object? CurrentValue { get; init; }
}
