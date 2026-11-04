<script setup lang="ts">
// 设置页:分类与布局对齐桌面端 SettingsView(每张设置卡 620px、标签在控件上方、
// 开关卡 = 胶囊开关 + 右侧文字),控件全部定制(下拉 = AppSelect,列表 = 蓝色选中 ListBox 规格)。
// - 自动保存:任何更改 600ms 防抖后整份 PUT(热更 + 落盘)
// - 桌宠/TTS 按引擎切换布局:只显示当前引擎的设置;气泡设置并入桌宠页(桌面端同布局)
// - 供应商:列表选中即生效(同桌面端),一次编辑一个服务
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import {
  avatarUrl, clearLive2dFallback, enterLive2dEditor, exitLive2dEditor, getAgents, getBiliWhitelistCandidates, getLive2dParts,
  getLive2dValues, getPetMotions, getSettings, getToolGroups, getTools, loadLive2dExpression, newLive2dExpression, playPetMotion,
  previewBubble, putSettings, removeAvatar, resetLive2dParams, saveLive2dExpression, saveLive2dFallback, deleteLive2dExpression,
  setLive2dEditorPart, setLive2dParam, setLive2dPartOpacity, setToolGroupEnabled, setToolEnabled, uploadAvatar,
  getHiddenTools,
  createSession,
  deleteBackground,
  getCompressPresets,
  getLastSession,
  getSessions,
  getSystemFonts,
  uploadBackground,
  getExternalPlugins,
  saveExternalPluginSettings,
  setExternalPluginEnabled,
  resetExternalPlugin,
  getSkills, getSkill, saveSkill, deleteSkill, setSkillEnabled,
  getMcpStatus,
  type BiliWhitelistCandidate, type CompressPresetInfo, type ExternalPluginInfo, type ExternalSettingInfo, type Live2dExpr, type Live2dParam, type Live2dPart, type McpServerStatus, type PetMotionInfo, type SkillEntry, type ToolGroupInfo, type ToolInfo,
} from '../api/client'
import AppSelect, { type SelectOption } from '../components/AppSelect.vue'
import ModalDialog from '../components/ModalDialog.vue'
import FileBrowser from '../components/FileBrowser.vue'
import PluginUiFrame from '../components/PluginUiFrame.vue'
import { currentTheme, toggleTheme, type ThemeMode } from '../theme/theme'
import { cachedLastChat } from '../store/sessionCache'
import { applyUiScale } from '../lib/uiScale'
import { useActionDialog } from '../lib/actionDialog'
import ActionDialog from '../components/ActionDialog.vue'

const props = defineProps<{ agentId: string; initialTab?: string }>()

type FieldType = 'text' | 'textarea' | 'number' | 'bool' | 'select' | 'dir' | 'file' | 'paths' | 'json' | 'slider' | 'percent' | 'color' | 'font'
type Opt = string | [string, string]
interface Field {
  path: string
  label: string
  type: FieldType
  options?: Opt[]
  exts?: string[]
  hint?: string
  min?: number
  max?: number
  step?: number
  w?: number
}
interface Category {
  key: string
  title: string
  sub?: string
  single?: boolean
  fields: Field[]
}

const settings = ref<any>(null)
const groups = ref<ToolGroupInfo[]>([])
const error = ref('')
const active = ref('agent')

// ───────── 全局 toast:固定位置,淡入淡出,自动消失;ok/warn/err 三态着色 ─────────

type ToastKind = 'ok' | 'warn' | 'err'
const toastMsg = ref('')
const toastKind = ref<ToastKind>('ok')
const toastVisible = ref(false)
let toastHideTimer: number | null = null
let toastClearTimer: number | null = null

function showToast(msg: string, kind: ToastKind = 'ok') {
  if (!msg) return
  if (toastHideTimer !== null) window.clearTimeout(toastHideTimer)
  if (toastClearTimer !== null) window.clearTimeout(toastClearTimer)
  toastMsg.value = msg
  toastKind.value = kind
  toastVisible.value = true
  const life = kind === 'err' ? 6000 : 3500
  toastHideTimer = window.setTimeout(() => {
    toastVisible.value = false
    toastClearTimer = window.setTimeout(() => {
      toastMsg.value = ''
      toastKind.value = 'ok'
    }, 300)
  }, life)
}

function dismissToast() {
  if (toastHideTimer !== null) window.clearTimeout(toastHideTimer)
  if (toastClearTimer !== null) window.clearTimeout(toastClearTimer)
  if (!toastVisible.value) return
  toastVisible.value = false
  toastClearTimer = window.setTimeout(() => {
    toastMsg.value = ''
    toastKind.value = 'ok'
  }, 300)
}

// 确认模态框(替代浏览器原生 confirm):删除技能/表情等
const { dlg, ask, settle } = useActionDialog()

/** 返回对话:回到该 Agent 上次打开的会话(后端记忆 → localStorage 兜底;记忆丢失/会话已删除才落最近活跃);无会话则新建,失败回主页 */
async function goBackToChat() {
  try {
    const [list, remembered] = await Promise.all([getSessions(props.agentId), getLastSession(props.agentId).catch(() => null)])
    if (list.length) {
      const last = remembered || cachedLastChat(props.agentId)
      const target = (last && list.find((s) => s.sessionId === last)) || list[list.length - 1]
      location.hash = `#/s/${target.sessionId}`
      return
    }
    const created = await createSession(props.agentId)
    location.hash = `#/s/${created.sessionId}`
  } catch {
    location.hash = '#/'
  }
}

// ───────── Agent 头像(上传到服务端 agents/{id}/avatar.{ext};rev 做缓存失效) ─────────

const avatarRev = ref(0)
const avatarUploading = ref(false)
const avatarInput = ref<HTMLInputElement | null>(null)
const avatarSrc = computed(() => (avatarRev.value ? avatarUrl(props.agentId, avatarRev.value) : ''))

async function loadAvatarRev() {
  try {
    const me = (await getAgents()).find((a) => a.id === props.agentId)
    avatarRev.value = me?.avatarRev ?? 0
  } catch {
    // 头像获取失败不阻塞设置页
  }
}

async function onAvatarPicked(e: Event) {
  const input = e.target as HTMLInputElement
  const f = input.files?.[0]
  input.value = ''
  if (!f) return
  if (!f.type.startsWith('image/')) {
    showToast('头像仅支持图片文件(PNG/JPG/WebP/GIF)', 'warn')
    return
  }
  if (f.size > 5 * 1024 * 1024) {
    showToast('头像图片超过 5 MB 上限', 'warn')
    return
  }
  avatarUploading.value = true
  try {
    const dataUrl = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader()
      reader.onload = () => resolve(reader.result as string)
      reader.onerror = () => reject(reader.error)
      reader.readAsDataURL(f)
    })
      const r = await uploadAvatar(props.agentId, dataUrl)
      avatarRev.value = r.rev
      showToast('头像已更新')
    } catch (err) {
      showToast(String((err as Error).message), 'err')
  } finally {
    avatarUploading.value = false
  }
}

async function onAvatarRemove() {
  try {
      await removeAvatar(props.agentId)
      avatarRev.value = 0
      showToast('已移除头像')
    } catch (err) {
      showToast(String((err as Error).message), 'err')
    }
}

// ───────── 外部插件(plugins 目录 DLL;按 [PluginSetting] 描述符自动生成表单) ─────────

const extPlugins = ref<ExternalPluginInfo[]>([])
const extModalName = ref<string | null>(null)
const extDraft = ref<Record<string, unknown>>({})
const extSaving = ref(false)

const extModalPlugin = computed(() => extPlugins.value.find((p) => p.name === extModalName.value) ?? null)

/** 描述符按 Group 分组(保序;未标 Group 归「常规」) */
const extModalGroups = computed(() => {
  const p = extModalPlugin.value
  if (!p) return []
  const groups: { name: string; items: ExternalSettingInfo[] }[] = []
  for (const s of p.settings) {
    const g = s.group || '常规'
    let bucket = groups.find((x) => x.name === g)
    if (!bucket) groups.push((bucket = { name: g, items: [] }))
    bucket.items.push(s)
  }
  return groups
})

async function loadExternalPlugins() {
  try {
    extPlugins.value = await getExternalPlugins(props.agentId)
  } catch {
    extPlugins.value = []
  }
}

/** 描述符 → 编辑控件类型(UiHint 优先,其次按值类型推断) */
function extControlType(s: ExternalSettingInfo): 'bool' | 'number' | 'slider' | 'select' | 'color' | 'password' | 'textarea' | 'json' | 'text' {
  if (s.uiHint === 'slider') return 'slider'
  if (s.uiHint === 'color') return 'color'
  if (s.uiHint === 'password') return 'password'
  if (s.uiHint === 'textarea') return 'textarea'
  if (s.valueType === 'bool') return 'bool'
  if (s.valueType === 'int' || s.valueType === 'double') return 'number'
  if (s.options.length) return 'select'
  if (s.valueType.startsWith('List')) return 'json'
  return 'text'
}

function openExtModal(p: ExternalPluginInfo) {
  const draft: Record<string, unknown> = {}
  for (const s of p.settings) draft[s.name] = s.currentValue ?? (s.valueType === 'bool' ? false : '')
  extDraft.value = draft
  extModalName.value = p.name
  nextTick(() => syncAllRangeFills())
}

/** 多行文本/JSON 列表控件提交:JSON 需可解析,失败提示并保留原值 */
function onExtTextArea(s: ExternalSettingInfo, text: string) {
  if (extControlType(s) === 'json') {
    try {
      extDraft.value[s.name] = text.trim() ? JSON.parse(text) : []
    } catch (e) {
      error.value = `「${s.label || s.name}」不是合法 JSON:${String((e as Error).message)}`
    }
  } else {
    extDraft.value[s.name] = text
  }
}

async function saveExtPlugin() {
  const name = extModalName.value
  if (!name || extSaving.value) return
  extSaving.value = true
  error.value = ''
  try {
    const r = await saveExternalPluginSettings(props.agentId, name, extDraft.value)
    if (!r.ok) {
      error.value = r.error || '保存失败'
      return
    }
    const p = extModalPlugin.value
    if (p) p.settings = r.settings
    extModalName.value = null
  } catch (e) {
    error.value = String((e as Error).message)
  } finally {
    extSaving.value = false
  }
}

async function toggleExtPlugin(p: ExternalPluginInfo, enabled: boolean) {
  try {
    const r = await setExternalPluginEnabled(props.agentId, p.name, enabled)
    if (!r.ok) {
      error.value = r.error || '操作失败'
      return
    }
    p.enabled = enabled
    reloadToolGroups() // 外部插件的工具随启停增删,工具页清单同步刷新
  } catch (e) {
    error.value = String((e as Error).message)
  }
}

/** 外部插件重置:销毁实例 + 清空持久化设置,以默认值重建(保留启停),随后刷新清单与工具页 */
async function resetExtPlugin(p: ExternalPluginInfo) {
  if (!window.confirm(`重置「${p.name}」?其全部设置将清空并回到默认值。`)) return
  try {
    const r = await resetExternalPlugin(props.agentId, p.name)
    if (!r.ok) {
      error.value = r.error || '重置失败'
      return
    }
    error.value = ''
    await loadExternalPlugins()
    reloadToolGroups()
  } catch (e) {
    error.value = String((e as Error).message)
  }
}

// ───────── 外部插件自定义界面(DLL 内嵌 ui.* 资产,同源 iframe + /apet-bridge.js 桥) ─────────

const extUi = ref<{ name: string; sessionId: string } | null>(null)

/** 打开插件自定义界面:会话上下文取该 Agent 最近会话(记忆 → 列表末项;无会话留空,插件页按 Agent 级处理) */
async function openExtUi(p: ExternalPluginInfo) {
  let sid = ''
  try {
    const [list, remembered] = await Promise.all([
      getSessions(props.agentId).catch(() => [] as { sessionId: string }[]),
      getLastSession(props.agentId).catch(() => null as string | null),
    ])
    sid = (remembered && list.find((s) => s.sessionId === remembered)?.sessionId) || list[list.length - 1]?.sessionId || ''
  } catch { /* 上下文缺失不阻断打开 */ }
  extUi.value = { name: p.name, sessionId: sid }
}

// ───────── MCP 两级管理:主页列表 + 新建/编辑(表单/JSON)+ 导入 ─────────

const mcpEditor = ref<any | null>(null)
const mcpEditorIndex = ref(-1)
const mcpEditorJsonMode = ref(false)
const mcpEditorDraft = ref('')
const mcpImportOpen = ref(false)
const mcpImportText = ref('')

function openMcpEditor(i: number) {
  const e = i >= 0
    ? { ...settings.value.mcp.servers[i] }
    : { name: '', type: 'stdio', command: '', arguments: '', url: '', envJson: '', workingDirectory: '', enabled: true, timeoutMs: 30000, protocolVersion: 'auto' }
  e.headersJson = e.headersJson ?? '' // 旧配置无此字段时补默认
  mcpEditor.value = e
  mcpEditorIndex.value = i
  mcpEditorJsonMode.value = false
  error.value = ''
}

function openMcpEditorJson(i: number) {
  openMcpEditor(i)
  mcpEditorJsonMode.value = true
  mcpEditorDraft.value = JSON.stringify(mcpEditor.value, null, 2)
}

/** 校验并写回服务器列表(增改即触发热更保存);非法名/缺命令或 URL 就地报错 */
function applyMcpEditor() {
  const e = mcpEditor.value
  if (!e) return
  try {
    const val: any = mcpEditorJsonMode.value ? JSON.parse(mcpEditorDraft.value) : e
    if (!String(val.name ?? '').trim()) { error.value = '请填写名称'; return }
    if ((val.type ?? 'stdio') === 'stdio') {
      if (!String(val.command ?? '').trim()) { error.value = 'stdio 类型必须填写命令'; return }
    } else if (!String(val.url ?? '').trim()) { error.value = 'http/sse 类型必须填写 URL'; return }
    const servers = settings.value.mcp.servers
    if (mcpEditorIndex.value >= 0) servers.splice(mcpEditorIndex.value, 1, val)
    else servers.push(val)
    mcpEditor.value = null
  } catch (err) {
    error.value = 'JSON 解析失败:' + String((err as Error).message)
  }
}

/** 导入:单个服务器对象 / {"servers":[…]} / MCP 常见配置格式 {"mcpServers":{名称:{command,args,env}}} */
function applyMcpImport() {
  try {
    const parsed = JSON.parse(mcpImportText.value)
    let list: any[] = []
    if (Array.isArray(parsed)) list = parsed
    else if (parsed && Array.isArray(parsed.servers)) list = parsed.servers
    else if (parsed && parsed.mcpServers && typeof parsed.mcpServers === 'object') {
      list = Object.entries(parsed.mcpServers).map(([name, v]: [string, any]) => ({
        name, type: 'stdio', command: v.command ?? '',
        arguments: Array.isArray(v.args) ? v.args.join(' ') : (v.args ?? ''),
        url: v.url ?? '', envJson: v.env ? JSON.stringify(v.env, null, 2) : '',
        workingDirectory: '', enabled: true, timeoutMs: 30000, protocolVersion: 'auto',
      }))
    } else if (parsed && typeof parsed === 'object') list = [parsed]
    let added = 0
    for (const s of list) {
      if (!s || typeof s !== 'object') continue
      settings.value.mcp.servers.push({
        name: s.name ?? '', type: s.type ?? 'stdio', command: s.command ?? '', arguments: s.arguments ?? '',
        url: s.url ?? '',
        envJson: typeof s.envJson === 'string' ? s.envJson : (s.env ? JSON.stringify(s.env, null, 2) : ''),
        headersJson: typeof s.headersJson === 'string' ? s.headersJson : (s.headers ? JSON.stringify(s.headers, null, 2) : ''),
        workingDirectory: s.workingDirectory ?? '',
        enabled: s.enabled ?? true,
        timeoutMs: s.timeoutMs ?? (s.timeoutSeconds ? s.timeoutSeconds * 1000 : 30000),
        protocolVersion: s.protocolVersion ?? 'auto',
      })
      added++
    }
    if (added) {
      mcpImportOpen.value = false
      mcpImportText.value = ''
      error.value = ''
    } else error.value = '没有识别到服务器配置'
  } catch (err) {
    error.value = 'JSON 解析失败:' + String((err as Error).message)
  }
}

const removeMcpServer = (i: number) => settings.value.mcp.servers.splice(i, 1)

// ───────── 智能体子代理:列表 + 新建/编辑(名称/系统提示词/工具集) ─────────

const SUB_AGENT_TOOLS = [
  { key: 'read_file', label: '读取文件' },
  { key: 'write_file', label: '写入文件' },
  { key: 'edit_file', label: '编辑文件' },
  { key: 'apply_patch', label: '应用补丁' },
  { key: 'bash', label: '运行终端' },
  { key: 'web_fetch', label: '网页获取' },
]

/** 旧配置可能没有 subAgents 节点:就地补形(后端 EnsureInitialized 亦兜底) */
function subAgents() {
  const s = settings.value
  if (!s.subAgents) s.subAgents = { enabled: false, agents: [] }
  if (!Array.isArray(s.subAgents.agents)) s.subAgents.agents = []
  return s.subAgents
}

const subAgentEditor = ref<any | null>(null)
const subAgentEditorIndex = ref(-1)

function openSubAgentEditor(i: number) {
  const a = i >= 0 ? subAgents().agents[i] : null
  const e = a
    ? { name: a.name ?? '', systemPrompt: a.systemPrompt ?? '', tools: [...(a.tools ?? [])], provider: a.provider ?? '', model: a.model ?? '', thinkingEffort: a.thinkingEffort ?? '', workingDirectoryAccess: a.workingDirectoryAccess || 'ReadOnly', maxToolIterations: typeof a.maxToolIterations === 'number' ? a.maxToolIterations : 0, enabled: a.enabled !== false }
    : { name: '', systemPrompt: '', tools: ['read_file', 'web_fetch'], provider: '', model: '', thinkingEffort: '', workingDirectoryAccess: 'ReadOnly', maxToolIterations: 0, enabled: true }
  subAgentEditor.value = e
  subAgentEditorIndex.value = i
  subModelMenu.value = false
}

function applySubAgentEditor() {
  const e = subAgentEditor.value
  if (!e) return
  const name = String(e.name ?? '').trim()
  if (!name) { error.value = '请填写子代理名称'; return }
  if (subAgents().agents.some((a: any, i: number) => i !== subAgentEditorIndex.value && a.name === name)) {
    error.value = '已存在同名子代理'
    return
  }
  const val = {
    name,
    systemPrompt: String(e.systemPrompt ?? '').trim(),
    tools: e.tools,
    // 空 = 继承主 Agent(会话当前所选供应商/模型/思考等级);启停开关在列表行,编辑器不再重复
    provider: String(e.provider ?? '').trim(),
    model: String(e.model ?? '').trim(),
    thinkingEffort: String(e.thinkingEffort ?? '').trim(),
    // 工作目录权限(默认只读);工作目录本身随主会话继承
    workingDirectoryAccess: String(e.workingDirectoryAccess ?? '').trim() || 'ReadOnly',
    // 最大工具轮数:0 = 不限
    maxToolIterations: e.maxToolIterations == null ? 0 : Math.max(0, Math.min(1000, Number(e.maxToolIterations) || 0)),
    enabled: !!e.enabled,
  }
  const agents = subAgents().agents
  if (subAgentEditorIndex.value >= 0) agents.splice(subAgentEditorIndex.value, 1, val)
  else agents.push(val)
  subAgentEditor.value = null
}

const removeSubAgent = (i: number) => { subAgents().agents.splice(i, 1) }

/** 工具集 toggle 按钮:点击选中/取消(多选) */
function toggleSubAgentTool(t: string) {
  const e = subAgentEditor.value
  if (!e) return
  e.tools = e.tools.includes(t) ? e.tools.filter((x: string) => x !== t) : [...e.tools, t]
}

/** 子代理思考等级选项:默认「继承」+ 选定模型启用的档位(该模型的思考等级设置;未配置 = 全部 7 档;
 * 模型为「继承」时主会话档位不定,展示全部) */
const subThinkingOptions = computed(() => {
  const e = subAgentEditor.value
  let levels: string[] = [...THINKING]
  if (e?.model) {
    const prows = (settings.value.llm?.providers ?? []) as any[]
    const prov = prows.find((p) => p.name === e.provider) ?? prows.find((p) => (p.models ?? []).some((m: any) => m.name === e.model))
    const m = (prov?.models ?? []).find((x: any) => x.name === e.model) as any
    const lv: string[] | null = m?.thinkingLevels
    levels = lv && lv.length ? [...lv] : [...THINKING]
  }
  return [{ value: '', label: '继承' }, ...levels.map((lv) => ({ value: lv, label: LEVEL_LABELS[lv] ?? lv, title: LEVEL_EN[lv] ?? lv }))]
})

// 换模型后当前思考等级若不在新模型档位内,回落「继承」
watch(
  () => [subAgentEditor.value?.model, subAgentEditor.value?.provider] as const,
  () => {
    const e = subAgentEditor.value
    if (!e?.thinkingEffort) return
    if (!subThinkingOptions.value.some((o) => o.value === e.thinkingEffort)) e.thinkingEffort = ''
  },
)

// ───────── 子代理模型级联菜单:「继承主 Agent」→ 供应商(悬停展开)→ 模型(与聊天工具栏同款交互) ─────────

const subModelMenu = ref(false)
const subHoverProv = ref('')
const subModelBtn = ref<HTMLElement | null>(null)
const subModelPopStyle = ref<Record<string, string>>({})
const subModelFlyLeft = ref(false)

const subProviders = computed<any[]>(() => settings.value.llm?.providers ?? [])

function subModelLabel() {
  const e = subAgentEditor.value
  if (!e?.model) return { name: '继承主 Agent', prov: '' }
  return { name: e.model, prov: e.provider || settings.value.llm?.activeProvider || '' }
}

/** 界面缩放(zoom)补偿:rect 是可视像素,fixed 定位需除回 zoom(与 AppSelect 同法) */
function subZoom() {
  const z = Number(getComputedStyle(document.documentElement).zoom)
  return Number.isFinite(z) && z > 0 ? z : 1
}

function placeSubModelPop() {
  const el = subModelBtn.value
  if (!el) return
  const z = subZoom()
  const r = el.getBoundingClientRect()
  const style: Record<string, string> = { left: r.left / z + 'px', minWidth: r.width / z + 'px' }
  const avail = (window.innerHeight - r.bottom) / z
  if (avail < 150 && r.top / z > 150) style.bottom = (window.innerHeight - r.top) / z + 4 + 'px'
  else style.top = r.bottom / z + 4 + 'px'
  // 右侧放不下(弹窗 224 + 子菜单 ~240)则子菜单向左展开
  subModelFlyLeft.value = r.left / z + 224 + 240 > window.innerWidth / z
  subModelPopStyle.value = style
}

async function toggleSubModelMenu() {
  if (subModelMenu.value) {
    subModelMenu.value = false
    return
  }
  subModelMenu.value = true
  await nextTick()
  placeSubModelPop()
}

function closeSubModelMenu(e: PointerEvent) {
  if ((e.target as HTMLElement).closest('.sub-mm-pop')) return
  subModelMenu.value = false
}

watch(subModelMenu, (v) => {
  if (v) {
    document.addEventListener('pointerdown', closeSubModelMenu, true)
    window.addEventListener('resize', placeSubModelPop)
  } else {
    document.removeEventListener('pointerdown', closeSubModelMenu, true)
    window.removeEventListener('resize', placeSubModelPop)
  }
})

function pickSubModel(p: string, m: string) {
  const e = subAgentEditor.value
  if (!e) return
  e.provider = p
  e.model = m
  subModelMenu.value = false
}

function pickSubInherit() {
  const e = subAgentEditor.value
  if (!e) return
  e.provider = ''
  e.model = ''
  subModelMenu.value = false
}

// MCP 连接状态:模态打开期间每 5 秒轮询,行内显示 已连接/未连接/未启用 徽标
const mcpStatus = ref<{ pluginEnabled: boolean; servers: McpServerStatus[] } | null>(null)
let mcpStatusTimer: ReturnType<typeof setInterval> | null = null

function mcpKey(s: any) { return String(s.name || s.command || s.url || '').trim() }
function mcpStatusOf(s: any) { return mcpStatus.value?.servers.find(x => x.name === mcpKey(s)) ?? null }
function mcpStatusText(s: any) {
  const st = mcpStatusOf(s)
  if (!st || !st.enabled) return '未启用'
  if (st.alive) return `已连接 · ${st.tools} 个工具`
  return st.error ? `未连接 · ${st.error}` : '未连接'
}
function mcpStatusCls(s: any) {
  const st = mcpStatusOf(s)
  if (!st || !st.enabled) return 'off'
  return st.alive ? 'ok' : 'err'
}
async function loadMcpStatus() {
  try { mcpStatus.value = await getMcpStatus(props.agentId) } catch { /* 状态拉取失败不打扰设置页 */ }
}
// 注意:对 pluginModal 的 watch 必须放在其声明之后(见下方),否则 setup 阶段 TDZ

