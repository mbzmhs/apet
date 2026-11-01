<script setup lang="ts">
// 通用模式对话框:设置页里插件设置 / 模型高级设置等集中编辑用。
// 遮罩点击 / Esc / 右上角 × 均可关闭;内容区滚动,宽度可调。
import { onMounted, onUnmounted } from 'vue'

const props = withDefaults(defineProps<{ title: string; sub?: string; width?: string }>(), { width: '680px' })
const emit = defineEmits<{ close: [] }>()

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') emit('close')
}

// 只有"按下且松手都在遮罩上"才算点击遮罩关闭:
// 从窗内按住拖到窗外再松手,mousedown/mouseup 目标不同,click 会落到公共祖先(遮罩)上,
// 仅靠 @click.self 无法区分,会误关
let downOnOverlay = false
function onOverlayDown(e: MouseEvent) {
  downOnOverlay = e.target === e.currentTarget
}
function onOverlayClick() {
  if (downOnOverlay) emit('close')
}

onMounted(() => document.addEventListener('keydown', onKey))
onUnmounted(() => document.removeEventListener('keydown', onKey))
</script>

<template>
  <Teleport to="body">
    <div class="mdl-overlay" @mousedown="onOverlayDown" @click="onOverlayClick">
      <div class="mdl-panel" :style="{ width: props.width, maxWidth: '94vw' }">
        <div class="mdl-head">
          <div class="mdl-titles">
            <span class="mdl-title">{{ title }}</span>
            <span v-if="sub" class="mdl-sub">{{ sub }}</span>
          </div>
          <button class="mdl-x" title="关闭(Esc)" @click="emit('close')"><span class="ms">close</span></button>
        </div>
        <div class="mdl-body">
          <slot />
        </div>
        <div v-if="$slots.footer" class="mdl-foot">
          <slot name="footer" />
        </div>
      </div>
    </div>
  </Teleport>
</template>

<style>
/* 非 scoped:Teleport 到 body */
.mdl-overlay {
  position: fixed;
  inset: 0;
  z-index: 2600;
  background: rgba(0, 0, 0, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
}

.mdl-panel {
  max-height: 88vh;
  display: flex;
  flex-direction: column;
  background: var(--bg-panel);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: 0 16px 48px rgba(0, 0, 0, 0.5);
  overflow: hidden;
}

.mdl-head {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 14px 18px 12px;
  border-bottom: 1px solid var(--border);
  flex: none;
}

.mdl-titles {
  display: flex;
  align-items: baseline;
  gap: 10px;
  min-width: 0;
  flex: 1;
}

.mdl-title {
  font-size: 16.5px;
  font-weight: 600;
}

.mdl-sub {
  font-size: 12.1px;
  color: var(--fg-muted);
}

.mdl-x {
  background: transparent;
  border: none;
  color: var(--fg-secondary);
  cursor: pointer;
  display: inline-flex;
  border-radius: 6px;
  padding: 3px;
}

.mdl-x:hover {
  background: var(--selection);
  color: var(--fg-primary);
}

.mdl-x .ms {
  font-size: 17.6px;
}

.mdl-body {
  flex: 1;
  overflow-y: auto;
  padding: 14px 18px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.mdl-foot {
  flex: none;
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  padding: 12px 18px;
  border-top: 1px solid var(--border);
}
</style>
