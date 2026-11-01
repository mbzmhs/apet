// 界面缩放:写根元素 CSS zoom(等效桌面端 uiScale;每 Agent 独立)。
// 各窗口在页面就绪时按所看 Agent 的设置应用(此前只在设置页应用,重启后聊天窗不生效)。

import { getSettings } from '../api/client'

export function applyUiScale(v: unknown) {
  const n = Number(v)
  document.documentElement.style.zoom =
    Number.isFinite(n) && n >= 0.5 && n <= 2.5 && n !== 1 ? String(n) : ''
}

/** 拉取该 Agent 的缩放设置并应用(失败/已停用时保留当前缩放) */
export async function applyAgentUiScale(agentId: string) {
  try {
    const s = await getSettings<any>(agentId)
    const n = Number(s?.uiScale)
    if (Number.isFinite(n)) applyUiScale(n)
  } catch {
    // 保留当前缩放
  }
}
