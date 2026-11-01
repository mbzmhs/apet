# APet 外部插件开发指南

外部插件 = 一个引用 `APet.Framework.dll` 的类库 DLL,放入部署目录 **`plugins\`** 文件夹,重启 APet 后即出现在设置页「内置插件 → 外部插件」中,每个 Agent 一份独立实例,设置表单按插件声明的字段**自动生成**。

## 能力边界(当前版本)

外部插件可以使用:

| 能力 | 说明 |
|---|---|
| 设置项 | `[PluginSetting]` 注解属性 → 设置页自动生成表单,启停/值持久化到 `agents/{id}/setting.json` |
| 元数据 | 重写 `Version` / `Author` / `Description` / `LoadOrder`,设置页卡片展示;LoadOrder 决定注册顺序 |
| `$` 聊天指令 | `GetChatCommands()`,输入 `$` 或 `/` 弹出补全面板 |
| 工具(文本) | `IsTool = true` + `ToolDefinition` / `ExecuteToolAsync`(返回 `Task<string>`) |
| 工具(多模态) | 重写 `ExecuteToolResultAsync`(返回 `Task<ToolResult>`),结果可携带图片(data URL)回传视觉模型 |
| system prompt 注入 | `GetSystemPromptSegment(session)` |
| 消息钩子 | `OnBeforeRunAsync` / `OnBeforeRequest(Async)` / `OnStreamDelta` / `OnAssistantMessage` / `OnAfterTurnAsync` |
| 运行收尾 | `OnRunFinishedAsync(agent, session, outcome)`——成功/中断/出错**三态**都会送达,收尾类需求(TTS 播报完成、资源释放)用这个;`OnAfterTurnAsync` 只在成功路径触发 |
| 会话生命周期 | `OnSessionEvent(agent, session, kind)`:Created(新建/分叉/从磁盘还原/移出归档)/ Removed(删除/归档) |
| 异步生命周期 | `InitializeAsync()`(同步 Initialize 之后;宿主构造路径后台执行,Web 启用端点 await 到完成)/ `DisposeAsync()`(运行时销毁时 await) |
| 前端数据面 | `ProvidesData = true` + `GetPluginData(sessionId)` / `HandlePluginData(sessionId, body)` → 侧边栏"插件"页签 + REST 端点;返回值含 `status` 字符串字段 → 底部状态栏 |
| 前端事件推送 | `PluginEvents.Publish(session, type, data)`(会话级)/ `PluginEvents.PublishAgent(agentId, type, data)`(Agent 级)→ SSE |
| 宿主配置视图 | `AgentConfigView` 读宿主 AgentConfig 上 `[PluginVisible]` 标注的属性(当前键:`language`);活读,未知键 null |
| 自定义 UI(界面级) | `UiSlots` 声明 `settings`/`sidebar` 插槽 + DLL 内嵌 `ui\*` 资产 → 同源 iframe 渲染;桥 `/apet-bridge.js` 提供设置/数据面/事件/**宿主 DOM** 能力(注入节点按 `data-APet-plugin` 标记自动回收) |

插件段由框架以 `<plugin name="插件名">…</plugin>` 标签包裹后按注册顺序拼接到 system prompt 末尾,段内保持插件返回的原文。

**拿不到**宿主服务(TTS 播放器、桌宠窗口、SessionStore、UI 等)——这些是内置插件由宿主手工接线的,外部插件的钩子入参只有 `(Agent, AgentSession)`;`AgentId` 属性由宿主注册后注入,可作 Agent 级事件的发布凭据。

## 最小步骤

1. 新建 **net8.0 类库**,引用 `APet.Framework`(建议像 `plugins-sample/HelloPlugin` 一样用 ProjectReference;发布时直接引用部署目录的 `APet.Framework.dll` 也可以)。
2. 写一个继承 `APet.Framework.Plugin.AgentPluginBase` 的公开非抽象类,**必须带无参构造**,并实现抽象成员 `Initialize()` / `Dispose()`(无资源可做空实现)。
3. 用 `[PluginSetting]` 标注需要出现在设置页的属性(见下)。
4. `dotnet build` 后,把 **`你的插件.dll`** 复制到部署目录 `plugins\`(自带第三方依赖 DLL 也放这里;有 `.deps.json` 一并带上可自动解析依赖路径)。
5. 重启 APet。加载发生在启动时,**更新 DLL 后需重启生效**。

完整可编译示例:`plugins-sample/HelloPlugin/`(设置控件 + `$` 指令 + 运行收尾钩子 + 会话事件 + 元数据 + 前端数据面 + 自定义 UI/宿主 DOM)。

## [PluginSetting] 参数

| 参数 | 说明 |
|---|---|
| `Name`(构造参数) | 设置名(唯一键);留空用属性名。查询不分大小写 |
| `Label` | 显示名(如中文标签);留空时设置页以 Name 充当 |
| `Group` | 分组名;设置页按组排版,留空归「常规」 |
| `Description` | 说明文本,显示在控件下方 |
| `Min` / `Max` | 数字的取值范围(校验失败拒绝保存) |
| `MinLength` / `MaxLength` | 文本长度 / 列表元素数限制 |
| `Options` | string 类型的可选项(渲染为下拉,值必须在选项内);枚举属性自动取枚举名 |
| `UiHint` | 控件提示:`slider`(数字滑条)/ `color`(取色器)/ `password`(掩码)/ `textarea`(多行) |

**控件映射**:bool → 复选框;int/double → 数字输入(或 UiHint=slider);string+Options → 下拉;`List<string>` 等 → JSON 文本域;其他 string → 文本框;枚举 → 下拉(值为枚举名)。

## $ 聊天指令

```csharp
public override IReadOnlyList<PluginChatCommand> GetChatCommands() =>
[
    new PluginChatCommand("hello", "描述", TakesArgument: false,
        (sessionId, argument) => "给用户看的反馈文本;返回 null 表示无反馈"),
];
```

指令名以 `$` 前缀呈现;输入框以 `/` 开头且无匹配内置命令时,宿主也会回退匹配插件指令(不带前缀,按 Name 匹配)。

## 工具插件(IsTool)

文本工具:

```csharp
public override bool IsTool => true;
public override ToolDefinition ToolDefinition => new() { Name = "my_tool", Description = "...", Parameters = ... };
public override Task<string> ExecuteToolAsync(string argumentsJson, CancellationToken ct = default) => ...;
```

多模态工具(截图/看图类,结果携带图片回传视觉模型)——重写富入口而非字符串版:

```csharp
public override Task<ToolResult> ExecuteToolResultAsync(string argumentsJson, CancellationToken ct = default) =>
    Task.FromResult(new ToolResult { Text = "截屏完成", ImageDataUrls = [dataUrl] });
