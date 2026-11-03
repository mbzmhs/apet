using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using APet.Framework.LLM;
using APet.Framework.Logging;
using APet.Framework.Models;
using APet.Framework.Plugin;
using APet.Framework.Session;
using APet.Framework.Tools;
using APet.Plugins.EmotionTags;
using System.Threading.Channels;
using AgentClass = APet.Framework.Agent.Agent;

namespace APet.Plugins.Tts;

/// <summary>TTS 触发时机。</summary>
public enum TtsTiming
{
    /// <summary>流式过程中按句合成(低延迟;工具调用轮的过渡文本也会被读出)。</summary>
    PerSentence,

    /// <summary>整条回复结束后一次性合成(只读最终对话内容,不含工具调用轮)。</summary>
    AfterReply,
}

/// <summary>一段合成完成的音频。</summary>
public sealed class TtsAudioEventArgs : EventArgs
{
    public TtsAudioEventArgs(string text, string voice, string format, byte[] audio)
    {
        Text = text;
        Voice = voice;
        Format = format;
        Audio = audio;
    }

    /// <summary>合成时剥离情感标签后的文本。</summary>
    public string Text { get; }

    /// <summary>本段使用的 voice(情感)。</summary>
    public string Voice { get; }

    /// <summary>音频格式(与 response_format 设置一致)。</summary>
    public string Format { get; }

    /// <summary>音频数据。</summary>
    public byte[] Audio { get; }
}

/// <summary>
/// TTS 客户端插件:对接 OpenAI 兼容 TTS 服务(fast-tts-server /v1/audio/speech)。
/// 对普通对话内容(非工具调用轮)进行语音合成:
/// - 触发时机可设为按句(流式过程中句尾即合成)或整条回复完成后合成;
/// - 文本中的情感标签(如 [happy],括号风格由桌宠插件决定,TTS 主动从桌宠读取)会切换其后文本使用的 voice(不校验是否存在),
///   每次回复结束后归位到默认 voice 设置;
/// - 合成在后台线程按入队顺序执行,结果经 AudioReady 事件交给宿主播放。
/// </summary>
public sealed class TtsClientPlugin : AgentPluginBase
{
    private const string SentenceEnders = "。!?！？;；\n."; // 注意:… 不在此列 —— 省略号停顿交给模型原生渲染,若在此切分,片段尾会叠加缩放静音导致停顿过长

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private readonly Channel<(string Text, string Voice)> _queue =
        Channel.CreateUnbounded<(string Text, string Voice)>();
    private CancellationTokenSource? _workerCts;
    private string _currentVoice = ""; // 初始值随即被 Initialize 的 EngineVoice 覆盖;空 = 引擎默认声线

    // 流式缓冲:原始未处理文本 / 按句模式的句尾残留 / AfterReply 模式的整段积累
    private readonly StringBuilder _pendingText = new();
    private readonly StringBuilder _afterReplyText = new();
    private string _sentenceTail = "";
    private int _turnEnqueued; // 本轮已入队句数(轮收口日志用)
    private bool _gateRejectedLogged; // 本轮门禁拒绝已记录(每轮一条,避免逐 delta 刷屏)

    public override string Name => "tts-client";

    public override void Initialize()
    {
        _currentVoice = EngineVoice;
        _workerCts = new CancellationTokenSource();
        _ = Task.Run(() => WorkerAsync(_workerCts.Token));
        Log.Global.Info($"[TtsClient] initialized (engine={Engine}, server={ServerUrl}, model='{Model}', serverVoice='{ServerVoice}', gsvVoice='{GsvVoice}', timing={Timing}, skipInnerThought={SkipInnerThought})");
    }

    public override void Dispose()
    {
        _workerCts?.Cancel();
        ResetLocalEngines(waitForWorker: true); // 插件停用/应用退出:等 worker 让出锁后收净本地引擎
        Log.Global.Info("[TtsClient] disposed");
    }

    protected override void OnSettingChanged(string name)
    {
        if (name is "serverVoice" or "gsvVoice")
            _currentVoice = EngineVoice; // 默认 voice 变更即时生效
        if (name is "gsvModelDir" or "gsvDevice")
            ResetLocalEngines(); // 模型包/计算设备变更:重建本地引擎(懒加载,下次合成生效)
        if (name == "engine" && Engine != "gsv")
            ResetLocalEngines(); // 切走 GSV 即卸载本地引擎,释放显存(切回时懒加载重建);
                                 // 按值判断:单条 SetSetting 未变化也会触发回调,不可无条件重置
        if (name is "gsvCutMute" or "gsvCutMinlen")
        {
            try { ApplyGsvPauseSettings(); } // 标点停顿参数:引擎存在时即时生效
            catch { /* 引擎尚未创建:下次 EnsureGsvEngine 时应用 */ }
        }
        if (name == "gsvVoices")
            _gsvVoices = null; // 声线列表变更:重解析(引擎无需重建,按需 LoadModel/预热)
    }

    // ───────── 设置(组合框:engine/timing/responseFormat/tagStyle) ─────────

    /// <summary>合成引擎:server = OpenAI 兼容 HTTP 服务;windows = 微软系统自带语音(SAPI);
    /// gsv = GSV-TTS ONNX 本地推理引擎(模型包目录 + 用户提供的参考音频)。</summary>
    [PluginSetting("engine", Description = "Synthesis engine: server (custom HTTP server) / windows (Microsoft system voices) / gsv (local GSV-TTS ONNX)",
        Options = new[] { "server", "windows", "gsv" })]
    public string Engine { get; set; } = "server";

    [PluginSetting("serverUrl", Description = "TTS server base URL (OpenAI-compatible /v1/audio/speech)", MinLength = 1, MaxLength = 256)]
    public string ServerUrl { get; set; } = "http://127.0.0.1:9883";

    [PluginSetting("model", Description = "TTS role/model (empty = server active voice)", MinLength = 0, MaxLength = 128)]
    public string Model { get; set; } = "";

    /// <summary>当前引擎的默认 voice:server 用服务端音色,gsv 用 GSV 默认声线,windows 无默认。</summary>
    private string EngineVoice => Engine switch
    {
        "gsv" => GsvVoice,
        "server" => ServerVoice,
        _ => "",
    };

