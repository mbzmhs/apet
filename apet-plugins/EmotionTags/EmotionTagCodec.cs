using System.Text;
using System.Text.RegularExpressions;

namespace APet.Plugins.EmotionTags;

/// <summary>
/// 情绪标签括号风格(桌宠插件注入 system prompt 的格式,TTS/气泡/聊天 UI 按同一风格解析)。
/// </summary>
public enum PetTagStyle
{
    SquareBrackets,
    CurlyBraces,
    AngleBrackets,
    DoubleSquareBrackets,
}

/// <summary>
/// 情绪标签解析/剥离的唯一实现(此前在 TTS、对话气泡、桌宠 TagWrapper、UI 流式剥离器四处各写一份,
/// 靠注释约定一致):按风格构建 tag 正则、剥离完整 tag、识别尾部未收完的半个 tag。
/// 所有消费方(TTS 换声线、气泡显示、聊天区流式显示)必须经由本类,禁止再私拷实现。
/// </summary>
public static class EmotionTagCodec
{
    /// <summary>按风格返回 tag 的包裹符。</summary>
    public static (string Open, string Close) Wrapper(PetTagStyle style) => style switch
    {
        PetTagStyle.CurlyBraces => ("{", "}"),
        PetTagStyle.AngleBrackets => ("<", ">"),
        PetTagStyle.DoubleSquareBrackets => ("[[", "]]"),
        _ => ("[", "]"),
    };

    /// <summary>情绪 tag 正则:open 与 open 之间最少一个非 close 字符(非贪婪),close 的去重集合排除在内容之外。</summary>
    public static Regex CreateRegex(PetTagStyle style)
    {
        var (open, close) = Wrapper(style);
        return CreateRegex(open, close);
    }

    public static Regex CreateRegex(string open, string close)
    {
        var excluded = string.Concat(close.Distinct().Select(c => Regex.Escape(c.ToString())));
        return new Regex(Regex.Escape(open) + "([^" + excluded + "]+?)" + Regex.Escape(close), RegexOptions.Compiled);
    }

    /// <summary>尾部挂了未收完的括号时,返回应扣留的起始位置;否则 -1。</summary>
    public static int IndexOfPartialTag(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var (isOpen, close) = text[i] switch
            {
                '[' => (true, ']'),
                '(' => (true, ')'),
                '{' => (true, '}'),
                '<' => (true, '>'),
                _ => (false, default),
            };
            if (isOpen && text.IndexOf(close, i + 1) < 0)
                return i;
        }
        return -1;
    }

    /// <summary>对完整文本一次性剥离(持久化记录还原用):无流式扣留,未收完的残片按普通文本保留。</summary>
    public static string StripAll(string text, PetTagStyle style)
    {
        if (text.Length == 0)
            return text;
        var stream = new EmotionTagStream(style);
        stream.Append(text);
        return stream.Finish();
    }
}

/// <summary>
/// 流式情绪 tag 剥离器:从显示文本中剥离完整情绪 tag,并扣留尾部未收完的半个 tag,
/// 保证半个 tag 不会在屏幕上闪现;一段结束时残片按普通文本放行。已显示的前缀只增不改,流式增量追加即可。
/// 有状态、按正文段一个实例。
/// </summary>
public sealed class EmotionTagStream
{
    private readonly Regex _tagRegex;
    private readonly StringBuilder _raw = new(); // 本段完整原文(含情绪 tag,未剥离)
    private int _decided;                        // 已裁决的 raw 下标(之前:已显示或已作为 tag 剥除)
    private string _displayed = "";               // 当前可显示的全文(单调追加)
    private bool _finished;

    public EmotionTagStream(string open, string close)
    {
        _tagRegex = EmotionTagCodec.CreateRegex(open, close);
    }

    /// <summary>按桌宠的括号风格创建(与 TTS/气泡注入-解析链同一来源,保证一致)。</summary>
    public EmotionTagStream(PetTagStyle style) : this(EmotionTagCodec.Wrapper(style).Open, EmotionTagCodec.Wrapper(style).Close)
    {
    }

    /// <summary>喂入一个流式增量,返回当前可显示的全文(完整 tag 已剥除;尾部半个 tag 扣留中)。</summary>
    public string Append(string delta)
    {
        if (_finished || delta.Length == 0)
            return _displayed;

        _raw.Append(delta);
        var raw = _raw.ToString();
        var pos = _decided;
        foreach (Match m in _tagRegex.Matches(raw, pos))
        {
            _displayed += raw[pos..m.Index]; // tag 之前的普通文本放行
            pos = m.Index + m.Length;        // 完整 tag 整体剥除
        }

        var tail = raw[pos..];
        var hold = EmotionTagCodec.IndexOfPartialTag(tail);
        if (hold >= 0)
        {
            _displayed += tail[..hold];      // 半个 tag 先扣住,等收完再整体裁决
            _decided = pos + hold;           // 裁决线停在半 tag 的起始括号处
        }
        else
        {
            _displayed += tail;
            _decided = raw.Length;
        }
        return _displayed;
    }

    /// <summary>本段结束(流式完成/工具调用切换/出错):扣留的残片按普通文本放行,返回最终全文。幂等。</summary>
    public string Finish()
    {
        if (_finished)
            return _displayed;
        _finished = true;
        var raw = _raw.ToString();
        _displayed += raw[_decided..]; // 未收完的标签残片按普通文本处理(与 TTS CompleteTurn 同规则)
        _decided = raw.Length;
        return _displayed;
    }
}