// MCP 编辑表单的两个下拉(与设置页同款 AppSelect)
const mcpTypeOptions: SelectOption[] = [
  { value: 'stdio', label: 'stdio(本地命令)' },
  { value: 'http', label: 'http(Streamable HTTP)' },
  { value: 'sse', label: 'sse(HTTP + SSE)' },
]

const mcpProtocolOptions: SelectOption[] = [
  { value: 'auto', label: '自动(推荐)' },
  { value: '2024-11-05', label: '2024-11-05' },
  { value: '2025-03-26', label: '2025-03-26' },
  { value: '2025-06-18', label: '2025-06-18' },
]

// ───────── 技能两级管理:主页列表 + 新建/编辑/导入(固定存于 ~/.apet/skills) ─────────

const skills = ref<SkillEntry[]>([])
const skillsLoading = ref(false)
const skillEditor = ref<{ name: string; description: string; content: string; isNew: boolean } | null>(null)
const skillSaving = ref(false)
const skillFileInput = ref<HTMLInputElement | null>(null)

async function loadSkills() {
  skillsLoading.value = true
  try {
    skills.value = await getSkills(props.agentId)
  } catch {
    skills.value = []
  } finally {
    skillsLoading.value = false
  }
}

async function openSkillEditor(name?: string) {
  if (!name) {
    skillEditor.value = { name: '', description: '', content: '', isNew: true }
    error.value = ''
    return
  }
  const d = await getSkill(props.agentId, name).catch(() => null)
  if (!d) {
    error.value = '读取技能失败'
    return
  }
  skillEditor.value = { name: d.name, description: d.description, content: d.content, isNew: false }
  error.value = ''
}

async function saveSkillEditor() {
  const e = skillEditor.value
  if (!e || skillSaving.value) return
  if (!e.name.trim()) {
    error.value = '请填写技能名'
    return
  }
  skillSaving.value = true
  error.value = ''
  try {
    const r = await saveSkill(props.agentId, { name: e.name.trim(), description: e.description, content: e.content })
    if (!r.ok) {
      error.value = r.error || '保存失败'
      return
    }
    skillEditor.value = null
    await loadSkills()
  } catch (err) {
    error.value = String((err as Error).message)
  } finally {
    skillSaving.value = false
  }
}

async function removeSkill(name: string) {
  if ((await ask({ title: '删除技能', message: `确定删除技能「${name}」?其文件将一并删除,不可恢复。`, confirmLabel: '删除', danger: true })) !== true) return
  try {
    await deleteSkill(props.agentId, name)
  } catch (e) {
    error.value = String((e as Error).message)
  }
  await loadSkills()
}

async function toggleSkill(name: string, enabled: boolean) {
  try {
    await setSkillEnabled(props.agentId, name, enabled)
  } catch (e) {
    error.value = String((e as Error).message)
  }
  await loadSkills()
}

/** 导入 .md 技能文件:读入后进新建编辑器(内容已带 frontmatter 时保存将原样保留) */
function onSkillFilePicked(ev: Event) {
  const input = ev.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  const reader = new FileReader()
  reader.onload = () => {
    skillEditor.value = {
      name: file.name.replace(/\.(md|markdown)$/i, ''),
      description: '',
      content: String(reader.result ?? ''),
      isNew: true,
    }
    error.value = ''
  }
  reader.readAsText(file, 'utf-8')
}

// ───────── 界面背景图(上传到服务端 agents/{id}/backgrounds/,写回 imagePath;桌面端/网页共用) ─────────

const bgErr = ref<{ chat: boolean; window: boolean }>({ chat: false, window: false })
const bgRev = ref<{ chat: number; window: number }>({ chat: 0, window: 0 })
const bgInput = ref<HTMLInputElement | null>(null)
const bgPickTarget = ref<'chat' | 'window'>('chat')

function bgUrl(which: 'chat' | 'window') {
  return `/api/agents/${props.agentId}/backgrounds/${which}?v=${bgRev.value[which]}`
}

/** 窗体背景图缩放策略 → CSS(同 WPF Stretch 语义;设置页与聊天页共用同一份 windowBackground 配置) */
const winBgSize = computed(() =>
  ({ Fill: '100% 100%', Uniform: 'contain', UniformToFill: 'cover', None: 'auto' } as Record<string, string>)[
    settings.value?.windowBackground?.stretch ?? 'UniformToFill'
  ] ?? 'cover')

function pickBg(which: 'chat' | 'window') {
  bgPickTarget.value = which
  bgInput.value?.click()
}

async function onBgPicked(e: Event) {
  const input = e.target as HTMLInputElement
  const f = input.files?.[0]
  input.value = ''
  if (!f) return
  const which = bgPickTarget.value
  if (!f.type.startsWith('image/')) {
    showToast('背景仅支持图片文件(PNG/JPG/WebP/GIF/BMP)', 'warn')
    return
  }
  if (f.size > 10 * 1024 * 1024) {
    showToast('背景图片超过 10 MB 上限', 'warn')
    return
  }
  try {
    const dataUrl = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader()
      reader.onload = () => resolve(reader.result as string)
      reader.onerror = () => reject(reader.error)
      reader.readAsDataURL(f)
    })
    const r = await uploadBackground(props.agentId, which, dataUrl)
    setBy(which === 'chat' ? 'chatBackground.imagePath' : 'windowBackground.imagePath', r.path)
    bgRev.value[which] = Date.now()
    bgErr.value[which] = false
    showToast('背景已更新')
  } catch (err) {
    showToast(String((err as Error).message), 'err')
  }
}

async function clearBg(which: 'chat' | 'window') {
  try {
    await deleteBackground(props.agentId, which)
    setBy(which === 'chat' ? 'chatBackground.imagePath' : 'windowBackground.imagePath', '')
    showToast('已恢复默认背景')
  } catch (err) {
    showToast(String((err as Error).message), 'err')
  }
}

// ───────── 模型高级设置:每个模型可用的思考等级与附件媒体类型(模态窗中编辑) ─────────

const modelModal = ref<number | null>(null)
const curModelSel = computed<any>(() => (modelModal.value !== null ? curProvider.value?.models?.[modelModal.value] ?? null : null))

const LEVEL_LABELS: Record<string, string> = {
  none: '关闭', default: '开启', minimal: '极简', low: '低', medium: '中', high: '高', xhigh: '超高', max: '最高',
}

/** 等级英文原文(tooltip 展示);default 开启后按 high 发送 */
const LEVEL_EN: Record<string, string> = {
  none: 'none', default: 'default(即 high)', minimal: 'minimal', low: 'low',
  medium: 'medium', high: 'high', xhigh: 'xhigh', max: 'max',
}

const MEDIA_ITEMS: [string, string][] = [
  ['image', '图片'], ['video', '视频'], ['audio', '音频'], ['pdf', 'PDF'],
]

/** 该模型生效的思考等级集合(未配置 = 全部档位) */
function advLevels(m: any): string[] {
  const lv: string[] | null = m.thinkingLevels
  return lv && lv.length ? lv : [...THINKING]
}

/** 该模型生效的媒体类型集合(未配置 = 默认仅图片;['none'] = 显式关闭全部媒体) */
function advMedia(m: any, t: string): boolean {
  const mt: string[] | null = m.mediaTypes
  if (!mt || mt.includes('none')) return false
  return mt.length ? mt.includes(t) : t === 'image'
}

function toggleMedia(m: any, t: string, e: Event) {
  const on = (e.target as HTMLInputElement).checked
  let cur: string[] | null = m.mediaTypes
  if (!cur || cur.includes('none')) cur = cur?.includes('none') ? [] : [...(cur ?? [])]
  const base: string[] = cur && cur.length ? [...cur] : ['image'] // 未配置默认仅图片
  const next = on ? (base.includes(t) ? base : [...base, t]) : base.filter((x) => x !== t)
  m.mediaTypes = next.length ? next : ['none'] // 全不勾 = 显式关闭媒体(仅文本/文件)
}

/** 方形 toggle 按钮:思考等级点击选中/取消。
 * 空数组与未配置同义(= 全部档位):切换基准必须与 advLevels 的展示口径一致,
 * 否则"全亮"状态下点击会因基准为空数组而看似无响应(旧配置空值修档的 bug)。
 * 至少保留一档:全不勾会被拒绝(提示),不再回落"不限制" */
function toggleLevelChip(m: any, lv: string) {
  const on = advLevels(m).includes(lv)
  const cur: string[] = Array.isArray(m.thinkingLevels) && m.thinkingLevels.length ? m.thinkingLevels : [...THINKING]
  const next = on ? cur.filter((x) => x !== lv) : [...cur, lv]
  if (next.length === 0) {
    showToast('至少保留一个思考等级', 'err')
    return
  }
  m.thinkingLevels = next
}

/** 方形 toggle 按钮:媒体类型点击选中/取消 */
function toggleMediaChip(m: any, t: string) {
  const on = advMedia(m, t)
  let cur: string[]
  if (Array.isArray(m.mediaTypes) && m.mediaTypes.includes('none')) cur = []
  else if (Array.isArray(m.mediaTypes) && m.mediaTypes.length) cur = [...m.mediaTypes]
  else cur = ['image']
  const next = on ? cur.filter((x) => x !== t) : cur.includes(t) ? cur : [...cur, t]
  m.mediaTypes = next.length ? next : ['none']
}

