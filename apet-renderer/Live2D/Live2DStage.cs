using APet.Renderer.Live2D.Cubism;
using System.IO;
using System.Text.Json;

namespace APet.Renderer.Live2D;

/// <summary>
/// 封装 Live2D 模型 + 动作/表情/物理/姿态 + 自动眨眼/呼吸/口型,
/// 对宿主暴露精细控制 API(参数、部件、Drawable、命中检测)。
/// 与 SpineStage 的定位一致:渲染控件(Live2DControl / Live2DOverlayWindow)持有它并每帧调用 Update。
/// </summary>
public class Live2DStage : IDisposable
{
    // ---- 模型与资源 ----
    public CubismModel Model { get; private set; }
    public CubismModel3Json ModelJson { get; private set; }
    public CubismCdiInfo Cdi { get; private set; }

    CubismPhysics3 _physics;
    CubismPose3 _pose;
    readonly Dictionary<string, List<(CubismMotion motion, CubismMotionDef def)>> _motionGroups = new();
    readonly List<(string name, CubismExpression expr)> _expressions = new();

    string[] _eyeBlinkIds = Array.Empty<string>();
    string[] _lipSyncIds = Array.Empty<string>();

    // ---- 播放状态 ----
    float _time;

    class MotionEntry
    {
        public CubismMotion Motion;
        public string Name;
        public float StartTime;
        public bool Loop;
        public bool Finished;
        public float FadeOutOverride = -1f;   // 被新动作替代时按新动作淡出
        public float FadeOutOverrideStart;    // 淡出覆盖生效时刻(淡出从该时刻起算,而非动作开始时刻)
        public float FadeInStart;             // 淡入参考时刻(官方:每个循环点重置,循环中重新淡入)
        public float LastMotionTime;          // 上一帧的圈内时间,用于检测换圈
        public float CrossToStart;            // 循环交叉淡化权重:本圈末段向初值混合,消除非无缝数据的换圈跳变
    }

    readonly List<MotionEntry> _motionQueue = new();

    class ExpressionEntry
    {
        public CubismExpression Expr;            // 内置 exp3 表情(自定义时为 null)
        public CustomExpression Custom;          // 自定义参数预设(内置时为 null)
        public string Name;
        public float StartTime;
        public bool FadingOut;
        // 自定义专用:应用时刻的基准值快照,淡出时精确还原(base + (target-base)*w)
        public Dictionary<string, float> ParamBases;
        public Dictionary<string, float> PartBases;
    }

    readonly List<ExpressionEntry> _expressionStack = new();

    // ---- 自定义表情(用户捏的参数预设) ----
    readonly Dictionary<string, CustomExpression> _customExpressions = new(StringComparer.Ordinal);
    float[] _setupPartOpacity = Array.Empty<float>();   // Load 时的部件初始不透明度(捕捉"偏离默认"用)

    // ---- 回位过渡(一次性动作播完:参数/部件平滑混回休息态,避免硬复位跳变) ----
    float _restStart = -1f;                    // 起始时刻(_time);<0 表示未激活
    float _restDuration = 0.5f;                // 过渡时长[s]
    float[] _restParamFrom = Array.Empty<float>();   // 启动时的参数值快照(混合起点)
    float[] _restPartFrom = Array.Empty<float>();
    float[] _restPartTarget = Array.Empty<float>();  // 部件目标不透明度(setup 或宿主兜底基线)

    // ---- 眨眼状态机 ----
    int _blinkState = 0;        // 0=空闲 1=闭合中 2=闭着保持 3=睁开中(与官方 CubismAutoEyeBlinkInput 同构)
    float _blinkPhaseTime;      // 当前阶段已进行时间[s]
    float _blinkTimer = 2f;     // 空闲倒计时[s](下次眨眼时刻)
    float[] _eyeBlinkBase = Array.Empty<float>();   // 各眼睛参数的"基准开合度"(眨眼在其上乘系数,避免逐帧乘当前值归零)

    // 官方默认眨眼曲线(CubismAutoEyeBlinkInput: Timescale=10 下的 closing/closed/opening):
    // 闭合 1.0/10=0.1s → 保持 0.5/10=0.05s → 睁开 1.5/10=0.15s,总计约 0.3s(睁开比闭合慢)
    const float BlinkClosingSeconds = 0.1f;
    const float BlinkClosedSeconds = 0.05f;
    const float BlinkOpeningSeconds = 0.15f;

    readonly HashSet<string> _motionTouchedParams = new(StringComparer.Ordinal);

    /// <summary>加载成功后为 true。</summary>
    public bool IsLoaded => Model != null;
    public string SourcePath { get; private set; }

    /// <summary>播放速度倍率(作用于动作与自动控制)。</summary>
    public float TimeScale { get; set; } = 1f;
    /// <summary>暂停后不再推进任何动画与物理,模型保持当前姿态。</summary>
    public bool Paused { get; set; }

    /// <summary>自动眨眼(model3.json 的 EyeBlink 组;动作已驱动这些参数时自动让位)。</summary>
    public bool AutoEyeBlink { get; set; } = true;
    /// <summary>呼吸(模型含 ParamBreath 时生效)。</summary>
    public bool BreathEnabled { get; set; } = true;
    /// <summary>物理演算(physics3.json)。</summary>
    public bool PhysicsEnabled { get => _physics?.Enabled ?? false; set { if (_physics != null) _physics.Enabled = value; } }
    /// <summary>姿态控制(pose3.json,互斥部件显隐)。</summary>
    public bool PoseEnabled { get => _pose?.Enabled ?? false; set { if (_pose != null) _pose.Enabled = value; } }

