// Markdown 渲染:marked 解析 + DOMPurify 净化(模型产出可能含抓取的网页内容,必须防注入)

import DOMPurify from 'dompurify'
import { marked } from 'marked'

marked.setOptions({ gfm: true, breaks: true })

export function renderMarkdown(text: string): string {
  const html = marked.parse(text ?? '') as string
  const clean = DOMPurify.sanitize(html, { ADD_ATTR: ['target'] })
  // 所有链接新窗口打开:WebShell webview 经 NewWindowRequested 交给系统默认浏览器,
  // 普通浏览器则开新标签——避免在应用内导航走丢(代码块内文本已被转义,不会误伤)
  return clean.replace(/<a /g, '<a target="_blank" rel="noreferrer" ')
}