const THINKING = ['none', 'default', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max']

// ───────── 内置插件(一层列表 + 模态窗设置) ─────────

const pluginModal = ref<string | null>(null)

// MCP 连接状态轮询:模态打开期间每 5 秒刷新(必须晚于 pluginModal 声明,避免 setup 阶段 TDZ)
watch(pluginModal, (v) => {
  if (mcpStatusTimer) { clearInterval(mcpStatusTimer); mcpStatusTimer = null }
  if (v === 'mcp') {
    loadMcpStatus()
    mcpStatusTimer = setInterval(loadMcpStatus, 5000)
  }
})

const PLUGINS: { key: string; title: string; icon: string; desc: string; enablePath?: string }[] = [
  {
    key: 'pet', title: '桌宠', icon: 'smart_toy',
    desc: '在桌面显示可点击、可移动的宠物;回复携带情绪标签时,宠物将执行对应动作。',
    enablePath: 'desktopPet.enabled',
  },
  {
    key: 'tts', title: 'TTS 语音', icon: 'graphic_eq',
    desc: '把回复朗读出来:朗读时机、语速音量,支持系统语音 / 服务端 / GSV 多引擎。',
    enablePath: 'tts.common.enabled',
  },
  {
    key: 'memory', title: '记忆', icon: 'psychology',
    desc: '长期记忆的自动遗忘策略(按天清理长期未使用的记忆)。',
    enablePath: 'memory.enabled',
  },
  {
    key: 'bili', title: 'BiliLive 直播互动', icon: 'live_tv',
    desc: '接入 B 站直播间:弹幕、礼物、SC、互动事件驱动的自动回复。',
    enablePath: 'biliLive.enabled',
  },
  {
    key: 'scene', title: '情景', icon: 'theaters',
    desc: '预设情景库:$scene 指令按名把情景内容注入会话提示词。',
    enablePath: 'sceneEnabled',
  },
  {
    key: 'mcp', title: 'MCP 服务器', icon: 'hub',
    desc: '接入外部 MCP 服务器(stdio / http / sse):服务器的工具以 mcp_{服务器}_{工具} 自动暴露给 Agent。',
    enablePath: 'mcp.enabled',
  },
   {
    key: 'skills', title: '技能', icon: 'auto_awesome',
    desc: '技能清单管理与新建/导入(存放于用户目录 .apet\\skills):索引注入提示词,模型经 skill 工具按需加载。',
    enablePath: 'skills.enabled',
  },
   {
    key: 'subagents', title: '智能体子代理', icon: 'person',
    desc: '预配置子代理:主 Agent 用 sub_run 把子任务委托出去,子代理带自己的系统提示词与工具集独立运行,sub_wait 取回结果。',
    enablePath: 'subAgents.enabled',
  },
]

// ───────── 压缩:摘要模板预设(内置只读展示,自定义可编辑;口径同桌面端 SettingsView) ─────────

const compressPresets = ref<CompressPresetInfo | null>(null)
const compressTruncate = computed(() => settings.value?.compressStrategy === 'truncate')

/** 回显预设:已存值为内置名/自定义则用之;为空时按用户模板是否为空回退(轻量通用/自定义);未知名回退缺省 */
const resolvedPreset = computed(() => {
  const info = compressPresets.value
  const saved: string = settings.value?.compressPreset ?? ''
  const custom = info?.customTag ?? '__custom__'
  const names = info?.presets.map((p) => p.name) ?? []
  if (saved === custom) return custom
  if (saved && names.includes(saved)) return saved
  if (!saved) return (settings.value?.compressTemplate ?? '').trim() ? custom : names[0] ?? ''
  return names[0] ?? ''
})

const presetText = computed(() => {
  const info = compressPresets.value
  if (!info) return ''
  if (resolvedPreset.value === info.customTag) {
    const t = settings.value?.compressTemplate ?? ''
    return t.trim() ? t : info.defaultTemplate
  }
  return info.presets.find((p) => p.name === resolvedPreset.value)?.text ?? ''
})

const templateEditable = computed(
  () => !compressTruncate.value && resolvedPreset.value === (compressPresets.value?.customTag ?? '__custom__'),
)

const presetOptions = computed<SelectOption[]>(() => [
  ...(compressPresets.value?.presets.map((p) => ({ value: p.name, label: p.name })) ?? []),
  { value: compressPresets.value?.customTag ?? '__custom__', label: '自定义' },
])

function onPresetChange(v: string) {
  setBy('compressPreset', v)
}

/** 滑条直接按路径写值(按 step 对齐小数位,避免浮点噪声) */
function setSlider(path: string, e: Event, step = 0.05) {
  const el = e.target as HTMLInputElement
  const dot = String(step).indexOf('.')
  const d = dot < 0 ? 0 : String(step).length - dot - 1
  const v = Number(Number(el.value).toFixed(d))
  el.value = String(v)
  setBy(path, v)
}
const ACCESS: Opt[] = [['ReadOnly', '只读'], ['ReadWrite', '读写'], ['FullAccess', '完全访问'], ['Ask', '全部询问']]
const STRETCH = ['Fill', 'Uniform', 'UniformToFill', 'None', 'Tile']
// WebView2 渲染模式:多显卡/虚拟显示器驱动下 GPU 渲染偶发黑屏与滚动残留,默认全软件渲染
const WEBVIEW_RENDER = [
  ['sw', '全软件渲染(最稳,推荐)'],
  ['cpu', 'CPU 合成(GPU 渲染)'],
  ['hw', '硬件加速 GPU 渲染'],
]
const FONT_STYLES = ['Regular', 'Bold', 'Italic', 'BoldItalic']
const PET_ACTIONS: [string, string][] = [['None', '无'], ['RandomMotion', '随机动作'], ['Idle', '回待机'], ['OpenChat', '打开聊天窗口']]
const TAG_STYLES: [string, string][] = [
  ['SquareBrackets', '[happy]'], ['CurlyBraces', '{happy}'],
  ['AngleBrackets', '<happy>'], ['DoubleSquareBrackets', '[[happy]]'],
]
const PROVIDER_TEMPLATES: [string, string][] = [
  ['DeepSeek', 'https://api.deepseek.com/v1'],
  ['Z.Ai', 'https://api.z.ai/api/paas/v4'],
  ['OpenAI', 'https://api.openai.com/v1'],
  ['Moonshot (Kimi)', 'https://api.moonshot.cn/v1'],
  ['Qwen', 'https://dashscope.aliyuncs.com/compatible-mode/v1'],
  ['Gemini API', 'https://generativelanguage.googleapis.com/v1beta/openai/'],
]
const TTS_ENGINES: [string, string][] = [['server', '自定义服务器'], ['windows', 'Windows 系统语音'], ['gsv', 'GSV-TTS 本地推理']]
const ENGINE_OPTIONS: SelectOption[] = [
  { value: 'spine', label: 'Spine' },
  { value: 'live2d', label: 'Live2D' },
  { value: 'vrm', label: 'VRM', title: '加载 .vrm 模型;动作为模型表情,内建眨眼呼吸与物理。' },
  { value: 'basic', label: '基础', title: '图片文件夹:每个 png/gif 图片是一个动作(文件名即动作名)。' },
]

const categories: Category[] = [
  {
    key: 'agent', title: 'Agent', fields: [
      // 启用开关已移除:经主页 Agent 卡片的菜单启用/停用(避免停用后失去入口)
      { path: 'name', label: '名称', type: 'text', w: 420 },
      { path: 'systemPrompt', label: '系统提示词', type: 'textarea' },
      { path: 'temperature', label: 'Temperature(留空 = 服务端默认)', type: 'number', w: 160 },
      {
        path: 'language', label: '语言(回复和语音朗读使用的语言)', type: 'select', w: 160,
        options: [['zh', '中文'], ['ja', '日本語'], ['en', 'English']],
      },
      { path: 'autoOpenOnStartup', label: '启动时打开对话窗口(取消后可经托盘图标打开)', type: 'bool' },
      { path: 'hideThinking', label: '隐藏思考内容(仅显示层,记录仍全量保存)', type: 'bool' },
      { path: 'hideEmotionTags', label: '隐藏情绪标签(流式剥离,记录保留原文)', type: 'bool' },
    ],
  },
  { key: 'llm', title: '模型服务', sub: '修改并保存后立即生效,适用于该 Agent 的所有会话。', fields: [] },
  { key: 'compress', title: '压缩', sub: 'Token 上限在「模型服务」里设置。', fields: [] },
  { key: 'tools', title: '工具', sub: '按功能分组开关工具:关闭的整组工具不会提供给模型调用。', fields: [] },
  {
    key: 'security', title: '权限', sub: '存在风险的文件操作将请求确认;白名单目录内的操作直接放行。终端命令跟随默认目录权限:只读档仅放行只读命令,读写档除灾难命令外静默执行,完全访问=全放行,全部询问=全弹。', fields: [
      { path: 'security.defaultWorkingDirectory', label: '默认工作目录(新建会话继承)', type: 'dir' },
      { path: 'security.workspaceRoot', label: '临时工作区根目录(未设工作目录的会话自动按会话建工作区;空 = 默认位置)', type: 'dir' },
      { path: 'security.defaultWorkingDirectoryAccess', label: '默认目录权限', type: 'select', w: 280, options: ACCESS },
      { path: 'security.whitelist', label: '白名单目录(任何操作放行)', type: 'paths' },
    ],
  },
  { key: 'plugins', title: '内置插件', sub: '系统内置插件的开关与独立设置:点「设置」在模态窗中编辑。', fields: [] },
  {
    key: 'ui', title: '界面', fields: [
      { path: 'uiScale', label: '界面缩放', type: 'slider', min: 0.75, max: 2.0, step: 0.05 },
      { path: 'panelOpacityPercent', label: '面板不透明度', type: 'slider', min: 0, max: 100, step: 1 },
      { path: 'webViewRenderMode', label: 'Web 主窗渲染(窗口重开生效)', type: 'select', w: 280, options: WEBVIEW_RENDER },
      // 背景图已改为上传式(见下方自定义模板):上传到服务端并写回 imagePath
      { path: 'chatBackground.opacityPercent', label: '聊天区背景不透明度', type: 'slider', min: 0, max: 100, step: 1 },
      { path: 'chatBackground.stretch', label: '聊天区背景缩放', type: 'select', w: 280, options: STRETCH },

      { path: 'windowBackground.opacityPercent', label: '窗体背景不透明度', type: 'slider', min: 0, max: 100, step: 1 },
      { path: 'windowBackground.stretch', label: '窗体背景缩放', type: 'select', w: 280, options: STRETCH },
    ],
  },
]

const cur = computed(() => categories.find((c) => c.key === active.value))

// 路由 ?tab= 直达指定分区(如聊天工具栏「管理模型」→ 模型服务);非法值回落 Agent
if (props.initialTab && categories.some((c) => c.key === props.initialTab)) {
  active.value = props.initialTab
}

const theme = ref<ThemeMode>(currentTheme())

// 界面缩放在设置加载完成时应用(见 onMounted);拖动过程不实时生效

// ───────── 自动保存(600ms 防抖,对齐桌面端 RestartDebounce → SaveAgent) ─────────

const saveState = ref<'idle' | 'pending' | 'saving' | 'saved' | 'error'>('idle')
const savedAt = ref('')
const saveError = ref('')
let saveTimer: number | null = null
let suppressWatch = false

watch(settings, () => {
  if (suppressWatch || !settings.value) return
  scheduleSave()
}, { deep: true })

function scheduleSave() {
  if (saveTimer !== null) window.clearTimeout(saveTimer)
  saveState.value = 'pending'
  saveTimer = window.setTimeout(flushSave, 600)
}

async function flushSave() {
  saveTimer = null
  saveState.value = 'saving'
  const sig = toolVisibilitySig()
  try {
    const r = await putSettings(props.agentId, settings.value)
    saveState.value = 'saved'
    savedAt.value = new Date().toTimeString().slice(0, 8)
    // 保存成功后服务端已热应用插件开关/MCP 配置(工具可见性随之变化),快照有差异就同步刷新工具组
    if (sig !== lastToolSig) {
      lastToolSig = sig
      reloadToolGroups()
    }
    if (r.petMessage) {
      showToast(r.petMessage, r.petMessage.includes('暂不能启停') ? 'warn' : 'ok')
      if (r.petMessage.includes('暂不能启停')) {
        // 服务端回滚了桌宠开关:重新同步本地状态
        suppressWatch = true
        settings.value = await getSettings(props.agentId)
        await nextTick()
        suppressWatch = false
      }
    }
  } catch (e) {
    saveState.value = 'error'
    saveError.value = String((e as Error).message)
    showToast(`保存失败:${saveError.value}`, 'err')
  }
}

// ───────── 工具组清单与插件开关同步 ─────────
// 这些设置决定服务端暴露哪些工具(内置插件开关控制 mem_*/mute 可见性与插件级启停,MCP 服务器列表增删启停
// 决定 mcp_* 工具的注册),保存时对比快照,有变化才刷新一次工具组,让「工具」页即时反映。
function toolVisibilitySig(): string {
  const parts = PLUGINS.map((p) => `${p.enablePath}=${p.enablePath ? getBy(p.enablePath) : ''}`)
  parts.push(JSON.stringify(settings.value?.mcp?.servers ?? null))
  return parts.join('|')
}
let lastToolSig = ''

// 当前实际生效(提供给模型)的工具清单:与工具组清单同步刷新
const activeTools = ref<ToolInfo[]>([])

// 折叠「其他」卡片:插件页单独管控而被主组清单隐藏的工具(MCP 服务器工具/B站直播等)
const hiddenTools = ref<ToolInfo[]>([])
const showHiddenTools = ref(false)

async function reloadToolGroups() {
  try {
    const [g, ts, ht] = await Promise.all([getToolGroups(props.agentId), getTools(props.agentId), getHiddenTools(props.agentId)])
    groups.value = g
    activeTools.value = ts.filter((t) => t.enabled)
    hiddenTools.value = ht
  } catch {
    // 刷新失败保留旧清单,不打扰用户
  }
}

async function toggleHiddenTool(t: ToolInfo, enabled: boolean) {
  try {
    await setToolEnabled(props.agentId, t.name, enabled)
    t.enabled = enabled
    reloadToolGroups() // 同步刷新「当前工具清单」
  } catch (e) {
    error.value = String((e as Error).message)
  }
}

// MCP 工具按服务器聚合:一台服务器的众多工具在清单里合成一个胶囊(悬浮查看工具列表),避免刷屏
const activeToolChips = computed(() => {
  const byServer = new Map<string, ToolInfo[]>()
  for (const t of activeTools.value) {
    const srv = mcpServerOf(t.name)
    if (!srv) continue
    const arr = byServer.get(srv) ?? []
    arr.push(t)
    byServer.set(srv, arr)
  }
  const chips: { key: string; label: string; title: string; mcp?: boolean }[] = []
  const placed = new Set<string>()
  for (const t of activeTools.value) {
    const srv = mcpServerOf(t.name)
    if (!srv) {
      chips.push({ key: t.name, label: t.displayName, title: t.name })
      continue
    }
    if (placed.has(srv)) continue
    placed.add(srv)
    const list = byServer.get(srv)!
    const label = `MCP·${srv}` + (list.length > 1 ? ` ×${list.length}` : '')
    chips.push({ key: 'mcp:' + srv, mcp: true, label, title: list.map((x) => x.name).join('\n') })
  }
  return chips
})

// mcp_{服务器}_{工具} → 服务器名(按已配置服务器名的净化前缀匹配;前缀带尾下划线,不会误配相似名)
function mcpServerOf(toolName: string): string | null {
  if (!toolName.startsWith('mcp_')) return null
  for (const s of (settings.value.mcp?.servers ?? []) as any[]) {
    if (!s?.name) continue
    const prefix = 'mcp_' + String(s.name).replace(/[^A-Za-z0-9_-]/g, '_') + '_'
    if (toolName.startsWith(prefix) && toolName.length > prefix.length) return s.name
  }
  return null
}

// ───────── 路径取值/赋值 ─────────

function getBy(path: string): any {
  return path.split('.').reduce((o: any, k) => (o == null ? o : o[k]), settings.value)
}

function setBy(path: string, v: any) {
  const keys = path.split('.')
  const last = keys.pop()!
  const target = keys.reduce((o: any, k) => o[k], settings.value)
  target[last] = v
}

/** 滑条值按 step 的小数位取整,避免 1.4500000000000002 这类浮点噪声写进配置 */
function onSliderInput(f: Field, e: Event) {
  const el = e.target as HTMLInputElement
  const v = Number(el.value)
  const stepStr = String(f.step ?? 1)
  const dot = stepStr.indexOf('.')
  const d = dot < 0 ? 0 : stepStr.length - dot - 1
  const clean = Number(v.toFixed(d))
  if (clean !== v) el.value = String(clean)
  setBy(f.path, clean)
}

/** 滑条释放(拖拽结束/键盘调整)时的提交:界面缩放只在此刻应用——
    拖动中若实时改 CSS zoom,滑条自身的尺寸/位置会随缩放联动漂移,产生连锁效应无法拖动 */
function onSliderChange(f: Field, e: Event) {
  if (f.path === 'uiScale') applyUiScale(Number((e.target as HTMLInputElement).value))
}

/** 显示用数值格式化(去掉浮点噪声尾巴) */
function fmtVal(v: any): string {
  if (typeof v === 'number') return String(Math.round(v * 100) / 100)
  return v ?? ''
}

/** 数字/百分比输入失焦时按 min/max 夹紧(原生 min/max 只挡步进按钮,不拦手输) */
function clampNum(f: Field, e: Event) {
  const el = e.target as HTMLInputElement
  if (el.value === '') return
  let v = Number(el.value)
  if (Number.isNaN(v)) return
  if (f.min !== undefined) v = Math.max(f.min, v)
  if (f.max !== undefined) v = Math.min(f.max, v)
  if (String(v) !== el.value) {
    el.value = String(v)
    setBy(f.path, v)
  }
}

/** 宽字段独占整行;短字段在宽视口下并排成组,提高密度 */
const WIDE_TYPES: FieldType[] = ['textarea', 'dir', 'file', 'paths', 'json', 'slider', 'font']

function isWideField(f: Field) {
  return WIDE_TYPES.includes(f.type)
}

const selStyle = (f: Field) => ({ width: (f.w ?? 280) + 'px' })
const numStyle = (f: Field) => ({ width: (f.w ?? 160) + 'px' })

function optList(options?: Opt[]): SelectOption[] {
  return (options ?? []).map((o) => (typeof o === 'string' ? { value: o, label: o } : { value: o[0], label: o[1] }))
}

// ───────── 气泡预览(真实气泡窗口,弹在宠物旁)+ 系统字体列表 ─────────

let previewTimer: number | null = null

async function firePreview() {
  previewTimer = null
  const b = settings.value?.chatBubble
  if (!b) return
  try {
    await previewBubble(props.agentId, {
      topOffset: b.topOffset, leftOffset: b.leftOffset, rightOffset: b.rightOffset,
      fontFamily: b.fontFamily, fontSize: b.fontSize, fontStyle: b.fontStyle,
      textColor: b.textColor, fillColor: b.fillColor, borderColor: b.borderColor,
      borderWidth: b.borderWidth, cornerRadius: b.cornerRadius, tailLength: b.tailLength,
      maxWidth: b.maxWidth, opacityPercent: b.opacityPercent,
    })
  } catch (e) {
    // 桌宠/气泡未启用时预览不可用:轻提示(toast 自动消失,拖滑条重复触发只是刷新倒计时)
    showToast(String((e as Error).message), 'warn')
  }
}

watch(
  () => settings.value?.chatBubble,
  () => {
    if (suppressWatch || !settings.value) return
    if (previewTimer !== null) window.clearTimeout(previewTimer)
    previewTimer = window.setTimeout(firePreview, 120) // 拖动过程中即时预览(短防抖避免请求洪水)
  },
  { deep: true },
)

// 系统字体清单:后端 System.Drawing(与桌面端同源,即本机已安装字体)
const fontList = ref<string[]>([])

async function loadFonts() {
  if (fontList.value.length > 0) return
  try {
    fontList.value = await getSystemFonts()
  } catch {
    fontList.value = []
  }
}

// ───────── 文件/目录选择 ─────────

const browser = ref<{
  mode: 'dir' | 'file'
  initial: string
  exts?: string[]
  /** 普通字段:选中后写回的目标路径 */
  target?: string
  /** paths 字段:选中后追加进数组而非覆盖 */
  append?: boolean
  onPick?: (p: string) => void
} | null>(null)

function openBrowser(field: Field) {
  const mode = field.type === 'file' ? 'file' : 'dir'
  browser.value = {
    mode,
    initial: field.type === 'paths' ? '' : String(getBy(field.path) ?? ''),
    exts: field.exts,
    target: field.path,
    append: field.type === 'paths',
  }
}

function onBrowserSelect(p: string) {
  const b = browser.value
  browser.value = null
  if (!b) return
  if (b.onPick) {
    b.onPick(p)
    return
  }
  if (!b.target) return
  if (b.append) {
    const list = getBy(b.target)
    const arr: string[] = Array.isArray(list) ? [...list] : []
    if (!arr.some((x) => x.toLowerCase() === p.toLowerCase())) arr.push(p)
    setBy(b.target, arr)
  } else {
    setBy(b.target, p)
  }
}

// ───────── 模型服务(列表选中即生效;一次编辑一个服务,同桌面端) ─────────

const selIdx = ref(0)
const newProviderTemplate = ref('自定义')
const templateOptions = computed(() => ['自定义', ...PROVIDER_TEMPLATES.map(([n]) => n)])
const fetchedModels = ref<Record<number, string[]>>({})
const fetchBusy = ref<Record<number, boolean>>({})
const fetchError = ref<Record<number, string>>({})
const modelDraft = ref<Record<number, string>>({})

const curProvider = computed<any>(() => settings.value?.llm?.providers[selIdx.value] ?? settings.value?.llm?.providers[0])

/** 选中项即生效:保持 activeProvider 与 selIdx 一致 */
function syncActive() {
  settings.value.llm.activeProvider = settings.value.llm.providers[selIdx.value]?.name ?? ''
}

function selectProvider(i: number) {
  selIdx.value = i
  syncActive()
}

/** 生效的思考字段样式:thinkingField 优先;旧配置按 sendThinkingField 开关映射(true→thinking,false→off) */
function providerThinkingField(p: any): string {
  if (p?.thinkingField) return p.thinkingField
  return p?.sendThinkingField === false ? 'off' : 'thinking'
}

/** 默认模型:已配置值优先(可能不在当前清单内,保留可编辑性);空 = 未设置 */
function providerDefaultModel(p: any): string {
  return p?.model ?? ''
}

/** 默认模型选项:该供应商模型清单(+ 当前值兜底) */
function providerDefaultModelOptions(p: any): { value: string; label: string }[] {
  const names = (p?.models ?? []).map((m: any) => m.name).filter(Boolean)
  const cur = providerDefaultModel(p)
  if (cur && !names.includes(cur)) names.unshift(cur)
  return names.map((n: string) => ({ value: n, label: n }))
}

function addProvider() {
  const template = newProviderTemplate.value
  const preset = PROVIDER_TEMPLATES.find(([n]) => n === template)
  const name = uniqueName(preset ? preset[0] : '自定义')
  const provider: any = {
    name, baseUrl: preset ? preset[1] : 'http://127.0.0.1:8080/v1', apiKey: '', model: '',
    reasoningEffort: 'high', thinkingField: 'reasoning_effort', models: [],
  }
  settings.value.llm.providers.push(provider)
  selectProvider(settings.value.llm.providers.length - 1)
  newProviderTemplate.value = '自定义'
}

function uniqueName(desired: string): string {
  let name = (desired || '').trim() || '自定义'
  const taken = () => settings.value.llm.providers.some((p: any) => p.name.toLowerCase() === name.toLowerCase())
  if (!taken()) return name
  for (let i = 2; ; i++) {
    if (!taken()) return name
    name = (desired || '自定义') + i
  }
}

function uniquifyProviderName() {
  const p = curProvider.value
  if (!p) return
  const desired = (p.name || '').trim()
  if (!desired) {
    p.name = '自定义'
  } else {
    const others = settings.value.llm.providers.filter((x: any) => x !== p)
    if (others.some((x: any) => x.name.toLowerCase() === desired.toLowerCase())) {
      for (let k = 2; ; k++) {
        const name = desired + k
        if (!others.some((x: any) => x.name.toLowerCase() === name.toLowerCase())) {
          p.name = name
          break
        }
      }
    }
  }
  syncActive()
}

function removeProvider() {
  const list = settings.value.llm.providers
  if (list.length <= 1) {
    error.value = '至少需保留一个服务。'
    return
  }
  list.splice(selIdx.value, 1)
  selIdx.value = Math.min(selIdx.value, list.length - 1)
  syncActive()
}

async function fetchModels(i: number) {
  const p = settings.value.llm.providers[i]
  fetchBusy.value = { ...fetchBusy.value, [i]: true }
  fetchError.value = { ...fetchError.value, [i]: '' }
  try {
    const { fetchProviderModels } = await import('../api/client')
    const models = await fetchProviderModels(p.baseUrl, p.apiKey)
    fetchedModels.value = { ...fetchedModels.value, [i]: models }
  } catch (e) {
    fetchError.value = { ...fetchError.value, [i]: String((e as Error).message) }
  } finally {
    fetchBusy.value = { ...fetchBusy.value, [i]: false }
  }
}

function addModelFromFetch() {
  const p = curProvider.value
  if (!p) return
  const name = (modelDraft.value[selIdx.value] ?? '').trim()
  if (!name) return
  if (p.models.some((m: any) => m.name.toLowerCase() === name.toLowerCase())) return
  p.models.push({ name, contextTokens: 65536, preferLocal: true, maxTokens: 0, priceInput: 0, priceInputCached: 0, priceOutput: 0 })
  modelDraft.value = { ...modelDraft.value, [selIdx.value]: '' }
}

// ───────── 工具组开关 ─────────

async function toggleGroup(g: ToolGroupInfo) {
  const enabled = !g.tools.every((t) => t.enabled)
  try {
    await setToolGroupEnabled(props.agentId, g.name, enabled)
    g.tools.forEach((t) => (t.enabled = enabled))
    reloadToolGroups() // 同步刷新「当前工具清单」
  } catch (e) {
    error.value = String((e as Error).message)
  }
}

// ───────── 桌宠(按引擎切换布局;动作列表来自运行中模型) ─────────

const petEngine = computed(() => (settings.value?.desktopPet?.engine ?? 'spine') as 'spine' | 'live2d' | 'vrm' | 'basic')
// 序列化键名:camelCase 把 Live2D 写成 live2D(内部大写 D),其余引擎全小写
const engineKey = computed(() => (petEngine.value === 'live2d' ? 'live2D' : petEngine.value))
const petMotions = ref<PetMotionInfo[]>([])
const emotionIndex = ref(0)

const PET_MODEL_META: Record<string, { label: string; exts: string[] }> = {
  spine: { label: '骨架文件(.skel/.json)', exts: ['.skel', '.json'] },
  live2d: { label: '模型文件(model3.json)', exts: ['.model3.json'] },
  vrm: { label: '模型文件(.vrm)', exts: ['.vrm'] },
  basic: { label: '图片文件夹(每个 png/gif 是一个动作)', exts: [] },
}

const petModelPath = computed(() => `desktopPet.${engineKey.value}.modelPath`)
const petModelValue = computed({
  get: () => getBy(petModelPath.value) ?? '',
  set: (v: string) => setBy(petModelPath.value, v),
})

function openPetModelBrowser() {
  const meta = PET_MODEL_META[petEngine.value]
  browser.value = {
    mode: petEngine.value === 'basic' ? 'dir' : 'file',
    initial: petModelValue.value,
    exts: meta.exts,
    onPick: (p: string) => {
      petModelValue.value = p
    },
  }
}

function openVrmaBrowser() {
  browser.value = {
    mode: 'file', exts: ['.vrma'], initial: '',
    onPick: (p: string) => {
      const list: string[] = settings.value.desktopPet.vrm.animationPaths
      if (!list.some((x) => x.toLowerCase() === p.toLowerCase())) list.push(p)
    },
  }
}

const petMotionNames = computed(() => petMotions.value.map((m) => m.name))
const petMotionDetail = (name: string) => petMotions.value.find((m) => m.name === name)?.detail ?? name

/** 可勾选动作 = 运行中模型的动作 ∪ 已配置在映射/待机里的动作(未加载模型也能编辑配置) */
const motionOptions = computed(() => {
  const engine = settings.value?.desktopPet?.[engineKey.value]
  if (!engine) return []
  const set = new Set(petMotionNames.value.map((n) => n.toLowerCase()))
  for (const n of engine.idleMotions ?? []) set.add(n.toLowerCase())
  for (const list of Object.values(engine.emotionMap ?? {}))
    for (const n of list as string[]) set.add(n.toLowerCase())
  return [...set].map((lower) =>
    petMotions.value.find((m) => m.name.toLowerCase() === lower)?.name ?? lower)
})

/** 情绪标签:idle 固定置顶(桌面端同规则),其余取 emotionMap 的键 */
const emotionTags = computed(() => {
  const map = settings.value?.desktopPet?.[engineKey.value]?.emotionMap ?? {}
  return ['idle', ...Object.keys(map).filter((k) => k !== 'idle')]
})

function motionsOf(tag: string): string[] {
  const engine = settings.value.desktopPet[engineKey.value]
  return tag === 'idle' ? engine.idleMotions ?? [] : engine.emotionMap[tag] ?? []
}

function isMotionChecked(tag: string, motion: string): boolean {
  return motionsOf(tag).some((m) => m.toLowerCase() === motion.toLowerCase())
}

function toggleMotion(tag: string, motion: string, checked: boolean) {
  const engine = settings.value.desktopPet[engineKey.value]
  if (tag === 'idle') {
    engine.idleMotions ??= []
    const list = engine.idleMotions as string[]
    const i = list.findIndex((m) => m.toLowerCase() === motion.toLowerCase())
    if (checked && i < 0) list.push(motion)
    if (!checked && i >= 0) list.splice(i, 1)
    return
  }
  engine.emotionMap ??= {}
  engine.emotionMap[tag] ??= []
  const list = engine.emotionMap[tag]
  const i = list.findIndex((m: string) => m.toLowerCase() === motion.toLowerCase())
  if (checked && i < 0) list.push(motion)
  if (!checked && i >= 0) list.splice(i, 1)
}

function renameEmotion(i: number, name: string) {
  const engine = settings.value.desktopPet[engineKey.value]
  const old = emotionTags.value[i]
  name = name.trim()
  if (!name || name === old || name === 'idle' || engine.emotionMap[name] !== undefined) return
  engine.emotionMap[name] = engine.emotionMap[old] ?? []
  delete engine.emotionMap[old]
}

function addEmotion() {
  const engine = settings.value.desktopPet[engineKey.value]
  engine.emotionMap ??= {}
  let name = '新情绪'
  for (let k = 2; engine.emotionMap[name] !== undefined; k++) name = '新情绪' + k
  engine.emotionMap[name] = []
  emotionIndex.value = emotionTags.value.indexOf(name)
}

function removeEmotionAt(tag: string) {
  if (!tag || tag === 'idle') return
  delete settings.value.desktopPet[engineKey.value].emotionMap[tag]
  if (emotionTags.value[emotionIndex.value] === tag) emotionIndex.value = 0
}

async function reloadPetMotions() {
  try {
    petMotions.value = await getPetMotions(props.agentId)
    if (petMotions.value.length === 0) error.value = '桌宠未启用或模型未加载(启用并保存后,待模型加载完成再读取)'
  } catch (e) {
    error.value = String((e as Error).message)
  }
}

async function testPlayMotion(name: string) {
  try {
    await playPetMotion(props.agentId, name)
  } catch (e) {
    error.value = String((e as Error).message)
  }
}

// ───────── TTS 角色(从 TTS 服务拉取) ─────────

const ttsModels = ref<string[]>([])
const ttsFetchBusy = ref(false)
const ttsFetchError = ref('')

async function fetchTtsRoles() {
  ttsFetchBusy.value = true
  ttsFetchError.value = ''
  try {
    const { fetchTtsModels } = await import('../api/client')
    ttsModels.value = await fetchTtsModels(props.agentId, settings.value.tts.server.serverUrl)
  } catch (e) {
    ttsFetchError.value = String((e as Error).message)
  } finally {
    ttsFetchBusy.value = false
  }
}

// ───────── GSV 声线可视化编辑(名称 + SoVITS/GPT 模型 + 参考音频列表 + 声线级采样) ─────────

interface GsvRefDraft { emotion: string; wav: string; text: string; lang: string }

const gsvSelName = ref('') // 声线下拉当前选中('' = 未选择),同时是采样参数的目标声线
const gsvEditName = ref('')
const gsvEditSovits = ref('')
const gsvEditGpt = ref('')
const gsvEditRefs = ref<GsvRefDraft[]>([])
const gsvModelDirs = ref<{ sovits: string[]; gpt: string[] }>({ sovits: [], gpt: [] })
const gsvNotice = ref('')

const gsvVoices = computed<any[]>(() => settings.value?.tts?.gsv?.voices ?? [])
const gsvSelVoice = computed(() => gsvVoices.value.find((v) => v.name === gsvSelName.value))

async function reloadGsvModelDirs() {
  try {
    const { getGsvModelDirs } = await import('../api/client')
    gsvModelDirs.value = await getGsvModelDirs(props.agentId, settings.value.tts.gsv.modelDir ?? '')
  } catch {
    gsvModelDirs.value = { sovits: [], gpt: [] }
  }
}

let gsvDirTimer: number | null = null
watch(
  () => settings.value?.tts?.gsv?.modelDir,
  () => {
    if (!settings.value) return
    if (gsvDirTimer !== null) window.clearTimeout(gsvDirTimer)
    gsvDirTimer = window.setTimeout(reloadGsvModelDirs, 600)
  },
)

function loadGsvEditor(name: string) {
  gsvSelName.value = name
  const vc = gsvVoices.value.find((v) => v.name === name)
  gsvEditName.value = vc?.name ?? ''
  gsvEditSovits.value = vc?.sovits ?? ''
  gsvEditGpt.value = vc?.gpt ?? ''
  gsvEditRefs.value = (vc?.refs ?? []).map((r: any) => ({
    emotion: r.emotion ?? 'neutral', wav: r.wav ?? '', text: r.text ?? '', lang: r.lang ?? 'zh',
  }))
}

function addGsvRef() {
  gsvEditRefs.value.push({ emotion: 'neutral', wav: '', text: '', lang: 'zh' })
}

function browseGsvRefWav(i: number) {
  browser.value = {
    mode: 'file', exts: ['.wav'], initial: gsvEditRefs.value[i]?.wav ?? '',
    onPick: (p: string) => {
      if (gsvEditRefs.value[i]) gsvEditRefs.value[i].wav = p
    },
  }
}

function saveGsvVoice() {
  const name = gsvEditName.value.trim()
  if (!name) {
    error.value = '请先填写声线名称。'
    return
  }
  const list: any[] = settings.value.tts.gsv.voices ??= []
  const existing = list.find((v) => v.name.toLowerCase() === name.toLowerCase())
  if (existing) {
    // 同名覆盖;改名时同步默认声线里的旧名引用;采样参数为即时编辑,原样保留
    const oldName = existing.name
    existing.name = name
    existing.sovits = gsvEditSovits.value
    existing.gpt = gsvEditGpt.value
    existing.refs = gsvEditRefs.value.map((r) => ({ ...r }))
    if (oldName !== name && (settings.value.tts.gsv.voice ?? '').toLowerCase() === oldName.toLowerCase())
      settings.value.tts.gsv.voice = name
  } else {
    list.push({ name, sovits: gsvEditSovits.value, gpt: gsvEditGpt.value, sampling: null, refs: gsvEditRefs.value.map((r) => ({ ...r })) })
  }
  gsvNotice.value = `声线 ${name} 已保存`
  gsvSelName.value = name
}

function removeGsvVoice() {
  if (!gsvSelName.value) {
    error.value = '请先在「声线」下拉选择要删除的声线'
    return
  }
  const list: any[] = settings.value.tts.gsv.voices ?? []
  const name = gsvSelName.value
  const i = list.findIndex((v) => v.name === name)
  if (i >= 0) list.splice(i, 1)
  gsvNotice.value = `声线 ${name} 已删除`
  loadGsvEditor('')
}

// 声线级采样:sampling == null 即跟随全局;取消勾选后以默认值建档,编辑即时写回选中声线
const GSV_SAMPLING_DEFAULTS = { temperature: 1.0, topK: 15, topP: 1.0, repetitionPenalty: 1.35, noiseScale: 0.5 }

const gsvGlobalProxy = computed({
  get: () => gsvSelVoice.value?.sampling == null,
  set: (v: boolean) => {
    const vc = gsvSelVoice.value
    if (!vc) return
    if (!v) vc.sampling ??= { ...GSV_SAMPLING_DEFAULTS }
    else vc.sampling = null
  },
})

function setGsvSampling(field: string, raw: string) {
  const vc = gsvSelVoice.value
  if (!vc) return
  const v = Number(raw)
  if (Number.isNaN(v)) return
  vc.sampling ??= { ...GSV_SAMPLING_DEFAULTS }
  vc.sampling[field] = v
}

// ───────── Live2D 形态/捏表情编辑(桌面端两个原生窗口的 Web 化;仅 live2d 引擎显示) ─────────

interface L2ParamRow extends Live2dParam { included: boolean }
interface L2PartRow extends Live2dPart { included: boolean }

// ── 形态(部件可见度):拖动实时预览,存为兜底形态后模型加载即应用 ──

const l2Parts = ref<L2PartRow[]>([])
const l2SavedCount = ref(0)
const l2Status = ref('')
const l2PartTimers: Record<string, number> = {}

async function reloadL2Parts() {
  try {
    const r = await getLive2dParts(props.agentId)
    const saved = r.saved ?? {}
    l2SavedCount.value = Object.keys(saved).length
    // 已存兜底形态优先回显(同桌面端 BuildPartPanel)
    l2Parts.value = r.parts.map((p) => ({ ...p, opacity: saved[p.id] ?? p.opacity }))
    syncAllRangeFills()
  } catch (e) {
    l2Parts.value = []
    l2Status.value = String((e as Error).message)
  }
}

function onL2PartInput(p: L2PartRow, v: number) {
  p.opacity = v
  window.clearTimeout(l2PartTimers[p.id])
  l2PartTimers[p.id] = window.setTimeout(() => {
    setLive2dPartOpacity(props.agentId, p.id, v).catch(() => {})
  }, 120)
}

async function saveL2Fallback() {
  try {
    const map: Record<string, number> = {}
    l2Parts.value.forEach((p) => (map[p.id] = p.opacity))
    await saveLive2dFallback(props.agentId, map)
    l2SavedCount.value = Object.keys(map).length
    l2Status.value = '已保存为默认形态,模型加载时将自动应用。'
  } catch (e) {
    l2Status.value = String((e as Error).message)
  }
}

async function clearL2Fallback() {
  try {
    await clearLive2dFallback(props.agentId)
    l2SavedCount.value = 0
    const r = await getLive2dParts(props.agentId)
    l2Parts.value = r.parts.map((p) => ({ ...p }))
    l2Status.value = '已清除默认形态,部件恢复初始状态。'
  } catch (e) {
    l2Status.value = String((e as Error).message)
  }
}

// ── 捏表情编辑态:隔离自动化、勾选捕捉、内置表情只读预览 ──

const l2EditorOpen = ref(false)
const l2ParamGroups = ref<{ id: string; name: string; rows: L2ParamRow[] }[]>([])
const l2EditorParts = ref<L2PartRow[]>([])
const l2Exprs = ref<Live2dExpr[]>([])
const l2SelExpr = ref('')
const l2EditName = ref('')
const l2EditorStatus = ref('')
const l2Dragging = ref('') // 正在拖动的滑杆行 key(拖动中该行不回写)
const l2Pending = ref<Record<string, number>>({}) // 待写模型的滑杆值(key: p:id / d:id)
let l2Timer: number | null = null
let l2Tick = 0

function l2ParamKey(id: string) {
  return 'p:' + id
}

function l2PartKey(id: string) {
  return 'd:' + id
}

async function enterL2Editor() {
  try {
    const st = await enterLive2dEditor(props.agentId)
    l2ParamGroups.value = st.parameters.map((g) => ({ ...g, rows: g.rows.map((r) => ({ ...r, included: false })) }))
    l2EditorParts.value = st.parts.map((p) => ({ ...p, included: false }))
    l2Exprs.value = st.expressions
    l2SelExpr.value = ''
    l2EditName.value = ''
    l2EditorOpen.value = true
    l2EditorStatus.value = '编辑态:动作与眨眼/呼吸/姿态已暂停;勾选要调整的变量,拖滑杆实时预览;保存后可在情绪映射里按名勾选触发。'
    if (l2Timer === null) l2Timer = window.setInterval(l2SyncTick, 200)
    syncAllRangeFills()
  } catch (e) {
    error.value = String((e as Error).message)
  }
}

async function exitL2Editor() {
  if (!l2EditorOpen.value) return
  l2EditorOpen.value = false
  if (l2Timer !== null) {
    window.clearInterval(l2Timer)
    l2Timer = null
  }
  l2Pending.value = {}
  l2Dragging.value = ''
  try {
    await exitLive2dEditor(props.agentId)
  } catch {
    // 模型可能已重载
  }
}

// 编辑态节奏:每 200ms flush 待写值;每 400ms 拉当前值(拖动中的行不回写)
async function l2SyncTick() {
  const pending = l2Pending.value
  const hasPending = Object.keys(pending).length > 0
  if (hasPending) {
    l2Pending.value = {}
    for (const [key, v] of Object.entries(pending)) {
      if (key.startsWith('p:')) setLive2dParam(props.agentId, key.slice(2), v).catch(() => {})
      else setLive2dEditorPart(props.agentId, key.slice(2), v).catch(() => {})
    }
  }
  l2Tick++
  if (l2Tick % 2 !== 0) return
  try {
    const cur = await getLive2dValues(props.agentId)
    const pv = new Map(cur.parameters.map((x) => [x.id, x.value]))
    const dv = new Map(cur.parts.map((x) => [x.id, x.value]))
    for (const g of l2ParamGroups.value)
      for (const r of g.rows) {
        if (!r.included || l2Dragging.value === l2ParamKey(r.id)) continue
        const v = pv.get(r.id)
        if (v !== undefined && Math.abs(v - r.value) > 0.005) r.value = v
      }
    for (const r of l2EditorParts.value) {
      if (!r.included || l2Dragging.value === l2PartKey(r.id)) continue
      const v = dv.get(r.id)
      if (v !== undefined && Math.abs(v - r.opacity) > 0.005) r.opacity = v
    }
  } catch {
    // 模型重载中,忽略
  }
}

function onL2ParamInput(r: L2ParamRow, v: number) {
  r.value = v
  l2Pending.value[l2ParamKey(r.id)] = v
}

function onL2EditorPartInput(r: L2PartRow, v: number) {
  r.opacity = v
  l2Pending.value[l2PartKey(r.id)] = v
}

function onL2ParamInclude(r: L2ParamRow, included: boolean) {
  r.included = included
  if (!included) {
    r.value = r.default
    setLive2dParam(props.agentId, r.id, r.default).catch(() => {})
  }
}

function onL2PartInclude(r: L2PartRow, included: boolean) {
  r.included = included
  if (!included) {
    r.opacity = r.setup
    setLive2dEditorPart(props.agentId, r.id, r.setup).catch(() => {})
  }
}

async function l2SelectExpr(name: string) {
  l2SelExpr.value = name
  l2EditName.value = name
  try {
    const r = await loadLive2dExpression(props.agentId, name)
    for (const g of l2ParamGroups.value)
      for (const row of g.rows) {
        const included = r.isCustom && r.parameters[row.id] !== undefined
        row.included = included
        row.value = included ? r.parameters[row.id] : row.default
      }
    for (const row of l2EditorParts.value) {
      const included = r.isCustom && r.parts[row.id] !== undefined
      row.included = included
      row.opacity = included ? r.parts[row.id] : row.setup
    }
    l2EditorStatus.value = r.isCustom
      ? `已载入「${name}」(勾选行 = 表情记录的变量),调整后点「保存」`
      : `「${name}」为内置表情,只读预览;自定义表情才可编辑保存`
  } catch (e) {
    l2EditorStatus.value = String((e as Error).message)
  }
}

async function l2NewExpr() {
  try {
    const r = await newLive2dExpression(props.agentId)
    l2Exprs.value = [...l2Exprs.value, { name: r.name, isCustom: true }]
    l2SelExpr.value = r.name
    l2EditName.value = r.name
    for (const g of l2ParamGroups.value)
      for (const row of g.rows) {
        row.included = false
        row.value = row.default
      }
    for (const row of l2EditorParts.value) {
      row.included = false
      row.opacity = row.setup
    }
    l2EditorStatus.value = `已新建「${r.name}」(各项默认值):勾选要调整的变量,拖滑杆后点「保存」`
  } catch (e) {
    l2EditorStatus.value = String((e as Error).message)
  }
}

async function l2SaveExpr() {
  const params: Record<string, number> = {}
  const parts: Record<string, number> = {}
  for (const g of l2ParamGroups.value) for (const r of g.rows) if (r.included) params[r.id] = r.value
  for (const r of l2EditorParts.value) if (r.included) parts[r.id] = r.opacity
  const name = l2EditName.value.trim()
  if (!name) {
    l2EditorStatus.value = '表情名字不能为空。'
    return
  }
  try {
    const r = await saveLive2dExpression(props.agentId, name, l2SelExpr.value, params, parts)
    l2Exprs.value = r.expressions
    l2SelExpr.value = r.name
    const n = Object.keys(params).length + Object.keys(parts).length
    l2EditorStatus.value = n === 0
      ? `已保存「${r.name}」。注意:未勾选任何变量,该表情不会改变外观。`
      : `已保存「${r.name}」。在情绪映射里勾选该名即可触发。`
  } catch (e) {
    l2EditorStatus.value = String((e as Error).message)
  }
}

async function l2DeleteExpr() {
  const expr = l2Exprs.value.find((x) => x.name === l2SelExpr.value)
  if (!expr) {
    l2EditorStatus.value = '先在列表中选中要删除的自定义表情。'
    return
  }
  if (!expr.isCustom) {
    l2EditorStatus.value = `「${expr.name}」为内置表情,不可删除。`
    return
  }
  if ((await ask({ title: '删除表情', message: `确定删除自定义表情「${expr.name}」吗?`, confirmLabel: '删除', danger: true })) !== true) return
  try {
    const r = await deleteLive2dExpression(props.agentId, expr.name)
    l2Exprs.value = r.expressions
    l2SelExpr.value = ''
    l2EditName.value = ''
    l2EditorStatus.value = `已删除「${expr.name}」`
  } catch (e) {
    l2EditorStatus.value = String((e as Error).message)
  }
}

async function l2ResetAll() {
  try {
    await resetLive2dParams(props.agentId)
    for (const g of l2ParamGroups.value)
      for (const r of g.rows) if (r.included) r.value = r.default
    l2EditorStatus.value = '已复位全部参数。'
  } catch (e) {
    l2EditorStatus.value = String((e as Error).message)
  }
}

watch(petEngine, (e) => {
  if (e === 'live2d') reloadL2Parts()
  else exitL2Editor()
})

onUnmounted(() => {
  // 定时器统一收口:MCP 状态轮询此前只在关模态时清除(组件销毁后泄漏);
  // Live2D 编辑器 200ms 同步循环此前离开页面后无限期继续打 REST
  if (mcpStatusTimer) {
    clearInterval(mcpStatusTimer)
    mcpStatusTimer = null
  }
  if (l2Timer !== null) {
    window.clearInterval(l2Timer)
    l2Timer = null
  }
  if (l2EditorOpen.value) exitLive2dEditor(props.agentId).catch(() => {})
})

// ───────── B站直播工具白名单(工具名勾选列表) ─────────

const biliCandidates = ref<BiliWhitelistCandidate[]>([])

// Windows 系统语音清单(SAPI;TTS windows 引擎用)
const winVoices = ref<string[]>([])

async function loadWinVoices() {
  try {
    const { getWindowsVoices } = await import('../api/client')
    winVoices.value = await getWindowsVoices(props.agentId)
  } catch {
    winVoices.value = []
  }
}

function isBiliSelected(name: string): boolean {
  return (settings.value?.biliLive?.toolWhitelist ?? []).includes(name)
}

function toggleBiliCandidate(name: string, checked: boolean) {
  const list: string[] = settings.value.biliLive.toolWhitelist ??= []
  const i = list.indexOf(name)
  if (checked && i < 0) list.push(name)
  if (!checked && i >= 0) list.splice(i, 1)
}

// 初次加载/切分类/切引擎后,同步所有滑条的左填充百分比(--fill)
function syncAllRangeFills() {
  nextTick(() =>
    document.querySelectorAll('input[type=range]').forEach((el) => {
      const t = el as HTMLInputElement
      const min = Number(t.min || 0)
      const max = Number(t.max || 100)
      t.style.setProperty('--fill', ((Number(t.value) - min) / (max - min || 1)) * 100 + '%')
    }),
  )
}

watch([active, petEngine, pluginModal, modelModal, selIdx, () => settings.value?.tts?.common?.engine], () => {
  syncAllRangeFills()
  error.value = ''
})

onMounted(async () => {
  try {
    suppressWatch = true
    settings.value = await getSettings(props.agentId)
    if (!settings.value.subAgents) settings.value.subAgents = { enabled: false, agents: [] } // 旧配置兜底
    selIdx.value = Math.max(0, settings.value.llm.providers.findIndex((p: any) => p.name === settings.value.llm.activeProvider))
    await reloadToolGroups()
    lastToolSig = toolVisibilitySig()
    biliCandidates.value = await getBiliWhitelistCandidates(props.agentId)
    petMotions.value = await getPetMotions(props.agentId).catch(() => [])
    loadAvatarRev() // 头像版本(不阻塞设置加载)
    syncAllRangeFills()
    getCompressPresets(props.agentId).then((r) => (compressPresets.value = r)).catch(() => {})
    loadFonts() // 系统字体清单预载(字体下拉为纯选择,不再手动输入)
    if ((settings.value.desktopPet?.engine ?? '') === 'live2d') reloadL2Parts()
    loadFonts()
    reloadGsvModelDirs()
    loadWinVoices()
    loadExternalPlugins() // 外部插件清单(plugins 目录;不阻塞设置加载)
    await nextTick()
    suppressWatch = false
    applyUiScale(settings.value.uiScale)
    syncAllRangeFills()
  } catch (e) {
    suppressWatch = false
    error.value = String(e)
  }
})

// 进入「内置插件」分类时刷新外部插件与技能清单(刚放入的 DLL 重启后在此出现);
// 进入「工具」分类时再拉一次工具组(兜底:插件启停、MCP 连接建立等异步生效的变化也能及时反映)
watch(active, (v) => {
  if (v === 'plugins') {
    loadExternalPlugins()
    loadSkills()
  } else if (v === 'tools') {
    reloadToolGroups()
  }
})
</script>

<template>
  <div class="settings" data-APet-anchor="settings">
    <!-- 窗体背景图:铺设置页底层,半透明侧栏透出(与聊天页同源配置,改动即时生效) -->
    <div
      v-if="settings?.windowBackground?.imagePath"
      class="win-bg"
      :style="{
        opacity: (settings.windowBackground.opacityPercent ?? 20) / 100,
        backgroundImage: `url(${bgUrl('window')})`,
        backgroundSize: winBgSize,
        backgroundRepeat: settings.windowBackground.stretch === 'Tile' ? 'repeat' : 'no-repeat',
      }"
    />
    <aside class="nav">
      <button class="ghost back" @click="goBackToChat"><span class="ms">arrow_back</span> 返回对话</button>
      <button
        v-for="c in categories"
        :key="c.key"
        class="nav-item"
        :class="{ sel: active === c.key }"
        @click="active = c.key"
      >
        {{ c.title }}
      </button>
    </aside>

    <main v-if="settings" class="pane">
      <div class="pane-inner" :key="active"><!-- key=active:切分区时整块重建并触发同款淡入过渡 -->
        <header class="pane-head">
          <h1>{{ cur?.title }}</h1>
          <span v-if="saveState === 'pending'" class="save-hint">更改待保存…</span>
          <span v-else-if="saveState === 'saving'" class="save-hint">正在保存…</span>
          <span v-else-if="saveState === 'saved'" class="saved"><span class="ms">check_circle</span> 已保存并热更新 {{ savedAt }}</span>
          <span v-else-if="saveState === 'error'" class="err">保存失败:{{ saveError }}</span>
          <button class="ghost" :title="theme === 'dark' ? '切换到亮色主题' : '切换到暗色主题'" @click="theme = toggleTheme()">
            <span class="ms">{{ theme === 'dark' ? 'dark_mode' : 'light_mode' }}</span>
          </button>
        </header>
        <p v-if="cur?.sub" class="pane-sub">{{ cur.sub }}</p>

        <!-- Agent 页置顶:头像(替代原"启用该 Agent"开关;启停在主页 Agent 卡片菜单) -->
        <template v-if="active === 'agent'">
          <section class="card">
            <div class="avatar-row">
              <img v-if="avatarRev" class="avatar-big" :src="avatarSrc" alt="头像" />
              <div v-else class="avatar-big avatar-empty"><span class="ms">person</span></div>
              <div class="avatar-side">
                <label class="label" style="margin: 0">头像(显示在主页 Agent 卡片与会话栏)</label>
                <div class="hint">PNG/JPG/WebP/GIF,≤5MB;建议使用方形图片</div>
                <div class="avatar-btns">
                  <button class="ghost" :disabled="avatarUploading" @click="avatarInput?.click()">
                    <span class="ms">upload</span> {{ avatarUploading ? '上传中…' : '上传头像' }}
                  </button>
                  <button v-if="avatarRev" class="ghost danger" @click="onAvatarRemove"><span class="ms">delete</span> 移除</button>
                  <input ref="avatarInput" type="file" accept="image/*" class="hidden-input" @change="onAvatarPicked" />
                </div>
              </div>
            </div>
          </section>
        </template>

        <!-- TTS:启用开关置顶 → 引擎卡 → 通用字段 → 按引擎切换的专属设置 -->
        <!-- 界面页:背景图上传(上传到服务端并写回 imagePath,桌面端/网页共用) -->
        <template v-if="active === 'ui'">
          <section class="card">
            <label class="label">聊天区背景图</label>
            <div class="bg-row">
              <img v-if="settings.chatBackground.imagePath && !bgErr.chat" class="bg-preview" :src="bgUrl('chat')" @error="bgErr.chat = true" />
              <div v-else class="bg-preview bg-none"><span class="ms">image</span></div>
              <div class="bg-side">
                <div class="hint" style="margin: 0">上传图片作为聊天区背景;支持 PNG/JPG/WebP/GIF/BMP,≤10MB。</div>
                <div class="avatar-btns">
                  <button class="ghost" @click="pickBg('chat')"><span class="ms">upload</span> 上传图片</button>
                  <button v-if="settings.chatBackground.imagePath" class="ghost danger" @click="clearBg('chat')"><span class="ms">delete</span> 恢复默认</button>
                </div>
                <input v-if="bgPickTarget === 'chat'" ref="bgInput" type="file" accept="image/*" class="hidden-input" @change="onBgPicked" />
              </div>
            </div>
          </section>
          <section class="card">
            <label class="label">窗体背景图</label>
            <div class="bg-row">
              <img v-if="settings.windowBackground.imagePath && !bgErr.window" class="bg-preview" :src="bgUrl('window')" @error="bgErr.window = true" />
              <div v-else class="bg-preview bg-none"><span class="ms">image</span></div>
              <div class="bg-side">
                <div class="hint" style="margin: 0">上传图片作为窗体背景;支持 PNG/JPG/WebP/GIF/BMP,≤10MB。</div>
                <div class="avatar-btns">
                  <button class="ghost" @click="pickBg('window')"><span class="ms">upload</span> 上传图片</button>
                  <button v-if="settings.windowBackground.imagePath" class="ghost danger" @click="clearBg('window')"><span class="ms">delete</span> 恢复默认</button>
                </div>
                <input v-if="bgPickTarget === 'window'" ref="bgInput" type="file" accept="image/*" class="hidden-input" @change="onBgPicked" />
              </div>
            </div>
          </section>
        </template>

        <!-- Agent 概览之外的通用字段卡:默认一字段一卡;single 分类(压缩)全部收进一张卡 -->
        <div v-if="cur && cur.fields.length" :class="['fields', { group: cur.single }]">
          <div v-for="f in cur.fields" :key="f.path" class="fcard" :class="{ wide: !cur.single && isWideField(f) }">
            <template v-if="f.type === 'bool'">
              <label class="switch-row">
                <input type="checkbox" :checked="!!getBy(f.path)" @change="setBy(f.path, ($event.target as HTMLInputElement).checked)" />
                <span>{{ f.label }}</span>
              </label>
              <div v-if="f.hint" class="hint">{{ f.hint }}</div>
            </template>
            <template v-else>
              <label class="label">{{ f.label }}</label>
              <textarea v-if="f.type === 'textarea'" class="ctl wide" rows="4"
                :value="getBy(f.path) ?? ''" @input="setBy(f.path, ($event.target as HTMLTextAreaElement).value)" />
              <AppSelect v-else-if="f.type === 'select'" :style="selStyle(f)"
                :model-value="getBy(f.path) ?? ''" :options="optList(f.options)"
                @update:model-value="setBy(f.path, $event)" />
              <div v-else-if="f.type === 'slider'" class="slider-row">
                <input type="range" class="grow" :min="f.min" :max="f.max" :step="f.step ?? 1"
                  :value="getBy(f.path) ?? f.min ?? 0" @input="onSliderInput(f, $event)"
                  @change="onSliderChange(f, $event)" />
                <span class="slider-val">{{ fmtVal(getBy(f.path)) }}</span>
              </div>
              <div v-else-if="f.type === 'percent'" class="pct-row">
                <input class="ctl" style="width: 70px" type="number" :min="f.min" :max="f.max" :value="getBy(f.path) ?? ''"
                  @input="setBy(f.path, ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value))"
                  @change="clampNum(f, $event)" />
                <span class="pct">%</span>
              </div>
              <div v-else-if="f.type === 'color'" class="color-row">
                <input type="color" :value="getBy(f.path) || '#000000'" @input="setBy(f.path, ($event.target as HTMLInputElement).value)" />
                <input class="ctl" style="width: 110px" :value="getBy(f.path) ?? ''" @input="setBy(f.path, ($event.target as HTMLInputElement).value)" />
              </div>
              <input v-else-if="f.type === 'number'" class="ctl" type="number" step="any" :style="numStyle(f)"
                :min="f.min" :max="f.max" :value="getBy(f.path) ?? ''"
                @input="setBy(f.path, ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value))"
                @change="clampNum(f, $event)" />
              <AppSelect v-else-if="f.type === 'font'" class="font-sel" style="width: 280px"
                :model-value="getBy(f.path) ?? ''" :options="fontList" placeholder="留空 = 默认字体"
                @update:model-value="setBy(f.path, $event)" />
              <div v-else-if="f.type === 'dir' || f.type === 'file'" class="path-row">
                <input class="ctl grow" :value="getBy(f.path) ?? ''" @input="setBy(f.path, ($event.target as HTMLInputElement).value)" />
                <button class="ghost" @click="openBrowser(f)">浏览…</button>
              </div>
              <div v-else-if="f.type === 'paths'" class="paths">
                <div v-for="(p, i) in getBy(f.path) ?? []" :key="i" class="path-item">
                  <span class="grow">{{ p }}</span>
                  <button class="ghost danger" @click="(getBy(f.path) as string[]).splice(i, 1)"><span class="ms">close</span></button>
                </div>
                <button class="ghost" @click="openBrowser(f)"><span class="ms">add</span> 添加…</button>
              </div>
              <div v-else-if="f.type === 'json'" class="json">
                <textarea class="ctl wide mono" rows="8" :value="JSON.stringify(getBy(f.path) ?? [], null, 2)"
                  @change="setBy(f.path, JSON.parse(($event.target as HTMLTextAreaElement).value || '[]'))" />
                <div v-if="f.hint" class="hint">{{ f.hint }}</div>
              </div>
              <input v-else class="ctl" :style="f.w ? { width: f.w + 'px' } : undefined" :value="getBy(f.path) ?? ''"
                @input="setBy(f.path, ($event.target as HTMLInputElement).value)" />
              <div v-if="f.hint && f.type !== 'json'" class="hint">{{ f.hint }}</div>
            </template>
          </div>
          <div v-if="cur.single" class="hint">仅影响发送给模型的内容,本地聊天记录不受影响。</div>
        </div>

        <!-- 压缩:按「触发 / 保留与策略 / 摘要模板」分组 -->
        <template v-if="active === 'compress'">
          <section class="card">
            <label class="label">自动压缩</label>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">启用</span>
                <span class="frow-ctl"><input type="checkbox" :checked="!!settings.autoCompress" @change="setBy('autoCompress', ($event.target as HTMLInputElement).checked)" /></span>
              </div>
              <div class="hint frow-hint">开启后,上下文占用达到触发阈值时自动把较早的历史压缩为摘要。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">触发阈值</span>
                <input class="ctl frow-ctl" style="width: 110px; flex: none" type="number" min="10" max="95" :value="getBy('compressThreshold') ?? ''"
                  @input="setBy('compressThreshold', ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value))" />
              </div>
              <div class="hint frow-hint">上下文占用达到上限的该百分比(%)时触发压缩,取值 10–95。</div>
            </div>
          </section>

          <section class="card">
            <label class="label">历史保留与策略</label>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">保留窗口</span>
                <input class="ctl frow-ctl" style="width: 110px; flex: none" type="number" min="1" max="90" :value="getBy('compressKeepPercent') ?? ''"
                  @input="setBy('compressKeepPercent', ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value))" />
              </div>
              <div class="hint frow-hint">压缩时从最新消息往前保留的百分比(%),取值 1–90。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">压缩策略</span>
                <AppSelect class="frow-ctl" style="width: 220px" :model-value="settings.compressStrategy ?? 'summarize'"
                  :options="optList([['summarize', '摘要压缩'], ['truncate', '直接截断']])"
                  @update:model-value="setBy('compressStrategy', $event)" />
              </div>
              <div class="hint frow-hint">摘要压缩 = 用摘要替换旧历史;直接截断 = 丢弃旧历史,不调用模型。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">摘要长度上限</span>
                <input class="ctl frow-ctl" style="width: 110px; flex: none" type="number" min="1" max="50" :value="getBy('compressSummaryMaxPercent') ?? ''"
                  :disabled="compressTruncate"
                  @input="setBy('compressSummaryMaxPercent', ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value))" />
              </div>
              <div class="hint frow-hint">摘要可占用上下文的上限(%),取值 1–50;截断策略下不生效。</div>
            </div>
          </section>

          <section class="card">
            <label class="label">摘要模板</label>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">模板预设</span>
                <AppSelect class="frow-ctl" style="width: 220px" :model-value="resolvedPreset"
                  :options="presetOptions" :disabled="compressTruncate"
                  @update:model-value="onPresetChange($event)" />
              </div>
              <div class="hint frow-hint">内置预设只读展示其正文;选「自定义」可编辑,切换预设不会覆盖你保存的自定义模板。</div>
            </div>
            <div class="fset">
              <textarea class="ctl wide mono" :class="{ 'tpl-readonly': !templateEditable }" rows="8" :value="presetText" :readonly="!templateEditable"
                @input="setBy('compressTemplate', ($event.target as HTMLTextAreaElement).value)" />
            </div>
          </section>
        </template>


        <template v-if="active === 'plugins'">
          <section v-for="p in PLUGINS" :key="p.key" class="card plugin-row">
            <span class="plugin-ico ms">{{ p.icon }}</span>
            <div class="plugin-meta">
              <div class="plugin-title">{{ p.title }}</div>
              <div class="hint" style="margin: 2px 0 0">{{ p.desc }}</div>
            </div>
            <label v-if="p.enablePath" class="switch-row plugin-switch" :title="'启用' + p.title">
              <input type="checkbox" :checked="!!getBy(p.enablePath)" @change="setBy(p.enablePath, ($event.target as HTMLInputElement).checked)" />
            </label>
            <button class="ghost" @click="pluginModal = p.key">设置</button>
          </section>
          <div class="hint">开关即改即存;各插件的详细设置在对应模态窗中编辑。</div>

          <!-- MCP 服务器:两级管理 —— 主页列表(启停/编辑/删除)+ 新建/编辑(表单/JSON)+ 导入 -->
          <ModalDialog v-if="pluginModal === 'mcp'" title="MCP 服务器" width="880px" @close="pluginModal = null">
            <section class="card">
              <div class="card-row">
                <span class="row-title">已配置的服务器</span>
                <span class="grow" />
                <button class="ghost" @click="loadMcpStatus()">刷新状态</button>
                <button class="ghost" @click="mcpImportOpen = true; error = ''"><span class="ms">upload</span> 导入</button>
                <button class="ghost" @click="openMcpEditor(-1)"><span class="ms">add</span> 新建</button>
              </div>
              <div v-if="!settings.mcp.servers.length" class="hint">
                还没有服务器;点「新建」或「导入」添加。示例:命令 npx,参数 -y @modelcontextprotocol/server-memory。
              </div>
              <div v-for="(s, i) in settings.mcp.servers" :key="i" class="mgr-row">
                <label class="switch-row" :title="'启用' + (s.name || s.command || s.url)">
                  <input type="checkbox" :checked="s.enabled"
                    @change="s.enabled = ($event.target as HTMLInputElement).checked" />
                </label>
                <div class="plugin-meta">
                  <div class="plugin-title">{{ s.name || s.command || s.url }}<span class="type-tag">{{ s.type || 'stdio' }}</span></div>
                  <div class="hint" style="margin: 2px 0 0">{{ s.type === 'stdio' ? [s.command, s.arguments].filter(Boolean).join(' ') : s.url }}</div>
                  <div class="mcp-status" :class="mcpStatusCls(s)">{{ mcpStatusText(s) }}</div>
                </div>
                <button class="ghost" @click="openMcpEditor(i)">编辑</button>
                <button class="ghost danger" @click="removeMcpServer(i)">删除</button>
              </div>
              <div class="hint">开关与增删即改即存,保存后自动按差异断开/重连;服务器工具以 mcp_{名称}_{工具} 暴露给模型。</div>
            </section>

            <!-- 编辑级:表单 / JSON 双模式(与截图同构) -->
            <ModalDialog v-if="mcpEditor" :title="(mcpEditorIndex >= 0 ? '编辑' : '新建') + ' MCP 服务器'" width="720px" @close="mcpEditor = null">
              <div class="card-row" style="margin-bottom: 8px">
                <span class="hint">填写 MCP 配置,保存后返回列表。</span>
                <span class="grow" />
                <div class="seg">
                  <button :class="{ on: !mcpEditorJsonMode }" @click="mcpEditorJsonMode = false">表单</button>
                  <button :class="{ on: mcpEditorJsonMode }"
                    @click="mcpEditorJsonMode = true; mcpEditorDraft = JSON.stringify(mcpEditor, null, 2)">JSON</button>
                </div>
              </div>
              <section v-if="!mcpEditorJsonMode" class="card mcp-editor">
                <div class="fset">
                  <div class="frow">
                    <span class="frow-label">名称</span>
                    <input v-model="mcpEditor.name" class="ctl frow-ctl" placeholder="my-mcp-server" />
                  </div>
                  <div class="frow">
                    <span class="frow-label">类型</span>
                    <AppSelect v-model="mcpEditor.type" class="frow-ctl" :options="mcpTypeOptions" />
                  </div>
                  <div class="frow">
                    <span class="frow-label">超时时间 MS</span>
                    <input v-model.number="mcpEditor.timeoutMs" class="ctl frow-ctl" type="number" min="1000" step="1000" />
                  </div>
                  <div class="frow">
                    <span class="frow-label">协议版本</span>
                    <AppSelect v-model="mcpEditor.protocolVersion" class="frow-ctl" :options="mcpProtocolOptions" />
                  </div>
                  <template v-if="mcpEditor.type === 'stdio'">
                    <div class="frow">
                      <span class="frow-label">命令</span>
                      <input v-model="mcpEditor.command" class="ctl frow-ctl" placeholder="npx" />
                    </div>
                    <div class="frow">
                      <span class="frow-label">参数(空格分隔)</span>
                      <input v-model="mcpEditor.arguments" class="ctl frow-ctl"
                        placeholder="-y @modelcontextprotocol/server-memory" />
                    </div>
                    <div class="frow">
                      <span class="frow-label">环境变量(可选)</span>
                      <textarea v-model="mcpEditor.envJson" class="ctl wide mono" rows="4"
                        placeholder='{ "MY_API_KEY": "your-key" }'></textarea>
                    </div>
                  </template>
                  <template v-else>
                    <div class="frow">
                      <span class="frow-label">URL</span>
                      <input v-model="mcpEditor.url" class="ctl frow-ctl" placeholder="https://example.com/mcp" />
                    </div>
                    <div class="frow">
                      <span class="frow-label">请求头(可选)</span>
                      <textarea v-model="mcpEditor.headersJson" class="ctl wide mono" rows="3"
                        placeholder='{ "Authorization": "Bearer ghp_你的PAT" }'></textarea>
                    </div>
                    <div class="hint" style="padding-left: 168px">
                      http/sse 每个请求都会带上这些头,用于 PAT/API 密钥鉴权;留空即匿名访问。
                    </div>
                  </template>
                  <div class="frow">
                    <span class="frow-label">授权</span>
                    <label class="switch-row" style="gap: 8px">
                      <input v-model="mcpEditor.trustTools" type="checkbox" />
                      <span style="font-size: 13.2px">信任此服务器的工具,调用不再询问授权</span>
                    </label>
                  </div>
                  <div class="hint" style="padding-left: 168px">
                    默认关闭:MCP 工具是外部黑盒,每次调用需确认;服务器声明只读(annotations.readOnlyHint)的工具本就自动放行。
                    完全信任的服务器才建议打开。
                  </div>
                </div>
              </section>
              <textarea v-else v-model="mcpEditorDraft" class="ctl wide mono" rows="17"></textarea>
              <div v-if="error" class="err" style="margin-top: 8px">{{ error }}</div>
              <template #footer>
                <span class="grow" />
                <button class="ghost" @click="mcpEditor = null">取消</button>
                <button @click="applyMcpEditor">保存</button>
              </template>
            </ModalDialog>

            <!-- 导入级:粘贴 JSON(单服务器 / servers 数组 / mcpServers 映射) -->
            <ModalDialog v-if="mcpImportOpen" title="导入 MCP 服务器" width="640px" @close="mcpImportOpen = false">
              <section class="card">
                <textarea v-model="mcpImportText" class="ctl wide mono" rows="13"
                  placeholder='支持:单个服务器对象 / {"servers":[…]} / {"mcpServers":{"名称":{"command":"npx","args":[…],"env":{…}}}}'></textarea>
                <div v-if="error" class="err" style="margin-top: 8px">{{ error }}</div>
              </section>
              <template #footer>
                <span class="grow" />
                <button class="ghost" @click="mcpImportOpen = false">取消</button>
                <button @click="applyMcpImport">导入</button>
              </template>
            </ModalDialog>
          </ModalDialog>

          <!-- 技能:两级管理 —— 主页列表(启停/编辑/删除)+ 新建/编辑/导入(固定存于 ~/.apet/skills) -->
          <ModalDialog v-if="pluginModal === 'skills'" title="技能" width="760px" @close="pluginModal = null">
            <section class="card">
              <div class="card-row">
                <span class="row-title">已安装的技能</span>
                <span class="grow" />
                <button class="ghost" @click="skillFileInput?.click()"><span class="ms">upload</span> 导入文件</button>
                <input ref="skillFileInput" type="file" accept=".md,.markdown,text/markdown,text/plain"
                  style="display: none" @change="onSkillFilePicked" />
                <button class="ghost" @click="openSkillEditor()"><span class="ms">add</span> 新建</button>
              </div>
              <div v-if="!skills.length && !skillsLoading" class="hint">
                还没有技能;点「新建」手动填写,或「导入文件」选择 .md 技能文件。
              </div>
              <div v-if="skillsLoading" class="hint">加载中…</div>
              <div v-for="s in skills" :key="s.name" class="mgr-row">
                <label class="switch-row" :title="'启用' + s.name">
                  <input type="checkbox" :checked="!s.disabled"
                    @change="toggleSkill(s.name, ($event.target as HTMLInputElement).checked)" />
                </label>
                <div class="plugin-meta">
                  <div class="plugin-title">{{ s.name }}<span class="type-tag" :class="{ builtin: s.builtin }">{{ s.builtin ? '内置' : '自定义' }}</span><span v-if="s.disabled" class="type-tag">已停用</span></div>
                  <div class="hint" style="margin: 2px 0 0">{{ s.description }}<span v-if="s.updatedAt" style="margin-left: 8px; opacity: .7">修改于 {{ new Date(s.updatedAt).toLocaleString() }}</span></div>
                </div>
                <button v-if="!s.builtin" class="ghost" @click="openSkillEditor(s.name)">编辑</button>
                <button v-if="!s.builtin" class="ghost danger" @click="removeSkill(s.name)">删除</button>
              </div>
              <div class="hint">技能固定存放于用户目录 .apet\skills;停用后不注入提示词,skill 工具也不可加载。</div>
            </section>

            <!-- 编辑级:名称/简介/内容(导入的文件若已带 frontmatter,保存时原样保留) -->
            <ModalDialog v-if="skillEditor" :title="(skillEditor.isNew ? '新建' : '编辑') + '技能'" width="720px" @close="skillEditor = null">
              <section class="card">
                <div class="fset">
                  <div class="frow">
                    <span class="frow-label">技能名</span>
                    <input v-model="skillEditor.name" class="ctl frow-ctl" placeholder="my-skill" :disabled="!skillEditor.isNew" />
                  </div>
                  <div class="frow">
                    <span class="frow-label">简介</span>
                    <input v-model="skillEditor.description" class="ctl frow-ctl" placeholder="这个技能做什么(会展示给模型)" />
                  </div>
                </div>
                <textarea v-model="skillEditor.content" class="ctl wide mono" rows="14"
                  placeholder="技能正文(markdown);若内容已带 --- frontmatter 将原样保存"></textarea>
                <div v-if="error" class="err" style="margin-top: 8px">{{ error }}</div>
              </section>
              <template #footer>
                <span class="grow" />
                <button class="ghost" @click="skillEditor = null">取消</button>
                <button :disabled="skillSaving" @click="saveSkillEditor">{{ skillSaving ? '保存中…' : '保存' }}</button>
              </template>
            </ModalDialog>
          </ModalDialog>

          <!-- 智能体子代理:两级管理 —— 主页列表(启停/编辑/删除)+ 新建/编辑(名称/系统提示词/工具集) -->
          <ModalDialog v-if="pluginModal === 'subagents'" title="智能体子代理" width="760px" @close="pluginModal = null">
            <section class="card">
              <div class="card-row">
                <span class="row-title">已配置的智能体</span>
                <span class="grow" />
                <button class="ghost" @click="openSubAgentEditor(-1)"><span class="ms">add</span> 新建</button>
              </div>
              <div v-if="!subAgents().agents.length" class="hint">
                还没有子代理;点「新建」添加。子代理独立运行,主 Agent 经 sub_run 委托任务、sub_wait 等待结果。
              </div>
              <div v-for="(a, i) in subAgents().agents" :key="i" class="mgr-row">
                <label class="switch-row" :title="'启用' + a.name">
                  <input type="checkbox" :checked="a.enabled"
                    @change="a.enabled = ($event.target as HTMLInputElement).checked" />
                </label>
                <div class="plugin-meta">
                  <div class="plugin-title">{{ a.name }}<span v-if="a.enabled === false" class="type-tag">已停用</span></div>
                  <div class="hint" style="margin: 2px 0 0">{{ a.systemPrompt ? (a.systemPrompt.length > 80 ? a.systemPrompt.slice(0, 77) + '…' : a.systemPrompt) : '未填写系统提示词' }}</div>
                  <div class="hint" style="margin: 2px 0 0">{{ (a.tools ?? []).map(t => SUB_AGENT_TOOLS.find(x => x.key === t)?.label ?? t).join(' / ') || '无工具' }}{{ a.model ? ` · 模型:${a.provider ? a.provider + '/' : ''}${a.model}` : '' }}{{ a.thinkingEffort ? ` · ${LEVEL_LABELS[a.thinkingEffort] ?? a.thinkingEffort}` : '' }}{{ a.maxToolIterations > 0 ? ` · 最大 ${a.maxToolIterations} 轮` : '' }}<span v-if="a.workingDirectoryAccess" :title="'工作目录权限'"> · {{ ACCESS.find(x => x[0] === a.workingDirectoryAccess)?.[1] ?? a.workingDirectoryAccess }}</span></div>
                </div>
                <button class="ghost" @click="openSubAgentEditor(i)">编辑</button>
                <button class="ghost danger" @click="removeSubAgent(i)">删除</button>
              </div>
              <div class="hint">开关与增删即改即存;启用后模型提示词会注入可用子代理清单,并发上限 4 个,停止主对话会同时终止运行中的子代理。</div>
            </section>

            <!-- 编辑级:名称 / 系统提示词 / 工具集 / 权限 / 模型与思考等级 / 最大轮数(每项独立卡片,间距一致) -->
            <ModalDialog v-if="subAgentEditor" :title="(subAgentEditorIndex >= 0 ? '编辑' : '新建') + '智能体'" width="640px" @close="subAgentEditor = null">
              <section class="card">
                <label class="label">名称</label>
                <input v-model="subAgentEditor.name" class="ctl" style="width: 100%" placeholder="例如:代码审查员" />
              </section>
              <section class="card">
                <label class="label">系统提示词</label>
                <textarea v-model="subAgentEditor.systemPrompt" class="ctl wide" rows="8"
                  placeholder="描述这个子代理的职责与工作方式,例如:你是资深代码审查员,阅读给定代码后输出问题清单与修改建议。"></textarea>
                <div class="hint">子代理看不到主对话,任务上下文全部由 sub_run 的 prompt 提供。</div>
              </section>
              <section class="card">
                <label class="label">工具集</label>
                <div class="hint" style="margin: 0 0 8px">点选暴露给该子代理的内置能力(可多选)。</div>
                <div class="chip-row">
                  <button v-for="t in SUB_AGENT_TOOLS" :key="t.key" class="chip-toggle"
                    :class="{ on: subAgentEditor.tools.includes(t.key) }" @click="toggleSubAgentTool(t.key)">
                    <span v-if="subAgentEditor.tools.includes(t.key)" class="ms">check</span>{{ t.label }}
                  </button>
                </div>
              </section>
              <section class="card">
                <label class="label">权限(工作目录内)</label>
                <AppSelect
                  style="width: 280px"
                  :model-value="subAgentEditor.workingDirectoryAccess"
                  :options="ACCESS"
                  @update:model-value="subAgentEditor.workingDirectoryAccess = $event"
                />
                <div class="hint">子代理在主会话工作目录内的操作权限,默认只读;工作目录本身继承主会话,目录外权限与白名单也随主会话。</div>
              </section>
              <section class="card">
                <label class="label">模型</label>
                <div class="sub-mm">
                  <button ref="subModelBtn" type="button" class="sub-mm-btn" @click="toggleSubModelMenu">
                    <span class="sub-mm-label">{{ subModelLabel().name }}</span>
                    <span v-if="subModelLabel().prov" class="sub-mm-prov">{{ subModelLabel().prov }}</span>
                    <span class="ms" aria-hidden="true">expand_more</span>
                  </button>
                </div>
                <div class="hint">「继承主 Agent」= 跟随聊天界面当前所选供应商/模型;显式选择时按所选供应商的设定连接。模型清单在「模型服务」设置里维护。</div>
              </section>
              <section class="card">
                <label class="label">思考等级</label>
                <div class="hint" style="margin: 0 0 8px">「继承」= 跟随聊天界面当前所选的思考等级。</div>
                <div class="chip-row">
                  <button v-for="lv in subThinkingOptions" :key="lv.value" class="chip-toggle"
                    :class="{ on: subAgentEditor.thinkingEffort === lv.value }" @click="subAgentEditor.thinkingEffort = lv.value">
                    <span v-if="subAgentEditor.thinkingEffort === lv.value" class="ms">check</span>{{ lv.label }}
                  </button>
                </div>
              </section>
              <section class="card">
                <label class="label">最大工具轮数</label>
                <input class="ctl" style="width: 140px" type="number" min="0" max="1000"
                  :value="subAgentEditor.maxToolIterations"
                  @input="subAgentEditor.maxToolIterations = ($event.target as HTMLInputElement).value === '' ? 0 : Math.max(0, Math.min(1000, Number(($event.target as HTMLInputElement).value)))" />
                <div class="hint">仅对该子代理生效;0 = 不限轮数(任务跑完为止),设上限后超出即停。运行中的任务不受影响。</div>
              </section>
              <div v-if="error" class="err" style="margin-top: 8px">{{ error }}</div>
              <template #footer>
                <span class="grow" />
                <button class="ghost" @click="subAgentEditor = null">取消</button>
                <button @click="applySubAgentEditor">保存</button>
              </template>
              <!-- 模型级联弹窗:Teleport 到 body 避免被模态框滚动区裁剪 -->
              <Teleport to="body">
                <div v-if="subModelMenu && subAgentEditor" class="sub-mm-pop" :style="subModelPopStyle" @mouseleave="subHoverProv = ''">
                  <div class="sub-mm-current" :class="{ off: !!subAgentEditor.model }" @click="pickSubInherit">
                    <span class="sub-mm-check"><span v-if="!subAgentEditor.model" class="ms">check</span></span>
                    <span class="sub-mm-name">继承主 Agent</span>
                  </div>
                  <div class="sub-mm-sep" />
                  <div v-for="p in subProviders" :key="p.name" class="sub-mm-prov-row" @mouseenter="subHoverProv = p.name">
                    <span>{{ p.name }}</span>
                    <span class="sub-mm-arrow"><span class="ms">chevron_right</span></span>
                    <div v-if="subHoverProv === p.name" class="sub-mm-sub" :class="subModelFlyLeft ? 'fly-left' : ''">
                      <div
                        v-for="m in (p.models || [])"
                        :key="m.name"
                        class="sub-mm-model"
                        :class="{ cur: subAgentEditor.provider === p.name && subAgentEditor.model === m.name }"
                        @click="pickSubModel(p.name, m.name)"
                      >
                        <span class="sub-mm-check"><span v-if="subAgentEditor.provider === p.name && subAgentEditor.model === m.name" class="ms">check</span></span>
                        <span class="sub-mm-name">{{ m.name }}</span>
                      </div>
                      <div v-if="!(p.models || []).length" class="sub-mm-empty">清单为空:到「模型服务」拉取/添加模型</div>
                    </div>
                  </div>
                  <div v-if="!subProviders.length" class="sub-mm-empty">还没有供应商:先到「模型服务」添加</div>
                </div>
              </Teleport>
            </ModalDialog>
          </ModalDialog>

          <!-- 外部插件:plugins 目录 DLL,设置表单按插件声明的 [PluginSetting] 字段自动生成 -->
          <div v-if="extPlugins.length" class="card-row" style="margin-top: 6px">
            <span class="row-title">外部插件</span>
            <span class="grow" />
            <span class="hint">来自部署目录 plugins\ 文件夹,更新 DLL 后重启生效</span>
          </div>
          <section v-for="p in extPlugins" :key="p.name" class="card plugin-row">
            <span class="plugin-ico ms">extension</span>
            <div class="plugin-meta">
              <div class="plugin-title">
                {{ p.name }}
                <span v-if="p.version" class="hint">v{{ p.version }}</span>
                <span v-if="p.author" class="hint">· {{ p.author }}</span>
                <span v-if="p.providesData" class="hint">· 提供面板数据</span>
              </div>
              <div v-if="p.description" class="hint" style="margin: 1px 0 0">{{ p.description }}</div>
              <div class="hint" style="margin: 2px 0 0">{{ p.settings.length }} 项设置 · {{ p.enabled ? '已启用' : '已停用' }}</div>
            </div>
            <label class="switch-row plugin-switch" :title="'启用' + p.name">
              <input type="checkbox" :checked="p.enabled" @change="toggleExtPlugin(p, ($event.target as HTMLInputElement).checked)" />
            </label>
            <button class="ghost" title="清空设置回到默认值" @click="resetExtPlugin(p)">重置</button>
            <!-- 设置入口唯一:插件自带 UI 资产时打开其自定义界面,否则打开按 [PluginSetting] 自动生成的通用表单 -->
            <button v-if="p.hasUi" class="ghost" @click="openExtUi(p)">设置</button>
            <button v-else-if="p.settings.length" class="ghost" @click="openExtModal(p)">设置</button>
          </section>
          <div v-if="!extPlugins.length" class="hint">
            外部插件:把引用 APet.Framework 并继承 AgentPluginBase 的 DLL 放入部署目录 plugins\ 文件夹,重启后出现在这里(见 docs/external-plugin-guide.md)。
          </div>

          <!-- 外部插件通用设置模态:按描述符渲染,保存走独立校验端点 -->
          <ModalDialog v-if="extModalPlugin" :title="extModalPlugin.name + ' · 外部插件'" width="720px" @close="extModalName = null">
            <section v-for="g in extModalGroups" :key="g.name" class="card">
              <div v-if="g.name !== '常规'" class="row-title" style="margin-bottom: 8px">{{ g.name }}</div>
              <div v-for="s in g.items" :key="s.name" class="fset">
                <div class="frow">
                  <span class="frow-label" :title="s.name">{{ s.label || s.name }}</span>

                  <input v-if="extControlType(s) === 'bool'" type="checkbox" class="plain-check frow-ctl" style="width: auto"
                    :checked="!!extDraft[s.name]" @change="extDraft[s.name] = ($event.target as HTMLInputElement).checked" />

                  <template v-else-if="extControlType(s) === 'slider'">
                    <input type="range" class="frow-ctl" style="flex: 1"
                      :min="Number.isFinite(s.min) && Math.abs(s.min) < 1e15 ? s.min : 0"
                      :max="Number.isFinite(s.max) && Math.abs(s.max) < 1e15 ? s.max : 100"
                      :step="s.valueType === 'int' ? 1 : 'any'"
                      :value="Number(extDraft[s.name] ?? 0)"
                      @input="extDraft[s.name] = Number(($event.target as HTMLInputElement).value)" />
                    <span class="slider-val">{{ extDraft[s.name] }}</span>
                  </template>

                  <input v-else-if="extControlType(s) === 'number'" class="ctl frow-ctl" style="width: 160px" type="number"
                    :step="s.valueType === 'int' ? 1 : 'any'"
                    :value="extDraft[s.name] ?? ''"
                    @input="extDraft[s.name] = ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value)" />

                  <select v-else-if="extControlType(s) === 'select'" class="ctl frow-ctl"
                    :value="String(extDraft[s.name] ?? '')"
                    @change="extDraft[s.name] = ($event.target as HTMLSelectElement).value">
                    <option v-for="o in s.options" :key="o" :value="o">{{ o }}</option>
                  </select>

                  <template v-else-if="extControlType(s) === 'color'">
                    <input type="color" class="frow-ctl" style="width: 46px; height: 30px; padding: 2px; flex: none"
                      :value="/^#[0-9a-fA-F]{6}$/.test(String(extDraft[s.name] ?? '')) ? String(extDraft[s.name]) : '#000000'"
                      @input="extDraft[s.name] = ($event.target as HTMLInputElement).value" />
                    <input class="ctl" style="width: 110px" :value="extDraft[s.name] ?? ''"
                      @input="extDraft[s.name] = ($event.target as HTMLInputElement).value" />
                  </template>

                  <textarea v-else-if="extControlType(s) === 'textarea' || extControlType(s) === 'json'" class="ctl wide mono" rows="4"
                    :value="extControlType(s) === 'json' ? JSON.stringify(extDraft[s.name] ?? null, null, 2) : String(extDraft[s.name] ?? '')"
                    @change="onExtTextArea(s, ($event.target as HTMLTextAreaElement).value)" />

                  <input v-else class="ctl frow-ctl" :type="extControlType(s) === 'password' ? 'password' : 'text'"
                    :value="extDraft[s.name] ?? ''"
                    @input="extDraft[s.name] = ($event.target as HTMLInputElement).value" />
                </div>
                <div v-if="s.description" class="hint frow-hint">{{ s.description }}</div>
              </div>
            </section>
            <template #footer>
              <span class="grow" />
              <button class="ghost" @click="extModalName = null">取消</button>
              <button :disabled="extSaving" @click="saveExtPlugin">{{ extSaving ? '保存中…' : '保存' }}</button>
            </template>
          </ModalDialog>

          <!-- 外部插件自定义界面:同源 iframe 渲染 DLL 内嵌 ui.* 资产(插件页经 /apet-bridge.js 桥读写设置/数据面/宿主 DOM) -->
          <ModalDialog v-if="extUi" :title="extUi.name + ' · 设置'" width="960px" @close="extUi = null">
            <div style="height: 520px">
              <PluginUiFrame :agent-id="props.agentId" :plugin-name="extUi.name" slot="settings" :session-id="extUi.sessionId" />
            </div>
          </ModalDialog>
        </template>

        <!-- 模型服务:列表选中即生效,一次编辑一个服务 -->
        <template v-if="active === 'llm'">
          <section class="card">
            <div class="card-row">
              <span class="row-title">服务</span>
              <span class="grow" />
              <AppSelect v-model="newProviderTemplate" style="width: 150px" :options="templateOptions" />
              <button class="ghost" @click="addProvider"><span class="ms">add</span> 添加</button>
              <button class="ghost" @click="removeProvider">删除</button>
            </div>
            <div class="provider-list">
              <button
                v-for="(p, i) in settings.llm.providers"
                :key="i"
                class="list-item"
                :class="{ sel: i === selIdx }"
                @click="selectProvider(i)"
              >
                {{ p.name || '(未命名)' }}
              </button>
            </div>
            <div class="hint">选中的服务即生效;同一服务可保存多条配置(如不同的 API Key),重名将自动追加序号。</div>
          </section>

          <section class="card">
            <label class="label">名称</label>
            <input class="ctl" style="width: 280px" :value="curProvider.name"
              @input="curProvider.name = ($event.target as HTMLInputElement).value; syncActive()"
              @change="uniquifyProviderName" />
          </section>
          <section class="card">
            <label class="label">服务地址(OpenAI 兼容接口,一般以 /v1 结尾)</label>
            <input class="ctl" style="width: 100%" :value="curProvider.baseUrl" @input="curProvider.baseUrl = ($event.target as HTMLInputElement).value" />
          </section>
          <section class="card">
            <label class="label">API Key</label>
            <input class="ctl" style="width: 100%" type="password" :value="curProvider.apiKey"
              @input="curProvider.apiKey = ($event.target as HTMLInputElement).value" />
          </section>
          <section class="card">
            <label class="label">思考字段(端点接受思考开关的字段样式)</label>
            <AppSelect
              style="width: 280px"
              :model-value="providerThinkingField(curProvider)"
              :options="[
                { value: 'reasoning_effort', label: 'reasoning_effort(带强度)' },
                { value: 'thinking', label: 'thinking(仅开关)' },
                { value: 'enable_thinking', label: 'enable_thinking(布尔开关)' },
                { value: 'all', label: 'all(全字段同发)' },
                { value: 'off', label: 'off(不发送)' },
              ]"
              @update:model-value="curProvider.thinkingField = $event"
            />
            <div class="hint">不同端点接受的字段名不同,发错字段会被 400,按你所用端点的文档选样式;思考强度在聊天输入框工具栏按模型选择。</div>
          </section>
          <section class="card">
            <label class="label">默认模型</label>
            <AppSelect
              style="width: 280px"
              :model-value="providerDefaultModel(curProvider)"
              :options="providerDefaultModelOptions(curProvider)"
              placeholder="未设置(模型列表为空时使用)"
              @update:model-value="curProvider.model = $event"
            />
            <div class="hint">聊天工具栏切换到该供应商时回落到此模型;新对话不再自动指定模型(沿用当前浏览会话的选择)。</div>
          </section>
          <section class="card">
            <div class="card-row">
              <span class="row-title">模型列表</span>
              <span class="grow" />
              <button class="ghost" :disabled="fetchBusy[selIdx]" @click="fetchModels(selIdx)">
                {{ fetchBusy[selIdx] ? '拉取中…' : '⟳ 拉取模型' }}
              </button>
            </div>
            <div class="card-row">
              <AppSelect editable class="grow" :model-value="modelDraft[selIdx] ?? ''"
                :options="fetchedModels[selIdx] ?? []" placeholder="拉取后从这里选择模型,或直接手动输入模型名"
                @update:model-value="modelDraft[selIdx] = $event" @enter="addModelFromFetch" />
              <button class="ghost" @click="addModelFromFetch"><span class="ms">add</span> 添加模型</button>
            </div>
            <div v-if="fetchError[selIdx]" class="err">{{ fetchError[selIdx] }}</div>
            <div class="model-list">
              <div v-for="(m, j) in curProvider.models" :key="j" class="model-row">
                <input class="name-inp" :value="m.name" placeholder="模型名" @input="m.name = ($event.target as HTMLInputElement).value" />
                <span class="grow" />
                <button class="ghost" @click="modelModal = j">设置</button>
                <button class="ghost" @click="curProvider.models.splice(j, 1)">删除</button>
              </div>
              <div v-if="!curProvider.models?.length" class="hint" style="padding: 4px 10px">暂无模型:拉取列表后选择添加,或手动输入模型名。聊天界面只能使用这里添加的模型。</div>
            </div>
          </section>

          <!-- 单模型设置(模态):上下文/MaxTokens/思考等级/媒体类型集中编辑 -->
          <ModalDialog
            v-if="curModelSel"
            :title="`模型设置 · ${curModelSel.name || '(未命名)'}`"
            sub="上下文与输出上限;思考等级与媒体类型决定聊天界面的可选项"
            width="680px"
            @close="modelModal = null"
          >
            <section class="card">
              <div class="fset">
                <div class="frow">
                  <span class="frow-label">上下文大小</span>
                  <input class="ctl frow-ctl" style="width: 140px; flex: none" type="number" :value="curModelSel.contextTokens"
                    @input="curModelSel.contextTokens = Number(($event.target as HTMLInputElement).value)" />
                  <label class="check" style="margin-left: auto">
                    <input v-model="curModelSel.preferLocal" type="checkbox" />
                    本地值优先
                  </label>
                </div>
                <div class="hint frow-hint">模型上下文窗口的 Token 上限,必须大于 0;未勾选「本地值优先」时优先采用服务端上报值,取不到再回退本地。</div>
              </div>
              <div class="fset">
                <div class="frow">
                  <span class="frow-label">MaxTokens</span>
                  <input class="ctl frow-ctl" style="width: 140px; flex: none" type="number" :value="curModelSel.maxTokens"
                    @input="curModelSel.maxTokens = Number(($event.target as HTMLInputElement).value)" />
                </div>
                <div class="hint frow-hint">单次回复的最大 Token 数;0 表示不限制(请求不携带该字段)。</div>
              </div>
            </section>
            <section class="card">
              <label class="label">思考等级</label>
              <div class="hint" style="margin: 0 0 8px">聊天界面思考下拉显示的档位;全部勾选 = 不限制,至少保留一个。</div>
              <div class="chip-row">
                <button
                  v-for="lv in THINKING"
                  :key="lv"
                  class="chip-toggle"
                  :class="{ on: advLevels(curModelSel).includes(lv) }"
                  :title="LEVEL_EN[lv] ?? lv"
                  @click="toggleLevelChip(curModelSel, lv)"
                >
                  <span v-if="advLevels(curModelSel).includes(lv)" class="ms">check</span>{{ LEVEL_LABELS[lv] ?? lv }}
                </button>
              </div>
            </section>
            <section class="card">
              <label class="label">支持的媒体类型</label>
              <div class="hint" style="margin: 0 0 8px">文本/文件附件始终可用;勾选的类型出现在聊天框 ＋ 菜单,按 file 内容片段发送(需端点支持)。</div>
              <div class="chip-row">
                <button class="chip-toggle on" disabled title="文本/文件附件始终可用"><span class="ms">lock</span>文件</button>
                <button
                  v-for="[t, label] in MEDIA_ITEMS"
                  :key="t"
                  class="chip-toggle"
                  :class="{ on: advMedia(curModelSel, t) }"
                  @click="toggleMediaChip(curModelSel, t)"
                >
                  <span v-if="advMedia(curModelSel, t)" class="ms">check</span>{{ label }}
                </button>
              </div>
            </section>
            <section class="card">
              <label class="label">计费因子(元/百万 tokens;0 = 该项不计费)</label>
              <div class="fset">
                <div class="frow" style="gap: 16px; flex-wrap: wrap">
                  <div>
                    <div class="frow-label">输入价格</div>
                    <input class="ctl frow-ctl" style="width: 120px; flex: none" type="number" min="0" step="0.01"
                      :value="curModelSel.priceInput ?? 0"
                      @input="curModelSel.priceInput = Number(($event.target as HTMLInputElement).value)" />
                  </div>
                  <div>
                    <div class="frow-label">输入缓存价格</div>
                    <input class="ctl frow-ctl" style="width: 120px; flex: none" type="number" min="0" step="0.01"
                      :value="curModelSel.priceInputCached ?? 0"
                      @input="curModelSel.priceInputCached = Number(($event.target as HTMLInputElement).value)" />
                  </div>
                  <div>
                    <div class="frow-label">输出价格</div>
                    <input class="ctl frow-ctl" style="width: 120px; flex: none" type="number" min="0" step="0.01"
                      :value="curModelSel.priceOutput ?? 0"
                      @input="curModelSel.priceOutput = Number(($event.target as HTMLInputElement).value)" />
                  </div>
                </div>
                <div class="hint frow-hint">单位:元/百万 tokens。每次请求按实际用量累计会话费用(输入按「输入价格」、其中缓存命中部分按「输入缓存价格」,输出与思考按「输出价格」),状态栏显示累计金额。</div>
              </div>
            </section>
          </ModalDialog>

          <section class="card">
            <label class="label">网络代理(对模型请求全局生效)</label>
            <AppSelect style="width: 280px" :model-value="settings.llm.proxyMode"
              :options="[{ value: 'system', label: '跟随系统' }, { value: 'none', label: '不使用' }, { value: 'custom', label: '自定义' }]"
              @update:model-value="settings.llm.proxyMode = $event" />
          </section>
          <section v-if="settings.llm.proxyMode === 'custom'" class="card">
            <label class="label">代理地址(例如 http://127.0.0.1:7890)</label>
            <input class="ctl" style="width: 100%" :value="settings.llm.proxyUrl" @input="settings.llm.proxyUrl = ($event.target as HTMLInputElement).value" />
          </section>
        </template>

        <!-- 工具:循环次数 + 终端 + 功能组 -->
        <template v-if="active === 'tools'">
          <section class="card">
            <label class="label">当前工具清单(下方开关生效后会实时刷新;共 {{ activeTools.length }} 项提供给模型;MCP 工具按服务器聚合,悬浮查看列表)</label>
            <div class="tool-chips">
              <span v-for="c in activeToolChips" :key="c.key" class="tool-chip" :class="{ mcp: c.mcp }" :title="c.title">{{ c.label }}</span>
              <span v-if="!activeTools.length" class="tool-chip none">暂无启用工具</span>
            </div>
          </section>
          <section class="card">
            <label class="label">最大循环次数(单轮对话中允许连续调用工具的最大次数;0 = 不限)</label>
            <input class="ctl" style="width: 160px" type="number" :value="settings.maxToolIterations ?? ''"
              @input="settings.maxToolIterations = ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value)" />
          </section>
          <section class="card">
            <label class="label">终端(「运行终端」与后台任务统一使用的 shell;保存即生效)</label>
            <AppSelect style="width: 220px" :model-value="settings.terminalShell"
              :options="[['bash', 'Git-Bash'], ['cmd', 'CMD'], ['powershell', 'PowerShell']]"
              @update:model-value="settings.terminalShell = $event" />
          </section>
          <section v-for="g in groups" :key="g.name" class="card tool-group">
            <div class="tool-group-row">
              <input type="checkbox" :checked="g.tools.every((t) => t.enabled)" @change="toggleGroup(g)" />
              <div class="grow">
                <div class="tool-group-name">{{ g.name }}</div>
                <div class="tool-group-desc">{{ g.description }}</div>
                <div class="tool-group-members">{{ g.tools.map((t) => t.displayName).join('、') }}</div>
              </div>
            </div>
          </section>
          <!-- 折叠卡片:在插件页单独管控而被主清单隐藏的工具(MCP 服务器工具/B站直播等),仅保留逐工具开关 -->
          <section class="card tool-group">
            <div class="tool-group-row">
              <button type="button" class="ghost tool-collapse-btn" :title="showHiddenTools ? '收起' : '展开'" @click="showHiddenTools = !showHiddenTools">
                <span class="ms">{{ showHiddenTools ? 'expand_less' : 'expand_more' }}</span>
              </button>
              <div class="grow">
                <div class="tool-group-name">其他</div>
                <div class="tool-group-desc">除以上列出的工具之外的工具(在插件页单独管控,组级开关请到对应插件页)。</div>
              </div>
            </div>
            <div v-if="showHiddenTools" class="tool-hidden-list">
              <div v-for="t in hiddenTools" :key="t.name" class="tool-hidden-row">
                <label class="check tool-hidden-check">
                  <input type="checkbox" :checked="t.enabled" @change="toggleHiddenTool(t, ($event.target as HTMLInputElement).checked)" />
                  <span>{{ t.displayName }}</span>
                </label>
                <div class="tool-hidden-desc">{{ t.name }}</div>
              </div>
              <div v-if="!hiddenTools.length" class="hint">暂无隐藏工具</div>
            </div>
          </section>
        </template>

        <!-- 桌宠:按引擎切换布局(只显示当前引擎的设置)+ 气泡设置(同桌面端并入本页) -->
        <ModalDialog v-if="pluginModal === 'pet'" title="桌宠插件设置" width="840px" @close="pluginModal = null">
          <section class="card">
            <div class="card-row">
              <label class="switch-row">
                <input v-model="settings.desktopPet.enabled" type="checkbox" />
                <span>启用桌宠</span>
              </label>
              <span class="row-title" style="margin-left: 12px">渲染引擎</span>
              <AppSelect style="width: 160px" v-model="settings.desktopPet.engine" :options="ENGINE_OPTIONS" />
            </div>
            <div class="field-block">
              <label class="label">{{ PET_MODEL_META[petEngine].label }}</label>
              <div class="path-row">
                <input class="ctl grow" :value="petModelValue" @input="petModelValue = ($event.target as HTMLInputElement).value" />
                <button class="ghost" @click="openPetModelBrowser">浏览…</button>
              </div>
            </div>
            <div v-if="petEngine === 'spine'" class="field-block">
              <label class="label">图集路径(留空时自动在模型同目录查找)</label>
              <div class="path-row">
                <input class="ctl grow" :value="settings.desktopPet.spine.atlasPath ?? ''"
                  @input="settings.desktopPet.spine.atlasPath = ($event.target as HTMLInputElement).value" />
                <button class="ghost" @click="browser = { mode: 'file', path: settings.desktopPet.spine.atlasPath ?? '', exts: ['.atlas'] }">浏览…</button>
              </div>
            </div>
            <div class="field-block">
              <label class="label">大小(100% 为模型原始尺寸,拖动即时生效)</label>
              <div class="slider-row">
                <input type="range" class="grow" min="0.05" max="1.5" step="0.01"
                  :value="settings.desktopPet.scale" @input="settings.desktopPet.scale = Number(($event.target as HTMLInputElement).value)" />
                <span class="slider-val">{{ Math.round((settings.desktopPet.scale ?? 1) * 100) }}%</span>
              </div>
            </div>
          </section>

          <section v-if="petEngine === 'vrm'" class="card">
            <div class="field-block">
              <label class="label">渲染风格</label>
              <AppSelect style="width: 180px" :model-value="settings.desktopPet.vrm.shadingStyle"
                :options="[['mtoon', '官方 (MToon)'], ['toon', '二次元 (Toon)'], ['pbr', '写实 (PBR)']]"
                @update:model-value="settings.desktopPet.vrm.shadingStyle = $event" />
            </div>
            <div class="field-block">
              <label class="label">VRMA 动作(情绪映射里可绑定,名 = 文件基名)</label>
              <button class="ghost" @click="openVrmaBrowser"><span class="ms">add</span> 导入 VRMA 动作…</button>
              <div v-for="(p, i) in settings.desktopPet.vrm.animationPaths ?? []" :key="i" class="path-item" style="margin-top: 6px">
                <span class="grow">{{ p }}</span>
                <button class="ghost danger" @click="settings.desktopPet.vrm.animationPaths.splice(i, 1)">删除</button>
              </div>
              <div v-if="!(settings.desktopPet.vrm.animationPaths ?? []).length" class="hint">尚未导入 VRMA 动作</div>
            </div>
          </section>

          <section v-if="petEngine === 'basic'" class="card">
            <label class="switch-row">
              <input v-model="settings.desktopPet.basic.fadeTransition" type="checkbox" />
              <span>过渡效果(切换动作时淡入淡出)</span>
            </label>
            <div class="card-row" style="margin-top: 8px">
              <span class="row-title">过渡时长(毫秒,50–5000)</span>
              <input class="ctl" style="width: 90px" type="number" :value="settings.desktopPet.basic.fadeDurationMs"
                @input="settings.desktopPet.basic.fadeDurationMs = Number(($event.target as HTMLInputElement).value)" />
            </div>
          </section>

          <section class="card">
            <div class="card-row">
              <button class="ghost" @click="reloadPetMotions">⟳ 读取动作列表</button>
              <span v-if="petMotions.length === 0" class="hint" style="margin-top: 0">未读到动作:启用桌宠 + 设置模型,保存并待模型加载后再读取</span>
              <span v-else class="hint" style="margin-top: 0">已加载 {{ petMotions.length }} 个动作</span>
            </div>
            <label class="label">情绪对应的动作(Agent 回复携带该情绪标签时,从勾选的动作中随机播放其一)</label>
            <div class="emotion-grid">
              <div class="emotion-col">
                <div
                  v-for="(tag, i) in emotionTags"
                  :key="tag + i"
                  class="emotion-row"
                  :class="{ sel: emotionIndex === i }"
                  @click="emotionIndex = i"
                >
                  <input
                    class="emotion-inp"
                    :value="tag"
                    :disabled="tag === 'idle'"
                    :title="tag === 'idle' ? 'idle(待机)不可删除/重命名' : '双击外区域后可直接改名'"
                    @change="renameEmotion(i, ($event.target as HTMLInputElement).value)"
                  />
                  <span
                    v-if="tag !== 'idle'"
                    class="emo-del"
                    title="删除该情绪"
                    @click.stop="removeEmotionAt(tag)"
                  ><span class="ms">close</span></span>
                </div>
                <button class="emotion-add" title="添加情绪" @click="addEmotion"><span class="ms">add</span> 添加情绪</button>
              </div>
              <div class="motion-col">
                <div v-for="m in motionOptions" :key="m" class="motion-row">
                  <label class="check">
                    <input
                      type="checkbox"
                      class="plain-check"
                      :checked="isMotionChecked(emotionTags[emotionIndex] ?? 'idle', m)"
                      @change="toggleMotion(emotionTags[emotionIndex] ?? 'idle', m, ($event.target as HTMLInputElement).checked)"
                    />
                    <span class="grow" :title="petMotionDetail(m)">{{ petMotionDetail(m) }}</span>
                  </label>
                  <button class="ghost mini" title="测试播放" @click="testPlayMotion(m)"><span class="ms">play_arrow</span></button>
                </div>
                <div v-if="motionOptions.length === 0" class="hint">尚无动作:读取动作列表后勾选;也可直接在此前的映射上修改</div>
              </div>
            </div>
            <div class="card-row" style="margin-top: 12px">
              <div class="fset">
                <div class="frow">
                  <span class="frow-label">待机触发间隔</span>
                  <input class="ctl frow-ctl" style="width: 110px; flex: none" type="number" :value="settings.desktopPet.idleIntervalSeconds"
                    @input="settings.desktopPet.idleIntervalSeconds = Number(($event.target as HTMLInputElement).value)" />
                  <span class="frow-label" style="width: auto">随机差 ±</span>
                  <input class="ctl" style="width: 110px; flex: none" type="number" :value="settings.desktopPet.idleJitterSeconds"
                    @input="settings.desktopPet.idleJitterSeconds = Number(($event.target as HTMLInputElement).value)" />
                  <span class="hint" style="margin: 0">秒</span>
                </div>
              </div>
            </div>
            <template v-if="petEngine === 'spine'">
              <section class="card">
                <label class="label">眨眼(待机时循环播放的眨眼动作;「无」则不眨眼)</label>
                <div class="fset">
                  <div class="frow">
                    <span class="frow-label">眨眼动作</span>
                    <AppSelect class="frow-ctl" style="width: 240px" :model-value="settings.desktopPet.spine.blinkMotion ?? ''"
                      :options="[{ value: '', label: '无' }, ...motionOptions.map((m) => ({ value: m, label: petMotionDetail(m) }))]"
                      @update:model-value="settings.desktopPet.spine.blinkMotion = $event" />
                  </div>
                </div>
                <div class="fset">
                  <div class="frow">
                    <span class="frow-label">眨眼触发间隔</span>
                    <input class="ctl frow-ctl" style="width: 110px; flex: none" type="number" :value="settings.desktopPet.blinkIntervalSeconds"
                      @input="settings.desktopPet.blinkIntervalSeconds = Number(($event.target as HTMLInputElement).value)" />
                    <span class="frow-label" style="width: auto">随机差 ±</span>
                    <input class="ctl" style="width: 110px; flex: none" type="number" :value="settings.desktopPet.blinkJitterSeconds"
                      @input="settings.desktopPet.blinkJitterSeconds = Number(($event.target as HTMLInputElement).value)" />
                    <span class="hint" style="margin: 0">秒</span>
                  </div>
                </div>
              </section>
            </template>
            <div v-if="petEngine === 'live2d'" class="card-row" style="margin-top: 12px">
              <label class="check"><input v-model="settings.desktopPet[engineKey].autoEyeBlink" type="checkbox" /> 自动眨眼</label>
              <label class="check"><input v-model="settings.desktopPet[engineKey].breathEnabled" type="checkbox" /> 呼吸动画</label>
            </div>
          </section>

          <!-- Live2D 专属:形态(部件可见度)与捏表情编辑器 -->
          <section v-if="petEngine === 'live2d'" class="card">
            <label class="label">形态(部件可见度):拖动滑杆实时预览;「存为默认形态」后模型每次加载完成即应用为默认基线,表情淡出后回到该基线(而非 setup 姿态)</label>
            <div class="l2-rows">
              <div v-for="p in l2Parts" :key="p.id" class="l2-row nock">
                <span class="l2-name" :title="p.id">{{ p.id }}</span>
                <input type="range" min="0" max="1" step="0.01" :value="p.opacity"
                  @input="onL2PartInput(p, Number(($event.target as HTMLInputElement).value))" />
                <span class="l2-val">{{ p.opacity.toFixed(2) }}</span>
              </div>
            </div>
            <div v-if="l2Parts.length === 0" class="hint">未读到部件:启用桌宠并加载 Live2D 模型后进入本页</div>
            <div class="card-row">
              <button class="ghost" @click="saveL2Fallback">存为默认形态</button>
              <button class="ghost" @click="clearL2Fallback">清空默认形态</button>
              <span class="hint" style="margin-top: 0">
                {{ l2SavedCount > 0 ? `已存 ${l2SavedCount} 项兜底` : '未保存兜底形态(用 setup 姿态)' }} {{ l2Status }}
              </span>
            </div>
          </section>

          <section v-if="petEngine === 'live2d'" class="card">
            <div class="card-row">
              <label class="label" style="margin: 0; flex: 1">捏表情(隔离编辑:勾选变量 → 拖滑杆 → 存为自定义表情;写入模型同目录 sidecar,情绪映射按名触发)</label>
              <button v-if="!l2EditorOpen" class="ghost" @click="enterL2Editor">进入编辑态</button>
              <button v-else class="ghost" @click="exitL2Editor">退出编辑态</button>
            </div>
            <template v-if="l2EditorOpen">
              <div class="card-row">
                <span class="row-title">表情</span>
                <AppSelect style="width: 240px" :model-value="l2SelExpr"
                  :options="l2Exprs.map((x) => ({ value: x.name, label: x.isCustom ? x.name : x.name + '(内置)' }))"
                  @update:model-value="l2SelectExpr($event)" />
                <input class="ctl" style="width: 150px" v-model="l2EditName" placeholder="表情名(保存时用)" />
                <button class="ghost" @click="l2NewExpr"><span class="ms">add</span> 新建</button>
                <button class="ghost" @click="l2SaveExpr">保存</button>
                <button class="ghost danger" @click="l2DeleteExpr">删除</button>
                <button class="ghost" title="全部参数复位默认值" @click="l2ResetAll">复位</button>
              </div>
              <div v-if="l2EditorStatus" class="hint">{{ l2EditorStatus }}</div>
              <div class="l2-editor">
                <div v-for="g in l2ParamGroups" :key="g.id" class="l2-group">
                  <div class="l2-group-name">{{ g.name }}</div>
                  <div v-for="r in g.rows" :key="r.id" class="l2-row" :class="{ dim: !r.included }">
                    <input type="checkbox" :checked="r.included" @change="onL2ParamInclude(r, ($event.target as HTMLInputElement).checked)" />
                    <span class="l2-name" :title="r.id">{{ r.name === r.id ? r.id : `${r.name} (${r.id})` }}</span>
                    <input type="range" :min="r.min" :max="r.max" :step="(r.max - r.min) / 100" :value="r.value"
                      :disabled="!r.included"
                      @pointerdown="l2Dragging = l2ParamKey(r.id)"
                      @pointerup="l2Dragging = ''"
                      @pointercancel="l2Dragging = ''"
                      @input="onL2ParamInput(r, Number(($event.target as HTMLInputElement).value))" />
                    <span class="l2-val">{{ r.value.toFixed(2) }}</span>
                  </div>
                </div>
                <div v-if="l2EditorParts.length" class="l2-group">
                  <div class="l2-group-name">部件(不透明度 0 = 隐藏;勾选后随表情保存)</div>
                  <div v-for="r in l2EditorParts" :key="r.id" class="l2-row" :class="{ dim: !r.included }">
                    <input type="checkbox" :checked="r.included" @change="onL2PartInclude(r, ($event.target as HTMLInputElement).checked)" />
                    <span class="l2-name" :title="r.id">{{ r.id }}</span>
                    <input type="range" min="0" max="1" step="0.01" :value="r.opacity" :disabled="!r.included"
                      @pointerdown="l2Dragging = l2PartKey(r.id)"
                      @pointerup="l2Dragging = ''"
                      @pointercancel="l2Dragging = ''"
                      @input="onL2EditorPartInput(r, Number(($event.target as HTMLInputElement).value))" />
                    <span class="l2-val">{{ r.opacity.toFixed(2) }}</span>
                  </div>
                </div>
              </div>
            </template>
          </section>

          <section class="card">
            <label class="label">情绪标签样式</label>
            <AppSelect style="width: 160px" :model-value="settings.desktopPet.tagStyle"
              :options="optList(TAG_STYLES)" @update:model-value="settings.desktopPet.tagStyle = $event" />
          </section>

          <section class="card">
            <label class="label">点击行为</label>
            <div v-for="[key, text] in [['clickAction', '单击'], ['doubleClickAction', '双击'], ['rightClickAction', '右键']] as const" :key="key" class="card-row">
              <span class="row-title" style="width: 48px">{{ text }}</span>
              <AppSelect style="width: 180px" :model-value="settings.desktopPet[key]"
                :options="optList(PET_ACTIONS)" @update:model-value="settings.desktopPet[key] = $event" />
            </div>
          </section>

          <!-- 对话气泡:位置微调 / 字体 / 外观(同桌面端并入桌宠页;更改后宠物旁自动弹出预览气泡) -->
          <section class="card">
            <label class="label">气泡位置微调(单位像素;空间不足时气泡自动移至宠物侧边)</label>
            <div class="grid-row">
              <span class="gl">上偏移</span>
              <input type="range" min="-300" max="300" step="1" :value="settings.chatBubble.topOffset"
                @input="settings.chatBubble.topOffset = Number(($event.target as HTMLInputElement).value)" />
              <span class="slider-val">{{ settings.chatBubble.topOffset }}</span>
            </div>
            <div class="grid-row">
              <span class="gl">左偏移</span>
              <input type="range" min="-300" max="300" step="1" :value="settings.chatBubble.leftOffset"
                @input="settings.chatBubble.leftOffset = Number(($event.target as HTMLInputElement).value)" />
              <span class="slider-val">{{ settings.chatBubble.leftOffset }}</span>
            </div>
            <div class="grid-row">
              <span class="gl">右偏移</span>
              <input type="range" min="-300" max="300" step="1" :value="settings.chatBubble.rightOffset"
                @input="settings.chatBubble.rightOffset = Number(($event.target as HTMLInputElement).value)" />
              <span class="slider-val">{{ settings.chatBubble.rightOffset }}</span>
            </div>
            <div class="hint">调整时宠物旁会弹出预览气泡实时显示效果。</div>
          </section>

          <section class="card">
            <label class="label">气泡字体(调整时宠物旁会弹出预览气泡)</label>
            <div class="grid-row">
              <span class="gl">字体</span>
              <AppSelect style="width: 280px" :model-value="settings.chatBubble.fontFamily ?? ''"
                :options="fontList" placeholder="留空 = 默认字体"
                @update:model-value="settings.chatBubble.fontFamily = $event" />
            </div>
            <div class="grid-row">
              <span class="gl">字号(磅)</span>
              <input type="range" min="6" max="48" step="1" :value="settings.chatBubble.fontSize"
                @input="settings.chatBubble.fontSize = Number(($event.target as HTMLInputElement).value)" />
              <span class="slider-val">{{ settings.chatBubble.fontSize }}</span>
            </div>
            <div class="grid-row">
              <span class="gl">字形</span>
              <AppSelect style="width: 280px" :model-value="settings.chatBubble.fontStyle ?? 'Regular'"
                :options="FONT_STYLES" @update:model-value="settings.chatBubble.fontStyle = $event" />
            </div>
          </section>

          <section class="card">
            <label class="label">气泡外观(颜色为不透明色;整体透明用不透明度控制)</label>
            <div class="grid-row">
              <span class="gl">文字颜色</span>
              <div class="color-row">
                <input type="color" :value="settings.chatBubble.textColor || '#000000'" @input="settings.chatBubble.textColor = ($event.target as HTMLInputElement).value" />
                <input class="ctl" style="width: 110px" :value="settings.chatBubble.textColor ?? ''" @input="settings.chatBubble.textColor = ($event.target as HTMLInputElement).value" />
              </div>
            </div>
            <div class="grid-row">
              <span class="gl">气泡底色</span>
              <div class="color-row">
                <input type="color" :value="settings.chatBubble.fillColor || '#000000'" @input="settings.chatBubble.fillColor = ($event.target as HTMLInputElement).value" />
                <input class="ctl" style="width: 110px" :value="settings.chatBubble.fillColor ?? ''" @input="settings.chatBubble.fillColor = ($event.target as HTMLInputElement).value" />
              </div>
            </div>
            <div class="grid-row">
              <span class="gl">边框颜色</span>
              <div class="color-row">
                <input type="color" :value="settings.chatBubble.borderColor || '#000000'" @input="settings.chatBubble.borderColor = ($event.target as HTMLInputElement).value" />
                <input class="ctl" style="width: 110px" :value="settings.chatBubble.borderColor ?? ''" @input="settings.chatBubble.borderColor = ($event.target as HTMLInputElement).value" />
              </div>
            </div>
            <div class="grid-row">
              <span class="gl">边框粗细(0 = 无边框)</span>
              <input type="range" min="0" max="10" step="0.5" :value="settings.chatBubble.borderWidth"
                @input="settings.chatBubble.borderWidth = Number(($event.target as HTMLInputElement).value)" />
              <span class="slider-val">{{ settings.chatBubble.borderWidth }}</span>
            </div>
            <div class="grid-row">
              <span class="gl">圆角半径</span>
              <input type="range" min="0" max="40" step="1" :value="settings.chatBubble.cornerRadius"
                @input="settings.chatBubble.cornerRadius = Number(($event.target as HTMLInputElement).value)" />
              <span class="slider-val">{{ settings.chatBubble.cornerRadius }}</span>
            </div>
            <div class="grid-row">
              <span class="gl">尾巴长度(0 = 无尾巴圆角卡)</span>
              <input type="range" min="0" max="40" step="1" :value="settings.chatBubble.tailLength"
                @input="settings.chatBubble.tailLength = Number(($event.target as HTMLInputElement).value)" />
              <span class="slider-val">{{ settings.chatBubble.tailLength }}</span>
            </div>
            <div class="grid-row">
              <span class="gl">气泡最大宽</span>
              <input type="range" min="160" max="800" step="10" :value="settings.chatBubble.maxWidth"
                @input="settings.chatBubble.maxWidth = Number(($event.target as HTMLInputElement).value)" />
              <span class="slider-val">{{ settings.chatBubble.maxWidth }}</span>
            </div>
            <div class="grid-row">
              <span class="gl">整体不透明度(%)</span>
              <input type="range" min="30" max="100" step="1" :value="settings.chatBubble.opacityPercent"
                @input="settings.chatBubble.opacityPercent = Number(($event.target as HTMLInputElement).value)" />
              <span class="slider-val">{{ settings.chatBubble.opacityPercent }}</span>
            </div>
          </section>
          <div class="card-row">
            <button class="ghost" @click="firePreview"><span class="ms">visibility</span> 预览气泡</button>
          </div>
        </ModalDialog>

        <ModalDialog v-if="pluginModal === 'tts'" title="TTS 语音插件设置" width="760px" @close="pluginModal = null">
          <section class="card">
            <label class="switch-row">
              <input type="checkbox" :checked="!!settings.tts.common.enabled" @change="setBy('tts.common.enabled', ($event.target as HTMLInputElement).checked)" />
              <span>启用语音</span>
            </label>
            <div class="hint">关闭后该 Agent 不再朗读回复;对话中也可用标签临时控制</div>
          </section>
          <section class="card">
            <label class="label">合成引擎</label>
            <AppSelect style="width: 220px" :model-value="settings.tts.common.engine"
              :options="optList(TTS_ENGINES)" @update:model-value="settings.tts.common.engine = $event" />
            <div class="hint">下方只显示当前引擎的设置;切换引擎即时保存</div>
          </section>
          <section class="card">
            <label class="label">朗读时机</label>
            <AppSelect style="width: 280px" :model-value="settings.tts.common.timing ?? 'PerSentence'"
              :options="optList([['PerSentence', '逐句朗读'], ['AfterReply', '整段回复后朗读']])"
              @update:model-value="setBy('tts.common.timing', $event)" />
          </section>
          <section class="card">
            <label class="switch-row">
              <input type="checkbox" :checked="!!settings.tts.common.skipInnerThought" @change="setBy('tts.common.skipInnerThought', ($event.target as HTMLInputElement).checked)" />
              <span>不朗读内心想法(圆括号)</span>
            </label>
          </section>
          <section class="card">
            <label class="label">语速倍率</label>
            <div class="slider-row">
              <input type="range" class="grow" min="0.1" max="4" step="0.05" :value="settings.tts.common.speed ?? 1"
                @input="setSlider('tts.common.speed', $event)" />
              <span class="slider-val">{{ fmtVal(settings.tts.common.speed) }}</span>
            </div>
          </section>
          <section class="card">
            <label class="label">播放音量</label>
            <div class="slider-row">
              <input type="range" class="grow" min="0" max="1" step="0.05" :value="settings.tts.common.volume ?? 1"
                @input="setSlider('tts.common.volume', $event)" />
              <span class="slider-val">{{ fmtVal(settings.tts.common.volume) }}</span>
            </div>
          </section>
        <template v-if="pluginModal === 'tts' && settings.tts.common.engine === 'server'">
          <section class="card">
            <label class="label">服务地址</label>
            <input class="ctl" style="width: 100%" :value="settings.tts.server.serverUrl" @input="settings.tts.server.serverUrl = ($event.target as HTMLInputElement).value" />
          </section>
          <section class="card">
            <label class="label">角色/模型(对应服务端 /v1/models 的 id;空 = 当前常驻)</label>
            <div class="path-row">
              <AppSelect editable class="grow" :model-value="settings.tts.server.model ?? ''"
                :options="ttsModels" placeholder="留空 = 服务端默认角色;可拉取后选择"
                @update:model-value="settings.tts.server.model = $event" />
              <button class="ghost" :disabled="ttsFetchBusy" @click="fetchTtsRoles">{{ ttsFetchBusy ? '拉取中…' : '⟳ 拉取角色' }}</button>
            </div>
            <div v-if="ttsFetchError" class="err">{{ ttsFetchError }}</div>
          </section>
          <section class="card">
            <label class="label">音色(voice 参数;空 = 服务端默认;对话中可用标签临时切换)</label>
            <input class="ctl" style="width: 100%" :value="settings.tts.server.voice" @input="settings.tts.server.voice = ($event.target as HTMLInputElement).value" />
          </section>
        </template>

        <section v-if="pluginModal === 'tts' && settings.tts.common.engine === 'windows'" class="card">
          <label class="label">系统语音角色(来自本机 SAPI 清单;空 = 系统默认)</label>
          <AppSelect editable :model-value="settings.tts.windows.sapiVoice ?? ''"
            :options="winVoices" placeholder="留空 = 系统默认;可拉取后选择"
            @update:model-value="settings.tts.windows.sapiVoice = $event" />
          <div v-if="winVoices.length === 0" class="hint">未读到系统语音:本机可能未安装 SAPI 语音包</div>
        </section>

        <template v-if="pluginModal === 'tts' && settings.tts.common.engine === 'gsv'">
          <section class="card">
            <div class="field-block">
              <label class="label">默认声线(名或 名/情绪,如 youxiang 或 youxiang/happy;留空 = 引擎默认)</label>
              <AppSelect editable class="grow" :model-value="settings.tts.gsv.voice ?? ''"
                :options="gsvVoices.map((v) => v.name)" placeholder="留空 = 引擎默认;可输入 名 或 名/情绪"
                @update:model-value="settings.tts.gsv.voice = $event" />
            </div>
            <div class="field-block">
              <label class="label">模型包目录(含 manifest.json)</label>
              <div class="path-row">
                <input class="ctl grow" :value="settings.tts.gsv.modelDir" @input="settings.tts.gsv.modelDir = ($event.target as HTMLInputElement).value" />
                <button class="ghost" @click="browser = { mode: 'dir', path: settings.tts.gsv.modelDir ?? '' }">浏览…</button>
              </div>
            </div>
            <div class="field-block">
              <label class="label">计算设备(gpu 需 cuDNN,缺失自动降级 CPU)</label>
              <AppSelect style="width: 140px" :model-value="settings.tts.gsv.device"
                :options="['auto', 'gpu', 'cpu']" @update:model-value="settings.tts.gsv.device = $event" />
            </div>
            <div class="card-row">
              <span class="row-title">标点停顿(秒)</span>
              <input class="ctl" style="width: 90px" type="number" step="0.05" :value="settings.tts.gsv.cutMute"
                @input="settings.tts.gsv.cutMute = Number(($event.target as HTMLInputElement).value)" />
              <span class="row-title">切块阈值(字)</span>
              <input class="ctl" style="width: 90px" type="number" step="0.1" :value="settings.tts.gsv.cutMinlen"
                @input="settings.tts.gsv.cutMinlen = Number(($event.target as HTMLInputElement).value)" />
            </div>
          </section>

          <section class="card">
            <div class="card-row">
              <span class="row-title">声线编辑</span>
              <AppSelect style="width: 280px" :model-value="gsvSelName"
                :options="[{ value: '', label: '未选择' }, ...gsvVoices.map((v) => ({ value: v.name, label: v.name }))]"
                @update:model-value="loadGsvEditor($event)" />
            </div>
            <div class="field-block">
              <span class="sub-label">声线名称</span>
              <input class="ctl" style="width: 280px" v-model="gsvEditName" />
            </div>
            <div class="field-block">
              <span class="sub-label">SoVITS 模型(voices/ 子目录)</span>
              <AppSelect style="width: 280px" v-model="gsvEditSovits"
                :options="gsvModelDirs.sovits" placeholder="未选择 = 底模" />
              <div v-if="gsvModelDirs.sovits.length === 0" class="hint">未找到:把 SoVITS onnx 放到 模型包/voices/&lt;模型名&gt;/</div>
            </div>
            <div class="field-block">
              <span class="sub-label">GPT 模型(gpt/ 子目录)</span>
              <AppSelect style="width: 280px" v-model="gsvEditGpt"
                :options="gsvModelDirs.gpt" placeholder="未选择 = 底模" />
              <div v-if="gsvModelDirs.gpt.length === 0" class="hint">未找到:把 GPT onnx 放到 模型包/gpt/&lt;模型名&gt;/</div>
            </div>
            <div class="field-block">
              <span class="sub-label">参考音频列表(每条可标情绪,如 neutral/happy;对话中的情绪标签将切到该条参考)</span>
              <div v-for="(r, i) in gsvEditRefs" :key="i" class="gsv-ref-row">
                <input class="ctl" style="width: 86px" v-model="r.emotion" title="情绪标签(对话中的情绪标签将切到该条参考)" placeholder="情绪" />
                <input class="ctl" style="width: 60px" v-model="r.lang" title="参考文本语言 zh/en/ja" placeholder="语言" />
                <input class="ctl grow" v-model="r.wav" title="参考音频 wav 路径" placeholder="参考音频 wav 路径" />
                <button class="ghost mini" title="选择参考音频 wav" @click="browseGsvRefWav(i)">…</button>
                <input class="ctl grow2" v-model="r.text" title="参考音频对应的文字(供引擎提取音色)" placeholder="参考音频对应的文字" />
                <button class="ghost mini" @click="gsvEditRefs.splice(i, 1)">删除</button>
              </div>
              <div v-if="gsvEditRefs.length === 0" class="hint">尚无参考音频:添加 wav + 对应文本(每条可标情绪,如 neutral/happy)</div>
              <div class="card-row">
                <button class="ghost" @click="addGsvRef"><span class="ms">add</span> 添加参考</button>
                <button class="ghost" @click="saveGsvVoice">保存声线</button>
                <button class="ghost" @click="removeGsvVoice">删除声线</button>
                <span v-if="gsvNotice" class="saved" @click="gsvNotice = ''">{{ gsvNotice }} <span class="ms">close</span></span>
              </div>
            </div>
          </section>

          <section class="card">
            <label class="label">采样参数(当前声线:{{ gsvSelName || '未选择' }})</label>
            <label class="switch-row">
              <input v-model="gsvGlobalProxy" type="checkbox" :disabled="!gsvSelVoice" />
              <span>跟随全局 TTS 采样设置</span>
            </label>
            <div class="gsv-sampling">
              <span class="sub-label">温度</span>
              <input class="ctl" style="width: 72px" type="number" step="any" :disabled="gsvGlobalProxy || !gsvSelVoice"
                :value="gsvSelVoice?.sampling?.temperature ?? ''" @input="setGsvSampling('temperature', ($event.target as HTMLInputElement).value)" />
              <span class="sub-label">top_k</span>
              <input class="ctl" style="width: 72px" type="number" step="any" :disabled="gsvGlobalProxy || !gsvSelVoice"
                :value="gsvSelVoice?.sampling?.topK ?? ''" @input="setGsvSampling('topK', ($event.target as HTMLInputElement).value)" />
              <span class="sub-label">top_p</span>
              <input class="ctl" style="width: 72px" type="number" step="any" :disabled="gsvGlobalProxy || !gsvSelVoice"
                :value="gsvSelVoice?.sampling?.topP ?? ''" @input="setGsvSampling('topP', ($event.target as HTMLInputElement).value)" />
              <span class="sub-label">重复惩罚</span>
              <input class="ctl" style="width: 72px" type="number" step="any" :disabled="gsvGlobalProxy || !gsvSelVoice"
                :value="gsvSelVoice?.sampling?.repetitionPenalty ?? ''" @input="setGsvSampling('repetitionPenalty', ($event.target as HTMLInputElement).value)" />
              <span class="sub-label">噪声</span>
              <input class="ctl" style="width: 72px" type="number" step="any" :disabled="gsvGlobalProxy || !gsvSelVoice"
                :value="gsvSelVoice?.sampling?.noiseScale ?? ''" @input="setGsvSampling('noiseScale', ($event.target as HTMLInputElement).value)" />
            </div>
            <div v-if="!gsvSelVoice" class="hint">先在上方「声线编辑」选择一个声线后编辑其采样参数。</div>
          </section>
        </template>
        </ModalDialog>

        <ModalDialog v-if="pluginModal === 'memory'" title="记忆插件设置" width="620px" @close="pluginModal = null">
          <section class="card">
            <div class="fset">
              <div class="frow">
                <span class="frow-label">自动保留天数</span>
                <input class="ctl frow-ctl" style="width: 140px; flex: none" type="number" :value="settings.memory.forgetAfterDays ?? 0"
                  @input="setBy('memory.forgetAfterDays', Number(($event.target as HTMLInputElement).value))" />
              </div>
              <div class="hint frow-hint">超过 N 天未被使用或召回的记忆将被自动清理;0 表示永不清理。</div>
            </div>
          </section>
        </ModalDialog>

        <ModalDialog v-if="pluginModal === 'bili'" title="BiliLive 直播互动插件设置" width="760px" @close="pluginModal = null">
          <section class="card">
            <div class="fset">
              <div class="frow">
                <span class="frow-label">直播间号</span>
                <input class="ctl frow-ctl" :value="settings.biliLive.roomCode" @input="setBy('biliLive.roomCode', ($event.target as HTMLInputElement).value)" />
              </div>
              <div class="hint frow-hint">要接入的 B 站直播间房间号;保存后即监听该直播间的弹幕与礼物事件。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">Cookie(可选)</span>
                <input class="ctl frow-ctl" :value="settings.biliLive.cookie" @input="setBy('biliLive.cookie', ($event.target as HTMLInputElement).value)" />
              </div>
              <div class="hint frow-hint">提供登录 Cookie 后以登录身份连接;留空则以游客身份连接。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">合并窗口(毫秒)</span>
                <input class="ctl frow-ctl" style="width: 140px; flex: none" type="number" :value="settings.biliLive.mergeWindowMs ?? 0"
                  @input="setBy('biliLive.mergeWindowMs', Number(($event.target as HTMLInputElement).value))" />
              </div>
              <div class="hint frow-hint">该时间窗内到达的多条事件会合并为一轮对话,避免高频刷屏。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">最小回复间隔(毫秒)</span>
                <input class="ctl frow-ctl" style="width: 140px; flex: none" type="number" :value="settings.biliLive.minIntervalMs ?? 0"
                  @input="setBy('biliLive.minIntervalMs', Number(($event.target as HTMLInputElement).value))" />
              </div>
              <div class="hint frow-hint">两次自动回复之间强制休息的最短时间。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">事件队列容量(1-500)</span>
                <input class="ctl frow-ctl" style="width: 140px; flex: none" type="number" :value="settings.biliLive.maxQueue ?? 100"
                  @input="setBy('biliLive.maxQueue', Number(($event.target as HTMLInputElement).value))" />
              </div>
              <div class="hint frow-hint">待处理事件的缓冲队列上限;溢出时丢弃最旧的事件。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">屏蔽关键词</span>
                <input class="ctl frow-ctl" :value="settings.biliLive.blockKeywords" @input="setBy('biliLive.blockKeywords', ($event.target as HTMLInputElement).value)" />
              </div>
              <div class="hint frow-hint">命中任一关键词的事件不触发回复,多个关键词用英文逗号分隔。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">回应弹幕</span>
                <span class="frow-ctl"><input type="checkbox" :checked="!!settings.biliLive.respondDanmaku" @change="setBy('biliLive.respondDanmaku', ($event.target as HTMLInputElement).checked)" /></span>
              </div>
              <div class="hint frow-hint">普通弹幕触发对话回复。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">回应礼物</span>
                <span class="frow-ctl"><input type="checkbox" :checked="!!settings.biliLive.respondGift" @change="setBy('biliLive.respondGift', ($event.target as HTMLInputElement).checked)" /></span>
              </div>
              <div class="hint frow-hint">收到礼物时进行致谢回复。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">回应 SC</span>
                <span class="frow-ctl"><input type="checkbox" :checked="!!settings.biliLive.respondSc" @change="setBy('biliLive.respondSc', ($event.target as HTMLInputElement).checked)" /></span>
              </div>
              <div class="hint frow-hint">醒目留言(超级留言)触发回复。</div>
            </div>
            <div class="fset">
              <div class="frow">
                <span class="frow-label">回应互动</span>
                <span class="frow-ctl"><input type="checkbox" :checked="!!settings.biliLive.respondInteract" @change="setBy('biliLive.respondInteract', ($event.target as HTMLInputElement).checked)" /></span>
              </div>
              <div class="hint frow-hint">进入直播间、关注等互动事件触发回复。</div>
            </div>
          </section>
          <section class="card">
            <label class="label">直播轮工具白名单</label>
            <div class="hint" style="margin: 0 0 8px">勾选后,B 站事件触发的一轮对话仅可调用这些工具;留空表示不限制。</div>
            <div class="bili-whitelist">
            <label
              v-for="c in biliCandidates"
              :key="c.name"
              class="check bili-row"
              :class="{ grey: c.disabled }"
            >
              <input
                type="checkbox"
                :checked="isBiliSelected(c.name)"
                :disabled="c.disabled"
                @change="toggleBiliCandidate(c.name, ($event.target as HTMLInputElement).checked)"
              />
              <span class="grow">{{ c.label }}</span>
            </label>
            <div v-if="biliCandidates.length === 0" class="hint">未读到可用工具</div>
            </div>
          </section>
        </ModalDialog>

        <ModalDialog v-if="pluginModal === 'scene'" title="情景插件设置" width="760px" @close="pluginModal = null">
        <section class="card">
          <div v-for="(s, i) in settings.scenePresets" :key="i" class="scene">
            <div class="scene-head">
              <input class="ctl grow" :value="s.name" placeholder="预设名($scene 使用)" @input="s.name = ($event.target as HTMLInputElement).value" />
              <button class="ghost danger" @click="settings.scenePresets.splice(i, 1)">删除</button>
            </div>
            <textarea class="ctl wide" rows="3" :value="s.content" placeholder="注入 system prompt 的情景描述" @input="s.content = ($event.target as HTMLTextAreaElement).value" />
          </div>
          <button class="ghost" @click="settings.scenePresets.push({ name: '', content: '' })"><span class="ms">add</span> 添加情景</button>
        </section>
        </ModalDialog>

        <div v-if="error" class="err" style="margin-top: 10px">{{ error }}</div>
      </div>
    </main>

    <main v-else class="pane"><div class="loading">加载中…</div></main>

    <FileBrowser
      v-if="browser"
      :mode="browser.mode"
      :initial="browser.initial"
      :exts="browser.exts"
      @select="onBrowserSelect"
      @cancel="browser = null"
    />

    <!-- 确认模态框(删除技能/表情等) -->
    <ActionDialog v-if="dlg" v-bind="dlg" @confirm="settle" @close="settle(null)" />

    <!-- 全局 toast:fixed 定位不受滚动/分类切换影响;点击可提前关闭 -->
    <div
      class="toast"
      :class="[toastKind, { show: toastVisible }]"
      role="status"
      @click="dismissToast"
    >
      <span class="ms">{{ toastKind === 'err' ? 'cancel' : toastKind === 'warn' ? 'warning' : 'check_circle' }}</span>
      {{ toastMsg }}
    </div>
  </div>
</template>

<style scoped>
.settings {
  height: 100%;
  display: flex;
  overflow: hidden;
  /* 窗体背景层用负 z-index 垫底:isolation 限制在本页容器内(同聊天页做法) */
  position: relative;
  isolation: isolate;
}

/* 窗体背景:铺满设置页,位于一切内容之下;左栏半透明可透出 */
.win-bg {
  position: absolute;
  inset: 0;
  z-index: -1;
  background-position: center;
  pointer-events: none;
}

/* ───── 左侧分类:240px 半透明列 + ListBox 规格(悬停 Selection、选中 Accent) ───── */

.nav {
  width: 240px;
  flex: none;
  background: var(--bg-panel-translucent);
  padding: 12px 0 10px;
  display: flex;
  flex-direction: column;
  overflow-y: auto;
}

.back {
  margin: 0 12px 10px;
  text-align: center;
  text-decoration: none;
  display: block;
  color: var(--fg-primary);
}

.nav-item {
  background: transparent;
  border: none;
  color: var(--fg-primary);
  text-align: left;
  padding: 10px 8px;
  margin: 2px 6px;
  border-radius: 6px;
  cursor: pointer;
  font-size: 14.3px;
  font-family: var(--font);
}

.nav-item:hover {
  background: var(--selection);
}

.nav-item.sel {
  background: var(--accent);
  color: #fff;
}

/* ───── 右侧内容列:28/22 边距、760 上限;卡片 620 半透明圆角10(SettingCard 规格) ───── */

.pane {
  flex: 1;
  overflow-y: auto;
  padding: 22px 28px 40px;
}

.pane-inner {
  max-width: 880px;
  display: flex;
  flex-direction: column;
  gap: 10px;
  animation: pane-in 180ms ease-out;
}
@keyframes pane-in {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}

.pane-head {
  display: flex;
  align-items: center;
  gap: 14px;
}

.pane-head h1 {
  font-size: 19.8px;
  font-weight: 600;
  margin: 0 0 10px;
  flex: 1;
}

.pane-sub {
  color: var(--fg-secondary);
  font-size: 12.1px;
  margin: 0;
}

.saved {
  color: var(--success);
  font-size: 13.2px;
  cursor: pointer;
}

/* 全局 toast:视口底部居中,fixed 定位随滚动/切分类始终可见;淡入淡出 0.25s */
.toast {
  position: fixed;
  left: 50%;
  bottom: 28px;
  transform: translateX(-50%);
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
  font-size: 14.3px;
  line-height: 1.4;
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.25s;
  cursor: pointer;
}

.toast.show {
  opacity: 1;
  pointer-events: auto;
}

.toast .ms {
  flex: none;
  font-size: 19.8px;
}

.toast.ok .ms {
  color: var(--success);
}

.toast.warn .ms {
  color: var(--warning);
}

.toast.err {
  border-color: var(--danger);
}

.toast.err .ms {
  color: var(--danger);
}

.save-hint {
  color: var(--fg-secondary);
  font-size: 13.2px;
}

.err {
  color: var(--danger);
  font-size: 13.2px;
}

.fcard,
.card {
  background: transparent;
  border-radius: 10px;
  padding: 12px 14px;
  margin: 0;
  width: auto;
  max-width: 100%;
  box-sizing: border-box;
  transition: background 0.12s;
}

.fcard:hover,
.card:hover {
  background: var(--bg-panel-hover);
}

/* single 分类(压缩):外壳即卡片,内部字段不再带卡片底 */
.fields {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(330px, 1fr));
  gap: 10px;
  align-items: stretch;
}