    /// <summary>口型值 0..1(写入 model3.json 的 LipSync 组参数);为负数表示不接管。</summary>
    public float LipSyncValue { get; set; } = -1f;

    /// <summary>整体不透明度 0..1(乘到所有 Drawable 上)。</summary>
    public float ModelOpacity { get; set; } = 1f;

    /// <summary>动作播放完毕(非循环)时触发:参数为动作名。</summary>
    public event Action<string> OnMotionEnd;
    /// <summary>表情切换时触发:参数为新表情名(null = 清除)。</summary>
    public event Action<string> OnExpressionChanged;

    // ================ 加载 ================

    /// <summary>
    /// 加载 model3.json(自动加载 moc3 / 纹理 / 物理 / 姿态 / 显示信息 / 动作 / 表情)。
    /// loadTexture 回调在 GL 上下文中把 PNG 加载为纹理并返回句柄(渲染器提供)。
    /// 重复调用会先释放上一个模型。
    /// </summary>
    public void Load(string model3Path, Func<string, int> loadTexture)
    {
        var json = CubismModel3Json.Load(model3Path);
        var model = CubismModel.Load(File.ReadAllBytes(json.MocPath));

        SourcePath = model3Path;
        ModelJson = json;
        Model = model;

        // 纹理:按 model3.json 的 Textures 顺序加载,Drawable.TextureIndex 即此数组下标
        foreach (var texPath in json.TexturePaths)
            loadTexture(texPath);

        // 显示信息(参数/部件的中文名与分组)
        Cdi = json.DisplayInfoPath != null && File.Exists(json.DisplayInfoPath)
            ? CubismCdiInfo.Load(json.DisplayInfoPath) : null;

        // 物理 / 姿态 / 眨眼(LIVE2D_NO_PHYSICS / LIVE2D_NO_POSE / LIVE2D_NO_BLINK 为诊断开关)
        _physics = Environment.GetEnvironmentVariable("LIVE2D_NO_PHYSICS") == "1" ? null
            : json.PhysicsPath != null && File.Exists(json.PhysicsPath) ? CubismPhysics3.Load(json.PhysicsPath) : null;
        _physics?.Bind(model);
        _pose = Environment.GetEnvironmentVariable("LIVE2D_NO_POSE") == "1" ? null
            : json.PosePath != null && File.Exists(json.PosePath) ? CubismPose3.Load(json.PosePath) : null;
        _pose?.Bind(model);
        AutoEyeBlink = Environment.GetEnvironmentVariable("LIVE2D_NO_BLINK") != "1";

        // 动作
        _motionGroups.Clear();
        foreach (var (group, defs) in json.Motions)
        {
            var list = new List<(CubismMotion, CubismMotionDef)>();
            foreach (var def in defs)
            {
                if (def.File == null || !File.Exists(def.File)) continue;
                var motion = CubismMotion.Load(def.File);
                motion.FadeInTime = def.FadeInTime ?? motion.FadeInTime;
                motion.FadeOutTime = def.FadeOutTime ?? motion.FadeOutTime;
                list.Add((motion, def));
            }
            if (list.Count > 0) _motionGroups[group] = list;
        }

        // 表情
        _expressions.Clear();
        foreach (var def in json.Expressions)
            if (def.File != null && File.Exists(def.File))
                _expressions.Add((def.Name, CubismExpression.Load(def.File, def.Name)));

        // 参数组
        _eyeBlinkIds = GetGroupIds("EyeBlink", "ParamEyeLOpen", "ParamEyeROpen");
        _eyeBlinkBase = new float[_eyeBlinkIds.Length];
        for (int i = 0; i < _eyeBlinkIds.Length; i++)
        {
            var p = model.GetParameter(_eyeBlinkIds[i]);
            _eyeBlinkBase[i] = p?.Value ?? 1f;
        }
        _lipSyncIds = GetGroupIds("LipSync", "ParamMouthOpenY");

        // 部件初始不透明度快照:自定义表情捕捉"偏离 setup 的部件"(如腮红)用
        _setupPartOpacity = new float[Model.Parts.Length];
        for (int i = 0; i < Model.Parts.Length; i++)
            _setupPartOpacity[i] = Model.Parts[i].Opacity;

        // 自定义表情 sidecar:与 model3.json 同目录的 <模型名>.customexp.json(用户捏脸预设),存在即自动加载
        var sidecar = CustomExpressionSidecarPath(model3Path);
        if (File.Exists(sidecar)) LoadCustomExpressions(sidecar);

        _time = 0;
        _motionQueue.Clear();
        _expressionStack.Clear();
        _restStart = -1f;   // 新模型:结束上一个模型的残留回位过渡
    }

    string[] GetGroupIds(string groupName, params string[] fallback)
    {
        var g = ModelJson?.Groups?.FirstOrDefault(x => x.Name == groupName);
        if (g?.Ids is { Length: > 0 })
            return g.Ids.Where(id => Model.GetParameter(id) != null).ToArray();
        return fallback.Where(id => Model.GetParameter(id) != null).ToArray();
    }

    // ================ 每帧更新 ================