    [PluginSetting("serverVoice", Description = "Server engine: voice/emotion sent as the request 'voice' parameter", MinLength = 0, MaxLength = 128)]
    public string ServerVoice { get; set; } = "neutral";

    [PluginSetting("gsvVoice", Description = "GSV engine: default voice (name or name/emotion); empty = engine default (first configured voice)", MinLength = 0, MaxLength = 128)]
    public string GsvVoice { get; set; } = "";

    /// <summary>Windows 引擎:系统语音角色名(空 = 系统默认角色)。语速由 speed 设置映射。</summary>
    [PluginSetting("sapiVoice", Description = "Windows engine: system voice name (empty = system default)", MinLength = 0, MaxLength = 128)]
    public string SapiVoice { get; set; } = "";

    /// <summary>GSV 引擎:模型包目录(含 manifest.json,如 gsv-onnx-pkg)。</summary>
    [PluginSetting("gsvModelDir", Description = "GSV engine: model package folder containing manifest.json", MinLength = 0, MaxLength = 512)]
    public string GsvModelDir { get; set; } = "";

    /// <summary>GSV 引擎计算设备:auto(有可用 GPU 则用,否则 CPU)/ gpu / cpu。
    /// gpu 需要 cuDNN(cudnn64_9.dll 等)在程序目录或 PATH;缺失时 ONNX Runtime 自动降级 CPU。</summary>
    [PluginSetting("gsvDevice", Description = "GSV engine device: auto / gpu (cuda) / cpu", Options = new[] { "auto", "gpu", "cpu" })]
    public string GsvDevice { get; set; } = "auto";

    /// <summary>GSV 标点停顿基长(秒):每个切块合成后追加的真实静音 = 此值 ÷ 语速 ×
    /// 尾标点缩放(省略号 2.0 / 句读 1.5 / 顿号 0.8 / 其余 1.0)。逗号、句号停顿的本体。</summary>
    [PluginSetting("gsvCutMute", Description = "GSV chunk pause base seconds (default 0.3; divided by speed, scaled by tail punctuation)")]
    public double GsvCutMute { get; set; } = 0.3;

    /// <summary>GSV 子句合并阈值(语义字数,中日字=1、英文单词=1.75):累积到阈值才切块。
    /// 默认 0 = 每个标点都切块;调大可减少切块、降低合成耗时。</summary>
    [PluginSetting("gsvCutMinlen", Description = "GSV clause merge threshold in semantic chars (default 0 = cut at every punctuation)")]
    public double GsvCutMinlen { get; set; } = 0;

    /// <summary>GSV 已配置声线(JSON 数组):[{"name":"音色名(=voices/模型名)","refs":[{"emotion","wav","text","lang"}]}]。
    /// 在设置页创建/编辑;wav 用绝对路径。</summary>
    [PluginSetting("gsvVoices", Description = "GSV configured voices JSON: [{name, refs:[{emotion,wav,text,lang}]}]", MinLength = 0, MaxLength = 262144)]
    public string GsvVoicesJson { get; set; } = "[]";



    /// <summary>不朗读内心想法:圆括号(全角()/半角 ())包裹的文本视为内心独白,不进行朗读(整对括号连同内容移除)。</summary>
    [PluginSetting("skipInnerThought", Description = "Do not speak inner thoughts: text wrapped in parentheses (full-width/half-width) is skipped")]
    public bool SkipInnerThought { get; set; } = true;

    [PluginSetting("timing", Description = "When to synthesize: per-sentence during streaming, or the whole reply after it finishes")]
    public TtsTiming Timing { get; set; } = TtsTiming.PerSentence;

    [PluginSetting("responseFormat", Description = "Audio format returned by the server",
        Options = new[] { "wav", "mp3", "pcm", "opus", "aac", "flac", "ogg" })]
    public string ResponseFormat { get; set; } = "wav";

    /// <summary>朗读语言(zh/ja/en),与 Agent 回复语言同源,由宿主从设置页"语言"推送。</summary>
    [PluginSetting("language", Description = "Spoken language code (zh/ja/en); matches the agent's reply language",
        Options = new[] { "zh", "ja", "en" })]
    public string Language { get; set; } = "zh";

    [PluginSetting("speed", Description = "Speech speed", Min = 0.1, Max = 4.0)]
    public double Speed { get; set; } = 1.0;

    [PluginSetting("temperature", Description = "GPT sampling temperature", Min = 0.1, Max = 1.5)]
    public double Temperature { get; set; } = 1.0;

    [PluginSetting("topK", Description = "GPT top_k", Min = 1, Max = 50)]
    public int TopK { get; set; } = 15;

    [PluginSetting("topP", Description = "GPT top_p", Min = 0.1, Max = 1.0)]
    public double TopP { get; set; } = 1.0;

    [PluginSetting("repetitionPenalty", Description = "Repetition penalty", Min = 1.0, Max = 2.0)]
    public double RepetitionPenalty { get; set; } = 1.35;

    [PluginSetting("noiseScale", Description = "SoVITS noise scale", Min = 0.1, Max = 1.0)]
    public double NoiseScale { get; set; } = 0.5;

    /// <summary>情感 tag 的括号风格来源(宿主注入桌宠的 tagStyle;未注入时按方括号解析)。</summary>
    public Func<PetTagStyle>? TagStyleProvider { get; set; }

    /// <summary>情绪联动闸门(宿主注入):桌宠禁用时情感标签无从驱动,本插件即便配置为启用也跳过全部合成逻辑
    /// (未注入同样跳过,与"桌宠未接线"同义)。</summary>
    public Func<bool>? EmotionSyncGate { get; set; }

    /// <summary>门禁拒绝原因描述(宿主注入,仅诊断日志用;被拒时按轮首现调用一次)。</summary>
    public Func<string>? EmotionSyncGateDetail { get; set; }

    /// <summary>测试/自定义合成接缝:设置后替代 HTTP 调用(参数 text, voice → 音频字节)。</summary>
    internal Func<string, string, Task<byte[]>>? SynthesizeOverride;

    /// <summary>一段音频合成完成;宿主订阅此事件播放 Audio。</summary>
    public event EventHandler<TtsAudioEventArgs>? AudioReady;

    /// <summary>一段文本合成失败(text 为原文本,异常已记录日志)。</summary>
    public event EventHandler<string>? SpeakFailed;

