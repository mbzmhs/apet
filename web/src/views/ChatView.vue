<script lang="ts">
// 右侧边栏开合态:模块级单例。ChatView 按 :key 随会话切换重挂载,实例级 ref 每次都会重置回关闭;
// 交叉过渡期间新旧视图短暂同挂,共享同一状态避免一侧开着另一侧关着的跳变
import { ref } from 'vue'
export const sidebarOpenShared = ref(false)
// 左侧栏归档视图开合态:同右栏,模块级单例。点归档项切会话重挂载后归档列表保持打开,
// 不再一预览就退回会话列表;交叉过渡期新旧视图共享,避免一侧开一侧关的跳变
export const archivedOpenShared = ref(false)
// 左侧栏文件夹折叠态:模块级 Map(agentId → 被折叠的文件夹 id 集)。实例级 reactive Set 每次重挂载重置,
// 切会话后用户手动折叠的文件夹会全部弹开;写穿到模块级,重挂载时回填,展开态跨会话保持;
// 进程重启后模块级是空的 → 挂载时从后端(Agent 配置文件)取回回填,跨重启恢复
export const collapsedFoldersShared = new Map<string, Set<string>>()
</script>
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, onUnmounted, reactive, ref, nextTick, watch } from 'vue'
import {
  archiveSession, clearArchived, compactSession, createFolder, createSession, deleteFolder, deleteSession, executeCommand,
  forkSession, getAgents, getArchived, getCollapsedFolders, getFolders,
  getCommands, getSession, getSessionOwner, getSessions, getSummary, getSystemPrompt, getProviders,
  moveFolder, moveSessionToFolder, pinSession, renameFolder, renameSession,
  reportLastSession, reorderFolders, reorderSessions, resolveAuth, sendMessage, setCollapsedFolders, setFolderColor, setModel, setQueueDefer, setWorkdir,
  stopSession, takeQueueMessage, undoSession, unarchiveSession,
  moveQueueMessage,
  openWorkdirFolder,
  getPendingAsks, resolveAsk,
  type AskAnswer, type AskPanelInfo, type AskQuestionInfo, type CommandInfo, type ProviderInfo,
} from '../api/client'
import { subscribeStream } from '../api/sse'
import { applyAgentUiScale } from '../lib/uiScale'
import { renderMarkdown } from '../lib/markdown'
import { useActionDialog } from '../lib/actionDialog'
import { useResize } from '../lib/useResize'
import { FOLDER_COLORS, folderColorHex } from '../lib/folderColors'
import { appendToolOutput } from '../lib/toolOutput'
import type { ArchivedSession, CommandResult, FolderInfo, Message, QueuedItem, SessionListItem, SessionState, ThinkingStep, TreeRow } from '../types'

// 每会话输入草稿(组件随会话切换重挂载,模块级 Map 才能跨会话保留;同桌面端 DraftText)
const drafts = new Map<string, string>()
import MessageBubble from '../components/MessageBubble.vue'
import ModalDialog from '../components/ModalDialog.vue'
import ActionDialog from '../components/ActionDialog.vue'
import SidePanel from '../components/SidePanel.vue'
import StatusBar from '../components/StatusBar.vue'
import AppSelect from '../components/AppSelect.vue'
import FileBrowser from '../components/FileBrowser.vue'
import Lightbox from '../components/Lightbox.vue'
import SubAgentModal from '../components/SubAgentModal.vue'
import SidebarTree from '../components/SidebarTree.vue'
import { avatarUrl } from '../api/client'
import { currentTheme, toggleTheme, type ThemeMode } from '../theme/theme'
import {
  cachedAgentAvatarRev, cachedAgentList, cachedAgentTitle, cachedArchived, cachedFolders, cachedOwner, cachedSessions, cachedState,
  dropState, lastShownListSnapshot, markListShown, sessionsFresh,
  storeAgentAvatarRev, storeAgentList, storeAgentTitle, storeArchived, storeFolders, storeLastChat, storeOwner, storeOwners, storeSessions, storeState,
} from '../store/sessionCache'

// background:后台预备视图(App 在旧视图之上不可见挂载本会话,数据就绪发 ready 后再切换上屏)
const props = defineProps<{ sessionId: string; background?: boolean }>()
const emit = defineEmits<{ ready: [sessionId: string] }>()

const state = ref<SessionState | null>(null)
// 归档只读预览:输入区/发送/命令/工作目录全部禁用,仅回看历史
const isArchived = computed(() => state.value?.isArchived === true)
// 旧版压缩状态行("会话上下文已压缩")已落盘在历史会话里,与分割标记重复,展示时过滤掉
const visibleMessages = computed(() =>
  (state.value?.messages ?? []).filter(m => !(m.role === 'status' && m.text === '会话上下文已压缩')),
)
// ───────── 历史分页:首屏只取末尾 MESSAGE_WINDOW 行;向上滚近顶再按页前插更早行(滚动位置补偿) ─────────
const MESSAGE_WINDOW = 200
const OLDER_PAGE = 100
let loadingOlder = false
const input = ref('')
// 页面级提示/错误:统一 toast(底部居中,自动消失,点击提前关闭);不再用顶部横幅
type ToastKind = 'ok' | 'err'
const toast = ref<{ msg: string; kind: ToastKind } | null>(null)
let toastTimer: number | null = null
function showToast(msg: string, kind: ToastKind = 'ok') {
  if (!msg) return
  if (toastTimer !== null) window.clearTimeout(toastTimer)
  toast.value = { msg, kind }
  toastTimer = window.setTimeout(() => {
    toast.value = null
  }, kind === 'err' ? 6000 : 3500)
}
function showErr(msg: string) {
  showToast(msg, 'err')
}
// 确认/输入模态框(替代浏览器原生 confirm/prompt)
const { dlg, ask, settle } = useActionDialog()
// $warmup 声线预热:模态进度框;完成后短暂展示结果自动收起,点击可提前关闭
const warmup = ref<{ total: number; done: number; current: string; message: string } | null>(null)
let warmupTimer: number | null = null
const warmupPct = computed(() => {
  const w = warmup.value
  if (!w) return 0
  if (w.message) return 100
  return w.total > 0 ? Math.min(100, Math.round((w.done / w.total) * 100)) : 0
})
function closeWarmup() {
  if (warmupTimer !== null) window.clearTimeout(warmupTimer)
  warmup.value = null
}
onUnmounted(() => {
  if (toastTimer !== null) window.clearTimeout(toastTimer)
  if (warmupTimer !== null) window.clearTimeout(warmupTimer)
})
const listEl = ref<HTMLElement | null>(null)
const agentId = ref('')

const providers = ref<ProviderInfo[]>([])
const commands = ref<CommandInfo[]>([])
const THOUGHT_LEVELS = ['none', 'default', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max']
/** 思考等级中文文案(值仍是后端枚举;default = 开启,强度按 high 发送) */
const THINK_LABELS = [
  ['none', '关闭'], ['default', '开启'], ['minimal', '极简'], ['low', '低'], ['medium', '中'],
  ['high', '高'], ['xhigh', '超高'], ['max', '最高'],
]
/** 等级英文原文(tooltip 展示) */
const LEVEL_EN: Record<string, string> = {
  none: 'none', default: 'default(即 high)', minimal: 'minimal', low: 'low',
  medium: 'medium', high: 'high', xhigh: 'xhigh', max: 'max',
}

const modelOptions = computed(() =>
  providers.value.find((p) => p.name === state.value?.provider)?.models ?? [])

// 切换模型后:若当前思考等级不在新模型的可用档位里,自动落到该模型的第一档
watch(
  () => [state.value?.provider, state.value?.model],
  async () => {
    const s = state.value
    if (!s || !s.model) return
    const levels = providers.value.find((p) => p.name === s.provider)?.models?.find((m) => m.name === s.model)?.thinkingLevels ?? []
    if (levels.length && !levels.includes(s.thinkingEffort)) {
      await applyModel({ thinkingEffort: levels[0] })
    }
  },
)

let closeStream: (() => void) | null = null

// 归属已知时的预取:setup 末段(首帧渲染前)即并行发出,挂载后直接等结果,
// 把"侧栏已就位、主区空白"的空窗从串行的多个往返压到约一个往返
type PreFetch = { agentId: string; state: Promise<SessionState>; providers: Promise<ProviderInfo[]>; commands: Promise<CommandInfo[]> }
let preFetch: PreFetch | null = null

onMounted(async () => {
  try {
    const owner = ownerCached || (await getSessionOwner(props.sessionId))
    if (!owner) {
      showErr('会话不存在(可能已被删除)')
      unfreeze() // 退出冻结:露出骨架/空态,别卡在空白
      return
    }
    const pre = preFetch && preFetch.agentId === owner ? preFetch : null
    agentId.value = owner
    storeOwner(props.sessionId, owner)
    // 文件夹折叠态回填:模块级单例有(本进程内切会话重挂载)直接用;
    // 没有(进程重启后首次挂载)从后端取回——折叠状态跨重启恢复
    const savedCollapsed = collapsedFoldersShared.get(owner)
    if (savedCollapsed) {
      for (const id of savedCollapsed) collapsedFolders.add(id)
    } else {
      getCollapsedFolders(owner)
        .then((ids) => {
          const set = collapsedFoldersShared.get(owner) ?? new Set<string>()
          for (const id of ids) set.add(id)
          collapsedFoldersShared.set(owner, set)
          for (const id of ids) collapsedFolders.add(id)
        })
        .catch(() => {})
    }
    openStream() // 归属解析后才开:此时 agentId 已就位,订阅地址才有效(挂载瞬间 agentId 尚为空,早开会订到 /agents//stream 404 永不翻牌);Agent 级事件经 withAgent=1 并入同一条连接
    storeLastChat(owner, props.sessionId) // 记住本 Agent 最近会话:设置页返回时回到这里
    // 标题/头像先用缓存(首页加载过 Agent 清单即有),不等 getAgents 往返
    if (!agentTitle.value) {
      const a = cachedAgentList()?.find((x) => x.id === owner)
      if (a) {
        agentTitle.value = a.title
        storeAgentTitle(owner, a.title)
        agentAvatarRev.value = a.avatarRev
        storeAgentAvatarRev(owner, a.avatarRev)
      }
    }
    if (!props.background) applyAgentUiScale(owner) // 聊天窗按该 Agent 的界面缩放生效;后台预备视图等上屏时再应用(避免旧视图还显示着缩放先跳)
    // 首屏窗口化:只拉末尾 MESSAGE_WINDOW 行,更早历史向上滚动时按需翻页(见 loadOlder)
    state.value = await (pre ? pre.state : getSession(agentId.value, props.sessionId, { limit: MESSAGE_WINDOW }))
    storeState(props.sessionId, state.value)
    // 预载背景图:背景层揭示时才建元素,先焐热浏览器缓存,避免揭示后图片"补现"
    if (state.value?.windowBgOpacity) new Image().src = `/api/agents/${agentId.value}/backgrounds/window`
    if (state.value?.chatBgOpacity) new Image().src = `/api/agents/${agentId.value}/backgrounds/chat`
    if (frozen.value) nextTick(pinReveal) // 新数据就位后再钉底揭示
    providers.value = await (pre ? pre.providers : getProviders(agentId.value))
    commands.value = await (pre ? pre.commands : getCommands(agentId.value))
    input.value = drafts.get(props.sessionId) ?? '' // 还原本会话草稿
    refreshSessions()
    reportViewing() // 列表缓存已同步回填,上报后本地立刻熄点能命中行(移到取状态之后:原在挂载首行时行还没进列表)
    openStream()
    loadPendingAsks() // 刷新前遗留的待决询问重建面板
    scrollBottom(true) // 打开/切换会话直接定位到最新消息
    getAgents()
      .then((list) => {
        storeAgentList(list) // 供其它页面/会话切换首帧直接取标题头像
        const me = list.find((a) => a.id === agentId.value)
        agentTitle.value = me?.title ?? ''
        storeAgentTitle(agentId.value, agentTitle.value)
        agentAvatarRev.value = me?.avatarRev ?? 0
        storeAgentAvatarRev(agentId.value, agentAvatarRev.value)
      })
      .catch(() => {})
  } catch (e) {
    showErr(String(e))
    unfreeze()
  }
})

onUnmounted(() => closeStream?.())

// ───────── 迟到重排补钉底 ─────────
// 钉底只在 SSE 帧后触发;内容还会在无帧时机长高——markdown 图片无固有尺寸加载完成、代码高亮/字体
// 替换后重排、complete 时 refetch 重渲染。这些时刻没人再调 scrollBottom,尾部就停在了视口之外。
// 监听"任何 DOM 变更/资源加载完成"后补一次钉底判定:阈值判定在事件当下进行,用户主动展开思考卡
// 会把尾部推远(>160px)不会被误拉;小尺寸迟到重排(<160px)则贴回底部。
let patchObserver: MutationObserver | null = null
const onResourceLoad = () => scrollBottom()

onMounted(() => {
  const el = listEl.value
  if (!el) return
  patchObserver = new MutationObserver(() => scrollBottom())
  patchObserver.observe(el, { childList: true, subtree: true, characterData: true })
  // load 不冒泡:capture 委托才能收到消息区内图片的加载完成
  el.addEventListener('load', onResourceLoad, true)
  // 标签页切回前台:后台期间 SSE 若中断过,状态可能陈旧(转圈卡死)——补一次快照自愈
  document.addEventListener('visibilitychange', onVisibilityRefetch)
})

onUnmounted(() => {
  patchObserver?.disconnect()
  patchObserver = null
  listEl.value?.removeEventListener('load', onResourceLoad, true)
  document.removeEventListener('visibilitychange', onVisibilityRefetch)
})

// ───────── SSE 流式 ─────────

function openStream() {
  closeStream?.()
  // withAgent=1:同一条连接并入 Agent 级总线(sessions_changed)——页面只占 1 条常驻连接。
  // 此前会话流+Agent 流两条 SSE,多 Agent 多窗口把 HTTP/1.1 每主机 6 连接上限吃满,
  // 后续请求全部在浏览器内排队 pending(后端无痕),表现为整个前端假死。
  // 值必须是 true 而非 1:服务端 bool? 绑定不认 "1",绑定失败=裸 400,EventSource 非 2xx 即永久断开
  closeStream = subscribeStream(
    `/api/agents/${agentId.value}/sessions/${props.sessionId}/stream?withAgent=true`,
    handleEvent,
    () => void refetch(), // 重连成功(含首次连接):补一次快照,断线窗口丢帧自愈,幂等无害
  )
}

/** 标签页切回前台时的快照自愈(转圈卡死兜底;后台标签的定时器被节流,回来补一次最可靠) */
function onVisibilityRefetch() {
  if (!document.hidden) void refetch()
}

/** 流式目标气泡:最后一条是助手气泡则复用,否则新建(其他标签页先发送时本地还没有) */
let localRowSeq = 0
/** 无 seq 的本地行(乐观气泡/事件状态行)补稳定渲染 key:列表前插/对齐时下标 key 会错位复用 DOM */
function withLocalKey<T extends Message>(m: T): T {
  if ((m.seq ?? null) === null && !m.localKey) m.localKey = 'k' + ++localRowSeq
  return m
}

function streamingMsg(): Message {
  const s = state.value!
  s.isStreaming = true // 流式帧在途 = 回合进行中(多标签页/迟到帧同样成立;complete/error 经 refetch 收口)
  let m = s.messages[s.messages.length - 1]
  if (!m || m.role !== 'assistant') {
    m = withLocalKey({
      clientId: null, role: 'assistant', text: '', timeText: '',
      isError: false, imageFiles: [], textFiles: null, steps: [],
    })
    s.messages.push(m)
  }
  return m
}

/** 运行指示射线:第 n 根(n=1..8)逆时针布圈,配合同序负延迟后波纹仍顺时针流转。
 *  负延迟是关键:正延迟会让动画等满 (n-1)/8 秒才启动、期间射线全亮静止
 *  (刚出现的一秒像静止星芒);负延迟让每根在创建瞬间就处于错开相位,立刻"转"起来 */
function rayStyle(n: number) {
  return {
    transform: `rotate(${-(n - 1) * 45}deg)`,
    animationDelay: `${-(n - 1) / 8}s`,
  }
}

function endThinking(m: Message) {
  const last = m.steps?.[m.steps.length - 1] as ThinkingStep | undefined
  if (last && last.kind === 'thinking' && last.isActive) {
    last.isActive = false
    last.header = '思考'
  }
}

// ───────── 状态栏 LLM 统计:会话累计用量(usage 帧) + 生成速度近似(流式字符率) ─────────
// 组件随会话重挂载,状态天然按会话归零
const llmStats = reactive({
  promptTotal: 0,
  completionTotal: 0,
  cachedTotal: 0,
  lastTps: 0, // 最新一次请求的输出速度(宿主计时,随会话持久化)
  cost: 0, // 会话累计费用(元;宿主按模型计费因子算,随会话持久化)
  streaming: false,
})
// 会话快照(含持久化累计用量)每次变化都重播种:服务端与本地上增同一批 usage 帧,
// 数值恒等,不存在重复累加;挂载时即从落盘值继续,重开会话不丢
watch(
  () => state.value,
  (s) => {
    const u = s?.usage
    if (!u) return
    llmStats.promptTotal = u.promptTotal
    llmStats.cachedTotal = u.cachedTotal
    llmStats.completionTotal = u.completionTotal
    llmStats.lastTps = u.lastTps ?? 0
    llmStats.cost = u.cost ?? 0
  },
)
// streaming 只作展示标记(状态栏首轮回合进行中也可见);TPS 一律用服务端实测值
// (usage 帧的 tps:宿主 Stopwatch 计时 + 供应商真实 token 数),不做前端字符估算
function beginLlmRun() {
  llmStats.streaming = true
}
function endLlmRun() {
  llmStats.streaming = false
}
onBeforeUnmount(() => {
  if (llmStats.streaming) endLlmRun()
})

async function handleEvent(type: string, data: any) {
  // Agent 级跨会话事件(withAgent=1 并入本连接):会话列表状态变化 → 合并强刷一次
  if (type === 'sessions_changed') {
    onSessionsChanged()
    return
  }
  // $warmup 预热进度:模态进度框(与消息数据无关,快照未就绪也要能显示)
  if (type === 'warmup_started') {
    if (warmupTimer !== null) window.clearTimeout(warmupTimer)
    warmup.value = { total: data.total, done: 0, current: '', message: '' }
    return
  }
  if (type === 'warmup_progress') {
    if (!warmup.value) warmup.value = { total: data.total, done: 0, current: '', message: '' }
    if (warmupTimer !== null) window.clearTimeout(warmupTimer)
    warmup.value = { ...warmup.value, total: data.total, done: data.done, current: data.current }
    return
  }
  if (type === 'warmup_finished') {
    if (!warmup.value) warmup.value = { total: 0, done: 0, current: '', message: '' }
    warmup.value = { ...warmup.value, message: data.message }
    warmupTimer = window.setTimeout(() => {
      warmup.value = null
    }, 2600)
    return
  }
  const s = state.value
  if (!s) return
  if (type === 'usage') {
    s.lastPromptTokens = data.promptTokens
    // 状态栏会话累计(按请求累加;快照重播种时与服务端恒等)
    llmStats.promptTotal += data.promptTokens ?? 0
    llmStats.completionTotal += data.completionTokens ?? 0
    llmStats.cachedTotal += data.cachedTokens ?? 0
    if (data.tps > 0) llmStats.lastTps = data.tps // TPS 只留最新一次
    llmStats.cost += data.cost ?? 0 // 费用按请求累加(快照重播种时与服务端恒等)
    return
  }
  // turn_started:携带本轮 context msgId 的标记帧;后端在 POST 请求体内同步发布,可能先于 202
  // 抢先到达——绝不能落到 streamingMsg()(那会在用户消息入列表前凭空造出占位气泡)。id 前端暂未消费
  if (type === 'turn_started') {
    beginLlmRun()
    return
  }
  if (type === 'queue_changed') {
    // 队列事件就地打补丁,绝不整树 refetch:运行期整树刷新会与在途的 tool_finished 帧竞态——
    // 先应用的收口帧被更早时刻拍的快照盖回"运行中",且此后不再有第二帧,工具行永久卡死
    const s = state.value
    if (s) {
      if (data.queuedMessages) s.queuedMessages = data.queuedMessages
      if (data.streaming === true) s.isStreaming = true
      if (data.insert) insertQueuedItems(data.insert.mode, data.insert.items)
    }
    return
  }
  // 排队触发的命令:后端派发时已按命令执行完(不进 LLM 上下文),这里做与 runCommand 相同的收尾
  if (type === 'command_executed') {
    await applyCommandResult(String(data.text ?? ''), data, Date.now() - mountedAt > 1000)
    return
  }
  if (type === 'complete' || type === 'turn_ended' || type === 'error' || type === 'compress_start') {
    endLlmRun()
    // 运行期错误由消息流里的告警卡呈现(后端已落盘状态行),顶部横幅不再重复显示
    await refetch()
    if (Date.now() - mountedAt > 1000) scrollBottom() // 刚切换上来不自动滚,避免整屏换位
    return
  }
  // ask 询问:面板不进气泡步骤流,单独挂在输入卡片上
  if (type === 'ask_request') {
    openAsk(data)
    return
  }
  if (type === 'ask_resolved') {
    closeAsk(data.askId)
    return
  }

  const m = streamingMsg()
  const steps = m.steps ?? (m.steps = [])
  switch (type) {
    case 'thinking_delta': {
      const last = steps[steps.length - 1] as ThinkingStep | undefined
      if (last && last.kind === 'thinking' && last.isActive) last.text += data.delta
      else steps.push({ kind: 'thinking', text: data.delta, isActive: true, header: '思考中', seconds: null })
      break
    }
      case 'content_delta': {
        endThinking(m)
      const last = steps[steps.length - 1]
      if (last && last.kind === 'content') last.text += data.delta
      else steps.push({ kind: 'content', text: data.delta })
      break
    }
    case 'tool_args': {
      // 参数流式预览(执行开始前):LLM 还在吐参数就先把"正在生成哪个文件的编辑 +N/−N"亮出来。
      // 帧是节流快照(幂等),按 argsKey 就地刷新占位行;tool_started 到达后被权威完整行回填。
      // 字段形状对齐真实 started 行(displayName 中文 / summary=纯路径 / 统计独立字段),样式同一套
      endThinking(m)
      const key = data.callId ?? 'k' + data.argsKey
      const ph: any = steps.find((s: any) => s.kind === 'tool' && s.isRunning && s.argsKey === key)
      const pPath = data.path ?? ''
      const pSummary = pPath || '生成参数中…' // 与真实 started 行同形:summary=显示路径,统计走独立彩色字段
      const slash = Math.max(pPath.lastIndexOf('/'), pPath.lastIndexOf('\\'))
      const row0 = {
        displayName: data.displayName ?? data.toolName ?? '工具调用',
        summary: pSummary,
        pathLink: slash >= 0 ? pPath.slice(slash + 1) : pPath, // 与真实行同形:pathLink=文件名
        statAdd: data.addLines ? `+${data.addLines}` : '',
        statDel: data.delLines ? `-${data.delLines}` : '',
      }
      if (ph) {
        Object.assign(ph, row0)
      } else {
        steps.push({
          kind: 'tool', toolName: data.toolName ?? 'tool', toolCallId: data.callId ?? null,
          argsKey: key, isPlaceholder: true,
          ...row0,
          detail: '', hasDetail: false, isRunning: true, isError: false,
          resultSummary: '', diffLines: [], imageFiles: [], subRunId: null, subRunTitle: '',
        })
      }
      break
    }
    case 'tool_started': {
      endThinking(m)
      const startedRow = {
        kind: 'tool', toolName: data.toolName, toolCallId: data.toolCallId ?? null,
        displayName: data.displayName, summary: data.summary,
        // 运行中即可展开看完整内容(如 bash 的完整命令);输出经 tool_output 帧增量续接
        detail: data.detail ?? '', hasDetail: !!data.hasDetail, isRunning: true, isError: false,
        resultSummary: '', pathLink: data.pathLink ?? '',
        statAdd: data.statAdd ?? '', statDel: data.statDel ?? '', diffLines: data.diffLines ?? [], imageFiles: [],
        subRunId: data.subRunId ?? null,
        subRunTitle: data.subRunTitle ?? '',
        isPlaceholder: false,
      }
      // 回填参数流式占位行(避免同一调用出现两张卡):callId 或 argsKey 能对上即就地替换
      const fid0 = data.toolCallId ?? null
      const ph0: any = steps.find((s: any) => s.kind === 'tool' && s.isRunning && s.isPlaceholder && (
        (fid0 != null && (s.toolCallId === fid0 || (data.argsKey != null && s.argsKey === 'k' + data.argsKey))) ||
        (fid0 == null && s.toolCallId == null && s.toolName === data.toolName)))
      if (ph0) Object.assign(ph0, startedRow)
      else steps.push(startedRow)
      break
    }
    case 'tool_output': {
      // 工具执行中的实时输出(运行终端工具的 stdout/stderr):增量续接到运行中的工具行
      appendToolOutput(steps as any[], data)
      break
    }
    case 'tool_finished': {
      // callId 严格匹配(与 toolOutput.appendToolOutput 同规则):两侧都有 id 才可比,
      // 任一侧缺失即视为不同行——同名并发调用按名字会错配收口到别的行
      for (let i = steps.length - 1; i >= 0; i--) {
        const st: any = steps[i]
        if (st.kind !== 'tool' || !st.isRunning || st.toolName !== data.toolName) continue
        const fid = data.toolCallId ?? null
        const rid = st.toolCallId ?? null
        if ((fid ?? rid) !== null && fid !== rid) continue
        if (data.subRunId != null && st.subRunId != null && st.subRunId !== data.subRunId) continue
        Object.assign(st, {
          isRunning: false, detail: data.detail, hasDetail: data.hasDetail,
          summary: data.summary ?? st.summary, // 摘要完成态可能变化(如 ask:等待作答→N 个问题)
          resultSummary: data.resultSummary, isError: data.isError,
          toolCallId: data.toolCallId ?? st.toolCallId ?? null,
          pathLink: data.pathLink ?? st.pathLink ?? '',
          statAdd: data.statAdd, statDel: data.statDel, diffLines: data.diffLines,
          imageFiles: data.imageFiles ?? [],
          subRunId: data.subRunId ?? st.subRunId ?? null,
          subRunTitle: data.subRunTitle || st.subRunTitle || '',
        })
        break
      }
      break
    }
    case 'auth_request': {
      endThinking(m)
      steps.push({
        kind: 'auth', authId: data.authId, detail: data.detail,
        reason: data.reason, command: data.command,
        trustDirs: data.trustDirs ?? [], decided: false, granted: false,
      })
      break
    }
    case 'auth_resolved': {
      const st: any = steps.find((x: any) => x.kind === 'auth' && x.authId === data.authId)
      if (st) {
        st.decided = true
        st.granted = data.granted
      }
      break
    }
  }
  scrollBottom()
}

async function refetch() {
  try {
    // 窗口感知刷新:已加载过历史(cursor 非空)只刷"窗口首行起 + 尾部新增",不把整段历史再搬一遍;
    // 全量态(短会话/模型切换等全量响应重置后)照旧整刷
    const cursor = state.value?.messageCursor
    state.value = cursor == null
      ? await getSession(agentId.value, props.sessionId)
      : await getSession(agentId.value, props.sessionId, { after: cursor - 1 })
    storeState(props.sessionId, state.value)
  } catch (e) {
    showErr(String(e))
  }
}

async function onAuth(step: any, allow: boolean, trust: boolean) {
  try {
    await resolveAuth(step.authId, allow, trust)
  } catch (e) {
    showErr(String(e))
  }
}

// ───────── ask 询问面板(盖住输入卡片;Tab/上下移动,Enter/空格选中,←/→ 切题) ─────────

interface AskPanelState {
  askId: string
  questions: AskQuestionInfo[]
  /** 当前题号(多题翻页) */
  page: number
  /** 每题选中的选项下标(单选至多一个;多选 0~n 个) */
  sel: number[][]
  /** 每题"其他"自由输入文本(非空即视为选中) */
  other: string[]
  /** 键盘光标:0..options.length,最后一项是"其他"输入行 */
  cursor: number
}

const asks = ref<AskPanelState[]>([])
const currentAsk = computed(() => asks.value[0] ?? null)
const askPanelEl = ref<HTMLElement | null>(null)
const askOtherEl = ref<HTMLInputElement | null>(null)

/** 当前题(面板只展示队首询问的当前页) */
const askCur = computed(() => {
  const a = currentAsk.value
  if (!a) return null
  const q = a.questions[a.page]
  return q ? { ask: a, q } : null
})

function openAsk(info: AskPanelInfo) {
  if (!info.questions?.length) return
  if (asks.value.some((a) => a.askId === info.askId)) return
  asks.value.push({
    askId: info.askId,
    questions: info.questions,
    page: 0,
    sel: info.questions.map(() => []),
    other: info.questions.map(() => ''),
    cursor: 0,
  })
  nextTick(() => askPanelEl.value?.focus())
}

function closeAsk(askId: string) {
  const i = asks.value.findIndex((a) => a.askId === askId)
  if (i < 0) return
  asks.value.splice(i, 1)
  if (!asks.value.length) nextTick(() => inputEl.value?.focus())
}

/** 页面刷新/重开后重建待决询问面板 */
async function loadPendingAsks() {
  try {
    const list = await getPendingAsks(agentId.value, props.sessionId)
    for (const info of list) openAsk(info)
  } catch {
    // 无待决询问或端点不可用:忽略
  }
}

function askRowCount(q: AskQuestionInfo) {
  return q.options.length + 1
}

/** 移动键盘光标;落到"其他"行时把焦点交给输入框,否则收回面板容器 */
function askMove(delta: number) {
  const a = currentAsk.value
  if (!a) return
  const q = a.questions[a.page]
  if (!q) return
  const n = askRowCount(q)
  a.cursor = (a.cursor + delta + n) % n
  nextTick(() => {
    if (a.cursor === n - 1) askOtherEl.value?.focus()
    else askPanelEl.value?.focus()
  })
}

function askPage(delta: number) {
  const a = currentAsk.value
  if (!a) return
  const next = a.page + delta
  if (next < 0 || next >= a.questions.length) return
  a.page = next
  a.cursor = 0
  nextTick(() => askPanelEl.value?.focus())
}

/** 选中光标项:单选独占并自动下一题;多选就地切换 */
function askPick() {
  const a = currentAsk.value
  if (!a) return
  const q = a.questions[a.page]
  if (!q) return
  const i = a.cursor
  if (i >= q.options.length) {
    // "其他"行:有文本则推进,无文本仅聚焦输入框
    if (!a.other[a.page].trim()) {
      askOtherEl.value?.focus()
      return
    }
    askAdvance()
    return
  }
  if (q.multiSelect) {
    const sel = a.sel[a.page]
    const at = sel.indexOf(i)
    if (at >= 0) sel.splice(at, 1)
    else sel.push(i)
  } else {
    a.sel[a.page] = [i]
    askAdvance()
  }
}

/** 非最后一题则翻页,最后一题则提交 */
function askAdvance() {
  const a = currentAsk.value
  if (!a) return
  if (a.page < a.questions.length - 1) {
    a.page++
    a.cursor = 0
    nextTick(() => askPanelEl.value?.focus())
  } else {
    askSubmit()
  }
}

function buildAskAnswers(a: AskPanelState): AskAnswer[] {
  return a.questions.map((q, i) => ({
    title: q.title,
    question: q.question,
    selected: q.options.filter((_, oi) => a.sel[i].includes(oi)).map((o) => o.label),
    other: a.other[i].trim() || undefined,
  }))
}

async function askSubmit() {
  const a = currentAsk.value
  if (!a) return
  try {
    await resolveAsk(a.askId, buildAskAnswers(a))
    closeAsk(a.askId) // 服务端随后广播 ask_resolved,closeAsk 幂等
  } catch (e) {
    showErr(String(e))
  }
}

async function askSkip() {
  const a = currentAsk.value
  if (!a) return
  try {
    await resolveAsk(a.askId, null)
    closeAsk(a.askId)
  } catch (e) {
    showErr(String(e))
  }
}

function onAskKeydown(e: KeyboardEvent) {
  const a = currentAsk.value
  if (!a) return
  // "其他"输入框聚焦时:Enter 提交本题(输入法组词回车不触发),↑ 回到选项;其余键放行给文本编辑
  if (e.target === askOtherEl.value) {
    if (e.key === 'Enter' && !(e.isComposing || e.keyCode === 229)) {
      e.preventDefault()
      askAdvance()
    } else if (e.key === 'ArrowUp' && !e.isComposing) {
      e.preventDefault()
      askMove(-1)
    }
    return
  }
  switch (e.key) {
    case 'ArrowDown':
      e.preventDefault()
      askMove(1)
      break
    case 'ArrowUp':
      e.preventDefault()
      askMove(-1)
      break
    case 'Tab':
      e.preventDefault()
      askMove(e.shiftKey ? -1 : 1)
      break
    case 'ArrowLeft':
      e.preventDefault()
      askPage(-1)
      break
    case 'ArrowRight':
      e.preventDefault()
      askPage(1)
      break
    case 'Enter':
    case ' ':
      e.preventDefault()
      askPick()
      break
  }
}

// ───────── 附件(文件/图片/视频/音频/PDF;类型由当前模型的媒体类型配置决定) ─────────

interface WebAttachment {
  kind: 'image' | 'text' | 'media'
  /** media 的具体类型(pdf/video/audio),决定图标 */
  media?: 'pdf' | 'video' | 'audio'
  mime?: string
  name: string
  dataUrl?: string
  content?: string
  sizeText: string
}

const attachments = ref<WebAttachment[]>([])
const fileInput = ref<HTMLInputElement | null>(null)
const pendingKind = ref<'file' | 'image' | 'video' | 'audio' | 'pdf'>('file')
const MAX_MEDIA = 20 * 1024 * 1024
const MAX_TEXT = 512 * 1024

function formatSize(n: number) {
  return n >= 1024 * 1024 ? `${(n / 1024 / 1024).toFixed(1)} MB` : n >= 1024 ? `${(n / 1024).toFixed(1)} KB` : `${n} B`
}

function readAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(reader.result as string)
    reader.onerror = () => reject(reader.error)
    reader.readAsDataURL(file)
  })
}