```

字符串版 `ExecuteToolAsync` 是富入口的默认回退(自动包装 `ToolResult.FromText`),旧插件不受影响。
插件启用即工具上线,禁用即下线;鉴权走统一管线(`AnalyzeToolEffect` 可覆写以声明文件/目录影响)。

## 生命周期与顺序

- **注册顺序**:`LoadOrder` 数字小者先注册(默认 0,同数按 Name 升序)。需要依赖其他插件先完成初始化时,用本属性声明顺序。
- **初始化**:`Initialize()`(同步,UI/启动线程,不得阻塞)→ `InitializeAsync()`(框架 await;启动路径后台执行不阻塞启动,Web 启用端点会 await 到完成)。用于建连接/加载模型等无法同步完成的预热。快速反复启停时,禁用会放弃尚未完成的初始化——钩子须容忍"尚未就绪"状态。
- **销毁**:`Dispose()` → `DisposeAsync()`(宿主运行时销毁/退出时 await)。

## 运行收尾与生命周期钩子

```csharp
public override Task OnRunFinishedAsync(Agent agent, AgentSession session, RunOutcome outcome)
{
    // outcome.Kind: Completed / Cancelled / Failed(失败时 outcome.Error 携带异常消息)
    // 运行已告一段落;此处异常仅记日志,不得依赖它影响运行,也不得阻塞
    return Task.CompletedTask;
}

