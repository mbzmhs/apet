/** 文件夹预设色板:color = 后端 folders 表存的色板 id(空串 = 默认,默认外观即中性灰)。
 * 灰色不单列预设(与默认重复);旧数据里残留的 'gray' id 查不到色,自动落回默认灰,无需迁移。
 * ChatView(右键菜单色板/图标着色)与 SidebarTree(范围竖线/图标着色)共用。 */
export const FOLDER_COLORS: { id: string; name: string; hex: string }[] = [
  { id: '', name: '默认', hex: '' },
  { id: 'red', name: '红色', hex: '#e5484d' },
  { id: 'orange', name: '橙色', hex: '#ef8f3a' },
  { id: 'yellow', name: '黄色', hex: '#e0c14b' },
  { id: 'green', name: '绿色', hex: '#5cae62' },
  { id: 'blue', name: '蓝色', hex: '#4d8fd6' },
  { id: 'purple', name: '紫色', hex: '#9a6fd0' },
]

export function folderColorHex(color: string): string {
  return FOLDER_COLORS.find((c) => c.id === color)?.hex ?? ''
}
