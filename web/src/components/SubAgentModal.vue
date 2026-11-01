<script setup lang="ts">
// 子代理运行模态框:只读回看 sub_run 委托的子代理完整过程(思考/正文/工具调用)。
// 工具帧由宿主按 ToolCallPresenter 格式化后下发(与主聊天 SSE 帧同构),渲染复用主窗体的
// 工具卡/文件卡/diff 结构与样式,视觉保持一致。
// 运行中:订阅 SSE 实时追加 + 每 10 秒拉一次快照整体重放(自愈断档/状态收口);结束后仅快照。
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { getSubAgentRun, subAgentStreamUrl, type SubAgentRunDetail } from '../api/client'
import { subscribeStream } from '../api/sse'
import { highlightLine } from '../lib/highlight'
import { renderMarkdown } from '../lib/markdown'
import { appendToolOutput } from '../lib/toolOutput'
import ModalDialog from './ModalDialog.vue'

const props = defineProps<{ agentId: string; runId: number }>()
const emit = defineEmits<{ close: [] }>()

interface Block {
  kind: 'thinking' | 'content' | 'tool' | 'compress' | 'divider' | 'tell'
  text?: string
  isActive?: boolean
  // 压缩行:done = 已完成(文案变「会话已压缩」);failed = 未完成即告终(摘要为空/中断,文案变「压缩失败」)
  done?: boolean
  failed?: boolean
  // 工具行:与主聊天 ToolStep 同构(后端 ToolCallPresenter 格式化)
  toolName?: string
  displayName?: string
  summary?: string
  detail?: string
  hasDetail?: boolean
  pathLink?: string
  statAdd?: string
  statDel?: string
  diffLines?: { lineNo: string; text: string; kind: string }[]
  resultSummary?: string
  isError?: boolean
  isRunning?: boolean
}

const detail = ref<SubAgentRunDetail | null>(null)
const blocks = ref<Block[]>([])
const error = ref('')
let closeStream: (() => void) | null = null
let pollTimer: number | null = null

const statusText = computed(() => {
  switch (detail.value?.status) {
    case 'running': return '运行中'
    case 'completed': return '已完成'
    case 'failed': return '失败'
    case 'cancelled': return '已取消'
    default: return detail.value?.status ?? ''
  }
})

/** 工具帧 → 行(后端格式化帧;旧版原始 args/result 帧降级兜底) */
function toolBlock(d: any): Block {
  if (d.summary !== undefined || d.detail !== undefined) {
    return {
      kind: 'tool',
      toolName: d.toolName,
      displayName: d.displayName ?? d.toolName,
      summary: d.summary ?? '',
      detail: d.detail ?? '',
      hasDetail: !!d.hasDetail,
      pathLink: d.pathLink ?? '',
      statAdd: d.statAdd ?? '',
      statDel: d.statDel ?? '',
      diffLines: Array.isArray(d.diffLines) ? d.diffLines : [],
      resultSummary: d.resultSummary ?? '',
      isError: !!d.isError,
      isRunning: true,
    }
  }
  return {
    kind: 'tool',
    toolName: d.toolName,
    displayName: d.displayName ?? d.toolName,
    summary: d.args ?? '',
    detail: '',
    hasDetail: false,
    isRunning: true,
  }
}

function finishTool(list: Block[], d: any) {
  for (let i = list.length - 1; i >= 0; i--) {
    const b = list[i]
    if (b.kind !== 'tool' || !b.isRunning || b.toolName !== d.toolName) continue
    b.isRunning = false
    if (d.summary !== undefined || d.detail !== undefined) {
      b.summary = d.summary ?? b.summary
      b.detail = d.detail ?? b.detail
      b.hasDetail = !!d.hasDetail
      b.pathLink = d.pathLink ?? b.pathLink
      b.statAdd = d.statAdd ?? b.statAdd
      b.statDel = d.statDel ?? b.statDel
      b.diffLines = Array.isArray(d.diffLines) ? d.diffLines : b.diffLines
      b.resultSummary = d.resultSummary ?? ''
      b.isError = !!d.isError
    }
    else {
      b.detail = (b.detail || d.result || '')
      b.hasDetail = !!d.result
      b.resultSummary = d.isError ? (d.result || '失败') : '完成'
      b.isError = !!d.isError
    }
    return
  }
}

