/* APet 插件 UI 桥(宿主同源静态文件,无版本耦合;更新宿主即更新桥)。
 * 插件页引用方式(脚本 src 的查询参数注入插件名):
 *   <script src="/apet-bridge.js?plugin=插件Name"></script>
 * agentId/sessionId/slot 来自 iframe 自身 URL 的查询参数(宿主 PluginUiFrame 生成)。
 * 窗口对象:window.APetPlugin
 */
(function () {
  var scriptSrc = (document.currentScript && document.currentScript.src) || '';
  var scriptParams = new URLSearchParams(scriptSrc.split('?')[1] || '');
  var locParams = new URLSearchParams(location.search);
  var name = scriptParams.get('plugin') || locParams.get('plugin') || '';
  var agentId = locParams.get('agentId') || scriptParams.get('agentId') || '';
  var sessionId = locParams.get('sessionId') || scriptParams.get('sessionId') || '';
  var slot = locParams.get('slot') || scriptParams.get('slot') || '';
  var parent = window.parent;
  var doc = parent === window ? document : parent.document;

  // ── 主题 token:加载时把宿主 :root 的 CSS 变量快照到插件页 :root(宿主换肤时由 PluginUiFrame 重推) ──
  var TOKENS = [
    "--bg-base", "--bg-panel", "--bg-panel-hover", "--bg-panel-translucent", "--bg-input", "--bg-code", "--bg-tool",
    "--border", "--fg-primary", "--fg-secondary", "--fg-muted", "--fg-accent", "--accent", "--accent-sub", "--accent-sub-bg",
    "--danger", "--danger-bg", "--danger-border", "--success", "--warning", "--selection", "--gutter-bg", "--code-fg", "--font",
    "--radius-panel", "--radius-bubble"
  ];
  function syncTokens() {
    if (!doc) return;
    var root = getComputedStyle(doc.documentElement);
    var mine = document.documentElement;
    for (var i = 0; i < TOKENS.length; i++) {
      var v = root.getPropertyValue(TOKENS[i]);
      if (v) mine.style.setProperty(TOKENS[i], v);
    }
  }
  syncTokens();

  // ── 标记:注入宿主 DOM 的节点/样式打 data-APet-plugin 标记;插件禁用/页面卸载时宿主批量回收 ──
  function tracked(el) { el.setAttribute('data-APet-plugin', name); return el; }

  window.APetPlugin = {
    name: name,
    agentId: agentId,
    sessionId: sessionId,
    slot: slot,

    // ── 数据面(REST;端点返回 {name, data} 信封,桥按契约拆出 data = GetPluginData/HandlePluginData 返回值) ──
    /** GET 数据面:ap.data(sessionId?) → GetPluginData 返回 */
    data: function (sid) {
      var s = sid === undefined ? sessionId : sid;
      return fetch('/api/agents/' + agentId + '/plugins/' + encodeURIComponent(name) + '/data?sessionId=' + encodeURIComponent(s || ''))
        .then(function (r) {
          return r.ok ? r.json().then(function (j) { return (j && j.data !== undefined) ? j.data : j; }) : null;
        });
    },
    /** POST 数据面自定义动作:ap.invoke(body, sessionId?) → HandlePluginData 返回 */
    invoke: function (body, sid) {
      var s = sid === undefined ? sessionId : sid;
      return fetch('/api/agents/' + agentId + '/plugins/' + encodeURIComponent(name) + '/data?sessionId=' + encodeURIComponent(s || ''), {
        method: 'POST', body: body === null || body === undefined ? '' : JSON.stringify(body)
      }).then(function (r) {
        return r.ok ? r.json().then(function (j) { return (j && j.data !== undefined) ? j.data : j; }) : null;
      });
    },
    /** 读本插件 [PluginSetting] 描述符与当前值 */
    settings: function () {
      return fetch('/api/agents/' + agentId + '/external-plugins').then(function (r) {
        return r.json().then(function (list) {
          var p = list.find(function (x) { return x.name === name; });
          return p ? p.settings : [];
        });
      });
    },
    /** 保存设置(整份 values 对象,经框架批量校验) */
    saveSettings: function (values) {
      return fetch('/api/agents/' + agentId + '/external-plugins/' + encodeURIComponent(name) + '/settings', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values)
      }).then(function (r) { return r.json(); });
    },

    // ── 事件(宿主 SSE,同源直连;会话级或 Agent 级) ──
    events: function (sid) {
      var s = sid === undefined ? sessionId : sid;
      var url = s ? '/api/agents/' + agentId + '/sessions/' + encodeURIComponent(s) + '/stream'
                  : '/api/agents/' + agentId + '/stream';
      return new EventSource(url);
    },

    // ── 宿主 DOM(同源完整访问;注入的节点/样式务必用 track/adoptStyle 标记以便回收) ──
    /** window.parent 本身:拿到即完整 DOM 自由(请克制) */
    host: parent,
    get document() { return doc; },
    /** 宿主命名锚点:ap.anchor('app-root' | 'sidebar' | 'chat' | 'chat-header' | 'messages' | 'composer' | 'settings') */
    anchor: function (n) { return doc.querySelector('[data-APet-anchor="' + n + '"]'); },
    /** 向宿主 head 注入样式(自动打标记,插件卸载时移除) */
    adoptStyle: function (css) {
      var s = doc.createElement('style');
      tracked(s);
      s.textContent = css;
      doc.head.appendChild(s);
      return s;
    },
    /** 标记注入宿主的节点(卸载时按标记批量移除) */
    track: tracked,
    untrack: function (el) { el.removeAttribute('data-APet-plugin'); },
    /** 主题 token 重推(宿主换肤时 PluginUiFrame 也会自动推) */
    syncTokens: syncTokens
  };
})();
