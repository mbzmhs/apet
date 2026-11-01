<script setup lang="ts">
// 侧边栏:任务清单(todo_* 插件)+ 后台任务(bg_* 工具)+ 智能体子代理(sub_* 工具,按会话过滤)+ 直播事件(仅绑定会话)。
// 数据轻量,经共享轮询器(sidebarPoll,与状态栏同一节拍)每 3 秒取一帧。
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import {
  deleteBgTask,
  deleteSubAgentRun,
  getSubAgentRuns,
  getTaskOutput,
  killBgTask,
  type SidebarInfo,
  type SubAgentRun,
} from '../api/client'
import { refreshSidebarNow, setSidebarTarget, subscribeSidebar } from '../store/sidebarPoll'
import PluginUiFrame from './PluginUiFrame.vue'

const props = defineProps<{ agentId: string; sessionId: string }>()
const emit = defineEmits<{ (e: 'open-run', runId: number): void }>()

const tab = ref<string>('todo')
const info = ref<SidebarInfo | null>(null)
const subRuns = ref<SubAgentRun[]>([])
const openTask = ref<number | null>(null)
const taskOutput = ref('')
const error = ref('')
let unsub: (() => void) | null = null
let outputEl: HTMLElement | null = null // 展开的输出面板(至多一个):模板 :ref 回调用,不参与响应式
function setOutputEl(el: unknown) {
  if (el instanceof HTMLElement) outputEl = el
}

/** 输出面板是否停在最底部(距底 40px 内视为贴底):贴底才跟随新输出,用户上翻查看历史时不拽回 */
function outputAtBottom(): boolean {
  if (!outputEl) return true
  return outputEl.scrollTop + outputEl.clientHeight >= outputEl.scrollHeight - 40
}

/** 轮询帧落地:更新任务清单并跟随展开中任务的实时输出 */
async function applyFrame(frame: SidebarInfo) {
  info.value = frame
  const open = frame.tasks.find((t) => t.id === openTask.value)
  if (!open) {
    openTask.value = null // 任务被淘汰/清除:收起输出面板
  } else {
    // 展开中的输出跟随轮询实时刷新(任务边跑边出);贴底才滚动跟随
    const follow = outputAtBottom()
    taskOutput.value = await getTaskOutput(props.agentId, props.sessionId, open.id)
    if (follow) {
      await nextTick()
      if (outputEl) outputEl.scrollTop = outputEl.scrollHeight
    }
  }
  error.value = '' // 成功即清错:轮询恢复后错误条消失,不永久驻留(同 App.vue 的成功清错做法)
}

/** 立即刷新(操作后调用):拉一拍侧边栏 + 子代理运行清单 */
function refresh() {
  refreshSidebarNow()
  // 子代理运行与 session 绑定:只列本会话发起的运行(与 bg_run 任务一致);失败不影响其余页签
  getSubAgentRuns(props.agentId, props.sessionId)
    .then((r) => (subRuns.value = r))
    .catch(() => {})
}

/** 后台任务状态归一:后端为英文(running/completed/failed (exit N)/killed),界面统一显示中文 */
const TASK_STATUS: Record<string, string> = {
  running: '运行中',
  completed: '已完成',
  killed: '已终止',
}

function taskStatus(t: { status: string }): string {
  if (t.status.startsWith('failed')) return '失败'
  return TASK_STATUS[t.status] ?? t.status
}

const SUB_STATUS: Record<string, string> = {
  running: '运行中',
  completed: '已完成',
  failed: '失败',
  cancelled: '已取消',
}

function subStatusText(r: SubAgentRun) {
  return SUB_STATUS[r.status] ?? r.status
}

function subTime(r: SubAgentRun) {
  const t = (r.endedAt ?? r.startedAt).slice(11, 19)
  return t || ''
}

/** 插件数据面:任意 JSON 形状按两空格缩进展示,序列化失败时原样兜底 */
function prettyJson(d: unknown): string {
  try {
    return JSON.stringify(d, null, 2)
  } catch {
    return String(d)
  }
}

// ───────── BiliLive 连接状态(圆点+状态行;3s 轮询帧驱动) ─────────

