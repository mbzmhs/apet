// REST 客户端(会话/发送/停止/归属定位/鉴权 + M2a 会话管理/模型/命令)

import type { AgentInfo, ArchivedSession, CommandInfo, CommandResult, FolderInfo, HealthInfo, MessageSummary, ProviderInfo, QueuedEdit, SessionListItem, SessionState } from '../types'

// REST 统一超时:后端在本机,正常响应远快于此;15s 无响应视为挂死,报可读中文而非
// 浏览器默认的英文 TimeoutError(SSE/流式端点不经这里的包装,长连接不受影响)
const REST_TIMEOUT_MS = 15000

/** fetch 统一包装:带超时中断,并把超时错误转成可读文案(其余错误原样上抛) */
async function fetchWithTimeout(url: string, init?: RequestInit): Promise<Response> {
  try {
    return await fetch(url, { ...init, signal: AbortSignal.timeout(REST_TIMEOUT_MS) })
  } catch (e) {
    if (e instanceof DOMException && e.name === 'TimeoutError') throw new Error(`请求超时(${REST_TIMEOUT_MS / 1000}s)`)
    throw e
  }
}

async function getJson<T>(url: string): Promise<T> {
  const res = await fetchWithTimeout(url)
  if (!res.ok) throw new Error(`${url} → HTTP ${res.status}`)
  return (await res.json()) as T
}

async function postJson<T>(url: string, body?: unknown): Promise<T> {
  const res = await fetchWithTimeout(url, {
    method: 'POST',
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  if (!res.ok) {
    let detail = ''
    try {
      detail = ((await res.json()) as { error?: string }).error ?? ''
    } catch {
      // 无错误详情体
    }
    throw new Error(detail || `${url} → HTTP ${res.status}`)
  }
  return (await res.json()) as T
}

export function getHealth(): Promise<HealthInfo> {
  return getJson<HealthInfo>('/api/health')
}

export function getAgents(): Promise<AgentInfo[]> {
  return getJson<AgentInfo[]>('/api/agents')
}

export function getSessions(agentId: string): Promise<SessionListItem[]> {
  return getJson<SessionListItem[]>(`/api/agents/${agentId}/sessions`)
}

/** 上报"最近打开的会话":桌面聊天窗下次开窗/托盘点名优先回到它(而非按消息时间猜的最新会话) */
export function reportLastSession(agentId: string, sessionId: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/last-session`, { sessionId }).then(() => undefined)
}

/** 读后端记忆的"最近打开的会话"(跨端通用;无记忆返回 null) */
export async function getLastSession(agentId: string): Promise<string | null> {
  const r = await getJson<{ sessionId: string | null }>(`/api/agents/${agentId}/last-session`)
  return r.sessionId
}

/** fromSessionId:新会话继承该会话的供应商/模型/思考等级(不传 = 后端回落"当前选中会话");
 * folderId:新对话直接建在该文件夹里(不传 = 根组) */
export function createSession(agentId: string, fromSessionId?: string, folderId?: string): Promise<SessionState> {
  const body: Record<string, string> = {}
  if (fromSessionId) body.fromSessionId = fromSessionId
  if (folderId) body.folderId = folderId
  return postJson<SessionState>(`/api/agents/${agentId}/sessions`, body)
}

/** 会话快照;历史分页(均可选,全不传 = 全量,兼容旧调用):
 * limit = 取末尾 N 行(首屏窗口);after = seq > after 的全部行(刷新已加载窗口);
 * before = seq < before 的末尾 N 行(向上翻页取更早历史) */
export function getSession(
  agentId: string,
  sessionId: string,
  opts?: { limit?: number; after?: number; before?: number },
): Promise<SessionState> {
  const q = new URLSearchParams()
  if (opts?.limit) q.set('limit', String(opts.limit))
  if (opts?.after != null) q.set('after', String(opts.after))
  if (opts?.before != null) q.set('before', String(opts.before))
  const qs = q.toString()
  return getJson<SessionState>(`/api/agents/${agentId}/sessions/${sessionId}${qs ? `?${qs}` : ''}`)
}

/** 发送消息:202 = 已受理,响应体 queued 标明实际去向(false = 空闲直接开跑,true = 运行中进中途消息队列);
 * 前端以该字段为准决定是否推本地气泡(不再信任本地 isStreaming 标志,防止与服务端脱节造成重复气泡);
 * 400 = 附件超限/格式错误 */
export async function sendMessage(
  agentId: string,
  sessionId: string,
  text: string,
  attachments?: { images: string[]; textFiles: { name: string; content: string }[] },
  defer = false,
): Promise<{ queued: boolean }> {
  const res = await fetch(`/api/agents/${agentId}/sessions/${sessionId}/message`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      text,
      images: attachments?.images ?? [],
      textFiles: attachments?.textFiles ?? [],
      defer, // Ctrl+Enter 入队标记"本轮后发送"(闲时发送忽略此字段)
    }),
  })
  if (res.status === 409) throw new Error('会话正在运行中')
  if (!res.ok) {
    let detail = ''
    try {
      detail = ((await res.json()) as { error?: string }).error ?? ''
    } catch {
      // 无错误详情体
    }
    throw new Error(detail || `发送失败(HTTP ${res.status})`)
  }
  const body = (await res.json().catch(() => null)) as { queued?: boolean } | null
  return { queued: !!body?.queued }
}

export async function stopSession(agentId: string, sessionId: string): Promise<void> {
  const res = await fetch(`/api/agents/${agentId}/sessions/${sessionId}/stop`, { method: 'POST' })
  if (!res.ok) throw new Error(`停止失败(HTTP ${res.status})`)
}

/** 取出并移除一条中途队列插话:取消 = 丢弃返回值;重新编辑 = 用返回的 text/images/textFiles 回填输入框 */
export async function takeQueueMessage(agentId: string, sessionId: string, queueId: string): Promise<QueuedEdit> {
  const res = await fetch(`/api/agents/${agentId}/sessions/${sessionId}/queue/${queueId}`, { method: 'DELETE' })
  if (!res.ok) throw new Error(`队列操作失败(HTTP ${res.status})`)
  return (await res.json()) as QueuedEdit
}

/** 切换队列条目"本轮后发送"标记;404 = 已被消费或移除 */
export async function setQueueDefer(agentId: string, sessionId: string, queueId: string, defer: boolean): Promise<void> {
  const res = await fetch(`/api/agents/${agentId}/sessions/${sessionId}/queue/${queueId}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ defer }),
  })
  if (!res.ok) throw new Error(`队列操作失败(HTTP ${res.status})`)
}

