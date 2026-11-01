namespace APet.Framework.Plugin;

/// <summary>
/// 标记插件上的一个可配置设置项(标在公开可读写的属性上)。
/// Name 留空时使用属性名;数字类型应用 Min/Max,文本应用 MinLength/MaxLength,
/// 列表应用 MinLength/MaxLength(元素个数)并将 Min/Max 应用到每个元素。
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class PluginSettingAttribute : Attribute
{
    public PluginSettingAttribute() { }

    public PluginSettingAttribute(string name) => Name = name;

    /// <summary>设置名称,留空时使用属性名。查询时不区分大小写。</summary>
    public string Name { get; } = "";

    /// <summary>设置说明,供宿主 UI 展示。</summary>
    public string? Description { get; set; }

    /// <summary>数字类型的最小值,默认不限制。</summary>
    public double Min { get; set; } = double.MinValue;

    /// <summary>数字类型的最大值,默认不限制。</summary>
    public double Max { get; set; } = double.MaxValue;

    /// <summary>文本的最小长度 / 列表的最小元素个数,默认不限制。</summary>
    public int MinLength { get; set; } = 0;

    /// <summary>文本的最大长度 / 列表的最大元素个数,默认不限制。</summary>
    public int MaxLength { get; set; } = int.MaxValue;

    /// <summary>
    /// 组合框可选项(仅对 string 类型设置生效):非空时宿主渲染为下拉框,值必须在选项内。
    /// 枚举类型的设置无需填写,框架自动把枚举名作为选项。
    /// </summary>
    public string[] Options { get; set; } = Array.Empty<string>();

    /// <summary>设置项显示名(如中文标签),留空时宿主以 Name 充当。</summary>
    public string Label { get; set; } = "";

    /// <summary>分组名:宿主渲染设置界面时按该值分组排版,留空归入默认组。</summary>
    public string Group { get; set; } = "";

    /// <summary>
    /// 控件类型提示(供宿主渲染更合适的编辑控件):slider(数字滑条)/ color(取色器)/
    /// password(密码掩码)/ textarea(多行文本);留空由宿主按值类型推断默认控件。
    /// </summary>
    public string UiHint { get; set; } = "";
}
