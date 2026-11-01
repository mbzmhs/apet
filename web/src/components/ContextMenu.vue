<script setup lang="ts">
// 全局自定义右键菜单(复制所选/复制全文等);session 右键菜单等局部菜单仍由各自组件渲染。
import { onMounted, onUnmounted } from 'vue'
import { closeContextMenu, contextMenuState } from '../store/contextMenu'

function run(item: (typeof contextMenuState.value)['items'][number]) {
  if (item.disabled) return
  closeContextMenu()
  item.action()
}

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') closeContextMenu()
}

function onPointer(e: PointerEvent) {
  if (!(e.target as HTMLElement).closest('.g-ctx-menu')) closeContextMenu()
}

onMounted(() => {
  document.addEventListener('keydown', onKey)
  document.addEventListener('pointerdown', onPointer, true)
  window.addEventListener('blur', closeContextMenu)
})

onUnmounted(() => {
  document.removeEventListener('keydown', onKey)
  document.removeEventListener('pointerdown', onPointer, true)
  window.removeEventListener('blur', closeContextMenu)
})
</script>

<template>
  <Teleport to="body">
    <div
      v-if="contextMenuState"
      class="g-ctx-menu"
      :style="{ left: contextMenuState.x + 'px', top: contextMenuState.y + 'px' }"
    >
      <button
        v-for="(item, i) in contextMenuState.items"
        :key="i"
        :class="{ danger: item.danger }"
        :disabled="item.disabled"
        @click="run(item)"
      >
        <span class="ms mi" :class="{ fill: item.danger }">{{ item.icon ?? 'check' }}</span>{{ item.label }}
      </button>
    </div>
  </Teleport>
</template>

<style>
/* 非 scoped:Teleport 到 body */
.g-ctx-menu {
  position: fixed;
  z-index: 3000;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 4px;
  min-width: 140px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
}

.g-ctx-menu button {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  background: transparent;
  border: none;
  color: var(--fg-primary);
  text-align: left;
  padding: 8px 12px;
  font-size: 14.3px;
  font-family: var(--font);
  cursor: pointer;
  border-radius: 6px;
}

.g-ctx-menu button:hover:not(:disabled) {
  background: var(--selection);
}

.g-ctx-menu button:disabled {
  opacity: 0.4;
  cursor: default;
}

.g-ctx-menu .mi {
  flex: none;
  width: 17px;
  font-size: 17.6px;
  text-align: center;
  color: var(--fg-secondary);
}

.g-ctx-menu button.danger {
  color: var(--danger);
}

.g-ctx-menu button.danger .mi {
  color: var(--danger);
}
</style>