/** 从事件帧整体重放(服务端帧日志是完整事实源,重放结果确定,可反复执行) */
function derive(frames: { type: string; data: any }[], running: boolean) {
  const list: Block[] = []
  for (const f of frames) {
    switch (f.type) {
      case 'thinking_delta': {
        // 代理恢复产出 = 上一段压缩已告终:未收到完成帧则它失败了,行收口为「压缩失败」
        failCompress(list)
        const last = list[list.length - 1]
        if (last?.kind === 'thinking') last.text = (last.text ?? '') + f.data.delta
        else list.push({ kind: 'thinking', text: f.data.delta })
        break
      }
      case 'content_delta': {
        failCompress(list)
        const last = list[list.length - 1]
        if (last?.kind === 'thinking') last.isActive = false
        if (last?.kind === 'content') last.text = (last.text ?? '') + f.data.delta
        else list.push({ kind: 'content', text: f.data.delta })
        break
      }
      case 'tool_started': {
        failCompress(list)
        const last = list[list.length - 1]
        if (last?.kind === 'thinking') last.isActive = false
        list.push(toolBlock(f.data))
        break
      }
      case 'tell': {
        // 主 Agent 传话(sub_tell):以醒目插入行呈现在子代理的对话流里
        failCompress(list)
        const last = list[list.length - 1]
        if (last?.kind === 'thinking') last.isActive = false
        list.push({ kind: 'tell', text: f.data.text })
        break
      }
      case 'tool_finished':
        finishTool(list, f.data)
        break
      case 'tool_output':
        // 工具执行中的实时输出增量(运行终端工具):续接到运行中的工具行
        // (子代理帧不带 callId,按工具名匹配)
        appendToolOutput(list as any[], f.data)
        break
      case 'compression_started': {
        const last = list[list.length - 1]
        if (last?.kind === 'thinking') last.isActive = false
        list.push({ kind: 'compress' })
        break
      }
      case 'compression_finished':
        // 有"压缩中"行则原位收口为"会话已压缩"
        for (let i = list.length - 1; i >= 0; i--) {
          if (list[i].kind !== 'compress' || list[i].done || list[i].failed) continue
          list[i].done = true
          break
        }
        break
      case 'compression_closed':
        // 运行结束但压缩未收口(总结失败/取消):行收口为「压缩失败」
        failCompress(list)
        break
      // complete / error / usage:状态由运行记录体现,不进气泡流
    }
  }
  // 收尾:运行中最后一段思考保持"思考中"流光态;结束后全部恢复常态
  if (list.length) {
    const last = list[list.length - 1]
    last.isActive = running && last.kind === 'thinking'
  }
  if (!running)
    // 仍在"压缩中"未收口的行是临时条目,运行结束后消失;已完成/已失败的保留
    list.splice(0, list.length, ...list.filter(b => !(b.kind === 'compress' && !b.done && !b.failed)))
  blocks.value = list
  nextTickScroll()
}

/** SSE 实时追加(只影响未收口前的显示;快照轮询会整体重放校正) */
function applyFrame(type: string, data: any) {
  const d = detail.value
  if (!d) return
  switch (type) {
    case 'thinking_delta': {
      // 代理恢复产出 = 上一段压缩已告终:未收到完成帧则它失败了,行收口为「压缩失败」
      failCompress(blocks.value)
      const last = blocks.value[blocks.value.length - 1]
      if (last?.kind === 'thinking') { last.text = (last.text ?? '') + data.delta; last.isActive = true }
      else blocks.value.push({ kind: 'thinking', text: data.delta, isActive: true })
      break
    }
    case 'content_delta': {
      failCompress(blocks.value)
      const last = blocks.value[blocks.value.length - 1]
      if (last?.kind === 'thinking') last.isActive = false
      if (last?.kind === 'content') last.text = (last.text ?? '') + data.delta
      else blocks.value.push({ kind: 'content', text: data.delta })
      break
    }
    case 'tool_started': {
      failCompress(blocks.value)
      const last = blocks.value[blocks.value.length - 1]
      if (last?.kind === 'thinking') last.isActive = false
      blocks.value.push(toolBlock(data))
      break
    }
    case 'tell': {
      failCompress(blocks.value)
      const last = blocks.value[blocks.value.length - 1]
      if (last?.kind === 'thinking') last.isActive = false
      blocks.value.push({ kind: 'tell', text: data.text })
      break
    }
    case 'tool_finished':
      finishTool(blocks.value, data)
      break
    case 'tool_output':
      appendToolOutput(blocks.value as any[], data)
      break
    case 'compression_started': {
      const last = blocks.value[blocks.value.length - 1]
      if (last?.kind === 'thinking') last.isActive = false
      blocks.value.push({ kind: 'compress' })
      break
    }
    case 'compression_finished': {
      for (let i = blocks.value.length - 1; i >= 0; i--) {
        if (blocks.value[i].kind !== 'compress' || blocks.value[i].done || blocks.value[i].failed) continue
        blocks.value[i].done = true
        break
      }
      break
    }
    case 'compression_closed':
      failCompress(blocks.value)
      break
    case 'complete':
      d.status = 'completed'
      settle()
      break
    case 'error':
      d.status = 'failed'
      d.error = data.message
      settle()
      break
  }
  nextTickScroll()
}