/** 调整队列条目顺序(index = 移除自身后的插入位置);404 = 已被消费或移除 */
export async function moveQueueMessage(
  agentId: string,
  sessionId: string,
  queueId: string,
  index: number,
): Promise<void> {
  const res = await fetch(`/api/agents/${agentId}/sessions/${sessionId}/queue/${queueId}/move`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ index }),
  })
  if (!res.ok) throw new Error(`队列操作失败(HTTP ${res.status})`)
}

/** 解析会话归属的 Agent(URL 只带 sessionId 的深链用);404 = 会话不存在 */
export async function getSessionOwner(sessionId: string): Promise<string | null> {
  const res = await fetch(`/api/session-owner/${sessionId}`)
  if (res.status === 404) return null
  if (!res.ok) throw new Error(`定位会话失败(HTTP ${res.status})`)
  return ((await res.json()) as { agentId: string }).agentId
}

export async function resolveAuth(authId: string, allow: boolean, trust: boolean): Promise<void> {
  await postJson(`/api/auth/${authId}`, { allow, trust })
}

// ───────── ask 询问工具(面板盖住输入框;答案/忽略经 REST 回传) ─────────

export interface AskOptionInfo {
  label: string
  description?: string
  recommended?: boolean
}

export interface AskQuestionInfo {
  title: string
  question: string
  multiSelect: boolean
  options: AskOptionInfo[]
}

/** 一道题的应答:选中的选项标题 + "其他"自由输入文本 */
export interface AskAnswer {
  title: string
  question: string
  selected: string[]
  other?: string
}

export interface AskPanelInfo {
  askId: string
  questions: AskQuestionInfo[]
}

/** 应答一次询问;skipped = 用户忽略(模型收到"自行合理假设"的提示) */
export async function resolveAsk(askId: string, answers: AskAnswer[] | null): Promise<void> {
  await postJson(`/api/ask/${askId}`, answers ? { answers, skipped: false } : { answers: null, skipped: true })
}

/** 会话当前待决询问(页面刷新/重开后重建面板) */
export function getPendingAsks(agentId: string, sessionId: string): Promise<AskPanelInfo[]> {
  return getJson(`/api/agents/${agentId}/sessions/${sessionId}/asks`)
}

// ───────── M2a:会话管理 / 模型切换 / 斜杠命令 / 图片 ─────────

export function renameSession(agentId: string, sessionId: string, title: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/sessions/${sessionId}/rename`, { title }).then(() => undefined)
}

export function archiveSession(agentId: string, sessionId: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/sessions/${sessionId}/archive`).then(() => undefined)
}

