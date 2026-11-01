using System.Diagnostics;
using System.Globalization;
using APet.Framework.Agent;
using APet.Framework.Plugin;
using APet.Framework.Session;

namespace NvidiaMonitor;

/// <summary>单卡快照(nvidia-smi 查询结果;单位:%/MB/°C/W,null = 硬件未上报 [N/A])。</summary>
public sealed record GpuSnapshot(
    string Name,
    int? Utilization,
    int? MemUsedMb,
    int? MemTotalMb,
    int? TemperatureC,
    double? PowerW,
    int? FanSpeedPct,
    DateTimeOffset At);

/// <summary>
/// NVIDIA GPU 实时监控(仿 vscode-nvidia-monitor):
/// 后台 Timer 每 interval 秒轮询一次 nvidia-smi(CSV 无单位),最近一轮快照经
/// 前端数据面暴露——侧边栏全量面板 + 底部状态栏短文本(status 字段,宿主约定)。
/// 无 NVIDIA 驱动时数据面 available=false 并带 error 说明,不抛错、不影响其他插件。
/// </summary>
public sealed class NvidiaMonitor : AgentPluginBase
{
    public override string Name => "GpuMonitor";

    // ───────── 元数据:设置页卡片展示 ─────────

    public override string? Version => "1.0.0";
    public override string? Author => "APet";
    public override string? Description => "NVIDIA GPU 实时:利用率/显存/温度/功耗/风扇;侧边栏面板 + 底部状态栏短文本";

    // 侧边栏页签(全量面板)+ 设置页;精简实时信息经数据面 status 字段进底部状态栏
    public override string UiSlots => "settings,sidebar";

    // ───────── 设置项:设置页自动渲染,保存后下一轮查询即按新间隔重新武装定时器 ─────────

    [PluginSetting("interval", Label = "轮询间隔(秒)", Description = "nvidia-smi 查询间隔;1-60,保存后立即生效", Min = 1, Max = 60)]
    public int IntervalSeconds { get; set; } = 2;

    private System.Threading.Timer? _timer;
    private volatile List<GpuSnapshot> _gpus = new();
    private volatile string? _error;
    private volatile bool _querying;

    // period=-1ms 表示"不重复"(自调度单次)。
    // 教训:.NET Timer 把 dueTime=-1ms 当"取消"——周期形态 Change(-1ms, X) 会直接停掉定时器
    //(实踩:首查后数据面永远停在首查快照,前端再刷新也没用)
    private static readonly TimeSpan NoRepeat = TimeSpan.FromMilliseconds(-1);

    public override void Initialize()
    {
        // 同步路径不启进程:300ms 后首查,随后每轮自调度
        _timer = new System.Threading.Timer(_ => PollOnce(), null, TimeSpan.FromMilliseconds(300), NoRepeat);
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void PollOnce()
    {
        if (!_querying) // 上一轮 nvidia-smi 还没回来,跳过本轮
        {
            _querying = true;
            try { QueryOnce(); }
            finally { _querying = false; }
        }
        // 自调度:按当前 interval 武装下一次单次(设置页改间隔,下一轮即生效)
        try { _timer?.Change(TimeSpan.FromSeconds(Math.Max(1, IntervalSeconds)), NoRepeat); }
        catch (ObjectDisposedException) { /* 正在 Dispose,忽略 */ }
    }

    private const string QueryFields =
        "name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,fan.speed";

    private void QueryOnce()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", $"--query-gpu={QueryFields} --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) { _error = "启动 nvidia-smi 失败"; return; }

            string csv = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                _error = "nvidia-smi 超时未返回";
                return;
            }
            if (p.ExitCode != 0)
            {
                _error = "nvidia-smi 退出码 " + p.ExitCode;
                return;
            }

            var list = new List<GpuSnapshot>();
            foreach (var line in csv.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split(',', 7);
                if (f.Length < 7) continue;
                list.Add(new GpuSnapshot(
                    f[0].Trim(),
                    ParseInt(f[1]),
                    ParseInt(f[2]),
                    ParseInt(f[3]),
                    ParseInt(f[4]),
                    ParseDouble(f[5]),
                    ParseInt(f[6]),
                    DateTimeOffset.Now));
            }
            if (list.Count == 0) { _error = "nvidia-smi 未返回 GPU 数据"; return; }
            _gpus = list;
            _error = null;
        }
        catch (Exception ex)
        {
            // 典型:Win32Exception "找不到应用程序" = 未装 NVIDIA 驱动
            _error = "nvidia-smi 不可用: " + ex.Message;
        }
    }

    private static int? ParseInt(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? ParseDouble(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // ───────── 前端数据面:侧边栏页签轮询 + 插件 UI 页(输入框下小部件)共用 ─────────

    public override bool ProvidesData => true;

    public override object? GetPluginData(string? sessionId) =>
        new
        {
            available = _error == null,
            error = _error,
            interval = IntervalSeconds,
            gpus = _gpus,
            status = StatusText, // 底部状态栏短文本(宿主约定:数据面 status 字符串字段);null 不显示
        };

    /// <summary>底部状态栏短文本:单卡 "45% 显存22.8/24.0G 45°C 29.8W";多卡带 G0/G1 前缀。</summary>
    private string? StatusText
    {
        get
        {
            if (_gpus.Count == 0) return null;
            return string.Join(" · ", _gpus.Select((g, i) =>
                (i > 0 ? $"G{i} " : "") +
                $"{g.Utilization}% 显存{g.MemUsedMb / 1024.0:0.0}/{g.MemTotalMb / 1024.0:0.0}G {g.TemperatureC}°C"
                + (g.PowerW is double w ? $" {w:0.0}W" : "")));
        }
    }

    // ───────── $ 聊天指令:一次性快照 ─────────

    public override IReadOnlyList<PluginChatCommand> GetChatCommands() =>
    [
        new PluginChatCommand(
            "gpu",
            "NVIDIA GPU 当前快照(利用率/显存/温度/功耗)",
            TakesArgument: false,
            Execute: (_, _) => SnapshotText() ?? "暂无数据(nvidia-smi 不可用,详见侧边栏 GPU 面板)"),
    ];

    private string? SnapshotText()
    {
        var gpus = _gpus;
        if (gpus.Count == 0) return null;
        return string.Join("\n", gpus.Select(g =>
            $"[{g.Name}] 利用率 {g.Utilization?.ToString(CultureInfo.InvariantCulture) ?? "–"}% · "
            + $"显存 {(g.MemUsedMb ?? 0) / 1024.0:0.0}/{(g.MemTotalMb ?? 0) / 1024.0:0.0} GB · "
            + $"{g.TemperatureC?.ToString(CultureInfo.InvariantCulture) ?? "–"}°C · "
            + $"功耗 {(g.PowerW?.ToString("0.#", CultureInfo.InvariantCulture) ?? "–")} W"
            + (g.FanSpeedPct is int fan ? $" · 风扇 {fan}%" : "")));
    }
}
