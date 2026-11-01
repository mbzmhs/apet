namespace APet.Renderer;

/// <summary>点击宠物模型时的响应动作。</summary>
public enum PetClickAction
{
    /// <summary>不响应。</summary>
    None,

    /// <summary>从非待机动作里随机播一个(一次性,播完回待机)。</summary>
    RandomMotion,

    /// <summary>立即回到待机动作。</summary>
    Idle,

    /// <summary>请求宿主打开/激活聊天窗口。</summary>
    OpenChat,
}

/// <summary>
/// 动作的展示信息:Name 是用于映射/播放的规范名;Detail 是含语义提示与时长的人类可读全名
/// (如 "NP0045_spr_HD  smile,happy  (2.30s)"),供设置页一目了然地辨识动作。
/// </summary>
public sealed class PetMotionInfo
{
    public required string Name { get; init; }
    public required string Detail { get; init; }
}

/// <summary>
/// 桌宠渲染引擎的统一接口:Spine / Live2D / VRM / 基础(图片文件夹)各自实现,
/// 插件层通过本接口与引擎交互(加载模型、播放动作、取动作列表、收点击事件),
/// 与渲染库的具体类型完全解耦——其他插件/宿主经由 DesktopPetPlugin 间接使用。
/// </summary>
public interface IPetRenderer : IDisposable
{
    /// <summary>引擎标识(spine / live2d / vrm / basic)。</summary>
    string Engine { get; }

    /// <summary>引擎的显示名(用于设置页与日志)。</summary>
    string EngineDisplayName { get; }

    /// <summary>模型是否已成功加载并在显示。</summary>
    bool IsLoaded { get; }

    /// <summary>最近一次加载失败的错误信息(成功为 null)。</summary>
    string? LoadError { get; }

    /// <summary>
    /// 加载模型文件。modelPath 按引擎解释(Spine: .skel/.json 骨架文件;
    /// Live2D: .model3.json)。atlasPath 为 Spine 图集的可选覆盖(空 = 自动查找)。
    /// 成功返回 null,失败返回错误信息(窗口保持隐藏)。
    /// </summary>
    string? Load(string modelPath, string? atlasPath);

    /// <summary>卸载当前模型并隐藏窗口。</summary>
    void Unload();

    /// <summary>当前模型可用动作名列表(排序后返回;未加载为空)。</summary>
    string[] GetMotionNames();

    /// <summary>当前模型可用动作的详细信息(排序后返回;未加载为空)。Detail 供设置页展示。</summary>
    PetMotionInfo[] GetMotionInfos();

    /// <summary>播放一个动作。loop=true 循环(如待机),否则播完自动回待机(由插件层负责)。</summary>
    void Play(string motionName, bool loop);

    /// <summary>
    /// 按情绪触发:names 是该情绪映射到的候选名(表情/动作混装)。默认实现随机挑一个交给 Play
    /// (Spine/Live2D/基础引擎沿用此「二选一」语义);VRM 覆写为「表情 + 动作同时生效」。
    /// </summary>
    void TriggerEmotion(IReadOnlyList<string> names)
    {
        if (names.Count > 0)
            Play(names[Random.Shared.Next(names.Count)], false);
    }

    /// <summary>设置可导入的动画文件路径列表(仅 VRM 引擎有实现,其余为空操作)。可在模型加载前调用,加载后自动生效。</summary>
    void SetAnimations(IReadOnlyList<string> animationPaths) { }

    /// <summary>
    /// 设置显示缩放因子(屏幕像素 / 模型单位,1 = 原始尺寸)。实时生效:
    /// 已加载的引擎据此调整窗口大小/比例;未加载时记录,加载后自动应用。
    /// </summary>
    void SetScale(float scale);

    /// <summary>设置悬浮窗左上角屏幕坐标。已显示时立即生效;未显示则记住,加载后应用。</summary>
    void SetPosition(int x, int y);

    /// <summary>当前正在播放的动作名(可能为 null)。</summary>
    string? CurrentMotion { get; }

    /// <summary>是否仍有动作在播(含循环待机)。插件层据此判断"最后一个动作播完"再回休息态/重排程。</summary>
    bool IsMotionPlaying { get; }

    /// <summary>悬浮窗当前屏幕包围盒(左上角 + 尺寸,屏幕像素);未显示时返回 Empty。供其他插件锚定(如对话气泡)。</summary>
    System.Drawing.Rectangle GetWindowBounds() { return System.Drawing.Rectangle.Empty; }

    /// <summary>模型像素被点击:button 为 "left" / "right" / "middle"。</summary>
    event Action<string>? ModelClicked;

    /// <summary>一个动作播完(循环动作每圈触发一次;插件层据此回待机/轮换待机)。</summary>
    event Action<string>? MotionCompleted;

    /// <summary>用户拖动窗口后触发,参数为新的左上角屏幕坐标(供宿主持久化)。</summary>
    event Action<int, int>? PositionMoved;

    /// <summary>
    /// 设置兜底部件不透明度(部件 Id → 0..1):模型加载完成后应用,作为默认形态;
    /// 表情淡出会精确还原到该基线而非 setup 姿态。仅 Live2D 引擎有部件概念,其余引擎为空操作。
    /// </summary>
    void SetPartOpacities(IReadOnlyDictionary<string, float> opacities) { }

    /// <summary>
    /// 回到休息形态:参数复位默认、部件回到「setup + 兜底」基线。
    /// Live2D 待机动作一次性播完时调用(播完不循环、回原状);其余引擎为空操作。
    /// </summary>
    void ResetToRest() { }

    /// <summary>设置切换动作时的过渡效果(基础引擎的淡入淡出;其余引擎为空操作)。</summary>
    void SetTransition(bool enabled, int durationMs) { }

    /// <summary>设置悬浮窗标题(如 "APet - 优香(Spine)"),供 OBS 等按窗口标题识别捕获。</summary>
    void SetWindowTitle(string title) { }

    /// <summary>设置渲染风格(0=二次元 toon,1=写实 pbr,2=官方 mtoon;仅 VRM 引擎有区别)。</summary>
    void SetShading(int mode) { }

    /// <summary>Live2D 内建自动眨眼开关(模型加载后生效,未加载则记住);其余引擎为空操作。</summary>
    void SetAutoEyeBlink(bool enabled) { }

    /// <summary>Live2D 呼吸动画开关(周期性身体起伏);其余引擎为空操作。</summary>
    void SetBreathEnabled(bool enabled) { }
}
