<script setup lang="ts">
// 图片预览灯箱:应用内弹出(Esc/点空白关闭),带 下载 / 缩放(按钮+滚轮)/ 拖拽平移。
// MessageBubble(消息图片)与 ChatView(未发送附件预览)共用。
import { onMounted, onUnmounted, ref, watch } from 'vue'

const props = defineProps<{ src: string; name?: string }>()
const emit = defineEmits<{ close: [] }>()

const scale = ref(1)
const tx = ref(0)
const ty = ref(0)
const dragging = ref(false)
const MAX = 8

/** 图片在缩放 1:1 时的适配尺寸(stage 内 contain);缩放直接改布局尺寸,避免 transform 只放大视觉被截断 */
const fit = ref({ w: 0, h: 0 })
const stageEl = ref<HTMLElement | null>(null)
const imgEl = ref<HTMLImageElement | null>(null)

function measure() {
  const img = imgEl.value
  const stage = stageEl.value
  if (!img || !stage || !img.naturalWidth) return
  const sw = stage.clientWidth
  const sh = stage.clientHeight
  const k = Math.min(sw / img.naturalWidth, sh / img.naturalHeight)
  fit.value = { w: Math.max(1, Math.floor(img.naturalWidth * k)), h: Math.max(1, Math.floor(img.naturalHeight * k)) }
}

function onImgLoad() {
  measure()
}

function zoom(delta: number) {
  scale.value = Math.min(MAX, Math.max(1, Math.round(scale.value * delta * 100) / 100))
  if (scale.value === 1) {
    tx.value = 0
    ty.value = 0
  }
}

function reset() {
  scale.value = 1
  tx.value = 0
  ty.value = 0
}

function onWheel(e: WheelEvent) {
  e.preventDefault()
  zoom(e.deltaY < 0 ? 1.2 : 1 / 1.2)
}

// 按住拖动平移(放大后查看局部)。
// 自由平移、不做边界钳制:窄图/超宽图放大后某一方向不超出窗口时,钳制会让该方向完全拖不动;
// 拖丢了按 1:1 重新回中。监听挂 window 上,拖出图片/窗口外也不中断。
let px = 0
let py = 0
function onWinMove(e: MouseEvent) {
  if (!dragging.value) return
  tx.value = e.clientX - px
  ty.value = e.clientY - py
}

function onWinUp() {
  dragging.value = false
  window.removeEventListener('mousemove', onWinMove)
  window.removeEventListener('mouseup', onWinUp)
}

function onGrabDown(e: MouseEvent) {
  if (scale.value <= 1) return
  e.preventDefault()
  dragging.value = true
  px = e.clientX - tx.value
  py = e.clientY - ty.value
  window.addEventListener('mousemove', onWinMove)
  window.addEventListener('mouseup', onWinUp)
}

/** 下载:拉取 blob 存盘(文件名取显示名或 URL 末段) */
async function download() {
  try {
    const res = await fetch(props.src)
    const blob = await res.blob()
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = props.name || decodeURIComponent(props.src.split('/').pop() || 'image.png')
    a.click()
    URL.revokeObjectURL(a.href)
  } catch {
    // 下载失败静默(灯箱仍可右键另存)
  }
}

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') emit('close')
}

// 只有"按下就落在遮罩空白处"的点击才关闭:从图片/工具条里拖到窗外松手会误触发关闭
let downOnOverlay = false
function onOverlayDown(e: MouseEvent) {
  downOnOverlay = e.target === e.currentTarget
}
function onOverlayClick() {
  if (downOnOverlay) emit('close')
}

function onResize() {
  measure()
}

watch(() => props.src, reset)

onMounted(() => {
  document.addEventListener('keydown', onKey)
  window.addEventListener('resize', onResize)
})

onUnmounted(() => {
  onWinUp()
  document.removeEventListener('keydown', onKey)
  window.removeEventListener('resize', onResize)
})
</script>

<template>
  <Teleport to="body">
    <div class="lightbox" @mousedown="onOverlayDown" @click="onOverlayClick">
      <div ref="stageEl" class="lb-stage" @wheel="onWheel" @click.stop>
        <img
          ref="imgEl"
          :src="src"
          :style="{
            width: (fit.w ? fit.w * scale : undefined) + 'px',
            height: (fit.h ? fit.h * scale : undefined) + 'px',
            transform: `translate(${tx}px, ${ty}px)`,
            cursor: scale > 1 ? (dragging ? 'grabbing' : 'grab') : 'default',
          }"
          @load="onImgLoad"
          @mousedown="onGrabDown"
        />
      </div>
      <div class="lb-bar" @click.stop>
        <button title="缩小" @click="zoom(1 / 1.2)"><span class="ms">zoom_out</span></button>
        <span class="lb-scale">{{ Math.round(scale * 100) }}%</span>
        <button title="放大" @click="zoom(1.2)"><span class="ms">zoom_in</span></button>
        <button class="lb-reset" title="重置缩放(滚轮缩放,放大后可拖拽)" @click="reset">1:1</button>
        <span class="lb-sep" />
        <button title="保存图像" @click="download"><span class="ms">download</span></button>
        <button title="关闭(Esc)" @click="emit('close')"><span class="ms">close</span></button>
      </div>
      <div class="lb-hint">滚轮缩放 · 放大后可拖拽 · 点空白处或按 Esc 关闭</div>
    </div>
  </Teleport>
</template>

<style scoped>
.lightbox {
  position: fixed;
  inset: 0;
  z-index: 3000;
  background: rgba(0, 0, 0, 0.78);
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 56px 32px 40px;
  cursor: zoom-out;
}

.lb-stage {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 0;
  min-width: 0;
  overflow: hidden;
}

.lb-stage img {
  flex: none;
  border-radius: 10px;
  box-shadow: 0 12px 48px rgba(0, 0, 0, 0.5);
  user-select: none;
}

/* 底部工具条:缩放/下载/关闭 */
.lb-bar {
  position: fixed;
  top: 14px;
  right: 16px;
  display: flex;
  align-items: center;
  gap: 4px;
  background: rgba(0, 0, 0, 0.55);
  border: 1px solid rgba(255, 255, 255, 0.14);
  border-radius: 10px;
  padding: 4px;
  backdrop-filter: blur(6px);
}

.lb-bar button {
  background: transparent;
  border: none;
  color: rgba(255, 255, 255, 0.85);
  cursor: pointer;
  border-radius: 7px;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 28px;
  padding: 0;
}

.lb-bar button:hover {
  background: rgba(255, 255, 255, 0.16);
}

.lb-bar .ms {
  font-size: 18.7px;
}

.lb-scale {
  color: rgba(255, 255, 255, 0.85);
  font-size: 12.1px;
  min-width: 40px;
  text-align: center;
}

.lb-reset {
  width: auto !important;
  padding: 0 8px !important;
  font-size: 12.1px !important;
  font-family: var(--font) !important;
}

.lb-sep {
  width: 1px;
  height: 16px;
  background: rgba(255, 255, 255, 0.2);
  margin: 0 3px;
}

.lb-hint {
  margin-top: 10px;
  color: rgba(255, 255, 255, 0.55);
  font-size: 12.1px;
}
</style>