const EXT_MIME: Record<string, string> = {
  '.pdf': 'application/pdf', '.mp3': 'audio/mpeg', '.wav': 'audio/wav', '.ogg': 'audio/ogg',
  '.m4a': 'audio/mp4', '.mp4': 'video/mp4', '.webm': 'video/webm', '.mov': 'video/quicktime',
}

/**
 * 读取待发送附件。forced 为 ＋ 菜单选定的类型;粘贴/拖入不带类型,按图片/文本自动判断。
 * 图片与媒体(pdf/音视频)以 data URL 发送;其他文件以文本方式内联(≤512KB)。
 */
async function addFiles(files: Iterable<File>, forced?: 'file' | 'image' | 'video' | 'audio' | 'pdf') {
  for (const f of [...files]) {
    try {
      const lower = f.name.toLowerCase()
      const isImageFile = f.type.startsWith('image/')
      const ext = lower.slice(lower.lastIndexOf('.'))
      const mediaMime = forced && forced !== 'file' && forced !== 'image'
        ? (f.type || EXT_MIME[ext] || '')
        : ''
      if (forced === 'image' || (!forced && isImageFile)) {
        if (!isImageFile) {
          showErr(`「${f.name}」不是图片文件`)
          continue
        }
        if (f.size > MAX_MEDIA) {
          showErr(`图片「${f.name}」超过 ${formatSize(MAX_MEDIA)} 上限`)
          continue
        }
        attachments.value.push({
          kind: 'image', name: f.name, dataUrl: await readAsDataUrl(f), sizeText: formatSize(f.size),
        })
      } else if (forced === 'video' || forced === 'audio' || forced === 'pdf') {
        const ok = forced === 'pdf' ? mediaMime === 'application/pdf' : mediaMime.startsWith(forced + '/')
        if (!ok) {
          showErr(`「${f.name}」不是支持的${forced === 'pdf' ? 'PDF' : forced === 'video' ? '视频' : '音频'}格式`)
          continue
        }
        if (f.size > MAX_MEDIA) {
          showErr(`「${f.name}」超过 ${formatSize(MAX_MEDIA)} 上限`)
          continue
        }
        attachments.value.push({
          kind: 'media', media: forced, mime: mediaMime, name: f.name,
          dataUrl: await readAsDataUrl(f), sizeText: formatSize(f.size),
        })
      } else {
        if (f.size > MAX_TEXT) {
          showErr(`文本文件「${f.name}」超过 ${formatSize(MAX_TEXT)} 上限`)
          continue
        }
        attachments.value.push({ kind: 'text', name: f.name, content: await f.text(), sizeText: formatSize(f.size) })
      }
    } catch (e) {
      showErr(`读取「${f.name}」失败:${String(e)}`)
    }
  }
}

function removeAttachment(i: number) {
  attachments.value.splice(i, 1)
}

async function onPaste(e: ClipboardEvent) {
  const files = [...(e.clipboardData?.files ?? [])].filter((f) => f.type.startsWith('image/'))
  if (files.length) {
    e.preventDefault()
    await addFiles(files)
  }
}

function onDragOver(e: DragEvent) {
  if (e.dataTransfer?.types.includes('Files')) e.preventDefault()
}

async function onDrop(e: DragEvent) {
  const files = [...(e.dataTransfer?.files ?? [])]
  if (files.length) {
    e.preventDefault()
    await addFiles(files)
  }
}

// ───────── 斜杠命令键盘导航(WPF 同语义:上下选择,Tab/Enter 补全,Esc 关闭) ─────────

const cmdIndex = ref(0)
const cmdPopupOpen = ref(false)
const cmdMatches = computed(() => {
  const text = input.value
  if (text.length === 0 || text.includes('\n') || !['/', '$'].includes(text[0])) return []
  const firstToken = text.split(/[ \t]/)[0]
  const matches = commands.value.filter((c) => c.name.toLowerCase().startsWith(firstToken.toLowerCase()))
  return matches
})

function closeCmdPopup() {
  cmdPopupOpen.value = false
  cmdIndex.value = 0
}

watch(input, () => {
  cmdPopupOpen.value = true
  cmdIndex.value = 0
})

function completeSelected() {
  const cmd = cmdMatches.value[cmdIndex.value]
  if (!cmd) return
  input.value = cmd.name + (cmd.takesArgument ? ' ' : '')
  closeCmdPopup()
}

function moveCmdSelection(delta: number) {
  const n = cmdMatches.value.length
  if (n === 0) return
  cmdIndex.value = (cmdIndex.value + delta + n) % n
}

/** 输入首 token 是否恰为一条完整命令名(/summary、$xx;含参数也算),是则返回该命令 */
function exactCommandOf(text: string) {
  const firstToken = text.trimStart().split(/[ \t]/)[0]
  return ['/', '$'].includes(firstToken[0])
    ? commands.value.find((c) => c.name.toLowerCase() === firstToken.toLowerCase())
    : undefined
}

async function onEnter(e?: KeyboardEvent, defer = false) {
  // 中文输入法组词回车不发送(isComposing/keyCode 229 = 组词中)
  if (e && (e.isComposing || e.keyCode === 229)) return
  const text = input.value
  // 只发图片/附件时允许正文为空
  if ((!text.trim() && !attachments.value.length) || !state.value) return
  if (state.value.isStreaming) {
    // 运行中回车 = 加入中途消息队列(服务端入队,下一次请求前按 FIFO 带出,命令由消费时触发)。
    // 但补全面板开着且还不是完整命令名时,先补全不入队——与空闲态同语义,半截命令不进队列。
    // defer(Ctrl+Enter)= 标记"本轮后发送":不拼进当前请求,本轮结束后作为新消息发出
    if (!exactCommandOf(text) && cmdPopupOpen.value && cmdMatches.value.length > 0) {
      completeSelected()
      return
    }
    input.value = ''
    autoGrow()
    await sendText(text, defer)
    return
  }
  if (exactCommandOf(text)) {
    input.value = ''
    closeCmdPopup()
    await runCommand(text)
    return
  }
  if (cmdPopupOpen.value && cmdMatches.value.length > 0) {
    completeSelected() // WPF 同语义:Enter 先补全并关闭面板,再按 Enter 才执行
    return
  }
  await send()
}

async function onInputKeydown(e: KeyboardEvent) {
  // Ctrl/Cmd+Enter:入队时直接标记"本轮后发送"(空闲时无队列,等同回车发送)。
  // .exact 的 enter 处理器不匹配带修饰键的回车,这里接管不会双发
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && !e.isComposing && e.keyCode !== 229) {
    e.preventDefault()
    await onEnter(e, true)
    return
  }
  if (e.key === 'Escape' && cmdPopupOpen.value) {
    closeCmdPopup()
    e.preventDefault()
    return
  }
  if (cmdPopupOpen.value && cmdMatches.value.length > 0) {
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      moveCmdSelection(1)
      return
    }
    if (e.key === 'ArrowUp') {
      e.preventDefault()
      moveCmdSelection(-1)
      return
    }
    if (e.key === 'Tab') {
      e.preventDefault()
      completeSelected()
      return
    }
  }
}

// ───────── 会话工作目录(胶囊,首次对话后锁定,与 WPF 同规则) ─────────

const workdirBrowser = ref(false)

const workdirName = computed(() => {
  const dir = state.value?.workingDirectory ?? ''
  if (!dir) return ''
  const name = dir.split(/[\\/]/).filter(Boolean).pop() ?? dir
  return name
})