export function unarchiveSession(agentId: string, sessionId: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/sessions/${sessionId}/unarchive`).then(() => undefined)
}

export function forkSession(agentId: string, sessionId: string, title?: string): Promise<{ sessionId: string }> {
  return postJson(`/api/agents/${agentId}/sessions/${sessionId}/fork`, { title })
}

export function deleteSession(agentId: string, sessionId: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/sessions/${sessionId}/delete`).then(() => undefined)
}

// 返回 "empty" = 没有可压缩的旧消息(前端 toast 提示);"cancelled" = 中断(分割线「压缩已中断」);
// "failed" = 失败(分割线「压缩失败」);null = 成功(「会话已压缩」分割线)
export function compactSession(agentId: string, sessionId: string, extra?: string): Promise<string | null> {
  return postJson<{ ok: boolean; reason?: string | null }>(`/api/agents/${agentId}/sessions/${sessionId}/compact`, { extra })
    .then(r => (r.reason ?? null))
}

export async function undoSession(agentId: string, sessionId: string): Promise<string> {
  const r = await postJson<{ restored: string }>(`/api/agents/${agentId}/sessions/${sessionId}/undo`)
  return r.restored
}

export function getSummary(agentId: string, sessionId: string): Promise<{ text: string }> {
  return getJson(`/api/agents/${agentId}/sessions/${sessionId}/summary`)
}

export interface SystemPromptTool {
  name: string
  description: string
}

export function getSystemPrompt(
  agentId: string,
  sessionId: string,
): Promise<{ text: string; tools: SystemPromptTool[] }> {
  return getJson(`/api/agents/${agentId}/sessions/${sessionId}/system-prompt`)
}

export function getArchived(agentId: string): Promise<ArchivedSession[]> {
  return getJson(`/api/agents/${agentId}/archived`)
}

export function clearArchived(agentId: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/archived/clear`).then(() => undefined)
}

// ───────── 会话列表:文件夹(嵌套)+ 组内排序 ─────────

export function getFolders(agentId: string): Promise<FolderInfo[]> {
  return getJson<FolderInfo[]>(`/api/agents/${agentId}/folders`)
}

/** parentId 传 null/不传 = 建在顶级 */
export function createFolder(agentId: string, name: string, parentId?: string | null): Promise<{ ok: boolean }> {
  return postJson(`/api/agents/${agentId}/folders`, { name, parentId: parentId ?? null }).then(() => ({ ok: true }))
}

export function renameFolder(agentId: string, folderId: string, name: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/folders/${folderId}/rename`, { name }).then(() => undefined)
}

/** 移动文件夹:parentId 传 null = 移到顶级;目标是自身/后代后端拒(400) */
export function moveFolder(agentId: string, folderId: string, parentId: string | null): Promise<void> {
  return postJson(`/api/agents/${agentId}/folders/${folderId}/move`, { parentId }).then(() => undefined)
}

/** 删除文件夹:子文件夹与组内会话降级到其父级(不丢会话) */
export function deleteFolder(agentId: string, folderId: string): Promise<void> {
  const res = fetch(`/api/agents/${agentId}/folders/${folderId}`, { method: 'DELETE' })
  return res.then((r) => {
    if (!r.ok) throw new Error(`删除文件夹 → HTTP ${r.status}`)
  })
}

/** 把会话移入某组末尾;folderId 传 null = 根组 */
export function moveSessionToFolder(agentId: string, sessionId: string, folderId: string | null): Promise<void> {
  return postJson(`/api/agents/${agentId}/sessions/${sessionId}/move`, { folderId }).then(() => undefined)
}

/** 整组重排(拖拽落点后整序提交;orderedIds 可混排会话与文件夹 id,后端按同一槽位表编号):folderId 传 null = 根组 */
export function reorderSessions(agentId: string, folderId: string | null, orderedIds: string[]): Promise<void> {
  return postJson(`/api/agents/${agentId}/sessions/reorder`, { folderId, orderedIds }).then(() => undefined)
}

/** 同级文件夹整序提交:parentId 传 null = 顶级 */
export function reorderFolders(agentId: string, parentId: string | null, orderedIds: string[]): Promise<void> {
  return postJson(`/api/agents/${agentId}/folders/reorder`, { parentId, orderedIds }).then(() => undefined)
}

/** 取该 Agent 全部折叠文件夹 id(展开态跨重启恢复用) */
export function getCollapsedFolders(agentId: string): Promise<string[]> {
  return getJson<string[]>(`/api/agents/${agentId}/folders/collapse-state`)
}

