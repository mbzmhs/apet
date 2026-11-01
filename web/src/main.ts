import { createApp } from 'vue'
import App from './App.vue'
import './theme/tokens.css'
import './theme/icons.css'
import './assets/list-rows.css'
import { initTheme } from './theme/theme'
import { openContextMenu, closeContextMenu } from './store/contextMenu'

// 挂载前先落主题,避免亮色用户首帧闪暗色
initTheme()

// ───────── 应用外壳行为约束(同桌面端感受,浏览器默认行为收敛) ─────────
// 右键策略:输入框/文本域保留原生菜单(复制/粘贴);标注 data-copy 的可复制文本
// (消息正文/思考/工具详情等)弹出应用内自定义菜单(复制所选/复制全文);
// 其余区域一律禁用。会话列表等组件自有菜单在元素层处理,不受影响。
document.addEventListener('contextmenu', (e) => {
  const t = e.target as HTMLElement | null
  if (t?.closest('input, textarea, [contenteditable="true"]')) return // 原生菜单:复制/粘贴
  e.preventDefault()
  const copyEl = t?.closest('[data-copy]') as HTMLElement | null
  const selection = (window.getSelection()?.toString() ?? '').trim()
  if (!copyEl && !selection) {
    closeContextMenu()
    return
  }
  const items = []
  if (selection)
    items.push({
      label: '复制所选',
      icon: 'content_copy',
      action: () => navigator.clipboard.writeText(selection).catch(() => {}),
    })
  if (copyEl) {
    const full = (copyEl.innerText ?? '').trim()
    if (full && full !== selection)
      items.push({
        label: '复制全文',
        icon: 'content_copy',
        action: () => navigator.clipboard.writeText(full).catch(() => {}),
      })
  }
  openContextMenu(e.clientX, e.clientY, items)
})
// 鼠标侧键(3=后退 4=前进):禁用历史导航
document.addEventListener('mouseup', (e) => {
  if (e.button === 3 || e.button === 4) e.preventDefault()
})
document.addEventListener('auxclick', (e) => {
  if (e.button === 3 || e.button === 4) e.preventDefault()
})
// 拖拽:禁止把文件/文本拖进窗口触发浏览器导航(聊天页自身的附件接收不受影响)
document.addEventListener('dragover', (e) => e.preventDefault())
document.addEventListener('drop', (e) => e.preventDefault())

const app = createApp(App)
// 渲染错误默认静默卸载应用:写入 DOM 便于远程诊断(生产可移除)
app.config.errorHandler = (err, _instance, info) => {
  console.error(err, info)
  const el = document.createElement('pre')
  el.id = 'vue-error'
  el.style.cssText = 'position:fixed;inset:auto 0 0 0;max-height:40%;overflow:auto;background:#400;color:#fff;font-size:11px;padding:8px;z-index:9999;white-space:pre-wrap'
  el.textContent = String((err as any)?.stack ?? err) + '\n[info] ' + info
  document.body.appendChild(el)
}
app.mount('#app')

// 滑条左填充(--fill):WPF Slider 同款「已划过部分 Accent、其余暗色」观感
function syncRangeFill(el: HTMLInputElement) {
  const min = Number(el.min || 0)
  const max = Number(el.max || 100)
  const pct = ((Number(el.value) - min) / (max - min || 1)) * 100
  el.style.setProperty('--fill', pct + '%')
}
document.addEventListener('input', (e) => {
  const t = e.target as HTMLInputElement
  if (t.type === 'range') syncRangeFill(t)
})
