using System.IO;
using APet.Framework.Logging;
using APet.Plugins.Tts;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace APet.UI.Services;

/// <summary>
/// TTS 串行播放器:常驻 WASAPI 共享输出(设备不反复启停,消除句首杂音),自动重采样到设备混音格式,
/// 音量为程序内增益 DSP。每 Agent 一个实例;句序由播放闸门保证。
/// </summary>
public sealed class TtsAudioPlayer
{
    private readonly SemaphoreSlim _playbackGate = new(1, 1); // 句序:一段播完才放行下一段
    private readonly Func<double> _volume;  // 音量来源(宿主读 Tts.Common.Volume)
    private readonly Action _onSpeechEnd;   // 一段播放结束的归还回调(Tts.TrackSpeechEnd)

    public TtsAudioPlayer(Func<double> volume, Action onSpeechEnd)
    {
        _volume = volume;
        _onSpeechEnd = onSpeechEnd;
    }

    /// <summary>播放一段合成完成的音频(NAudio/WASAPI 共享模式):播放设备进程内常驻,音频段解码重采样后推进
    /// 常驻缓冲,句与句之间由设备渲染静音 —— 不再每句启停音频会话,消除起始杂音。
    /// 音量在浮点采样上缩放,不触碰系统/会话音量。WASAPI 失败时回退 SoundPlayer。</summary>
    public void Play(TtsAudioEventArgs e)
    {
        if (!string.Equals(e.Format, "wav", StringComparison.OrdinalIgnoreCase))
        {
            Log.Global.Info($"[TtsClient] playback skipped: format '{e.Format}' is not wav (configure responseFormat=wav for playback)");
            _onSpeechEnd(); // 放弃播放:计数已在出队时加上,这里归还(与播放路径配对)
            return;
        }
        var volume = Math.Clamp(_volume(), 0.0, 1.0);
        _ = Task.Run(() =>
        {
            var acquired = false;
            try
            {
                // 闸门等待带上限:前段播放链假死时后续句不再无限排队——丢一句话
                // 好过整链卡死(HasActiveSpeech 恒真、气泡常驻、该 Agent 语音全哑)
                acquired = _playbackGate.Wait(TimeSpan.FromSeconds(30));
                if (!acquired)
                {
                    Log.Global.Error("[TtsClient] playback gate wait timed out; clip skipped");
                    return;
                }
                PlayClipPersistent(e.Audio, (float)volume);
            }
            catch (Exception exWasapi)
            {
                Log.Global.Info($"[TtsClient] WASAPI playback failed, falling back to SoundPlayer: {exWasapi.Message}");
                Reset(); // 常驻设备可能已失效:丢弃,下一段重建
                try
                {
                    // 回退播放同样带上限:winmm 与 WASAPI 是不同栈,通常可救;真挂死也只损失这一句
                    var fallback = Task.Run(() =>
                    {
                        using var stream = new MemoryStream(e.Audio);
                        using var player = new System.Media.SoundPlayer(stream);
                        player.PlaySync(); // 串行播放,保证句序
                    });
                    if (!fallback.Wait(TimeSpan.FromSeconds(30)))
                        Log.Global.Error("[TtsClient] SoundPlayer fallback timed out; clip abandoned");
                }
                catch (Exception ex)
                {
                    Log.Global.Error("[TtsClient] playback failed: " + ex.Message);
                }
            }
            finally
            {
                if (acquired)
                    _playbackGate.Release();
                _onSpeechEnd(); // 队列空且无在播段时触发 SpeechDrained——所有路径都保证归还计数
            }
        });
    }

    private readonly object _audioLock = new();
    private WasapiOut? _audioOut;              // 常驻 WASAPI 输出:进程内只启一次,句间渲染静音
    private BufferedWaveProvider? _audioBuffer; // 重采样后的 PCM 缓冲(设备混音格式)
    private PersistentSampleProvider? _audioGain; // 字节→浮点 + 音量(DSP,可随时改)

    /// <summary>常驻播放设备懒创建:Init 后立即 Play,缓冲无数据时由 BufferedWaveProvider 填静音,
    /// 音频会话不再反复启停。</summary>
    private void EnsureAudioOut()
    {
        if (_audioOut is not null)
            return;
        var device = new WasapiOut(AudioClientShareMode.Shared, 150);
        _audioBuffer = new BufferedWaveProvider(device.OutputWaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(10),
            DiscardOnBufferOverflow = true,
        };
        _audioGain = new PersistentSampleProvider(_audioBuffer);
        device.Init(_audioGain);
        device.Play();
        _audioOut = device;
        Log.Global.Info($"[TtsClient] persistent WASAPI output started (mixfmt={device.OutputWaveFormat})");
    }