/** 设置文件夹预设色(color = 色板 id,'' = 默认不着色) */
export function setFolderColor(agentId: string, folderId: string, color: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/folders/${folderId}/color`, { color }).then(() => undefined)
}

/** 整体提交折叠集合(前端每次展开/收起时写穿;空数组 = 全部展开) */
export function setCollapsedFolders(agentId: string, ids: string[]): Promise<void> {
  return postJson(`/api/agents/${agentId}/folders/collapse-state`, { ids }).then(() => undefined)
}

export function getProviders(agentId: string): Promise<ProviderInfo[]> {
  return getJson(`/api/agents/${agentId}/providers`)
}

export function setModel(
  agentId: string,
  sessionId: string,
  patch: { provider?: string; model?: string; thinkingEffort?: string },
): Promise<SessionState> {
  return postJson(`/api/agents/${agentId}/sessions/${sessionId}/model`, patch)
}

export function getCommands(agentId: string): Promise<CommandInfo[]> {
  return getJson(`/api/agents/${agentId}/commands`)
}

export function executeCommand(agentId: string, sessionId: string, text: string): Promise<CommandResult> {
  return postJson(`/api/agents/${agentId}/sessions/${sessionId}/command`, { text })
}

export function imageUrl(agentId: string, sessionId: string, fileName: string): string {
  return `/api/agents/${agentId}/sessions/${sessionId}/images/${encodeURIComponent(fileName)}`
}

// ───────── Agent 头像(agents/{agentId}/avatar;rev 做缓存失效) ─────────

export interface CompressPresetInfo {
  customTag: string
  defaultTemplate: string
  presets: { name: string; text: string }[]
}

export function getCompressPresets(agentId: string): Promise<CompressPresetInfo> {
  return getJson(`/api/agents/${agentId}/compress-presets`)
}

export function avatarUrl(agentId: string, avatarRev: number): string {
  return `/api/agents/${agentId}/avatar?v=${avatarRev}`
}

/** 上传头像(data URL,PNG/JPG/WebP/GIF ≤5MB);返回新的 rev */
export function uploadAvatar(agentId: string, dataUrl: string): Promise<{ rev: number }> {
  return postJson(`/api/agents/${agentId}/avatar`, { dataUrl })
}

export function removeAvatar(agentId: string): Promise<{ ok: boolean }> {
  return deleteJson(`/api/agents/${agentId}/avatar`)
}

// ───────── 界面背景图(聊天区/窗体;上传后服务端写回 imagePath) ─────────

export function uploadBackground(agentId: string, which: 'chat' | 'window', dataUrl: string): Promise<{ path: string }> {
  return postJson(`/api/agents/${agentId}/backgrounds/${which}`, { dataUrl })
}

export function deleteBackground(agentId: string, which: 'chat' | 'window'): Promise<{ ok: boolean }> {
  return deleteJson(`/api/agents/${agentId}/backgrounds/${which}`)
}

/** 桌面客户端内请求打开某 Agent 的原生窗体(直达其聊天页);网页环境不调用 */
export function openAgentWindow(agentId: string): Promise<{ ok: boolean }> {
  return postJson(`/api/agents/${agentId}/open-window`, {})
}

/** 桌面客户端内请求在该 Agent 自己的原生窗体里打开设置页;网页环境不调用 */
export function openAgentSettings(agentId: string): Promise<{ ok: boolean }> {
  return postJson(`/api/agents/${agentId}/open-settings`, {})
}

// ───────── 侧边栏(任务清单 / 后台任务 / 直播事件) ─────────

/** BiliLive 插件连接状态(侧边栏圆点+状态行) */
export interface BiliConn {
  state: string // idle/resolving/connecting/authenticating/connected/retrying/waiting_live
  host: string
  hostIndex: number
  hostCount: number
  round: number
  roomCode: string
  internalRoomId: number
  roomLive: boolean
  error: string
  changedAt: string
  nextRetryAt: string | null
  received: number
}

export interface SidebarInfo {
  todos: { id: number; content: string; status: string }[]
  tasks: { id: number; title: string; command: string; status: string; isError: boolean; startedAt: string; endedAt: string | null }[]
  liveBound: boolean
  live: { kind: string; user: string; text: string; priceYuan: number; time: string }[]
  /** BiliLive 连接状态(圆点+状态行;始终返回,未启用为 state=idle) */
  bili: BiliConn | null
  /** 插件前端数据面:每个启用插件 GetPluginData 的返回值(任意 JSON 形状),侧边栏"插件"页签展示 */
  pluginData: { name: string; data: unknown }[]
  /** 声明 sidebar UI 插槽的启用插件:侧边栏单开自定义页签(DLL 内嵌 ui.* 页面) */
  uiPlugins: { name: string; slots: string }[]
  /** 插件数据面返回对象含 status 字符串字段者:底部状态栏右侧显示(不含完整 data) */
  statusPlugins: { name: string; status: string }[]
}

export function getSidebar(agentId: string, sessionId: string): Promise<SidebarInfo> {
  return getJson(`/api/agents/${agentId}/sessions/${sessionId}/sidebar`)
}

export async function getTaskOutput(agentId: string, sessionId: string, taskId: number): Promise<string> {
  const r = await getJson<{ output: string }>(`/api/agents/${agentId}/sessions/${sessionId}/tasks/${taskId}/output`)
  return r.output
}

/** 助手气泡悬停详情:按 msgIds 聚合原始消息(时间/Token 用量/模型);id 全缺失(旧会话无关联)→ 404 */
export function getMessageSummary(agentId: string, sessionId: string, ids: string[]): Promise<MessageSummary> {
  const q = ids.map((id) => `ids=${encodeURIComponent(id)}`).join('&')
  return getJson<MessageSummary>(`/api/agents/${agentId}/sessions/${sessionId}/messages-summary?${q}`)
}

// ───────── 设置 / 工具 / 文件浏览 ─────────

export function getSettings<T = any>(agentId: string): Promise<T> {
  return getJson<T>(`/api/agents/${agentId}/settings`)
}

export async function putSettings(
  agentId: string,
  settings: unknown,
): Promise<{ ok: boolean; petMessage?: string | null }> {
  const res = await fetch(`/api/agents/${agentId}/settings`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(settings),
  })
  if (!res.ok) {
    let detail = ''
    try {
      detail = ((await res.json()) as { error?: string }).error ?? ''
    } catch {
      // 无错误详情体
    }
    throw new Error(detail || `保存失败(HTTP ${res.status})`)
  }
  return (await res.json()) as { ok: boolean; petMessage?: string | null }
}

export interface ToolInfo {
  name: string
  displayName: string
  enabled: boolean
}

export function getTools(agentId: string): Promise<ToolInfo[]> {
  return getJson(`/api/agents/${agentId}/tools`)
}

export interface ToolGroupInfo {
  name: string
  description: string
  tools: ToolInfo[]
}

export function getToolGroups(agentId: string): Promise<ToolGroupInfo[]> {
  return getJson(`/api/agents/${agentId}/tool-groups`)
}

/** 工具页折叠「其他」卡片:插件页单独管控而被主组清单隐藏的工具(MCP/B站直播等) */
export function getHiddenTools(agentId: string): Promise<ToolInfo[]> {
  return getJson(`/api/agents/${agentId}/tools/hidden`)
}

export async function setToolGroupEnabled(agentId: string, group: string, enabled: boolean): Promise<void> {
  await postJson(`/api/agents/${agentId}/tool-groups/${encodeURIComponent(group)}/enabled`, { enabled })
}

export async function setToolEnabled(agentId: string, name: string, enabled: boolean): Promise<void> {
  await postJson(`/api/agents/${agentId}/tools/${encodeURIComponent(name)}/enabled`, { enabled })
}

/** 在线拉取供应商的模型列表(服务端代理 GET {baseUrl}/models;URL 中的 agentId 为占位,服务端不消费) */
export async function fetchProviderModels(baseUrl: string, apiKey: string): Promise<string[]> {
  return postJson<string[]>(`/api/agents/agent/providers/models-fetch`, { baseUrl, apiKey })
}

/** 设置会话工作目录/权限(首次对话后服务端锁定,409 = 已锁定) */
export async function setWorkdir(agentId: string, sessionId: string, directory: string, access?: string): Promise<void> {
  const res = await fetch(`/api/agents/${agentId}/sessions/${sessionId}/workdir`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ directory, access }),
  })
  if (res.status === 409) throw new Error('会话已产生对话,工作目录已锁定')
  if (!res.ok) throw new Error(`设置失败(HTTP ${res.status})`)
}

export interface FsEntry {
  target?: string | null
  name: string
  isDir: boolean
  size: number | null
  mtime: string | null
}

export interface FsListing {
  path: string
  parent: string | null
  entries: FsEntry[]
}

export function fsList(path = ''): Promise<FsListing> {
  return getJson<FsListing>(`/api/fs/list?path=${encodeURIComponent(path)}`)
}

/** 文件选择器上次浏览位置(服务端记忆) */
export function getFsLast(): Promise<{ path: string }> {
  return getJson('/api/fs/last')
}

/** Windows 系统语音清单(SAPI) */
export function getWindowsVoices(agentId: string): Promise<string[]> {
  return getJson(`/api/agents/${agentId}/tts/windows-voices`)
}

/** 会话列表置顶/取消置顶 */
export function pinSession(agentId: string, sessionId: string, pinned: boolean): Promise<void> {
  return postJson(`/api/agents/${agentId}/sessions/${sessionId}/pin`, { pinned }).then(() => undefined)
}

// ───────── B站白名单候选 / 桌宠动作 ─────────

export interface BiliWhitelistCandidate {
  name: string
  label: string
  selected: boolean
  disabled: boolean
}

export function getBiliWhitelistCandidates(agentId: string): Promise<BiliWhitelistCandidate[]> {
  return getJson(`/api/agents/${agentId}/bili-whitelist-candidates`)
}

export interface PetMotionInfo {
  name: string
  detail: string
}

export function getPetMotions(agentId: string): Promise<PetMotionInfo[]> {
  return getJson(`/api/agents/${agentId}/pet/motions`)
}

export async function playPetMotion(agentId: string, name: string): Promise<void> {
  await postJson(`/api/agents/${agentId}/pet/motions/play`, { name })
}

/** 气泡预览:在宠物旁边弹出真实预览气泡(WPF ShowOffsetPreview 同路径;不落盘) */
export async function previewBubble(agentId: string, style: Record<string, any>): Promise<void> {
  await postJson(`/api/agents/${agentId}/bubble/preview`, style)
}

/** 系统字体清单(后端 System.Drawing,与 WPF 同源) */
export function getSystemFonts(): Promise<string[]> {
  return getJson('/api/system/fonts')
}

/** 从 TTS 服务拉取可用角色(GET {serverUrl}/v1/models) */
export function fetchTtsModels(agentId: string, serverUrl: string): Promise<string[]> {
  return postJson(`/api/agents/${agentId}/tts/models-fetch`, { serverUrl })
}

/** GSV 模型包目录扫描(模型包 voices/ 与 gpt/ 子目录名,供声线编辑下拉) */
export function getGsvModelDirs(agentId: string, modelDir: string): Promise<{ sovits: string[]; gpt: string[] }> {
  return getJson(`/api/agents/${agentId}/gsv-model-dirs?dir=${encodeURIComponent(modelDir)}`)
}

// ───────── Agent 管理(增删/启停/重命名) ─────────

async function deleteJson(url: string): Promise<void> {
  const res = await fetch(url, { method: 'DELETE' })
  if (!res.ok) {
    let detail = ''
    try {
      detail = ((await res.json()) as { error?: string }).error ?? ''
    } catch {
      // 无错误详情体
    }
    throw new Error(detail || `${url} → HTTP ${res.status}`)
  }
}

/** 新增 Agent(立即初始化,托盘可见) */
export function createAgent(name: string): Promise<{ id: string }> {
  return postJson('/api/agents', { name })
}

/** 删除 Agent(其全部会话与设定一并清除;至少保留一个) */
export function deleteAgent(agentId: string): Promise<void> {
  return deleteJson(`/api/agents/${agentId}`)
}

/** 启用/停用 Agent */
export function setAgentEnabled(agentId: string, enabled: boolean): Promise<void> {
  return postJson(`/api/agents/${agentId}/enabled`, { enabled }).then(() => undefined)
}

/** 重命名 Agent */
export function renameAgent(agentId: string, name: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/rename`, { name }).then(() => undefined)
}