    /// <summary>全部语音朗读完毕(队列空且最后一段播放结束);宿主用于驱动气泡等朗读期展示。</summary>
    public event EventHandler? SpeechDrained;

    private int _activeSpeech; // 正在播放的语音段数(宿主播放前后维护)

    /// <summary>是否仍有语音待播或正在朗读;气泡等在朗读期间应保持展示。</summary>
    public bool HasActiveSpeech => _queue.Reader.Count > 0 || Volatile.Read(ref _activeSpeech) > 0;

    /// <summary>宿主开始播放一段语音时调用(播放结束须配对调用 TrackSpeechEnd)。</summary>
    public void TrackSpeechBegin() => Interlocked.Increment(ref _activeSpeech);

    /// <summary>宿主一段语音播放结束:队列空且无在播段时触发 SpeechDrained。</summary>
    public void TrackSpeechEnd()
    {
        if (Interlocked.Decrement(ref _activeSpeech) == 0 && _queue.Reader.Count == 0)
            SpeechDrained?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 流式文本中解析到一个情感标签时触发(携带拆解后的 tag 名,如 "happy",不含包裹符)。
    /// 宿主可据此驱动桌宠切表情——由 TTS 决定时机,与发声严格同步。
    /// </summary>
    public event Action<string>? EmotionTag;

    // ───────── 消息链钩子:只处理内容增量,跳过工具调用轮 ─────────

    public override void OnStreamDelta(AgentClass agent, AgentSession session, StreamDeltaContext delta)
    {
        // 桌宠禁用:情感标签无从驱动、语音不完整——即便本插件配置为启用也跳过全部合成逻辑
        // (设置项保持原状;桌宠启停在运行期间被宿主拦截,轮内状态稳定)。
        // 拒绝只记一条(每轮首现时):TTS 静默曾无从排查,这里给出唯一静默出口的实况
        if (EmotionSyncGate?.Invoke() != true)
        {
            if (!_gateRejectedLogged)
            {
                _gateRejectedLogged = true;
                // 带宿主提供的拒绝原因(桌宠 null / Enabled=false),TTS 静默曾无从排查
                Log.Global.Info($"[TtsClient] gate rejected: {EmotionSyncGateDetail?.Invoke() ?? "未接线"},本轮跳过全部语音合成");
            }
            return;
        }

        switch (delta.Kind)
        {
            case StreamDeltaKind.Content:
                ProcessContentText(delta.Text);
                break;

            case StreamDeltaKind.Finished when delta.SourceEvent is FinishedEvent { Reason: FinishReason.ToolCalls }:
                ResetTurn(); // 工具调用轮:丢弃过渡文本,voice 归位
                break;

            case StreamDeltaKind.Finished:
                CompleteTurn();
                break;
        }
    }

    // ───────── 流式文本处理:标签切 voice + 按句/整段切分 ─────────

    internal string CurrentVoice => _currentVoice;

    internal void ProcessContentText(string text)
    {
        _pendingText.Append(text);
        var raw = _pendingText.ToString();

        var spoken = new StringBuilder();
        var pos = 0;
        foreach (Match match in TagRegex().Matches(raw))
        {
            spoken.Append(raw, pos, match.Index - pos);
            var voice = match.Groups[1].Value.Trim();
            if (voice.Length > 0)
            {
                _currentVoice = voice;
                EmotionTag?.Invoke(voice); // 通知宿主此刻切到该情感(驱动桌宠,与发声同步)
            }
            pos = match.Index + match.Length;
        }

        var tail = raw[pos..];
        var holdIndex = EmotionTagCodec.IndexOfPartialTag(tail);
        if (holdIndex >= 0)
        {
            spoken.Append(tail, 0, holdIndex);
            _pendingText.Clear();
            _pendingText.Append(tail[holdIndex..]); // 可能是未收完的标签,留待后续增量
        }
        else
        {
            spoken.Append(tail);
            _pendingText.Clear();
        }

        // 内心想法剥离:圆括号内文本不朗读;尾部未闭合的开括号起扣留,等后续增量闭合
        var (spokenText, innerHold) = SplitInnerThought(spoken.ToString());
        if (innerHold is not null)
            _pendingText.Append(innerHold);

        HandleSpoken(spokenText);
    }

    /// <summary>一轮输出结束(Stop/Length/ContentFilter):按时机交付剩余文本,voice 归位。</summary>
    internal void CompleteTurn()
    {
        // 未收完的标签残片按普通文本处理;到轮末仍未闭合的括号内容视为内心独白,不读
        var tail = _pendingText.ToString();
        _pendingText.Clear();
        var (spokenTail, _) = SplitInnerThought(tail);
        HandleSpoken(spokenTail);

        if (Timing == TtsTiming.PerSentence)
        {
            Enqueue(_sentenceTail);
            _sentenceTail = "";
        }
        else
        {
            Enqueue(_afterReplyText.ToString());
            _afterReplyText.Clear();
        }

        _currentVoice = EngineVoice; // 对话结束,voice 归位(当前引擎自己的默认)

        // 轮收口实况:入队句数 + 残留量。enqueued=0 而 tail>0 → 句尾残留从未刷出;
        // enqueued=0 且 tail=0 → 文本全被标签/内心独白剥离;本行缺失 → 流式 Finished 未到达或门禁拒绝
        Log.Global.Info($"[TtsClient] turn complete: enqueued={_turnEnqueued}, tail={_sentenceTail.Length} chars, pending={_pendingText.Length} chars");
        _turnEnqueued = 0;
        _gateRejectedLogged = false;
    }

    /// <summary>工具调用轮:丢弃已积累文本(非对话内容),voice 归位。</summary>
    internal void ResetTurn()
    {
        _pendingText.Clear();
        _afterReplyText.Clear();
        _sentenceTail = "";
        _currentVoice = EngineVoice;
        _turnEnqueued = 0;
        _gateRejectedLogged = false;
    }

    private void HandleSpoken(string spokenText)
    {
        if (spokenText.Length == 0)
            return;

        if (Timing == TtsTiming.AfterReply)
        {
            _afterReplyText.Append(spokenText);
            return;
        }

        // 按句:与上一轮句尾残留拼接;连续句读符(如"……" "?!")合并为一个切分点,
        // 纯句读/空白段(如句首孤立的"……")不单独成句,并入下一句
        var combined = _sentenceTail + spokenText;
        var start = 0;
        for (var i = 0; i < combined.Length; i++)
        {
            if (!SentenceEnders.Contains(combined[i]))
                continue;
            while (i + 1 < combined.Length && SentenceEnders.Contains(combined[i + 1]))
                i++;

            var segment = combined[start..(i + 1)];
            if (HasSpeakableContent(segment))
            {
                Enqueue(segment);
                start = i + 1;
            }
        }
        _sentenceTail = combined[start..];
    }

    /// <summary>剥离引擎无法发音的字符(emoji/符号/控制符,按 Unicode Rune 判断,保留各语言
    /// 字母数字与中英标点)。gsvtts 的 G2P 对「剥离后无可发音内容」的文本存在原生缺陷
    /// (纯 emoji 段实测原生堆崩溃),故在入队前统一清洗,清洗后无内容则不送合成。</summary>
    internal static string StripUnspeakable(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            // 空格必须保留:JA/EN 文本里空格承担词边界,剥掉会把相邻词熔成一个大词
            if (rune.Value == '\n' || rune.Value == ' ' || SpeakablePunct.Contains(rune.Value) || Rune.IsLetterOrDigit(rune))
                sb.Append(rune.ToString());
            // 其余(emoji/符号/控制符/私有区)直接剥离
        }
        return sb.ToString();
    }

