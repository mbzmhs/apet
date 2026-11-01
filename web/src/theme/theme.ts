// 明暗主题切换:写 documentElement[data-theme] 驱动 tokens.css 的亮色令牌;
// 选择经 localStorage 持久化(每个浏览器各自记住),默认暗色(与桌面端一致)。
// localStorage 访问包 try:隐私模式/禁用存储会抛 SecurityError——不能因此白屏,主题回落默认即可。
export type ThemeMode = 'dark' | 'light'

const KEY = 'apet-theme'

export function currentTheme(): ThemeMode {
  try {
    return localStorage.getItem(KEY) === 'light' ? 'light' : 'dark'
  } catch {
    return 'dark'
  }
}

export function applyTheme(t: ThemeMode) {
  document.documentElement.dataset.theme = t
  try {
    localStorage.setItem(KEY, t)
  } catch {
    // 存储不可用:主题本次会话内生效,不持久化
  }
}

export function toggleTheme(): ThemeMode {
  const next: ThemeMode = currentTheme() === 'dark' ? 'light' : 'dark'
  applyTheme(next)
  return next
}

export function initTheme() {
  document.documentElement.dataset.theme = currentTheme()
}