// ───────── Live2D 形态/捏表情编辑器(桌面端两个原生窗口的 Web 化) ─────────

export interface Live2dPart {
  id: string
  opacity: number
  setup: number
}

export interface Live2dParam {
  id: string
  name: string
  group: string
  min: number
  max: number
  default: number
  value: number
}

export interface Live2dExpr {
  name: string
  isCustom: boolean
}

/** 顶层部件列表 + 已存兜底形态 */
export function getLive2dParts(agentId: string): Promise<{ saved: Record<string, number>; parts: Live2dPart[] }> {
  return getJson(`/api/agents/${agentId}/pet/live2d/parts`)
}

/** 实时设置部件不透明度(预览) */
export function setLive2dPartOpacity(agentId: string, id: string, value: number): Promise<void> {
  return postJson(`/api/agents/${agentId}/pet/live2d/parts/opacity`, { id, value }).then(() => undefined)
}

/** 存为兜底形态:写入配置并推给渲染器,模型每次加载完成即应用为默认部件基线 */
export function saveLive2dFallback(agentId: string, opacities: Record<string, number>): Promise<void> {
  return postJson(`/api/agents/${agentId}/pet/live2d/fallback/save`, { opacities }).then(() => undefined)
}

/** 清空兜底记录,部件回到 setup 初始可见度 */
export function clearLive2dFallback(agentId: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/pet/live2d/fallback/clear`, {}).then(() => undefined)
}

export interface Live2dEditorState {
  parameters: { id: string; name: string; rows: Live2dParam[] }[]
  parts: Live2dPart[]
  expressions: Live2dExpr[]
}

/** 进入捏表情编辑态:暂停自动化(停动作/关眨眼呼吸姿态/复位参数),返回参数与部件全表 */
export function enterLive2dEditor(agentId: string): Promise<Live2dEditorState> {
  return postJson(`/api/agents/${agentId}/pet/live2d/editor/enter`, {})
}

/** 退出编辑态:恢复进入前的自动化状态 */
export function exitLive2dEditor(agentId: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/pet/live2d/editor/exit`, {}).then(() => undefined)
}

