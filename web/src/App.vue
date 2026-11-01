<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { avatarUrl, createAgent, createSession, deleteAgent, getAgents, getHealth, getLastSession, getSessions, openAgentSettings, openAgentWindow, renameAgent, setAgentEnabled } from './api/client'
import { currentTheme, toggleTheme, type ThemeMode } from './theme/theme'
import { applyAgentUiScale, applyUiScale } from './lib/uiScale'
import { useActionDialog } from './lib/actionDialog'
import { cachedLastChat, storeAgentList } from './store/sessionCache'
import ActionDialog from './components/ActionDialog.vue'
import ContextMenu from './components/ContextMenu.vue'
import ChatView from './views/ChatView.vue'
import Settings from './views/Settings.vue'

// 极简 hash 路由:#/ → Agent 列表(管理首页);#/a/{agentId} → 该 Agent 页(过渡态:
// 解析到其最新会话的聊天页,无会话则新建);#/a/{agentId}/settings → 设置;#/s/{sessionId} → 聊天
// (session 深链的归属 Agent 由后端 /api/session-owner 解析)
type Route =
  | { name: 'home' }
  | { name: 'agent'; agentId: string }
  | { name: 'settings'; agentId: string; tab?: string }
  | { name: 'chat'; sessionId: string }

type AgentInfo = Awaited<ReturnType<typeof getAgents>>[number]

const agents = ref<AgentInfo[]>([])
const health = ref<{ ok: boolean; port: number } | null>(null)
const error = ref('')
const route = ref<Route>(parseHash())
const menuFor = ref<string | null>(null)
const theme = ref<ThemeMode>(currentTheme())
// 确认/输入模态框(替代浏览器原生 confirm/prompt)
const { dlg, ask, settle } = useActionDialog()

