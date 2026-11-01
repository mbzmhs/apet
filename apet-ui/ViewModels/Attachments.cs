using System.Windows.Media;

namespace APet.UI.ViewModels;

/// <summary>附件种类:图片(以多模态内容发送给模型)/ 文本文件(内联进消息正文,任意模型可用)。</summary>
public enum AttachmentKind
{
    Image,
    TextFile,
}

/// <summary>
/// 输入卡片上的待发送附件:横向缩略图显示在输入框上方,点叉号删除;发送时消费。
/// 图片携带 data URL(多模态内容),文本文件携带内联正文。
/// </summary>
public sealed class AttachmentViewModel
{
    public AttachmentViewModel(AttachmentKind kind, string name, ImageSource? preview)
    {
        Kind = kind;
        Name = name;
        Preview = preview;
    }

    /// <summary>种类(图片 / 文本文件)。</summary>
    public AttachmentKind Kind { get; }

    /// <summary>文件名(展示与文本文件内联头用)。</summary>
    public string Name { get; }

    /// <summary>图片缩略图(文本文件为 null,以文档图标显示)。</summary>
    public ImageSource? Preview { get; }

    /// <summary>图片 data URL(发送给模型);文本文件内容(内联进正文)。按种类二选一。</summary>
    public string? DataUrl { get; init; }

    /// <summary>文本文件内容(内联进消息正文)。</summary>
    public string? TextContent { get; init; }

    /// <summary>大小描述(tooltip,如 1.2 MB)。</summary>
    public string SizeText { get; init; } = "";

    /// <summary>是否图片(模板据此显示缩略图或文档图标)。</summary>
    public bool IsImage => Kind == AttachmentKind.Image;

    /// <summary>悬停提示:文件名 + 大小。</summary>
    public string Tooltip => SizeText.Length > 0 ? $"{Name}({SizeText})" : Name;
}
