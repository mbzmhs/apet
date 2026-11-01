namespace APet.Framework.Security;

/// <summary>
/// 工具的权限类别:目录白名单可按类别限定工具(目录可读不代表可写、可执行终端)。
/// 文件修改类工具(write_file/edit_file/apply_patch)共用 "edit"。
/// </summary>
public static class ToolPermission
{
    public static string GetKey(string toolName) => toolName switch
    {
        "read_file" => "read",
        "write_file" or "edit_file" or "apply_patch" => "edit",
        "bash" or "bg_run" or "bg_output" or "bg_wait" or "bg_kill" or "bg_list" => "terminal",
        _ => toolName,
    };
}
