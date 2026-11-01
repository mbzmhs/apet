<script setup lang="ts">
// 自风格确认/输入模态框:替代浏览器原生 confirm/prompt(样式与 AppSelect/ghost 体系一致)。
import { nextTick, ref } from 'vue'
import ModalDialog from './ModalDialog.vue'

const props = withDefaults(
  defineProps<{
    title: string
    message?: string
    input?: boolean
    initialValue?: string
    confirmLabel?: string
    danger?: boolean
  }>(),
  { confirmLabel: '确定' },
)
const emit = defineEmits<{ confirm: [value: string | boolean | null]; close: [] }>()

const value = ref(props.initialValue ?? '')
const inputEl = ref<HTMLInputElement | null>(null)
nextTick(() => {
  if (props.input && inputEl.value) {
    inputEl.value.focus()
    inputEl.value.select()
  }
})

function submit() {
  emit('confirm', props.input ? value.value : true)
}
</script>

<template>
  <ModalDialog :title="title" :width="input ? '500px' : '460px'" @close="emit('close')">
    <div v-if="message" class="ad-msg">{{ message }}</div>
    <input
      v-if="input"
      ref="inputEl"
      v-model="value"
      class="ad-input"
      @keyup.enter="submit"
    />
    <template #footer>
      <button class="ad-cancel" @click="emit('close')">取消</button>
      <button class="ad-ok" :class="{ danger }" @click="submit">
        <span v-if="danger" class="ms">delete</span>
        {{ confirmLabel }}
      </button>
    </template>
  </ModalDialog>
</template>

<style>
/* 非 scoped:Teleport 到 body */
.ad-msg {
  font-size: 14.3px;
  line-height: 1.55;
  color: var(--fg-primary);
  word-break: break-word;
  white-space: pre-wrap;
}

.ad-input {
  width: 100%;
  box-sizing: border-box;
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid var(--border);
  background: var(--bg-code);
  color: var(--fg-primary);
  font-size: 14.3px;
  font-family: var(--font);
  outline: none;
}

.ad-input:focus {
  border-color: var(--accent);
}

.ad-cancel {
  padding: 7px 16px;
  border-radius: 8px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--fg-secondary);
  font-size: 14.3px;
  font-family: var(--font);
  cursor: pointer;
}

.ad-cancel:hover {
  background: var(--selection);
  color: var(--fg-primary);
}

.ad-ok {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 7px 16px;
  border-radius: 8px;
  border: none;
  background: var(--accent);
  color: #fff;
  font-size: 14.3px;
  font-family: var(--font);
  cursor: pointer;
}

.ad-ok:hover {
  filter: brightness(1.1);
}

.ad-ok.danger {
  background: var(--danger);
}

.ad-ok .ms {
  font-size: 17.6px;
}
</style>
