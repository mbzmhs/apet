using System;
using System.IO;
using APet.Renderer.Spine.Runtime;

namespace APet.Renderer.Spine;

/// <summary>
/// 封装 Spine 骨架 + 动画状态:加载、更新、动作切换、参数调整。
/// </summary>
public class SpineStage : IDisposable
{
    readonly GLTextureLoader _textureLoader = new();

    public Atlas Atlas { get; private set; }
    public SkeletonData SkeletonData { get; private set; }
    public Skeleton Skeleton { get; private set; }
    public AnimationStateData StateData { get; private set; }
    public AnimationState State { get; private set; }

    public string SourcePath { get; private set; }

    /// <summary>播放速度倍率。</summary>
    public float TimeScale { get; set; } = 1f;

    /// <summary>暂停后 State.TimeScale 为 0。</summary>
    public bool Paused { get; set; }

    /// <summary>动画事件(用于日志显示)。</summary>
    public event Action<TrackEntry, APet.Renderer.Spine.Runtime.Event> OnEvent;

    /// <summary>轨道动画完整播放一遍(或被中断前播完)时触发。</summary>
    public event Action<TrackEntry> OnTrackComplete;

    public bool IsLoaded => SkeletonData != null;

    /// <summary>加载骨架。skeletonPath 为 .json 或 .skel;atlasPath 为 .atlas 文件。
    /// 重复调用会先释放上一个模型的 GPU 纹理,支持运行中切换模型。</summary>
    public void Load(string skeletonPath, string atlasPath)
    {
        if (Atlas != null) _textureLoader.DisposeAll();

        Atlas = new Atlas(atlasPath, _textureLoader);
        var attachmentLoader = new AtlasAttachmentLoader(Atlas);

        SkeletonData skeletonData;
        if (string.Equals(Path.GetExtension(skeletonPath), ".json", StringComparison.OrdinalIgnoreCase))
            skeletonData = new SkeletonJson(attachmentLoader).ReadSkeletonData(skeletonPath);
        else
            skeletonData = new SkeletonBinary(attachmentLoader).ReadSkeletonData(skeletonPath);

        SkeletonData = skeletonData;
        SourcePath = skeletonPath;
        Skeleton = new Skeleton(skeletonData);
        StateData = new AnimationStateData(skeletonData) { DefaultMix = 0.2f };
        State = new AnimationState(StateData);
        State.Event += (entry, e) => OnEvent?.Invoke(entry, e);
        State.Complete += entry => OnTrackComplete?.Invoke(entry);
    }

    /// <summary>每帧更新。delta 单位秒。</summary>
    public void Update(float delta)
    {
        if (Skeleton == null) return;
        delta = Math.Min(delta, 0.1f);
        State.TimeScale = Paused ? 0f : TimeScale;
        Skeleton.Update(delta);
        State.Update(delta);
        State.Apply(Skeleton);
        Skeleton.UpdateWorldTransform(Skeleton.Physics.Update);
    }

    /// <summary>在指定轨道播放动画(默认 0 号)。多轨道用法:0=下半身移动,1=上半身动作等,各轨道独立循环/速度。</summary>
    public TrackEntry Play(string animationName, bool loop, float mix = -1f, int trackIndex = 0)
    {
        var entry = State.SetAnimation(trackIndex, animationName, loop);
        if (mix >= 0) entry.MixDuration = mix;
        return entry;
    }

    /// <summary>清空指定轨道并淡出为空动画(SetEmptyAnimation),轨道动画在 mixTime 秒内淡出,用于"退出叠加动作"。</summary>
    public TrackEntry SetEmptyAnimation(int trackIndex, float mixSeconds = 0.2f)
        => State.SetEmptyAnimation(trackIndex, mixSeconds);

    public void Stop()
    {
        State.ClearTrack(0);
        Skeleton.SetToSetupPose();
    }

    /// <summary>设置皮肤(名称为 null 时用 default)。</summary>
    public void SetSkin(string skinName)
    {
        if (Skeleton == null) return;
        var skin = skinName == null ? SkeletonData.DefaultSkin : SkeletonData.FindSkin(skinName);
        Skeleton.SetSkin(skin);
        Skeleton.SetSlotsToSetupPose();
    }

    /// <summary>直接设置某槽位当前显示的附件(如表情切换),attachmentName 为 null 时清空该槽位。
    /// 查找顺序:当前皮肤 → default 皮肤。附件名可用 GetAttachmentNames(slotName) 查询。</summary>
    public bool SetAttachment(string slotName, string attachmentName)
    {
        var slot = Skeleton?.FindSlot(slotName);
        if (slot == null) return false;
        if (attachmentName == null)
        {
            slot.Attachment = null;
            return true;
        }
        var attachment = Skeleton.GetAttachment(slotName, attachmentName);
        if (attachment == null) return false;
        slot.Attachment = attachment;
        return true;
    }

    public TrackEntry CurrentTrack => State?.GetCurrent(0);