    /// <summary>每帧更新。delta 单位秒。暂停时模型保持当前姿态。</summary>
    public void Update(float delta)
    {
        if (Model == null || Paused) return;
        delta = Math.Min(delta, 0.1f);
        _time += delta * TimeScale;

        _motionTouchedParams.Clear();

        UpdateRestTransition();   // 先回位、后动作/表情:共享参数上动作与表情优先
        UpdateMotions(delta);
        UpdateExpressions(delta);
        UpdateEyeBlink(delta);
        UpdateBreath();
        UpdateLipSync();
        _physics?.Evaluate(Model, delta);
        _pose?.Evaluate(Model, delta);
        Model.Update();
    }

    void UpdateMotions(float delta)
    {
        for (int i = _motionQueue.Count - 1; i >= 0; i--)
        {
            var entry = _motionQueue[i];
            float t = _time - entry.StartTime;
            if (t < 0) continue;

            float duration = entry.Motion.Duration;
            bool ended = !entry.Loop && t >= duration;
            float motionTime, loopEndTime = 0f;
            if (entry.Loop)
            {
                // 官方 V2:一圈为 Duration + 1/Fps,(末关键帧, 圈长] 区间内做尾部修正拉回初值
                float loopDur = entry.Motion.LoopDuration;
                motionTime = t % loopDur;
                if (motionTime < entry.LastMotionTime)
                    entry.FadeInStart = _time;   // 官方 updateForNextLoop:换圈时淡入重新开始

                // 循环交叉淡化:每圈最后 FadeInTime 秒内把姿态向初值平滑混合。
                // 无缝数据上本圈末段≈初值,混合不可见(与官方表现一致);
                // 非无缝数据(如 Hiyori m07 结尾抬臂)则由"快速回位"变成平滑过渡。
                float c = Math.Min(entry.Motion.FadeInTime, loopDur * 0.5f);
                entry.CrossToStart = c > 0f && motionTime > loopDur - c
                    ? EasingSine((motionTime - (loopDur - c)) / c)
                    : 0f;
                // 交叉淡化窗口内由它负责回位,避免与尾部修正叠加
                loopEndTime = entry.CrossToStart > 0f ? 0f : loopDur;
            }
            else motionTime = Math.Min(t, duration);
            entry.LastMotionTime = motionTime;

            // 淡入淡出权重(官方 getEasingSine 正弦缓动)
            float fadeIn = entry.Motion.FadeInTime <= 0 ? 1f : EasingSine((_time - entry.FadeInStart) / entry.Motion.FadeInTime);
            float fadeOut = 1f;
            if (entry.FadeOutOverride >= 0)
                fadeOut = EasingSine(1f - (_time - entry.FadeOutOverrideStart) / entry.FadeOutOverride);
            else if (!entry.Loop && !ended && entry.Motion.FadeOutTime > 0)
                fadeOut = EasingSine((duration - t) / entry.Motion.FadeOutTime);
            float weight = Math.Min(fadeIn, fadeOut);

            if (ended || (entry.FadeOutOverride >= 0 && fadeOut <= 0f))
            {
                if (!entry.Finished)
                {
                    entry.Finished = true;
                    OnMotionEnd?.Invoke(entry.Name);
                }
                _motionQueue.RemoveAt(i);
                continue;
            }

            ApplyMotion(entry.Motion, motionTime, weight, loopEndTime, entry.CrossToStart);
        }
    }

    /// <summary>官方 CubismMath.getEasingSine:[0,1] 上的正弦缓动。</summary>
    static float EasingSine(float x) => x <= 0f ? 0f : x >= 1f ? 1f : 0.5f - 0.5f * MathF.Cos(x * MathF.PI);

    /// <summary>
    /// 回位过渡:把参数/部件从启动时的快照向休息态(默认值 / setup+兜底基线)按正弦缓动混合。
    /// 每帧先于动作/表情执行——共享参数上它们优先;眨眼/呼吸随后覆盖各自参数,环境效果不受影响。
    /// </summary>
    void UpdateRestTransition()
    {
        if (_restStart < 0f || Model == null) return;
        float t = (_time - _restStart) / _restDuration;
        if (t >= 1f)
        {
            // 收尾精确落到休息态,结束过渡
            for (int i = 0; i < Model.Parameters.Length && i < _restParamFrom.Length; i++)
                Model.Parameters[i].Value = Model.Parameters[i].Default;
            for (int i = 0; i < Model.Parts.Length && i < _restPartTarget.Length; i++)
            {
                Model.Parts[i].Opacity = Math.Clamp(_restPartTarget[i], 0f, 1f);
                _pose?.NotifyPartOpacity(Model.Parts[i].Id, Model.Parts[i].Opacity);
            }
            _restStart = -1f;
            return;
        }
        float w = EasingSine(Math.Clamp(t, 0f, 1f));
        for (int i = 0; i < Model.Parameters.Length && i < _restParamFrom.Length; i++)
        {
            var p = Model.Parameters[i];
            p.Value = _restParamFrom[i] + (p.Default - _restParamFrom[i]) * w;
        }
        for (int i = 0; i < Model.Parts.Length && i < _restPartFrom.Length; i++)
        {
            Model.Parts[i].Opacity = Math.Clamp(_restPartFrom[i] + (_restPartTarget[i] - _restPartFrom[i]) * w, 0f, 1f);
            _pose?.NotifyPartOpacity(Model.Parts[i].Id, Model.Parts[i].Opacity);
        }
    }