const bili = computed(() => info.value?.bili ?? null)

/** 连接引擎非 idle 时,"直播"页签在未绑定会话的侧边栏也显示(看连接健康不依赖绑定) */
const biliActive = computed(() => !!bili.value && bili.value.state !== 'idle')

/** 圆点配色:绿=已连接,黄=连接中/认证中/等待开播,红=失败等待重连,灰=未连接 */
const biliCls = computed(() => {
  const s = bili.value?.state
  if (s === 'connected') return 'ok'
  if (s === 'retrying') return 'err'
  if (!s || s === 'idle') return 'idle'
  return 'run'
})

const biliText = computed(() => {
  const b = bili.value
  if (!b) return '未连接'
  switch (b.state) {
    case 'resolving': return '连接中 · 获取线路'
    case 'connecting': return `连接中 ${b.hostIndex}/${b.hostCount}`
    case 'authenticating': return '认证中'
    case 'connected': return '已连接'
    case 'waiting_live': return '未开播 · 等待'
    case 'retrying': return '重连等待'
    default: return '未连接'
  }
})

/** 直播事件类型 → 中文标签 / 配色类(SC 走金色突出卡片) */
function kindLabel(k: string): string {
  return k === 'Danmaku' ? '弹幕' : k === 'Gift' ? '礼物' : k === 'Sc' ? 'SC' : k === 'Interact' ? '互动' : k
}
function kindClass(k: string): string {
  return k === 'Gift' ? 'k-gift' : k === 'Sc' ? 'k-sc' : k === 'Interact' ? 'k-interact' : 'k-danmaku'
}

/** 状态详情行(按状态给出"到底在干什么/卡在哪一步") */
const biliLines = computed(() => {
  const b = bili.value
  if (!b) return []
  const room = b.roomCode ? `房间 ${b.roomCode}` : b.internalRoomId ? `房间 ${b.internalRoomId}` : ''
  const liveTag = b.roomLive ? '直播中' : '未开播'
  const retryIn = b.nextRetryAt
    ? Math.max(0, Math.round((new Date(b.nextRetryAt).getTime() - Date.now()) / 1000))
    : null
  switch (b.state) {
    case 'connected':
      return [`${room} · ${liveTag}`, `线路 ${b.host} · 已处理 ${b.received} 条事件`]
    case 'authenticating':
      return [`${room} · ${liveTag}`, `WS 已建立(${b.host}),等待认证应答`]
    case 'connecting':
      return [`${room} · ${liveTag}`, `握手中 ${b.host}(${b.hostIndex}/${b.hostCount})`]
    case 'resolving':
      return [`${room} · 解析房间 / 获取弹幕线路`]
    case 'waiting_live':
      return [`${room} · 未开播,60s 后复查`]
    case 'retrying': {
      const head = b.hostCount ? `全部 ${b.hostCount} 条线路失败` : '连接失败'
      const tail = [b.error]
      if (retryIn !== null) tail.push(`${retryIn}s 后重试`)
      if (b.round > 0) tail.push(`第 ${b.round} 轮`)
      return [`${room} · ${head}`, tail.join(' · ')]
    }
    default:
      return ['未连接(未配置房间号或插件未启用)']
  }
})

async function toggleTask(id: number) {
  if (openTask.value === id) {
    openTask.value = null
    return
  }
  openTask.value = id
  taskOutput.value = '加载中…'
  try {
    taskOutput.value = await getTaskOutput(props.agentId, props.sessionId, id)
    await nextTick()
    if (outputEl) outputEl.scrollTop = outputEl.scrollHeight // 展开即看最新输出
  } catch (e) {
    taskOutput.value = String(e)
  }
}

/** 侧边栏停止运行中的后台任务(杀整棵进程树;成功后立即刷新) */
async function stopTask(id: number) {
  try {
    await killBgTask(props.agentId, props.sessionId, id)
    refresh()
  } catch (e) {
    error.value = String(e)
  }
}

