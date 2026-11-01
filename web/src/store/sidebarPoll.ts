// 侧边栏与状态栏共用的单一轮询器:此前两者各自 setInterval(3s / 2.5s)打同一端点,
// 双槽位过渡期(旧组件未卸载、新组件已挂载)并发 4 份;统一 3s 一拍,帧分发给全部订阅者。
import { getSidebar, type SidebarInfo } from '../api/client'

type FrameHandler = (info: SidebarInfo) => void
type ErrorHandler = (err: unknown) => void

const frames = new Set<FrameHandler>()
const errors = new Set<ErrorHandler>()
let timer: number | null = null
let inFlight = false
let agentId = ''
let sessionId = ''
let latest: SidebarInfo | null = null

async function tick() {
  // agentId 异步解析(路由 hash → getSessionOwner):未就绪不发请求,避免 /api/agents//… 404
  if (inFlight || !agentId) return
  inFlight = true
  try {
    latest = await getSidebar(agentId, sessionId)
    for (const h of [...frames]) h(latest)
  } catch (e) {
    for (const h of [...errors]) h(e)
  } finally {
    inFlight = false
  }
}

/** 更新轮询目标(agentId 深链解析后/切会话时由组件上报) */
export function setSidebarTarget(agent: string, session: string): void {
  agentId = agent
  sessionId = session
}

/** 订阅侧边栏帧:立即拉一拍,之后每 3s 一拍;无订阅者时轮询停止。返回退订函数。 */
export function subscribeSidebar(onFrame: FrameHandler, onError?: ErrorHandler): () => void {
  frames.add(onFrame)
  if (onError) errors.add(onError)
  if (timer === null) timer = window.setInterval(tick, 3000)
  void tick()
  return () => {
    frames.delete(onFrame)
    if (onError) errors.delete(onError)
    if (frames.size === 0 && timer !== null) {
      window.clearInterval(timer)
      timer = null
    }
  }
}

/** 主动刷新(操作后立即拉一拍,如杀任务/清除运行) */
export function refreshSidebarNow(): void {
  void tick()
}

/** 最近一帧(组件重挂载先渲染不闪) */
export function lastSidebarFrame(): SidebarInfo | null {
  return latest
}