function parseHash(): Route {
  const h = location.hash
  // 可选 ?tab= 直达指定设置分区(如 ?tab=llm 打开"模型服务")
  let m = h.match(/^#\/a\/([\w-]+)\/settings(?:\?(.*))?$/)
  if (m) {
    const tab = m[2]?.match(/(?:^|&)tab=([\w-]+)/)?.[1]
    return { name: 'settings', agentId: m[1], tab: tab || undefined }
  }
  m = h.match(/^#\/a\/([\w-]+)$/)
  if (m) return { name: 'agent', agentId: m[1] }
  m = h.match(/^#\/s\/([\w-]+)$/)
  if (m) return { name: 'chat', sessionId: m[1] }
  return { name: 'home' }
}

window.addEventListener('hashchange', () => (route.value = parseHash()))

// 该 Agent 页是过渡路由:落到其最新会话的聊天页,无会话则新建(与点击 Agent 卡片同规则)
let resolvingAgent = false
async function resolveAgent(agentId: string) {
  if (resolvingAgent) return
  resolvingAgent = true
  try {
    // 优先回到上次打开的会话(后端记忆跨端通用,localStorage 兜底),无记忆/会话已删才落最近活跃
    const [list, remembered] = await Promise.all([getSessions(agentId), getLastSession(agentId).catch(() => null)])
    if (list.length) {
      const last = remembered || cachedLastChat(agentId)
      const target = (last && list.find((s) => s.sessionId === last)) || list[list.length - 1]
      location.hash = `#/s/${target.sessionId}`
      return
    }
    const s = await createSession(agentId)
    location.hash = `#/s/${s.sessionId}`
  } catch (e) {
    error.value = String((e as Error).message)
    location.hash = '#/'
  } finally {
    resolvingAgent = false
  }
}

// 聊天页双槽位"后台预备、就绪上屏、淡出卸载":
// prep 不可见挂载 → live 覆盖在上层淡入(旧视图留在底下继续显示,不露黑底)→ dying 过渡结束后卸载
type ChatSlotPhase = 'prep' | 'live' | 'dying'
type ChatSlot = { id: string; phase: ChatSlotPhase } | null
const slotA = ref<ChatSlot>(null)
const slotB = ref<ChatSlot>(null)

function chatClass(s: ChatSlot): string {
  return s.phase === 'prep' ? 'chat-prep' : s.phase === 'live' ? 'chat-live' : 'chat-dying'
}

function promote(slot: 'A' | 'B', id: string) {
  const self = slot === 'A' ? slotA : slotB
  const other = slot === 'A' ? slotB : slotA
  if (!self.value || self.value.phase !== 'prep' || self.value.id !== id) return
  if (other.value?.phase === 'live') other.value = { id: other.value.id, phase: 'dying' } // 旧视图退到底层
  self.value = { id, phase: 'live' } // 新视图同实例原地上屏:覆盖淡入,无空窗无黑底
  window.setTimeout(() => {
    if (other.value?.phase === 'dying') other.value = null // 过渡结束后卸旧实例
  }, 240)
}

watch(
  route,
  (r) => {
    if (r.name === 'chat') {
      const curRef = slotA.value?.phase === 'live' ? slotA : slotB.value?.phase === 'live' ? slotB : null
      if (curRef && curRef.value!.id === r.sessionId) {
        // 目标即当前可见会话:废弃后台预备(若连切时切了回去)
        if (slotA.value?.phase === 'prep') slotA.value = null
        if (slotB.value?.phase === 'prep') slotB.value = null
      } else if (!curRef) {
        slotA.value = { id: r.sessionId, phase: 'live' } // 首次进入:直接上屏
      } else {
        const otherRef = curRef === slotA ? slotB : slotA
        if (otherRef.value?.phase === 'dying' && otherRef.value.id === r.sessionId) {
          // 过渡期内切回即将卸载的旧会话:取消卸载、直接复用它
          curRef.value = null
          otherRef.value = { id: r.sessionId, phase: 'live' }
        } else if (otherRef.value?.id !== r.sessionId) {
          // 换会话:在另一槽位后台预备(同槽位换会话靠 key 强制重挂载)
          otherRef.value = { id: r.sessionId, phase: 'prep' }
        }
      }
    } else {
      slotA.value = null
      slotB.value = null
    }
    if (r.name === 'agent') resolveAgent(r.agentId)
    // 界面缩放随路由应用:设置/Agent 过渡页按该 Agent 设置;首页(无归属 Agent)复位 100%;
    // 聊天页由 ChatView 解析出归属 Agent 后自行应用
    if (r.name === 'settings' || r.name === 'agent') applyAgentUiScale(r.agentId)
    else if (r.name === 'home') applyUiScale(1)
  },
  { immediate: true },
)

async function refreshAgents() {
  try {
    agents.value = await getAgents()
    storeAgentList(agents.value) // 缓存 Agent 清单:聊天页首帧直接取标题/头像,免 getAgents 往返
    error.value = '' // 成功即清错:错误条不再永久锁死界面(见模板注释)
  } catch (e) {
    error.value = String(e)
  }
}

async function act(fn: () => Promise<void>) {
  menuFor.value = null
  try {
    await fn()
    await refreshAgents()
  } catch (e) {
    error.value = String((e as Error).message)
  }
}

// 错误条点击:清错并重试刷新(单次瞬时失败不该要用户刷新整页)
const retryError = () => {
  error.value = ''
  refreshAgents()
}

const doAddAgent = () => act(async () => {
  const name = await ask({ title: '新建 Agent', message: '输入新 Agent 的名称:', input: true })
  if (typeof name === 'string' && name.trim()) await createAgent(name.trim())
})

// 是否运行在桌面客户端(WebView2 壳)内:存在 chrome.webview 桥
const inDesktopShell = !!(window as any).chrome?.webview

// 点卡片:桌面客户端 → 唤起该 Agent 的原生窗体(每 Agent 至多一个,直达最新聊天);
// 网页 → 本页路由到最近会话(没有则新建)。停用的 Agent 不进入,由菜单启用。
async function openAgent(a: AgentInfo) {
  if (!a.enabled) return
  if (inDesktopShell) {
    try {
      await openAgentWindow(a.id)
    } catch (e) {
      error.value = String((e as Error).message)
    }
    return
  }
  try {
    // 与 resolveAgent 同规则:回到上次打开的会话,而非按消息时间猜的最新
    const [list, remembered] = await Promise.all([getSessions(a.id), getLastSession(a.id).catch(() => null)])
    if (list.length) {
      const last = remembered || cachedLastChat(a.id)
      const target = (last && list.find((s) => s.sessionId === last)) || list[list.length - 1]
      location.hash = `#/s/${target.sessionId}`
      return
    }
    const s = await createSession(a.id)
    location.hash = `#/s/${s.sessionId}`
  } catch (e) {
    error.value = String((e as Error).message)
  }
}

// 设置入口:停用的 Agent 不可进设置;桌面客户端在其独立原生窗体里打开(管理窗停留首页,
// 避免本窗切走后回不到首页);纯网页则本页路由跳转(浏览器可后退)
const goAgentSettings = (a: AgentInfo) => {
  menuFor.value = null
  if (!a.enabled) return
  if (inDesktopShell) {
    openAgentSettings(a.id).catch((e) => { error.value = String((e as Error).message) })
    return
  }
  location.hash = `#/a/${a.id}/settings`
}

const doRenameAgent = (a: AgentInfo) => act(async () => {
  const name = await ask({ title: '重命名 Agent', input: true, initialValue: a.name })
  if (typeof name === 'string' && name.trim()) await renameAgent(a.id, name.trim())
})

const doToggleAgent = (a: AgentInfo) => act(() => setAgentEnabled(a.id, !a.enabled))

const doDeleteAgent = (a: AgentInfo) => act(async () => {
  if ((await ask({ title: '删除 Agent', message: `确定要删除 Agent「${a.title}」吗?其全部会话与设定将一并清除,不可恢复。`, confirmLabel: '删除', danger: true })) !== true) return
  await deleteAgent(a.id)
})

const doToggleTheme = () => {
  theme.value = toggleTheme()
}

onMounted(async () => {
  try {
    health.value = await getHealth()
  } catch (e) {
    error.value = String(e)
  }
  await refreshAgents()
})
</script>

<template>
  <div class="shell" data-APet-anchor="app-root">
    <!-- 错误条与主内容并存(曾经 v-if/v-else 互斥:任何一次请求失败都把整个 UI 换成横幅,
         表现为"所有 agent 都无法操作、设置页空白"且永不恢复);点击横幅重试并清除 -->
    <div v-if="error" class="error" title="点击重试" @click="retryError">请求失败:{{ error }}(点击重试)</div>
    <template v-if="route.name === 'home'">
      <div class="home">
        <div class="brand">
          <h1>APet</h1>
          <button class="ghost theme-btn" :title="theme === 'dark' ? '切换到亮色主题' : '切换到暗色主题'" @click="doToggleTheme">
            <span class="ms">{{ theme === 'dark' ? 'dark_mode' : 'light_mode' }}</span>{{ theme === 'dark' ? '暗色' : '亮色' }}
          </button>
        </div>
        <p class="sub">本地服务 http://127.0.0.1:{{ health?.port ?? '…' }} · 浏览器与客户端访问同一服务</p>
        <div class="agents">
          <a v-for="a in agents" :key="a.id" class="agent-card" :class="{ off: !a.enabled }" :href="`#/a/${a.id}`" @click.prevent="openAgent(a)">
            <img v-if="a.avatarRev" class="avatar" :src="avatarUrl(a.id, a.avatarRev)" alt="" />
            <div v-else class="avatar">{{ a.title.slice(0, 1) }}</div>
            <div class="meta">
              <div class="name">{{ a.title }}<span v-if="!a.enabled" class="off-tag">已停用</span></div>
              <div class="hint">{{ a.enabled ? '进入聊天' : '已停用 · 通过右侧菜单启用' }}</div>
            </div>
            <button class="ghost menu-btn" title="管理" @click.stop.prevent="menuFor = menuFor === a.id ? null : a.id"><span class="ms">more_vert</span></button>
            <div v-if="menuFor === a.id" class="menu" @click.stop.prevent>
              <button :disabled="!a.enabled" title="打开该 Agent 的设置页(已停用时不可用)" @click="goAgentSettings(a)"><span class="ms">settings</span>设置</button>
              <button @click="doRenameAgent(a)"><span class="ms">edit</span>重命名</button>
              <button @click="doToggleAgent(a)"><span class="ms">{{ a.enabled ? 'pause' : 'play_arrow' }}</span>{{ a.enabled ? '停用' : '启用' }}</button>
              <button class="danger" @click="doDeleteAgent(a)"><span class="ms">delete</span>删除</button>
            </div>
          </a>
          <button class="agent-card add" @click="doAddAgent"><span class="ms">add</span> 新增 Agent</button>
        </div>
      </div>
    </template>
    <p v-else-if="route.name === 'agent'" class="empty">正在打开会话…</p>
      <Settings v-else-if="route.name === 'settings'" :agent-id="route.agentId" :initial-tab="route.tab" :key="route.agentId + ':' + (route.tab ?? '')" />
      <template v-else-if="route.name === 'chat'">
        <!-- 双槽位:prep 不可见挂载;live 覆盖上层淡入;dying 留在底下,过渡结束才卸载(交叉过渡不露黑底) -->
        <ChatView v-if="slotA" :key="slotA.id" :session-id="slotA.id" :background="slotA.phase !== 'live'" :class="chatClass(slotA)" @ready="(id: string) => promote('A', id)" />
        <ChatView v-if="slotB" :key="'b' + slotB.id" :session-id="slotB.id" :background="slotB.phase !== 'live'" :class="chatClass(slotB)" @ready="(id: string) => promote('B', id)" />
      </template>
    <!-- 全局自定义右键菜单(复制所选/复制全文) -->
    <ContextMenu />

    <!-- 确认/输入模态框(新建/重命名/删除 Agent) -->
    <ActionDialog v-if="dlg" v-bind="dlg" @confirm="settle" @close="settle(null)" />
  </div>
</template>

<style scoped>
.shell {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: auto;
}

/* 聊天槽位交叉过渡:prep 不可见挂载;live 覆盖上层淡入(底下旧视图继续显示,不露黑底);dying 退到底层等卸载 */
.chat-prep {
  position: fixed;
  inset: 0;
  z-index: 300;
  opacity: 0;
  pointer-events: none;
  overflow: hidden;
}
.chat-live {
  position: fixed;
  inset: 0;
  z-index: 301;
  overflow: hidden;
  animation: chat-in 180ms ease-out;
}
.chat-dying {
  position: relative;
  /* 与新视图淡入同步淡出:两者不透明度此消彼长,文本真正交叉、背景叠加不变亮 */
  animation: chat-out 180ms ease-out forwards;
}
@keyframes chat-in {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}
@keyframes chat-out {
  from {
    opacity: 1;
  }
  to {
    opacity: 0;
  }
}

.error {
  margin: auto;
  color: var(--danger);
}

.home {
  max-width: 760px;
  margin: 0 auto;
  padding: 48px 24px;
}

.brand {
  display: flex;
  align-items: center;
  gap: 14px;
}

.brand h1 {
  margin: 0;
  font-size: 24.2px;
  letter-spacing: 1px;
  flex: 1;
}

.theme-btn {
  flex: none;
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.theme-btn .ms {
  font-size: 16.5px;
}

.sub {
  margin: 6px 0 32px;
  color: var(--fg-secondary);
  font-size: 13.2px;
}

.agents {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 12px;
}

.agent-card {
  position: relative;
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 60px 14px 16px; /* 右侧固定留白:菜单按钮列右对齐 */
  background: var(--bg-panel);
  border: 1px solid var(--border);
  border-radius: var(--radius-panel);
  color: inherit;
  text-decoration: none;
  transition: background 0.15s;
}

.agent-card:hover {
  background: var(--bg-panel-hover);
}

/* 停用只淡化内容(头像/名称),菜单按钮保持可读可点,否则无法启用停用的 Agent */
.agent-card.off .avatar,
.agent-card.off .meta {
  opacity: 0.55;
}

.agent-card.add {
  justify-content: center;
  background: transparent;
  border-style: dashed;
  color: var(--fg-secondary);
  cursor: pointer;
  font-size: 14.3px;
  font-family: var(--font);
}

.agent-card.add .ms {
  font-size: 17.6px;
}

.agent-card.add:hover {
  color: var(--fg-accent);
  border-color: var(--fg-accent);
  background: transparent;
}

.off-tag {
  margin-left: 8px;
  font-size: 11px;
  color: var(--fg-muted);
  border: 1px solid var(--border);
  border-radius: 999px;
  padding: 1px 8px;
  vertical-align: 1px;
}

.menu-btn {
  position: absolute;
  right: 14px;
  top: 50%;
  transform: translateY(-50%);
}

.menu-btn .ms {
  font-size: 17.6px;
}

.menu {
  position: absolute;
  right: 10px;
  top: 48px;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  border-radius: 8px;
  display: flex;
  flex-direction: column;
  min-width: 130px;
  z-index: 30;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
  padding: 4px;
}

.menu button {
  display: flex;
  align-items: center;
  gap: 9px;
  background: transparent;
  border: none;
  color: var(--fg-primary);
  text-align: left;
  padding: 8px 12px;
  font-size: 14.3px;
  font-family: var(--font);
  cursor: pointer;
  border-radius: 6px;
}

.menu button:disabled {
  opacity: 0.38;
  cursor: not-allowed;
}

.menu .ms {
  flex: none;
  width: 17px;
  font-size: 17.6px;  text-align: center;
  color: var(--fg-secondary);
}

.menu button.danger .ms {
  color: var(--danger);
}

.menu button:hover {
  background: var(--selection);
}

.menu button.danger {
  color: var(--danger);
}

.avatar {
  width: 36px;
  height: 36px;
  border-radius: 50%;
  background: var(--accent);
  color: #fff;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 17.6px;
  flex: none;
  object-fit: cover;
}

.name {
  font-size: 15.4px;
}

.hint {
  margin-top: 2px;
  color: var(--fg-muted);
  font-size: 12.1px;
}

.empty {
  color: var(--fg-muted);
  padding: 24px;
  text-align: center;
}
</style>