/** 侧边栏清除已结束后台任务(运行中后端拒绝;成功后立即刷新) */
async function removeTask(id: number) {
  try {
    await deleteBgTask(props.agentId, props.sessionId, id)
    if (openTask.value === id) openTask.value = null
    refresh()
  } catch (e) {
    error.value = String(e)
  }
}

/** 侧边栏清除已结束子代理运行(运行中后端拒绝;成功后立即刷新) */
async function removeSub(r: SubAgentRun) {
  try {
    await deleteSubAgentRun(props.agentId, r.id)
    refresh()
  } catch (e) {
    error.value = String(e)
  }
}

watch(
  [() => props.sessionId, () => props.agentId],
  () => {
    openTask.value = null
    setSidebarTarget(props.agentId, props.sessionId)
    refresh()
  },
)

onMounted(() => {
  setSidebarTarget(props.agentId, props.sessionId)
  unsub = subscribeSidebar(applyFrame, (e) => (error.value = String(e)))
  refresh()
})

onUnmounted(() => {
  unsub?.()
})
</script>

<template>
  <div class="panel" data-APet-anchor="sidebar">
    <div class="tabs">
      <button :class="{ sel: tab === 'todo' }" @click="tab = 'todo'">
        任务清单<span v-if="info?.todos.length" class="badge">{{ info.todos.length }}</span>
      </button>
      <button :class="{ sel: tab === 'tasks' }" @click="tab = 'tasks'">
        后台任务<span v-if="info?.tasks.filter((t) => taskStatus(t) === '运行中').length" class="badge run"><span class="ms fill dot-run">circle</span></span>
      </button>
      <button :class="{ sel: tab === 'subagents' }" @click="tab = 'subagents'">
        智能体<span v-if="subRuns.some((r) => r.status === 'running')" class="badge run"><span class="ms fill dot-run">circle</span></span><span v-else-if="subRuns.length" class="badge">{{ subRuns.length }}</span>
      </button>
      <button v-if="info?.liveBound || biliActive" :class="{ sel: tab === 'live' }" @click="tab = 'live'">直播<span class="bili-dot tab-dot" :class="biliCls"></span></button>
      <button v-if="info?.pluginData?.length" :class="{ sel: tab === 'plugin' }" @click="tab = 'plugin'">
        插件<span class="badge">{{ info.pluginData.length }}</span>
      </button>
      <button
        v-for="p in info?.uiPlugins ?? []"
        :key="p.name"
        :class="{ sel: tab === 'ui:' + p.name }"
        @click="tab = 'ui:' + p.name"
      >{{ p.name }}</button>
    </div>

    <div v-if="error" class="error">{{ error }}</div>

    <div v-if="tab === 'todo'" class="body">
      <div v-for="t in info?.todos ?? []" :key="t.id" class="todo" :class="t.status">
        <span class="glyph ms">{{ t.status === 'in_progress' ? 'pending' : t.status === 'completed' ? 'check_circle' : 'radio_button_unchecked' }}</span>
        <span class="content">#{{ t.id }} {{ t.content }}</span>
      </div>
      <div v-if="!info?.todos.length" class="empty">本会话还没有任务清单(todo 工具创建)</div>
    </div>

    <div v-else-if="tab === 'tasks'" class="body">
      <div v-for="t in info?.tasks ?? []" :key="t.id" class="task">
        <div class="row">
          <span class="glyph ms" :class="{ ok: !t.isError && taskStatus(t) === '已完成', err: t.isError, warn: taskStatus(t) === '运行中' }">
            {{ taskStatus(t) === '运行中' ? 'pending' : t.isError ? 'cancel' : 'check_circle' }}
          </span>
          <button class="task-head" @click="toggleTask(t.id)">
            <span class="title" :title="t.command">#{{ t.id }} {{ t.title || t.command }}</span>
          </button>
          <button v-if="taskStatus(t) === '运行中'" class="row-del" title="停止任务" @click="stopTask(t.id)">
            <span class="ms">stop</span>
          </button>
          <button v-else class="row-del" title="从列表清除" @click="removeTask(t.id)">
            <span class="ms">delete</span>
          </button>
        </div>
        <pre v-if="openTask === t.id" :ref="setOutputEl" class="task-output">{{ taskOutput }}</pre>
      </div>
      <div v-if="!info?.tasks.length" class="empty">本会话没有后台任务(bg_run 工具创建)</div>
    </div>

    <div v-else-if="tab === 'subagents'" class="body">
      <div v-for="r in subRuns" :key="r.id" class="task sub-row" @click="emit('open-run', r.id)">
        <div class="row">
          <span class="glyph ms sub-glyph" :class="{ ok: r.status === 'completed', err: r.status === 'failed' || r.status === 'cancelled', run: r.status === 'running' }">
            {{ r.status === 'running' ? 'pending' : r.status === 'completed' ? 'check_circle' : 'cancel' }}
          </span>
          <div class="sub-meta">
            <div class="sub-title-line">#{{ r.id }} {{ r.title || r.name }}<span class="sub-tag">{{ subStatusText(r) }}</span></div>
            <div class="sub-sub">
              <span class="sub-who" :title="r.title ? r.name : r.prompt">{{ r.title ? r.name : r.prompt }}</span>
              <span v-if="subTime(r)" class="sub-time">{{ subTime(r) }}</span>
            </div>
          </div>
          <button v-if="r.status !== 'running'" class="row-del" title="从列表清除" @click.stop="removeSub(r)">
            <span class="ms">delete</span>
          </button>
        </div>
      </div>
      <div v-if="!subRuns.length" class="empty">本智能体还没有子代理运行(主 Agent 经 sub_run 创建)</div>
    </div>

    <div v-else-if="tab === 'live'" class="body">
      <div class="bili-status">
        <span class="bili-dot" :class="biliCls"></span>
        <span class="bili-state">{{ biliText }}</span>
      </div>
      <div v-for="(l, i) in biliLines" :key="'l' + i" class="bili-line">{{ l }}</div>
      <div v-if="!info?.liveBound" class="empty">当前会话未绑定直播间消息($blive 绑定)</div>
      <div v-for="(e, i) in info?.live ?? []" :key="'e' + i" class="live" :class="[kindClass(e.kind), i === 0 ? 'first' : '']">
        <div class="live-head">
          <span class="live-kind" :class="kindClass(e.kind)">{{ kindLabel(e.kind) }}</span>
          <span class="live-user">{{ e.user }}</span>
          <span v-if="e.priceYuan > 0" class="live-price" :class="{ hot: e.kind === 'Sc' }">¥{{ e.priceYuan }}</span>
          <span class="live-time">{{ e.time }}</span>
        </div>
        <div class="live-text">{{ e.text }}</div>
      </div>
      <div v-if="info?.liveBound && !info?.live.length" class="empty">暂无直播事件</div>
    </div>

    <div v-else-if="tab === 'plugin'" class="body">
      <div v-for="p in info?.pluginData ?? []" :key="p.name" class="plugin-data">
        <div class="plugin-data-head">{{ p.name }}</div>
        <pre class="plugin-data-json">{{ prettyJson(p.data) }}</pre>
      </div>
      <div v-if="!info?.pluginData.length" class="empty">没有插件提供面板数据</div>
    </div>

    <!-- 声明 sidebar UI 插槽的插件:自定义页签,内容 = DLL 内嵌 ui.* 页面(v-show 保活,插件内部状态跨页签保留)
         agentId 未就绪时不渲染——否则首帧拼出空 agentId 的坏 iframe URL -->
    <div
      v-for="p in props.agentId ? info?.uiPlugins ?? [] : []"
      v-show="tab === 'ui:' + p.name"
      :key="p.name"
      class="body ui-body"
    >
      <PluginUiFrame :agent-id="agentId" :plugin-name="p.name" slot="sidebar" :session-id="sessionId" />
    </div>
  </div>
