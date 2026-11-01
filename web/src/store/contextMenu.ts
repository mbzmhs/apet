// 全局自定义右键菜单:main.ts 的 contextmenu 策略收集候选动作后经此弹出。
// 坐标与 ChatView/AppSelect 一样做界面缩放(zoom)补偿:clientX 是可视像素,
// fixed 定位 px 会被 zoom 放大,需除回 zoom。
import { ref } from 'vue'

export interface ContextMenuItem {
  label: string
  icon?: string
  danger?: boolean
  disabled?: boolean
  action: () => void
}

export const contextMenuState = ref<{ x: number; y: number; items: ContextMenuItem[] } | null>(null)

export function closeContextMenu() {
  contextMenuState.value = null
}

/** 界面缩放因子(未缩放 = 1);与 ChatView/AppSelect 同一口径 */
export function zoomFactor() {
  const z = Number(getComputedStyle(document.documentElement).zoom)
  return Number.isFinite(z) && z > 0 ? z : 1
}

export function openContextMenu(clientX: number, clientY: number, items: ContextMenuItem[]) {
  if (!items.length) return
  const z = zoomFactor()
  const width = 150
  const height = items.length * 34 + 8
  const x = Math.min(clientX, window.innerWidth - width * z - 8) / z
  const y = Math.min(clientY, window.innerHeight - height * z - 8) / z
  contextMenuState.value = { x, y, items }
}
