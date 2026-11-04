namespace APet.Framework.Tools.BuiltIn;

/// <summary>内置工具的批量注册配置。</summary>
public sealed class BuiltInToolOptions
{
    public FileToolOptions ReadFile { get; } = new();

    public FileToolOptions WriteFile { get; } = new();

    public FileToolOptions EditFile { get; } = new();

    public FileToolOptions Patch { get; } = new();

    /// <summary>搜索类工具(search_files / find_files)共用:根目录约束与默认编码。</summary>
    public FileToolOptions Search { get; } = new();

    public TerminalToolOptions Terminal { get; } = new();

    public WebFetchToolOptions WebFetch { get; } = new();
}

public static class ToolRegistryExtensions
{
    /// <summary>
    /// 注册内置工具:read_file / write_file / edit_file / apply_patch / bash / web_fetch /
    /// search_files / find_files。
    /// 注册后均为启用状态,宿主可通过工具实例的 Enabled 随时开关。
    /// </summary>
    public static ToolRegistry RegisterBuiltIns(this ToolRegistry registry, Action<BuiltInToolOptions>? configure = null)
    {
        var options = new BuiltInToolOptions();
        configure?.Invoke(options);
        registry.Register(new ReadFileTool(options.ReadFile));
        registry.Register(new WriteFileTool(options.WriteFile));
        registry.Register(new EditFileTool(options.EditFile));
        registry.Register(new ApplyPatchTool(options.Patch));
        registry.Register(new TerminalTool(options.Terminal));
        registry.Register(new WebFetchTool(options.WebFetch));
        registry.Register(new SearchFilesTool(options.Search));
        registry.Register(new FindFilesTool(options.Search));
        return registry;
    }
}