public override void OnSessionEvent(Agent agent, AgentSession session, SessionEventKind kind)
{
    // kind: Created / Removed;不得阻塞,异常仅记日志
}
```

## 前端数据面

```csharp
public override bool ProvidesData => true;

public override object? GetPluginData(string? sessionId) =>
    new { count = 42, label = "任意可 JSON 序列化形状" }; // null = 无数据

public override object? HandlePluginData(string? sessionId, string? body) =>
    new { ok = true }; // body = 请求体原始 JSON 文本
```

- 侧边栏"插件"页签列出 `ProvidesData = true` 的启用插件,按既有节奏(数秒)轮询 `GetPluginData`。
- REST:`GET/POST /api/agents/{agentId}/plugins/{pluginName}/data?sessionId=…`(sessionId 可省略 = Agent 级)。
- 数据变更时发布 `PluginEvents.Publish(session, "plugin_data_changed", …)`,前端随既有刷新节奏重拉。
- 宿主以 UI 线程派发,**不得阻塞**。
- **底部状态栏约定**:`GetPluginData` 返回对象含 `status` **字符串**字段时,其值显示在该 Agent 聊天窗底部状态栏右侧(约 2.5s 轮询)。适合一行短实时信息,如 GPU 插件的 `status = "12% · 3.1/8.0 GB · 45°C"`;省略或为 null 则不显示。短、单行——长内容请放侧边栏页签。

## 前端事件推送(PluginEvents)

```csharp
PluginEvents.Publish(session, "my_event", new { status = "done" });   // 会话级 SSE
PluginEvents.PublishAgent(AgentId, "agent_status", new { … });        // Agent 级 SSE(AgentId 由宿主注入)
```

事件帧与会话流其余事件同形 `{"type":"…","data":{…}}`;无订阅者时返回 false;任何线程可调用,发布不阻塞。

## 宿主配置视图(AgentConfigView)

插件能读到本 Agent 宿主配置(AgentConfig)上**被宿主显式标注**的属性。宿主侧在属性上标注
`[PluginVisible("key")]` 即开放(这是宿主的决策,插件无法自行扩大可见范围):

```csharp
// 插件侧读取(宿主注入到 AgentPluginBase.AgentConfigView,编译期只依赖框架):
var language = AgentConfigView?.Get("language") as string;   // 当前值,每次调用活读
var json     = AgentConfigView?.GetJson("…");                // 复杂值:JSON 文本
var keys     = AgentConfigView?.Keys ?? Enumerable.Empty<string>(); // 全部可见键名
```

- **活读**:设置页保存后就地修改同一配置实例,插件下次读取即得新值,无需重注册。
- **键名是对外契约**:被使用过的键名不得改名;当前开放的键:`language`(Agent 语言:zh / ja / en)。
- 未注入(极早期构造时序)或未知键返回 null;值类型未知,请按 `object?` 取用(常用 `as string` / `GetJson`)。
- 插件自身 `[PluginSetting]` 的值与本视图互不替代:前者是插件私有设置(设置页持久化),后者是宿主级共享配置。

## 自定义 UI(界面级插件)

外部插件可以携带**自己的前端界面**(任意 Web 技术:Vue 构建产物、原生 JS、单文件页面都行),占用宿主定义的 **UI 插槽** 渲染,并通过桥读写设置/数据面/事件、乃至**直接修改宿主 DOM** 做界面级扩展。

### 机制总览

```
插件 DLL(内嵌 ui\*.* 资源) ──► 宿主 GET …/external-plugins/{name}/ui/{path} 伺服
                                              │
                          同源 <iframe> 渲染(设置页模态 / 侧边栏页签)
                                              │
              插件页 <script src="/apet-bridge.js?plugin=插件Name"> 获得 window.APetPlugin
                                              │
      数据面 fetch · 设置读写 fetch · 事件 EventSource(SSE) · 宿主 DOM 直接访问(同源)
