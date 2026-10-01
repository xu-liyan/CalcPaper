/* ==========================================================================
   计算稿纸 · 应用内确认弹窗（confirm.js）
   --------------------------------------------------------------------------
   · 纯 JavaScript（ES5 风格：只用 var 与 function），零依赖，不使用 ES Module
   · 加载顺序：bridge.js → calc-engine.js → app.js → tooltip.js → confirm.js
               → persist.js → settings.js
   · 职责：替代宿主原生 MessageBox，提供应用内确认弹窗（删除稿纸等）
       1) 结构与视觉完全复用设置面板（.settings-card），窗口正中，无遮罩
       2) 交互：Esc 取消、点击卡片外部取消（都不改变稿纸）；Tab 在两按钮间切换；
          默认焦点在「取消」；只有点确认按钮或聚焦它后回车才执行
       3) 对外 API：window.ConfirmBox = { open, close, isOpen }
          open(message, onConfirm, title)：旧式，title 可选，给出时改写弹窗标题；
          open({ title, message, cancelText, confirmText, onCancel, onConfirm })：
            新式，可自定义两个按钮文案，并把「取消」按钮挂上独立回调（如「另存为」）。
            两种写法并存，旧调用完全兼容（按钮文案回落为 取消 / 确认删除）。
     · 与设置面板互斥：打开确认弹窗前先关闭设置面板，避免两个浮层重叠
   ========================================================================== */

(function (root) {
  'use strict';

  var doc = root.document || null;

  /* 弹窗节点（init 时获取） */
  var card = null;          /* #confirm-card */
  var titleEl = null;       /* #confirm-title */
  var messageEl = null;     /* #confirm-message */
  var cancelBtn = null;     /* #confirm-cancel */
  var confirmBtn = null;    /* #confirm-confirm */

  /* 当前是否展开 */
  var opened = false;
  /* 待执行的回调（同一时刻最多一个） */
  var callback = null;
  /* 「取消」按钮的回调（可选；旧式调用不提供时为 null） */
  var onCancelCallback = null;

  /* 两个按钮的默认文案：旧式调用未指定按钮文案时使用 */
  var DEFAULT_CANCEL_TEXT = '取消';
  var DEFAULT_CONFIRM_TEXT = '确认删除';

  /* ======================================================================
     一、小工具
     ====================================================================== */

  /* target 是否位于 node 之内（不用 Element.contains，兼容性更好） */
  function isInside(node, target) {
    var cur = target;
    while (cur) {
      if (cur === node) return true;
      cur = cur.parentNode || null;
    }
    return false;
  }

  function consume(event) {
    if (event) {
      if (event.preventDefault) event.preventDefault();
      if (event.stopPropagation) event.stopPropagation();
    }
  }

  /* ======================================================================
     二、显隐
     ====================================================================== */

  /* 打开弹窗。两种调用方式（向后兼容）：
       1) 旧式：open(message, onConfirm, title)   —— 删除稿纸等既有场景
       2) 新式：open({ title, message, cancelText, confirmText, onCancel, onConfirm })
     新式的 onConfirm 对应「确认」按钮，onCancel 对应「取消」按钮（可承载「另存为」
     等次要动作）；Esc / 点击卡片外部只收起弹窗，不触发任何回调。 */
  function open(a, b, c) {
    if (!card) return;

    /* 与设置面板互斥：先收起设置面板，避免两个浮层同时显示 */
    if (root.SettingsPanel && root.SettingsPanel.close) root.SettingsPanel.close();

    var opts;
    if (a && typeof a === 'object') opts = a;
    else opts = { message: a, onConfirm: b, title: c };

    onCancelCallback = (typeof opts.onCancel === 'function') ? opts.onCancel : null;
    callback = (typeof opts.onConfirm === 'function') ? opts.onConfirm : null;

    if (titleEl && opts.title !== undefined && opts.title !== null && opts.title !== '') {
      titleEl.textContent = String(opts.title);
    }
    if (messageEl && opts.message !== undefined && opts.message !== null) {
      messageEl.textContent = String(opts.message);
    }
    setButtonText(cancelBtn, opts.cancelText, DEFAULT_CANCEL_TEXT);
    setButtonText(confirmBtn, opts.confirmText, DEFAULT_CONFIRM_TEXT);

    opened = true;
    card.hidden = false;

    /* 默认焦点放在「取消」上 */
    if (cancelBtn && cancelBtn.focus) {
      try { cancelBtn.focus(); } catch (e) { /* 忽略 */ }
    }
  }

  /* 设置按钮文案：给了非空值用给定文案，否则恢复该按钮的默认文案
     （保证「旧式调用」下两个按钮始终回到 取消 / 确认删除） */
  function setButtonText(btn, text, fallback) {
    if (!btn) return;
    btn.textContent = (text !== undefined && text !== null && text !== '') ? String(text) : fallback;
  }

  /* 关闭：不执行任何回调（取消 / Esc / 点击外部） */
  function close() {
    opened = false;
    callback = null;
    onCancelCallback = null;
    if (card) card.hidden = true;
  }

  /* 确认：先取出回调，关闭后可再调用（回调里可能再次操作 DOM） */
  function accept() {
    var cb = callback;
    close();
    if (cb) cb();
  }

  /* 「取消」按钮：与 Esc / 点击外部不同，这里会执行 onCancel（若提供） */
  function cancelAccept() {
    var cb = onCancelCallback;
    close();
    if (cb) cb();
  }

  /* ======================================================================
     三、事件
     ====================================================================== */

  function onDocumentKeyDown(event) {
    if (!opened) return;
    if (event && event.key === 'Escape') {
      consume(event);
      close();
    }
  }

  function onDocumentMouseDown(event) {
    if (!opened) return;
    var target = event ? event.target : null;
    if (card && isInside(card, target)) return;    /* 卡片内部：交给按钮自己处理 */
    close();                                       /* 点击外部即取消 */
  }

  /* 按钮上的回车 / 空格：只有聚焦在确认按钮上回车才执行 */
  function bindButtonActivate(btn, handler) {
    if (!btn || !btn.addEventListener) return;
    btn.addEventListener('click', handler);
    btn.addEventListener('keydown', function (event) {
      var key = event ? event.key : null;
      if (key === 'Enter' || key === ' ' || key === 'Spacebar') {
        consume(event);
        handler();
      }
    });
  }

  /* ======================================================================
     四、初始化
     ====================================================================== */

  function init() {
    if (!doc) return;

    card = doc.getElementById('confirm-card');
    if (!card) return;   /* 页面上没有该浮层：不做任何事 */

    messageEl = doc.getElementById('confirm-message');
    titleEl = doc.getElementById('confirm-title');
    cancelBtn = doc.getElementById('confirm-cancel');
    confirmBtn = doc.getElementById('confirm-confirm');

    bindButtonActivate(cancelBtn, function () { cancelAccept(); });
    bindButtonActivate(confirmBtn, function () { accept(); });

    if (doc.addEventListener) {
      doc.addEventListener('keydown', onDocumentKeyDown);
      doc.addEventListener('mousedown', onDocumentMouseDown);
    }
  }

  /* ======================================================================
     五、对外 API
     ====================================================================== */

  root.ConfirmBox = {
    open: open,
    close: close,
    isOpen: function () { return opened; }
  };

  if (doc && doc.readyState === 'loading') {
    doc.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

})(typeof window !== 'undefined' ? window : this);