</template>

<style scoped>
.panel {
  height: 100%;
  display: flex;
  flex-direction: column;
  background: var(--bg-panel);
  border-left: 1px solid var(--border);
}

.tabs {
  display: flex;
  border-bottom: 1px solid var(--border);
}

.tabs button {
  flex: 1;
  background: transparent;
  border: none;
  color: var(--fg-secondary);
  padding: 10px 6px;
  font-size: 13.2px;
  cursor: pointer;
}

.tabs button.sel {
  color: var(--fg-primary);
  border-bottom: 2px solid var(--accent);
}

.badge {
  margin-left: 4px;
  color: var(--fg-muted);
}

.badge.run {
  color: var(--success);
}

.dot-run {
  font-size: 7.7px;
  vertical-align: 2px;
}

.body {
  flex: 1;
  overflow-y: auto;
  padding: 10px;
}

.todo,
.task {
  padding: 6px 8px;
  border-radius: 6px;
  font-size: 13.2px;
  margin-bottom: 4px;
}

.todo:hover,
.task:hover {
  background: var(--bg-panel-hover);
}

.glyph {
  margin-right: 6px;
  color: var(--fg-muted);
  font-size: 14.3px;
  flex: none;
  display: inline-flex;
}

.todo.in_progress .glyph {
  color: var(--fg-accent);
}

