<script setup lang="ts">
// 底部状态栏:配色对齐 WPF 标题栏(--bg-panel #222227)。
// 左起依次为:模型 | 输入(prompt) | 缓存命中(cached + 会话命中率) | 输出(completion) |
// TPS(最新一次请求的 token 输出速度,服务端实测) | 插件状态(数据面 status 字段,如 GPU)。
// 累计值由会话文件持久化(宿主 OnUsageReported 累加落盘),重开会话从快照种子继续。
import { computed, onBeforeUnmount, onMounted, reactive, watch } from 'vue'
import { setSidebarTarget, subscribeSidebar } from '../store/sidebarPoll'

const props = defineProps<{
  agentId: string
  sessionId: string
  model: string
  /** 累计用量:promptTotal/cachedTotal/completionTotal 会话累计;lastTps 最新请求 TPS(服务端实测);cost 累计费用 */
  usage: { promptTotal: number; cachedTotal: number; completionTotal: number; lastTps: number; cost: number }
  streaming: boolean
}>()

const pluginStatuses = reactive<{ name: string; status: string }[]>([])
let unsub: (() => void) | null = null

// 插件状态走共享轮询器(与 SidePanel 同一数据面、同一节拍):此前各自定时器把同一端点打到双倍频率
onMounted(() => {
  setSidebarTarget(props.agentId, props.sessionId)
  unsub = subscribeSidebar((info) => {
    pluginStatuses.length = 0
    for (const sp of info.statusPlugins ?? []) {
      if (sp.status) pluginStatuses.push(sp)
    }
  })
})
onBeforeUnmount(() => {
  unsub?.()
})

// 深链解析后 agentId 才就绪/切会话:同步轮询目标
watch([() => props.agentId, () => props.sessionId], ([a, s]) => setSidebarTarget(a, s))

function fmtK(n: number): string {
  if (n >= 1000000) return (n / 1000000).toFixed(1) + 'M'
  if (n >= 1000) return (n / 1000).toFixed(1) + 'k'
  return String(n)
}

function fmtCost(yuan: number): string {
  return yuan < 0.01 ? yuan.toFixed(4) : yuan.toFixed(3)
}

const hitRate = computed(() =>
  props.usage.promptTotal > 0 ? Math.round((props.usage.cachedTotal / props.usage.promptTotal) * 100) + '%' : null)
// TPS 一律用服务端实测值(usage 帧 tps:宿主 Stopwatch + 供应商真实 token 数),不做前端估算
const tps = computed(() => Math.round(props.usage.lastTps))
const hasTotals = computed(() =>
  props.usage.promptTotal > 0 || props.usage.cachedTotal > 0 || props.usage.completionTotal > 0)
const visible = computed(() => Boolean(props.model) || hasTotals.value || pluginStatuses.length > 0)
</script>

<template v-if="visible">
  <div class="statusbar">
    <span v-if="model" class="sb-item sb-model">
      <span class="ms sb-icon accent">psychology</span>
      <span class="sb-name">{{ model }}</span>
    </span>
    <!-- 有累计 或 正在流式即显示:首轮回合进行中也可见(TPS/累计待 usage 帧落地) -->
    <template v-if="hasTotals || streaming">
      <span class="sb-item">
        <span class="ms sb-icon">description</span>
        <span class="sb-label">输入</span>
        <span class="sb-value">{{ fmtK(usage.promptTotal) }}</span>
      </span>
      <span class="sb-item">
        <span class="ms sb-icon">hub</span>
        <span class="sb-label">缓存命中</span>
        <span class="sb-value">{{ fmtK(usage.cachedTotal) }}<em v-if="hitRate" class="hit"> ({{ hitRate }})</em></span>
      </span>
      <span class="sb-item">
        <span class="ms sb-icon">send</span>
        <span class="sb-label">输出</span>
        <span class="sb-value">{{ fmtK(usage.completionTotal) }}</span>
      </span>
      <span class="sb-item">
        <span class="ms sb-icon">graphic_eq</span>
        <span class="sb-label">TPS</span>
        <span class="sb-value accent">{{ tps }}</span>
      </span>
      <span v-if="usage.cost > 0" class="sb-item">
        <span class="ms sb-icon">paid</span>
        <span class="sb-label">费用</span>
        <span class="sb-value cost">¥{{ fmtCost(usage.cost) }}</span>
      </span>
    </template>
    <span v-for="sp in pluginStatuses" :key="sp.name" class="sb-item sb-plugin">
      <span class="ms sb-icon green">computer</span>
      <span class="sb-value">{{ sp.status }}</span>
    </span>
  </div>
</template>

<style scoped>
/* 与 WPF 标题栏同色(BgPanel #222227);28px 高,格间 1px 竖线分隔 */
.statusbar {
  display: flex;
  align-items: stretch;
  height: 28px;
  background: var(--bg-panel);
  border-top: 1px solid var(--border);
  font-size: 11.5px;
  user-select: none;
}
.sb-item {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 0 11px;
  border-right: 1px solid var(--border);
  min-width: 0;
  white-space: nowrap;
}
.sb-item:first-child {
  border-left: none;
}
.sb-icon {
  font-size: 14px;
  width: 14px;
  height: 14px;
  color: var(--fg-secondary);
  opacity: 0.85;
}
.sb-icon.accent {
  color: var(--fg-accent);
}
.sb-icon.green {
  color: var(--success);
}
/* 模型名:最重一档 */
.sb-name {
  font-weight: 600;
  color: var(--fg-primary);
  letter-spacing: 0.2px;
}
/* 中文标签:次级色常规字重,与数值拉开层级 */
.sb-label {
  color: var(--fg-secondary);
  font-weight: 400;
  font-size: 11px;
}
/* 数值:主色半粗 + 等宽数字,变化时不抖动 */
.sb-value {
  color: var(--fg-primary);
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
.sb-value.accent {
  color: var(--fg-accent);
}
/* 命中率:括号内绿色(命中是好事) */
.sb-value .hit {
  font-style: normal;
  font-weight: 500;
  color: var(--success);
}
/* GPU 等插件状态:字重略降,信息从属于 LLM 统计 */
.sb-plugin .sb-value {
  font-weight: 500;
}

.sb-value.cost {
  color: #e5c07b;
  font-weight: 500;
}
</style>