function nextTickScroll() {
  nextTick(() => {
    // 真正的滚动容器是 ModalDialog 的 .mdl-body:自内层锚点向上找到它再滚,
    // 内层不再自带滚动区(之前双层滚动导致新内容/忙碌行滚不进来)
    let el = bodyEl.value
    while (el && el !== document.body) {
      const ov = getComputedStyle(el).overflowY
      if ((ov === 'auto' || ov === 'scroll') && el.scrollHeight > el.clientHeight)
        break
      el = el.parentElement
    }
    if (el && el !== document.body)
      el.scrollTop = el.scrollHeight
  })
}
const bodyEl = ref<HTMLElement | null>(null)

/** 最后一个"压缩中"行收口为「压缩失败」:代理恢复产出或运行结束都说明这段压缩未成功即告终
 * (插件失败/取消时不发完成帧),行保留作为事实记录 */
function failCompress(list: Block[]) {
  for (let i = list.length - 1; i >= 0; i--) {
    if (list[i].kind !== 'compress' || list[i].done || list[i].failed) continue
    list[i].failed = true
    break
  }
}

/** 运行收口:思考行恢复"思考"常态;仍在"压缩中"未收口的行是临时条目消失,已完成/已失败的保留 */
function settle() {
  for (const b of blocks.value)
    if (b.kind === 'thinking') b.isActive = false
  blocks.value = blocks.value.filter(b => !(b.kind === 'compress' && !b.done && !b.failed))
}

// 并发请求序号:只允许最新一次请求的结果落状态/动流,迟到的旧响应作废
// (快速切 runId 时旧快照后到会覆盖新 run 的列表、甚至关掉新 run 刚开的流;仿 ChatView listSeq)
let loadSeq = 0

async function load() {
  const seq = ++loadSeq
  error.value = ''
  try {
    const d = await getSubAgentRun(props.agentId, props.runId)
    if (seq !== loadSeq) return // 已切到别的 run:迟到的旧快照作废,不落状态、不动流
    detail.value = d
    derive(d.frames, d.status === 'running')
    if (d.status === 'running') {
      closeStream?.()
      closeStream = subscribeStream(subAgentStreamUrl(props.agentId, props.runId), applyFrame)
      startPoll()
    } else {
      stopLive()
    }
  } catch (e) {
    if (seq !== loadSeq) return // 旧请求的失败同样作废:不拿旧 run 的报错盖新 run 的界面
    error.value = String(e)
  }
}

function startPoll() {
  stopPoll()
  pollTimer = window.setInterval(load, 10000)
}

function stopPoll() {
  if (pollTimer !== null) window.clearInterval(pollTimer)
  pollTimer = null
}

function stopLive() {
  closeStream?.()
  closeStream = null
  stopPoll()
}

watch(() => props.runId, () => load())
onMounted(load)
onUnmounted(stopLive)

function html(text: string) {
  return renderMarkdown(text)
}

// 折叠行展开状态按步骤序号记忆(流式更新不打断用户手动展开;思考行默认收起,同主窗体)
const openRows = ref<Set<number>>(new Set())

function toggleRow(i: number) {
  const s = openRows.value
  s.has(i) ? s.delete(i) : s.add(i)
  openRows.value = new Set(s)
}