/** 编辑态:滑杆值写模型参数 */
export function setLive2dParam(agentId: string, id: string, value: number): Promise<void> {
  return postJson(`/api/agents/${agentId}/pet/live2d/editor/param`, { id, value }).then(() => undefined)
}

/** 编辑态:滑杆值写模型部件不透明度 */
export function setLive2dEditorPart(agentId: string, id: string, value: number): Promise<void> {
  return postJson(`/api/agents/${agentId}/pet/live2d/editor/part`, { id, value }).then(() => undefined)
}

/** 编辑态当前值(内置表情预览逐帧跟随) */
export function getLive2dValues(agentId: string): Promise<{ parameters: { id: string; value: number }[]; parts: { id: string; value: number }[] }> {
  return getJson(`/api/agents/${agentId}/pet/live2d/editor/values`)
}

/** 全部参数复位默认 */
export function resetLive2dParams(agentId: string): Promise<void> {
  return postJson(`/api/agents/${agentId}/pet/live2d/editor/reset`, {}).then(() => undefined)
}

/** 载入表情:自定义返回存储值;内置只读预览 */
export function loadLive2dExpression(
  agentId: string,
  name: string,
): Promise<{ isCustom: boolean; parameters: Record<string, number>; parts: Record<string, number> }> {
  return postJson(`/api/agents/${agentId}/pet/live2d/editor/expression/load`, { name })
}