.fields > .fcard {
  margin: 0;
}

/* 长内容字段(文本域/路径/滑条/JSON 等)独占整行 */
.fcard.wide {
  grid-column: 1 / -1;
}

.fields.group {
  display: block;
  background: transparent;
  border-radius: 10px;
  padding: 12px 14px;
  margin: 0;
  width: auto;
  max-width: 100%;
  box-sizing: border-box;
}

.fields.group > .fcard {
  background: none;
  padding: 0;
  margin: 0 0 14px;
  width: auto;
}

.fields.group > .fcard:last-of-type {
  margin-bottom: 0;
}

.label {
  display: block;
  font-size: 14.3px;
  font-weight: 600;
  color: var(--fg-primary);
  margin-bottom: 6px;
}

.sub-label {
  display: block;
  font-size: 12.1px;
  color: var(--fg-secondary);
  margin-bottom: 4px;
}

.gsv-ref-row {
  display: flex;
  align-items: center;
  gap: 6px;
  background: var(--bg-input);
  border-radius: 6px;
  padding: 6px 8px;
  margin: 6px 0;
}

.gsv-ref-row .grow2 {
  flex: 2;
  min-width: 0;
}

.gsv-sampling {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 8px;
}

.gsv-sampling .sub-label {
  margin: 0 0 0 8px;
}

