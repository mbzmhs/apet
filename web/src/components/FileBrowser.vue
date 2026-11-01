<script setup lang="ts">
// 服务端文件/文件夹浏览器:替代 WPF 的 OpenFileDialog / OpenFolderDialog。
// - 面包屑导航(此电脑 → 盘符 → 目录逐级可点)
// - 常用文件夹快捷入口(桌面/文档/下载/图片/音乐/视频,来自服务端"此电脑"列表)
// - 服务端记住上次浏览位置(无 initial 时恢复)
// - mode=dir 点"选择当前目录";mode=file 单击文件选定(exts 过滤显示)
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { fsList, getFsLast, type FsEntry } from '../api/client'

const props = defineProps<{ mode: 'dir' | 'file'; initial?: string; exts?: string[]; title?: string }>()
const emit = defineEmits<{ select: [path: string]; cancel: [] }>()

const path = ref('')
const parent = ref<string | null>(null)
const entries = ref<FsEntry[]>([])
const error = ref('')
const selected = ref('')

async function nav(p: string) {
  error.value = ''
  try {
    const r = await fsList(p)
    path.value = r.path
    parent.value = r.parent
    selected.value = ''
    // 目录永远显示(导航用);文件模式下文件按扩展名过滤
    entries.value = r.entries.filter((e) => {
      if (e.isDir) return true
      if (!props.exts || props.exts.length === 0) return true
      const lower = e.name.toLowerCase()
      return props.exts.some((x) => lower.endsWith(x.toLowerCase()))
    })
  } catch (e) {
    error.value = String((e as Error).message)
  }
}

/** 面包屑段:此电脑 → 盘符 → 逐级目录 */
const crumbs = computed(() => {
  if (!path.value) return [{ name: '此电脑', path: '' }]
  const segs = path.value.split(/[\\/]/).filter(Boolean)
  const list = [{ name: '此电脑', path: '' }]
  let acc = ''
  for (const seg of segs) {
    acc += seg + '\\'
    list.push({ name: seg.endsWith(':') ? seg : seg, path: acc })
  }
  return list
})

/** 常用文件夹(服务端"此电脑"列表给定的真实路径) */
const quickList = ref<FsEntry[]>([])

function clickEntry(e: FsEntry) {
  if (e.target) {
    nav(e.target) // 常用夹/盘符:直接进服务端给定的路径
    return
  }
  if (e.isDir) {
    const base = path.value.endsWith('\\') || path.value === '' || path.value.endsWith('/') ? path.value : path.value + '\\'
    nav(base + e.name)
    return
  }
  selected.value = joinPath(path.value, e.name)
  if (props.mode === 'file') emit('select', selected.value)
}

function selectDir() {
  emit('select', path.value)
}

function confirmSel() {
  if (selected.value) emit('select', selected.value)
  else selectDir()
}

function joinPath(dir: string, name: string) {
  const sep = dir.endsWith('\\') || dir.endsWith('/') ? '' : '\\'
  return dir + sep + name
}

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') emit('cancel')
}

// 同 ModalDialog:只有按下就落在遮罩上的点击才关闭,窗内拖出松手不误关
let downOnOverlay = false
function onOverlayDown(e: MouseEvent) {
  downOnOverlay = e.target === e.currentTarget
}
function onOverlayClick() {
  if (downOnOverlay) emit('cancel')
}

onMounted(async () => {
  document.addEventListener('keydown', onKey)
  if (props.initial) {
    nav(props.initial)
    return
  }
  try {
    const last = await getFsLast()
    if (last.path) {
      nav(last.path)
      return
    }
  } catch {
    // 无记忆位置:回落此电脑
  }
  nav('')
})

onUnmounted(() => document.removeEventListener('keydown', onKey))
</script>

