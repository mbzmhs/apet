using System.Reflection;
using APet.Framework.Logging;

namespace APet.Framework.Plugin;

/// <summary>
/// 插件设置 ↔ 宿主配置的反射桥:按插件声明的 [PluginSetting] 名,在宿主配置节(section)上找
/// 同名属性(忽略大小写)取值,经 TrySetSetting 推送(自动触发插件的 OnSettingChanged 副作用)。
/// 目的:消灭"插件 [PluginSetting] + 宿主 typed 配置类 + AgentHost 手写 SetSettings 字典"的三处重复——
/// 宿主只需保证配置属性与插件设置同名,新增插件设置不再要求改宿主装配代码。
/// 找不到同名属性的设置记 Debug 日志(漂移从此可见:要么配置缺字段,要么该设置本就不由宿主管理);
/// 推送被插件校验拒绝(Options/Min/Max/长度)记 Error 日志并跳过,不中断其余设置。
/// </summary>
public static class PluginConfigBridge
{
    /// <summary>
    /// 把一个或多个配置节的值推送到插件。
    /// aliases:键 = 插件设置名,值 = 配置属性名。属性名与设置名不同(如插件带引擎前缀 "gsvVoice"
    /// 而配置节内就叫 "Voice")时必须声明;值为 "节类型/属性名" 时按节类型限定(用于多个节存在同名
    /// 属性、纯属性名会歧义的情况,如 Server.Voice 与 Gsv.Voice)。
    /// transforms:键 = 插件设置名,值 = 配置原值 → 设置值的转换
    /// (如 List&lt;T&gt; → 插件期望的 JSON 字符串、单位换算、范围夹取)。
    /// 返回推送成功的设置数。
    /// </summary>
    public static int Push(AgentPluginBase plugin,
                           IReadOnlyDictionary<string, Func<object, object?>>? transformsIn,
                           IReadOnlyDictionary<string, string>? aliasesIn,
                           params object?[] sections)
    {
        // 插件设置名不区分大小写(FindSetting 语义),别名/transform 字典对齐该语义
        var aliases = aliasesIn is null ? null
            : new Dictionary<string, string>(aliasesIn, StringComparer.OrdinalIgnoreCase);
        var transforms = transformsIn is null ? null
            : new Dictionary<string, Func<object, object?>>(transformsIn, StringComparer.OrdinalIgnoreCase);
        var applied = 0;
        foreach (var setting in plugin.GetSettings())
        {
            string? propPattern = aliases?.GetValueOrDefault(setting.Name) ?? setting.Name;
            string? qualifier = null;
            var propName = propPattern;
            var slash = propPattern.IndexOf('/');
            if (slash >= 0)
            {
                qualifier = propPattern[..slash];
                propName = propPattern[(slash + 1)..];
            }

            object? value = null;
            var found = false;
            foreach (var section in sections)
            {
                if (section is null) continue;
                if (qualifier is not null &&
                    !string.Equals(section.GetType().Name, qualifier, StringComparison.OrdinalIgnoreCase))
                    continue;
                var prop = section.GetType().GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop is null || prop.GetMethod is null) continue;
                value = prop.GetValue(section);
                found = true;
                break;
            }

            if (!found)
                continue; // 宿主未托管该设置:保持插件默认值(启动期逐条告警曾占日志 6%,静默跳过)
            if (transforms is not null && transforms.TryGetValue(setting.Name, out var transform))
                value = transform(value!);
            if (value is null)
                continue; // 配置值为空:保持插件当前值

            if (plugin.TrySetSetting(setting.Name, value, out var error))
                applied++;
            else
                Log.Global.Error($"[ConfigBridge] {plugin.Name}.{setting.Name}: rejected: {error}");
        }
        return applied;
    }
}