function isOpen(i: number) {
  return openRows.value.has(i)
}

// ───────── 与主窗体工具行同口径的判定/高亮/预览 ─────────

/** 文件卡片:带文件名且可展开(有 diff 或详情)的工具行,展开区按 IDE 风格 diff 卡渲染 */
function isFileCard(b: Block): boolean {
  return b.kind === 'tool' && !!b.pathLink && (!!b.diffLines?.length || !!b.hasDetail)
}

/** 工具行是否可展开(有详情或有 diff) */
function canExpand(b: Block): boolean {
  return !!b.hasDetail || !!b.diffLines?.length
}

/** diff 单行语法高亮(按文件扩展名;不支持返回 null 走纯文本) */
function hlLine(line: string, fileName: string): string | null {
  return highlightLine(line, fileName ?? '')
}

/** 折叠行预览 = 最新一行思考内容(超长取尾部 200 字符,同主窗体) */
function thinkingPreview(b: Block): string {
  const trimmed = (b.text || '').replace(/[\r\n]+$/, '')
  if (!trimmed) return ''
  const nl = Math.max(trimmed.lastIndexOf('\n'), trimmed.lastIndexOf('\r'))
  const line = (nl < 0 ? trimmed : trimmed.slice(nl + 1)).trim()
  return line.length > 200 ? line.slice(-200) : line
}
</script>