/** 新建自定义表情(各项默认值、空差量) */
export function newLive2dExpression(agentId: string): Promise<{ name: string }> {
  return postJson(`/api/agents/${agentId}/pet/live2d/editor/expression/new`, {})
}

/** 保存自定义表情(名字改动即重命名;只存勾选行) */
export function saveLive2dExpression(
  agentId: string,
  name: string,
  oldName: string,
  params: Record<string, number>,
  parts: Record<string, number>,
): Promise<{ ok: boolean; name: string; expressions: Live2dExpr[] }> {
  return postJson(`/api/agents/${agentId}/pet/live2d/editor/expression/save`, { name, oldName, params, parts })
}

/** 删除自定义表情(内置不可删) */
export function deleteLive2dExpression(agentId: string, name: string): Promise<{ ok: boolean; expressions: Live2dExpr[] }> {
  return postJson(`/api/agents/${agentId}/pet/live2d/editor/expression/delete`, { name })
}

// ───────── 外部插件(plugins 目录 DLL) ─────────

export interface ExternalSettingInfo {
  name: string
  label: string
  group: string
  uiHint: string
  valueType: string
  description: string | null
  min: number
  max: number
  options: string[]
  currentValue: unknown
}

export interface ExternalPluginInfo {
  name: string
  enabled: boolean
  version: string | null
  author: string | null
  description: string | null
  loadOrder: number
  providesData: boolean
  /** 是否携带 UI 资产(DLL 内嵌 ui.* 资源):有则卡片"设置"按钮直接打开其自定义界面(无 UI 资产时打开自动生成的通用表单) */
  hasUi: boolean
  /** UI 插槽声明(逗号分隔:settings/sidebar) */
  uiSlots: string
  settings: ExternalSettingInfo[]
}

/** 外部插件清单(名称/启停/[PluginSetting] 描述符与当前值) */
export function getExternalPlugins(agentId: string): Promise<ExternalPluginInfo[]> {
  return getJson(`/api/agents/${agentId}/external-plugins`)
}

/** 批量保存外部插件设置:服务端整体校验后应用;失败返回 ok=false 与原因 */
export function saveExternalPluginSettings(
  agentId: string,
  pluginName: string,
  values: Record<string, unknown>,
): Promise<{ ok: boolean; error?: string; settings: ExternalSettingInfo[] }> {
  return postJson(`/api/agents/${agentId}/external-plugins/${encodeURIComponent(pluginName)}/settings`, values)
}

/** 外部插件启停(驱动插件的 Initialize/Dispose 并持久化) */
export function setExternalPluginEnabled(agentId: string, pluginName: string, enabled: boolean): Promise<{ ok: boolean; error?: string }> {
  return postJson(`/api/agents/${agentId}/external-plugins/${encodeURIComponent(pluginName)}/enabled`, { enabled })
}

