using System.Runtime.InteropServices;
using System.Text;

namespace APet.Framework.Tools;

/// <summary>
/// 流式自适应解码:把陆续到达的子进程输出字节增量解成文本,编码只判定一次
/// (判定规则同 <see cref="OutputTextDecoder"/>:BOM → UTF-16 的 NUL 分布 → UTF-8 合法性 → 系统代码页)。
/// 纯 ASCII 段即时出(UTF-8/GBK/ASCII 同码,无须等判定);出现歧义字节后攒样本,
/// 样本可判定时锁定编码,此后按状态化 Decoder 续解——跨块的多字节字符(UTF-8/GBK/UTF-16)不会被劈坏。
/// 运行终端工具的实时输出与后台任务(bg_run)的输出读取共用它,避免两处解码行为分叉。
/// </summary>
public sealed class IncrementalTextDecoder
{
    /// <summary>攒样本的上限:正常情况下出现高位字节即锁定,只有异常字节流才会攒到这里强制判定。</summary>
    private const int SampleCap = 64 * 1024;

    private readonly List<byte> _sample = new();
    private bool _asciiOnly;
    private Decoder? _decoder;
    private int _bomSkip;

    /// <summary>forced:调用方已指定编码(如宿主配置的命令输出编码),跳过 ASCII 快路径与自动判定,一律按它解码。</summary>
    public IncrementalTextDecoder(Encoding? forced = null)
    {
        _asciiOnly = forced is null;
        if (forced is not null)
            _decoder = forced.GetDecoder(); // BOM 不特殊处理:与原"整段按指定编码解码"的行为一致
    }

    /// <summary>喂入一段原始字节,返回本次可确定的文本(尚不可确定时返回空串)。</summary>
    public string Push(byte[] buffer, int count)
    {
        if (count <= 0)
            return "";
        if (_decoder is not null)
        {
            _sample.AddRange(buffer.AsSpan(0, count));
            return DecodePending(flush: false); // 编码已定:按状态化 Decoder 续解,不劈坏跨块多字节字符
        }
        if (!_asciiOnly)
        {
            _sample.AddRange(buffer.AsSpan(0, count));
            return LockIfReady() ? DecodePending(flush: false) : "";
        }

        var ambiguous = -1;
        for (var i = 0; i < count; i++)
        {
            if (buffer[i] < 0x80 && buffer[i] != 0)
                continue;
            ambiguous = i; // 高位字节或 NUL:编码出现歧义,转入样本攒取
            break;
        }
        if (ambiguous < 0)
            return Encoding.UTF8.GetString(buffer, 0, count); // 整块纯 ASCII

        _asciiOnly = false;
        var head = ambiguous > 0 ? Encoding.UTF8.GetString(buffer, 0, ambiguous) : "";
        _sample.AddRange(buffer.AsSpan(ambiguous, count - ambiguous));
        return head + (LockIfReady() ? DecodePending(flush: false) : "");
    }

    /// <summary>输出流结束:未锁定则按现有样本判定,残余字节一并解出(尾部不完整序列以替换符收场)。</summary>
    public string Flush()
    {
        if (_decoder is null)
        {
            if (_asciiOnly)
                return ""; // 全程纯 ASCII:已即时出完
            if (!Lock())
                return "";
        }
        return DecodePending(flush: true);
    }

    private bool LockIfReady()
    {
        if (_decoder is not null)
            return false;
        var span = CollectionsMarshal.AsSpan(_sample);
        if (span.Length >= SampleCap)
            return Lock();
        // 无高位字节(仅 NUL 等)不足以判定 UTF-16 与 UTF-8/GBK 之别:继续攒到流结束或出现高位字节。
        // 例外:UTF-16 的纯 ASCII 输出(部分 PowerShell/cmd 工具)只有 NUL 与 ASCII 交织,永远等不来
        // 高位字节——NUL 分布已达 Detect 的 UTF-16 判据时即锁定,否则实时预览被静默压到 64KB
        if (span.IndexOfAnyInRange((byte)0x80, (byte)0xFF) < 0)
            return LooksLikeUtf16(span) && Lock();
        // 尾部是被截断的 UTF-8 序列:等后续字节到齐再判定,避免误判成 GBK
        if (HasTruncatedUtf8Tail(span))
            return false;
        return Lock();
    }

    /// <summary>NUL 分布是否已满足 UTF-16 判据(与 OutputTextDecoder.Detect 同规则)。</summary>
    private static bool LooksLikeUtf16(ReadOnlySpan<byte> span)
    {
        if (span.Length < 4)
            return false;
        int even = 0, odd = 0;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != 0)
                continue;
            if ((i & 1) == 0)
                even++;
            else
                odd++;
        }
        return (odd > even && odd * 8 > span.Length) || (even > odd && even * 8 > span.Length);
    }

    private bool Lock()
    {
        if (_sample.Count == 0)
            return false;
        var (encoding, bom) = OutputTextDecoder.Detect(_sample.ToArray());
        _decoder = encoding.GetDecoder();
        _bomSkip = bom;
        return true;
    }

    private string DecodePending(bool flush)
    {
        var bytes = _sample.ToArray();
        _sample.Clear();
        var skip = _bomSkip;
        _bomSkip = 0;
        if (bytes.Length <= skip)
            return "";
        var decoder = _decoder!;
        var chars = new char[decoder.GetCharCount(bytes, skip, bytes.Length - skip, flush)];
        var count = decoder.GetChars(bytes, skip, bytes.Length - skip, chars, 0, flush);
        return new string(chars, 0, count);
    }

    /// <summary>样本尾部是否为待续的 UTF-8 多字节序列(UTF-8 起始字节 + 不足的续字节)。</summary>
    private static bool HasTruncatedUtf8Tail(ReadOnlySpan<byte> span)
    {
        var limit = Math.Min(4, span.Length);
        for (var back = 1; back <= limit; back++)
        {
            var b = span[^back];
            if ((b & 0xC0) == 0x80)
                continue; // 续字节:继续往前找起始字节
            var expected = b switch
            {
                >= 0xF0 and <= 0xF4 => 4,
                >= 0xE0 and <= 0xEF => 3,
                >= 0xC2 and <= 0xDF => 2,
                _ => 1, // ASCII 或非法 UTF-8 起始字节(如 GBK 首字节):不构成待续
            };
            return expected > back;
        }
        return false;
    }
}