<template>
  <ModalDialog
    :title="`#${props.runId} ${detail?.title || detail?.name || ''}`"
    :sub="`${statusText} · ${detail?.startedAt?.slice(11, 19) ?? ''}${detail?.endedAt ? ' → ' + detail.endedAt.slice(11, 19) : ''}`"
    width="720px"
    @close="emit('close')"
  >
    <div v-if="error" class="err">{{ error }}</div>
    <div v-else ref="bodyEl">
      <div v-if="detail" class="prompt">
        <span class="ms">call_split</span> 委托任务({{ detail.title || detail.name }}):{{ detail.prompt }}
      </div>
      <template v-for="(b, i) in blocks" :key="i">
        <div v-if="b.kind === 'content'" class="md" data-copy v-html="html(b.text ?? '')" />
        <div v-else-if="b.kind === 'tell'" class="tell-row" data-copy>
          <span class="tell-ico ms">forum</span>
          <span class="tell-label">主 Agent 传话</span>
          <span class="tell-text">{{ b.text }}</span>
        </div>
        <!-- 上下文压缩:与主聊天同款(压缩中=两侧横线+流光文字;完成后原位变"会话已压缩"分割线,不再追加第二条) -->
        <div v-else-if="b.kind === 'compress'" class="compress-row" :class="{ done: b.done, failed: b.failed }" data-copy>
          <span v-if="b.done" class="compressed-text">会话已压缩</span>
          <span v-else-if="b.failed" class="compress-failed-text">压缩失败</span>
          <span v-else class="compressing-text">上下文压缩中</span>
        </div>
        <!-- 思考行:与主窗体同构(默认收起由用户点击展开;运行中标题 shimmer + 折叠行尾部预览) -->
        <details v-else-if="b.kind === 'thinking'" class="seg" :open="isOpen(i)" @toggle="isOpen(i) !== ($event.target as HTMLDetailsElement).open && toggleRow(i)">
          <summary>
            <span class="chev ms">arrow_right</span>
            <span class="seg-header" :class="{ run: b.isActive }">{{ b.isActive ? '思考中' : '思考' }}</span>
            <span class="seg-preview">{{ thinkingPreview(b) }}</span>
          </summary>
          <div class="seg-body" data-copy>{{ b.text }}</div>
        </details>
        <!-- 工具行:齿轮(运行中旋转/完成√)+ 名称 + 摘要 + 结果摘要(错红)+ 行数统计;
             文件类工具展开为 IDE 风格 diff 卡片,其余展开为详情 -->
        <details v-else class="seg tool" :class="{ filetool: isFileCard(b) }">
          <summary @click="!canExpand(b) && $event.preventDefault()">
            <span class="tool-icon ms" :class="{ spin: b.isRunning, fill: b.isRunning }">{{ b.isRunning ? 'settings' : 'check' }}</span>
            <span class="tool-name" :class="{ run: b.isRunning }">{{ b.displayName }}</span>
            <span class="tool-summary">{{ b.summary }}</span>
            <span v-if="b.statAdd" class="stat add">{{ b.statAdd }}</span>
            <span v-if="b.statDel" class="stat del">{{ b.statDel }}</span>
            <span v-if="!b.isRunning" class="tool-result" :class="{ err: b.isError }">{{ b.resultSummary }}</span>
            <span v-if="canExpand(b)" class="chev ms">arrow_right</span>
          </summary>
          <!-- 文件卡片:头部文件图标+文件名+行数统计,正文带行号红绿 diff + 按扩展名语法高亮 -->
          <div v-if="isFileCard(b)" class="filecard" :class="{ err: b.isError }">
            <div class="filecard-head">
              <span class="filecard-ico ms">description</span>
              <span class="filecard-name" :title="b.summary || b.pathLink">{{ b.pathLink }}</span>
              <span class="filecard-stats">
                <span v-if="b.statAdd" class="stat add">{{ b.statAdd }}</span>
                <span v-if="b.statDel" class="stat del">{{ b.statDel }}</span>
              </span>
            </div>
            <div class="filecard-body" data-copy>
              <div v-if="b.diffLines?.length" class="diff">
                <div v-for="(d, j) in b.diffLines" :key="j" class="diff-line" :class="d.kind">
                  <span class="no">{{ d.lineNo }}</span>
                  <span v-if="hlLine(d.text, b.pathLink)" class="hl" v-html="hlLine(d.text, b.pathLink)" />
                  <span v-else>{{ d.text }}</span>
                </div>
              </div>
              <pre v-else class="detail-body">{{ b.detail }}</pre>
            </div>
          </div>
          <!-- 无 diff/详情/图片的空步骤不渲染详情壳,避免出现空黑框 -->
          <div v-else-if="canExpand(b)" class="tool-detail" data-copy>
            <div v-if="b.diffLines?.length" class="diff">
              <div v-for="(d, j) in b.diffLines" :key="j" class="diff-line" :class="d.kind">
                <span class="no">{{ d.lineNo }}</span>
                <span v-if="hlLine(d.text, b.pathLink)" class="hl" v-html="hlLine(d.text, b.pathLink)" />
                <span v-else>{{ d.text }}</span>
              </div>
            </div>
            <pre v-else-if="b.hasDetail" class="detail-body">{{ b.detail }}</pre>
          </div>
        </details>
      </template>
      <div v-if="detail?.status === 'running'" class="busy"><span class="ms spin">settings</span> 子代理正在工作中…</div>
      <div v-else-if="detail?.status === 'failed'" class="err">{{ detail.error }}</div>
      <div v-else-if="detail?.status === 'cancelled'" class="hint">已取消(主对话停止或 sub_kill)</div>
      <div v-if="detail?.framesTruncated" class="hint">较早的事件帧已超出缓存上限,以上仅含最近部分。</div>
    </div>
    <template #footer>
      <span class="grow" />
      <button class="ghost" @click="emit('close')">关闭</button>
    </template>
  </ModalDialog>
</template>

<style scoped>
/* 内容锚点:本身不滚动,滚动统一交给 ModalDialog 的 .mdl-body(避免双层滚动导致自动滚动失效) */
.prompt {
  padding: 8px 10px;
  margin-bottom: 10px;
  background: var(--bg-tool);
  border: 1px solid var(--border);
  border-left: 3px solid var(--accent-sub);
  border-radius: 6px;
  font-size: 13.2px;
  color: var(--fg-secondary);
  word-break: break-all;
}

/* ───── 以下与 MessageBubble 主窗体样式逐条对齐(思考/工具折叠行、文件卡、diff、Markdown) ───── */

.md {
  word-break: break-word;
  margin: 2px 0;
}

.md :deep(p) {
  margin: 6px 0;
}

.md :deep(pre) {
  background: var(--bg-code);
  border-radius: 6px;
  padding: 10px 12px;
  overflow-x: auto;
  font-size: 13.2px;
}

.md :deep(code) {
  background: var(--bg-code);
  border-radius: 4px;
  padding: 1px 5px;
  font-size: 13.2px;
}

.md :deep(pre code) {
  background: transparent;
  padding: 0;
}

.md :deep(a) {
  color: var(--fg-accent);
}