```

- **资产自包含**:UI 文件以 DLL **内嵌资源** 形式发布(csproj 里 `<EmbeddedResource Include="ui\**\*" />`),插件 = 一个 DLL,更新即替换。
- **同源隔离**:iframe 与宿主同域(共享 REST/SSE),但崩了不带走主应用;样式经主题 token 对齐。
- **宿主 DOM 能力**:桥暴露完整 `document`(同源),插件可以注入/修改任意宿主节点;宿主提供**命名锚点** 与 **mark-and-sweep 回收**。

### 三个步骤

1. **声明插槽**(基类元数据,逗号分隔;空 = 无):
   ```csharp
   public override string UiSlots => "settings,sidebar";
   ```
   - `settings`——设置页卡片的"设置"按钮**直接**打开你的 UI(一个入口:插件带 UI 资产走你的界面,不带则走自动生成的通用表单)。
   - `sidebar`——侧边栏为本插件**单开一个页签**(替代 JSON 数据区;`GetPluginData` 仍可用作其数据源)。
2. **放 UI 文件**:工程里建 `ui\index.html`(及任意子目录资产),csproj 加:
   ```xml
   <ItemGroup><EmbeddedResource Include="ui\**\*" /></ItemGroup>
   ```
   入口固定 `index.html`;子资源按相对路径引用(`assets/app.js` → 资源名 `ns.ui.assets.app.js`)。
3. **引用桥**:插件页头部(自身脚本之前):
   ```html
   <script src="/apet-bridge.js?plugin=你的插件Name"></script>
   ```
   `agentId/sessionId/slot` 由 iframe URL 查询参数给出(桥自动读取),无需硬编码。

### 桥 API(window.APetPlugin)

```js
ap.name / ap.agentId / ap.sessionId / ap.slot     // 上下文
ap.getSettings()                                   // GET 本插件 [PluginSetting] 值
ap.setSettings(obj)                                // PUT 本插件设置(走框架校验)
ap.data(sessionId)                                 // GET 数据面 GetPluginData
ap.invoke(sessionId, body)                         // POST 数据面 HandlePluginData(任意 JSON)
ap.events(sessionId)                               // 会话级 EventSource(SSE 流,含 PluginEvents 帧)
ap.agentEvents()                                   // Agent 级 EventSource
// ── 宿主 DOM(同源完整访问)──
ap.dom.document                                    // 宿主 document(直接 querySelector/appendChild…)
ap.dom.anchor('app-root'|'sidebar'|'chat'|'chat-header'|'messages'|'composer'|'settings')  // 命名锚点元素
ap.dom.track(el)                                   // 标记注入节点(打 data-APet-plugin="Name")
ap.dom.adoptStyle(cssText)                         // 注入 <style>(带标记,自动回收)
```

**回收约定**:插件注入宿主 DOM 的节点/样式请经 `track`/`adoptStyle`(或直接打 `data-APet-plugin="Name"` 属性)。插件**禁用/重置**、模态关闭、页签消失、页面刷新时,宿主按标记**批量移除** 该插件的一切注入——不做标记的注入会残留,请务必标记。

### 示例

`plugins-sample/HelloPlugin/` 的 `ui\index.html`(单文件、无构建):
- `slot=settings`:自定义设置表单(问候语/风格/音量)+ "向宿主注入浮动徽章" 按钮(演示 `anchor('app-root')` + `track` + `adoptStyle`)。
- `slot=sidebar`:运行结局统计(数据面)+ 每轮 `plugin_data_changed` 事件实时刷新。

### 常驻宿主小部件(页签之外的持续 UI)

侧边栏 UI 页是**保活**的(页签 `v-show`,侧边栏收起亦不卸载),因此声明 `sidebar` 的插件页
可以维护**页签之外**的宿主区域 UI,例如"聊天输入框下方的简化实时显示"(见示例 NvidiaMonitor):

```js
function ensureWidget() {
  var anchor = ap.anchor('composer');            // 输入卡锚点
  if (!anchor) { widget = null; return; }         // 聊天视图卸载 → 小部件随子树销毁
  if (!widget || !widget.isConnected) {           // 每 tick 检测断连 → 重新注入
    var st = ap.adoptStyle('.gpu-widget{…}'); st.id = 'gpu-widget-style'; // 样式打标记,防重复
    widget = ap.document.createElement('div');
    widget.className = 'gpu-widget';
    ap.track(widget);
    anchor.insertAdjacentElement('afterend', widget);  // 输入框下方
  }
}
```

要点:锚点消失(视图切换)时节点随子树销毁,每 tick 重新检测并注入;节点与样式务必打标记
(`track` / `adoptStyle` 自动完成),插件禁用或页面卸载时宿主 mark-and-sweep 自动回收。
插件 UI 页在**会话切换时重载**(iframe 地址含 sessionId),重载后按上述模式重建即可。

### 边界与注意

- 桥是宿主**静态文件**(`web/public/apet-bridge.js`,随宿主更新),插件页不要打包自己的副本——避免版本漂移。
- iframe 内无法访问宿主 JS 运行时(只共享 DOM/网络);需要 .NET 侧行为请用数据面 `invoke` 或插件自身 .NET 代码。
- 宿主换肤时 token 会实时重推;插件页若覆写了 token,需自行响应 `ap.syncTokens`。
- 插槽页签数量 = 声明 `sidebar` 的启用插件数,侧边栏会随之变宽,插件生态里请克制。

## 行为细节

- **校验**:设置页保存走 `TrySetSettings` 批量校验——先全部校验后一次应用,任一项失败整批拒绝并显示原因,不会出现半保存状态;`OnSettingChanged(name)` 会被逐项触发(仅真正变化的项),可在此做重连/重建等副作用。
- **持久化**:`agents/{id}/setting.json` 的 `pluginSettings` 节(键 = 插件 Name);启动时灌回实例。设置页"重置"按钮:销毁实例 + 清空持久化袋,以默认值重建(保留启停状态)。
- **启停**:卡片开关经 `PluginRegistration.SetEnabled` 驱动 `Initialize`/`Dispose`;启用且重写了 `InitializeAsync` 时,Web 端点 await 到就绪。
- **同名冲突**:插件 `Name` 与内置插件重名时注册失败(记日志,不影响其余插件)。
- **依赖**:不要携带 `APet.Framework.dll` 副本进 plugins 目录(宿主强制共享宿主版本);第三方依赖放插件目录或随 `.deps.json` 解析。
- **加载失败重试**:加载失败的 DLL 不会缓存为"永久失败"——宿主每次 Discover 都会重试(例如补上缺失的第三方依赖 DLL 后重启即可,无需改码)。
- **容错**:单插件的 `GetSystemPromptSegment` / `GetChatCommands` / `GetPluginData` 抛异常只记日志并跳过该项,不影响其余插件与主流程;消息链钩子(`OnBeforeRequest` 等)抛异常则按框架语义中断本轮运行。

## 已知边界(当前版本)

- 外部插件的工具不出现在「工具」页的开关列表(与内置动态插件工具一致),启停请用插件卡片开关。
- 不支持运行期卸载/热重载 DLL——更新插件后重启(加载失败的 DLL 会在下次启动重试)。
- 内置插件(如 TTS/BiliLive)部分设置的 Web 保存热推以设置页为准;外部插件走独立校验端点,**保存即生效**,不受此影响。