.todo.completed .glyph {
  color: var(--success);
}

.todo.completed .content {
  color: var(--fg-muted);
  text-decoration: line-through;
}

.content {
  color: var(--fg-primary);
  word-break: break-all;
}

/* 行:图标顶对齐 + 内容伸展 + 清除按钮右贴边 */
.row {
  display: flex;
  align-items: flex-start;
  gap: 2px;
}

.task-head {
  flex: 1;
  min-width: 0;
  background: transparent;
  border: none;
  color: var(--fg-primary);
  text-align: left;
  cursor: pointer;
  font-size: 13.2px;
  padding: 0;
}

/* 清除按钮:仅已结束的行显示;常态淡显,悬停转红 */
.row-del {
  flex: none;
  margin-left: auto;
  margin-top: -2px;
  background: transparent;
  border: none;
  color: var(--fg-muted);
  opacity: 0.55;
  cursor: pointer;
  padding: 1px 5px;
  border-radius: 4px;
  font-size: 14.3px;
  line-height: 1;
}

.row-del:hover {
  opacity: 1;
  color: var(--danger);
  background: var(--bg-panel-hover);
}

.glyph.ok {
  color: var(--fg-secondary);
}

.glyph.err {
  color: var(--danger);
}

.glyph.warn {
  color: var(--warning);
}

.title {
  word-break: break-all;
}

.task-output {
  margin: 6px 0 2px;
  padding: 8px;
  background: var(--bg-base);
  border: 1px solid var(--border);
  border-radius: 6px;
  font-size: 12.1px;
  color: var(--fg-secondary);
  /* 空输出时保持一行的样子,不再是贴着内边距的扁条 */
  min-height: 1.55em;
  max-height: 280px;
  overflow-y: auto;
  white-space: pre-wrap;
  word-break: break-all;
}

/* 直播事件:类型标签配色 + 时间固定贴右;SC 走金色卡片突出 */
.live {
  padding: 6px 8px;
  border-bottom: 1px solid var(--border);
  font-size: 13.2px;
}

/* 列表首行补顶部分割线(与状态头之间留 6px 呼吸) */
.live.first {
  margin-top: 6px;
  border-top: 1px solid var(--border);
}

.live-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12.1px;
}

.live-kind {
  flex: none;
  font-size: 10.5px;
  line-height: 16px;
  padding: 0 6px;
  border-radius: 8px;
  color: var(--fg-secondary);
  background: var(--bg-panel-hover);
}

.live-kind.k-gift {
  color: #fff;
  background: rgba(236, 72, 153, 0.75);
}

.live-kind.k-sc {
  color: #241500;
  font-weight: 600;
  background: linear-gradient(90deg, #fbbf24, #f97316);
}

.live-kind.k-interact {
  color: #06230e;
  background: rgba(74, 222, 128, 0.75);
}

.live-user {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--fg-secondary);
}

.live-price {
  flex: none;
  color: var(--warning);
  font-weight: 600;
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
}

