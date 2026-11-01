namespace APet.Framework.Plugin;

/// <summary>插件设置操作失败(未知设置名、类型不符、超出限制等)。</summary>
public sealed class PluginSettingException : Exception
{
    public PluginSettingException(string message) : base(message) { }
}
