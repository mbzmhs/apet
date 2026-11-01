<script setup lang="ts">
import type { FolderInfo, SessionListItem, TreeRow } from '../types'
import { folderColorHex } from '../lib/folderColors'

/** 会话树递归渲染组件(按文件名自引用实现递归)。行样式在全局 list-rows.css
 * (父组件 ChatView 的 scoped 样式到不了这里)。文件夹子块的展开/收起动画由 .kids
 * 的 grid-template-rows 过渡承担(整块一个元素,天然平滑)。 */
const props = defineProps<{
  tree: TreeRow[]
  collapsed: Set<string>
  selId: string
  dragKey: string | null
  dropHint: string | null
  onOpenSession: (id: string) => void
  onToggleFolder: (id: string) => void
  onSessionMenu: (s: SessionListItem, e: MouseEvent) => void
  onFolderMenu: (f: FolderInfo, e: MouseEvent) => void
  onDragStartRow: (row: TreeRow, e: DragEvent) => void
  onDragEndRow: () => void
  onDragOverRow: (row: TreeRow, e: DragEvent) => void
  onDropRow: (row: TreeRow, e: DragEvent) => void
}>()

function rowClass(r: TreeRow) {
  return {
    sel: r.kind === 'session' && r.s!.sessionId === props.selId,
    folder: r.kind === 'folder',
    dragging: props.dragKey === r.key,
    'drop-before': props.dropHint === r.key + ':b',
    'drop-after': props.dropHint === r.key + ':a',
    'drop-into': props.dropHint === r.key + ':i',
  }
}

/** 文件夹自身线色:预设色或默认灰(同图标色)。恒显式设置在子块容器上——CSS 自定义属性
 * 默认被继承,只在有预设色时设置的话,默认色子夹的线会随祖先夹的预设色,而不是自己的图标色 */
function ownLineColor(f: FolderInfo): string {
  return folderColorHex(f.color ?? '') || 'var(--fg-secondary)'
}

/** 每级缩进步距:须大于行内"箭头中心 + 线宽"偏移(行内 padding 10 + 箭头 8.5 + 线 1 ≈ 20px),
 * 否则对准箭头中心的竖线会插进下一级子行的圆角背景里(视觉重叠) */
const INDENT = 22

/** 行内联样式:缩进(竖线在子块容器上画,行本身不再画线) */
function rowStyle(r: TreeRow): Record<string, string> {
  return {
    marginLeft: 6 + r.depth * INDENT + 'px',
    width: `calc(100% - ${12 + r.depth * INDENT}px)`,
  }
}

/** 子块容器内联样式:--line-x 对准该夹行展开箭头的正中心 = 行左缘(6+INDENT·d) + 行内 padding(10px)
 * + 箭头半宽(字号 17px → 8.5px,Material Symbols advance = 1em) − 线宽半值(1px 线 → 0.5px);
 * 线色 = 本夹自身色 */
function kidsStyle(r: TreeRow): Record<string, string> {
  return { '--line-x': 6 + r.depth * INDENT + 18 + 'px', '--own-line-color': ownLineColor(r.f!) }
}
</script>

<template>
  <template v-for="node in tree" :key="node.key">
    <button
      v-if="node.kind === 'session'"
      class="session-item list-row"
      :class="rowClass(node)"
      :style="rowStyle(node)"
      draggable="true"
      @click="onOpenSession(node.s!.sessionId)"
      @contextmenu.prevent="onSessionMenu(node.s!, $event)"
      @dragstart="onDragStartRow(node, $event)"
      @dragend="onDragEndRow()"
      @dragover.prevent="onDragOverRow(node, $event)"
      @drop.prevent="onDropRow(node, $event)"
    >
      <span
        class="dot"
        :class="{ work: node.s!.isStreaming && !node.s!.isCompressing, compress: node.s!.isCompressing, err: node.s!.hasError, unread: node.s!.unread }"
      />
      <span class="s-title">{{ node.s!.title }}</span>
      <span v-if="node.s!.lastMessageTimeText" class="s-time">{{ node.s!.lastMessageTimeText }}</span>
    </button>
    <template v-else>
      <button
        class="session-item list-row"
        :class="rowClass(node)"
        :style="rowStyle(node)"
              :title="node.f!.name + (collapsed.has(node.f!.id) ? '(点击展开/收起,右键管理;拖拽:上缘=插到前,下缘=插到后,其余=移入/嵌套进该夹)' : '(点击展开/收起,右键管理;拖拽:上缘=插到前,其余=移入/嵌套进该夹)')"
        draggable="true"
        @click="onToggleFolder(node.f!.id)"
        @contextmenu.prevent="onFolderMenu(node.f!, $event)"
        @dragstart="onDragStartRow(node, $event)"
        @dragend="onDragEndRow()"
        @dragover.prevent="onDragOverRow(node, $event)"
        @drop.prevent="onDropRow(node, $event)"
      >
        <span class="ms chevron" :class="{ open: !collapsed.has(node.f!.id) }">{{ collapsed.has(node.f!.id) ? 'chevron_right' : 'expand_more' }}</span>
        <span class="ms folder-ico" :style="folderColorHex(node.f!.color ?? '') ? { color: folderColorHex(node.f!.color ?? '') } : undefined">{{ collapsed.has(node.f!.id) ? 'folder' : 'folder_open' }}</span>
        <span class="s-title folder-title">{{ node.f!.name }}</span>
        <span v-if="node.count" class="s-time folder-count" :title="`共 ${node.count} 个会话(含子文件夹)`">{{ node.count }}</span>
      </button>
      <!-- 子块:收起时 grid 0fr 高度归零;展开/收起 = 这一个元素的 0fr↔1fr 原生过渡。
           沿左缘为该夹整棵子树续竖线(.kids::before,位置/色由 --line-x / --own-line-color 决定) -->
      <div v-if="node.children && node.children.length" class="kids" :class="{ open: !collapsed.has(node.f!.id) }" :style="kidsStyle(node)">
        <div class="kids-in">
          <SidebarTree
            v-bind="$props"
            :tree="node.children"
            :on-open-session="onOpenSession"
            :on-toggle-folder="onToggleFolder"
            :on-session-menu="onSessionMenu"
            :on-folder-menu="onFolderMenu"
            :on-drag-start-row="onDragStartRow"
            :on-drag-end-row="onDragEndRow"
            :on-drag-over-row="onDragOverRow"
            :on-drop-row="onDropRow"
          />
        </div>
      </div>
    </template>
  </template>
</template>
