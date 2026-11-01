// 工具实时输出增量的落位逻辑(主聊天工具卡与子代理模态框共用,避免两处走样):
// 按 toolCallId(有则精确)/工具名找到"运行中"的工具行,把增量续接到其展开详情上。

interface ToolOutputFrame {
  toolCallId?: string | null
  toolName: string
  stream?: string
  delta: string
}

interface ToolLikeRow {
  kind?: string
  toolCallId?: string | null
  toolName?: string
  detail?: string
  hasDetail?: boolean
  isRunning?: boolean
  liveOutput?: boolean
}

/**
 * 把一段输出增量追加到运行中的工具行上并标记可展开;找不到对应行时静默忽略
 * (快照重放会补上完整内容)。首次追加补一个换行——详情首行是命令原文($ 命令)。
 */
export function appendToolOutput(rows: ToolLikeRow[], data: ToolOutputFrame): boolean {
  if (!data?.delta) return false
  for (let i = rows.length - 1; i >= 0; i--) {
    const row = rows[i]
    if (row?.kind !== 'tool' || !row.isRunning || row.toolName !== data.toolName) continue
    // callId 严格匹配:两侧都有 id 才可比,任一侧缺失即视为不同行(同名并发调用按名字会错配;
    // 后端保证帧带 callId,仅在快照重放等边角可能缺,快照本身会补全内容)
    const fid = data.toolCallId ?? null
    const rid = row.toolCallId ?? null
    if ((fid ?? rid) !== null && fid !== rid) continue
    if (!row.liveOutput) {
      row.liveOutput = true
      row.detail = row.detail ? row.detail + '\n' + data.delta : data.delta
    } else {
      row.detail = (row.detail ?? '') + data.delta
    }
    row.hasDetail = true
    return true
  }
  return false
}