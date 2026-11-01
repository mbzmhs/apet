// diff 行内代码高亮:highlight.js 核心 + 常用语言子集(按文件扩展名匹配),
// 逐行高亮(多行构造跨行时按单行近似,展示用途足够);结果缓存避免流式重渲染重复解析
import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import csharp from 'highlight.js/lib/languages/csharp'
import cpp from 'highlight.js/lib/languages/cpp'
import css from 'highlight.js/lib/languages/css'
import go from 'highlight.js/lib/languages/go'
import java from 'highlight.js/lib/languages/java'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import markdown from 'highlight.js/lib/languages/markdown'
import python from 'highlight.js/lib/languages/python'
import rust from 'highlight.js/lib/languages/rust'
import sql from 'highlight.js/lib/languages/sql'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'
import ini from 'highlight.js/lib/languages/ini'

hljs.registerLanguage('bash', bash)
hljs.registerLanguage('csharp', csharp)
hljs.registerLanguage('cpp', cpp)
hljs.registerLanguage('css', css)
hljs.registerLanguage('go', go)
hljs.registerLanguage('java', java)
hljs.registerLanguage('javascript', javascript)
hljs.registerLanguage('json', json)
hljs.registerLanguage('markdown', markdown)
hljs.registerLanguage('python', python)
hljs.registerLanguage('rust', rust)
hljs.registerLanguage('sql', sql)
hljs.registerLanguage('typescript', typescript)
hljs.registerLanguage('xml', xml)
hljs.registerLanguage('yaml', yaml)
hljs.registerLanguage('ini', ini)

const EXT_LANG: Record<string, string> = {
  ts: 'typescript', mts: 'typescript', cts: 'typescript', tsx: 'typescript',
  js: 'javascript', mjs: 'javascript', cjs: 'javascript', jsx: 'javascript',
  vue: 'xml', html: 'xml', htm: 'xml', xhtml: 'xml', xml: 'xml', svg: 'xml', csproj: 'xml',
  css: 'css', scss: 'css', less: 'css',
  json: 'json', jsonc: 'json',
  md: 'markdown', markdown: 'markdown',
  sh: 'bash', bash: 'bash', zsh: 'bash', cmd: 'bash', bat: 'bash', ps1: 'bash',
  py: 'python', pyw: 'python',
  cs: 'csharp', csx: 'csharp',
  c: 'cpp', h: 'cpp', cc: 'cpp', cpp: 'cpp', cxx: 'cpp', hh: 'cpp', hpp: 'cpp',
  java: 'java',
  go: 'go',
  rs: 'rust',
  yml: 'yaml', yaml: 'yaml',
  ini: 'ini', toml: 'ini', conf: 'ini', cfg: 'ini',
  sql: 'sql',
}

export function langOf(fileName: string): string | null {
  const i = fileName.lastIndexOf('.')
  if (i < 0) return null
  return EXT_LANG[fileName.slice(i + 1).toLowerCase()] ?? null
}

const cache = new Map<string, string>()

/** 单行高亮:返回转义后的 HTML(hljs 已转义 <>&);不支持的扩展名/空行返回 null */
export function highlightLine(line: string, fileName: string): string | null {
  const lang = langOf(fileName)
  if (!lang || line.length === 0) return null
  const key = lang + '\u0000' + line
  const hit = cache.get(key)
  if (hit !== undefined) return hit
  let out = ''
  try {
    out = hljs.highlight(line, { language: lang, ignoreIllegals: true }).value
  } catch {
    out = ''
  }
  if (cache.size > 12000) cache.clear()
  cache.set(key, out)
  return out
}
