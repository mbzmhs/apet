// 侧栏宽度拖动:按下后宽度实时变化,松手持久化到 localStorage(每浏览器各自记住);
// 初始值/上下限由调用方给定,存储值越界时夹回限内。
// dir: 'left' = 面板在左(向右拖变宽),'right' = 面板在右(向右拖变窄)。

import { onScopeDispose, reactive, ref } from 'vue'

export interface ResizeHandle {
  /** 面板宽度(px);reactive 包装后模板里直接绑数值(普通对象套 Ref 不会被模板解包,绑上就是无效样式) */
  width: number
  handlers: { onpointerdown: (e: PointerEvent) => void }
}

export function useResize(key: string, initial: number, min: number, max: number, dir: 'left' | 'right'): ResizeHandle {
  let stored = Number.NaN
  try {
    stored = Number(localStorage.getItem(key))
  } catch {
    /* 存储不可用时仅内存态 */
  }
  const width = ref(Number.isFinite(stored) ? Math.min(max, Math.max(min, stored)) : initial)

  let dragging = false
  let activeId = -1
  let startX = 0
  let startW = 0
  // 拖动期间挂起的 iframe(插件标签页如 GpuMonitor 是 iframe):pointer 事件会被 iframe 内文档
  // 吞掉,window 级监听收不到 move/up —— 表现为拖不动、松手状态卡死、之后无按键移动也"自动拖动"。
  // 拖动期间统一禁用其指针事件,结束/卸载时恢复。
  let suspended: HTMLElement[] = []

  const suspendIframes = () => {
    suspended = Array.from(document.querySelectorAll('iframe')) as HTMLElement[]
    for (const f of suspended) f.style.pointerEvents = 'none'
  }

  const resumeIframes = () => {
    for (const f of suspended) f.style.pointerEvents = ''
    suspended = []
  }

  // 拖动期间在 window 收指针事件,而不是依赖 setPointerCapture:
  // 把手只有 5px 宽,指针一移出去事件就改派给底下元素;部分内嵌浏览器/WebView 的指针捕获不可靠,
  // 靠捕获会导致"按下能触发、拖不动"。window 层收事件对两者都免疫(按 pointerId 过滤多指/多指针)。
  const onMove = (e: PointerEvent) => {
    if (!dragging || e.pointerId !== activeId) return
    if (e.buttons === 0) {
      settle(e) // 兜底:pointerup 在窗口外/被吞时按键已全部松开,立即结束,防止无按键"自动拖动"
      return
    }
    const d = e.clientX - startX
    width.value = Math.min(max, Math.max(min, dir === 'left' ? startW + d : startW - d))
  }

  const settle = (e: PointerEvent) => {
    if (!dragging || e.pointerId !== activeId) return
    dragging = false
    activeId = -1
    resumeIframes()
    document.body.classList.remove('resizing-h')
    try {
      localStorage.setItem(key, String(width.value))
    } catch {
      /* 忽略 */
    }
  }

  const onPointerDown = (e: PointerEvent) => {
    e.preventDefault()
    dragging = true
    activeId = e.pointerId
    startX = e.clientX
    startW = width.value
    suspendIframes()
    document.body.classList.add('resizing-h')
  }

  window.addEventListener('pointermove', onMove)
  window.addEventListener('pointerup', settle)
  window.addEventListener('pointercancel', settle)
  onScopeDispose(() => {
    window.removeEventListener('pointermove', onMove)
    window.removeEventListener('pointerup', settle)
    window.removeEventListener('pointercancel', settle)
    resumeIframes()
    document.body.classList.remove('resizing-h')
  })

  return reactive({ width, handlers: { onpointerdown: onPointerDown } })
}
