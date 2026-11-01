<script setup lang="ts">
// 高度定制的下拉框:1:1 复刻 apet-ui/Themes/Dark.xaml 的 ComboBox 规格 ——
// 触发器 BgInput 圆角6、悬停/展开描边 Accent;弹出层 BgPanelHover 圆角8 上距4、
// 条目 padding 12,7 margin 4,2 圆角6、悬停 Selection、选中 Accent;原生 select 在
// 各浏览器渲染差异大,全部改用本组件保证观感一致。Teleport 到 body 避免被容器裁剪。
// editable 模式(= 桌面端 IsEditable):可输入可筛选,用于字体/模型名等"可输入下拉"。
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'

export interface SelectOption {
  value: string
  label: string
  title?: string
}

const props = withDefaults(
  defineProps<{
    modelValue?: string
    options?: (string | SelectOption | [string, string])[]
    editable?: boolean
    placeholder?: string
    disabled?: boolean
    /** 嵌在按钮/胶囊里的内联模式:触发器渲染为 span(button 里不允许再套 button) */
    inline?: boolean
  }>(),
  { modelValue: '', options: () => [], editable: false, placeholder: '', disabled: false, inline: false },
)

const emit = defineEmits<{
  (e: 'update:modelValue', v: string): void
  (e: 'pick', v: string): void
  (e: 'enter'): void
}>()

const norm = computed<SelectOption[]>(() =>
  props.options.map((o) => {
    if (typeof o === 'string') return { value: o, label: o }
    if (Array.isArray(o)) return { value: o[0], label: o[1] || o[0] } // [值, 标签] 元组
    return { value: o.value, label: o.label ?? o.value, title: o.title }
  }),
)

const open = ref(false)
const hi = ref(-1)
const trigger = ref<HTMLElement | null>(null)
const pop = ref<HTMLElement | null>(null)
const popStyle = ref<Record<string, string>>({})

const current = computed(() => norm.value.find((o) => o.value === props.modelValue))
const currentLabel = computed(() => current.value?.label ?? props.modelValue)

const filterText = ref('')
const filtered = computed(() => {
  if (!props.editable) return norm.value
  const q = filterText.value.trim().toLowerCase()
  if (!q) return norm.value
  return norm.value.filter((o) => o.label.toLowerCase().includes(q) || o.value.toLowerCase().includes(q))
})

/** 界面缩放(zoom)补偿:rect/clientX 是可视像素,fixed 定位 px 会被 zoom 放大,需除回 */
function zoomFactor() {
  const z = Number(getComputedStyle(document.documentElement).zoom)
  return Number.isFinite(z) && z > 0 ? z : 1
}

function place() {
  const el = trigger.value
  if (!el) return
  const z = zoomFactor()
  const r = el.getBoundingClientRect()
  const est = Math.min(filtered.value.length, 8) * 31 + 16
  const avail = (window.innerHeight - r.bottom) / z // 触发器下方的剩余空间(CSS px)
  const style: Record<string, string> = { left: r.left / z + 'px', minWidth: r.width / z + 'px' }
  // 下方空间不足则向上弹(Popup 同行为)
  if (avail < est + 36 && r.top / z > est + 36)
    style.bottom = (window.innerHeight - r.top) / z + 4 + 'px'
  else style.top = r.bottom / z + 4 + 'px'
  popStyle.value = style
}

async function openPopup() {
  if (props.disabled || open.value) return
  filterText.value = props.editable ? props.modelValue : ''
  open.value = true
  hi.value = Math.max(0, filtered.value.findIndex((o) => o.value === props.modelValue))
  await nextTick()
  place()
  scrollHi()
}

function close() {
  open.value = false
}

function toggle() {
  open.value ? close() : openPopup()
}

function pick(v: string) {
  emit('update:modelValue', v)
  emit('pick', v)
  close()
}

function onInput(e: Event) {
  const v = (e.target as HTMLInputElement).value
  filterText.value = v
  emit('update:modelValue', v)
  if (!open.value) openPopup()
  else place()
  hi.value = filtered.value.length ? 0 : -1
}

function scrollHi() {
  pop.value?.querySelector('.asel-item.hl')?.scrollIntoView({ block: 'nearest' })
}

function moveHi(d: number) {
  const n = filtered.value.length
  if (!n) return
  hi.value = (hi.value + d + n) % n
  nextTick(scrollHi)
}

function onKeydown(e: KeyboardEvent) {
  if (props.disabled) return
  if (!open.value) {
    if (props.editable) {
      if (e.key === 'ArrowDown') {
        e.preventDefault()
        openPopup()
      }
      return
    }
    if (['Enter', ' ', 'ArrowDown', 'ArrowUp'].includes(e.key)) {
      e.preventDefault()
      openPopup()
    }
    return
  }
  switch (e.key) {
    case 'ArrowDown':
      e.preventDefault()
      moveHi(1)
      break
    case 'ArrowUp':
      e.preventDefault()
      moveHi(-1)
      break
    case 'Home':
      e.preventDefault()
      hi.value = filtered.value.length ? 0 : -1
      nextTick(scrollHi)
      break
    case 'End':
      e.preventDefault()
      hi.value = filtered.value.length - 1
      nextTick(scrollHi)
      break
    case 'Enter':
      e.preventDefault()
      if (filtered.value[hi.value]) pick(filtered.value[hi.value].value)
      emit('enter')
      break
    case 'Escape':
      e.preventDefault()
      close()
      break
    case 'Tab':
      close()
      break
  }
}

