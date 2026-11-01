<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { getMessageSummary, imageUrl } from '../api/client'
import { highlightLine } from '../lib/highlight'
import { renderMarkdown } from '../lib/markdown'
import Lightbox from './Lightbox.vue'
import type { Message, MessageSummary, Step } from '../types'

const props = defineProps<{ message: Message; agentId: string; sessionId: string }>()
const emit = defineEmits<{
  auth: [step: any, allow: boolean, trust: boolean]
  /** sub_* 工具行点击:打开关联子代理运行的模态框(只读回看) */
  'open-sub-run': [runId: number]
}>()

const role = computed(() => props.message.role)

// ───────── 助手消息悬停详情:msgIds 回链原始消息聚合(时间/Token 用量/模型),首次悬停拉取 ─────────

const summary = ref<MessageSummary | null>(null)
let summaryInFlight = false
let summaryPoll: number | null = null

function fetchSummary() {
  const ids = props.message.msgIds
  if (!ids?.length || summaryInFlight) return
  summaryInFlight = true
  getMessageSummary(props.agentId, props.sessionId, ids)
    .then((s) => (summary.value = s))
    .catch(() => { /* 失败(旧会话无关联 / 流式中未落库)→ 不显示详情行,悬停期间轮询会重试 */ })
    .finally(() => (summaryInFlight = false))
}

function stopSummaryPoll() {
  if (summaryPoll !== null) window.clearInterval(summaryPoll)
  summaryPoll = null
}

function onMouseEnter() {
  fetchSummary()
  // 悬停期间每 2s 刷新:运作中的轮次数据陆续落库,行内实时跟进;
  // completedAt 出现即定型,停轮——后续悬停不再重复请求(旧会话数据不会变)
  if (summary.value?.completedAt || summaryPoll !== null) return
  summaryPoll = window.setInterval(() => {
    if (summary.value?.completedAt) return stopSummaryPoll()
    fetchSummary()
  }, 2000)
}

function onMouseLeave() {
  stopSummaryPoll()
}

onUnmounted(stopSummaryPoll)