    void ApplyMotion(CubismMotion motion, float time, float weight, float loopEndTime = 0f, float crossToStart = 0f)
    {
        if (weight <= 0) return;
        foreach (var p in Model.Parameters)
        {
            if (!motion.EvaluateParameter(p.Id, time, out float v, loopEndTime, crossToStart)) continue;
            _motionTouchedParams.Add(p.Id);
            p.Value = weight >= 1f ? v : p.Value + (v - p.Value) * weight;
        }
        foreach (var part in Model.Parts)
        {
            if (!motion.EvaluatePart(part.Id, time, out float v, loopEndTime, crossToStart)) continue;
            part.Opacity = weight >= 1f ? v : part.Opacity + (v - part.Opacity) * weight;
        }
    }

    void UpdateExpressions(float delta)
    {
        for (int i = _expressionStack.Count - 1; i >= 0; i--)
        {
            var e = _expressionStack[i];
            float t = _time - e.StartTime;
            float fadeIn = e.Custom?.FadeInTime ?? e.Expr.FadeInTime;
            float fadeOut = e.Custom?.FadeOutTime ?? e.Expr.FadeOutTime;
            float w;
            if (e.FadingOut)
                w = 1f - Math.Clamp(t / fadeOut, 0f, 1f);
            else
                w = Math.Clamp(t / fadeIn, 0f, 1f);
            if (w <= 0f)
            {
                _expressionStack.RemoveAt(i);
                continue;
            }
            if (e.Custom != null) ApplyCustomExpression(e, w);
            else e.Expr.Apply(Model, w);
        }
    }

    /// <summary>自定义表情:基准快照 + 线性混合,淡出结束精确还原到应用前的值。</summary>
    void ApplyCustomExpression(ExpressionEntry e, float w)
    {
        foreach (var (id, target) in e.Custom.Targets)
        {
            var p = Model.GetParameter(id);
            if (p == null) continue;
            float baseVal = e.ParamBases[id];
            p.Value = baseVal + (target - baseVal) * w;
        }
        foreach (var (id, target) in e.Custom.PartTargets)
        {
            var part = Model.GetPart(id);
            if (part == null) continue;
            float baseVal = e.PartBases[id];
            part.Opacity = Math.Clamp(baseVal + (target - baseVal) * w, 0f, 1f);
        }
    }

    void UpdateEyeBlink(float delta)
    {
        if (!AutoEyeBlink || _eyeBlinkIds.Length == 0) return;

        // 动作正在驱动眼睛参数时让位(基准值跟随当前值,动作结束后从新姿态继续)
        bool motionDriving = false;
        foreach (var id in _eyeBlinkIds)
            if (_motionTouchedParams.Contains(id)) { motionDriving = true; break; }
        if (motionDriving)
        {
            _blinkState = 0;
            SyncEyeBlinkBase();
            return;
        }

        float v;
        switch (_blinkState)
        {
            case 0:
                // 空闲:基准值跟随当前参数(表情/宿主的外部改动即时生效)
                SyncEyeBlinkBase();
                _blinkTimer -= delta;
                if (_blinkTimer <= 0f)
                {
                    _blinkState = 1;
                    _blinkPhaseTime = 0f;
                }
                v = 1f;
                break;

            case 1:
                // 闭合:线性 1 → 0
                _blinkPhaseTime += delta;
                if (_blinkPhaseTime >= BlinkClosingSeconds)
                {
                    _blinkState = 2;
                    _blinkPhaseTime = 0f;
                    v = 0f;
                }
                else v = 1f - _blinkPhaseTime / BlinkClosingSeconds;
                break;

            case 2:
                // 闭着保持
                _blinkPhaseTime += delta;
                if (_blinkPhaseTime >= BlinkClosedSeconds)
                {
                    _blinkState = 3;
                    _blinkPhaseTime = 0f;
                }
                v = 0f;
                break;

            default:
                // 睁开:线性 0 → 1,收尾精确回到基准值(避免最后一帧的 base×<1 被下轮同步误认为新基准)
                _blinkPhaseTime += delta;
                if (_blinkPhaseTime >= BlinkOpeningSeconds)
                {
                    _blinkState = 0;
                    _blinkTimer = NextBlinkInterval();
                    v = 1f;
                }
                else v = _blinkPhaseTime / BlinkOpeningSeconds;
                break;
        }

        // 写"基准值 × 系数"而非乘当前值,否则闭眼阶段把参数乘到 0 后睁眼恢复不了原值
        for (int i = 0; i < _eyeBlinkIds.Length; i++)
        {
            var p = Model.GetParameter(_eyeBlinkIds[i]);
            if (p != null) p.Value = _eyeBlinkBase[i] * v;
        }
    }

    static float NextBlinkInterval()
    {
        // 官方:Mean 2.5s ± MaximumDeviation 2s(均匀分布,即 [0.5, 4.5]s)
        return MathF.Max(2.5f + (Random.Shared.NextSingle() * 2f - 1f) * 2f, 0.1f);
    }

    void SyncEyeBlinkBase()
    {
        for (int i = 0; i < _eyeBlinkIds.Length; i++)
        {
            var p = Model.GetParameter(_eyeBlinkIds[i]);
            if (p != null) _eyeBlinkBase[i] = p.Value;
        }
    }

    void UpdateBreath()
    {
        if (!BreathEnabled) return;
        var p = Model.GetParameter("ParamBreath");
        if (p != null)
            p.Value = 0.5f + 0.5f * MathF.Sin(_time * MathF.PI * 2f / 3.5f);
    }

