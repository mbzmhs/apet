// 确认/输入对话框的状态机:替代浏览器原生 confirm/prompt。
// 用法:const { dlg, ask, settle } = useActionDialog()
//   模板渲染 <ActionDialog v-if="dlg" v-bind="dlg" @confirm="settle" @close="settle(null)" />
//   代码里:const v = await ask({ title: '…', input: true, initialValue: 'x' })  // 输入框:值 | null(取消)
//         const ok = await ask({ title: '…', message: '…', danger: true })       // 确认框:true | null(取消)
import { ref } from 'vue'

export interface DialogReq {
  title: string
  message?: string
  input?: boolean
  initialValue?: string
  confirmLabel?: string
  danger?: boolean
}

export interface ActiveDialog extends DialogReq {
  resolve: (v: string | boolean | null) => void
}

export function useActionDialog() {
  const dlg = ref<ActiveDialog | null>(null)

  function ask(req: DialogReq): Promise<string | boolean | null> {
    if (dlg.value) return Promise.resolve(null) // 已有对话框时忽略,避免叠加
    return new Promise((resolve) => {
      dlg.value = { ...req, resolve }
    })
  }

  function settle(value: string | boolean | null) {
    const d = dlg.value
    dlg.value = null
    d?.resolve(value)
  }

  return { dlg, ask, settle }
}