function onDocPointer(e: PointerEvent) {
  const t = e.target as Node
  if (trigger.value?.contains(t) || pop.value?.contains(t)) return
  close()
}

function onScroll() {
  if (open.value) place()
}

watch(open, (v) => {
  if (v) {
    document.addEventListener('pointerdown', onDocPointer, true)
    window.addEventListener('scroll', onScroll, true)
    window.addEventListener('resize', onScroll)
  } else {
    document.removeEventListener('pointerdown', onDocPointer, true)
    window.removeEventListener('scroll', onScroll, true)
    window.removeEventListener('resize', onScroll)
  }
})

onBeforeUnmount(() => {
  if (open.value) {
    document.removeEventListener('pointerdown', onDocPointer, true)
    window.removeEventListener('scroll', onScroll, true)
    window.removeEventListener('resize', onScroll)
  }
})
</script>

<template>
  <span ref="trigger" class="asel" :class="{ open, editable: props.editable, disabled: props.disabled }">
    <input
      v-if="props.editable"
      class="asel-text"
      :value="props.modelValue"
      :placeholder="props.placeholder"
      :disabled="props.disabled"
      spellcheck="false"
      autocomplete="off"
      @input="onInput"
      @focus="openPopup"
      @keydown="onKeydown"
    />
    <span
      v-else-if="props.inline"
      class="asel-text asel-btn"
      role="button"
      tabindex="0"
      @click="toggle"
      @keydown="onKeydown"
    >
      <span class="asel-label" :class="{ ph: !current && !props.modelValue }">{{ currentLabel || props.placeholder || '请选择…' }}</span>
    </span>
    <button v-else type="button" class="asel-text asel-btn" :disabled="props.disabled" @click="toggle" @keydown="onKeydown">
      <span class="asel-label" :class="{ ph: !current && !props.modelValue }">{{ currentLabel || props.placeholder || '请选择…' }}</span>
    </button>
    <span class="ms asel-chevron" aria-hidden="true">expand_more</span>
    <Teleport to="body">
      <div v-if="open" ref="pop" class="asel-pop" :style="popStyle">
        <div
          v-for="(o, i) in filtered"
          :key="o.value"
          class="asel-item"
          :class="{ hl: i === hi, sel: o.value === props.modelValue }"
          :title="o.title"
          @mousemove="hi = i"
          @mousedown.prevent="pick(o.value)"
        >
          {{ o.label }}
        </div>
        <div v-if="filtered.length === 0" class="asel-none">无匹配项</div>
      </div>
    </Teleport>
  </span>
</template>

<style>
/* 非scoped:弹出层 Teleport 到 body,必须用全局样式 */
.asel {
  position: relative;
  display: inline-flex;
  align-items: center;
  height: 30px;
  max-width: 100%;
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: 6px;
  box-sizing: border-box;
  transition: border-color 0.12s;
  vertical-align: middle;
}

.asel:hover,
.asel.open {
  border-color: var(--fg-accent);
}

.asel.disabled {
  opacity: 0.45;
  pointer-events: none;
}

.asel-text {
  flex: 1;
  min-width: 0;
  height: 100%;
  background: transparent;
  border: none;
  color: var(--fg-primary);
  font-family: var(--font);
  font-size: 14.3px;
  padding: 0 26px 0 9px;
  text-align: left;
  outline: none;
  box-sizing: border-box;
}

input.asel-text {
  cursor: text;
}

.asel-text::placeholder {
  color: var(--fg-muted);
}

.asel-btn {
  display: flex;
  align-items: center;
  cursor: pointer;
}

.asel-label {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.asel-label.ph {
  color: var(--fg-muted);
}

.asel-chevron {
  position: absolute;
  right: 7px;
  font-size: 15.4px;
  color: var(--fg-secondary);
  pointer-events: none;
  flex: none;
}

.asel-pop {
  position: fixed;
  z-index: 2800;
  box-sizing: border-box;
  background: var(--bg-panel-hover);
  border: 1px solid var(--border);
  border-radius: 8px;
  max-height: 260px;
  overflow-y: auto;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.4);
}

.asel-item {
  padding: 7px 12px;
  margin: 2px 4px;
  border-radius: 6px;
  font-size: 14.3px;
  color: var(--fg-primary);
  cursor: pointer;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.asel-item.hl {
  background: var(--selection);
}

.asel-item.sel,
.asel-item.sel.hl {
  background: var(--accent);
  color: #fff;
}

.asel-none {
  padding: 8px 12px;
  color: var(--fg-muted);
  font-size: 13.2px;
}
</style>