/** 详情行时间:起始 → 结束(同日则结束时间省日期),本地时区;未结束只有起始 */
function fmtSummaryTime(iso: string | null, withDate: boolean): string {
  if (!iso) return ''
  const d = new Date(iso)
  const p = (n: number) => String(n).padStart(2, '0')
  const hm = `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
  return withDate ? `${p(d.getMonth() + 1)}-${p(d.getDate())} ${hm}` : hm
}
const summaryTimeText = computed(() => {
  const s = summary.value
  if (!s) return ''
  const start = fmtSummaryTime(s.time, true)
  if (!s.completedAt) return start // 未结束:只有起始
  const sameDay = s.time && s.completedAt &&
    new Date(s.time).toDateString() === new Date(s.completedAt).toDateString()
  return `${start} → ${fmtSummaryTime(s.completedAt, !sameDay)}`
})
const summaryTokenText = computed(() => (summary.value?.totalTokens ? summary.value.totalTokens.toLocaleString() : ''))
/** Token 悬停提示:输入/输出/缓存/思考分项 */
const summaryUsageTitle = computed(() => {
  const s = summary.value
  if (!s) return ''
  const parts = [`输入 ${s.promptTokens}`, `输出 ${s.completionTokens}`]
  if (s.cachedTokens) parts.push(`缓存 ${s.cachedTokens}`)
  if (s.reasoningTokens) parts.push(`思考 ${s.reasoningTokens}`)
  return parts.join(' · ')
})

// 中断标记:按分割标记样式展示(左右横线),文字去掉 ⏹ 前缀
const isInterrupt = computed(() =>
  props.message.role === 'status' && (props.message.text ?? '').replace(/^⏹\s*/, '').trim() === '已中断',
)
// 错误卡:已有警示图标,去掉落盘文本里历史的 ✗ 前缀
const errText = computed(() => (props.message.text ?? '').replace(/^✗\s*/, ''))
// 分叉标记:同"已中断"的分割线+文本样式(已从「…」分叉)
const isFork = computed(() =>
  props.message.role === 'status' && !props.message.isError && (props.message.text ?? '').includes('分叉'),
)
// 提示卡:非错误状态行(如"无法撤销")统一用中性黑白 info 卡片
// (错误状态 isError=true 走上方的红色错误卡);展示时去掉历史的 ⚠/⏳ 前缀符号
const isInfoNotice = computed(
  () => props.message.role === 'status' && !props.message.isError && !isInterrupt.value && !isFork.value,
)
const infoText = computed(() => (props.message.text ?? '').replace(/^[⚠⏳]\s*/, ''))
// 多行反馈(如 $skills 技能清单):在 info 卡内按 Markdown 块渲染
const isBlockNotice = computed(() => isInfoNotice.value && infoText.value.includes('\n'))

// ───────── 图片预览灯箱(下载/缩放/拖拽;Esc/点空白关闭) ─────────

const lightbox = ref('')
const lightboxName = ref('')

function openPreview(src: string, name = '') {
  lightbox.value = src
  lightboxName.value = name
}

/** 会话图片目录的文件按扩展名区分:位图显示缩略图,PDF/音视频显示文件卡 */
const IMAGE_EXTS = ['.png', '.jpg', '.jpeg', '.webp', '.gif', '.bmp']

function isImageFile(fileName: string) {
  const i = fileName.lastIndexOf('.')
  return i >= 0 && IMAGE_EXTS.includes(fileName.slice(i).toLowerCase())
}

function isMediaFile(fileName: string) {
  return !isImageFile(fileName)
}

/** 媒体文件(PDF/音视频)的图标 */
function mediaIcon(fileName: string) {
  const i = fileName.lastIndexOf('.')
  const ext = i >= 0 ? fileName.slice(i).toLowerCase() : ''
  if (ext === '.pdf') return 'picture_as_pdf'
  if (['.mp3', '.wav', '.ogg', '.m4a'].includes(ext)) return 'mic'
  if (['.mp4', '.webm', '.mov'].includes(ext)) return 'videocam'
  return 'description'
}

/** 媒体文件下载:拉 blob 存盘 */
async function downloadFile(src: string, name: string) {
  try {
    const res = await fetch(src)
    const a = document.createElement('a')
    a.href = URL.createObjectURL(await res.blob())
    a.download = name
    a.click()
    URL.revokeObjectURL(a.href)
  } catch {
    // 静默:可右键另存
  }
}

function isThinking(s: Step): s is Extract<Step, { kind: 'thinking' }> {
  return s.kind === 'thinking'
}

/** 文件卡片:带文件名且可展开(有 diff 或详情)的工具行,展开区按 IDE 风格 diff 卡渲染 */
function isFileCard(s: Step): boolean {
  const t = s as any
  return t?.kind === 'tool' && !!t.pathLink && (!!t.diffLines?.length || !!t.hasDetail)
}

/** 路径行:有文件名的工具行(写入/编辑/补丁/读取/查看图片)——折叠行统一走图标+双色路径渲染 */
function isPathRow(s: Step): boolean {
  const t = s as any
  return t?.kind === 'tool' && !!t.pathLink
}

/** 文件工具行路径拆分:文件名(亮)+ 目录(暗)。summary 为显示路径(工作目录下=相对,其余=全),
 * pathLink = 文件名;summary 以文件名结尾时其余部分即目录,否则按最后一个斜杠兜底 */
function pathParts(s: Step): { name: string; dir: string } {
  const sum = String((s as any).summary ?? '')
  const name = String((s as any).pathLink ?? '')
  if (name && sum.endsWith(name)) return { name, dir: sum.slice(0, sum.length - name.length) }
  const i = sum.lastIndexOf('/')
  return i >= 0 ? { name: sum.slice(i + 1), dir: sum.slice(0, i + 1) } : { name: sum, dir: '' }
}

/** 工具行是否可展开(有详情或有 diff) */
function canExpand(s: Step): boolean {
  const t = s as any
  return !!t?.hasDetail || !!t?.diffLines?.length || !!t?.imageFiles?.length
}

/** diff 单行语法高亮(按文件扩展名;不支持返回 null 走纯文本) */
function hlLine(line: string, fileName: string): string | null {
  return highlightLine(line, fileName ?? '')
}

// 折叠行展开状态按步骤序号记忆(流式更新不打断用户手动展开)
const openRows = ref<Set<number>>(new Set())

function toggleRow(i: number) {
  const s = openRows.value
  s.has(i) ? s.delete(i) : s.add(i)
  openRows.value = new Set(s)
}

function isOpen(i: number) {
  return openRows.value.has(i)
}

/** 折叠头部预览 = 最新一行思考内容(超长取尾部 200 字符,同桌面端 ThinkingSegmentViewModel.Preview) */
function preview(s: Extract<Step, { kind: 'thinking' }>) {
  const trimmed = (s.text || '').replace(/[\r\n]+$/, '')
  if (!trimmed) return ''
  const nl = Math.max(trimmed.lastIndexOf('\n'), trimmed.lastIndexOf('\r'))
  const line = (nl < 0 ? trimmed : trimmed.slice(nl + 1)).trim()
  return line.length > 200 ? line.slice(-200) : line
}

// 思考正文跟随:打开时内容增长自动滚到最新;用户上滚即暂停跟随(ChainingScrollViewer 语义)
const bodyEls = new Map<number, HTMLElement>()
const bodyPinned = new Map<number, boolean>()

function noteBody(i: number, el: any) {
  if (el) bodyEls.set(i, el as HTMLElement)
  else bodyEls.delete(i)
}

function onBodyScroll(i: number, e: Event) {
  const el = e.target as HTMLElement
  bodyPinned.set(i, el.scrollHeight - el.scrollTop - el.clientHeight < 40)
}

watch(
  () => props.message,
  () => {
    for (const [i, el] of bodyEls) {
      if (!isOpen(i) || bodyPinned.get(i) === false) continue
      el.scrollTop = el.scrollHeight
    }
  },
  // flush post:默认 pre 在 DOM 更新前读 scrollHeight,读到的是旧值——最后一帧的增量
  // 跟不上,流结束后思考正文停在离底部一帧的位置(看似被截断)
  { deep: true, flush: 'post' },
)

// markdown 渲染两层设防:流式期间同一正文段每个增量都会重求 v-html,全量 marked+DOMPurify
// 对增长文本是 O(n²);①同文本命中缓存直接复用(深 watcher/MutationObserver 触发的重复求值);
// ②流式前缀延长链 120ms 内的增量帧复用上次 HTML,下一增量到达即补上(收尾帧必渲染,内容无损)
const mdCache = new Map<string, string>()
let mdChainText = ''
let mdChainHtml = ''
let mdChainAt = 0

function html(text: string) {
  const hit = mdCache.get(text)
  if (hit !== undefined) return hit
  if (text.length > mdChainText.length && text.startsWith(mdChainText) && Date.now() - mdChainAt < 120)
    return mdChainHtml
  const out = renderMarkdown(text)
  if (mdCache.size > 400) mdCache.clear() // 粗略上限:长会话翻页不无限累积
  mdCache.set(text, out)
  mdChainText = text
  mdChainHtml = out
  mdChainAt = Date.now()
  return out
}

function imgSrc(file: string) {
  return imageUrl(props.agentId, props.sessionId, file)
}
</script>

<template>
  <!-- 用户消息:右对齐不透明气泡(BgBubbleUser 圆角10,最大 540;图片/文件卡在正文之前) -->
  <div v-if="role === 'user'" class="msg user-row">
    <div class="user-bubble">
      <div v-if="(message.imageFiles?.length || message.clientDataUrls?.length)" class="thumbs">
        <template v-for="f in message.imageFiles" :key="f">
          <img
            v-if="isImageFile(f)"
            :src="imgSrc(f)"
            class="thumb-img"
            @click="openPreview(imgSrc(f), f)"
          />
        </template>
        <img
          v-for="(u, i) in message.clientDataUrls ?? []"
          :key="'u' + i"
          :src="u"
          class="thumb-img"
          @click="openPreview(u)"
        />
      </div>
      <div v-if="(message.imageFiles ?? []).some(isMediaFile)" class="files">
        <div v-for="f in message.imageFiles!.filter(isMediaFile)" :key="'m' + f" class="file">
          <span class="ms">{{ mediaIcon(f) }}</span>
          {{ f }}
          <span class="file-dl" title="下载" @click="downloadFile(imgSrc(f), f)"><span class="ms">download</span></span>
        </div>
      </div>
      <div v-if="message.textFiles?.length" class="files">
        <div v-for="f in message.textFiles" :key="f.name" class="file"><span class="ms">description</span> {{ f.name }}<span v-if="f.sizeText">({{ f.sizeText }})</span></div>
      </div>
      <div class="user-text" data-copy>{{ message.text }}</div>
      <div class="time right">{{ message.timeText }}</div>
    </div>
  </div>

  <!-- 中断标记:同分割标记样式,去掉 ⏹ 符号 -->
  <div v-else-if="isInterrupt" class="msg divider">已中断</div>

  <!-- 分叉标记:同分割线+文本样式 -->
  <div v-else-if="isFork" class="msg divider" data-copy>{{ infoText }}</div>

  <!-- 错误状态:红字告警卡(图标 + 全文);不显示时间 -->
  <div v-else-if="role === 'status' && message.isError" class="msg status-err" data-copy>
    <span class="ms err-ic">warning</span>
    <span class="err-tx">{{ errText }}</span>
  </div>

  <!-- 多行反馈(如 $skills 技能清单):不用提示卡,直接 Markdown 块(表格/列表),看得清 -->
  <div v-else-if="isBlockNotice" class="msg notice-block" data-copy>
    <div class="md" v-html="html(infoText)" />
  </div>

  <!-- 提示卡:非错误状态行(无法撤销/分叉等),中性黑白 info 卡;不显示时间 -->
  <div v-else-if="isInfoNotice" class="msg info-card" data-copy>
    <span class="ms info-ic">info</span>
    <span class="info-tx">{{ infoText }}</span>
  </div>

  <!-- 状态行:居中弱化小字;不显示时间 -->
  <div v-else-if="role === 'status'" class="msg status" data-copy>
    {{ message.text }}
  </div>

  <!-- 压缩完成标记:两侧横线+中间文字(同"已中断"样式) -->
  <div v-else-if="role === 'divider'" class="msg divider" data-copy>{{ message.text || '会话已压缩' }}</div>

  <!-- 压缩进行中:两侧横线+中间文字(临时条目,压缩结束即从快照消失);横线与文字比完成标记更亮,文字带流光扫过(同"思考中") -->
  <div v-else-if="role === 'spinner'" class="msg divider compressing" data-copy>
    <span class="compressing-text">{{ message.text || '上下文压缩中' }}</span>
  </div>

  <!-- 助手消息:无气泡,全宽步骤流(正文分段/思考行/工具行/鉴权卡按发生顺序交错) -->
  <div v-else-if="role === 'assistant'" class="msg assistant-block" @mouseenter="onMouseEnter" @mouseleave="onMouseLeave">
    <template v-for="(s, i) in message.steps ?? []" :key="i">
      <div v-if="s.kind === 'content'" class="md" data-copy v-html="html((s as any).text)" />
      <!-- 思考行:悬停高亮的折叠行(默认收起,同桌面端);正文左描边、灰字、限高滚动 -->
      <details v-else-if="isThinking(s)" class="seg" :open="isOpen(i)" @toggle="isOpen(i) !== ($event.target as HTMLDetailsElement).open && toggleRow(i)">
        <summary>
          <span class="chev ms">arrow_right</span>
          <span class="seg-header" :class="{ run: s.isActive }">{{ s.header }}</span>
          <span class="seg-preview">{{ preview(s as any) }}</span>
        </summary>
        <div class="seg-body" data-copy :ref="el => noteBody(i, el)" @scroll="onBodyScroll(i, $event)">{{ s.text }}</div>
      </details>
      <!-- 工具行:齿轮(运行中旋转/完成√)+ 名称 + 摘要 + 结果摘要(错红)+ 行数统计;
           文件类工具(写入/编辑/补丁)展开为 IDE 风格 diff 卡片,其余展开为详情 -->
      <details
        v-else-if="s.kind === 'tool'"
        class="seg tool"
        :class="{ filetool: isFileCard(s) }"
        :open="isOpen(i)"
        @toggle="($event.target as HTMLDetailsElement).open !== isOpen(i) && toggleRow(i)"
      >
        <summary @click="!canExpand(s) && $event.preventDefault()">
          <span class="tool-icon ms" :class="{ spin: (s as any).isRunning, fill: (s as any).isRunning }">{{ (s as any).isRunning ? 'settings' : 'check' }}</span>
          <span class="tool-name" :class="{ run: (s as any).isRunning, skill: (s as any).toolName === 'skill', mcp: String((s as any).toolName || '').startsWith('mcp_'), sub: String((s as any).toolName || '').startsWith('sub_') }">{{ (s as any).displayName }}</span>
          <span class="tool-summary">
            <!-- 路径行(写入/编辑/读取等):图标+文件名一组,目录弱色隔开;其余工具保持原样 -->
            <template v-if="isPathRow(s)"><span class="filecard-ico ms">description</span><span class="pname">{{ pathParts(s).name }}</span><span v-if="pathParts(s).dir" class="pdir">{{ pathParts(s).dir }}</span></template>
            <template v-else>{{ (s as any).summary }}</template>
          </span>
           <!-- 子代理行:点击打开该运行的模态框(运行 id 由后端从结果/参数提取;入口标题优先显示任务名称) -->
           <span
             v-if="(s as any).subRunId != null"
             class="sub-link"
             title="查看子代理运行"
             @click.stop.prevent="emit('open-sub-run', (s as any).subRunId)"
           >{{ (s as any).subRunTitle || '运行 #' + (s as any).subRunId }} ▸</span>
          <!-- 行数统计紧跟文件名(卡片头部内的统计保持原位置) -->
          <span v-if="(s as any).statAdd" class="stat add">{{ (s as any).statAdd }}</span>
          <span v-if="(s as any).statDel" class="stat del">{{ (s as any).statDel }}</span>
          <span v-if="!(s as any).isRunning" class="tool-result" :class="{ err: (s as any).isError }">{{ (s as any).resultSummary }}</span>
          <span v-if="canExpand(s)" class="chev ms">arrow_right</span>
        </summary>
        <!-- 文件卡片:头部文件图标+文件名(亮)+目录(暗)+行数统计,正文带行号红绿 diff + 按扩展名语法高亮 -->
        <div v-if="isFileCard(s)" class="filecard" :class="{ err: (s as any).isError }">
          <div class="filecard-head">
            <span class="filecard-ico ms">description</span>
            <span class="filecard-name" :title="(s as any).summary || (s as any).pathLink">{{ pathParts(s).name }}<span v-if="pathParts(s).dir" class="pdir">{{ pathParts(s).dir }}</span></span>
            <span class="filecard-stats">
              <span v-if="(s as any).statAdd" class="stat add">{{ (s as any).statAdd }}</span>
              <span v-if="(s as any).statDel" class="stat del">{{ (s as any).statDel }}</span>
            </span>
          </div>
          <div class="filecard-body" data-copy :ref="el => noteBody(i, el)" @scroll="onBodyScroll(i, $event)">
            <div v-if="(s as any).diffLines?.length" class="diff">
              <div v-for="(d, j) in (s as any).diffLines" :key="j" class="diff-line" :class="d.kind">
                <span class="no">{{ d.lineNo }}</span>
                <span v-if="hlLine(d.text, (s as any).pathLink)" class="hl" v-html="hlLine(d.text, (s as any).pathLink)" />
                <span v-else>{{ d.text }}</span>
              </div>
            </div>
            <pre v-else-if="(s as any).detail" class="detail-body">{{ (s as any).detail }}</pre>
            <div v-if="(s as any).imageFiles?.length" class="tool-images">
              <img
                v-for="f in (s as any).imageFiles"
                :key="f"
                :src="imgSrc(f)"
                class="tool-img"
                @click="openPreview(imgSrc(f), f)"
              />
            </div>
          </div>
        </div>
        <!-- 无 diff/详情/图片的空步骤不渲染详情壳,避免出现空黑框;
             详情体挂跟随滚动:运行中工具的输出经 tool_output 增量续接,展开时自动跟到最新 -->
        <div v-else-if="canExpand(s)" class="tool-detail" data-copy :ref="el => noteBody(i, el)" @scroll="onBodyScroll(i, $event)">
          <div v-if="(s as any).diffLines?.length" class="diff">
            <div v-for="(d, j) in (s as any).diffLines" :key="j" class="diff-line" :class="d.kind">
              <span class="no">{{ d.lineNo }}</span>
              <span v-if="hlLine(d.text, (s as any).pathLink)" class="hl" v-html="hlLine(d.text, (s as any).pathLink)" />
              <span v-else>{{ d.text }}</span>
            </div>
          </div>
          <pre v-else-if="(s as any).hasDetail" class="detail-body">{{ (s as any).detail }}</pre>
          <div v-if="(s as any).imageFiles?.length" class="tool-images">
            <img
              v-for="f in (s as any).imageFiles"
              :key="f"
              :src="imgSrc(f)"
              class="tool-img"
              @click="openPreview(imgSrc(f), f)"
            />
          </div>
        </div>
      </details>
      <!-- 鉴权卡:未决 = 卡片 + 按钮;拒绝 = 弱化结果行;允许后整卡隐藏(同桌面端) -->
      <div v-else-if="s.kind === 'auth' && !((s as any).decided && (s as any).granted)" class="auth" :class="{ decided: (s as any).decided }">
        <template v-if="!(s as any).decided">
          <div class="auth-head"><span class="auth-shield ms">gpp_maybe</span><span class="auth-title">权限确认</span></div>
      <div class="auth-detail" data-copy>{{ (s as any).detail }}</div>
          <div class="auth-actions">
            <button class="ghost" @click="emit('auth', s, true, false)">允许</button>
            <button v-if="(s as any).trustDirs?.length" class="ghost" @click="emit('auth', s, true, true)">信任此目录</button>
            <button class="ghost" @click="emit('auth', s, false, false)">拒绝</button>
          </div>
        </template>
        <div v-else class="auth-result">{{ (s as any).resultText ?? '已拒绝本次操作' }}</div>
      </div>
    </template>
    <!-- 悬停详情行:msgIds 回链原始消息聚合(生成时间/Token 用量/所用模型);首次悬停拉取,移出即隐 -->
    <div v-if="summary" class="meta">
      <span class="ms">schedule</span><span>{{ summaryTimeText }}</span>
      <span v-if="summary.totalTokens" class="ms">paid</span><span :title="summaryUsageTitle">{{ summaryTokenText }} tokens</span>
      <span v-if="summary.model" class="ms">smart_toy</span><span :title="summary.provider ?? ''">{{ summary.model }}</span>
    </div>
  </div>

  <!-- 图片预览灯箱(下载/缩放/拖拽) -->
  <Lightbox v-if="lightbox" :src="lightbox" :name="lightboxName" @close="lightbox = ''" />
</template>

<style scoped>
/* 消息行:边距 18,12 同桌面端;助手块全宽左对齐(最大 860),无气泡底 */
/* 消息行占满居中列(列由 .messages 的 align-items:center 居中,最大 1056);
   flex: none 必须——.messages 是滚动 flex 容器,默认 shrink:1 会在内容超出时压缩所有项
   (分割线被压成高度 0、长列表无法滚动) */
.msg {
  flex: none;
  width: 100%;
  max-width: 1056px;
  padding: 12px 18px;
}

.assistant-block {
  position: relative; /* 悬停详情行的定位锚 */
  max-width: 1056px;
  padding-bottom: 28px; /* 原时间行位置改为空预留带:悬停详情行叠在这里,高度与原时间行相当 */
}

/* ───── 用户气泡:BgBubbleUser,圆角10,右对齐,最大 540 ───── */

.user-row {
  display: flex;
  justify-content: flex-end;
}

.user-bubble {
  background: var(--bg-bubble-user);
  border-radius: 10px;
  padding: 10px 8px 8px 10px;
  max-width: min(540px, 86%);
  min-width: 0;
}

.user-text {
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 14.3px;
  line-height: 1.55;
}

.thumbs {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-bottom: 6px;
}

.thumb-img {
  width: 72px;
  height: 72px;
  object-fit: cover;
  border-radius: 8px;
  border: 1px solid var(--border);
  cursor: pointer;
  background: var(--bg-code);
}

.files {
  margin-bottom: 6px;
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.file {
  background: rgba(0, 0, 0, 0.18);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 7px 10px;
  font-size: 13.2px;
}

.file .ms {
  font-size: 13.2px;
  color: var(--fg-secondary);
}

.file-dl {
  margin-left: 6px;
  color: var(--fg-accent);
  cursor: pointer;
  display: inline-flex;
}

.file-dl:hover {
  text-decoration: underline;
}

.file-dl .ms {
  font-size: 14.3px;
}

/* 时间戳:10px 次要色;助手/状态靠左,用户靠右 */
.time {
  margin-top: 2px;
  font-size: 11px;
  color: var(--fg-secondary);
}

.time.right {
  text-align: right;
  margin-right: 2px;
}

/* 悬停详情行:绝对定位叠在块底预留带(原时间行位置,见 .assistant-block 的 padding-bottom),
   不占流内空间 —— 零布局位移,不推动后续消息;
   显隐走 opacity 过渡(淡入淡出),visibility 延迟切换保证淡出完成后才隐藏 */
.assistant-block .meta {
  display: flex;
  position: absolute;
  left: 0;
  right: 0;
  bottom: 7px;
  gap: 4px;
  align-items: center;
  white-space: nowrap;
  font-size: 11px;
  color: var(--fg-secondary);
  opacity: 0;
  visibility: hidden;
  transition: opacity 160ms ease, visibility 0s linear 160ms;
}
.assistant-block:hover .meta {
  opacity: 1;
  visibility: visible;
  transition: opacity 160ms ease;
}
.assistant-block .meta .ms + span {
  margin-right: 10px;
}
.assistant-block .meta .ms {
  font-size: 13px;
  color: var(--fg-muted);
}

/* ───── 状态行 / 分割线 ───── */

.status {
  text-align: center;
  color: var(--fg-muted);
  font-size: 12.1px;
  padding: 2px 18px;
  max-width: 720px;
  margin: 0 auto;
}

/* 错误告警卡:淡红底 + 红描边圆角卡,警示图标开头,错误全文展示 */
.status-err {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  max-width: 720px;
  margin: 6px auto;
  padding: 10px 14px;
  background: var(--danger-bg);
  border: 1px solid var(--danger-border);
  border-radius: 10px;
  color: var(--danger);
  font-size: 13.2px;
  line-height: 1.5;
}

.status-err .err-ic {
  flex: none;
  font-size: 18px;
  margin-top: 1px;
}

.status-err .err-tx {
  min-width: 0;
  white-space: pre-wrap;
  word-break: break-word;
}

/* 提示卡:⚠/⏳ 类提示的中性呈现(面板底 + 常规描边,黑白灰,不用红色) */
.info-card {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  max-width: 720px;
  margin: 6px auto;
  padding: 10px 14px;
  background: var(--bg-panel);
  border: 1px solid var(--border);
  border-radius: 10px;
  color: var(--fg-primary);
  font-size: 13.2px;
  line-height: 1.5;
}

.info-card .info-ic {
  flex: none;
  font-size: 18px;
  margin-top: 1px;
  color: var(--fg-muted);
}

.info-card .info-tx {
  min-width: 0;
  white-space: pre-wrap;
  word-break: break-word;
}

/* 多行 Markdown 反馈($skills 表格等):无卡片块,宽度沿用 .msg 的 1056px 聊天框架;超宽表格横向滚动 */
.notice-block {
  padding: 6px 18px;
  font-size: 13.2px;
  line-height: 1.5;
}
.notice-block .md {
  overflow-x: auto;
}
.notice-block .md :deep(p) {
  margin: 4px 0;
}

/* 分割标记:居中弱化小字,左右横线撑满整行 */
.divider {
  display: flex;
  align-items: center;
  gap: 12px;
  color: var(--fg-muted);
  font-size: 14.3px;
  white-space: nowrap;
  padding: 10px 18px;
}
.divider::before,
.divider::after {
  content: '';
  flex: 1;
  height: 1px;
  background: var(--border);
}

/* 上下文压缩中行:两侧横线比完成标记略亮 */
.msg.divider.compressing::before,
.msg.divider.compressing::after {
  background: var(--fg-muted);
}

/* 压缩中文字:比完成标记更亮 + 波浪流光扫过(与"思考中"/执行中同款 run-shimmer) */
.compressing-text {
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
  /* 执行行(思考/工具/终端)与正文同号:跟随根字号,不再单独缩小 */
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
  color: var(--fg-dim); /* 思考标签暗灰,与工具名同档 */
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
}

.tool-name {
  color: var(--fg-dim); /* 完成态工具名暗灰(写入文件/编辑文件等);运行中被 run 流光覆盖 */
  flex: none;
}

/* skill/MCP 工具标题着色,与内置工具区分一眼可辨 */
.tool-name.skill {
  color: var(--warning);
}

.tool-name.mcp {
  color: var(--accent);
}

/* 子代理工具标题青色,与 skill(琥珀)/MCP(蓝)区分 */
.tool-name.sub {
  color: var(--accent-sub);
}

/* 执行中的 shimmer 用各自主题色(默认 run 渐变基于 --fg-primary 会盖掉上面的颜色) */
.tool-name.skill.run {
  background-image: linear-gradient(90deg, var(--warning) 25%, var(--fg-muted) 50%, var(--warning) 75%);
}

.tool-name.mcp.run {
  background-image: linear-gradient(90deg, var(--accent) 25%, var(--fg-muted) 50%, var(--accent) 75%);
}

.tool-name.sub.run {
  background-image: linear-gradient(90deg, var(--accent-sub) 25%, var(--fg-muted) 50%, var(--accent-sub) 75%);
}

/* 执行中的行首文字(思考/写入文件/编辑文件/运行终端…)波浪渐变扫过,结束恢复常态;仅标签,详情不带动效 */
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

/* 文件工具行路径双色:文件名亮、目录弱(折叠行与文件卡头部同规) */
.tool-summary .pname {
  /* 文件名:比次要色亮、比正文柔(主色 72% 混次要色,随主题自适应) */
  color: color-mix(in srgb, var(--fg-primary) 72%, var(--fg-secondary));
}

.tool-summary .filecard-ico {
  margin-right: 4px; /* 图标与文件名成组 */
}

.pdir {
  color: var(--fg-muted);
  font-weight: 400; /* 卡片头文件名是 600 加重,目录段回常规字重 */
  margin-left: 6px; /* 文件名与路径之间留空隙 */
}

/* 子代理行的「运行 #N」可点击入口(青色,悬停提亮) */
.sub-link {
  flex: none;
  margin-left: 8px;
  padding: 0 6px;
  border-radius: 4px;
  background: var(--accent-sub-bg);
  color: var(--accent-sub);
  font-size: 12.1px;
  cursor: pointer;
  user-select: none;
}

.sub-link:hover {
  filter: brightness(1.25);
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

.tool-images {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.tool-img {
  /* 固定 180×180 卡片 + contain:窄长/超宽/过小的图不拉伸变形,小图居中,黑底兜底 */
  width: 180px;
  height: 180px;
  object-fit: contain;
  border-radius: 8px;
  border: 1px solid var(--border);
  cursor: pointer;
  background: #000;
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

/* 增/删行:整行(含行号槽)染底色,左缘 3px 色条用定位伪元素绘制
   (inset 阴影会被行号槽背景盖住,定位伪元素绘于子元素之上) */
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

/* 行号槽随增/删行同色(底色延伸到槽位,行号文字染绿/红,仿例图) */
.diff-line.add .no {
  background: var(--diff-add-gutter);
  color: var(--diff-add-sgn);
}

.diff-line.del .no {
  background: var(--diff-del-gutter);
  color: var(--diff-del-sgn);
}

/* ───── 文件卡片(写入/编辑/补丁:工具行展开区;头部文件图标+文件名+行数统计,正文带行号 diff) ───── */

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



/* ───── 鉴权卡:BgPanel 圆角8;决定后透明化(允许=整卡隐藏,拒绝=弱化结果行) ───── */

.auth {
  background: var(--bg-panel);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 10px 12px;
  margin: 6px 0 0;
}

.auth.decided {
  background: transparent;
  border: none;
  padding: 0;
  margin: 4px 0 0;
}

.auth-head {
  display: flex;
  align-items: center;
  gap: 7px;
}

.auth-shield {
  color: var(--danger);
  font-size: 16.5px;
}

.auth-title {
  font-weight: 600;
  font-size: 14.3px;
}

.auth-detail {
  white-space: pre-wrap;
  font-size: 13.2px;
  margin-top: 6px;
  color: var(--fg-primary);
}

.auth-actions {
  display: flex;
  gap: 8px;
  margin-top: 10px;
}

.auth-result {
  color: var(--fg-muted);
  font-size: 12.1px;
}

.thumb-img,
.tool-img {
  cursor: zoom-in;
}

/* ───── Markdown 正文(自定样式,不跟随桌面端) ───── */

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
</style>

<!-- diff 行内代码高亮配色:hljs 输出经 v-html 注入,不带 scoped 的 data-v 属性,
     必须用全局(非 scoped)样式才能命中 -->
<style>
.hl {
  white-space: inherit;
}

/* 低饱和" dusty "色板:贴近例图的近单色观感,各 token 仅微微偏色(暗色值) */
.hl .hljs-keyword,
.hl .hljs-selector-tag,
.hl .hljs-literal,
.hl .hljs-operator,
.hl .hljs-tag,
.hl .hljs-name {
  color: #9d9dc9;
}

.hl .hljs-string,
.hl .hljs-attr,
.hl .hljs-attribute,
.hl .hljs-selector-attr {
  color: #8fae8b;
}

.hl .hljs-comment,
.hl .hljs-doctag {
  color: #676771;
  font-style: italic;
}

.hl .hljs-number,
.hl .hljs-built_in,
.hl .hljs-type,
.hl .hljs-class .hljs-title {
  color: #c2a878;
}

.hl .hljs-title,
.hl .hljs-section,
.hl .hljs-property,
.hl .hljs-variable {
  color: #c9cdd8;
}

.hl .hljs-regexp,
.hl .hljs-symbol {
  color: #c08497;
}

/* 亮色主题下的同构色板 */
[data-theme='light'] .hl .hljs-keyword,
[data-theme='light'] .hl .hljs-selector-tag,
[data-theme='light'] .hl .hljs-literal,
[data-theme='light'] .hl .hljs-operator,
[data-theme='light'] .hl .hljs-tag,
[data-theme='light'] .hl .hljs-name {
  color: #6d6db8;
}

[data-theme='light'] .hl .hljs-string,
[data-theme='light'] .hl .hljs-attr,
[data-theme='light'] .hl .hljs-attribute,
[data-theme='light'] .hl .hljs-selector-attr {
  color: #4c8a50;
}

[data-theme='light'] .hl .hljs-comment,
[data-theme='light'] .hl .hljs-doctag {
  color: #9a9aa4;
}

[data-theme='light'] .hl .hljs-number,
[data-theme='light'] .hl .hljs-built_in,
[data-theme='light'] .hl .hljs-type,
[data-theme='light'] .hl .hljs-class .hljs-title {
  color: #a07d2a;
}

[data-theme='light'] .hl .hljs-title,
[data-theme='light'] .hl .hljs-section,
[data-theme='light'] .hl .hljs-property,
[data-theme='light'] .hl .hljs-variable {
  color: #3a3a42;
}

[data-theme='light'] .hl .hljs-regexp,
[data-theme='light'] .hl .hljs-symbol {
  color: #b0596e;
}
</style>
