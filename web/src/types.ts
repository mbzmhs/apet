// 与后端 DTO(apet-ui/Web/SessionDtos.cs)和 SSE 事件帧对应的类型定义

export interface AgentInfo {
  id: string
  name: string
  title: string
  enabled: boolean
  /** 头像版本(文件修改时间):>0 表示有头像,拼 ?v= 做缓存失效 */
  avatarRev: number
}

export interface HealthInfo {
  ok: boolean
  port: number
  agents: number
}

export interface SessionListItem {
  sessionId: string
  title: string
  lastMessageTimeText: string
  isStreaming: boolean
  isCompressing: boolean
  hasError: boolean
  unread: boolean
  pinned: boolean
  /** 所属文件夹(null = 根组);旧快照缺省 = 根。 */
  folderId: string | null
  /** 组内槽位序号(与同组文件夹共享一个序号空间,列表按它把会话与文件夹合并排序);缺省 = -1(排组尾)。 */
  slotSort: number
}

/** 会话列表文件夹(嵌套):parentId 为 null = 顶级;sort = 其在父组列表中的槽位序号(与组内会话同一序号空间)。 */
export interface FolderInfo {
  id: string
  name: string
  parentId: string | null
  sort: number
  /** 预设色板 id(见 ChatView FOLDER_COLORS);'' = 默认不着色。旧数据缺省 = '' */
  color?: string
}

export interface DiffLine {
  lineNo: string
  text: string
  kind: string
}

export interface ContentStep {
  kind: 'content'
  text: string
}

export interface ThinkingStep {
  kind: 'thinking'
  text: string
  isActive: boolean
  header: string
  seconds: number | null
}

export interface ToolStep {
  kind: 'tool'
  displayName: string
  toolName: string
  summary: string
  detail: string
  hasDetail: boolean
  isRunning: boolean
  isError: boolean
  resultSummary: string
  /** 文件类工具(写入/编辑/补丁)的文件名:非空且有 diff 时按文件卡片渲染 */
  pathLink: string
  statAdd: string
  statDel: string
  diffLines: DiffLine[]
  imageFiles: string[]
  /** sub_* 工具关联的子代理运行 id:非空时行可点击打开子代理模态框 */
  subRunId: number | null
  /** 工具调用回链 id(SSE 帧;旧快照行可能缺省) */
  toolCallId?: string | null
  /** 参数流式预览阶段的归并键(callId ?? 'k'+argsKey):tool_args 帧按它就地刷新 */
  argsKey?: string
  /** 参数流式占位行:tool_started 到达后被完整行回填替换 */
  isPlaceholder?: boolean
}

export interface AuthStep {
  kind: 'auth'
  authId: string | null
  detail: string
  trustDirs: string[]
  decided: boolean
  granted: boolean
}

export type Step = ContentStep | ThinkingStep | ToolStep | AuthStep | { kind: string }

export interface TextFile {
  name: string
  sizeText: string
}

export interface Message {
  clientId: string | null
  role: 'user' | 'assistant' | 'status' | 'divider' | 'spinner'
  text: string
  timeText: string
  isError: boolean
  imageFiles: string[]
  textFiles: TextFile[] | null
  /** 本气泡对应的 context 原始消息 id 集(助手=该轮全部助手消息按请求序;用户=其 clientId):
   *  凭它调 /messages/{id} 取原始内容(用量/模型/思考/工具参数)显示详情,后期消费;
   *  乐观渲染的本地消息可不带 */
  msgIds?: string[]
  steps: Step[] | null
  /** 前端专用:乐观渲染时随消息携带的图片 data URL(快照对齐后由 imageFiles 接管) */
  clientDataUrls?: string[]
  /** 会话内稳定序号(与 display 落盘 seq 同口径;临时行=压缩齿轮等=null):
   * 历史分页游标与渲染 key 的依据;旧快照可能缺省 */
  seq?: number | null
  /** 前端专用:无 seq 行(乐观气泡/事件插入的状态行)的稳定渲染 key。
   * 缺省时列表只能退回数组下标做 key,前插/对齐会把后续行的 DOM 复用错位 */
  localKey?: string
}

