// SSE 订阅:每帧 {"type":..,"data":..};断线由 EventSource 自动重连
// 用法:onEvent(type, payload) 注册处理器;返回关闭函数。

export type StreamHandler = (type: string, data: any) => void

export function subscribeStream(url: string, onEvent: StreamHandler): () => void {
  const source = new EventSource(url)
  // 断线最小反馈:本文件没有错误回调机制,不为此扩 API,只留一行日志供排查
  // (EventSource 会自动重连;readyState:0=重连中 1=已连接 2=已关闭,2 即不再重连)
  source.onerror = () => {
    console.warn(`[sse] 连接中断,浏览器将自动重连: url=${url} readyState=${source.readyState}`)
  }
  source.onmessage = (e) => {
    try {
      const frame = JSON.parse(e.data) as { type: string; data: any }
      onEvent(frame.type, frame.data ?? {})
    } catch {
      // 非 JSON 帧(保活注释由浏览器过滤,这里兜底忽略坏帧)
    }
  }
  return () => source.close()
}