    private static readonly HashSet<int> SpeakablePunct = new(
        ".,!?;:'\"()-_&%+*/@#$^=<>|`".Select(c => (int)c)
        .Concat("。，、；…—–~·￥！？：《》〈〉「」『』[]【】“”‘’".Select(c => (int)c)));

    /// <summary>段落中是否含有可发音内容(非句读符且非空白)。</summary>
    private static bool HasSpeakableContent(string segment)
    {
        foreach (var ch in segment)
        {
            if (!SentenceEnders.Contains(ch) && !char.IsWhiteSpace(ch))
                return true;
        }
        return false;
    }

    private void Enqueue(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return;

        // 防御性兜底:任何路径漏剥的内心括号在此二次剥离,保证括号内容绝不进入合成队列
        var (spoken, hold) = SplitInnerThought(trimmed);
        if (hold is not null)
        {
            // 到入队时仍未闭合的括号,视为内心独白整段丢弃
            Log.Global.Info($"[TtsClient] enqueue hold dropped: {hold[..Math.Min(hold.Length, 40)]}");
        }
        spoken = StripUnspeakable(spoken).Trim();
        // 清洗后纯句读/空白残留(如轮末孤立"……"、纯 emoji 段)无可发音内容,不送合成
        if (spoken.Length == 0 || !HasSpeakableContent(spoken))
            return;

        Log.Global.Info($"[TtsClient] enqueue: \"{spoken}\" (voice={_currentVoice})");
        _turnEnqueued++;
        _queue.Writer.TryWrite((spoken, _currentVoice));
    }

    private Regex TagRegex()
    {
        // 括号风格由桌宠决定:主动从桌宠读取(未接线时按方括号);解析规则统一在 EmotionTagCodec
        return EmotionTagCodec.CreateRegex(TagStyleProvider?.Invoke() ?? PetTagStyle.SquareBrackets);
    }

    /// <summary>
    /// 内心想法剥离:圆括号(全角()/半角 ())内的文本不朗读,整对括号连同内容移除
    /// (允许嵌套与全半角混用,按深度计数)。返回剥离后的文本;
    /// hold 为尾部未闭合开括号起的原文——流式时应扣留待闭合,轮末仍未闭合则整段视为内心不读。
    /// </summary>
    internal (string Spoken, string? Hold) SplitInnerThought(string text)
    {
        if (!SkipInnerThought || text.Length == 0)
            return (text, null);

        var sb = new StringBuilder(text.Length);
        var depth = 0;
        var holdStart = -1; // 最近一次进入括号的位置(未闭合时从这里扣留)
        const char halfOpen = '(';
        const char halfClose = ')';
        const char fullOpen = '\uFF08';   // 全角开括号
        const char fullClose = '\uFF09';  // 全角闭括号
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (depth > 0)
            {
                if (ch is halfOpen or fullOpen) depth++;
                else if (ch is halfClose or fullClose) depth--;
                continue;
            }
            if (ch is halfOpen or fullOpen)
            {
                depth = 1;
                holdStart = i;
                continue;
            }
            sb.Append(ch);
        }