/** 助手气泡悬停详情:按 msgIds 聚合原始消息(生成时间/服务端真实用量/所用模型) */
export interface MessageSummary {
  time: string | null
  completedAt: string | null
  promptTokens: number
  completionTokens: number
  totalTokens: number
  cachedTokens: number
  reasoningTokens: number
  provider: string | null
  model: string | null
}

export interface SessionState {
  sessionId: string
  title: string
  isStreaming: boolean
  isCompressing: boolean
  hasError: boolean
  provider: string
  model: string
  thinkingEffort: string
  workingDirectory: string
  workingDirectoryAccess: string
  isWorkDirLocked: boolean
  lastPromptTokens: number | null
  contextLimit: number
  /** 会话累计 LLM 用量(持久化;状态栏以此为初始值,再按 usage 帧增量) */
  usage: { promptTotal: number; cachedTotal: number; completionTotal: number; lastTps: number; cost: number }
  /** 聊天背景:非空 = 有背景图(取 /backgrounds/chat),值为图片不透明度% */
  chatBgOpacity: number | null
  chatBgStretch: string | null
  /** 窗体背景:非空 = 有背景图(取 /backgrounds/window),铺整个聊天窗底层,半透明面板透出 */
  windowBgOpacity: number | null
  windowBgStretch: string | null
  lastMessageTimeText: string
  messages: Message[]
  /** 中途消息队列(运行中发送的插话;下一次请求前按 FIFO 带出),输入框上方队列条消费 */
  queuedMessages: QueuedItem[]
  /** 历史分页:非临时消息行总数(全量口径;全量快照 = messages 长度) */
  messageTotal?: number
  /** 已加载窗口首行的 seq;null = 全量加载,无窗口(旧快照/短会话) */
  messageCursor?: number | null
  /** 窗口之前是否还有更早历史(可向上翻页加载) */
  hasMoreMessages?: boolean
  /** 归档只读预览:禁输入/发送/命令,仅回看(旧快照可能缺省) */
  isArchived?: boolean
}

/** 中途队列条目(轻量展示字段;重新编辑经 DELETE 取完整原文与附件) */
export interface QueuedItem {
  id: string
  text: string
  imageCount: number
  fileNames: string[]
  /** 本轮后发送:不拼进进行中请求,当前轮结束后作为新消息发出 */
  defer: boolean
}

/** 队列条目完整载荷(取消直接丢弃;重新编辑回填输入框) */
export interface QueuedEdit {
  id: string
  text: string
  images: string[] | null
  textFiles: { name: string; content: string }[] | null
}

// ───────── SSE 事件负载 ─────────

export interface StreamEvent {
  type: string
  data: any
}

// ───────── M2a:会话管理 / 模型 / 斜杠命令 ─────────

export interface ProviderInfo {
  name: string
  defaultModel: string
  reasoningEffort: string
  models: ProviderModelInfo[]
}

/** 模型清单中的一项:名称 + 高级设置(空数组 = 默认全集) */
export interface ProviderModelInfo {
  name: string
  contextTokens: number
  /** 计费因子(元/百万 tokens);0 = 不计费 */
  priceInput: number
  priceInputCached: number
  priceOutput: number
  /** 该模型可用的思考等级(空 = 全部 7 档) */
  thinkingLevels: string[] | null
  /** 该模型支持的附件媒体类型:image/video/audio/pdf(空 = 仅默认图片) */
  mediaTypes: string[] | null
}

export interface CommandInfo {
  name: string
  description: string
  takesArgument: boolean
}

export interface CommandResult {
  handled: boolean
  feedback: string | null
  restored: string | null
  newSessionId: string | null
  forkedId: string | null
}

export interface ArchivedSession {
  id: string
  title: string
  updatedAt: string
}

/** 侧栏会话树的渲染节点(SidebarTree 递归渲染):文件夹节点的 children 恒构建
 * (收起时由 CSS 隐藏),展开/收起才能做整块高度过渡。
 * 范围竖线按"每夹沿自己子块续线"渲染,行本身无需携带父夹信息。 */
export interface TreeRow {
  key: string
  kind: 'session' | 'folder'
  depth: number
  s?: SessionListItem
  f?: FolderInfo
  count?: number
  children?: TreeRow[]
}
