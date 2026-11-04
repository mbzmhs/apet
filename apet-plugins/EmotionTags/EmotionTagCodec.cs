using System.Collections.Concurrent;
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

    private static readonly ConcurrentDictionary<(string Open, string Close), Regex> _regexCache = new();

    /// <summary>情绪 tag 正则:open 与 open 之间最少一个非 close 字符(非贪婪),close 的去重集合排除在内容之外。
    /// 按包裹符对缓存(Compiled 编译代价高,流式期间每个增量都会来取,风格在会话内不变,命中即复用)。</summary>
    public static Regex CreateRegex(PetTagStyle style)
    {
        var (open, close) = Wrapper(style);
        return CreateRegex(open, close);
    }

    public static Regex CreateRegex(string open, string close) =>
        _regexCache.GetOrAdd((open, close), static key =>
        {
            var excluded = string.Concat(key.Close.Distinct().Select(c => Regex.Escape(c.ToString())));
            return new Regex(Regex.Escape(key.Open) + "([^" + excluded + "]+?)" + Regex.Escape(key.Close), RegexOptions.Compiled);
        });

    /// <summary>尾部挂了未收完的包裹符时,返回应扣留的起始位置;否则 -1。
    /// 按传入的包裹符成对识别:双字符包裹符(如 [[ ]])在单侧 ] 到达时不得误判为已收完。
    /// 扣留点取【最早】的未闭合 open——取最近会让相邻 [[ 的前一个 [ 被误放行为普通文本,
    /// 与一次性剥离的匹配起点(正则从左扫描)不一致。</summary>
    public static int IndexOfPartialTag(string text, string open, string close)
    {
        for (var i = text.IndexOf(open, StringComparison.Ordinal); i >= 0; i = text.IndexOf(open, i + 1, StringComparison.Ordinal))
        {
            if (text.IndexOf(close, i + open.Length, StringComparison.Ordinal) < 0)
                return i;
        }

        // 尾部是 open 的真前缀(含尾巴整体,如刚到的一个 [ 对 [[ 包裹符)→ 可能正要长成包裹符,同样扣住
        for (var k = Math.Min(open.Length, text.Length); k >= 1; k--)
        {
            if (text.EndsWith(open[..k], StringComparison.Ordinal))
                return text.Length - k;
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
    private readonly string _open;
    private readonly string _close;
    private readonly StringBuilder _raw = new();  // 本段完整原文(含情绪 tag,未剥离)
    private readonly StringBuilder _display = new(); // 已显示文本(单调追加)
    private int _decided;                         // 已裁决的 raw 下标(之前:已显示或已作为 tag 剥除)
    private bool _finished;

    public EmotionTagStream(string open, string close)
    {
        _open = open;
        _close = close;
        _tagRegex = EmotionTagCodec.CreateRegex(open, close);
    }

    /// <summary>按桌宠的括号风格创建(与 TTS/气泡注入-解析链同一来源,保证一致)。</summary>
    public EmotionTagStream(PetTagStyle style) : this(EmotionTagCodec.Wrapper(style).Open, EmotionTagCodec.Wrapper(style).Close)
    {
    }

    /// <summary>喂入一个流式增量,返回当前可显示的全文(完整 tag 已剥除;尾部半个 tag 扣留中)。
    /// 每增量只重扫未裁决段、只拼接新增量:裁决线之前的内容已定,永不重复处理。</summary>
    public string Append(string delta)
    {
        if (_finished || delta.Length == 0)
            return _display.ToString();

        _raw.Append(delta);
        var pending = _raw.ToString(_decided, _raw.Length - _decided); // 未裁决段(半个 tag 收完才含它的收尾括号)
        var pos = 0;
        foreach (Match m in _tagRegex.Matches(pending))
        {
            _display.Append(pending, pos, m.Index - pos); // tag 之前的普通文本放行
            pos = m.Index + m.Length;                     // 完整 tag 整体剥除
        }

        var tail = pending[pos..];
        var hold = EmotionTagCodec.IndexOfPartialTag(tail, _open, _close);
        if (hold >= 0)
        {
            _display.Append(tail, 0, hold);   // 半个 tag 先扣住,等收完再整体裁决
            _decided += pos + hold;           // 裁决线停在半 tag 的起始括号处
        }
        else
        {
            _display.Append(tail);
            _decided = _raw.Length;
        }
        return _display.ToString();
    }

    /// <summary>本段结束(流式完成/工具调用切换/出错):扣留的残片按普通文本放行,返回最终全文。幂等。</summary>
    public string Finish()
    {
        if (_finished)
            return _display.ToString();
        _finished = true;
        _display.Append(_raw.ToString(_decided, _raw.Length - _decided)); // 未收完的标签残片按普通文本处理(与 TTS CompleteTurn 同规则)
        _decided = _raw.Length;
        return _display.ToString();
    }
}