        return depth > 0 ? (sb.ToString(), text[holdStart..]) : (sb.ToString(), null);
    }

    // ───────── 后台合成线程 ─────────

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            while (await _queue.Reader.ReadAsync(ct) is { } item)
            {
                // 出队即计入朗读活动:HasActiveSpeech 在"队列→播放"的交替间隙不留空窗
                TrackSpeechBegin();
                try
                {
                    var (audio, format) = SynthesizeOverride is not null
                        ? (await SynthesizeOverride(item.Text, item.Voice), ResponseFormat)
                        : await SpeakOnceAsync(item.Text, item.Voice, ct);
                    AudioReady?.Invoke(this, new TtsAudioEventArgs(item.Text, item.Voice, format, audio));
                    // 计数由宿主在播放结束(含放弃播放)时 TrackSpeechEnd 归还
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TrackSpeechEnd(); // 插件停用:已计入的段必须归还,否则计数泄漏卡住后续朗读判定
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Global.Error($"[TtsClient] speak failed (engine={Engine}, voice={item.Voice}): {ex.Message}");
                    SpeakFailed?.Invoke(this, item.Text);
                    // 合成失败没有真实音频:按字数估算朗读时长占位,结束时照常触发
                    // SpeechDrained(情绪切回待机、气泡启动隐藏),不会无限卡住
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(EstimateSpeechMs(item.Text), ct);
                        }
                        catch (OperationCanceledException) { }
                        finally
                        {
                            TrackSpeechEnd();
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>按字数估算朗读时长(约 4.5 字/秒,下限 0.8 秒,上限 30 秒)。</summary>
    private static int EstimateSpeechMs(string text) =>
        Math.Clamp(text.Count(c => !char.IsWhiteSpace(c)) * 220, 800, 30000);

    private async Task<(byte[] Audio, string Format)> SpeakOnceAsync(string text, string voice, CancellationToken ct)
    {
        return Engine.Trim().ToLowerInvariant() switch
        {
            "windows" => await Task.Run(() => SpeakWindows(text, voice), ct),
            "gsv" => await Task.Run(() => SpeakGsv(text, voice, ct), ct),
            _ => await SpeakServerAsync(text, voice, ct),
        };
    }

    /// <summary>server 引擎:OpenAI 兼容 HTTP 服务(原有路径),格式由 responseFormat 设置决定。</summary>
    private async Task<(byte[] Audio, string Format)> SpeakServerAsync(string text, string voice, CancellationToken ct)
    {
        var endpoint = ServerUrl.TrimEnd('/') + "/v1/audio/speech";
        var body = JsonSerializer.Serialize(new
        {
            model = Model,
            input = text,
            voice,
            language = Language,
            response_format = ResponseFormat,
            speed = Speed,
            temperature = Temperature,
            top_k = TopK,
            top_p = TopP,
            repetition_penalty = RepetitionPenalty,
            noise_scale = NoiseScale,
            stream = false,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await s_http.SendAsync(request, ct);
        var payload = await response.Content.ReadAsByteArrayAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 300))}");
        return (payload, ResponseFormat);
    }

    // ───────── windows 引擎:微软系统自带语音(System.Speech / SAPI) ─────────

    private readonly object _sapiLock = new(); // _sapi 的使用(SpeakWindows)与释放(ResetLocalEngines)同锁,防释放/使用竞态
    private System.Speech.Synthesis.SpeechSynthesizer? _sapi;
    private HashSet<string>? _sapiVoiceNames; // 已安装角色名(小写),判断 tag 字符串能否切换角色

    /// <summary>windows 引擎:SelectVoice(角色)+ 语速(speed → SAPI Rate 映射),输出 16kHz WAV。</summary>
    private (byte[] Audio, string Format) SpeakWindows(string text, string voice)
    {
        lock (_sapiLock)
        {
            _sapi ??= new System.Speech.Synthesis.SpeechSynthesizer();
            _sapiVoiceNames ??= new HashSet<string>(
                _sapi.GetInstalledVoices().Select(v => v.VoiceInfo.Name),
                StringComparer.OrdinalIgnoreCase);

            // 角色选择:优先设置项;标签字符串恰好命中已安装角色名时也可临时切换
            var name = SapiVoice.Trim();
            if (name.Length == 0 || !_sapiVoiceNames.Contains(name))
                name = _sapiVoiceNames.Contains(voice) ? voice : "";
            if (name.Length > 0)
                _sapi.SelectVoice(name);
            _sapi.Rate = MapSapiRate(Speed);

            using var ms = new MemoryStream();
            _sapi.SetOutputToWaveStream(ms);
            try
            {
                _sapi.Speak(text);
            }
            finally
            {
                _sapi.SetOutputToDefaultAudioDevice(); // 复位输出目标,避免下次合成静音
            }
            return (ms.ToArray(), "wav");
        }
    }

    /// <summary>语速倍率 → SAPI Rate(-10~10 整数,对数映射:0.5×→-4、1×→0、2×→+4)。</summary>
    internal static int MapSapiRate(double speed) =>
        Math.Clamp((int)Math.Round(Math.Log2(Math.Clamp(speed, 0.1, 4.0)) * 4), -10, 10);

    // ───────── gsv 引擎:GSV-TTS ONNX 本地推理(P/Invoke gsvtts.dll) ─────────

    private readonly object _gsvLock = new();
    private volatile bool _gsvResetPending; // 合成进行中收到重建请求:延迟到 worker 归还锁后执行(防 UI 冻在秒级推理上)
    private Gsv.GsvEngine? _gsv;
    private string _gsvLoadedKey = ""; // 引擎创建参数指纹(modelDir|dllDir),变更即重建
    private readonly HashSet<string> _gsvWarmed = new(StringComparer.OrdinalIgnoreCase); // 已预热的 ref("voice/emotion")
    private string _gsvCurrentVoice = ""; // 引擎当前驻留音色(单驻留:切换时 LoadModel 并清 ref 缓存)
    private List<GsvVoiceConfig>? _gsvVoices; // 已配置声线(gsvVoices JSON 解析缓存)

    /// <summary>已配置声线列表(gsvVoices 设置解析;声线 = 模型名 + 参考列表)。</summary>
    internal List<GsvVoiceConfig> GsvVoices => _gsvVoices ??= ParseGsvVoices(GsvVoicesJson);

    internal sealed class GsvVoiceConfig
    {
        public string Name = "";
        public string Sovits = ""; // 模型包 voices/ 下的目录名;空 = 底模
        public string Gpt = "";    // 模型包 gpt/ 下的目录名;空 = 底模
        public Gsv.GsvSampling? Sampling; // 声线级采样;null = 跟随全局设置
        public List<GsvRefConfig> Refs = new();
    }


    internal sealed class GsvRefConfig
    {
        public string Emotion = "neutral";
        public string Wav = "";
        public string Text = "";
        public string Lang = "zh";
    }

    internal static List<GsvVoiceConfig> ParseGsvVoices(string json)
    {
        // 宿主序列化是 PascalCase,这里大小写不敏感反序列化,两种命名都兼容
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            var dtos = JsonSerializer.Deserialize<List<GsvVoiceDto>>(
                string.IsNullOrWhiteSpace(json) ? "[]" : json, opts) ?? new List<GsvVoiceDto>();
            var list = new List<GsvVoiceConfig>();
            foreach (var d in dtos)
            {
                if (string.IsNullOrWhiteSpace(d.Name))
                    continue;
                var vc = new GsvVoiceConfig
                {
                    Name = d.Name.Trim(),
                    Sovits = d.Sovits?.Trim() ?? "",
                    Gpt = d.Gpt?.Trim() ?? "",
                };
                if (d.Sampling is not null)
                {
                    vc.Sampling = new Gsv.GsvSampling
                    {
                        Temperature = (float)Math.Clamp(d.Sampling.Temperature, 0.1, 1.5),
                        TopK = Math.Clamp(d.Sampling.TopK, 1, 50),
                        TopP = (float)Math.Clamp(d.Sampling.TopP, 0.1, 1.0),
                        RepetitionPenalty = (float)Math.Clamp(d.Sampling.RepetitionPenalty, 1.0, 2.0),
                        NoiseScale = (float)Math.Clamp(d.Sampling.NoiseScale, 0.1, 1.0),
                    };
                }
                foreach (var r in d.Refs)
                {
                    if (string.IsNullOrWhiteSpace(r.Wav))
                        continue;
                    vc.Refs.Add(new GsvRefConfig
                    {
                        Emotion = string.IsNullOrWhiteSpace(r.Emotion) ? "neutral" : r.Emotion,
                        Wav = r.Wav,
                        Text = r.Text ?? "",
                        Lang = string.IsNullOrWhiteSpace(r.Lang) ? "zh" : r.Lang,
                    });
                }
                list.Add(vc);
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Global.Error("[TtsClient] GSV voices JSON parse failed: " + ex.Message);
            return new List<GsvVoiceConfig>();
        }
    }

    internal sealed class GsvVoiceDto
    {
        public string Name { get; set; } = "";
        public string? Sovits { get; set; }
        public string? Gpt { get; set; }
        public GsvSamplingDto? Sampling { get; set; }
        public List<GsvRefDto> Refs { get; set; } = new();
    }

    internal sealed class GsvSamplingDto
    {
        public double Temperature { get; set; } = 1.0;
        public int TopK { get; set; } = 15;
        public double TopP { get; set; } = 1.0;
        public double RepetitionPenalty { get; set; } = 1.35;
        public double NoiseScale { get; set; } = 0.5;
    }

    internal sealed class GsvRefDto
    {
        public string Emotion { get; set; } = "neutral";
        public string Wav { get; set; } = "";
        public string Text { get; set; } = "";
        public string Lang { get; set; } = "zh";
    }

    /// <summary>GSV voice 状态解析规则:_currentVoice 与标签 voice 都是自由字符串,
    /// 含"/"= "音色/情绪";纯已配置音色名 = 切音色(情绪不变);其余 = 默认音色 + 该情绪。</summary>
    internal (string Voice, string Emotion) ResolveGsvRef(string voice)
    {
        var (curV, curE) = SplitGsvVoice(_currentVoice);
        var (v, e) = SplitGsvVoice(voice);
        if (!voice.Contains('/') && GsvVoices.Any(x => x.Name.Equals(voice.Trim(), StringComparison.OrdinalIgnoreCase)))
            e = curE; // 纯音色名:保持情绪
        // 兜底:解析出的声线未配置(voice 设置无效/已删)→ 用第一个已配置声线
        if (!GsvVoices.Any(x => x.Name.Equals(v, StringComparison.OrdinalIgnoreCase)) && GsvVoices.Count > 0)
            v = GsvVoices[0].Name;
        return (v, e);
    }

    /// <summary>拆 "音色/情绪";纯已配置音色名 → (它, neutral);其余非空 → (默认音色, 它);空 → 默认音色/neutral。</summary>
    private (string Voice, string Emotion) SplitGsvVoice(string s)
    {
        s = s.Trim();
        if (s.Contains('/'))
        {
            var i = s.IndexOf('/');
            return (s[..i].Trim(), s[(i + 1)..].Trim());
        }
        if (s.Length == 0)
            return (GsvDefaultVoice, "neutral");
        if (GsvVoices.Any(x => x.Name.Equals(s, StringComparison.OrdinalIgnoreCase)))
            return (s, "neutral");
        return (GsvDefaultVoice, s); // 视为情绪名
    }

    /// <summary>GSV 默认音色:gsvVoice 设置值的主段(含"/"取"/"前段,否则原样)。</summary>
    private string GsvDefaultVoice
    {
        get
        {
            var v = GsvVoice.Trim();
            var i = v.IndexOf('/');
            return i > 0 ? v[..i].Trim() : v;
        }
    }

    /// <summary>gsv 引擎:确保引擎就绪 → 解析声线/情绪 → 按需 LoadModel(单驻留)与预热 → 合成封装 WAV。</summary>
    private (byte[] Audio, string Format) SpeakGsv(string text, string voice, CancellationToken ct)
    {
        lock (_gsvLock)
        {
            ct.ThrowIfCancellationRequested();
            Log.Global.Info($"[TtsClient] gsv synth: \"{text}\"");
            EnsureGsvEngine();
            var (v, e) = ResolveGsvRef(voice);
            var vc = GsvVoices.FirstOrDefault(x => x.Name.Equals(v, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException($"GSV: 未配置声线 '{v}'(设置页添加声线后使用)");
            if (vc.Refs.Count == 0)
                throw new InvalidOperationException($"GSV: 声线 '{v}' 还没有参考音频(设置页添加)");

            // 引擎单驻留:切换声线 = LoadModel(sovits, gpt) 两侧独立解析,空名 = 该侧用底模
            // (引擎自动清空其 ref 缓存;模型包内不再创建任何联接目录)
            if (!string.Equals(_gsvCurrentVoice, v, StringComparison.OrdinalIgnoreCase))
            {
                _gsv!.LoadModel(vc.Sovits.Trim(), vc.Gpt.Trim());
                _gsvCurrentVoice = v;
                _gsvWarmed.Clear();
                Log.Global.Info($"[TtsClient] GSV voice loaded: {v} (sovits='{vc.Sovits.Trim()}', gpt='{vc.Gpt.Trim()}')");
            }

            // 采样按声线:配置了声线级采样用之,否则全局设置
            _gsv!.SetSampling(vc.Sampling ?? new Gsv.GsvSampling
            {
                Temperature = (float)Temperature,
                TopK = TopK,
                TopP = (float)TopP,
                RepetitionPenalty = (float)RepetitionPenalty,
                NoiseScale = (float)NoiseScale,
            });

            // ref 查找:指定情绪 → neutral → 第一条
            var refr = vc.Refs.FirstOrDefault(r => r.Emotion.Equals(e, StringComparison.OrdinalIgnoreCase))
                       ?? vc.Refs.FirstOrDefault(r => r.Emotion.Equals("neutral", StringComparison.OrdinalIgnoreCase))
                       ?? vc.Refs[0];
            e = refr.Emotion;
            var refName = $"{v}/{e}";
            if (!_gsvWarmed.Contains(refName))
            {
                if (!File.Exists(refr.Wav))
                    throw new InvalidOperationException($"GSV: 参考音频不存在:{refr.Wav}");
                _gsv.WarmupRef(refName, refr.Wav, refr.Text, refr.Lang);
                _gsvWarmed.Add(refName);
                Log.Global.Info($"[TtsClient] GSV ref warmed: {refName}");
            }

            var audio = _gsv!.Synthesize(text, Language, (float)Speed, refName);
            Log.Global.Info($"[TtsClient] gsv synth done: \"{text}\" -> {audio.DurationSeconds:F2}s");
            return (WrapWav(audio.Pcm, audio.SampleRate), "wav");
        }
    }

    /// <summary>创建/重建 GSV 引擎:定位 gsvtts.dll → 挂载解析器 → 载模型包 → 解析 voice.json → 应用采样参数。
    /// 采样参数依赖的设置改动(temperature 等)在重建/创建时生效一次。_gsvLock 内调用。</summary>
    private void EnsureGsvEngine()
    {
        if (_gsvResetPending)
        {
            _gsv?.Dispose(); // 延迟收编的重建请求(ResetLocalEngines 忙时挂起):先执行再建新引擎
            _gsv = null;
            _gsvLoadedKey = "";
            _gsvWarmed.Clear();
            _gsvCurrentVoice = "";
            _gsvResetPending = false;
        }
        var modelDir = GsvModelDir.Trim();
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "models"); // 随应用分发的默认模型包(deploy 自动下载到程序旁 models\)
        if (modelDir.Length == 0)
            modelDir = bundledDir;
        if (!File.Exists(Path.Combine(modelDir, "manifest.json")))
            throw new InvalidOperationException(
                string.Equals(modelDir, bundledDir, StringComparison.OrdinalIgnoreCase)
                    ? "GSV: 未设置模型目录,程序目录旁也没有 models\\manifest.json(设置页可指定模型包文件夹)"
                    : $"GSV: {modelDir} 下没有 manifest.json(模型包不完整)");

        var dllDir = LocateGsvDllDir(modelDir) ?? throw new InvalidOperationException(
            "GSV: 找不到 gsvtts.dll(设置页可手动指定引擎 DLL 目录)");
        var key = $"{modelDir}|{dllDir}";
        if (_gsv is not null && _gsvLoadedKey == key)
        {
            _gsv.SetSampling(new Gsv.GsvSampling
            {
                Temperature = (float)Temperature,
                TopK = TopK,
                TopP = (float)TopP,
                RepetitionPenalty = (float)RepetitionPenalty,
                NoiseScale = (float)NoiseScale,
            });
            ApplyGsvPauseSettings();
            return;
        }

        _gsv?.Dispose();
        _gsv = null;
        Gsv.GsvNative.Configure(dllDir);
        var engine = new Gsv.GsvEngine(new Gsv.GsvConfig
        {
            Device = GsvDevice.Trim().ToLowerInvariant() switch
            {
                "gpu" => "cuda",
                "cpu" => "cpu",
                _ => "auto",
            },
            BaseManifest = Path.Combine(modelDir, "manifest.json"),
            OrtLibDir = dllDir,
        });
                engine.SetSampling(new Gsv.GsvSampling
        {
            Temperature = (float)Temperature,
            TopK = TopK,
            TopP = (float)TopP,
            RepetitionPenalty = (float)RepetitionPenalty,
            NoiseScale = (float)NoiseScale,
        });
        _gsv = engine;
        ApplyGsvPauseSettings();
        _gsvLoadedKey = key;
        _gsvWarmed.Clear();
        Log.Global.Info($"[TtsClient] GSV engine ready (model={modelDir}, dll={dllDir})");
    }

    /// <summary>把标点停顿参数(切块静音基长 / 子句合并阈值)推给已创建的 GSV 引擎。</summary>
    private void ApplyGsvPauseSettings()
    {
        if (_gsv is null) return;
        _gsv.SetCutMute(Math.Clamp((float)GsvCutMute, 0f, 2f));
        _gsv.SetCutMinlen(Math.Clamp((float)GsvCutMinlen, 0f, 100f));
    }

    // ───────── $warmup 指令:预热全部声线的参考音频 ─────────

    private int _warmingUp; // $warmup 进行中标记(防重入)

    /// <summary>本次预热由哪个会话发起($warmup 命令携带):宿主据此把 Web 进度事件路由到该会话;非会话触发为 null。</summary>
    public string? WarmupSessionId { get; private set; }

    /// <summary>预热开始 (总数);命令执行线程(UI)同步触发,宿主据此立即显示进度 UI。</summary>
    public event Action<int>? WarmupStarted;

    /// <summary>预热进度 (已完成数, 总数, 当前条说明);后台线程触发。</summary>
    public event Action<int, int, string>? WarmupProgress;

    /// <summary>预热结束(携带结果说明);后台线程触发。</summary>
    public event Action<string>? WarmupFinished;

    public override IReadOnlyList<PluginChatCommand> GetChatCommands() =>
        Engine.Equals("gsv", StringComparison.OrdinalIgnoreCase)
            ? [new PluginChatCommand("warmup", "预热当前声线的全部参考音频($warmup)", false, ExecuteWarmupCommand)]
            : [];

    private string? ExecuteWarmupCommand(string? sessionId, string arg)
    {
        var voiceName = GsvVoice.Trim();
        var vc = GsvVoices.FirstOrDefault(x => x.Name.Equals(voiceName, StringComparison.OrdinalIgnoreCase));
        if (vc is null)
            return string.IsNullOrEmpty(voiceName)
                ? "GSV: 未设置默认声线(设置页选择)"
                : $"GSV: 默认声线 '{voiceName}' 未配置(设置页添加)";
        if (vc.Refs.Count == 0)
            return $"GSV: 声线 '{voiceName}' 还没有参考音频(设置页添加)";
        var jobs = vc.Refs.Select(r => (RefName: $"{vc.Name}/{r.Emotion}", Wav: r.Wav, Text: r.Text, Lang: r.Lang)).ToList();
        if (Interlocked.Exchange(ref _warmingUp, 1) == 1)
            return "$warmup 已在进行中";
        WarmupSessionId = sessionId;
        Log.Global.Info($"[TtsClient] $warmup started: voice={vc.Name}, {jobs.Count} refs");
        WarmupStarted?.Invoke(jobs.Count); // UI 线程同步触发:进度 UI 立即显示(不等于首条完成)
        Task.Run(() => RunWarmup(vc.Name, vc.Sovits.Trim(), vc.Gpt.Trim(), jobs));
        return $"$warmup 已开始:预热声线 {vc.Name} 的 {jobs.Count} 条参考音频";
    }

    /// <summary>预热当前声线:先 LoadModel(refs 单驻留,换声线即销毁,预热只对当前选择的模型有效),
    /// 再逐条预热(每条持引擎锁,不阻塞期间到达的正常合成);进度经事件上报宿主。</summary>
    private void RunWarmup(string voiceName, string sovits, string gpt, List<(string RefName, string Wav, string Text, string Lang)> jobs)
    {
        int done = 0;
        var errors = new List<string>();
        try
        {
            lock (_gsvLock)
            {
                EnsureGsvEngine();
                if (!string.Equals(_gsvCurrentVoice, voiceName, StringComparison.OrdinalIgnoreCase))
                {
                    _gsv!.LoadModel(sovits, gpt);
                    _gsvCurrentVoice = voiceName;
                    _gsvWarmed.Clear();
                    Log.Global.Info($"[TtsClient] GSV voice loaded: {voiceName} (sovits='{sovits}', gpt='{gpt}')");
                }
            }
            foreach (var job in jobs)
            {
                string note = "";
                lock (_gsvLock)
                {
                    try
                    {
                        if (_gsvWarmed.Add(job.RefName)) // Add 返回 false = 已预热,跳过
                        {
                            if (!File.Exists(job.Wav))
                                throw new InvalidOperationException($"参考音频不存在:{job.Wav}");
                            _gsv!.WarmupRef(job.RefName, job.Wav, job.Text, job.Lang);
                        }
                        else note = "(已缓存,跳过)";
                    }
                    catch (Exception ex)
                    {
                        _gsvWarmed.Remove(job.RefName);
                        note = "失败:" + ex.Message;
                        errors.Add($"{job.RefName}: {ex.Message}");
                        Log.Global.Error($"[TtsClient] warmup fail: {job.RefName}: {ex.Message}");
                    }
                }
                done++;
                WarmupProgress?.Invoke(done, jobs.Count, $"{job.RefName} {note}".Trim());
            }
            Log.Global.Info($"[TtsClient] $warmup finished: {done} refs, {errors.Count} errors");
            WarmupFinished?.Invoke(errors.Count == 0
                ? $"预热完成:{done} 条参考音频全部就绪"
                : $"预热完成:{done - errors.Count} 条成功,{errors.Count} 条失败(详见日志)");
        }
        catch (Exception ex)
        {
            WarmupFinished?.Invoke("预热失败:" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _warmingUp, 0);
        }
    }

    /// <summary>定位 gsvtts.dll 目录:程序目录优先(DLL 随应用分发),再兜底模型包旁的 gsv-tts-onnx/build。</summary>
    internal string? LocateGsvDllDir(string modelDir)
    {
        foreach (var dir in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.GetFullPath(Path.Combine(modelDir, "..", "gsv-tts-onnx", "build")),
                     Path.Combine(modelDir, "gsvtts.dll") is { } p ? Path.GetDirectoryName(p) : null,
                 })
            if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "gsvtts.dll")))
                return dir;
        return null;
    }

    /// <summary>s16le 单声道 PCM → WAV(RIFF)字节流(宿主播放端只吃完整 wav)。</summary>
    internal static byte[] WrapWav(byte[] pcm, int sampleRate)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + pcm.Length);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);              // PCM
            w.Write((short)1);              // mono
            w.Write(sampleRate);
            w.Write(sampleRate * 2);        // byte rate
            w.Write((short)2);              // block align
            w.Write((short)16);             // bits
            w.Write("data"u8);
            w.Write(pcm.Length);
            w.Write(pcm);
        }
        return ms.ToArray();
    }

    /// <summary>释放本地引擎资源(设置变更重建 / 插件停用时调用)。
    /// GSV 合成全程持 _gsvLock(秒级 ONNX 推理):设置线程死等锁会把 UI 冻到合成结束——
    /// 忙时改挂 _gsvResetPending,延迟到 worker 归还锁后的下次 EnsureGsvEngine 执行;Dispose 用 wait=true 强制收净。
    /// _sapi 与 SpeakWindows 同锁:此前在锁外释放、worker 无锁使用,是释放/使用竞态。</summary>
    private void ResetLocalEngines(bool waitForWorker = false)
    {
        lock (_sapiLock)
        {
            _sapi?.Dispose();
            _sapi = null;
            _sapiVoiceNames = null;
        }
        if (Monitor.TryEnter(_gsvLock))
        {
            try
            {
                DisposeGsvLocked();
            }
            finally
            {
                Monitor.Exit(_gsvLock);
            }
        }
        else if (waitForWorker)
        {
            lock (_gsvLock)
                DisposeGsvLocked();
        }
        else
        {
            _gsvResetPending = true; // 合成进行中:不抢锁,下次合成前由 EnsureGsvEngine 收编
        }
    }

    /// <summary>释放 GSV 引擎及其状态(_gsvLock 内调用)。</summary>
    private void DisposeGsvLocked()
    {
        _gsv?.Dispose();
        _gsv = null;
        _gsvLoadedKey = "";
        _gsvWarmed.Clear();
        _gsvCurrentVoice = "";
    }

    /// <summary>枚举系统已安装语音角色(设置页 Windows 引擎下拉;失败返回空表)。</summary>
    public static string[] ListWindowsVoices()
    {
        try
        {
            using var s = new System.Speech.Synthesis.SpeechSynthesizer();
            return s.GetInstalledVoices()
                .Select(v => v.VoiceInfo.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>扫描模型包 voices/ 子目录,返回可用的音色模型名(声线=模型名;设置页下拉)。
    /// SoVITS 放 voices/<名>/,GPT 放 gpt/<名>/,缺省部分自动回退底模。</summary>
    public static string[] ListGsvModelNames(string modelDir)
    {
        try
        {
            var voicesDir = Path.Combine(modelDir, "voices");
            return Directory.Exists(voicesDir)
                ? Directory.EnumerateDirectories(voicesDir)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n) && !n.Equals("s2Gv2ProPlus", StringComparison.OrdinalIgnoreCase))
                    .Cast<string>()
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
