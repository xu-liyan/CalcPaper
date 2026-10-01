/*
 * bridge.js —— 「计算稿纸」宿主桥（前端侧）
 *
 * 作用：当页面运行在 .NET/WinForms + WebView2 宿主中时，提供与宿主通信的统一入口；
 *       在普通浏览器中打开时（设计稿预览）本文件完全惰性，不影响现有交互。
 *
 * ── 消息协议（后续任务在此基础上扩展）────────────────────────────────
 * JS → C#  （通过 window.chrome.webview.postMessage 发送）
 *   { cmd: 'window', action: 'drag' }                                    // 拖动窗口
 *   { cmd: 'window', action: 'minimize' }                                // 最小化
 *   { cmd: 'window', action: 'close' }                                   // 关闭
 *   { cmd: 'window', action: 'resize', edge: 'r|b|br' }                 // 边缘缩放（仅下/右/右下角）
 *   { cmd: 'window', action: 'pin', value: true|false }                 // 窗口置顶 / 取消置顶
 *   { cmd: 'ready' }                                                     // 页面就绪
 *   { cmd: 'ping' }                                                      // 连通性探测
 *   { cmd: 'setSettings', angleMode, useGrouping, startOnBoot, globalHotkey, menuOrder }
 *                                                                        // menuOrder：运算菜单顺序（稳定 id 数组）
 *   （后续任务扩展：saveAs / open / autosave / getPaper / getSettings / confirm）
 *
 * C# → JS  （通过 CoreWebView2.PostWebMessageAsJson 推送，按 type 分发）
 *   { type: 'pong' }
 *   { type: 'error', message: '...' }
 *   { type: 'pinned', value: true|false }                                // 窗口置顶状态（按钮视觉据此更新）
 *   { type: 'settings', angleMode, useGrouping, startOnBoot, globalHotkey, menuOrder }
 *                                                                        // menuOrder 为 null 表示无该字段（用默认顺序）
 *   （后续任务扩展：paper / saved / loaded 等）
 * ────────────────────────────────────────────────────────────────
 */