.gsv-sampling .sub-label:first-child {
  margin-left: 0;
}

.hint {
  margin-top: 4px;
  color: var(--fg-secondary);
  font-size: 13.2px;
  line-height: 1.5;
}

.fset {
  padding: 4px 0;
}

.frow {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 30px;
}

.frow-label {
  flex: none;
  width: 158px;
  font-size: 14.3px;
  color: var(--fg-primary);
}

.frow-ctl {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
}

.fset > .hint,
.frow-hint {
  margin-top: 3px;
  padding-left: 168px;
}

.switch-row {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14.3px;
  color: var(--fg-primary);
  cursor: pointer;
  width: fit-content;
}

.row-title {
  font-size: 14.3px;
  font-weight: 600;
  color: var(--fg-primary);
}

.card-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 6px 0;
  flex-wrap: wrap;
}

.field-block {
  margin-top: 10px;
}

/* ───── 控件:输入框/文本域(ComboBox/TextBox 规格:BgInput 圆角6,焦点描边 Accent) ───── */

.ctl {
  background: var(--bg-input);
  color: var(--fg-primary);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 5px 10px;
  font-size: 14.3px;
  font-family: var(--font);
  box-sizing: border-box;
}

input.ctl {
  height: 30px;
  padding: 0 10px;
}