    void UpdateLipSync()
    {
        if (LipSyncValue < 0f || _lipSyncIds.Length == 0) return;
        float v = Math.Clamp(LipSyncValue, 0f, 1f);
        foreach (var id in _lipSyncIds)
        {
            var p = Model.GetParameter(id);
            if (p != null)
                p.Value = p.Minimum + (p.Maximum - p.Minimum) * v;
        }
    }

    // ================ 动作 ================

    public enum MotionPriority
    {
        Idle = 0,      // 空闲填充:无动作在播时才播
        Normal = 1,    // 常规:同组动作可互相打断
        Force = 2,     // 强制:立即打断并播放
    }

    /// <summary>所有动作分组名。</summary>
    public string[] GetMotionGroups() => _motionGroups.Keys.ToArray();

    /// <summary>某分组的所有动作名(与 model3.json 顺序一致)。</summary>
    public string[] GetMotionNames(string group)
        => _motionGroups.TryGetValue(group, out var list)
            ? list.Select(x => x.def.Name ?? Path.GetFileNameWithoutExtension(x.def.File)).ToArray()
            : Array.Empty<string>();

    /// <summary>全部动作信息(组名/动作名/时长秒),与 model3.json 顺序一致。供宿主展示与命名映射。</summary>
    public (string Group, string Name, float Duration)[] GetMotionInfos()
    {
        var list = new List<(string, string, float)>();
        foreach (var (group, defs) in _motionGroups)
            foreach (var (motion, def) in defs)
                list.Add((group, def.Name ?? Path.GetFileNameWithoutExtension(def.File), motion.Duration));
        return list.ToArray();
    }

    /// <summary>
    /// 播放某分组下的动作。priority 决定与当前动作的冲突策略;
    /// loop 为空时用动作自带的 Loop 标记(motion3.json Meta.Loop,缺省 false)。
    /// 返回是否真的开始播放。
    /// </summary>
    public bool PlayMotion(string group, string motionName, MotionPriority priority = MotionPriority.Force, bool? loop = null)
    {
        if (!_motionGroups.TryGetValue(group, out var list)) return false;
        int index = list.FindIndex(x => (x.def.Name ?? Path.GetFileNameWithoutExtension(x.def.File)) == motionName);
        if (index < 0) index = list.FindIndex(x => Path.GetFileNameWithoutExtension(x.def.File) == motionName);
        return PlayMotion(group, index, priority, loop);
    }

    public bool PlayMotion(string group, int index, MotionPriority priority = MotionPriority.Force, bool? loop = null)
    {
        if (!_motionGroups.TryGetValue(group, out var list)) return false;
        if (index < 0 || index >= list.Count) return false;

        var (motion, def) = list[index];
        string name = def.Name ?? Path.GetFileNameWithoutExtension(def.File);

        // 冲突策略
        var playing = _motionQueue.LastOrDefault(e => !e.Finished);
        if (playing != null && priority != MotionPriority.Force)
        {
            if (priority == MotionPriority.Idle) return false;
            if (playing.Name == name) return false;
        }

        float fadeIn = motion.FadeInTime > 0 ? motion.FadeInTime : 0.5f;
        // 已有动作从此刻起在指定时间内淡出(与官方 startMotion 的 setFadeOut 语义一致)
        foreach (var e in _motionQueue.Where(e => !e.Finished))
        {
            if (e.FadeOutOverride < 0f)
                e.FadeOutOverrideStart = _time;
            e.FadeOutOverride = Math.Max(e.FadeOutOverride, fadeIn);
        }

        _motionQueue.Add(new MotionEntry
        {
            Motion = motion,
            Name = name,
            StartTime = _time,
            Loop = loop ?? motion.Loop,
            FadeInStart = _time,
        });
        return true;
    }

    /// <summary>随机播放分组里的一个动作(常用于 Idle / TapBody)。</summary>
    public bool PlayRandomMotion(string group, MotionPriority priority = MotionPriority.Force)
    {
        if (!_motionGroups.TryGetValue(group, out var list) || list.Count == 0) return false;
        return PlayMotion(group, Random.Shared.Next(list.Count), priority);
    }

    /// <summary>停止全部动作(模型回到当前参数状态)。</summary>
    public void StopMotions() => _motionQueue.Clear();

    /// <summary>
    /// 平滑回到休息态:duration 秒内(正弦缓动)把参数混回默认值、部件混回目标不透明度
    /// (partTargets 中指定的部件用给定值,其余用 setup 初始值)。用于一次性动作播完后的自然回位;
    /// 过渡期间动作/表情照常驱动共享参数,眨眼呼吸等环境效果不受影响。
    /// </summary>
    public void ResetToRest(float duration = 0.5f, IReadOnlyDictionary<string, float>? partTargets = null)
    {
        if (Model == null) return;
        _restDuration = Math.Max(0.1f, duration);
        _restStart = _time;
        _restParamFrom = Model.Parameters.Select(p => p.Value).ToArray();
        _restPartFrom = Model.Parts.Select(p => p.Opacity).ToArray();
        _restPartTarget = new float[Model.Parts.Length];
        for (int i = 0; i < Model.Parts.Length; i++)
            _restPartTarget[i] = partTargets is not null && partTargets.TryGetValue(Model.Parts[i].Id, out var target)
                ? target : _setupPartOpacity[i];
    }

    /// <summary>0 号语义兼容:当前(最后加入的)正在播放的动作名。</summary>
    public string CurrentMotionName => _motionQueue.LastOrDefault(e => !e.Finished)?.Name;

