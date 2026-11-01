<script setup lang="ts">
// 插件自定义 UI:同源 iframe 伺服 DLL 内嵌 ui.* 资产(插件页自己引用 /apet-bridge.js?plugin=… 获得桥)。
// 职责:① 主题 token 同步(子页 load 时推 + 宿主换肤 MutationObserver 实时推);
// ② 卸载时回收插件标记过(data-APet-plugin="name")的宿主 DOM 节点与样式(mark-and-sweep)。
// iframe URL 查询参数(agentId/sessionId/slot)由插件页桥读取,故此处只拼 index.html 路径。
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

const props = withDefaults(defineProps<{
  agentId: string
  pluginName: string
  slot?: string
  sessionId?: string
  height?: string
}>(), { slot: 'page', sessionId: '', height: '100%' })

const el = ref<HTMLElement | null>(null)
// computed:会话/插槽变化时 src 更新 → iframe 重载(插件页按新上下文重新初始化)
const src = computed(() =>
  `/api/agents/${props.agentId}/external-plugins/${encodeURIComponent(props.pluginName)}/ui/index.html?agentId=${encodeURIComponent(props.agentId)}&sessionId=${encodeURIComponent(props.sessionId)}&slot=${encodeURIComponent(props.slot)}`)

function syncTokens() {
  const doc = el.value?.contentDocument
  if (!doc) return
  const root = getComputedStyle(document.documentElement)
  const mine = doc.documentElement
  // 与 apet-bridge.js 的 TOKENS 保持同一名单(宿主换肤重推用)
  for (const t of [
    '--bg-base', '--bg-panel', '--bg-panel-hover', '--bg-panel-translucent', '--bg-input', '--bg-code', '--bg-tool',
    '--border', '--fg-primary', '--fg-secondary', '--fg-muted', '--fg-accent', '--accent',
    '--accent-sub', '--accent-sub-bg', '--danger', '--danger-bg', '--success', '--warning',
    '--selection', '--gutter-bg', '--code-fg', '--font', '--radius-panel', '--radius-bubble',
  ]) {
    const v = root.getPropertyValue(t)
    if (v) mine.style.setProperty(t, v)
  }
}

let obs: MutationObserver | null = null
let lateTimer: number | null = null
onMounted(() => {
  el.value?.addEventListener('load', syncTokens)
  // 子页脚本可能覆写变量:首屏后再推一次兜底
  lateTimer = window.setTimeout(syncTokens, 500)
  obs = new MutationObserver(syncTokens)
  obs.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
})

/** 卸载回收:插件注入宿主 DOM 的节点/样式全部按标记移除(插件禁用/切页/关模态统一走这里) */
function sweep() {
  const sel = `[data-APet-plugin="${CSS.escape(props.pluginName)}"]`
  document.querySelectorAll(sel).forEach((n) => n.remove())
}
onBeforeUnmount(() => {
  obs?.disconnect()
  if (lateTimer !== null) window.clearTimeout(lateTimer)
  sweep()
})
</script>

<template>
  <iframe
    ref="el"
    :src="src"
    class="plugin-ui-frame"
    :style="{ height: height }"
    :title="`插件界面 ${pluginName}`"
  />
</template>

<style scoped>
.plugin-ui-frame {
  width: 100%;
  border: 0;
  display: block;
  background: transparent;
}
</style>