textarea.ctl {
  padding: 8px 10px;
  line-height: 1.5;
}

.grow {
  flex: 1;
  min-width: 0;
}

.wide {
  width: 100%;
  resize: vertical;
}

.mono {
  font-family: Consolas, monospace;
  font-size: 13.2px;
}

.pct-row {
  display: flex;
  align-items: center;
  gap: 6px;
}

.pct {
  color: var(--fg-primary);
  font-size: 14.3px;
}

.slider-row {
  display: flex;
  align-items: center;
  gap: 12px;
}

.slider-val {
  min-width: 48px;
  text-align: right;
  color: var(--fg-primary);
  font-size: 13.2px;
}

.grid-row {
  display: grid;
  grid-template-columns: 150px 1fr 52px;
  align-items: center;
  gap: 12px;
  margin: 8px 0;
}

.grid-row .gl {
  font-size: 14.3px;
  color: var(--fg-primary);
}

.color-row {
  display: flex;
  gap: 10px;
  align-items: center;
}

.color-row input[type='color'] {
  width: 42px;
  height: 30px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--bg-input);
  padding: 2px;
  cursor: pointer;
}

.path-row,
.paths,
.json {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.path-row {
  flex-direction: row;
}

.path-item {
  display: flex;
  gap: 8px;
  align-items: center;
  background: var(--bg-input);
  border-radius: 6px;
  padding: 6px 10px;
  font-size: 13.2px;
  word-break: break-all;
}

.check {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13.2px;
  color: var(--fg-primary);
  cursor: pointer;
}

/* ───── 模型服务:供应商列表(ListBox 规格)+ 模型行(BgInput 圆角条) ───── */

.provider-list {
  margin-top: 10px;
  height: 160px;
  overflow-y: auto;
}

.list-item {
  display: block;
  width: calc(100% - 12px);
  margin: 2px 6px;
  padding: 8px 10px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--fg-primary);
  text-align: left;
  cursor: pointer;
  font-size: 14.3px;
  font-family: var(--font);
}