    public bool IsMotionPlaying => _motionQueue.Any(e => !e.Finished);

    // ================ 表情(内置 exp3 + 自定义参数预设) ================

    /// <summary>全部表情名:内置(exp3.json)+ 自定义(用户捏的参数预设),排序返回。
    /// 同名时只出现一次(自定义遮蔽内置)。</summary>
    public string[] GetExpressionNames()
    {
        var names = new List<string>(_expressions.Count + _customExpressions.Count);
        foreach (var (name, _) in _expressions)
            if (!_customExpressions.ContainsKey(name)) names.Add(name);   // 被自定义遮蔽的内置不重复列出
        names.AddRange(_customExpressions.Keys);
        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }

    /// <summary>切换表情(name 为 null 时清除,旧表情按淡出时间消失)。内置与自定义名统一入口;
    /// 同名时自定义优先(遮蔽内置)。</summary>
    public void SetExpression(string? name)
    {
        var current = _expressionStack.FirstOrDefault(e => !e.FadingOut);
        if (current != null && current.Name == name) return;
        if (current != null)
        {
            current.FadingOut = true;
            current.StartTime = _time;
        }
        if (name != null)
        {
            if (_customExpressions.TryGetValue(name, out var custom))
            {
                // 自定义:快照当前基准值,淡出时精确还原
                var paramBases = new Dictionary<string, float>();
                foreach (var id in custom.Targets.Keys)
                    if (Model.GetParameter(id) is { } p) paramBases[id] = p.Value;
                var partBases = new Dictionary<string, float>();
                foreach (var id in custom.PartTargets.Keys)
                    if (Model.GetPart(id) is { } pt) partBases[id] = pt.Opacity;
                _expressionStack.Add(new ExpressionEntry
                {
                    Custom = custom, Name = name, StartTime = _time,
                    ParamBases = paramBases, PartBases = partBases,
                });
            }
            else if (_expressions.FirstOrDefault(x => x.name == name).expr is { } expr)
            {
                _expressionStack.Add(new ExpressionEntry { Expr = expr, Name = name, StartTime = _time });
            }
        }
        OnExpressionChanged?.Invoke(name);
    }

    public string CurrentExpressionName => _expressionStack.FirstOrDefault(e => !e.FadingOut)?.Name;

    // ---- 自定义表情(用户捏的参数预设) ----

    /// <summary>
    /// 把模型当前参数/部件状态存为自定义表情:参数只记录偏离默认值的差量,
    /// 部件全量记录(形态意图自包含,不随后续兜底基线漂移)。
    /// 建议在无动作干扰时(如先 StopMotions)保存。
    /// 同名时覆盖旧自定义;若与内置 exp3 表情重名,则遮蔽该内置表情(播放走自定义)。
    /// </summary>
    public void SaveCustomExpression(string name)
    {
        var targets = new Dictionary<string, float>();
        foreach (var p in Model.Parameters)
            if (Math.Abs(p.Value - p.Default) > 0.01f) targets[p.Id] = p.Value;
        // 部件全量记录(而非只记差量):表情对形态的意图必须自包含——
        // 若只存"偏离 setup 的部件",之后宿主设置的兜底基线会覆盖表情未记录的部件
        // (如先捏表情再设兜底隐藏手臂,表情触发时手臂仍是隐藏的)
        var partTargets = new Dictionary<string, float>();
        for (int i = 0; i < Model.Parts.Length && i < _setupPartOpacity.Length; i++)
            partTargets[Model.Parts[i].Id] = Model.Parts[i].Opacity;
        _customExpressions[name] = new CustomExpression { Name = name, Targets = targets, PartTargets = partTargets };
    }

    /// <summary>
    /// 用给定的目标值直接构建自定义表情(宿主编辑器的勾选式捕捉:只记录勾选的参数/部件,
    /// 值来自滑杆,不经模型当前状态,无时序问题)。参数为目标绝对值,部件为不透明度 0..1,同名覆盖。
    /// </summary>
    public void SaveCustomExpressionFrom(string name,
        IReadOnlyDictionary<string, float> paramTargets, IReadOnlyDictionary<string, float> partTargets)
    {
        _customExpressions[name] = new CustomExpression
        {
            Name = name,
            Targets = new Dictionary<string, float>(paramTargets),
            PartTargets = new Dictionary<string, float>(partTargets),
        };
    }

    /// <summary>全部自定义表情名(排序返回)。</summary>
    public string[] GetCustomExpressionNames()
    {
        var names = _customExpressions.Keys.ToArray();
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    public bool HasCustomExpression(string name) => _customExpressions.ContainsKey(name);

    /// <summary>
    /// 读取一个自定义表情的存储内容(参数目标值 + 部件目标值,均为偏离默认/初始的差量)。
    /// 宿主编辑器用它把预设的存储值载入滑杆(而非读模型当前值,避免受动作/表情混合影响)。
    /// 内置 exp3 表情或名字不存在返回 null。
    /// </summary>
    public (Dictionary<string, float> Targets, Dictionary<string, float> PartTargets)? GetCustomExpression(string name)
        => _customExpressions.TryGetValue(name, out var e) ? (new Dictionary<string, float>(e.Targets), new Dictionary<string, float>(e.PartTargets)) : null;

    /// <summary>删除自定义表情;若它正生效,先转入淡出(否则条目持有定义引用会永久卡住)。</summary>
    public void RemoveCustomExpression(string name)
    {
        _customExpressions.Remove(name);
        foreach (var e in _expressionStack)
            if (!e.FadingOut && e.Name == name)
            {
                e.FadingOut = true;
                e.StartTime = _time;
            }
    }

    /// <summary>
    /// 重命名自定义表情(宿主编辑器内联改名用)。若旧名正生效,栈内条目名一并更新,预览不中断;
    /// 旧名不存在、新名为空或已占用时返回 false(内置 exp3 不在本表,天然不可改)。
    /// </summary>
    public bool RenameCustomExpression(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || !_customExpressions.Remove(oldName, out var entry))
            return false;
        if (_customExpressions.ContainsKey(newName))
        {
            _customExpressions[oldName] = entry; // 改名失败:放回原位
            return false;
        }
        entry.Name = newName;
        _customExpressions[newName] = entry;
        foreach (var e in _expressionStack)
            if (e.Name == oldName)
                e.Name = newName;
        return true;
    }