async function applyWorkdir(dir: string) {
  workdirBrowser.value = false
  try {
    await setWorkdir(agentId.value, props.sessionId, dir)
    await refetch()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function clearWorkdir() {
  try {
    await setWorkdir(agentId.value, props.sessionId, '')
    await refetch()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function applyWorkdirAccess(access: string) {
  try {
    await setWorkdir(agentId.value, props.sessionId, state.value?.workingDirectory ?? '', access)
    await refetch()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

// ───────── 工作目录权限档位:锁图标与配色 ─────────
// 只读=正文色(深色主题下即白),读写=蓝,完全访问=橙,全部询问=绿

const PERM_STYLE = {
  ReadOnly: { icon: 'visibility', cls: 'perm-ro' },
  ReadWrite: { icon: 'edit', cls: 'perm-rw' },
  FullAccess: { icon: 'key', cls: 'perm-full' },
  Ask: { icon: 'help', cls: 'perm-ask' },
} as const;

const permStyle = computed(() => {
  const key = state.value?.workingDirectoryAccess as keyof typeof PERM_STYLE | undefined;
  return (key && PERM_STYLE[key]) || PERM_STYLE.ReadWrite;
});

/** 工作目录胶囊:可选时开目录选择器;锁定/归档/流式中的禁用态点击改为用资源管理器打开当前目录 */
function onWorkdirPillClick() {
  if (!state.value) return;
  if (!state.value.isWorkDirLocked && !isArchived.value && !state.value.isStreaming) {
    workdirBrowser.value = true;
    return;
  }
  if (state.value.workingDirectory)
    void openWorkdirFolder(agentId.value, props.sessionId);
}

// ───────── 发送 / 停止 ─────────

async function runCommand(text: string) {
  try {
    // /summary 与桌面端一致:摘要内容可能很长,用模态框展示(doSummary 内部拉取并打开)
    if (text.trim().toLowerCase().startsWith('/summary')) {
      await doSummary()
      return
    }
    // 命令会往消息流尾部追加反馈行:refetch 前先记录是否钉底,避免"追加后距离>160px"导致软滚动失效
    const sc = listEl.value
    const wasAtBottom = sc ? sc.scrollHeight - sc.scrollTop - sc.clientHeight < 160 : true
    const r = await executeCommand(agentId.value, props.sessionId, text)
    if (!r.handled) {
      await sendText(text)
      return
    }
    await applyCommandResult(text, r, wasAtBottom)
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

/** 命令结果统一收尾:空闲态 runCommand 与运行期排队命令的 command_executed 帧共用——
 * 摘要走模态框、回填输入/新会话跳转/toast,随后刷新快照并按钉底滚动 */
async function applyCommandResult(text: string, r: CommandResult, scrollPinned: boolean) {
  if (text.trim().toLowerCase().startsWith('/summary')) {
    await doSummary() // 排队触发时后端已执行过,反馈即摘要全文:仍按 runCommand 习惯开模态,不 toast
    return
  }
  if (r.restored) input.value = r.restored
  if (r.newSessionId) location.hash = `#/s/${r.newSessionId}`
  else if (r.forkedId) location.hash = `#/s/${r.forkedId}`
  // $ 插件指令的反馈后端已写入会话状态行(refetch 后上屏),不再重复 toast;其余命令反馈用 toast
  if (r.feedback && !text.trim().startsWith('$')) showToast(r.feedback)
  await refetch()
  scrollBottom(scrollPinned)
}

// ───────── 中途消息队列(输入框上方;运行中发送的插话在此排队) ─────────

const queueItems = computed(() => state.value?.queuedMessages ?? [])

/** 命令样文本(与后端 QueuedUserMessage.IsCommandLike 同规则):队列条打 terminal 徽标,
 * 消费时按命令触发而非拼进 LLM 上下文 */
const isCmdText = (t: string) => {
  const s = (t ?? '').trimStart()
  return s[0] === '/' || s[0] === '$'
}

/** 切换"本轮后发送"标记:就地更新按钮态(即时反馈),后端 QueueChanged 广播的全量快照随后覆盖同值 */
async function toggleDefer(q: QueuedItem) {
  try {
    const next = !q.defer
    await setQueueDefer(agentId.value, props.sessionId, q.id, next)
    const cur = state.value?.queuedMessages.find((x) => x.id === q.id)
    if (cur) cur.defer = next
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

// ───────── 队列排序:行首把手拖拽调整 FIFO 顺序(目标按"移除自身后的插入位"回传) ─────────

const qDragId = ref('')
const qOverId = ref('')
const qOverAfter = ref(false)

function startQueueDrag(q: QueuedItem, e: DragEvent) {
  qDragId.value = q.id
  if (e.dataTransfer) {
    e.dataTransfer.setData('text/plain', q.id)
    e.dataTransfer.effectAllowed = 'move'
  }
}

function endQueueDrag() {
  qDragId.value = ''
  qOverId.value = ''
  qOverAfter.value = false
}

/** 拖经某行:记录落点(上半 = 插到该行前,下半 = 插到该行后,按行高中线判定) */
function overQueueItem(q: QueuedItem, e: DragEvent) {
  if (!qDragId.value) return
  e.preventDefault()
  if (e.dataTransfer) e.dataTransfer.dropEffect = 'move'
  if (q.id === qDragId.value) {
    qOverId.value = ''
    return
  }
  const el = e.currentTarget as HTMLElement
  const rect = el.getBoundingClientRect()
  qOverId.value = q.id
  qOverAfter.value = e.clientY > rect.top + rect.height / 2
}

async function dropQueueItem(q: QueuedItem, e: DragEvent) {
  e.preventDefault()
  const src = qDragId.value
  const after = qOverAfter.value
  endQueueDrag()
  if (!src || src === q.id) return
  const list = state.value?.queuedMessages
  if (!list) return
  const s = list.findIndex((x) => x.id === src)
  const t = list.findIndex((x) => x.id === q.id)
  if (s < 0 || t < 0) return
  // 插入位 = 移除自身后的坐标:源在目标前时,移除会让目标下标左移一位
  const insert = after ? (s < t ? t : t + 1) : s < t ? t - 1 : t
  // 本地先移动(即时反馈);后端 QueueChanged 广播的全量快照随后覆盖同序
  const [item] = list.splice(s, 1)
  list.splice(Math.max(0, Math.min(insert, list.length)), 0, item)
  try {
    await moveQueueMessage(agentId.value, props.sessionId, src, Math.max(0, insert))
  } catch (err) {
    showErr(String((err as Error).message))
  }
}

/** 队列插话气泡上屏:镜像后端锚点规则(组装期消费→最后一条用户气泡前;开跑后→当前助手气泡定格之后,
 * 后续步骤由下一次流式增量长出新气泡;自动派发→追加到尾部)。按 clientId 去重,防任何来源的重复插入。 */
function insertQueuedItems(mode: string, items: Message[]) {
  const s = state.value
  if (!s || !items?.length) return
  const fresh = items.filter((it) => !it.clientId || !s.messages.some((m) => m.clientId === it.clientId))
  if (!fresh.length) return
  let idx = s.messages.length
  if (mode === 'beforeLastUser') {
    for (let i = s.messages.length - 1; i >= 0; i--)
      if (s.messages[i].role === 'user') { idx = i; break }
  } else if (mode === 'afterLastAssistant') {
    for (let i = s.messages.length - 1; i >= 0; i--)
      if (s.messages[i].role === 'assistant') { idx = i + 1; break }
    if (idx > 0 && s.messages[idx - 1].role === 'assistant') {
      endThinking(s.messages[idx - 1]) // 定格气泡:仍在转的思考计时就地收口(后端已翻页)
    }
  }
  s.messages.splice(idx, 0, ...fresh.map(withLocalKey))
}

/** 取消:取出即移除,返回内容直接丢弃;队列条经 queue_changed 载荷就地刷新 */
async function cancelQueue(id: string) {
  try {
    await takeQueueMessage(agentId.value, props.sessionId, id)
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

/** 重新编辑:取出即移除,原文与附件(图片/文本文件)回填输入框;再次发送时回到队尾 */
async function reeditQueue(id: string) {
  try {
    const item = await takeQueueMessage(agentId.value, props.sessionId, id)
    input.value = item.text
    for (const dataUrl of item.images ?? []) {
      const m = /^data:image\/([\w+-]+);/.exec(dataUrl)
      const ext = m ? (m[1] === 'jpeg' ? 'jpg' : m[1]) : 'png'
      attachments.value.push({
        kind: 'image', name: `图片.${ext}`, dataUrl,
        sizeText: formatSize(Math.round(dataUrl.length * 0.75)),
      })
    }
    for (const f of item.textFiles ?? [])
      attachments.value.push({
        kind: 'text', name: f.name, content: f.content,
        sizeText: formatSize(new TextEncoder().encode(f.content).length),
      })
    nextTick(() => { inputEl.value?.focus(); autoGrow() })
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function sendText(text: string, defer = false) {
  if (!state.value) return
  const pending = attachments.value
  try {
    // 图片与媒体(PDF/音视频)都以 data URL 走多模态通道,后端按 mime 决定 image_url / file 片段
    const res = await sendMessage(agentId.value, props.sessionId, text, {
      images: pending.filter((a) => a.dataUrl).map((a) => a.dataUrl!),
      textFiles: pending.filter((a) => a.kind === 'text').map((a) => ({ name: a.name, content: a.content! })),
    }, defer)
    attachments.value = []
    drafts.delete(props.sessionId)
    const s = state.value
    if (res.queued) {
      // 服务端判定入队(会话运行中):不推本地气泡(消费时由 queue_changed 插入),运行态以服务端为准自愈
      s.isStreaming = true
      return
    }
    s.messages.push(withLocalKey({
      clientId: null, role: 'user', text, timeText: new Date().toTimeString().slice(0, 8),
      isError: false, imageFiles: [],
      textFiles: pending.filter((a) => a.kind === 'text').length
        ? pending.filter((a) => a.kind === 'text').map((a) => ({ name: a.name, sizeText: a.sizeText }))
        : null,
      steps: null,
      clientDataUrls: pending.filter((a) => a.kind === 'image').map((a) => a.dataUrl!),
    }))
    // 助手占位不再本地预推:聊天流等首个流式增量到达才长出气泡;
    // 运行中的可见指示只保留输入框上下文圆盘左侧的旋转齿轮(按 isStreaming 显隐)
    s.isStreaming = true
    scrollBottom(true)
  } catch (e) {
    // 失败路径服务端从未收下这条消息(404/403/400 在写入前返回,网络失败根本没到服务端),
    // 正文已被 send() 清空且不在草稿里——回填输入框,否则一次瞬时失败即永久丢输入
    if (!input.value) {
      input.value = text
      autoGrow()
      drafts.set(props.sessionId, text)
    }
    showErr(String((e as Error).message))
  }
}

async function send() {
  const text = input.value.trim()
  // 只发图片/附件时允许正文为空
  if (!text && !attachments.value.length) return
  input.value = ''
  autoGrow()
  if (state.value?.isStreaming) {
    // 运行中点击发送 = 加入中途消息队列(不解析斜杠命令:命令需空闲执行)
    await sendText(text)
    return
  }
  // 与桌面端一致:首个字符是 / 或 $ 且无附件时按斜杠命令处理(/summary /compact 等),
  // 命令不可用(未匹配完整命令名)时 runCommand 内部回落到普通消息
  if ((text[0] === '/' || text[0] === '$') && !attachments.value.length) {
    await runCommand(text)
    return
  }
  await sendText(text)
}

async function stop() {
  try {
    await stopSession(agentId.value, props.sessionId)
    // 中断不产生 complete/turn_ended/error 帧,思考步的 isActive 不会被收口,头部 shimmer
    // 会一直闪(实测)。这里手动收口 + 拉快照对齐(「已中断」分割线随快照上屏)。
    const s = state.value
    const m = s?.messages[s.messages.length - 1]
    if (m && m.role === 'assistant') endThinking(m)
    endLlmRun()
    await refetch()
    scrollBottom()
  } catch (e) {
    showErr(String(e))
  }
}

// ───────── 侧边栏(任务清单/后台任务/直播) ─────────

// 开合态用模块级单例:会话切换重挂载后保持,不再每次切会话自动关上
const sidebarOpen = sidebarOpenShared

// ───────── 上下文圆环(占用/上限,口径同桌面端 ContextUsageText) ─────────

const ctxUsed = computed(() => state.value?.lastPromptTokens ?? 0)

/** 背景图缩放策略 → CSS(同 WPF Stretch 语义) */
const bgSize = computed(() =>
  ({ Fill: '100% 100%', Uniform: 'contain', UniformToFill: 'cover', None: 'auto' } as Record<string, string>)[
    state.value?.chatBgStretch ?? 'UniformToFill'
  ] ?? 'cover')

/** 窗体背景图缩放(同上;铺整个聊天窗底层) */
const winBgSize = computed(() =>
  ({ Fill: '100% 100%', Uniform: 'contain', UniformToFill: 'cover', None: 'auto' } as Record<string, string>)[
    state.value?.windowBgStretch ?? 'UniformToFill'
  ] ?? 'cover')
const ctxRatio = computed(() => {
  const s = state.value
  if (!s || !s.contextLimit) return -1
  return Math.min(1, ctxUsed.value / s.contextLimit)
})
const ringColor = computed(() =>
  ctxRatio.value > 0.95 ? 'var(--danger)' : ctxRatio.value > 0.8 ? 'var(--warning)' : 'var(--fg-accent)')
const CIRC = 2 * Math.PI * 15.5

/** token 数缩写(万/亿,同桌面端 ContextUsageText 的显示习惯) */
function fmtTokens(n: number) {
  if (n >= 1e8) return (n / 1e8).toFixed(1).replace(/\.0$/, '') + '亿'
  if (n >= 1e4) return (n / 1e4).toFixed(1).replace(/\.0$/, '') + '万'
  return String(n)
}

// ───────── 左侧会话栏(可收起,同桌面端左栏;点击切会话,不再单独一层) ─────────

const agentTitle = ref('')

/** Agent 头像版本(>0 显示头像图;0 回落首字母) */
const agentAvatarRev = ref(0)

const sessionsOpen = ref(true)
// 侧栏宽度拖动调宽(宽度持久化;上下限防止把主区拖没或窄到不可用)
const leftRz = useResize('apet-left-w', 264, 180, 480, 'left')
const rightRz = useResize('apet-right-w', 300, 240, 600, 'right')
const sessions = ref<SessionListItem[]>([])
// 会话列表尚未拿到任何数据(缓存或拉取):显示微光骨架条,而不是"还没有会话"占位
const listPending = ref(true)
const archivedOpen = archivedOpenShared // 模块级单例:切会话重挂载后归档视图保持打开
// 正在预览的归档被删且没有下一条可切:主区转空(状态栏保留),留在归档页不切走
const archPreviewEmpty = ref(false)
const archived = ref<ArchivedSession[]>([])

/** 归档条目时间:两位年月+日 时分,窄侧栏内比完整 toLocaleString 省宽不失年份 */
const fmtArchTime = (iso: string) =>
  new Date(iso).toLocaleString([], { year: '2-digit', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
let sessionTimer: number | null = null

// Agent 级 SSE:会话列表状态点(绿=运行/黄=压缩/红=出错/蓝=未读)变化即推 sessions_changed,
// 强刷替代干等轮询;与会话流分离——列表状态不只属于当前选中会话,别的会话开跑/收尾也要立刻变色
let sessPushTimer: number | null = null

/** Agent 级 sessions_changed 处理:曾独立订阅 Agent 流,现已并入会话流(withAgent=1,
 *  单连接复用);此处只保留消费逻辑。收尾时 IsStreaming/HasError/Unread 等连发多帧:
 *  合并成一次强刷,避免连环 GET */
function onSessionsChanged() {
  if (sessPushTimer !== null) window.clearTimeout(sessPushTimer)
  sessPushTimer = window.setTimeout(() => {
    sessPushTimer = null
    void (async () => {
      await refreshSessions(true)
      // 一并回拉当前会话快照(窗口增量刷新,很轻):别处归档/移出归档本会话时,
      // 打开的视图随之翻牌——归档 → 只读预览横幅,移出 → 恢复可发送,不再停留旧状态
      await refetch()
    })()
  }, 150)
}

/** force = 列表发生真实变更(置顶/重命名/归档/删除/新建)后必须重拉;
 *  常规挂载/轮询走新鲜度守卫:4 秒内拉过就不重复拉,切换会话不再每次都重载列表 */
let listSeq = 0 // 并发请求序号:只允许最新一次请求的结果落表,旧响应作废(防乱序覆盖出幽灵行)

/** 上报"正在看"本会话(last-session):后端据此判未读,并清本会话的未读/出错点。
 * 成功后本地立刻熄点 —— 不赌 sessions_changed 推送(常在 EventSource 握手完成前发出而丢失);
 * 行还没进列表时由 refreshSessions 里的 healViewedDot 兜底。 */
function reportViewing() {
  const sid = props.sessionId
  if (!agentId.value || !sid) return
  reportLastSession(agentId.value, sid)
    .then(() => extinguishViewed(sid))
    .catch(() => {})
}

/** 本地熄掉列表里本会话的未读/出错点(列表即模块缓存的同一数组,改了即刻对后续挂载生效) */
function extinguishViewed(sid: string) {
  const row = sessions.value.find((x) => x.sessionId === sid)
  if (row && (row.unread || row.hasError)) {
    row.unread = false
    row.hasError = false
  }
}

/** 自愈:正在看的会话绝不允许带点 —— 列表里发现本会话脏(回复收完置位晚于上报、
 * 视图复用漏报等时序)就本地熄 + 再上报一次清服务端(端点同 id 重复上报也清)。 */
function healViewedDot() {
  if (!agentId.value) return
  const sid = props.sessionId
  const row = sessions.value.find((x) => x.sessionId === sid)
  if (row && (row.unread || row.hasError)) {
    row.unread = false
    row.hasError = false
    reportLastSession(agentId.value, sid).catch(() => {})
  }
}

// 实例复用重新上屏(过渡期切回 dying 实例直接转正、后台预备转正)不重挂载,onMounted 不再跑:
// 这里补报"正在看",否则 LastSessionId 停在旧会话 —— 看着本会话跑完误亮蓝点、点开别的会话蓝点不清
watch(
  () => props.background,
  (bg) => {
    if (bg === false) reportViewing()
  },
)

async function refreshSessions(force = false) {
  if (!agentId.value) return
  healViewedDot() // 已展示的列表里本会话带点:先熄再谈刷新
  // 重挂载后列表是空的:先回填模块缓存(即使下面的新鲜度守卫拦截了本次拉取,列表也不空白)
  if (sessions.value.length === 0) {
    const ss = cachedSessions(agentId.value)
    if (ss) {
      sessions.value = ss
      listPending.value = false
      markListShown(agentId.value, ss)
      healViewedDot() // 缓存可能带着置位后的脏点
    }
  }
  // 归档视图开着时归档列表同理回填:否则新鲜度守卫拦掉本次拉取后,重挂载视图的归档列表一直空白
  if (archivedOpen.value && archived.value.length === 0) {
    const ca = cachedArchived(agentId.value)
    if (ca) archived.value = ca
  }
  // 文件夹树同例回填(树很轻,不单独设新鲜度守卫)
  if (folders.value.length === 0) {
    const cf = cachedFolders(agentId.value)
    if (cf) folders.value = cf
  }
  if (!force && sessionsFresh(agentId.value, 4000)) return
  const seq = ++listSeq
  try {
    const list = await getSessions(agentId.value)
    if (seq !== listSeq) return // 已有更新的请求在途:迟到的旧快照作废,防乱序覆盖(轮询/推送/显式刷新会并发)
    // 内容没变就不替换:避免无谓的 DOM 重排造成的列表闪烁
    if (JSON.stringify(list) !== JSON.stringify(sessions.value)) {
      sessions.value = list
      // 列表整体换新对象后,右键菜单不再跨刷新存活:行数据/顺序可能已变,留着会让人归档到"看起来不是那条"
      closeSessionMenu()
    }
    healViewedDot() // 新列表里本会话仍带点(置位晚于上报的赛跑):熄点并回报清服务端
    storeSessions(agentId.value, list)
    listPending.value = false
    markListShown(agentId.value, list)
    storeOwners(agentId.value, list.map((s) => s.sessionId)) // 列表中会话归属已知:切换它们免发 owner 解析请求
    // 文件夹树随列表一并回拉(拖拽/建夹/删夹等布局变更后其他窗口也靠 sessions_changed → 本刷新同步)
    const fl = await getFolders(agentId.value)
    if (seq === listSeq) {
      folders.value = fl
      storeFolders(agentId.value, fl)
    }
    if (archivedOpen.value) {
      archived.value = await getArchived(agentId.value)
      storeArchived(agentId.value, archived.value) // 留一份模块缓存:切会话重挂载先渲染,不等拉取
    }
  } catch {
    // 轮询容错:瞬时失败下次再取
  }
}

const pinnedSessions = computed(() => sessions.value.filter((x) => x.pinned))
const normalSessions = computed(() => sessions.value.filter((x) => !x.pinned))

// ───────── 文件夹树 + 组内排序(拖拽) ─────────
// 布局 = 已置顶(独立区) → 根组 → 递归(会话与文件夹按槽位序号任意交错,文件夹行可展开/收起)。
// 会话与文件夹共享同一张槽位表(后端 session_slots),组内顺序 = 槽位序号,
// 因此文件夹能拖到组内任意位置,不再"文件夹块在前、会话块在后"。
const folders = ref<FolderInfo[]>([])
// 被折叠的文件夹 id 集(未出现的 = 展开;新文件夹默认展开)。实例级 reactive 驱动渲染,
// 写穿模块级 collapsedFoldersShared(切会话重挂载时回填,见 onMounted)
// + 后端(Agent 配置文件,跨重启恢复)
const collapsedFolders = reactive(new Set<string>())
function toggleFolderCollapse(id: string) {
  if (collapsedFolders.has(id)) collapsedFolders.delete(id)
  else collapsedFolders.add(id)
  if (!agentId.value) return
  if (!collapsedFoldersShared.has(agentId.value)) collapsedFoldersShared.set(agentId.value, new Set())
  const saved = collapsedFoldersShared.get(agentId.value)!
  if (collapsedFolders.has(id)) saved.add(id)
  else saved.delete(id)
  // 折叠态是低频操作,写穿即可(不阻塞交互;失败静默,下次切换再报)
  setCollapsedFolders(agentId.value, [...saved]).catch(() => {})
}

/** 文件夹按父级分组(同级按 sort)。 */
const folderGroups = computed(() => {
  const m = new Map<string | null, FolderInfo[]>()
  for (const f of folders.value) {
    const k = f.parentId ?? null
    if (!m.has(k)) m.set(k, [])
    m.get(k)!.push(f)
  }
  for (const list of m.values()) list.sort((a, b) => a.sort - b.sort)
  return m
})

/** 某组(含递归子级)的会话总数(文件夹行尾计数)。 */
function rowCountOf(folderId: string, groups: Map<string | null, FolderInfo[]>, seen = new Set<string>()): number {
  if (seen.has(folderId)) return 0 // 防御:环不该存在(建夹/移动都有环校验),兜底防死循环
  seen.add(folderId)
  let n = normalSessions.value.filter((s) => (s.folderId ?? null) === folderId).length
  for (const c of groups.get(folderId) ?? []) n += rowCountOf(c.id, groups, seen)
  return n
}

/** 某组的混合条目(会话+文件夹,按槽位序号;缺槽的排组尾)。渲染与拖拽整序共用同一口径。 */
function scopeItems(parentId: string | null): { id: string; sort: number; s?: SessionListItem; f?: FolderInfo }[] {
  const items: { id: string; sort: number; s?: SessionListItem; f?: FolderInfo }[] = []
  for (const s of normalSessions.value)
    if ((s.folderId ?? null) === parentId) items.push({ id: s.sessionId, sort: s.slotSort < 0 ? Number.MAX_SAFE_INTEGER : s.slotSort, s })
  for (const f of folders.value)
    if ((f.parentId ?? null) === parentId) items.push({ id: f.id, sort: f.sort < 0 ? Number.MAX_SAFE_INTEGER : f.sort, f })
  items.sort((a, b) => a.sort - b.sort || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
  return items
}

/** 会话树(递归渲染,见 SidebarTree):根组 → 顶级文件夹(递归:组内会话 + 子文件夹)。
 * 文件夹节点的 children 恒构建(收起时由 .kids 的 CSS 隐藏),
 * 展开/收起 = 子块容器 grid-template-rows 0fr↔1fr 的原生过渡,整块同节奏,天然平滑。 */
const treeRows = computed<TreeRow[]>(() => {
  const groups = folderGroups.value
  const walk = (parentId: string | null, depth: number): TreeRow[] => {
    const out: TreeRow[] = []
    for (const it of scopeItems(parentId)) {
      if (it.f) {
        out.push({
          key: 'f' + it.f.id,
          kind: 'folder',
          depth,
          f: it.f,
          count: rowCountOf(it.f.id, groups),
          children: walk(it.f.id, depth + 1),
        })
      } else {
        out.push({ key: 's' + it.s!.sessionId, kind: 'session', depth, s: it.s! })
      }
    }
    return out
  }
  return walk(null, 0)
})

/** 顶级文件夹(会话右键菜单"移入…"用,按 sort)。 */
const topFolders = computed(() => (folderGroups.value.get(null) ?? []))

// ───────── 拖拽(原生 HTML5 DnD):会话行/文件夹行均可拖 ─────────
// 落点语义:会话行的上/下半区 = 插到该行前/后,组内按"会话+文件夹"混合槽位序重排
// (同组纯重排 / 跨组先移归属再调位置);拖到文件夹行 = 上 1/4 插到该夹前(同级),收起的夹另有
// 下 1/4 插到该夹后,展开的夹下半区归子项(其"插后"实际落在整个子树之后,指示误导故不设),
// 其余区域 = 移入/嵌套进该夹(整行高亮);文件夹拖文件夹禁自身与后代(成环)。列表空白区 = 回根组。
// 松手后整序提交受影响的 1~2 个组,再强刷(本地往返毫秒级)。
const dragKey = ref<string | null>(null)
const dragItem = ref<{ kind: 'session' | 'folder'; id: string } | null>(null)
const dropHint = ref<string | null>(null) // `${rowKey}:b`(前) / `:a`(后) / `:i`(移入/嵌套)
const rootDrop = ref(false) // 指针悬在容器自身空白区(列表底部等):列表最底部显示"回根组"指示线

function onDragStart(row: TreeRow, e: DragEvent) {
  dragKey.value = row.key
  dragItem.value = row.kind === 'session' ? { kind: 'session', id: row.s!.sessionId } : { kind: 'folder', id: row.f!.id }
  if (e.dataTransfer) {
    e.dataTransfer.setData('text/plain', row.key)
    e.dataTransfer.effectAllowed = 'move'
  }
}

function onDragEnd() {
  dragKey.value = null
  dragItem.value = null
  dropHint.value = null
  rootDrop.value = false
}

/** 文件夹 item 是否为 target 自身或 target 的祖先(拖到自身/子文件夹 = 成环,禁止)。 */
function isSelfOrAncestor(itemId: string, targetFolderId: string): boolean {
  if (itemId === targetFolderId) return true
  const byId = new Map(folders.value.map((f) => [f.id, f]))
  let cur = byId.get(targetFolderId)?.parentId ?? null
  while (cur) {
    if (cur === itemId) return true
    cur = byId.get(cur)?.parentId ?? null
  }
  return false
}

function onDragOverRow(row: TreeRow, e: DragEvent) {
  const item = dragItem.value
  if (!item) return
  // 文件夹拖到自身/后代 = 成环:文件夹行(嵌套)与会话行(移入该会话所在组,组可能是后代)都禁
  if (item.kind === 'folder' && row.kind === 'folder' && isSelfOrAncestor(item.id, row.f!.id)) {
    dropHint.value = null
    if (e.dataTransfer) e.dataTransfer.dropEffect = 'none'
    return
  }
  // 会话所在组是拖拽夹的严格后代才成环;所在组就是拖拽夹本身 = 同组重排,合法
  if (item.kind === 'folder' && row.kind === 'session' && row.s!.folderId && row.s!.folderId !== item.id && isSelfOrAncestor(item.id, row.s!.folderId)) {
    dropHint.value = null
    if (e.dataTransfer) e.dataTransfer.dropEffect = 'none'
    return
  }
  if (e.dataTransfer) e.dataTransfer.dropEffect = 'move'
  const r = (e.currentTarget as HTMLElement).getBoundingClientRect()
  const frac = r.height > 0 ? (e.clientY - r.top) / r.height : 0.5
  const expanded = row.kind === 'folder' && !collapsedFolders.has(row.f!.id)
  dropHint.value = row.key + ':' + dropMode(item.kind, row.kind, frac, expanded)
}

/** 容器级 dragover(行上的事件也会冒泡到这里):指针压在行/子块上 = 行处理器负责指示,
 * 顺手清掉空区指示;指针压在容器自身背景且位于可见内容底缘之下 = 真·底部空区,
 * 显示"回根组"指示线。仅判 target=容器不够:根组行是容器直接子元素,行间距/左右行距
 * 露出的也是容器背景,列表中间的空隙同样会命中,必须再按纵向位置排除。 */
function onDragOverRoot(e: DragEvent) {
  if (e.target !== e.currentTarget) {
    rootDrop.value = false
    return
  }
  const c = e.currentTarget as HTMLElement
  const rect = c.getBoundingClientRect()
  // 可见内容底缘:内容短于视口 = 内容实际底部;长于视口 = 视口底(下方还有内容,不存在空区)
  const contentEndY = rect.top + Math.min(c.scrollHeight - c.scrollTop, c.clientHeight)
  rootDrop.value = dragItem.value !== null && e.clientY > contentEndY
  dropHint.value = null
  if (e.dataTransfer) e.dataTransfer.dropEffect = dragItem.value ? 'move' : 'none'
}

/** 落点模式:`b`=插该行前 / `a`=插该行后 / `i`=整行高亮。
 * 文件夹行(拖会话或拖文件夹同规则)= 上 1/4 插到该夹前(同级定位),收起的夹 = 下 1/4 插后,
 * 其余区域 = 移入/嵌套;展开的夹只留上 1/4 插前,其余(含下半区)一律移入——下半区视觉上是
 * 子项的地盘,"插到该夹后"实际落在整个子树之后,指示在那儿既误导又没人想要。
 * 会话行 = 上/下半区。 */
function dropMode(_itemKind: 'session' | 'folder', rowKind: 'session' | 'folder', frac: number, targetFolderExpanded = false): 'b' | 'a' | 'i' {
  if (rowKind === 'folder') {
    if (frac < 0.25) return 'b'
    if (!targetFolderExpanded && frac > 0.75) return 'a'
    return 'i'
  }
  return frac < 0.5 ? 'b' : 'a'
}

async function persistAndRefresh() {
  await refreshSessions(true)
}

/** 组内混合顺序(会话+文件夹,按槽位序号)的 id 列表——拖拽落点整序提交的基准。
 * 跨组移动时本地快照里被拖项还挂在源组,目标组列表天然不含它,手动插到落点即可。 */
function scopeItemIds(folderId: string | null): string[] {
  return scopeItems(folderId).map((x) => x.id)
}

/** anchor 在 order 中的插入下标(前/后);anchor 不在列表则退化 append 末尾 */
function insertIdx(order: string[], anchorId: string, before: boolean): number {
  const i = order.indexOf(anchorId)
  if (i < 0) return order.length
  return before ? i : i + 1
}

async function onDropOnRow(row: TreeRow, e: DragEvent) {
  const item = dragItem.value
  onDragEnd()
  if (!item || !agentId.value) return
  e.stopPropagation()
  const r = (e.currentTarget as HTMLElement).getBoundingClientRect()
  const frac = r.height > 0 ? (e.clientY - r.top) / r.height : 0.5
  const expanded = row.kind === 'folder' && !collapsedFolders.has(row.f!.id)
  const mode = dropMode(item.kind, row.kind, frac, expanded)
  const before = mode === 'b'
  try {
    if (item.kind === 'session') {
      if (item.id === props.sessionId && row.kind === 'session' && row.s!.sessionId === item.id) return
      if (row.kind === 'folder' && mode === 'i') {
        await moveSessionToFolder(agentId.value, item.id, row.f!.id) // 文件夹行中间 = 移入该夹末尾
      } else {
        // 定位插入:锚点 = 目标行自身 id,目标组 = 目标行所在组
        // (会话行 = 它所在的夹;文件夹行 = 它的父组,即插到该夹前/后)
        const anchorId = row.kind === 'session' ? row.s!.sessionId : row.f!.id
        const toFolder = row.kind === 'session' ? (row.s!.folderId ?? null) : (row.f!.parentId ?? null)
        const fromFolder = currentFolderOf(item.id)
        if (fromFolder === toFolder) {
          const order = scopeItemIds(toFolder).filter((id) => id !== item.id)
          order.splice(insertIdx(order, anchorId, before), 0, item.id)
          await reorderSessions(agentId.value, toFolder, order)
        } else {
          // 跨组:只重排不移动的话,服务端整序会忽略不在该组的 self,会话就"拖不动"
          await moveSessionToFolder(agentId.value, item.id, toFolder) // append 目标组末尾
          const toOrder = scopeItemIds(toFolder)
          toOrder.splice(insertIdx(toOrder, anchorId, before), 0, item.id)
          const fromOrder = scopeItemIds(fromFolder).filter((id) => id !== item.id)
          await Promise.all([
            reorderSessions(agentId.value, fromFolder, fromOrder),
            reorderSessions(agentId.value, toFolder, toOrder),
          ])
        }
      }
    } else if (row.kind === 'session') {
      // 文件夹拖到会话行 = 插到该会话前/后(所在组末尾之外也可精确定位);
      // 同组 = 纯重排,跨组 = 先移归属再调位置
      const toFolder = row.s!.folderId ?? null
      const fromFolder = folderParentOf(item.id)
      if (fromFolder !== toFolder) await moveFolder(agentId.value, item.id, toFolder)
      const toOrder = scopeItemIds(toFolder).filter((id) => id !== item.id)
      toOrder.splice(insertIdx(toOrder, row.s!.sessionId, before), 0, item.id)
      if (fromFolder === toFolder) {
        await reorderSessions(agentId.value, toFolder, toOrder)
      } else {
        const fromOrder = scopeItemIds(fromFolder).filter((id) => id !== item.id)
        await Promise.all([
          reorderSessions(agentId.value, fromFolder, fromOrder),
          reorderSessions(agentId.value, toFolder, toOrder),
        ])
      }
    } else if (mode === 'i') {
      await moveFolder(agentId.value, item.id, row.f!.id) // 嵌套(环已在 dragover 拦截)
    } else {
      // 插到目标文件夹前/后 = 在其父组内定位插入(同组 = 纯重排,跨组 = 先移归属再调位置)
      const toFolder = row.f!.parentId ?? null
      const fromFolder = folderParentOf(item.id)
      if (fromFolder !== toFolder) await moveFolder(agentId.value, item.id, toFolder)
      const toOrder = scopeItemIds(toFolder).filter((id) => id !== item.id)
      toOrder.splice(insertIdx(toOrder, row.f!.id, before), 0, item.id)
      if (fromFolder === toFolder) {
        await reorderSessions(agentId.value, toFolder, toOrder)
      } else {
        const fromOrder = scopeItemIds(fromFolder).filter((id) => id !== item.id)
        await Promise.all([
          reorderSessions(agentId.value, fromFolder, fromOrder),
          reorderSessions(agentId.value, toFolder, toOrder),
        ])
      }
    }
    await persistAndRefresh()
  } catch (err) {
    showErr(String((err as Error).message))
  }
}

/** 拖到列表空白区 = 回根组(会话 append 根组末尾 / 文件夹移到顶级)。 */
async function onDropRoot(e: DragEvent) {
  const item = dragItem.value
  onDragEnd() // 连同清空回根指示线
  if (!item || !agentId.value) return
  try {
    if (item.kind === 'session') await moveSessionToFolder(agentId.value, item.id, null)
    else await moveFolder(agentId.value, item.id, null)
    await persistAndRefresh()
  } catch (err) {
    showErr(String((err as Error).message))
  }
}

function currentFolderOf(sessionId: string): string | null {
  return sessions.value.find((x) => x.sessionId === sessionId)?.folderId ?? null
}

/** 文件夹所在组(null = 顶级)。 */
function folderParentOf(folderId: string): string | null {
  return folders.value.find((f) => f.id === folderId)?.parentId ?? null
}

// ───────── 文件夹管理(底部栏新建 + 右键菜单) ─────────

async function doCreateFolder() {
  const name = await ask({ title: '新建文件夹', input: true, message: '输入文件夹名称(建在顶级)。' })
  if (typeof name !== 'string' || !name.trim()) return
  try {
    await createFolder(agentId.value, name.trim(), null)
    await persistAndRefresh()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

const folderMenu = ref<{ x: number; y: number; f: FolderInfo } | null>(null)

function openFolderMenu(f: FolderInfo, e: MouseEvent) {
  closeSessionMenu() // 两类菜单互斥:开一个关另一个
  const z = zoomFactor()
  const x = Math.min(e.clientX, window.innerWidth - 170 * z - 8)
  const y = Math.min(e.clientY, window.innerHeight - 250 * z - 8)
  colorSub.value = false
  cancelColorSubClose()
  folderMenu.value = { x: x / z, y: y / z, f }
  document.addEventListener('pointerdown', closeFolderMenu)
}

function closeFolderMenu(e?: PointerEvent) {
  if (e && (e.target as HTMLElement).closest('.f-menu')) return
  folderMenu.value = null
  colorSub.value = false
  cancelColorSubClose()
  document.removeEventListener('pointerdown', closeFolderMenu)
}

/** "修改颜色"子菜单:与"移入"同款的悬停展开 + 250ms 宽限 */
const colorSub = ref(false)
let colorSubTimer: number | undefined
function cancelColorSubClose() {
  if (colorSubTimer !== undefined) {
    window.clearTimeout(colorSubTimer)
    colorSubTimer = undefined
  }
}
function scheduleColorSubClose() {
  cancelColorSubClose()
  colorSubTimer = window.setTimeout(() => {
    colorSub.value = false
    colorSubTimer = undefined
  }, 250)
}
/** 主菜单已贴屏幕右缘时,颜色子菜单翻到左侧,不溢出 */
const colorSubLeft = computed(() => {
  if (!folderMenu.value) return false
  const z = zoomFactor()
  return (folderMenu.value.x + 330) * z > window.innerWidth
})

/** 在该文件夹里新建对话(LLM 配置跟随当前会话,同底部栏新建) */
async function doFolderNewSession() {
  const f = folderMenu.value?.f
  closeFolderMenu()
  if (!f) return
  try {
    const s = await createSession(agentId.value, props.sessionId, f.id)
    await refreshSessions(true)
    location.hash = `#/s/${s.sessionId}`
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

/** 选预设色:color = 色板 id,空串 = 恢复默认 */
async function doFolderSetColor(color: string) {
  const f = folderMenu.value?.f
  closeFolderMenu()
  if (!f) return
  try {
    await setFolderColor(agentId.value, f.id, color)
    await persistAndRefresh()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function doFolderNewChild() {
  const f = folderMenu.value?.f
  closeFolderMenu()
  if (!f) return
  const name = await ask({ title: `新建子文件夹(在「${f.name}」里)`, input: true })
  if (typeof name !== 'string' || !name.trim()) return
  try {
    await createFolder(agentId.value, name.trim(), f.id)
    await persistAndRefresh()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function doFolderRename() {
  const f = folderMenu.value?.f
  closeFolderMenu()
  if (!f) return
  const name = await ask({ title: '重命名文件夹', input: true, initialValue: f.name })
  if (typeof name !== 'string' || !name.trim()) return
  try {
    await renameFolder(agentId.value, f.id, name.trim())
    await persistAndRefresh()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function doFolderToRoot() {
  const f = folderMenu.value?.f
  closeFolderMenu()
  if (!f || !f.parentId) return
  try {
    await moveFolder(agentId.value, f.id, null)
    await persistAndRefresh()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function doFolderDelete() {
  const f = folderMenu.value?.f
  closeFolderMenu()
  if (!f) return
  const ok = await ask({
    title: `删除文件夹「${f.name}」`,
    message: '子文件夹与其中的会话会移到它的上一级,不会丢失。',
    confirmLabel: '删除',
    danger: true,
  })
  if (!ok) return
  try {
    await deleteFolder(agentId.value, f.id)
    // 文件夹没了,它的折叠态也一并清掉(模块级 + 后端;子文件夹的折叠态保留)
    const saved = collapsedFoldersShared.get(agentId.value)
    if (saved?.has(f.id)) {
      saved.delete(f.id)
      setCollapsedFolders(agentId.value, [...saved]).catch(() => {})
    }
    await persistAndRefresh()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

// 会话右键菜单里的"移到…"项(不靠拖拽也能换组)
async function doSessionToRoot(s: SessionListItem) {
  closeSessionMenu()
  try {
    await moveSessionToFolder(agentId.value, s.sessionId, null)
    await persistAndRefresh()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function doSessionToFolder(s: SessionListItem, f: FolderInfo) {
  closeSessionMenu()
  try {
    await moveSessionToFolder(agentId.value, s.sessionId, f.id)
    await persistAndRefresh()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

/** 会话右键菜单(置顶/重命名/分叉/归档/删除) */
const ctxMenu = ref<{ x: number; y: number; s: SessionListItem } | null>(null)

/**
 * 界面缩放下的坐标补偿:documentElement 上的 CSS zoom 会把 fixed 定位的 px
 * 再放大一次,而 clientX/clientY 是可视(物理)像素 —— 落点与边界都要先除回 zoom。
 */
function zoomFactor() {
  const z = Number(getComputedStyle(document.documentElement).zoom)
  return Number.isFinite(z) && z > 0 ? z : 1
}

function openSessionMenu(s: SessionListItem, e: MouseEvent) {
  closeFolderMenu() // 两类菜单互斥:开一个关另一个
  const z = zoomFactor()
  const x = Math.min(e.clientX, window.innerWidth - 170 * z - 8)
  const y = Math.min(e.clientY, window.innerHeight - 280 * z - 8)
  moveSub.value = false
  cancelMoveSubClose()
  ctxMenu.value = { x: x / z, y: y / z, s }
  document.addEventListener('pointerdown', closeSessionMenu)
}

function closeSessionMenu(e?: PointerEvent) {
  if (e && (e.target as HTMLElement).closest('.s-menu')) return
  ctxMenu.value = null
  moveSub.value = false
  cancelMoveSubClose()
  document.removeEventListener('pointerdown', closeSessionMenu)
}

/** "移入"子菜单:悬停展开;离开后留 250ms 宽限,鼠标来得及横穿缝隙到子菜单 */
const moveSub = ref(false)
let moveSubTimer: number | undefined
function cancelMoveSubClose() {
  if (moveSubTimer !== undefined) {
    window.clearTimeout(moveSubTimer)
    moveSubTimer = undefined
  }
}
function scheduleMoveSubClose() {
  cancelMoveSubClose()
  moveSubTimer = window.setTimeout(() => {
    moveSub.value = false
    moveSubTimer = undefined
  }, 250)
}
/** "移入"的目标:顶级文件夹里除去会话当前所在的组 */
const moveTargets = computed(() =>
  ctxMenu.value ? topFolders.value.filter((x) => x.id !== ctxMenu.value!.s.folderId) : [],
)
/** 主菜单已贴屏幕右缘时,子菜单翻到左侧,不溢出 */
const moveSubLeft = computed(() => {
  if (!ctxMenu.value) return false
  const z = zoomFactor()
  return (ctxMenu.value.x + 330) * z > window.innerWidth
})

async function togglePin(s: SessionListItem) {
  closeSessionMenu()
  try {
    await pinSession(agentId.value, s.sessionId, !s.pinned)
    await refreshSessions(true)
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function renameSessionById(s: SessionListItem) {
  closeSessionMenu()
  const title = await ask({ title: '重命名会话', input: true, initialValue: s.title })
  if (typeof title === 'string' && title.trim()) {
    try {
      await renameSession(agentId.value, s.sessionId, title.trim())
      await refreshSessions(true)
      if (s.sessionId === props.sessionId) await refetch()
    } catch (e) {
      showErr(String((e as Error).message))
    }
  }
}

async function forkSessionById(s: SessionListItem) {
  closeSessionMenu()
  try {
    const r = await forkSession(agentId.value, s.sessionId)
    location.hash = `#/s/${r.sessionId}`
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function archiveSessionById(s: SessionListItem) {
  closeSessionMenu()
  try {
    await archiveSession(agentId.value, s.sessionId)
    await refreshSessions(true)
    if (s.sessionId === props.sessionId) showToast('已归档当前对话')
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function deleteSessionById(s: SessionListItem) {
  closeSessionMenu()
  if ((await ask({ title: '删除会话', message: `确定要永久删除「${s.title}」吗?此操作不可恢复。`, confirmLabel: '删除', danger: true })) !== true) return
  try {
    await deleteSession(agentId.value, s.sessionId)
    dropState(s.sessionId)
    await refreshSessions(true)
    if (s.sessionId === props.sessionId) {
      const next = sessions.value[0]
      if (next) location.hash = `#/s/${next.sessionId}`
      else {
        // 最后一个会话被删:直接建新会话进聊天页(不再回落会话列表层)
        const created = await createSession(agentId.value).catch(() => null)
        location.hash = created ? `#/s/${created.sessionId}` : '#/'
      }
    }
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

async function toggleArchivedList() {
  archivedOpen.value = !archivedOpen.value
  if (!archivedOpen.value && archPreviewEmpty.value) {
    // 从"预览已删空"态返回会话列表:落回最新会话(右侧不能一直空着)
    archPreviewEmpty.value = false
    location.hash = `#/a/${agentId.value}`
  }
  if (archivedOpen.value) await refreshSessions(true)
}

function openSession(id: string) {
  if (id !== props.sessionId) location.hash = `#/s/${id}`
}

const doNewChat = () => withMenu(async () => {
  // 显式传当前会话作为继承源:新对话沿用本会话的供应商/模型/思考等级(后端 WPF 选中态与 Web 不同步)
  const s = await createSession(agentId.value, props.sessionId)
  // 先强制刷列表(绕过 4s 新鲜度守卫)再切走:新视图挂载时缓存里已有新会话,立即可见且被选中
  await refreshSessions(true)
  location.hash = `#/s/${s.sessionId}`
})

const doUnarchive = (s: ArchivedSession) => withMenu(async () => {
  await unarchiveSession(agentId.value, s.id)
  showToast('已移出归档') // 停在归档面板原地刷新:不改路由不切会话(改 hash 会让 ChatView 重挂、归档面板被重置)
  await refreshSessions(true) // 归档面板开着时一并重拉 archived:该条从归档列表消失,会话列表补上
})

const doDeleteArchived = (s: ArchivedSession) => withMenu(async () => {
  if ((await ask({ title: '删除会话', message: `确定要永久删除「${s.title}」吗?此操作不可恢复。`, confirmLabel: '删除', danger: true })) !== true) return
  const prevIdx = archived.value.findIndex((x) => x.id === s.id)
  await deleteSession(agentId.value, s.id)
  dropState(s.id)
  await refreshSessions(true)
  if (s.id !== props.sessionId) return
  // 删的是正在预览的归档:切到删除后落在原位置的那条(原位置超出末尾则取最后一条);
  // 归档已清空则右侧转空、留在归档页
  const list = archived.value
  if (list.length) {
    const next = list[Math.min(Math.max(prevIdx, 0), list.length - 1)]
    location.hash = `#/s/${next.id}`
  } else {
    archPreviewEmpty.value = true
  }
})

const doClearArchived = () => withMenu(async () => {
  const n = archived.value.length
  if (n === 0) { showToast('没有可删除的归档会话'); return }
  if ((await ask({ title: '删除所有归档', message: `确定要永久删除全部 ${n} 个归档会话吗?此操作不可恢复。`, confirmLabel: '全部删除', danger: true })) !== true) return
  const deletedCurrent = props.sessionId != null && archived.value.some((s) => s.id === props.sessionId)
  await clearArchived(agentId.value)
  archived.value = [] // 本地先行清空,避免刷新空档闪回旧条目
  showToast(`已删除 ${n} 个归档会话`)
  await refreshSessions(true) // 以服务端为准对齐(归档面板开着时一并重拉 archived)
  // 右侧正在预览的会话也在被清之列:右侧转空、留在归档页
  if (deletedCurrent) archPreviewEmpty.value = true
})

onMounted(() => {
  sessionTimer = window.setInterval(refreshSessions, 5000)
  // 会话流不在此开:此刻 agentId 还是空串(归属异步解析中),订阅 URL 无效;
  // 它在 onMounted(async) 解析出 owner 后开启(withAgent=1 已并入 Agent 级事件)
})

onUnmounted(() => {
  if (sessionTimer !== null) window.clearInterval(sessionTimer)
  if (sessPushTimer !== null) {
    window.clearTimeout(sessPushTimer)
    sessPushTimer = null
  }
  document.removeEventListener('pointerdown', closeModelMenu, true)
  document.removeEventListener('pointerdown', closeSessionMenu)
  document.removeEventListener('pointerdown', closeFolderMenu) // 打开着文件夹菜单时卸载:监听随组件移除
  // 离开会话时保存输入草稿(同桌面端每会话 DraftText)
  if (input.value.trim()) drafts.set(props.sessionId, input.value)
  else drafts.delete(props.sessionId)
})

// ───────── 模型级联菜单:当前模型 → 供应商分组(悬停展开) → 管理模型 ─────────

const modelMenuOpen = ref(false)
const hoverProvider = ref('')

function pickModel(provider: string, model: string) {
  modelMenuOpen.value = false
  applyModel({ provider, model })
}

function closeModelMenu(e: PointerEvent) {
  if ((e.target as HTMLElement).closest('.model-menu')) return
  modelMenuOpen.value = false
}

watch(modelMenuOpen, (v) => {
  if (v) document.addEventListener('pointerdown', closeModelMenu, true)
  else document.removeEventListener('pointerdown', closeModelMenu, true)
})

// ───────── 会话操作(⋯ 菜单) ─────────

const menuOpen = ref(false)

async function withMenu(fn: () => Promise<void>) {
  menuOpen.value = false
  try {
    await fn()
  } catch (e) {
    showErr(String((e as Error).message))
  }
}

const doRename = () => withMenu(async () => {
  const title = await ask({ title: '重命名会话', input: true, initialValue: state.value?.title ?? '' })
  if (typeof title === 'string' && title.trim()) {
    await renameSession(agentId.value, props.sessionId, title.trim())
    await refetch()
  }
})

const doFork = () => withMenu(async () => {
  const r = await forkSession(agentId.value, props.sessionId)
  location.hash = `#/s/${r.sessionId}`
})

const doCompact = () => withMenu(async () => {
  showToast('正在压缩上下文…') // 进行中提示走 toast(齿轮行+黄点仍在消息流里表状态),不再挤占布局
  const reason = await compactSession(agentId.value, props.sessionId)
  await refetch()
  // 成功落「会话已压缩」、中断落「压缩已中断」、失败落「压缩失败」分割线(均随快照上屏);仅"无可压缩"用 toast 提示
  if (reason === 'empty') showToast('没有可压缩的旧消息(会话太短或上下文上限未知)')
})

const doUndo = () => withMenu(async () => {
  const restored = await undoSession(agentId.value, props.sessionId)
  if (restored) input.value = restored
  await refetch()
})

// 摘要:模态框内 Markdown 渲染(内容可能很长,不适合横幅/toast)
const summaryOpen = ref(false)
const summaryText = ref('')
const summaryHtml = computed(() => (summaryText.value ? renderMarkdown(summaryText.value) : ''))
const doSummary = () => withMenu(async () => {
  const r = await getSummary(agentId.value, props.sessionId)
  if (r.text) {
    summaryText.value = r.text
    summaryOpen.value = true
  } else {
    showToast('本会话还没有摘要')
  }
})

// 系统提示词:模态框内原样展示(Agent 配置 + 插件片段实时组装,与下一轮请求一致);纯文本 pre 保留排版;
// 下方列当前启用的工具及原始描述(与请求的 tools 字段一致),便于评估 Agent 当前工作能力
const spOpen = ref(false)
const spText = ref('')
const spTools = ref<{ name: string; description: string }[]>([])
const doSystemPrompt = () => withMenu(async () => {
  const r = await getSystemPrompt(agentId.value, props.sessionId)
  spText.value = r.text
  spTools.value = r.tools ?? []
  spOpen.value = true
})

const doArchive = () => withMenu(async () => {
  await archiveSession(agentId.value, props.sessionId)
  showToast('已归档当前对话')
  await refreshSessions(true)
})

const doUnarchiveCurrent = () => withMenu(async () => {
  await unarchiveSession(agentId.value, props.sessionId)
  showToast('已移出归档')
  await refetch() // 摘掉只读标记:快照 isArchived 翻回 false,输入区恢复可用
  await refreshSessions(true) // 会话列表补上该条
})

const doDelete = () => withMenu(async () => {
  if ((await ask({ title: '删除会话', message: '确定要永久删除该对话吗?此操作不可恢复。', confirmLabel: '删除', danger: true })) !== true) return
  await deleteSession(agentId.value, props.sessionId)
  dropState(props.sessionId)
  await refreshSessions(true)
  const next = sessions.value[0]
  if (next) location.hash = `#/s/${next.sessionId}`
  else {
    const created = await createSession(agentId.value).catch(() => null)
    location.hash = created ? `#/s/${created.sessionId}` : '#/'
  }
})

function goSettings(tab?: string) {
  location.hash = `#/a/${agentId.value}/settings${tab ? `?tab=${tab}` : ''}`
}

const theme = ref<ThemeMode>(currentTheme())

// ───────── 当前模型的高级设置(思考等级子集 / 媒体类型),来自设置页每个模型的高级配置 ─────────

const curModel = computed(() => {
  const s = state.value
  if (!s) return null
  return providers.value.find((p) => p.name === s.provider)?.models?.find((m) => m.name === s.model) ?? null
})

/** 思考下拉按模型配置过滤;空 = 全部 7 档;当前值不在清单时置顶保底显示 */
const thinkOptions = computed(() => {
  const lv = curModel.value?.thinkingLevels ?? []
  let list = lv.length ? THINK_LABELS.filter(([v]) => lv.includes(v)) : THINK_LABELS
  const cur = state.value?.thinkingEffort
  if (cur && !list.some(([v]) => v === cur)) list = [[cur, cur] as [string, string], ...list]
  return list.map(([v, l]) => ({ value: v, label: l, title: LEVEL_EN[v] ?? v }))
})

/** ＋ 菜单的文件类型是否可用(文本/文件始终可用;未配置媒体时默认仅图片) */
function mediaEnabled(type: 'image' | 'video' | 'audio' | 'pdf') {
  const mt = curModel.value?.mediaTypes ?? []
  return mt.length ? mt.includes(type) : type === 'image'
}

/** ＋ 菜单:文件(全部类型)始终显示;图片/视频/音频/PDF 按当前模型的媒体类型配置 */
const attachTypes = computed(() => {
  const list: { kind: 'file' | 'image' | 'video' | 'audio' | 'pdf'; label: string; icon: string; accept: string }[] = [
    { kind: 'file', label: '文件(所有类型)', icon: 'attach_file', accept: '' },
  ]
  if (mediaEnabled('image')) list.push({ kind: 'image', label: '图片', icon: 'photo', accept: 'image/*' })
  if (mediaEnabled('video')) list.push({ kind: 'video', label: '视频', icon: 'videocam', accept: 'video/*' })
  if (mediaEnabled('audio')) list.push({ kind: 'audio', label: '音频', icon: 'mic', accept: 'audio/*' })
  if (mediaEnabled('pdf')) list.push({ kind: 'pdf', label: 'PDF', icon: 'picture_as_pdf', accept: 'application/pdf' })
  return list
})

const attachMenuOpen = ref(false)

function pickAttachType(kind: 'file' | 'image' | 'video' | 'audio' | 'pdf', accept: string) {
  attachMenuOpen.value = false
  pendingKind.value = kind
  const input = fileInput.value
  if (!input) return
  input.accept = accept
  input.click()
}

// 附件图片预览(未发送也可见)
const lightboxSrc = ref('')

// 子代理运行模态框:侧边栏「智能体」页签或聊天里 sub_* 工具行点击打开(只读回看)
const subAgentRun = ref<number | null>(null)
function openSubAgentRun(runId: number) {
  subAgentRun.value = runId
}

/** 未发送媒体附件的图标 */
const MEDIA_ICONS: Record<string, string> = { pdf: 'picture_as_pdf', video: 'videocam', audio: 'mic' }

/** 附件弹出菜单的关闭:点外部收起 */
function closeAttachMenu(e: PointerEvent) {
  if ((e.target as HTMLElement).closest('.attach-wrap')) return
  attachMenuOpen.value = false
}

watch(attachMenuOpen, (v) => {
  if (v) document.addEventListener('pointerdown', closeAttachMenu, true)
  else document.removeEventListener('pointerdown', closeAttachMenu, true)
})

onUnmounted(() => document.removeEventListener('pointerdown', closeAttachMenu, true))

/** 直接下载未发送的媒体附件(data URL) */
function downloadAttachment(a: WebAttachment) {
  if (!a.dataUrl) return
  const link = document.createElement('a')
  link.href = a.dataUrl
  link.download = a.name
  link.click()
}

// ───────── 模型切换 ─────────

async function applyModel(patch: { provider?: string; model?: string; thinkingEffort?: string }) {
  try {
    state.value = await setModel(agentId.value, props.sessionId, patch)
  } catch (e) {
    showErr(String(e))
  }
}

function scrollBottom(force = false) {
  nextTick(() => {
    const el = listEl.value
    if (!el) return
    if (listAnimating && !force) return // 平滑滚动接管期间,不做逐帧瞬钉(会打断动画)
    if (!force && el.scrollHeight - el.scrollTop - el.clientHeight < 160) el.scrollTop = el.scrollHeight
    else if (force) el.scrollTop = el.scrollHeight
  })
}

let listAnimating = false
let listAnimRaf = 0

/** 列表平滑滚到底(思考卡展开用):内容还在增长,目标取【实时底】;用户中途滚动立即让位 */
function animateListToBottom() {
  const el = listEl.value
  if (!el) return
  cancelAnimationFrame(listAnimRaf)
  const start = el.scrollTop
  const startT = performance.now()
  const DUR = 320
  let lastSet = start
  listAnimating = true
  const step = (t: number) => {
    if (Math.abs(el.scrollTop - lastSet) > 1) {
      listAnimating = false
      return // 用户滚动了,让位
    }
    const p = Math.min(1, (t - startT) / DUR)
    const eased = 1 - Math.pow(1 - p, 3) // easeOutCubic
    const target = el.scrollHeight - el.clientHeight
    const next = start + (target - start) * eased
    el.scrollTop = next
    lastSet = next
    if (p < 1) listAnimRaf = requestAnimationFrame(step)
    else {
      el.scrollTop = el.scrollHeight
      listAnimating = false
    }
  }
  listAnimRaf = requestAnimationFrame(step)
}

/** 思考卡展开:原生 details 在 click 默认动作里同步长高 ~280px,等 toggle 事件到达时
 *  距底距离已是展开后的(读实时状态会误判)。改读 listAtBottom——它由真实滚动事件维护,
 *  展开只改 scrollHeight 不改 scrollTop、不触发滚动事件,保留的正是展开前的贴底状态。
 *  贴底时平滑滚到新底(原地展开+顺滑跟随,不做瞬跳) */
function onThinkingExpand() {
  if (listAtBottom.value) animateListToBottom()
}

/** 向上滚近顶:按页前插更早的历史行,并补偿 scrollTop 保持视觉位置不动;
 * 按 seq 去重(翻页途中压缩等重排已删的行不会重复插入);翻页拿不到行 = 到头/游标失效,停止 */
async function loadOlder() {
  const s = state.value
  if (!s || loadingOlder || !s.hasMoreMessages || s.messageCursor == null) return
  loadingOlder = true
  const el = listEl.value
  const prevTop = el?.scrollTop ?? 0
  const prevHeight = el?.scrollHeight ?? 0
  try {
    const page = await getSession(agentId.value, props.sessionId, { limit: OLDER_PAGE, before: s.messageCursor })
    const known = new Set(s.messages.filter(m => m.seq != null).map(m => m.seq))
    const fresh = page.messages.filter(m => m.seq == null || !known.has(m.seq))
    if (fresh.length) s.messages.splice(0, 0, ...fresh)
    s.messageCursor = page.messageCursor ?? s.messageCursor
    s.hasMoreMessages = page.messageCursor != null ? (page.hasMoreMessages ?? false) : false
    s.messageTotal = page.messageTotal ?? s.messageTotal
    storeState(props.sessionId, s)
    await nextTick()
    const el2 = listEl.value
    if (el2 && fresh.length) el2.scrollTop = el2.scrollHeight - prevHeight + prevTop
  } catch (e) {
    showErr(String(e))
  } finally {
    loadingOlder = false
  }
}

const listAtBottom = ref(true) // 列表是否贴底(跟随语义同 scrollBottom 的 160px);供思考卡展开时判断是否强制跟随

function onListScroll() {
  const el = listEl.value
  if (!el) return
  listAtBottom.value = el.scrollHeight - el.scrollTop - el.clientHeight < 160
  if (el.scrollTop < 120) void loadOlder()
}

// ───────── 会话切换冻结态:首帧起隐藏消息区(叠微光骨架),数据就绪钉底后淡入揭示 ─────────

const frozen = ref(true)

/** 消息区钉到底部并揭示(解除冻结,以短暂淡入过渡呈现;滚动在隐藏态完成,无跳变) */
function pinReveal() {
  const el = listEl.value
  if (el) el.scrollTop = el.scrollHeight
  requestAnimationFrame(() => {
    unfreeze()
  })
}

// 退出冻结的唯一出口:内容已就绪(钉底完成或异常兜底),后台视图由此通知 App 切换上屏
function unfreeze() {
  if (!frozen.value) return
  frozen.value = false
  if (props.background) emit('ready', props.sessionId)
}

// ───────── 输入框:多行自动长高(至 160,同桌面端);页面标题跟随会话名 ─────────

const inputEl = ref<HTMLTextAreaElement | null>(null)

function autoGrow() {
  const el = inputEl.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = Math.min(el.scrollHeight, 160) + 'px'
}

watch(input, () => nextTick(autoGrow))

watch(
  () => state.value?.title,
  (t) => {
    if (props.background) return // 后台预备视图不改窗口标题(上屏时补)
    document.title = t ? `${t} · APet` : 'APet'
  },
  { immediate: true },
)

// 后台预备视图被 App 切换上屏时(background false→本视图可见):补应用窗口标题与界面缩放
watch(
  () => props.background,
  (bg) => {
    if (bg) return
    const t = state.value?.title
    document.title = t ? `${t} · APet` : 'APet'
    if (agentId.value) applyAgentUiScale(agentId.value)
  },
)

// 本次挂载时刻:挂载后 1 秒内忽略 SSE 事件的自动滚动(切换瞬间撞事件导致的整屏换位)
const mountedAt = Date.now()

// 缓存先行(同步,早于首帧渲染):切换会话重挂载时首帧就带上上次的列表/标题,
// 消息区保持冻结,数据就绪钉底后同帧揭示;网络刷新仍在 onMounted 里后台进行
const ownerCached = cachedOwner(props.sessionId)
if (sessions.value.length === 0) {
  // 归属 Agent 尚未解析(首次访问的会话):先铺上次显示过的列表作过渡,避免空白一闪
  const last = lastShownListSnapshot()
  if (last) sessions.value = last.list
}
if (ownerCached) {
  agentId.value = ownerCached
  const cs = cachedState(props.sessionId)
  if (cs) state.value = cs
  const ss = cachedSessions(ownerCached)
  if (ss) {
    sessions.value = ss
    listPending.value = false // 列表已有缓存内容,不再显示骨架
    markListShown(ownerCached, ss)
  }
  agentTitle.value = cachedAgentTitle(ownerCached) ?? ''
  agentAvatarRev.value = cachedAgentAvatarRev(ownerCached) ?? 0
  if (!agentTitle.value) {
    // 标题缓存没有(该 Agent 首页没加载过清单)→ 从 Agent 清单缓存取
    const a = cachedAgentList()?.find((x) => x.id === ownerCached)
    if (a) {
      agentTitle.value = a.title
      storeAgentTitle(ownerCached, a.title)
      agentAvatarRev.value = a.avatarRev
      storeAgentAvatarRev(ownerCached, a.avatarRev)
    }
  }
  // 归属已知:首帧渲染前并行预发快照/工具/命令请求,挂载后直接等结果
  preFetch = {
    agentId: ownerCached,
    state: getSession(ownerCached, props.sessionId, { limit: MESSAGE_WINDOW }),
    providers: getProviders(ownerCached),
    commands: getCommands(ownerCached),
  }
}
// 有缓存状态(哪怕空会话)就不等网络刷新,直接按缓存钉底揭示(约两帧):
// 消除"侧栏已就位、主区空白"的空窗闪。网络快照稍后到达属同会话增量更新,不产生整屏空白
if (state.value) nextTick(pinReveal)
</script>

<template>
  <div class="chat" data-APet-anchor="chat" @dragover="onDragOver" @drop="onDrop">
    <!-- 窗体背景图:铺整个聊天窗最底层,半透明侧栏/输入卡透出(对应桌面端 WindowBackground)。
         归属 Agent 未解析前不渲染(空 agentId 会打 404 坏 URL);且随 frozen 与消息区同帧揭示,
         避免图片异步加载完单独"啪"地冒出来 = 闪烁源 -->
    <div
      v-if="!frozen && agentId && state?.windowBgOpacity !== null && state?.windowBgOpacity !== undefined"
      class="win-bg"
      :style="{
        opacity: state.windowBgOpacity / 100,
        backgroundImage: `url(/api/agents/${agentId}/backgrounds/window)`,
        backgroundSize: winBgSize,
        backgroundRepeat: state.windowBgStretch === 'Tile' ? 'repeat' : 'no-repeat',
      }"
    />
    <header class="top" data-APet-anchor="chat-header">
      <button class="icon-btn" :title="sessionsOpen ? '收起会话栏' : '展开会话栏'" @click="sessionsOpen = !sessionsOpen"><span class="ms">menu</span></button>
      <div class="meta"><div class="title">{{ state?.title ?? '…' }}</div></div>
      <button
        class="icon-btn"
        :title="sidebarOpen ? '收起侧边栏' : '展开侧边栏(任务清单/后台任务)'"
        @click="sidebarOpen = !sidebarOpen"
      ><span class="ms">view_sidebar</span></button>
      <button class="icon-btn" :title="theme === 'dark' ? '切换到亮色主题' : '切换到暗色主题'" @click="theme = toggleTheme()">
        <span class="ms">{{ theme === 'dark' ? 'dark_mode' : 'light_mode' }}</span>
      </button>
      <div class="menu-wrap">
        <button class="icon-btn" title="会话操作" @click="menuOpen = !menuOpen"><span class="ms">more_vert</span></button>
        <div v-if="menuOpen" class="menu">
          <button @click="doRename"><span class="ms">edit</span>重命名</button>
          <button @click="doFork"><span class="ms">call_split</span>分叉</button>
          <button :disabled="isArchived" @click="doCompact"><span class="ms">compress</span>压缩上下文</button>
          <button :disabled="isArchived" @click="doUndo"><span class="ms">undo</span>撤销上一条</button>
          <button @click="doSummary"><span class="ms">summarize</span>查看摘要</button>
          <button @click="doSystemPrompt"><span class="ms">psychology</span>查看系统提示词</button>
          <button v-if="!isArchived" @click="doArchive"><span class="ms">archive</span>归档</button>
          <button v-else @click="doUnarchiveCurrent"><span class="ms">unarchive</span>移出归档</button>
          <button @click="goSettings"><span class="ms">settings</span>设置</button>
          <button class="danger" @click="doDelete"><span class="ms">delete</span>删除会话</button>
        </div>
      </div>
    </header>

    <div class="body-row">
      <!-- 左:会话栏(可收起;新对话 / 会话列表 / 归档 / 设置) -->
      <aside v-if="sessionsOpen" class="session-col" :style="{ width: leftRz.width + 'px' }">
        <div class="session-top">
          <button class="ghost wide-btn" @click="doNewChat"><span class="ms">add</span> 新对话</button>
        </div>
        <!-- 列表空白区 = 拖拽落点"回根组"(行上的 drop 会 stopPropagation,互不干扰);
             空白区悬停时底部显示回根指示线(onDragOverRoot) -->
        <div class="session-scroll" @dragover.prevent="onDragOverRoot($event)" @drop.prevent="onDropRoot($event)">
          <template v-if="!archivedOpen">
            <div v-if="pinnedSessions.length" class="pin-label">已置顶</div>
            <button
              v-for="s in pinnedSessions"
              :key="'p' + s.sessionId"
              class="session-item"
              :class="{ sel: s.sessionId === props.sessionId }"
              @click="openSession(s.sessionId)"
              @contextmenu.prevent="openSessionMenu(s, $event)"
            >
              <span
                class="dot"
                :class="{ work: s.isStreaming && !s.isCompressing, compress: s.isCompressing, err: s.hasError, unread: s.unread }"
              />
              <span class="s-title">{{ s.title }}</span>
              <span v-if="s.lastMessageTimeText" class="s-time">{{ s.lastMessageTimeText }}</span>
              <span class="pin-ico" title="已置顶"><span class="ms fill">push_pin</span></span>
            </button>
            <div v-if="pinnedSessions.length && normalSessions.length" class="pin-label">全部会话</div>
            <!-- 会话树(递归组件):根组会话 → 顶级文件夹(递归:组内会话 + 子文件夹);缩进 = 层级。
                 文件夹子块的展开/收起动画 = .kids 的 grid-template-rows 原生过渡(整块一个元素,平滑无顿挫) -->
            <SidebarTree
              :tree="treeRows"
              :collapsed="collapsedFolders"
              :sel-id="props.sessionId"
              :drag-key="dragKey"
              :drop-hint="dropHint"
              :on-open-session="openSession"
              :on-toggle-folder="toggleFolderCollapse"
              :on-session-menu="openSessionMenu"
              :on-folder-menu="openFolderMenu"
              :on-drag-start-row="onDragStart"
              :on-drag-end-row="onDragEnd"
              :on-drag-over-row="onDragOverRow"
              :on-drop-row="onDropOnRow"
            />
            <div v-if="sessions.length === 0 && !listPending" class="s-empty">还没有会话,点上方开始新对话</div>
          </template>
          <template v-else>
            <div
              v-for="s in archived"
              :key="s.id"
              class="arch-item"
              :class="{ sel: s.id === props.sessionId }"
              :title="s.title + '(点击预览)'"
              @click="openSession(s.id)"
            >
              <span class="ms arch-ico">archive</span>
              <span class="s-title">{{ s.title }}</span>
              <span class="s-time">{{ fmtArchTime(s.updatedAt) }}</span>
              <div class="arch-actions">
                <button class="arch-btn" title="移出归档(放回会话列表)" @click.stop="doUnarchive(s)"><span class="ms">unarchive</span></button>
                <button class="arch-btn danger" title="删除(不可恢复)" @click.stop="doDeleteArchived(s)"><span class="ms">delete</span></button>
              </div>
            </div>
            <div v-if="archived.length === 0" class="s-empty">暂无归档会话</div>
          </template>
          <!-- 空区落点指示:指针在容器空白区时,列表最底部一条细线 = 落这里回根组 -->
          <div v-if="rootDrop" class="drop-root-line" />
        </div>
        <div class="session-bottom">
          <div class="agent-id" :title="agentTitle">
            <img v-if="agentAvatarRev" class="avatar avatar-img" :src="avatarUrl(agentId, agentAvatarRev)" alt="" />
            <div v-else class="avatar">{{ (agentTitle || 'A').slice(0, 1) }}</div>
            <span class="agent-name">{{ agentTitle || 'APet' }}</span>
          </div>
          <span class="grow" />
          <button
            v-if="archivedOpen"
            class="icon-btn danger"
            :title="`删除所有归档会话(${archived.length} 个,不可恢复)`"
            @click="doClearArchived"
          ><span class="ms">delete_sweep</span></button>
          <button v-if="!archivedOpen" class="icon-btn" title="新建文件夹" @click="doCreateFolder"><span class="ms">create_new_folder</span></button>
          <button class="icon-btn" :class="{ on: archivedOpen }" :title="archivedOpen ? '返回会话列表' : '归档对话'" @click="toggleArchivedList"><span class="ms">archive</span></button>
          <button class="icon-btn" title="设置" @click="goSettings"><span class="ms">settings</span></button>
        </div>
      </aside>
      <div v-if="sessionsOpen" class="resizer" title="拖动调整会话栏宽度" v-bind="leftRz.handlers" />

      <div class="main-col">
        <!-- 预览中的归档被删空:主区整体留白(状态栏保留,底部骨架与把手垫色对齐不受影响) -->
        <div v-if="archPreviewEmpty" class="arch-blank" />
        <template v-else>
        <!-- 聊天区背景图(设置页上传;不透明度/缩放随配置);归属未解析前不渲染,随 frozen 与消息区同帧揭示 -->
        <div
          v-if="!frozen && agentId && state?.chatBgOpacity !== null && state?.chatBgOpacity !== undefined"
          class="chat-bg"
          :style="{
            opacity: state.chatBgOpacity / 100,
            backgroundImage: `url(/api/agents/${agentId}/backgrounds/chat)`,
            backgroundSize: bgSize,
            backgroundRepeat: state.chatBgStretch === 'Tile' ? 'repeat' : 'no-repeat',
          }"
        />
        <div ref="listEl" class="messages" data-APet-anchor="messages" :class="{ frozen }" @scroll="onListScroll">
          <MessageBubble
            v-for="(m, i) in visibleMessages"
            :key="m.seq ?? m.localKey ?? 'e' + i"
            :message="m"
            :agent-id="agentId"
            :session-id="props.sessionId"
            @auth="(step: any, allow: boolean, trust: boolean) => onAuth(step, allow, trust)"
            @open-sub-run="openSubAgentRun"
            @expand-thinking="onThinkingExpand"
          />
          <!-- 运行指示行:聊天文本最下面一行,形态与显隐同输入框圆盘旁的 spinner(isStreaming 同步) -->
          <div v-if="state && state.isStreaming" class="turn-row">
            <span class="turn-spin" title="模型工作中">
              <i v-for="n in 8" :key="n" :style="rayStyle(n)" />
            </span>
          </div>
        </div>

        <!-- 输入卡片:附件行 + 无边框输入 + 工具栏行(附件/工作目录 | 上下文/模型/思考/发送),同桌面端 -->
        <footer class="input-card" data-APet-anchor="composer">
          <!-- 中途消息队列:运行中发送的插话在此排队;取消=移出,重新编辑=回填输入框(再发送回队尾);
               时钟按钮=切换"本轮后发送"(Ctrl+Enter 入队即带此标记);行首把手=拖拽调整顺序 -->
          <div v-if="queueItems.length" class="queue-panel" title="运行中回车=插话入队;Ctrl+Enter=本轮后发送;把手拖拽调序;命令消费时按命令触发">
            <div class="queue-head">
              <span class="ms">queue</span>
              <span>中途消息队列</span>
              <span class="queue-count">{{ queueItems.length }}</span>
            </div>
            <div
              v-for="(q, i) in queueItems"
              :key="q.id"
              class="queue-item"
              :class="{
                cmd: isCmdText(q.text),
                defer: q.defer && !isCmdText(q.text),
                'over-top': qOverId === q.id && !qOverAfter,
                'over-bot': qOverId === q.id && qOverAfter,
              }"
              @dragover="overQueueItem(q, $event)"
              @drop="dropQueueItem(q, $event)"
            >
              <span
                class="ms queue-grip"
                draggable="true"
                title="拖拽调整队列顺序"
                @dragstart="startQueueDrag(q, $event)"
                @dragend="endQueueDrag()"
              >drag_indicator</span>
              <span class="queue-seq">{{ i + 1 }}</span>
              <span
                v-if="isCmdText(q.text)"
                class="ms queue-cmd"
                title="命令消息:队列消费时按命令触发,不发给模型"
              >terminal</span>
              <div class="queue-main">
                <div class="queue-text">{{ q.text || '（仅附件）' }}</div>
                <div class="queue-meta">
                  <span v-if="q.imageCount">图片 ×{{ q.imageCount }}</span>
                  <span v-for="n in q.fileNames" :key="n">{{ n }}</span>
                  <span v-if="q.defer && !isCmdText(q.text)" class="queue-flag">本轮后发送</span>
                </div>
              </div>
              <div class="queue-acts">
                <button
                  v-if="!isCmdText(q.text)"
                  class="queue-btn"
                  :class="{ on: q.defer }"
                  :title="q.defer ? '本轮结束后才发送(点击改回:随下次请求拼入上下文)' : '本轮后发送:不拼进当前请求,本轮结束后作为新消息发出(Ctrl+Enter 入队即带此标记)'"
                  @click="toggleDefer(q)"
                ><span class="ms" :class="{ fill: q.defer }">schedule</span></button>
                <button class="queue-btn" title="重新编辑:移出队列并放回输入框(再次发送时回到队尾)" @click="reeditQueue(q.id)"><span class="ms">edit</span></button>
                <button class="queue-btn danger" title="取消:移出队列" @click="cancelQueue(q.id)"><span class="ms">close</span></button>
              </div>
            </div>
          </div>
          <!-- ask 询问面板:盖住整个输入卡片;Tab/上下移动光标,Enter/空格选中,←/→ 切题 -->
          <div
            v-if="askCur"
            ref="askPanelEl"
            class="ask-panel"
            tabindex="0"
            @keydown="onAskKeydown"
          >
            <div class="ask-head">
              <span class="ask-title">{{ askCur.q.title }}</span>
              <span class="ask-question">{{ askCur.q.question }}</span>
              <span v-if="askCur.q.multiSelect" class="ask-multi">(可多选)</span>
              <div v-if="askCur.ask.questions.length > 1" class="ask-pager">
                <button
                  class="ask-page-btn"
                  :disabled="askCur.ask.page === 0"
                  title="上一题"
                  @click="askPage(-1)"
                ><span class="ms">chevron_left</span></button>
                <span class="ask-page-num">{{ askCur.ask.page + 1 }}/{{ askCur.ask.questions.length }}</span>
                <button
                  class="ask-page-btn"
                  :disabled="askCur.ask.page >= askCur.ask.questions.length - 1"
                  title="下一题"
                  @click="askPage(1)"
                ><span class="ms">chevron_right</span></button>
              </div>
            </div>
            <div class="ask-options">
              <div
                v-for="(o, i) in askCur.q.options"
                :key="i"
                class="ask-opt"
                :class="{ sel: askCur.ask.sel[askCur.ask.page].includes(i), cur: askCur.ask.cursor === i }"
                @mouseenter="askCur.ask.cursor = i"
                @click="askCur.ask.cursor = i; askPick()"
              >
                <span v-if="askCur.q.multiSelect" class="ask-check" :class="{ on: askCur.ask.sel[askCur.ask.page].includes(i) }">
                  <span v-if="askCur.ask.sel[askCur.ask.page].includes(i)" class="ms">check</span>
                </span>
                <span v-else class="ask-num">{{ i + 1 }}.</span>
                <span class="ask-label">{{ o.label }}</span>
                <span v-if="o.description" class="ask-desc">{{ o.description }}</span>
                <span v-if="o.recommended" class="ask-rec">(Recommended)</span>
              </div>
              <div
                class="ask-opt ask-other"
                :class="{ cur: askCur.ask.cursor === askCur.q.options.length }"
                @mouseenter="askCur.ask.cursor = askCur.q.options.length"
                @click="askCur.ask.cursor = askCur.q.options.length; askOtherEl?.focus()"
              >
                <span v-if="askCur.q.multiSelect" class="ask-check" :class="{ on: !!askCur.ask.other[askCur.ask.page].trim() }">
                  <span v-if="!!askCur.ask.other[askCur.ask.page].trim()" class="ms">check</span>
                </span>
                <span v-else class="ask-num">{{ askCur.q.options.length + 1 }}.</span>
                <input
                  ref="askOtherEl"
                  v-model="askCur.ask.other[askCur.ask.page]"
                  class="ask-other-input"
                  placeholder="输入你的回答…"
                  @focus="askCur.ask.cursor = askCur.q.options.length"
                />
              </div>
            </div>
            <div class="ask-foot">
              <span class="ask-hint"><span class="ms">info</span>使用 Tab / 上下键选择，回车或空格选中</span>
              <button class="ask-skip" @click="askSkip">忽略</button>
              <!-- 非最后一题=继续(翻到下一题),最后一题=提交;键盘回车同 askAdvance 语义 -->
              <button class="ask-continue" @click="askAdvance">{{ askCur && askCur.ask.page < askCur.ask.questions.length - 1 ? '继续' : '提交' }}</button>
            </div>
          </div>
          <div v-if="attachments.length" class="attach-row">
            <div v-for="(a, i) in attachments" :key="i" class="attach">
              <img
                v-if="a.kind === 'image' && a.dataUrl"
                :src="a.dataUrl"
                class="attach-thumb"
                title="点击预览(可缩放/下载)"
                @click="lightboxSrc = a.dataUrl!"
              />
              <span v-else-if="a.kind === 'media'" class="attach-file attach-click" title="点击下载" @click="downloadAttachment(a)">
                <span class="ms">{{ MEDIA_ICONS[a.media ?? 'pdf'] }}</span> {{ a.name }}<span class="attach-size">{{ a.sizeText }}</span>
              </span>
              <span v-else class="attach-file"><span class="ms">description</span> {{ a.name }}<span class="attach-size">{{ a.sizeText }}</span></span>
              <button class="attach-remove" @click="removeAttachment(i)"><span class="ms">close</span></button>
            </div>
          </div>
          <div v-if="cmdPopupOpen && cmdMatches.length" class="cmd-popup">
            <div
              v-for="(c, i) in cmdMatches"
              :key="c.name"
              class="cmd-item"
              :class="{ sel: i === cmdIndex }"
              @mouseenter="cmdIndex = i"
              @click="completeSelected(); closeCmdPopup()"
            >
              <span class="cmd-name">{{ c.name }}</span>
              <span class="cmd-desc">{{ c.description }}</span>
            </div>
          </div>
          <div v-if="isArchived" class="arch-banner"><span class="ms">lock</span>会话已归档:只读预览,无法发送新消息</div>
          <textarea
            ref="inputEl"
            v-model="input"
            class="input"
            rows="1"
            :disabled="isArchived"
            :placeholder="isArchived ? '会话已归档,无法输入' : '输入消息,Enter 发送,Shift+Enter 换行;/ 或 $ 唤出命令;可粘贴/拖入图片'"
            @keydown="onInputKeydown"
            @keydown.enter.exact.prevent="onEnter($event)"
            @paste="onPaste"
          />
          <div class="toolbar">
            <div class="tb-left">
              <!-- ＋ 附件:弹出类型菜单(文件始终可用;图片/视频/音频/PDF 按当前模型的媒体类型配置) -->
              <div class="attach-wrap">
                <button class="icon-plain" :disabled="isArchived" title="添加附件" @click="attachMenuOpen = !attachMenuOpen"><span class="ms">add</span></button>
                <div v-if="attachMenuOpen" class="attach-pop">
                  <button v-for="t in attachTypes" :key="t.kind" @click="pickAttachType(t.kind, t.accept)">
                    <span class="ms">{{ t.icon }}</span>{{ t.label }}
                  </button>
                </div>
              </div>
              <input
                ref="fileInput"
                type="file"
                multiple
                class="file-input"
                @change="addFiles(($event.target as HTMLInputElement).files ?? [], pendingKind); ($event.target as HTMLInputElement).value = ''"
              />
              <button
                v-if="state"
                class="pill workdir"
                :class="{ locked: state.isStreaming || state.isWorkDirLocked || isArchived }"
                :title="state.isWorkDirLocked
                  ? (state.workingDirectory || '工作目录') + '(会话已产生对话,工作目录已锁定;点击用资源管理器打开)'
                  : (state.workingDirectory || '选择本会话的工作目录(配合权限决定工具的读写范围)')"
                @click="onWorkdirPillClick"
              >
                <span class="ms pill-ico">folder_open</span> {{ workdirName || '选择工作目录' }}
              </button>
              <span
                v-if="state && !state.isWorkDirLocked && !isArchived && state.workingDirectory"
                class="wd-clear"
                title="清除工作目录"
                @click="clearWorkdir"
              ><span class="ms">close</span></span>
              <div v-if="state" class="bare-wrap" title="工作目录权限:决定工具在目录内的读写范围">
                <span class="ms lock-ico" :class="permStyle.cls">{{ permStyle.icon }}</span>
                <AppSelect
                  class="bare"
                  :model-value="state.workingDirectoryAccess"
                  :options="[['ReadOnly', '只读'], ['ReadWrite', '读写'], ['FullAccess', '完全访问'], ['Ask', '全部询问']]"
                  :disabled="state.isStreaming || isArchived"
                  @update:model-value="applyWorkdirAccess($event)"
                />
              </div>
            </div>
            <div class="tb-right">
              <!-- 运行指示:射线环形 spinner,回合进行中常驻,回合结束或停止即隐藏 -->
              <span v-if="state && state.isStreaming" class="turn-spin" title="模型工作中">
                <i v-for="n in 8" :key="n" :style="rayStyle(n)" />
              </span>
              <!-- 上下文占用圆环:模型选择左侧;悬停浮出用量明细(自定义浮层,不用问号光标) -->
              <div v-if="state && ctxRatio >= 0" class="ctx-wrap">
                <svg class="ctx-ring" width="16" height="16" viewBox="0 0 36 36">
                  <circle cx="18" cy="18" r="15.5" fill="none" stroke="var(--border)" stroke-width="4.5" />
                  <circle
                    cx="18"
                    cy="18"
                    r="15.5"
                    fill="none"
                    :stroke="ringColor"
                    stroke-width="4.5"
                    stroke-linecap="round"
                    :stroke-dasharray="`${CIRC * ctxRatio} ${CIRC}`"
                    transform="rotate(-90 18 18)"
                  />
                </svg>
                <div class="ctx-tip">
                  <div class="ctx-tip-head">上下文容量</div>
                  <div class="ctx-tip-row">{{ fmtTokens(ctxUsed) }} / {{ fmtTokens(state.contextLimit) }}({{ (ctxRatio * 100).toFixed(1) }}%)</div>
                  <div class="ctx-tip-bar"><span :style="{ width: Math.min(100, ctxRatio * 100) + '%', background: ringColor }" /></div>
                </div>
              </div>
              <!-- 模型级联菜单:当前选中 → 供应商分组(悬停展开模型清单) → 管理模型 -->
              <div v-if="state" class="model-menu">
                <button
                  class="bare-btn"
                  :disabled="state.isStreaming || isArchived"
                  title="聊天模型(清单在设置页维护;切换供应商自动回落其默认模型)"
                  @click.stop="modelMenuOpen = !modelMenuOpen"
                >
                  <span class="bare-label" :class="{ ph: !state.model }">{{ state.model || '请选择模型' }}</span>
                  <span class="ms chev-d">expand_more</span>
                </button>
                <div v-if="modelMenuOpen" class="m-pop" @mouseleave="hoverProvider = ''">
                  <div v-if="state.model" class="m-current">
                    <span class="m-check"><span class="ms">check</span></span>
                    <span class="m-cur-name">{{ state.model }}</span>
                    <span class="m-cur-prov">{{ state.provider }}</span>
                  </div>
                  <div class="m-sep" />
                  <div v-for="p in providers" :key="p.name" class="m-prov-row" @mouseenter="hoverProvider = p.name">
                    <span>{{ p.name }}</span>
                    <span class="m-arrow"><span class="ms">chevron_right</span></span>
                    <div v-if="hoverProvider === p.name" class="m-sub">
                      <div
                        v-for="m in p.models"
                        :key="m.name"
                        class="m-model"
                        :class="{ cur: state.provider === p.name && state.model === m.name }"
                        @click="pickModel(p.name, m.name)"
                      >
                        <span class="m-check"><span v-if="state.provider === p.name && state.model === m.name" class="ms">check</span></span>
                        <span>{{ m.name }}</span>
                      </div>
                      <div v-if="!p.models.length" class="m-empty">清单为空:到设置页拉取/添加模型</div>
                    </div>
                  </div>
                  <div class="m-sep" />
                  <div class="m-manage" title="直达「模型服务」设置分区" @click="modelMenuOpen = false; goSettings('llm')"><span class="ms">tune</span>管理模型</div>
                </div>
              </div>
              <div v-if="state" class="bare-wrap" title="思考等级:关闭直接作答,其余档位先推理后回答(default 按 high 发送)">
                <AppSelect
                  class="bare"
                  :model-value="state.thinkingEffort"
                  :disabled="state.isStreaming || isArchived"
                  :options="thinkOptions"
                  @update:model-value="applyModel({ thinkingEffort: $event })"
                />
              </div>
              <button
                v-if="state && !state.isStreaming && !isArchived"
                class="send-sq"
                :disabled="!input.trim() && !attachments.length"
                title="发送(Enter 发送 / Shift+Enter 换行)"
                @click="send()"
              ><span class="ms">send</span></button>
              <button
                v-if="state && state.isStreaming && !isArchived && (input.trim() || attachments.length)"
                class="send-sq queue-send"
                title="运行中:加入中途消息队列(Enter 同;下一次请求前按序带出)"
                @click="send()"
              ><span class="ms">send</span>入队</button>
              <button
                v-if="state && state.isStreaming"
                class="send-sq stop"
                title="终止当前请求"
                @click="stop()"
              ><span class="ms">stop</span></button>
            </div>
          </div>
        </footer>
        </template>
        <!-- 底部状态栏:左 LLM 信息(模型/≈tok/s/会话累计/缓存%),右 插件数据面 status 字段 -->
        <StatusBar :agent-id="agentId" :session-id="props.sessionId" :model="state?.model" :usage="llmStats" :streaming="state?.isStreaming" />
      </div>
      <!-- v-show 保活(非 v-if):收起侧边栏不卸载插件 UI 页,插件脚本(数据面轮询等)不中断 -->
      <div v-if="sidebarOpen" class="resizer" title="拖动调整侧边栏宽度" v-bind="rightRz.handlers" />
      <SidePanel v-show="sidebarOpen" class="side" :style="{ width: rightRz.width + 'px' }" :agent-id="agentId" :session-id="props.sessionId" @open-run="openSubAgentRun" />
    </div>

    <!-- 子代理运行模态框(只读):回看 sub_run 委托的完整过程 -->
    <SubAgentModal v-if="subAgentRun !== null" :agent-id="agentId" :run-id="subAgentRun" @close="subAgentRun = null" />

    <!-- 会话右键菜单:图标固定宽列对齐(Material Symbols) -->
    <div v-if="ctxMenu" class="s-menu" :style="{ left: ctxMenu.x + 'px', top: ctxMenu.y + 'px' }">
      <button @click="togglePin(ctxMenu.s)">
        <span class="ms mi" :class="{ fill: !ctxMenu.s.pinned }">{{ ctxMenu.s.pinned ? 'keep_off' : 'push_pin' }}</span>{{ ctxMenu.s.pinned ? '取消置顶' : '置顶' }}
      </button>
      <button @click="renameSessionById(ctxMenu.s)"><span class="ms mi">edit</span>重命名</button>
      <template v-if="topFolders.length">
        <button v-if="ctxMenu.s.folderId" @click="doSessionToRoot(ctxMenu.s)"><span class="ms mi">drive_folder_upload</span>移到根目录</button>
        <div v-if="moveTargets.length" class="sub" @mouseenter="cancelMoveSubClose(); moveSub = true" @mouseleave="scheduleMoveSubClose">
          <button @click="moveSub = !moveSub"><span class="mi"></span>移入<span class="ms chev">chevron_right</span></button>
          <div v-if="moveSub" class="sub-menu" :class="{ left: moveSubLeft }">
            <button v-for="f in moveTargets" :key="'mv' + f.id" @click="doSessionToFolder(ctxMenu.s, f)"><span class="ms mi">create_new_folder</span>{{ f.name }}</button>
          </div>
        </div>
      </template>
      <button @click="forkSessionById(ctxMenu.s)"><span class="ms mi">call_split</span>分叉</button>
      <button @click="archiveSessionById(ctxMenu.s)"><span class="ms mi">archive</span>归档</button>
      <button class="danger" @click="deleteSessionById(ctxMenu.s)"><span class="ms mi">delete</span>删除</button>
    </div>

    <!-- 文件夹右键菜单(新建对话/新建子夹/重命名/修改颜色/移到根/删除) -->
    <div v-if="folderMenu" class="s-menu f-menu" :style="{ left: folderMenu.x + 'px', top: folderMenu.y + 'px' }">
      <button @click="doFolderNewSession"><span class="ms mi">add</span>新建对话</button>
      <button @click="doFolderNewChild"><span class="ms mi">create_new_folder</span>新建子文件夹</button>
      <button @click="doFolderRename"><span class="ms mi">edit</span>重命名</button>
      <div class="sub" @mouseenter="cancelColorSubClose(); colorSub = true" @mouseleave="scheduleColorSubClose">
        <button @click="colorSub = !colorSub"><span class="ms mi">tune</span>修改颜色<span class="ms chev">chevron_right</span></button>
        <div v-if="colorSub" class="sub-menu" :class="{ left: colorSubLeft }">
          <button v-for="c in FOLDER_COLORS" :key="'fc' + c.id" @click="doFolderSetColor(c.id)">
            <span class="c-dot" :style="c.hex ? { background: c.hex, borderColor: c.hex } : {}" />
            <span class="c-name">{{ c.name }}</span>
            <span v-if="(folderMenu.f.color ?? '') === c.id" class="ms c-check">check</span>
          </button>
        </div>
      </div>
      <button v-if="folderMenu.f.parentId" @click="doFolderToRoot"><span class="ms mi">drive_folder_upload</span>移到根目录</button>
      <button class="danger" @click="doFolderDelete"><span class="ms mi">delete</span>删除文件夹</button>
    </div>

    <!-- 未发送附件的图片预览(可缩放/下载) -->
    <Lightbox v-if="lightboxSrc" :src="lightboxSrc" @close="lightboxSrc = ''" />

    <FileBrowser
      v-if="workdirBrowser"
      mode="dir"
      :initial="state?.workingDirectory ?? ''"
      title="选择本会话的工作目录"
      @select="applyWorkdir"
      @cancel="workdirBrowser = false"
    />

    <!-- 确认/输入模态框(重命名/删除等) -->
    <ActionDialog v-if="dlg" v-bind="dlg" @confirm="settle" @close="settle(null)" />

    <!-- 页面级提示/错误 toast:底部居中,自动消失;点击提前关闭 -->
    <div v-if="toast" class="toast" :class="toast.kind" role="status" @click="toast = null">
      <span class="ms">{{ toast.kind === 'err' ? 'cancel' : 'info' }}</span>
      {{ toast.msg }}
    </div>

    <!-- 会话摘要:模态框 + Markdown 渲染(Esc/遮罩/× 关闭) -->
    <ModalDialog v-if="summaryOpen" title="会话摘要" width="720px" @close="summaryOpen = false">
      <div class="md summary-md" v-html="summaryHtml" />
    </ModalDialog>

    <!-- 系统提示词:模态框内原样展示(下一轮实际发给 LLM 的 system 消息)+ 本轮启用的工具及原始描述 -->
    <ModalDialog v-if="spOpen" title="系统提示词" sub="插件注入后的实时结果" width="760px" @close="spOpen = false">
      <div class="sp-text">{{ spText }}</div>
      <div v-if="spTools.length" class="sp-tools">
        <div class="sp-tools-title">工具列表(共 {{ spTools.length }} 项启用,描述为发给模型的原文)</div>
        <div class="sp-tools-box">
          <div v-for="(t, i) in spTools" :key="t.name" class="sp-tool" :class="{ first: i === 0 }">
            <div class="sp-tool-name">{{ t.name }}</div>
            <div class="sp-tool-desc">{{ t.description }}</div>
          </div>
        </div>
      </div>
    </ModalDialog>

    <!-- $warmup 声线预热:模态进度框(进行中不可关;完成短暂展示后自动收起) -->
    <div v-if="warmup" class="warmup-mask" :class="{ done: !!warmup.message }" @click="warmup.message && closeWarmup()">
      <div class="warmup-card">
        <div class="warmup-title">
          <span v-if="!warmup.message" class="ms spin">settings</span>
          <span v-else-if="warmup.message.startsWith('预热失败')" class="ms">cancel</span>
          {{ warmup.message || (warmup.done ? `正在预热 (${warmup.done}/${warmup.total})` : '正在预热…') }}
        </div>
        <div class="warmup-bar">
          <div class="warmup-fill" :style="{ width: warmupPct + '%' }" />
        </div>
        <div v-if="!warmup.message && warmup.current" class="warmup-sub">{{ warmup.current }}</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.chat {
  height: 100%;
  display: flex;
  flex-direction: column;
  /* 窗体背景层用负 z-index 垫底: isolation 使其不越出本容器(否则会沉到应用底色之下不可见) */
  position: relative;
  isolation: isolate;
}

/* 窗体背景:铺满整个聊天窗,位于一切内容之下;侧栏/顶栏/输入卡为半透明面板可透出 */
.win-bg {
  position: absolute;
  inset: 0;
  z-index: -1;
  background-position: center;
  pointer-events: none;
}



.body-row {
  flex: 1;
  display: flex;
  min-height: 0;
}

.main-col {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  position: relative;
}

/* 归档预览被删空的留白区:占满主列,无任何内容(状态栏保留维持底部骨架) */
.arch-blank {
  flex: 1;
}

/* 聊天区背景:铺在消息与输入卡之下 */
.chat-bg {
  position: absolute;
  inset: 0;
  background-position: center;
  pointer-events: none;
}

.messages {
  position: relative;
}

.side {
  width: 300px;
  flex: none;
  /* 定位+层级:窗口缩到最小时输入卡内控件可能溢出,侧栏须盖在溢出内容之上 */
  position: relative;
  z-index: 1;
  background: var(--bg-panel-translucent);
}

/* 侧栏拖动把手:5px 窄条(左右各一条),悬停/拖动中显浅色条;
   拖动期间(body.resizing-h)全局锁定光标与文本选中,指针捕获保证跨区拖动连续 */
.resizer {
  flex: none;
  width: 5px;
  cursor: col-resize;
  touch-action: none;
  z-index: 2;
  position: relative;
}

/* 把手全高横贯,状态栏(1px 上边线 + 28px 体)只到主列边缘:底部用同款"线+体"垫块补齐,
 * 否则状态栏与左右侧栏之间露出 5px 异色缝、且其上边框线在把手处断开。
 * 尺寸须与 StatusBar(height 28px + border-top 1px)同步 */
.resizer::after {
  content: '';
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  height: 28px;
  background: var(--bg-panel);
  border-top: 1px solid var(--border);
}

.resizer:hover,
.resizer:active {
  background: var(--selection);
}

.resizer:hover::after,
.resizer:active::after {
  background: var(--selection);
}

body.resizing-h,
body.resizing-h * {
  cursor: col-resize !important;
  user-select: none !important;
}

.top {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 14px;
  background: var(--bg-panel-translucent);
  border-bottom: 1px solid var(--border);
}

.icon-btn {
  flex: none;
  width: 30px;
  height: 28px;
  background: transparent;
  border: none;
  color: var(--fg-secondary);
  border-radius: 6px;
  cursor: pointer;
  font-size: 14.3px;
}

.icon-btn:hover {
  background: var(--selection);
  color: var(--fg-primary);
}

.icon-btn .ms {
  font-size: 18.7px;
}

/* 危险图标键:默认同普通键,悬停转危险色(与 .arch-btn.danger 一致) */
.icon-btn.danger:hover {
  background: var(--danger-bg);
  color: var(--danger);
}

.meta {
  flex: 1;
  min-width: 0;
}

.title {
  font-size: 16.5px;
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ctx-ring {
  display: block;
}

/* ───── 上下文圆环悬停浮层:容量明细 + 进度条(悬停区用普通光标) ───── */

.ctx-wrap {
  position: relative;
  display: inline-flex;
  flex: none;
  cursor: default;
}

.ctx-tip {
  display: none;
  position: absolute;
  bottom: calc(100% + 10px);
  right: -12px;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 8px 12px 9px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
  white-space: nowrap;
  z-index: 90;
}

.ctx-wrap:hover .ctx-tip {
  display: block;
}

.ctx-tip-head {
  font-size: 11px;
  color: var(--fg-muted);
  margin-bottom: 3px;
}

.ctx-tip-row {
  font-size: 13.2px;
  color: var(--fg-primary);
}

.ctx-tip-bar {
  margin-top: 6px;
  height: 4px;
  width: 176px;
  border-radius: 2px;
  background: var(--border);
  overflow: hidden;
}

.ctx-tip-bar span {
  display: block;
  height: 100%;
  border-radius: 2px;
  transition: width 0.2s;
}

/* ───── 左侧会话栏:264px 半透明列;条目 = 状态点 + 标题 + 相对时间 ───── */

.session-col {
  width: 264px;
  flex: none;
  display: flex;
  flex-direction: column;
  background: var(--bg-panel-translucent);
  border-right: 1px solid var(--border);
}

.session-top {
  padding: 12px;
}

/* 新对话按钮:无边框纯文字态(覆盖 ghost 的描边与底色),悬停浅底与会话行一致,内容靠左 */
.session-top .wide-btn {
  border: none;
  background: transparent;
  justify-content: flex-start;
}

.session-top .wide-btn:hover {
  background: var(--selection);
}

.session-top .wide-btn:active {
  background: var(--bg-panel-hover);
}

.wide-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  width: 100%;
  box-sizing: border-box;
  text-align: center;
}

.wide-btn .ms {
  font-size: 16.5px;
}

.session-scroll {
  position: relative; /* 回根指示线的定位基准(绝对定位叠加在视口底部) */
  flex: 1;
  overflow-y: auto;
  overflow-x: hidden; /* 行宽已按容器精确计算,横向永不需要滚动:显式藏掉,防 1px 舍入/新行撑宽再冒出横条 */
  padding: 4px 0;
}

/* 列表底部空白区的落点指示线:指针悬在可见内容底缘之下时出现 = 落这里回根组(与行缘插入线同色)。
 * 绝对定位叠加在滚动视口底部:不进文档流,不改变 scrollHeight(防出现/消失自激闪烁) */
.drop-root-line {
  position: absolute;
  left: 6px;
  right: 6px;
  bottom: 2px;
  height: 2px;
  border-radius: 1px;
  background: var(--fg-accent);
  pointer-events: none;
}

/* 列表行基础样式(.session-item/.s-title/.s-time/.dot 等)已抽到全局 assets/list-rows.css,
 * 置顶行与 SidebarTree 递归行共用 */

.arch-item {
  display: flex;
  align-items: center;
  gap: 8px;
  /* 行是 content-box:width 不含 padding,不加 border-box 时 padding+margin 会把行撑出容器 16px,永远拖着一条横滚条 */
  box-sizing: border-box;
  width: calc(100% - 12px);
  margin: 2px 6px;
  padding: 5px 8px;
  border-radius: 6px;
  cursor: pointer;
  user-select: none;
}

.arch-item:hover {
  background: var(--selection);
}

.arch-item.sel {
  background: var(--selection);
}

.arch-ico {
  flex: none;
  font-size: 14px;
  color: var(--fg-muted);
}

.arch-actions {
  display: flex;
  gap: 2px;
  flex: none;
}

/* 归档条目行尾无边框图标按钮:底色透明,悬停浅底;删除悬停走危险色 */
.arch-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: none;
  background: transparent;
  color: var(--fg-secondary);
  cursor: pointer;
  padding: 3px;
  border-radius: 6px;
  min-width: 22px;
  min-height: 22px;
}

.arch-btn:hover {
  background: var(--selection);
  color: var(--fg-primary);
}

.arch-btn.danger:hover {
  background: var(--danger-bg);
  color: var(--danger);
}

.arch-btn .ms {
  font-size: 15px;
}

.s-empty {
  color: var(--fg-muted);
  text-align: center;
  padding: 24px 12px;
  font-size: 13.2px;
}

/* 文件夹行/竖线/拖拽态/.kids 展开收起过渡 = 全局 assets/list-rows.css(SidebarTree 共用) */

.pin-label {
  color: var(--fg-muted);
  font-size: 11px;
  padding: 6px 14px 2px;
  user-select: none;
}

.pin-ico {
  flex: none;
  font-size: 13.2px;
  color: var(--fg-accent);
  display: inline-flex;
}

.session-bottom {
  padding: 10px 12px;
  display: flex;
  align-items: center;
  gap: 4px;
  border-top: 1px solid var(--border);
}

.session-bottom .icon-btn {
  width: 28px;
  height: 26px;
}

.session-bottom .icon-btn.on {
  background: var(--selection);
  color: var(--fg-accent);
}

.agent-id {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  flex: 1;
}

.agent-id .avatar {
  width: 26px;
  height: 26px;
  border-radius: 50%;
  background: var(--accent);
  color: #fff;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 14.3px;
  flex: none;
}

.agent-id .avatar-img {
  object-fit: cover;
}

/* ───── ＋ 附件类型菜单(文件/图片/视频/音频/PDF,按模型媒体类型配置) ───── */

.attach-wrap {
  position: relative;
}

.attach-pop {
  position: absolute;
  bottom: calc(100% + 8px);
  left: 0;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  border-radius: 10px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
  padding: 5px;
  min-width: 180px;
  z-index: 70;
}

.attach-pop button {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  box-sizing: border-box; /* 与 .input-card 同理:content-box 会把 padding 撑出弹层 */
  background: transparent;
  border: none;
  color: var(--fg-primary);
  text-align: left;
  padding: 8px 12px;
  font-size: 13.2px;
  font-family: var(--font);
  cursor: pointer;
  border-radius: 6px;
  white-space: nowrap;
}

.attach-pop button:hover {
  background: var(--selection);
}

.attach-pop .ms {
  flex: none;
  font-size: 16.5px;
  color: var(--fg-secondary);
}

.agent-name {
  font-size: 14.3px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 会话右键菜单 */
.s-menu {
  position: fixed;
  z-index: 200;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 4px;
  min-width: 140px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
}

.s-menu button {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  box-sizing: border-box; /* 与 .input-card 同理:content-box 会把 padding 撑出弹层 */
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

/* 图标固定宽列:所有菜单项文字从同一起点开始 */
.s-menu .mi {
  flex: none;
  width: 17px;
  font-size: 17.6px;
  text-align: center;
  color: var(--fg-secondary);
}

.s-menu button.danger .mi {
  color: var(--danger);
}

.s-menu button:hover {
  background: var(--selection);
}

.s-menu button.danger {
  color: var(--danger);
}

/* "移入"子菜单:锚在主菜单右侧(贴右缘时翻到左侧),与触发项顶部对齐 */
.s-menu .sub {
  position: relative;
}

.s-menu .chev {
  flex: none;
  margin-left: auto;
  font-size: 18px;
  color: var(--fg-secondary);
}

.s-menu .sub-menu {
  position: absolute;
  top: -4px;
  left: calc(100% + 6px);
  min-width: 150px;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 4px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
}

.s-menu .sub-menu.left {
  left: auto;
  right: calc(100% + 6px);
}

/* 修改颜色子菜单:色点 + 名称 + 当前选中对勾 */
.s-menu .c-dot {
  flex: none;
  width: 13px;
  height: 13px;
  border-radius: 50%;
  border: 1.5px solid var(--fg-secondary);
  display: inline-block;
}

.s-menu .c-name {
  flex: 1;
}

.s-menu .c-check {
  flex: none;
  margin-left: auto;
  font-size: 18px;
  color: var(--fg-accent);
}

.cmd-item.sel,
.cmd-item:hover {
  background: var(--selection);
}

.menu-wrap {
  position: relative;
}

.menu {
  position: absolute;
  right: 0;
  top: 34px;
  background: var(--bg-panel);
  border: 1px solid var(--border);
  border-radius: 8px;
  display: flex;
  flex-direction: column;
  min-width: 150px;
  z-index: 30;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
}

.menu button {
  display: flex;
  align-items: center;
  gap: 9px;
  background: transparent;
  border: none;
  color: var(--fg-primary);
  text-align: left;
  padding: 9px 14px;
  font-size: 14.3px;
  cursor: pointer;
}

.menu .ms {
  flex: none;
  width: 17px;
  font-size: 17.6px;
  text-align: center;
  color: var(--fg-secondary);
}

.menu button.danger .ms {
  color: var(--danger);
}

.menu button:hover {
  background: var(--bg-panel-hover);
}

.menu button:disabled {
  opacity: .45;
  pointer-events: none;
}

.menu button.danger {
  color: var(--danger);
}

/* 页面级提示/错误 toast:聊天区中央;
   err = 红描边红图标 6s,ok = 常规描边灰图标 3.5s */
.toast {
  position: fixed;
  left: 50%;
  top: 50%;
  transform: translate(-50%, -50%);
  z-index: 1000;
  display: flex;
  align-items: center;
  gap: 8px;
  max-width: min(70vw, 560px);
  padding: 10px 16px;
  border-radius: 10px;
  background: var(--bg-panel);
  border: 1px solid var(--border);
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.28);
  color: var(--fg-primary);
  font-size: 14.3px;
  line-height: 1.4;
  cursor: pointer;
  animation: toast-in 0.2s ease-out;
}

.toast .ms {
  flex: none;
  font-size: 19.8px;
  color: var(--fg-muted);
}

.toast.err {
  border-color: var(--danger);
}

.toast.err .ms {
  color: var(--danger);
}

@keyframes toast-in {
  from {
    opacity: 0;
    transform: translate(-50%, -46%);
  }
  to {
    opacity: 1;
    transform: translate(-50%, -50%);
  }
}

/* $warmup 预热进度:全屏遮罩 + 居中进度卡(规格同 WPF 侧模糊遮罩的进度卡) */
.warmup-mask {
  position: fixed;
  inset: 0;
  z-index: 2700;
  background: rgba(0, 0, 0, 0.45);
  display: flex;
  align-items: center;
  justify-content: center;
}

.warmup-card {
  width: min(420px, 86vw);
  padding: 22px 24px 18px;
  background: var(--bg-panel);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: 0 16px 48px rgba(0, 0, 0, 0.5);
}

.warmup-mask.done .warmup-card {
  cursor: pointer;
}

.warmup-title {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  font-size: 15.4px;
  font-weight: 600;
  color: var(--fg-primary);
}

.warmup-title .ms {
  font-size: 20px;
  color: var(--fg-muted);
}

.warmup-mask.done .warmup-title .ms {
  color: var(--success);
}

.warmup-mask.done .warmup-title .ms {
  font-size: 20px;
}

.warmup-bar {
  height: 10px;
  margin-top: 16px;
  border-radius: 5px;
  background: var(--selection);
  overflow: hidden;
}

.warmup-fill {
  height: 100%;
  border-radius: 5px;
  background: var(--accent);
  transition: width 0.25s ease;
}

.warmup-sub {
  margin-top: 12px;
  font-size: 12.1px;
  color: var(--fg-muted);
  text-align: center;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* 摘要模态框内的 Markdown 正文:规格同消息气泡的 .md(组件 Teleport 到 body,
   但插槽内容带本页 scope 属性,scoped :deep 可命中) */
.summary-md {
  word-break: break-word;
}

.summary-md :deep(p) {
  margin: 6px 0;
}

.summary-md :deep(pre) {
  background: var(--bg-code);
  border-radius: 6px;
  padding: 10px 12px;
  overflow-x: auto;
  font-size: 13.2px;
}

.summary-md :deep(code) {
  background: var(--bg-code);
  border-radius: 4px;
  padding: 1px 5px;
  font-size: 13.2px;
}

.summary-md :deep(pre code) {
  background: transparent;
  padding: 0;
}

.summary-md :deep(a) {
  color: var(--fg-accent);
}

.summary-md :deep(table) {
  border-collapse: collapse;
  margin: 8px 0;
}

.summary-md :deep(th),
.summary-md :deep(td) {
  border: 1px solid var(--border);
  padding: 4px 10px;
}

.summary-md :deep(blockquote) {
  border-left: 3px solid var(--border);
  margin: 6px 0;
  padding: 2px 12px;
  color: var(--fg-secondary);
}

/* 系统提示词:原样纯文本(与发给 LLM 的逐字一致),代码块底 */
.sp-text {
  margin: 0;
  padding: 12px 14px;
  background: var(--bg-code);
  border-radius: 8px;
  font-size: 13.2px;
  line-height: 1.55;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: var(--font-mono, ui-monospace, Consolas, monospace);
  color: var(--fg-primary);
}

.sp-tools-title {
  font-size: 12.1px;
  color: var(--fg-muted);
}

/* 工具列表:单一容器,项间只用一条分隔线(避免逐卡描边在视觉上重复堆叠) */
.sp-tools-box {
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--bg-panel);
  overflow: hidden;
}

.sp-tool {
  padding: 8px 12px;
  border-top: 1px solid var(--border);
}

.sp-tool.first {
  border-top: none;
}

.sp-tool-name {
  font-family: var(--font-mono, ui-monospace, Consolas, monospace);
  font-size: 13.2px;
  font-weight: 600;
  color: var(--fg-primary);
}

.sp-tool-desc {
  margin-top: 4px;
  font-size: 12.1px;
  line-height: 1.5;
  color: var(--fg-secondary);
  white-space: pre-wrap;
  word-break: break-word;
}

/* 消息列:整体居中,单列最大 1056px(与输入卡同宽,仿主流聊天页) */
.messages {
  flex: 1;
  overflow-y: auto;
  padding: 8px 4px 8px 0;
  display: flex;
  flex-direction: column;
  align-items: center;
}

/* 冻结态:钉底定位完成前隐藏消息区(切换会话/返回对话时避免滚动跳变闪烁) */
.messages.frozen {
  opacity: 0;
}


/* ───── 输入卡片:半透明面板 + 圆角12;无边框输入 + 工具栏行(同桌面端) ───── */

.input-card {
  position: relative;
  display: flex;
  flex-direction: column;
  margin: 8px auto 10px;
  width: 100%;
  max-width: 1056px;
  box-sizing: border-box; /* 项目无全局 border-box:content-box 下 padding+border 把卡片撑出主列 22px,压进右侧栏 */
  padding: 8px 10px;
  background: var(--bg-panel-translucent);
  border: 1px solid var(--border);
  border-radius: 12px;
}

/* ───── 中途消息队列:输入卡片顶部的排队插话条 ───── */

.queue-panel {
  margin: -2px 0 6px;
  padding-bottom: 6px;
  border-bottom: 1px dashed var(--border);
}

.queue-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11.5px;
  color: var(--fg-secondary);
  margin-bottom: 5px;
}

.queue-head > .ms {
  font-size: 14px;
  color: var(--fg-accent);
}

.queue-count {
  background: var(--fg-accent);
  color: #fff;
  border-radius: 8px;
  padding: 0 6px;
  font-size: 10.5px;
  line-height: 15px;
}

.queue-item {
  position: relative;
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 5px 8px;
  border-radius: 8px;
  margin-bottom: 3px;
  background: var(--bg-input);
  border: 1px solid transparent;
  transition: border-color 0.15s;
}

.queue-item:hover {
  border-color: var(--border);
}

/* 拖拽落点指示:目标行上/下缘一条强调线(上半=插到该行前,下半=插到其后) */
.queue-item::after {
  content: '';
  position: absolute;
  left: 6px;
  right: 6px;
  height: 2px;
  border-radius: 1px;
  background: var(--fg-accent);
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.1s;
}

.queue-item.over-top::after {
  top: -2px;
  opacity: 1;
}

.queue-item.over-bot::after {
  bottom: -2px;
  opacity: 1;
}

/* 行首拖拽把手 */
.queue-grip {
  flex: none;
  font-size: 14px;
  line-height: 18px;
  color: var(--fg-secondary);
  opacity: 0.45;
  cursor: grab;
  user-select: none;
}

.queue-grip:hover {
  opacity: 0.9;
}

.queue-grip:active {
  cursor: grabbing;
}

/* 命令样条目:左侧强调条 + terminal 徽标,一眼区分"会按命令触发" */
.queue-item.cmd {
  box-shadow: inset 2px 0 0 var(--fg-accent);
}

/* "本轮后发送"条目:左侧警示条 + meta 标签,与命令(本轮后触发)区分 */
.queue-item.defer {
  box-shadow: inset 2px 0 0 var(--warning);
}

.queue-flag {
  color: var(--warning);
}

.queue-seq {
  min-width: 14px;
  text-align: center;
  font-size: 11px;
  line-height: 18px;
  color: var(--fg-secondary);
  opacity: 0.75;
}

.queue-cmd {
  font-size: 14px;
  line-height: 18px;
  color: var(--fg-accent);
}

.queue-main {
  flex: 1;
  min-width: 0;
}

.queue-text {
  font-size: 12.5px;
  line-height: 18px;
  color: var(--fg-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.queue-meta {
  display: flex;
  gap: 8px;
  font-size: 11px;
  color: var(--fg-secondary);
}

.queue-acts {
  display: flex;
  gap: 2px;
  flex: none;
}

/* 无边框图标按钮:底色透明,悬停出浅底;取消悬停走危险色 */
.queue-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: none;
  background: transparent;
  color: var(--fg-secondary);
  cursor: pointer;
  padding: 3px;
  border-radius: 6px;
  min-width: 22px;
  min-height: 22px;
}

.queue-btn:hover {
  background: var(--selection);
  color: var(--fg-primary);
}

/* "本轮后发送"标记点亮:强调色 + 实心图标 */
.queue-btn.on {
  color: var(--fg-accent);
}

.queue-btn.danger:hover {
  background: var(--danger-bg);
  color: var(--danger);
}

.queue-btn .ms {
  font-size: 15px;
}

.attach-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 6px;
}

.attach {
  position: relative;
}

.attach-thumb {
  width: 52px;
  height: 52px;
  object-fit: cover;
  border-radius: 6px;
  border: 1px solid var(--border);
}

.attach-file {
  display: inline-block;
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 8px 10px;
  font-size: 13.2px;
  max-width: 220px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.attach-file .ms {
  font-size: 13.2px;
  color: var(--fg-secondary);
}

.attach-click {
  cursor: pointer;
}

.attach-click:hover {
  border-color: var(--fg-accent);
}

.attach-size {
  color: var(--fg-muted);
  margin-left: 6px;
}

.attach-remove {
  position: absolute;
  top: -6px;
  right: -6px;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  border: none;
  background: var(--danger);
  color: #fff;
  padding: 0;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
}

.attach-remove .ms {
  font-size: 13.2px;
}

.attach-file .ms {
  font-size: 13.2px;
  color: var(--fg-secondary);
}

.clip,
.file-input {
  display: none;
}

/* 工具栏行:左(附件/工作目录/权限) | 右(上下文/模型/思考/发送) */
.toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
  flex-wrap: wrap;
}

.tb-left {
  display: flex;
  align-items: center;
  gap: 6px;
  flex: 1;
  min-width: 0;
  flex-wrap: wrap;
}

.tb-right {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

/* 运行指示:射线环形 spinner(形态同参考图)——16px 圈内 8 根细射线(45° 布圈,间隙充足),
   各自错峰淡出,透明度波纹顺时针流转;纯 CSS,零字体依赖 */
.turn-spin {
  position: relative;
  display: inline-block;
  width: 16px;
  height: 16px;
}

.turn-spin i {
  position: absolute;
  left: 50%;
  top: 1px;
  width: 1.5px;
  height: 4.5px;
  margin-left: -0.75px;
  background: color-mix(in srgb, var(--fg-primary) 80%, #000); /* 参考图的中性亮色压暗一档:纯白偏亮,混 20% 黑稍沉 */
  border-radius: 1px;
  transform-origin: 0.75px 7px; /* 射线根部指向的圆心 */
  animation: turn-wave 1s linear infinite;
}

@keyframes turn-wave {
  from {
    opacity: 1;
  }
  to {
    opacity: 0.12;
  }
}

/* 运行指示行:消息列末行,占位尺寸对齐消息行(.msg),与工具栏工位同款 */
.turn-row {
  flex: none;
  width: 100%;
  max-width: 1056px;
  box-sizing: border-box; /* 与 .input-card 同理:窄列下 padding 会把行撑出主列 */
  padding: 12px 18px;
}

.pill {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  color: var(--fg-primary);
  border-radius: 6px;
  padding: 5px 12px;
  font-size: 12.1px;
  cursor: pointer;
  font-family: var(--font);
  max-width: 260px;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.pill:hover:not(:disabled) {
  border-color: var(--fg-accent);
}

.pill-ico {
  font-size: 14.3px;
  color: var(--fg-secondary);
}

.pill:disabled {
  opacity: 0.45;
  cursor: default;
}

.wd-clear {
  color: var(--fg-muted);
  cursor: pointer;
  display: inline-flex;
}

.wd-clear .ms {
  font-size: 13.2px;
}

.wd-clear:hover {
  color: var(--danger);
}

.ctx {
  color: var(--fg-secondary);
  font-size: 12.1px;
  white-space: nowrap;
}

/* ───── 裸样式控件:无边框,悬停浮起;配合前置图标(锁/脑) ───── */

.icon-plain {
  flex: none;
  width: 28px;
  height: 26px;
  background: transparent;
  border: none;
  color: var(--fg-primary);
  font-size: 16.5px;
  border-radius: 6px;
  cursor: pointer;
}

.icon-plain:hover {
  background: var(--selection);
}

.icon-plain:disabled {
  opacity: .45;
  pointer-events: none;
}

.icon-plain .ms {
  font-size: 19.8px;
}

.bare-wrap {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  border-radius: 6px;
}

.bare-wrap:hover {
  background: var(--selection);
}

.lock-ico {
  flex: none;
  font-size: 15.4px;
  color: var(--fg-primary);
}

/* 权限档位配色:只读=正文色(深色主题下即白),读写=蓝,完全访问=橙,全部询问=绿 */
.lock-ico.perm-rw {
  color: #4d9fff;
}

.lock-ico.perm-full {
  color: #ffa94d;
}

.lock-ico.perm-ask {
  color: #43c17e;
}

.bare-wrap :deep(.asel) {
  background: transparent;
  border: none;
  height: 26px;
}

.bare-wrap :deep(.asel-text) {
  font-size: 13.2px;
  padding: 0 20px 0 2px;
}

/* ───── 模型级联菜单 ───── */

.model-menu {
  position: relative;
}

.bare-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  background: transparent;
  border: none;
  color: var(--fg-primary);
  font-size: 13.2px;
  padding: 5px 8px;
  border-radius: 6px;
  cursor: pointer;
  font-family: var(--font);
  max-width: 240px;
}

.bare-btn:hover:not(:disabled) {
  background: var(--selection);
}

.bare-btn:disabled {
  opacity: 0.45;
  cursor: default;
}

.bare-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bare-label.ph {
  color: var(--fg-muted);
}

.chev-d {
  color: var(--fg-secondary);
  font-size: 15.4px;
}

.m-pop {
  position: absolute;
  bottom: calc(100% + 8px);
  right: 0;
  width: 224px;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  border-radius: 10px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
  padding: 5px;
  z-index: 60;
  /* 不能设 overflow/max-height:会裁掉供应商的右展子菜单 */
  overflow: visible;
}

.m-current {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 7px 10px;
  font-size: 13.2px;
  border-radius: 6px;
  background: var(--selection);
}

.m-check {
  color: var(--fg-accent);
  width: 14px;
  flex: none;
  display: inline-flex;
}

.m-check .ms {
  font-size: 14.3px;
}

.m-cur-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.m-cur-prov {
  margin-left: auto;
  color: var(--fg-muted);
  font-size: 12.1px;
  flex: none;
}

.m-sep {
  height: 1px;
  background: var(--border);
  margin: 5px 4px;
}

.m-prov-row {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 7px 10px;
  font-size: 13.2px;
  border-radius: 6px;
  cursor: pointer;
}

.m-prov-row:hover {
  background: var(--selection);
}

.m-arrow {
  color: var(--fg-muted);
  display: inline-flex;
}

.m-arrow .ms {
  font-size: 15.4px;
}

.m-sub {
  position: absolute;
  /* 菜单锚在屏幕右下:向左展开避免超出视口 */
  right: calc(100% - 4px);
  bottom: -6px;
  min-width: 240px;
  max-height: 300px;
  overflow-y: auto;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  border-radius: 10px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
  padding: 5px;
  z-index: 61;
}

.m-model {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 7px 10px;
  font-size: 13.2px;
  border-radius: 6px;
  cursor: pointer;
}

.m-model:hover {
  background: var(--selection);
}

.m-model.cur {
  color: var(--fg-accent);
}

.m-empty {
  padding: 8px 10px;
  color: var(--fg-muted);
  font-size: 12.1px;
}

.m-manage {
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 7px 10px;
  font-size: 13.2px;
  border-radius: 6px;
  cursor: pointer;
}

.m-manage .ms {
  font-size: 15.4px;
  color: var(--fg-secondary);
}

.m-manage:hover {
  background: var(--selection);
}

/* ───── 方形发送钮(流式中变红 = 停止) ───── */

.send-sq {
  flex: none;
  width: 30px;
  height: 30px;
  background: var(--bg-input);
  border: 1px solid var(--border);
  color: var(--fg-primary);
  border-radius: 8px;
  font-size: 13.2px;
  cursor: pointer;
}

.send-sq:hover:not(:disabled) {
  border-color: var(--fg-accent);
}

.send-sq .ms {
  font-size: 15.4px;
}

.send-sq:disabled {
  opacity: 0.45;
  cursor: default;
}

.send-sq.stop {
  background: var(--danger);
  border-color: var(--danger);
  color: #fff;
}

/* 运行中的"入队"按钮:方形图标钮加宽容纳文字(入中途消息队列) */
.send-sq.queue-send {
  width: auto;
  padding: 0 8px;
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.cmd-popup {
  position: absolute;
  bottom: calc(100% + 4px);
  left: 10px;
  right: 10px;
  background: var(--bg-panel);
  border: 1px solid var(--border);
  border-radius: 8px;
  max-height: 260px;
  overflow-y: auto;
  z-index: 20;
  box-shadow: 0 -8px 24px rgba(0, 0, 0, 0.35);
}

/* ───── ask 询问面板:锚定输入卡片底边向上生长,盖住输入框与工具栏 ───── */

.ask-panel {
  position: absolute;
  left: -1px;
  right: -1px;
  bottom: -1px;
  z-index: 30;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px 10px;
  background: var(--bg-panel);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: 0 -8px 24px rgba(0, 0, 0, 0.35);
  outline: none;
  max-height: 62vh;
}

.ask-head {
  display: flex;
  align-items: baseline;
  gap: 8px;
  min-width: 0;
}

.ask-title {
  font-weight: 600;
  font-size: 14.3px;
  flex: none;
}

.ask-question {
  font-size: 14.3px;
  min-width: 0;
  overflow-wrap: anywhere;
}

.ask-multi {
  font-size: 13.2px;
  color: var(--fg-secondary);
  flex: none;
}

.ask-pager {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: 2px;
  flex: none;
}

.ask-page-btn {
  background: none;
  border: none;
  color: var(--fg-secondary);
  cursor: pointer;
  padding: 2px;
  display: inline-flex;
  border-radius: 4px;
}

.ask-page-btn:hover:not(:disabled) {
  background: var(--bg-panel-hover);
  color: var(--fg-primary);
}

.ask-page-btn:disabled {
  opacity: 0.35;
  cursor: default;
}

.ask-page-num {
  font-size: 13.2px;
  color: var(--fg-secondary);
  min-width: 30px;
  text-align: center;
}

.ask-options {
  display: flex;
  flex-direction: column;
  gap: 4px;
  overflow-y: auto;
  min-height: 0;
}

.ask-opt {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 10px;
  border-radius: 8px;
  cursor: pointer;
  font-size: 14.3px;
  border: 1px solid transparent;
}

.ask-opt:hover {
  background: var(--bg-panel-hover);
}

/* 键盘光标行:描边高亮;选中行:底色加深 + 标题加粗 */
.ask-opt.cur {
  border-color: var(--accent);
}

.ask-opt.sel {
  background: var(--selection);
}

.ask-opt.sel .ask-label {
  font-weight: 600;
}

.ask-num {
  color: var(--fg-secondary);
  flex: none;
  width: 18px;
}

.ask-check {
  flex: none;
  width: 15px;
  height: 15px;
  border: 1.5px solid var(--border);
  border-radius: 4px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: var(--bg-base);
}

.ask-check.on {
  background: var(--accent);
  border-color: var(--accent);
  color: #fff;
}

.ask-check .ms {
  font-size: 13.2px;
}

.ask-label {
  flex: none;
  max-width: 55%;
}

.ask-desc {
  color: var(--fg-secondary);
  font-size: 13.2px;
  min-width: 0;
  overflow-wrap: anywhere;
}

.ask-rec {
  color: var(--fg-secondary);
  font-size: 13.2px;
  flex: none;
}

.ask-other-input {
  flex: 1;
  background: none;
  border: none;
  outline: none;
  color: var(--fg-primary);
  font-size: 14.3px;
  font-family: inherit;
  min-width: 0;
}

.ask-other-input::placeholder {
  color: var(--fg-secondary);
}

.ask-foot {
  display: flex;
  align-items: center;
  gap: 10px;
}

.ask-hint {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--fg-secondary);
  font-size: 13.2px;
  min-width: 0;
}

.ask-hint .ms {
  font-size: 15.4px;
}

.ask-skip {
  margin-left: auto;
  background: none;
  border: 1px solid var(--border);
  border-radius: 8px;
  color: var(--fg-secondary);
  padding: 4px 14px;
  font-size: 13.2px;
  cursor: pointer;
}

.ask-skip:hover {
  color: var(--fg-primary);
  background: var(--bg-panel-hover);
}

.ask-continue {
  background: var(--fg-primary);
  color: var(--bg-base);
  border: none;
  border-radius: 8px;
  padding: 4px 16px;
  font-size: 13.2px;
  font-weight: 600;
  cursor: pointer;
}

.ask-continue:hover {
  opacity: 0.85;
}

.cmd-item {
  display: flex;
  gap: 10px;
  padding: 8px 12px;
  font-size: 14.3px;
  cursor: pointer;
}

.cmd-item:hover {
  background: var(--selection);
}

.cmd-name {
  color: var(--fg-accent);
  flex: none;
}

.cmd-desc {
  color: var(--fg-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.input {
  flex: 1;
  resize: none;
  background: transparent;
  color: var(--fg-primary);
  border: none;
  padding: 2px;
  font-family: var(--font);
  font-size: 14.3px;
  min-height: 44px;
  max-height: 160px;
  line-height: 1.5;
}

/* 归档只读预览提示条:输入区顶部一条弱提示 */
.arch-banner {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 2px 2px 6px;
  font-size: 12.5px;
  color: var(--fg-muted);
}

.arch-banner .ms {
  font-size: 15px;
}

.input:focus {
  outline: none;
}
</style>