    public void Reset()
    {
        lock (_audioLock)
        {
            _audioOut?.Dispose();
            _audioOut = null;
            _audioBuffer = null;
            _audioGain = null;
        }
    }

    /// <summary>把一段 WAV 解码重采样后推进常驻缓冲并等它播完(句序由 PlaybackGate 保证)。
    /// 带背压写入,任意长度音频都不会撑爆缓冲。两个等待循环以"预期时长 + 余量"为总上限:
    /// 设备"状态 Playing 但渲染停摆"(驱动切换窗口等)时抛超时,由外层丢弃常驻设备并走回退——
    /// 否则循环自旋不退,句序闸门永久被占。</summary>
    private void PlayClipPersistent(byte[] audio, float volume)
    {
        lock (_audioLock)
        {
            EnsureAudioOut();
            var device = _audioOut!;
            var buffer = _audioBuffer!;
            _audioGain!.Gain = volume; // 本段起效;下一段可随时改
            using var ms = new MemoryStream(audio);
            using var wav = new WaveFileReader(ms);
            using var resampler = new MediaFoundationResampler(wav, device.OutputWaveFormat) { ResamplerQuality = 60 };
            // 超时基准 = 源 WAV 自身的时长(重采样不改变时长)。不能用设备混音格式的字节率:
            // 源码率(如 32k16bit 单声道)通常远低于设备码率(48k float32 立体声),比值可达数倍,
            // 长句会被算短、正常播放中途被超时掐断
            var expectedSeconds = wav.Length / (double)wav.WaveFormat.AverageBytesPerSecond;
            var deadline = TimeSpan.FromSeconds(expectedSeconds + 30); // 预期时长 + 30s 余量(慢设备/调度抖动)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var chunk = new byte[Math.Max(4096, device.OutputWaveFormat.AverageBytesPerSecond / 4)];
            int n;
            while ((n = resampler.Read(chunk, 0, chunk.Length)) > 0)
            {
                while (buffer.BufferedDuration > TimeSpan.FromSeconds(2) && device.PlaybackState == PlaybackState.Playing)
                {
                    if (sw.Elapsed > deadline)
                        throw new TimeoutException($"playback stalled (backpressure): {sw.Elapsed.TotalSeconds:F0}s for a {expectedSeconds:F1}s clip");
                    Thread.Sleep(20);
                }
                buffer.AddSamples(chunk, 0, n);
            }
            // 等本段播完(缓冲排空)再返回;设备意外停机时退出,由外层回退兜底
            while (buffer.BufferedDuration > TimeSpan.Zero && device.PlaybackState == PlaybackState.Playing)
            {
                if (sw.Elapsed > deadline)
                    throw new TimeoutException($"playback stalled (drain): {sw.Elapsed.TotalSeconds:F0}s for a {expectedSeconds:F1}s clip");
                Thread.Sleep(20);
            }
            Log.Global.Info($"[TtsClient] play done: clip={expectedSeconds:F2}s, wall={sw.Elapsed.TotalSeconds:F2}s");
        }
    }

    /// <summary>混音格式 → 浮点采样:WASAPI 共享模式的混音格式是 WAVE_FORMAT_EXTENSIBLE 包
    /// float32(或 PCM16),NAudio 现成转换器不认 Extensible,这里按位深直接读字节;
    /// 音量逐样本乘增益,进程内 DSP,与系统音量无关。对外报告等价的 IEEE float 格式
    /// (字节布局与 Extensible float 一致),供 WasapiOut.Init 的浮点采样通路使用。</summary>
    private sealed class PersistentSampleProvider(IWaveProvider source) : ISampleProvider
    {
        public float Gain { get; set; } = 1f;

        private readonly byte[] _bytes = new byte[source.WaveFormat.AverageBytesPerSecond];
        private readonly WaveFormat _floatFormat =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, source.WaveFormat.Channels);

        public WaveFormat WaveFormat => _floatFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int bps = source.WaveFormat.BitsPerSample / 8;
            int read = source.Read(_bytes, 0, Math.Min(count * bps, _bytes.Length));
            int samples = read / bps;
            if (bps == 4)
                for (int i = 0; i < samples; i++)
                    buffer[offset + i] = BitConverter.ToSingle(_bytes, i * 4) * Gain;
            else if (bps == 2)
                for (int i = 0; i < samples; i++)
                    buffer[offset + i] = BitConverter.ToInt16(_bytes, i * 2) / 32768f * Gain;
            else
                Array.Clear(buffer, offset, samples);
            return samples;
        }
    }
}