    /// <summary>立即清除全部表情(不做淡出混合):编辑器开始拖动参数时调用,
    /// 避免淡出期间的逐帧混合覆写导致滑杆改不动。参数停在最后应用值,可自由修改。</summary>
    public void ClearExpressionImmediate() => _expressionStack.Clear();

    /// <summary>全部部件恢复到加载时的初始不透明度(setup 姿态;清除兜底形态后调用可回到原始外观)。</summary>
    public void ResetPartsToSetup()
    {
        for (int i = 0; i < Model.Parts.Length && i < _setupPartOpacity.Length; i++)
            Model.Parts[i].Opacity = _setupPartOpacity[i];
    }

    /// <summary>
    /// 部件在加载时(setup 姿态)的初始不透明度。宿主编辑器把预设未涉及的部件复位到该值——
    /// 若用"当前模型值"兜底,前一个表情留下的部件状态会泄漏到下一个表情。
    /// </summary>
    public float GetSetupPartOpacity(string id)
    {
        var index = Array.FindIndex(Model.Parts, p => p.Id == id);
        return index >= 0 && index < _setupPartOpacity.Length ? _setupPartOpacity[index] : 0f;
    }

    /// <summary>
    /// sidecar 约定路径:与 model3.json 同目录的 &lt;模型名&gt;.customexp.json
    /// (模型名 = 去掉 .model3.json 后缀的文件名,如 Hiyori.model3.json → Hiyori.customexp.json)。
    /// </summary>
    public static string CustomExpressionSidecarPath(string model3Path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(model3Path)) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(model3Path);   // 去掉 .json
        if (baseName.EndsWith(".model3", StringComparison.OrdinalIgnoreCase))
            baseName = baseName.Substring(0, baseName.Length - ".model3".Length);
        return Path.Combine(dir, baseName + ".customexp.json");
    }

    /// <summary>把全部自定义表情保存到 JSON 文件(宿主持久化用;sidecar 约定见 Load)。</summary>
    public void SaveCustomExpressions(string path)
    {
        var doc = new
        {
            version = 1,
            expressions = _customExpressions.Values.Select(c => new
            {
                name = c.Name,
                fadeInTime = c.FadeInTime,
                fadeOutTime = c.FadeOutTime,
                targets = c.Targets,
                partTargets = c.PartTargets,
            }).ToArray(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOpts));
    }

    /// <summary>从 JSON 文件加载自定义表情(同名覆盖)。返回加载条数。</summary>
    public int LoadCustomExpressions(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        int count = 0;
        if (json.RootElement.TryGetProperty("expressions", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (!el.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrEmpty(name)) continue;
                var c = new CustomExpression { Name = name };
                if (el.TryGetProperty("fadeInTime", out var fi)) c.FadeInTime = Math.Max(0.01f, fi.GetSingle());
                if (el.TryGetProperty("fadeOutTime", out var fo)) c.FadeOutTime = Math.Max(0.01f, fo.GetSingle());
                ReadTargets(el, "targets", c.Targets);
                ReadTargets(el, "partTargets", c.PartTargets);
                _customExpressions[name] = c;
                count++;
            }
        }
        return count;
    }

    static void ReadTargets(JsonElement el, string prop, Dictionary<string, float> into)
    {
        if (el.TryGetProperty(prop, out var obj) && obj.ValueKind == JsonValueKind.Object)
            foreach (var kv in obj.EnumerateObject())
                into[kv.Name] = kv.Value.GetSingle();
    }

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ================ 参数精细控制 ================

    /// <summary>所有参数 Id。</summary>
    public string[] GetParameterIds() => Model.Parameters.Select(p => p.Id).ToArray();

    public float GetParameterValue(string id) => Model.GetParameter(id)?.Value ?? 0f;
    public float GetParameterMinimum(string id) => Model.GetParameter(id)?.Minimum ?? 0f;
    public float GetParameterMaximum(string id) => Model.GetParameter(id)?.Maximum ?? 0f;
    public float GetParameterDefault(string id) => Model.GetParameter(id)?.Default ?? 0f;

    /// <summary>参数显示名(cdi3.json;没有显示信息时返回 Id 本身)。</summary>
    public string GetParameterDisplayName(string id) => Cdi?.GetParameterName(id) ?? id;

    /// <summary>设置参数值(自动夹取到范围内)。注意:动作每帧会覆盖它驱动的参数。</summary>
    public void SetParameterValue(string id, float value)
    {
        var p = Model.GetParameter(id);
        if (p != null) p.Value = Math.Clamp(value, p.Minimum, p.Maximum);
    }

    /// <summary>把参数加/乘一个增量(同样夹取范围)。</summary>
    public void AddParameterValue(string id, float delta)
    {
        var p = Model.GetParameter(id);
        if (p != null) p.Value = Math.Clamp(p.Value + delta, p.Minimum, p.Maximum);
    }

    public void MultiplyParameterValue(string id, float factor)
    {
        var p = Model.GetParameter(id);
        if (p != null) p.Value = Math.Clamp(p.Value * factor, p.Minimum, p.Maximum);
    }

    /// <summary>把参数复位到默认值。</summary>
    public void ResetParameter(string id) => SetParameterValue(id, GetParameterDefault(id));

    /// <summary>复位所有参数到默认值(setup pose)。</summary>
    public void ResetAllParameters()
    {
        foreach (var p in Model.Parameters) p.Value = p.Default;
        Model.Update();
    }

    /// <summary>参数快照(宿主可保存/恢复自定义姿态)。</summary>
    public Dictionary<string, float> SaveParameters()
        => Model.Parameters.ToDictionary(p => p.Id, p => p.Value);

    public void RestoreParameters(Dictionary<string, float> snapshot)
    {
        foreach (var (id, v) in snapshot) SetParameterValue(id, v);
    }

    // ================ 部件控制 ================

    public string[] GetPartIds() => Model.Parts.Select(p => p.Id).ToArray();
    public float GetPartOpacity(string id) => Model.GetPart(id)?.Opacity ?? 0f;
    public void SetPartOpacity(string id, float opacity)
    {
        var p = Model.GetPart(id);
        if (p != null) p.Opacity = opacity;
        _pose?.NotifyPartOpacity(id, opacity); // 同步姿态镜像:否则基线/手动写入在下一帧被姿态目标覆盖
    }

    // ================ Drawable / 命中检测 ================

    public string[] GetDrawableIds() => Model.Drawables.Select(d => d.Id).ToArray();

    /// <summary>命中区域名(Body / Head 等,来自 model3.json)。</summary>
    public string[] GetHitAreaNames() => ModelJson?.HitAreas?.Select(h => h.Name).ToArray() ?? Array.Empty<string>();

    /// <summary>
    /// 命中检测:模型坐标系下的一点(如鼠标反算坐标)落在哪个命中区域内。
    /// 返回命中区域名,未命中返回 null。
    /// </summary>
    public string HitTest(float x, float y)
    {
        if (Model == null || ModelJson?.HitAreas == null) return null;
        foreach (var area in ModelJson.HitAreas)
        {
            int di = Model.FindDrawableIndex(area.Id);
            if (di < 0) continue;
            var d = Model.Drawables[di];
            if (!d.Visible) continue;
            if (PointInDrawable(d, x, y)) return area.Name;
        }
        return null;
    }

    static bool PointInDrawable(CubismDrawable d, float x, float y)
    {
        for (int i = 0; i + 2 < d.Indices.Length; i += 3)
        {
            float ax = d.VertexPositions[d.Indices[i] * 2], ay = d.VertexPositions[d.Indices[i] * 2 + 1];
            float bx = d.VertexPositions[d.Indices[i + 1] * 2], by = d.VertexPositions[d.Indices[i + 1] * 2 + 1];
            float cx = d.VertexPositions[d.Indices[i + 2] * 2], cy = d.VertexPositions[d.Indices[i + 2] * 2 + 1];
            if (PointInTriangle(x, y, ax, ay, bx, by, cx, cy)) return true;
        }
        return false;
    }

    static bool PointInTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        float d1 = Sign(px, py, ax, ay, bx, by);
        float d2 = Sign(px, py, bx, by, cx, cy);
        float d3 = Sign(px, py, cx, cy, ax, ay);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    static float Sign(float px, float py, float ax, float ay, float bx, float by)
        => (px - bx) * (ay - by) - (ax - bx) * (py - by);

    // ================ 视图辅助 ================

    /// <summary>模型包围盒(模型坐标系,由当前 Drawable 顶点计算)。用于自适应视图。</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) GetModelBounds()
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var d in Model.Drawables)
        {
            if (!d.Visible || d.VertexCount == 0) continue;
            for (int v = 0; v < d.VertexCount; v++)
            {
                float x = d.VertexPositions[v * 2], y = d.VertexPositions[v * 2 + 1];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        }
        if (minX > maxX) return (0, 0, 100, 100);
        return (minX, minY, maxX, maxY);
    }

    public void Dispose()
    {
        Model?.Dispose();
        Model = null;
        _motionGroups.Clear();
        _expressions.Clear();
        _customExpressions.Clear();
        _motionQueue.Clear();
        _expressionStack.Clear();
    }
}

/// <summary>
/// 自定义表情(用户捏的参数预设):一组参数目标值 + 部件不透明度目标值,Overwrite 语义。
/// 与内置 exp3 表情的区别:应用时快照基准值、按 base+(target-base)*w 线性混合,
/// 淡出结束精确还原到应用前状态(内置表情的相对混合做不到)。
/// </summary>
public class CustomExpression
{
    public string Name { get; set; }
    /// <summary>参数 Id → 目标值。</summary>
    public Dictionary<string, float> Targets { get; set; } = new();
    /// <summary>部件 Id → 目标不透明度(如腮红)。</summary>
    public Dictionary<string, float> PartTargets { get; set; } = new();
    public float FadeInTime { get; set; } = 0.15f;
    public float FadeOutTime { get; set; } = 0.6f;
}