.md :deep(table) {
  border-collapse: collapse;
  margin: 8px 0;
}

.md :deep(th),
.md :deep(td) {
  border: 1px solid var(--border);
  padding: 4px 10px;
}

.md :deep(blockquote) {
  border-left: 3px solid var(--border);
  margin: 6px 0;
  padding: 2px 12px;
  color: var(--fg-secondary);
}

/* ───── 思考/工具折叠行:透明底、圆角4、悬停 Selection;正文左描边 ───── */

.seg {
  margin: 2px 0 2px;
}

.seg summary {
  display: flex;
  align-items: center;
  gap: 5px;
  cursor: pointer;
  border-radius: 4px;
  padding: 3px;
  color: var(--fg-secondary);
  user-select: none;
  list-style: none;
}

.seg summary::-webkit-details-marker {
  display: none;
}

.seg summary:hover {
  background: var(--selection);
}

.chev {
  font-size: 16.5px;
  color: var(--fg-secondary);
  flex: none;
  transition: transform 0.12s;
  display: inline-flex;
}

.seg[open] > summary .chev {
  transform: rotate(90deg);
}

.seg-header {
  flex: none;
}

.seg-preview {
  font-style: italic;
  margin-left: 10px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.seg-body {
  margin: 3px 0 0 12px;
  border-left: 2px solid var(--border);
  padding: 4px 10px;
  white-space: pre-wrap;
  color: var(--fg-secondary);
  font-size: 13.2px;
  line-height: 1.55;
  max-height: 280px;
  overflow-y: auto;
}

/* ───── 工具行 ───── */

.tool-icon {
  flex: none;
  color: var(--fg-secondary);
  font-size: 14.3px;
  width: 15px;
  text-align: center;
}

.tool-icon.spin {
  display: inline-flex;
  animation: spin 1.6s linear infinite;
  color: var(--accent-sub);
}

.tool-name {
  color: var(--fg-primary);
  flex: none;
}

/* 执行中的行首文字波浪渐变扫过,结束恢复常态;仅标签,详情不带动效 */
.tool-name.run,
.seg-header.run {
  background: linear-gradient(
    90deg,
    var(--fg-primary) 25%,
    var(--fg-muted) 50%,
    var(--fg-primary) 75%
  );
  background-size: 200% 100%;
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
  -webkit-text-fill-color: transparent;
  animation: run-shimmer 2.6s linear infinite;
}

@keyframes run-shimmer {
  from {
    background-position: 200% 0;
  }
  to {
    background-position: -200% 0;
  }
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

/* 文件名/摘要不伸展:后续统计紧贴其后(超长省略) */
.tool-summary {
  color: var(--fg-secondary);
  margin-left: 8px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 0 1 auto;
  min-width: 0;
}

.tool-result {
  flex: none;
  margin-left: auto;
  color: var(--fg-secondary);
}

.tool-result.err {
  color: var(--danger);
}

.stat.add {
  flex: none;
  margin-left: 6px;
  color: var(--success);
}

.stat.del {
  flex: none;
  margin-left: 6px;
  color: var(--danger);
}

.tool-detail {
  margin: 3px 0 0 12px;
  background: var(--bg-tool);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 7px 10px;
  max-height: 280px;
  overflow-y: auto;
}

.detail-body {
  white-space: pre-wrap;
  font-size: 13.2px;
  color: var(--fg-primary);
  margin: 0;
}

.diff {
  font-family: Consolas, monospace;
  font-size: 13.2px;
}

.diff-line {
  display: flex;
  white-space: pre-wrap;
  word-break: break-all;
  padding: 0 6px;
  font-size: 13.8px;
  line-height: 1.55;
  color: var(--code-fg);
  position: relative;
}

/* 增/删行:整行(含行号槽)染底色,左缘 3px 色条用定位伪元素绘制 */
.diff-line.add {
  background: var(--diff-add-bg);
}

.diff-line.add::before {
  content: '';
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: 3px;
  background: var(--diff-add-sgn);
}

.diff-line.del {
  background: var(--diff-del-bg);
}

.diff-line.del::before {
  content: '';
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: 3px;
  background: var(--diff-del-sgn);
}

/* 正文相对行号列的左边距 */
.diff-line > span:not(.no) {
  margin-left: 8px;
}

.diff-line .no {
  width: 42px;
  flex: none;
  color: var(--fg-muted);
  text-align: right;
  padding-right: 8px;
  user-select: none;
  background: var(--gutter-bg);
}

/* 行号槽随增/删行同色 */
.diff-line.add .no {
  background: var(--diff-add-gutter);
  color: var(--diff-add-sgn);
}

.diff-line.del .no {
  background: var(--diff-del-gutter);
  color: var(--diff-del-sgn);
}

/* ───── 文件卡片(写入/编辑/补丁:工具行展开区) ───── */

.filecard {
  margin: 4px 0 0;
  border: 1px solid var(--border);
  border-radius: 8px;
  overflow: hidden;
  background: var(--bg-tool);
}

.filecard.err {
  border-color: var(--danger);
}

.filecard-head {
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 6px 10px;
  background: var(--bg-panel-hover);
  border-bottom: 1px solid var(--border);
  font-size: 13.2px;
}

.filecard-ico {
  flex: none;
  font-size: 16.5px;
  color: var(--fg-secondary);
}

.filecard-name {
  flex: none;
  max-width: 50%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--fg-primary);
  font-weight: 600;
}

.filecard-stats {
  margin-left: auto;
  display: flex;
  gap: 8px;
  flex: none;
}

.filecard-body {
  padding: 0;
  max-height: 320px;
  overflow-y: auto;
}

.filecard .diff-line {
  padding: 0 10px 0 0;
}

.filecard .diff-line .no {
  width: 52px;
}

/* ───── 上下文压缩行(与主聊天 MessageBubble .msg.divider / .compressing 逐条同规格) ───── */

.compress-row {
  display: flex;
  align-items: center;
  gap: 12px;
  color: var(--fg-muted);
  font-size: 14.3px;
  white-space: nowrap;
  padding: 10px 18px;
}

/* 主 Agent 传话行:左侧主题色描边 + 图标,与思考/工具行区分一眼可辨 */
.tell-row {
  display: flex;
  align-items: baseline;
  gap: 6px;
  margin: 6px 0 6px 12px;
  padding: 4px 10px;
  border-left: 2px solid var(--accent-sub);
  border-radius: 0 4px 4px 0;
  background: var(--accent-sub-bg);
}

.tell-row .tell-ico {
  flex: none;
  font-size: 14.3px;
  color: var(--accent-sub);
  display: inline-flex;
}

.tell-row .tell-label {
  flex: none;
  font-size: 12.1px;
  font-weight: 600;
  color: var(--accent-sub);
  white-space: nowrap;
}

.tell-row .tell-text {
  color: var(--fg-primary);
  font-size: 13.2px;
  line-height: 1.5;
  word-break: break-word;
  white-space: pre-wrap;
}

.compress-row::before,
.compress-row::after {
  content: '';
  flex: 1;
  height: 1px;
  background: var(--border);
}

/* 压缩中:两侧横线比完成标记略亮(失败态与完成态同规格,只换文字颜色) */
.compress-row:not(.done):not(.failed)::before,
.compress-row:not(.done):not(.failed)::after {
  background: var(--fg-muted);
}

/* 压缩中文字:比完成标记更亮 + 波浪流光扫过(与"思考中"/执行中同款 run-shimmer) */
.compress-row .compressing-text {
  background: linear-gradient(
    90deg,
    var(--fg-primary) 25%,
    var(--fg-muted) 50%,
    var(--fg-primary) 75%
  );
  background-size: 200% 100%;
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
  -webkit-text-fill-color: transparent;
  animation: run-shimmer 2.6s linear infinite;
}

.compress-row .compressed-text {
  color: var(--fg-muted);
}

/* 压缩失败:与「会话已压缩」同色(原色,不用警示红) */
.compress-row .compress-failed-text {
  color: var(--fg-muted);
}

/* ───── 忙碌行/错误/提示 ───── */

.busy {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 6px;
  color: var(--fg-secondary);
  font-size: 12.1px;
}

.busy .ms.spin {
  animation: spin 1.6s linear infinite;
}

.err {
  color: var(--danger);
  font-size: 12.6px;
  padding: 6px 10px;
}

.hint {
  color: var(--fg-muted);
  font-size: 12.1px;
  margin-top: 6px;
}
</style>