.list-item:hover {
  background: var(--selection);
}

.list-item.sel {
  background: var(--accent);
  color: #fff;
}

.model-row {
  display: flex;
  gap: 8px;
  align-items: center;
  border-radius: 6px;
  padding: 5px 8px;
  margin: 2px 4px;
}

.model-row:hover {
  background: var(--selection);
}

.name-inp {
  flex: 1;
  min-width: 0;
  background: transparent;
  border: none;
  color: var(--fg-primary);
  font-size: 13.2px;
  font-family: var(--font);
  outline: none;
}

.name-inp::placeholder {
  color: var(--fg-muted);
}

/* ───── 工具组卡 ───── */

.tool-group-row {
  display: flex;
  gap: 14px;
  align-items: flex-start;
}

.tool-group-row > input {
  margin-top: 2px;
}

.tool-group-name {
  font-size: 14.3px;
  font-weight: 600;
  color: var(--fg-primary);
}

.tool-group-desc {
  font-size: 12.1px;
  color: var(--fg-secondary);
  margin-top: 2px;
}

.tool-group-members {
  font-size: 11px;
  color: var(--fg-muted);
  margin-top: 3px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 「其他」折叠卡片:收起按钮与展开后的逐工具开关列表(两行格式与功能组卡一致:首行开关+名称,次行缩进说明) */
.tool-collapse-btn {
  margin: -4px -6px 0 0;
  padding: 0 6px;
  line-height: 20px;
}

.tool-hidden-list {
  margin-top: 10px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.tool-hidden-row {
  display: flex;
  flex-direction: column;
}

.tool-hidden-check {
  display: flex;
  align-items: center;
  gap: 14px;
  cursor: pointer;
  /* 名称行与功能组卡标题同规格 */
  font-size: 14.3px;
  font-weight: 600;
  color: var(--fg-primary);
}

.tool-hidden-desc {
  /* 说明行与功能组卡描述同规格,同一缩进:开关宽 42px + 间距 14px */
  margin-left: 56px;
  font-size: 12.1px;
  color: var(--fg-secondary);
  font-family: var(--font-mono, monospace);
  overflow-wrap: anywhere;
}

/* 当前工具清单:胶囊流式排列 */
.tool-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 4px;
}

.tool-chip {
  padding: 3px 10px;
  border-radius: 999px;
  background: var(--bg-code);
  border: 1px solid var(--border);
  font-size: 13.2px;
  color: var(--fg-primary);
}

.tool-chip.none {
  color: var(--fg-muted);
}

/* MCP 聚合胶囊:区别于内置工具,悬浮 title 列出该服务器全部工具 */
.tool-chip.mcp {
  border-color: color-mix(in srgb, var(--accent) 45%, var(--border));
  color: var(--accent);
}

/* ───── 桌宠:情绪/动作双列(ListBox 规格:无边框、悬停 Selection、选中 Accent) ───── */

.emotion-grid {
  display: grid;
  grid-template-columns: 190px 1fr;
  gap: 12px;
  margin-top: 8px;
}

.emotion-col,
.motion-col {
  display: flex;
  flex-direction: column;
  max-height: 260px;
  overflow-y: auto;
}

/* 情绪列表:边框 + 背景区分范围 */
.emotion-col {
  height: 260px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--bg-input);
  padding: 4px;
}

