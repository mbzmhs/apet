// 聊天页跨重挂载的轻量缓存:切换会话时整个 ChatView 因路由 key 重挂载,
// 没有缓存会经历「空白 → 拉取 → 填充」的闪烁;这里把最近数据留在模块级,
// 重挂载时先渲染缓存再后台刷新,视觉上即做到平滑过渡。
import type { ArchivedSession, FolderInfo, SessionListItem, SessionState } from '../types'

const sessionsByAgent = new Map<string, SessionListItem[]>()
const archivedByAgent = new Map<string, ArchivedSession[]>()
const foldersByAgent = new Map<string, FolderInfo[]>()
const stateBySession = new Map<string, SessionState>()
const ownerBySession = new Map<string, string>()
const titleByAgent = new Map<string, string>()
const avatarRevByAgent = new Map<string, number>()

// 消息快照可能较大,只保留最近几个会话
const MAX_STATES = 8

export function cachedSessions(agentId: string): SessionListItem[] | null {
  return sessionsByAgent.get(agentId) ?? null
}

export function storeSessions(agentId: string, list: SessionListItem[]) {
  sessionsByAgent.set(agentId, list)
  sessionsAtByAgent.set(agentId, Date.now())
}

export function cachedArchived(agentId: string): ArchivedSession[] | null {
  return archivedByAgent.get(agentId) ?? null
}

export function storeArchived(agentId: string, list: ArchivedSession[]) {
  archivedByAgent.set(agentId, list)
}

// 文件夹树缓存:与归档列表同例,切会话重挂载先渲染缓存再后台刷新
export function cachedFolders(agentId: string): FolderInfo[] | null {
  return foldersByAgent.get(agentId) ?? null
}
export function storeFolders(agentId: string, list: FolderInfo[]) {
  foldersByAgent.set(agentId, list)
}

// 会话列表最近一次成功拉取的时刻:切换会话重挂载时数据仍新鲜即跳过立即重拉(5s 轮询照常兜底)
const sessionsAtByAgent = new Map<string, number>()
export function sessionsFresh(agentId: string, maxAgeMs: number): boolean {
  const at = sessionsAtByAgent.get(agentId)
  return at != null && Date.now() - at < maxAgeMs
}

// 最近一次上屏的会话列表(单条,含所属 Agent):切换会话重挂载、新列表尚未到达前,
// 先继续显示旧列表,避免侧栏"空白→填充"的闪烁
let lastShownList: { agentId: string; list: SessionListItem[] } | null = null
export function markListShown(agentId: string, list: SessionListItem[]) {
  lastShownList = { agentId, list }
}
export function lastShownListSnapshot(): { agentId: string; list: SessionListItem[] } | null {
  return lastShownList
}

// 记住每个 Agent 最近聊过的会话(localStorage 持久,跨重启有效):
// 设置页返回 / 点 Agent 卡片时回到原会话,而不是永远跳到最新会话
const LAST_PREFIX = 'apet.lastChat.'
const lastChatByAgent = new Map<string, string>()
export function storeLastChat(agentId: string, sessionId: string) {
  lastChatByAgent.set(agentId, sessionId)
  try { localStorage.setItem(LAST_PREFIX + agentId, sessionId) } catch { /* 存储不可用时仅内存态 */ }
}
export function cachedLastChat(agentId: string): string | null {
  const mem = lastChatByAgent.get(agentId)
  if (mem) return mem
  try { return localStorage.getItem(LAST_PREFIX + agentId) } catch { return null }
}

export function cachedState(sessionId: string): SessionState | null {
  return stateBySession.get(sessionId) ?? null
}

export function storeState(sessionId: string, s: SessionState) {
  stateBySession.delete(sessionId)
  stateBySession.set(sessionId, s)
  if (stateBySession.size > MAX_STATES) {
    stateBySession.delete(stateBySession.keys().next().value!)
  }
}

export function dropState(sessionId: string) {
  stateBySession.delete(sessionId)
}

export function cachedOwner(sessionId: string): string | null {
  return ownerBySession.get(sessionId) ?? null
}

export function storeOwner(sessionId: string, agentId: string) {
  ownerBySession.set(sessionId, agentId)
}

/** 列表拉取时批量记住:列表中每个会话的归属都已知,切换它们无需再发 owner 解析请求 */
export function storeOwners(agentId: string, sessionIds: string[]) {
  for (const id of sessionIds) ownerBySession.set(id, agentId)
}

// Agent 清单缓存(首页加载过即有):聊天页首帧直接取标题/头像,不等 getAgents 往返
let agentListCache: { id: string; title: string; avatarRev: number }[] | null = null
export function storeAgentList(list: readonly { id: string; title: string; avatarRev: number }[]) {
  agentListCache = [...list]
}
export function cachedAgentList(): { id: string; title: string; avatarRev: number }[] | null {
  return agentListCache
}

export function cachedAgentTitle(agentId: string): string | null {
  return titleByAgent.get(agentId) ?? null
}

export function storeAgentTitle(agentId: string, title: string) {
  titleByAgent.set(agentId, title)
}

export function cachedAgentAvatarRev(agentId: string): number | null {
  return avatarRevByAgent.get(agentId) ?? null
}

export function storeAgentAvatarRev(agentId: string, rev: number) {
  avatarRevByAgent.set(agentId, rev)
}