<template>
  <!-- Teleport 到 body:与 ModalDialog/ActionDialog 同层竞争 z-index。此前留在页内时,
       .settings 的 isolation:isolate 把整个设置页封进独立层叠上下文(对外仅相当于
       z-index auto),插件设置等 Teleport 模态(2600)直接压住整个页面——
       文件选择器(z-2800)被锁在页内永远垫底,关掉模态才能看到它 -->
  <Teleport to="body">
    <div class="overlay" @mousedown="onOverlayDown" @click="onOverlayClick">
    <div class="dialog">
      <div class="head">
        <span>{{ title ?? (mode === 'dir' ? '选择文件夹' : '选择文件') }}</span>
        <button class="x" title="关闭(Esc)" @click="emit('cancel')"><span class="ms">close</span></button>
      </div>

      <!-- 面包屑 -->
      <div class="crumbs">
        <template v-for="(c, i) in crumbs" :key="c.path + i">
          <button class="crumb" :class="{ cur: i === crumbs.length - 1 }" @click="nav(c.path)">{{ c.name }}</button>
          <span v-if="i < crumbs.length - 1" class="crumb-sep">›</span>
        </template>
      </div>

      <!-- 常用位置 -->
      <div class="quick">
        <button class="chip" :class="{ cur: !path }" @click="nav('')"><span class="ms">computer</span> 此电脑</button>
        <button class="chip" v-for="q in quickList" :key="q.target" @click="nav(q.target!)">{{ q.name }}</button>
      </div>

      <div v-if="error" class="error">{{ error }}</div>

      <div class="entries">
        <button
          v-for="e in entries"
          :key="e.name"
          class="entry"
          :class="{ sel: selected && selected === joinPath(path, e.name) }"
          @click="clickEntry(e)"
        >
          <span class="ico ms">{{ e.isDir ? (e.target ? 'computer' : 'folder') : 'description' }}</span>
          <span class="name">{{ e.name }}</span>
          <span v-if="!e.isDir && e.size !== null" class="size">{{ e.size }}</span>
          <span v-if="mode === 'file' && !e.isDir && selected === joinPath(path, e.name)" class="pick"><span class="ms">check</span></span>
        </button>
        <div v-if="entries.length === 0" class="empty">(空目录)</div>
      </div>

      <div class="foot">
        <span class="hint">{{ selected || (mode === 'dir' ? '当前目录即所选' : '单击文件选定') }}</span>
        <span class="grow" />
        <button v-if="mode === 'dir'" class="primary" :disabled="!path" @click="selectDir">选择当前目录</button>
        <button v-if="mode === 'file'" class="primary" :disabled="!selected" @click="confirmSel">打开</button>
        <button class="ghost" @click="emit('cancel')">取消</button>
      </div>
    </div>
  </div>
  </Teleport>
</template>

<style scoped>
.overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.55);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 2800;
}

.dialog {
  width: min(720px, 92vw);
  height: min(560px, 84vh);
  background: var(--bg-panel);
  border: 1px solid var(--border);
  border-radius: 10px;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  box-shadow: 0 16px 48px rgba(0, 0, 0, 0.45);
}

.head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  border-bottom: 1px solid var(--border);
  font-size: 15.4px;
}

.x {
  background: transparent;
  border: none;
  color: var(--fg-secondary);
  cursor: pointer;
  display: inline-flex;
}

.x .ms {
  font-size: 17.6px;
}

/* 面包屑 */
.crumbs {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 9px 16px 0;
  flex-wrap: wrap;
}

.crumb {
  background: transparent;
  border: none;
  color: var(--fg-secondary);
  font-size: 13.2px;
  padding: 3px 7px;
  border-radius: 5px;
  cursor: pointer;
  max-width: 200px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.crumb:hover {
  background: var(--selection);
  color: var(--fg-primary);
}

.crumb.cur {
  color: var(--fg-primary);
  background: var(--selection);
}

.crumb-sep {
  color: var(--fg-muted);
  font-size: 12.1px;
}

/* 常用位置 */
.quick {
  display: flex;
  gap: 6px;
  padding: 8px 16px;
  flex-wrap: wrap;
}

.chip {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  background: var(--bg-input);
  border: 1px solid var(--border);
  color: var(--fg-primary);
  font-size: 12.1px;
  padding: 4px 10px;
  border-radius: 999px;
  cursor: pointer;
}

.chip .ms {
  font-size: 14.3px;
  color: var(--fg-secondary);
}

.chip:hover {
  border-color: var(--fg-accent);
}

.chip.cur {
  border-color: var(--fg-accent);
  color: var(--fg-accent);
}

.error {
  color: var(--danger);
  padding: 0 16px;
  font-size: 13.2px;
}

.entries {
  flex: 1;
  overflow-y: auto;
  padding: 6px 10px;
}

.entry {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  background: transparent;
  border: none;
  color: var(--fg-primary);
  text-align: left;
  padding: 7px 10px;
  border-radius: 6px;
  cursor: pointer;
  font-size: 14.3px;
}

.entry:hover {
  background: var(--bg-panel-hover);
}

.entry.sel {
  background: var(--selection);
}

.ico {
  flex: none;
  font-size: 16.5px;
  color: var(--fg-secondary);
  display: inline-flex;
}

.name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.size {
  color: var(--fg-muted);
  font-size: 12.1px;
}

.pick {
  color: var(--fg-accent);
  flex: none;
  display: inline-flex;
}

.pick .ms {
  font-size: 16.5px;
}

.empty {
  color: var(--fg-muted);
  text-align: center;
  padding: 24px;
  font-size: 13.2px;
}

.foot {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px 16px;
  border-top: 1px solid var(--border);
}

.foot .hint {
  color: var(--fg-muted);
  font-size: 12.1px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 320px;
}

.foot .grow {
  flex: 1;
}

.primary,
.ghost {
  border-radius: 6px;
  cursor: pointer;
  font-size: 13.2px;
  padding: 6px 12px;
}

.primary {
  background: var(--accent);
  border: none;
  color: #fff;
}

.primary:disabled {
  opacity: 0.5;
}

.ghost {
  background: var(--bg-input);
  border: 1px solid var(--border);
  color: var(--fg-primary);
}
</style>