/* 动作列表:与情绪列同风格容器 */
.motion-col {
  height: 260px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--bg-input);
  padding: 6px;
}

/* 普通 checkbox(动作勾选):覆盖全局胶囊开关 */
input[type='checkbox'].plain-check {
  width: 15px !important;
  height: 15px !important;
  border-radius: 4px;
  border: 1px solid var(--fg-muted);
  background: var(--bg-base);
}

input[type='checkbox'].plain-check::after {
  top: 3px !important;
  left: 4px !important;
  width: 5px !important;
  height: 7px !important;
  border: solid #fff !important;
  border-width: 0 2px 2px 0 !important;
  border-radius: 0 !important;
  background: transparent !important;
  transform: rotate(45deg) !important;
  opacity: 0 !important;
  transition: none !important;
}

input[type='checkbox'].plain-check:checked {
  background: var(--fg-accent);
}

input[type='checkbox'].plain-check:checked::after {
  left: 4px !important;
  opacity: 1 !important;
  background: transparent !important;
}

/* 只读的内置模板正文:灰色弱化 */
.tpl-readonly {
  color: var(--fg-secondary);
}

/* 服务/模型列表容器:统一边框列表 */
.provider-list {
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--bg-input);
  padding: 4px;
}

.model-list {
  margin-top: 10px;
  max-height: 260px;
  overflow-y: auto;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--bg-input);
  padding: 4px;
}

/* 分类切换过渡 */
.pane-inner {
  animation: pane-in 0.16s ease-out;
}

@keyframes pane-in {
  from {
    opacity: 0.3;
    transform: translateY(4px);
  }
  to {
    opacity: 1;
    transform: none;
  }
}

/* 背景图预览 */
.bg-row {
  display: flex;
  align-items: center;
  gap: 16px;
}

.bg-preview {
  width: 160px;
  height: 90px;
  border-radius: 8px;
  border: 1px solid var(--border);
  object-fit: cover;
  background: var(--bg-input);
  flex: none;
}

.bg-preview.bg-none {
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--fg-muted);
}

.bg-preview.bg-none .ms {
  font-size: 30.8px;
}

.bg-side {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

.emotion-add {
  margin-top: auto;
  display: flex;
  align-items: center;
  gap: 6px;
  background: transparent;
  border: 1px dashed var(--border);
  border-radius: 6px;
  color: var(--fg-muted);
  font-size: 13.2px;
  font-family: var(--font);
  padding: 6px 10px;
  cursor: pointer;
}

.emotion-add:hover {
  color: var(--fg-accent);
  border-color: var(--fg-accent);
}

.emo-del {
  flex: none;
  color: var(--fg-muted);
  cursor: pointer;
  display: inline-flex;
  padding: 2px;
}

.emo-del:hover {
  color: var(--danger);
}

.emo-del .ms {
  font-size: 14.3px;
}

.emotion-row {
  display: flex;
  align-items: center;
  border-radius: 6px;
  margin: 2px 6px;
  cursor: pointer;
}

.emotion-row:hover {
  background: var(--selection);
}

.emotion-row.sel {
  background: var(--accent);
}

.emotion-inp {
  width: 100%;
  background: transparent;
  border: none;
  color: var(--fg-primary);
  font-size: 14.3px;
  font-family: var(--font);
  padding: 8px 10px;
  outline: none;
  box-sizing: border-box;
}

.emotion-inp:disabled {
  color: var(--fg-muted);
  cursor: default;
  /* 关键:禁用的输入框会吞掉点击事件,导致 idle 行永远选不中;放行给父级行 */
  pointer-events: none;
}

.emotion-row.sel .emotion-inp {
  color: #fff;
}

.motion-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 8px;
  border-radius: 6px;
  margin: 2px 0;
}

.motion-row:hover {
  background: var(--selection);
}

.motion-row .check {
  flex: 1;
  min-width: 0;
}

.motion-row .grow {
  font-size: 13.2px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ghost.mini {
  padding: 1px 8px;
  font-size: 12.1px;
}

/* ───── Live2D 形态/捏表情编辑器(紧凑行:小方勾选框 + 名称 + 滑杆 + 数值) ───── */

.l2-rows,
.l2-editor {
  display: flex;
  flex-direction: column;
  max-height: 320px;
  overflow-y: auto;
}

.l2-editor {
  max-height: 460px;
  margin-top: 6px;
}

.l2-group-name {
  font-size: 13.2px;
  font-weight: 600;
  color: var(--fg-secondary);
  margin: 8px 0 4px;
}

.l2-row {
  display: grid;
  grid-template-columns: 20px minmax(120px, 170px) 1fr 44px;
  align-items: center;
  gap: 8px;
  margin: 2px 0;
}

/* 形态卡行没有勾选框:三列布局 */
.l2-row.nock {
  grid-template-columns: minmax(120px, 200px) 1fr 44px;
}

.l2-row .l2-name {
  font-size: 12.1px;
  color: var(--fg-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.l2-row.dim .l2-name,
.l2-row.dim .l2-val {
  opacity: 0.45;
}

.l2-row .l2-val {
  font-size: 12.1px;
  text-align: right;
  color: var(--fg-primary);
}

/* 编辑行用紧凑方形勾选框(全站胶囊开关在此过重) */
.l2-row input[type='checkbox'] {
  width: 16px !important;
  height: 16px !important;
  border-radius: 4px;
  background: var(--bg-input);
  border: 1px solid var(--border);
  justify-self: center;
}

.l2-row input[type='checkbox']::after {
  display: none;
}

.l2-row input[type='checkbox']:checked {
  background: var(--accent);
  border-color: var(--accent);
}

/* ───── B站白名单(无边框行,悬停高亮) ───── */

.bili-whitelist {
  display: flex;
  flex-direction: column;
  max-height: 320px;
  overflow-y: auto;
}

.bili-row {
  padding: 6px 8px;
  border-radius: 6px;
  margin: 2px 0;
}

.bili-row:hover {
  background: var(--selection);
}

.bili-row.grey {
  color: var(--fg-muted);
}

/* ───── 情景 / 高级 JSON ───── */

.scene {
  background: var(--bg-input);
  border-radius: 8px;
  padding: 10px;
  margin-bottom: 10px;
}

.scene-head {
  display: flex;
  gap: 8px;
  margin-bottom: 6px;
}

.loading {
  color: var(--fg-muted);
  text-align: center;
  padding: 40px;
}

/* ───── Material Symbols 图标按钮:图标与文案横向对齐 ───── */

.ghost {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}

.ghost .ms {
  font-size: 14.3px;
}

.saved .ms {
  font-size: 14.3px;
  vertical-align: -2px;
}

.ghost.on {
  border-color: var(--fg-accent);
  color: var(--fg-accent);
}

.ghost.danger {
  color: var(--danger);
}

/* ───── Agent 头像卡 ───── */

.avatar-row {
  display: flex;
  align-items: center;
  gap: 16px;
}

.avatar-big {
  width: 72px;
  height: 72px;
  border-radius: 50%;
  object-fit: cover;
  background: var(--bg-input);
  border: 1px solid var(--border);
  flex: none;
}

.avatar-big.avatar-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--fg-muted);
}

.avatar-big.avatar-empty .ms {
  font-size: 37.4px;
}

.avatar-side {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

.avatar-btns {
  display: flex;
  align-items: center;
  gap: 8px;
}

.hidden-input {
  display: none;
}

/* ───── 模型高级设置(思考等级 / 媒体类型) ───── */

.model-adv {
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 10px 12px;
  margin: 6px 0 10px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.adv-row {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.adv-label {
  flex: none;
  width: 64px;
  font-size: 13.2px;
  color: var(--fg-secondary);
}

.adv-row .check {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 13.2px;
  cursor: pointer;
}

.adv-row .check.dim {
  color: var(--fg-muted);
  cursor: default;
}

/* 方形 toggle 选中按钮(模型高级设置) */
.chip-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.chip-toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  background: var(--bg-input);
  border: 1px solid var(--border);
  color: var(--fg-secondary);
  border-radius: 6px;
  padding: 6px 14px;
  font-size: 13.2px;
  font-family: var(--font);
  cursor: pointer;
}

.chip-toggle:hover:not(:disabled) {
  border-color: var(--fg-accent);
  color: var(--fg-primary);
}

.chip-toggle.on {
  border-color: var(--fg-accent);
  color: var(--fg-accent);
  background: rgba(79, 127, 224, 0.1);
}

.chip-toggle:disabled {
  opacity: 0.55;
  cursor: default;
}

.chip-toggle .ms {
  font-size: 14.3px;
}

/* ───── 内置插件列表行 ───── */

.plugin-row {
  display: flex;
  align-items: center;
  gap: 14px;
}

.plugin-ico {
  flex: none;
  font-size: 28.6px;
  color: var(--fg-accent);
}

/* 管理列表行(MCP 服务器 / 技能主页):启停 + 元信息 + 操作 */
.mgr-row {
  display: flex;
  align-items: center;
  gap: 12px;
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 10px 12px;
  margin: 8px 0;
}

/* 编辑模态右上角 表单/JSON 分段切换 */
.seg {
  display: inline-flex;
  border: 1px solid var(--border);
  border-radius: 8px;
  overflow: hidden;
}

.seg button {
  background: transparent;
  border: none;
  color: var(--fg-secondary);
  padding: 5px 14px;
  cursor: pointer;
  font-size: 13.2px;
  font-family: var(--font);
}

/* MCP 编辑模态:表单行之间留出纵向呼吸感(该表单行下无 hint 文本) */
.mcp-editor .frow + .frow {
  margin-top: 12px;
}

.seg button.on {
  background: var(--bg-panel-hover);
  color: var(--fg-primary);
}

.type-tag {
  margin-left: 8px;
  font-size: 12.1px;
  color: var(--fg-secondary);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 1px 6px;
  vertical-align: 2px;
}

.type-tag.builtin {
  color: var(--fg-accent);
}

/* MCP 服务器行内连接状态徽标 */
.mcp-status {
  margin: 3px 0 0;
  font-size: 12.1px;
}

.mcp-status.ok {
  color: var(--success);
}

.mcp-status.err {
  color: var(--danger);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mcp-status.off {
  color: var(--fg-muted);
}

.plugin-meta {
  flex: 1;
  min-width: 0;
}

.plugin-title {
  font-size: 15.4px;
  font-weight: 600;
}

.plugin-switch {
  flex: none;
  margin: 0;
}

/* 子代理模型选择触发器:仿 AppSelect 观感(BgInput 圆角6,悬停描边 Accent) */
.sub-mm {
  display: inline-block;
}

.sub-mm-btn {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  width: 300px;
  box-sizing: border-box;
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: 6px;
  color: var(--fg-primary);
  font-family: var(--font);
  font-size: 14.3px;
  padding: 0 26px 0 9px;
  cursor: pointer;
  transition: border-color 0.12s;
}

.sub-mm-btn:hover {
  border-color: var(--fg-accent);
}

.sub-mm-btn .sub-mm-label {
  flex: 1;
  min-width: 0;
  text-align: left;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.sub-mm-btn .sub-mm-prov {
  flex: none;
  color: var(--fg-muted);
  font-size: 12.1px;
}

.sub-mm-btn .ms {
  position: absolute;
  right: 7px;
  font-size: 15.4px;
  color: var(--fg-secondary);
  pointer-events: none;
}
</style>

<style>
/* 非scoped:级联弹窗 Teleport 到 body,必须用全局样式(与 AppSelect 同法)。
   观感对齐聊天工具栏的模型级联菜单(.m-*)。 */
.sub-mm-pop {
  position: fixed;
  z-index: 2800;
  box-sizing: border-box;
  width: 224px;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  border-radius: 10px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
  padding: 5px;
  overflow: visible;
}

.sub-mm-current {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 7px 10px;
  font-size: 13.2px;
  border-radius: 6px;
  background: var(--selection);
  cursor: pointer;
}

.sub-mm-current.off {
  background: transparent;
}

.sub-mm-check {
  color: var(--fg-accent);
  width: 14px;
  flex: none;
  display: inline-flex;
}

.sub-mm-check .ms {
  font-size: 14.3px;
}

.sub-mm-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.sub-mm-sep {
  height: 1px;
  background: var(--border);
  margin: 5px 4px;
}

.sub-mm-prov-row {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 7px 10px;
  font-size: 13.2px;
  border-radius: 6px;
  cursor: pointer;
}

.sub-mm-prov-row:hover {
  background: var(--selection);
}

.sub-mm-arrow {
  color: var(--fg-muted);
  display: inline-flex;
}

.sub-mm-arrow .ms {
  font-size: 15.4px;
}

.sub-mm-sub {
  position: absolute;
  left: calc(100% - 4px);
  top: -6px;
  min-width: 240px;
  max-height: 300px;
  overflow-y: auto;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  border-radius: 10px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
  padding: 5px;
  z-index: 1;
}

.sub-mm-sub.fly-left {
  left: auto;
  right: calc(100% - 4px);
}

.sub-mm-model {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 7px 10px;
  font-size: 13.2px;
  border-radius: 6px;
  cursor: pointer;
}

.sub-mm-model:hover {
  background: var(--selection);
}

.sub-mm-model.cur {
  color: var(--fg-accent);
}

.sub-mm-model.cur .sub-mm-check {
  color: #fff;
}

.sub-mm-empty {
  padding: 8px 10px;
  color: var(--fg-muted);
  font-size: 12.1px;
}
</style>