(function () {
  'use strict';

  /* 边缘热区宽度（px）：光标进入该范围才视为缩放区 */
  var EDGE_BAND = 7;

  /* 仅「下边线 / 右边线 / 右下角」是缩放热区，其余边缘与四角一律禁用。
     原因（已定决策）：拖上/左边线会移动窗口原点，而合成器当帧仍是「旧原点 + 旧尺寸」，
     内容会整体平移一下，表现为剧烈抖动（Chromium 行为，宿主侧无法消除）；
     拖下/右/右下角不改变原点，抖动平缓可接受。
     故此处只保留 b / r / br 三条热区，对应光标如下。 */
  var CURSOR_BY_EDGE = {
    r: 'ew-resize',
    b: 'ns-resize',
    br: 'nwse-resize'
  };

  /* 已订阅的宿主消息处理器：{ type: [handler, ...] } */
  var subscribers = Object.create(null);

  function webview() {
    return (window.chrome && window.chrome.webview) || null;
  }

  function isAvailable() {
    return !!webview();
  }

  /* 发送 {cmd, ...payload}；宿主不存在时静默忽略（保证浏览器预览不受影响） */
  function post(cmd, payload) {
    var wv = webview();
    if (!wv) return;

    var message = { cmd: cmd };
    if (payload && typeof payload === 'object') {
      for (var key in payload) {
        if (Object.prototype.hasOwnProperty.call(payload, key)) {
          message[key] = payload[key];
        }
      }
    }

    try {
      wv.postMessage(message);
    } catch (err) {
      /* 发送失败不影响页面其它功能 */
    }
  }

  /* 订阅宿主消息，返回取消订阅函数；同一 type 支持多个订阅者 */
  function on(type, handler) {
    if (typeof handler !== 'function') return function () {};

    var list = subscribers[type] || (subscribers[type] = []);
    list.push(handler);

    return function off() {
      var index = list.indexOf(handler);
      if (index >= 0) list.splice(index, 1);
    };
  }

  /* 按 type 分发一条宿主消息 */
  function dispatch(message) {
    if (!message || typeof message.type !== 'string') return;

    var list = subscribers[message.type];
    if (!list) return;

    for (var i = 0; i < list.length; i++) {
      try {
        list[i](message);
      } catch (err) {
        /* 单个订阅者异常不影响其它订阅者 */
      }
    }
  }

  /* ── 边缘判定与光标反馈 ────────────────────────────────── */

  function edgeAt(x, y) {
    var width = window.innerWidth;
    var height = window.innerHeight;

    var left = x <= EDGE_BAND;
    var right = x >= width - EDGE_BAND;
    var top = y <= EDGE_BAND;
    var bottom = y >= height - EDGE_BAND;

    /* 四角优先判定：只有右下角是缩放热区；左上/右上/左下角返回 null（禁用） */
    if (bottom && right) return 'br';
    if ((top && left) || (top && right) || (bottom && left)) return null;

    /* 只有右边线与下边线是缩放热区；左边线与上边线返回 null（禁用） */
    if (right) return 'r';
    if (bottom) return 'b';
    return null;
  }

  function setEdgeCursor(edge) {
    document.documentElement.style.cursor = edge ? CURSOR_BY_EDGE[edge] : '';
  }

  /* ── 宿主中的行为接线（浏览器里不生效）────────────────────── */

  function wireWindowControls() {
    var titlebar = document.getElementById('titlebar');
    if (titlebar) {
      titlebar.addEventListener('mousedown', function (event) {
        if (event.button !== 0) return;
        /* 标题栏右侧的窗口按钮不参与拖动 */
        if (event.target && event.target.closest && event.target.closest('.win-btn')) return;
        /* 缩放热区优先于标题栏拖动：右侧热区会与标题栏右端重叠，落在热区内时放行，
           交给 wireEdgeResize 发 resize，避免同一次按下既发 drag 又发 resize。
           注意：上边线与左上/右上角已不是热区，顶部约 6px 带在此处返回 null，
           因此标题栏任意位置（含最顶部带与左右两端）都能正常拖动窗口。 */
        if (edgeAt(event.clientX, event.clientY)) return;
        post('window', { action: 'drag' });
      });
    }

    var buttons = document.querySelectorAll('.win-btn');
    /* 最小化按钮：显式类名定位（标题栏按钮组顺序为 置顶 / 最小化 / 关闭，
       不能再靠"第一个 .win-btn"来识别；保留旧回退以防旧标记） */
    var minimizeButton = document.querySelector('.win-btn--min') || buttons[0];
    if (minimizeButton) {
      minimizeButton.addEventListener('click', function () {
        post('window', { action: 'minimize' });
      });
    }

    var closeButton = document.querySelector('.win-btn--close');
    if (closeButton) {
      closeButton.addEventListener('click', function () {
        post('window', { action: 'close' });
      });
    }

    wirePin();
  }

  /* ── 窗口置顶按钮 ──────────────────────────────────────────
     点击 → 取反后立即更新按钮视觉并发送 window/pin；宿主处理 TopMost 后会回发
     {type:'pinned', value}（含「最小化 / 关闭时自动取消」的状态同步），据此复位。 */
  var pinned = false;

  function applyPinnedState(value) {
    pinned = !!value;
    var btn = document.querySelector('.win-btn--pin');
    if (!btn) return;

    if (pinned) {
      btn.classList.add('is-pinned');
      btn.setAttribute('aria-pressed', 'true');
      btn.setAttribute('aria-label', '取消置顶');
      btn.setAttribute('data-tip', '取消置顶');
    } else {
      btn.classList.remove('is-pinned');
      btn.setAttribute('aria-pressed', 'false');
      btn.setAttribute('aria-label', '窗口置顶');
      btn.setAttribute('data-tip', '窗口置顶');
    }
  }

  function wirePin() {
    var btn = document.querySelector('.win-btn--pin');
    if (!btn) return;

    btn.addEventListener('click', function () {
      var next = !pinned;
      applyPinnedState(next);   // 页面自己触发的操作：立即更新视觉
      post('window', { action: 'pin', value: next });
    });
  }

  function wireEdgeResize() {
    document.addEventListener('mousemove', function (event) {
      setEdgeCursor(edgeAt(event.clientX, event.clientY));
    });

    document.addEventListener('mouseleave', function () {
      setEdgeCursor(null);
    });

    document.addEventListener('mousedown', function (event) {
      if (event.button !== 0) return;

      var edge = edgeAt(event.clientX, event.clientY);
      if (!edge) return;   /* 正文区域按下时不拦截，保证输入框等正常使用 */

      event.preventDefault();
      post('window', { action: 'resize', edge: edge });
    });
  }

  function listenHostMessages() {
    var wv = webview();
    if (!wv || typeof wv.addEventListener !== 'function') return;

    wv.addEventListener('message', function (event) {
      var data = event.data;

      /* PostWebMessageAsJson 通常直接给对象；兼容字符串形式 */
      if (typeof data === 'string') {
        try {
          data = JSON.parse(data);
        } catch (err) {
          return;
        }
      }

      dispatch(data);
    });
  }

  function boot() {
    /* 浏览器中（非宿主）不添加 app-mode、不接线，保持设计稿预览原样 */
    if (!isAvailable()) return;

    if (document.body) document.body.classList.add('app-mode');

    listenHostMessages();
    wireWindowControls();
    wireEdgeResize();

    /* 宿主回发的置顶状态（含最小化/关闭时自动取消）→ 更新按钮视觉 */
    on('pinned', function (message) {
      applyPinnedState(message ? message.value : false);
    });

    post('ready');
  }

  window.Host = {
    isAvailable: isAvailable,
    post: post,
    on: on
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();