/** 外部插件重置:销毁实例并清空持久化设置,以默认值重建(保留启停状态) */
export function resetExternalPlugin(agentId: string, pluginName: string): Promise<{ ok: boolean; error?: string }> {
  return postJson(`/api/agents/${agentId}/external-plugins/${encodeURIComponent(pluginName)}/reset`, {})
}

// ───────── 技能管理(固定存于用户目录 .apet/skills) ─────────

export interface SkillEntry {
  name: string
  description: string
  disabled: boolean
  /** 内置技能(随应用发布):不可编辑/删除,可停用 */
  builtin: boolean
  /** 用户技能最后修改时间(内置为 null) */
  updatedAt: string | null
}

export interface SkillDetail {
  name: string
  description: string
  content: string
}

/** 技能清单(含停用标记) */
export function getSkills(agentId: string): Promise<SkillEntry[]> {
  return getJson(`/api/agents/${agentId}/skills`)
}

/** 技能全文(编辑用) */
export function getSkill(agentId: string, name: string): Promise<SkillDetail> {
  return getJson(`/api/agents/${agentId}/skills/${encodeURIComponent(name)}`)
}

/** 新建/覆盖技能(手动填写与导入文件共用) */
export function saveSkill(
  agentId: string,
  body: { name: string; description: string; content: string },
): Promise<{ ok: boolean; error?: string; name: string }> {
  return postJson(`/api/agents/${agentId}/skills`, body)
}

/** 删除技能(整目录) */
export function deleteSkill(agentId: string, name: string): Promise<{ ok: boolean; error?: string }> {
  return deleteJson(`/api/agents/${agentId}/skills/${encodeURIComponent(name)}`)
}

/** 技能启停(写入停用清单并热推插件) */
export function setSkillEnabled(agentId: string, name: string, enabled: boolean): Promise<{ ok: boolean }> {
  return postJson(`/api/agents/${agentId}/skills/${encodeURIComponent(name)}/enabled`, { enabled })
}

// ───────── MCP 服务器连接状态 ─────────

export interface McpServerStatus {
  name: string
  type: string
  /** 插件总开关 且 该服务器启用 */
  enabled: boolean
  alive: boolean
  tools: number
  error: string | null
}

/** MCP 服务器连接状态(配置清单合并运行时状态) */
export function getMcpStatus(agentId: string): Promise<{ pluginEnabled: boolean; servers: McpServerStatus[] }> {
  return getJson(`/api/agents/${agentId}/mcp-status`)
}

// ───────── 智能体子代理(运行观测) ─────────

export interface SubAgentRun {
  id: number
  name: string
  /** 任务名称(sub_run 的 title 参数;替代"运行 #N"式模糊标题) */
  title: string
  status: string // running / completed / failed / cancelled
  prompt: string
  startedAt: string
  endedAt: string | null
}

export interface SubAgentRunDetail extends SubAgentRun {
  finalContent: string | null
  error: string | null
  framesTruncated: boolean
  frames: { type: string; data: any }[]
}

/** 子代理运行与 session 绑定:仅返回该会话发起的运行(与 bg_run 任务一致) */
export function getSubAgentRuns(agentId: string, sessionId: string): Promise<SubAgentRun[]> {
  return getJson(`/api/agents/${agentId}/sessions/${sessionId}/sub-agents/runs`)
}

export function getSubAgentRun(agentId: string, runId: number): Promise<SubAgentRunDetail> {
  return getJson(`/api/agents/${agentId}/sub-agents/runs/${runId}`)
}

export function subAgentStreamUrl(agentId: string, runId: number): string {
  return `/api/agents/${agentId}/sub-agents/runs/${runId}/stream`
}

/** 侧边栏「智能体」清除:移除已结束的子代理运行(运行中/不存在 → 404) */
export function deleteSubAgentRun(agentId: string, runId: number): Promise<void> {
  return postJson(`/api/agents/${agentId}/sub-agents/runs/${runId}/delete`).then(() => undefined)
}

/** 侧边栏「后台任务」清除:移除已结束的任务(运行中/不存在 → 404) */
export function deleteBgTask(agentId: string, sessionId: string, taskId: number): Promise<void> {
  return postJson(`/api/agents/${agentId}/sessions/${sessionId}/tasks/${taskId}/delete`).then(() => undefined)
}

/** 侧边栏「后台任务」停止:终止运行中的任务(已结束/不存在 → 404) */
export function killBgTask(agentId: string, sessionId: string, taskId: number): Promise<void> {
  return postJson(`/api/agents/${agentId}/sessions/${sessionId}/tasks/${taskId}/kill`).then(() => undefined)
}