    /// <summary>所有动画名。</summary>
    public string[] GetAnimationNames()
    {
        if (SkeletonData == null) return Array.Empty<string>();
        var names = new string[SkeletonData.Animations.Count];
        for (int i = 0; i < names.Length; i++) names[i] = SkeletonData.Animations.Items[i].Name;
        return names;
    }

    /// <summary>所有皮肤名(跳过空项)。</summary>
    public string[] GetSkinNames()
    {
        if (SkeletonData == null) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var skin in SkeletonData.Skins.Items)
            if (!string.IsNullOrEmpty(skin?.Name)) list.Add(skin.Name);
        return list.ToArray();
    }

    /// <summary>查询动画时长(秒)。找不到时返回 0。</summary>
    public float GetAnimationDuration(string animationName)
    {
        var anim = SkeletonData?.FindAnimation(animationName);
        return anim?.Duration ?? 0f;
    }

    /// <summary>所有槽位名。</summary>
    public string[] GetSlotNames()
    {
        if (SkeletonData == null) return Array.Empty<string>();
        var names = new string[SkeletonData.Slots.Count];
        for (int i = 0; i < names.Length; i++) names[i] = SkeletonData.Slots.Items[i].Name;
        return names;
    }

    /// <summary>某槽位的所有附件名。skinName 为 null 时读当前皮肤(SetSkin 已切换的),
    /// 并按 Spine 的查找顺序并入 default 皮肤的条目(皮肤可以只覆盖部分槽位)。</summary>
    public string[] GetAttachmentNames(string slotName, string skinName = null)
    {
        var slot = SkeletonData?.FindSlot(slotName);
        if (slot == null) return Array.Empty<string>();

        Skin primary = null;
        if (skinName != null)
            primary = SkeletonData.FindSkin(skinName);
        else
            primary = Skeleton?.Skin ?? SkeletonData.DefaultSkin;

        var skins = new List<Skin>();
        if (primary != null) skins.Add(primary);
        var def = SkeletonData.DefaultSkin;
        if (def != null && !skins.Contains(def)) skins.Add(def);

        var names = new List<string>();
        foreach (var skin in skins)
            foreach (var entry in skin.Attachments)
                if (entry.SlotIndex == slot.Index && !names.Contains(entry.Name))
                    names.Add(entry.Name);
        return names.ToArray();
    }

    /// <summary>动画元信息。</summary>
    public sealed class AnimationInfo
    {
        public string Name;
        public float Duration;
        public string[] EventNames;
        /// <summary>该动画切换显示的附件名(表情、换装等,即动作的"语义标签")。</summary>
        public string[] AttachmentNames;
    }

    /// <summary>动画元信息:时长 + 触发的事件 + 切换的附件。Spine 格式没有"动作注释"字段,
    /// 语义信息(如 smile/angry)通常在动画名、附件名或事件里。</summary>
    public AnimationInfo GetAnimationInfo(string animationName)
    {
        var anim = SkeletonData?.FindAnimation(animationName);
        if (anim == null) return null;
        var eventNames = new List<string>();
        var attachmentNames = new List<string>();
        foreach (var t in anim.Timelines.Items)
        {
            if (t is EventTimeline et && et.Events != null)
                foreach (var e in et.Events)
                    if (e?.Data != null && !eventNames.Contains(e.Data.Name))
                        eventNames.Add(e.Data.Name);
            if (t is AttachmentTimeline at && at.AttachmentNames != null)
                foreach (var n in at.AttachmentNames)
                    if (!string.IsNullOrEmpty(n) && !attachmentNames.Contains(n))
                        attachmentNames.Add(n);
        }
        return new AnimationInfo
        {
            Name = anim.Name,
            Duration = anim.Duration,
            EventNames = eventNames.ToArray(),
            AttachmentNames = attachmentNames.ToArray(),
        };
    }

    /// <summary>在指定轨道排队一个动画:该轨道当前动画播完 delay 秒后过渡到它。</summary>
    public TrackEntry Queue(string animationName, bool loop, float delay, int trackIndex = 0)
    {
        return State.AddAnimation(trackIndex, animationName, loop, delay);
    }

    /// <summary>清空全部轨道。</summary>
    public void ClearTracks()
    {
        State.ClearTracks();
        Skeleton.SetToSetupPose();
    }

    /// <summary>清空指定轨道(不整体重置骨架)。</summary>
    public void ClearTrack(int trackIndex) => State.ClearTrack(trackIndex);

    /// <summary>取指定轨道的当前条目(可读 TrackTime/TimeScale/Alpha 等,可写实现逐轨道控制)。</summary>
    public TrackEntry GetTrack(int trackIndex = 0) => State?.GetCurrent(trackIndex);

    public void Dispose()
    {
        _textureLoader.DisposeAll();
        State = null;
        StateData = null;
        Skeleton = null;
        SkeletonData = null;
        Atlas = null;
    }
}