.live-price.hot {
  color: #241500;
  font-weight: 700;
  padding: 0 7px;
  line-height: 17px;
  border-radius: 8px;
  background: linear-gradient(90deg, #fbbf24, #f97316);
}

.live-time {
  flex: none;
  margin-left: auto; /* 时间永远贴右;价格(若有)紧挨其左 */
  color: var(--fg-muted);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.live-text {
  margin-top: 2px;
  color: var(--fg-primary);
  word-break: break-all;
}

/* SC 卡片:暖金渐变底 + 金色描边,正文略大更醒目 */
.live.k-sc {
  background: linear-gradient(135deg, rgba(251, 191, 36, 0.16), rgba(249, 115, 22, 0.05));
  border-bottom: 1px solid rgba(251, 191, 36, 0.35);
}

.live.k-sc .live-user {
  color: var(--fg-primary);
  font-weight: 600;
}

.live.k-sc .live-text {
  font-size: 13.6px;
}

/* 礼物行:礼物名(正文)加重;弹幕行正文降灰,把视觉层级让给 SC/礼物 */
.live.k-gift .live-text {
  font-weight: 600;
}

.live.k-danmaku .live-text {
  color: var(--fg-secondary);
}

/* BiliLive 连接状态:圆点(绿=已连接 黄=连接/认证/等待开播 红=失败重连中 灰=未连接)+ 状态行 */
.bili-status {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 2px 8px 4px; /* 分隔交给列表首行的顶线,这里不再画线避免双线 */
}

.bili-state {
  font-size: 12.8px;
  color: var(--fg-primary);
}

.bili-line {
  padding: 1px 8px;
  font-size: 12.1px;
  color: var(--fg-secondary);
  word-break: break-all;
}

.bili-dot {
  flex: none;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  display: inline-block;
}

.bili-dot.ok { background: var(--success); }
.bili-dot.run { background: var(--warning); }
.bili-dot.err { background: var(--danger); }
.bili-dot.idle { background: var(--fg-muted); }

/* 页签按钮上的小号圆点 */
.tab-dot {
  margin-left: 5px;
  width: 7px;
  height: 7px;
  vertical-align: 1px;
}

/* 子代理:标题/图标青色,运行中呼吸点同后台任务(样式本地定义,不依赖其他组件的 scoped 类) */
.sub-row {
  cursor: pointer;
}

.sub-glyph {
  color: var(--accent-sub);
}

.sub-glyph.run {
  color: var(--accent-sub);
}

.sub-meta {
  flex: 1;
  min-width: 0;
}

.sub-title-line {
  color: var(--accent-sub);
  word-break: break-all;
}

.sub-tag {
  margin-left: 8px;
  font-size: 12.1px;
  font-weight: 400;
  color: var(--accent-sub);
  background: var(--accent-sub-bg);
  border-radius: 6px;
  padding: 1px 6px;
  vertical-align: 2px;
  white-space: nowrap;
}

.sub-sub {
  display: flex;
  align-items: baseline;
  gap: 6px;
  margin-top: 3px;
  font-size: 12.1px;
  color: var(--fg-muted);
}

.sub-who {
  flex: 0 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.sub-time {
  flex: none;
  margin-left: auto;
  color: var(--fg-muted);
}

.empty {
  color: var(--fg-muted);
  text-align: center;
  padding: 24px 8px;
  font-size: 13.2px;
}

.error {
  color: var(--danger);
  padding: 6px 10px;
  font-size: 12.1px;
}

/* 插件数据面:卡片头(插件名)+ JSON 正文 */
.plugin-data {
  padding: 6px 8px;
  margin-bottom: 8px;
  border-bottom: 1px solid var(--border);
}

.plugin-data-head {
  color: var(--accent-sub);
  font-size: 12.1px;
  margin-bottom: 4px;
}

.plugin-data-json {
  margin: 0;
  padding: 8px;
  background: var(--bg-base);
  border: 1px solid var(--border);
  border-radius: 6px;
  font-size: 12.1px;
  color: var(--fg-secondary);
  max-height: 280px;
  overflow-y: auto;
  white-space: pre-wrap;
  word-break: break-all;
}

/* 插件自定义页签:iframe 撑满内容区,去掉默认内边距 */
.ui-body {
  padding: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
</style>
