/* ==========================================================================
   计算稿纸 · 设置浮层（settings.js）
   --------------------------------------------------------------------------
   · 纯 JavaScript（ES5 风格：只用 var 与 function），零依赖，不使用 ES Module
   · 加载顺序固定为：bridge.js → calc-engine.js → app.js → tooltip.js → persist.js → settings.js
   · 职责（Task 6）：
       1) 统一接线底部「设置」按钮（persist.js 不再接线该按钮）
       2) 浮层显隐：按钮切换展开/收起、Esc 收起、点击面板外部收起
       3) 设置项回显与交互：
            · 三角函数：角度 / 弧度
            · 结果千分位：开关
            · 开机自启动：开关
            · 全局快捷键：单值录制（点击胶囊→按键→Esc 取消 / Delete 清空）+ 重置
       4) 角度制 / 千分位：本地立即 App.applySettings 并通知宿主持久化；
          开机自启动：只通知宿主，界面状态以宿主回发的真实状态为准（失败会回滚）；
          全局快捷键：本地先回显 + 通知宿主持久化，冲突错误（hotkeyError）以红字提示
       5) 宿主不可用（普通浏览器打开设计稿）：面板仍能打开/查看/切换，也仍可录制
          快捷键，但只作用于本地、不发宿主消息；开机自启动开关呈不可用观感
   ========================================================================== */

(function (root) {
  'use strict';

  var doc = root.document || null;
  var Host = root.Host || null;
  var App = root.App || null;
  var Persist = root.Persist || null;

  /* 面板与各控件（init 时获取） */
  var panel = null;                 /* #settings-card */
  var trigger = null;               /* [data-action="settings"] */
  var closeButton = null;           /* #settings-close */
  var angleOptions = [];            /* [{ el, value }]：角度 / 弧度两个分段 */
  var groupingSwitch = null;        /* #settings-grouping */
  var bootSwitch = null;            /* #settings-boot */
  var hotkeyPill = null;            /* #settings-hotkey-key */
  var hotkeyReset = null;           /* #settings-hotkey-reset */
  var hotkeyErrorEl = null;         /* #settings-hotkey-error */
  var hotkeyHintEl = null;          /* #settings-hotkey-hint */

  /* 当前设置快照：打开面板时用于回显，并随宿主回发持续更新 */
  var current = {
    angleMode: 'deg',
    useGrouping: true,
    startOnBoot: false,
    globalHotkey: '',
    hotkeyError: ''
  };

  /* 面板是否处于展开状态 */
  var opened = false;

  /* 快捷键录制状态 */
  var recording = false;
  var recordingHandler = null;
  /* 双击识别：上一次按下的键与时间戳 */
  var lastKey = null;
  var lastKeyTime = 0;
  /* 双击判定的时间窗（毫秒） */
  var DOUBLE_TAP_MS = 400;
  /* 录制轻提示的自动消失计时器 */
  var hintTimer = null;

  /* ======================================================================
     一、小工具
     ====================================================================== */

  /* 宿主（WebView2）是否可用 */
  function hostReady() {
    return !!(Host && Host.isAvailable && Host.isAvailable());
  }

  /* target 是否位于 node 之内（不用 Element.contains，兼容性更好） */
  function isInside(node, target) {
    var cur = target;
    while (cur) {
      if (cur === node) return true;
      cur = cur.parentNode || null;
    }
    return false;
  }

  /* 轻量提示：借用底部状态文字（非错误态），不弹任何对话框 */
  function hint(text) {
    if (Persist && Persist.refreshStatus) Persist.refreshStatus(text, false);
  }

  /* 安全地挂点击事件 */
  function onClick(el, handler) {
    if (el && el.addEventListener) el.addEventListener('click', handler);
  }

  /* 开关（.switch）的键盘可达：Enter / 空格 等同点击 */
  function onSwitchKey(toggle) {
    return function (event) {
      var key = event ? event.key : null;
      if (key === 'Enter' || key === ' ' || key === 'Spacebar') {
        if (event.preventDefault) event.preventDefault();
        toggle();
      }
    };
  }

  /* ======================================================================
     二、渲染（把 current 回显到控件）
     ====================================================================== */

  function renderSwitch(el, on) {
    if (!el) return;
    if (on) el.classList.add('is-on');
    else el.classList.remove('is-on');
    el.setAttribute('aria-checked', on ? 'true' : 'false');
  }

  function render() {
    /* 角度 / 弧度高亮 */
    for (var i = 0; i < angleOptions.length; i++) {
      var opt = angleOptions[i];
      if (opt.value === current.angleMode) opt.el.classList.add('is-on');
      else opt.el.classList.remove('is-on');
    }

    /* 千分位开关 */
    renderSwitch(groupingSwitch, current.useGrouping === true);

    /* 开机自启动开关（+ 宿主不可用时的弱化态） */
    renderSwitch(bootSwitch, current.startOnBoot === true);
    if (bootSwitch) {
      if (hostReady()) bootSwitch.classList.remove('is-disabled');
      else bootSwitch.classList.add('is-disabled');
    }

    /* 全局快捷键胶囊（录制中不覆盖其临时文案）；
       空值显示为「未设置」 */
    if (hotkeyPill && !recording) {
      hotkeyPill.textContent = current.globalHotkey ? current.globalHotkey : '未设置';
    }

    /* 快捷键冲突提示（红字） */
    renderHotkeyError();
  }

  /* 冲突提示：有 hotkeyError 显示红字，否则清除 */
  function renderHotkeyError() {
    if (!hotkeyErrorEl) return;
    var msg = current.hotkeyError ? String(current.hotkeyError) : '';
    if (msg) {
      hotkeyErrorEl.textContent = msg;
      hotkeyErrorEl.hidden = false;
    } else {
      hotkeyErrorEl.textContent = '';
      hotkeyErrorEl.hidden = true;
    }
  }

  /* ======================================================================
     三、展开 / 收起
     ====================================================================== */

  function openPanel() {
    if (!panel) return;
    opened = true;
    panel.hidden = false;

    /* 先用本地快照回显，再向宿主拉一次最新设置（保证与注册表实际状态一致） */
    render();
    if (Persist && Persist.requestSettings) Persist.requestSettings();
  }

  function closePanel() {
    if (!panel) return;
    if (recording) stopRecording();
    hideRecordHint();
    opened = false;
    panel.hidden = true;
  }

  function togglePanel() {
    if (opened) closePanel();
    else openPanel();
  }

  /* ======================================================================
     四、三项设置交互
     ====================================================================== */

  /* 角度制 / 弧度制：本地立即生效 + 通知宿主持久化 */
  function chooseAngle(mode) {
    if (mode !== 'deg' && mode !== 'rad') return;
    current.angleMode = mode;
    if (App && App.applySettings) App.applySettings({ angleMode: mode });
    if (hostReady() && Host.post) Host.post('setSettings', { angleMode: mode });
    render();
  }

  /* 结果千分位：本地立即生效 + 通知宿主持久化 */
  function toggleGrouping() {
    var next = !(current.useGrouping === true);
    current.useGrouping = next;
    if (App && App.applySettings) App.applySettings({ useGrouping: next });
    if (hostReady() && Host.post) Host.post('setSettings', { useGrouping: next });
    render();
  }

  /* 开机自启动：只通知宿主；不在本地先行改状态，等宿主回发实际状态再回显。
     失败时宿主会回发实际状态（回滚）并用 error 提示。 */
  function toggleBoot() {
    if (!hostReady()) {
      hint('开机自启动仅在应用内可用');
      return;
    }
    if (Host && Host.post) {
      Host.post('setSettings', { startOnBoot: !(current.startOnBoot === true) });
    }
  }

  /* ======================================================================
     四之二、全局快捷键（单值：组合键 或 双击键）
     ----------------------------------------------------------------------
     · 面板上只有一个可录制的取值胶囊：组合键（如 Alt+C）、双击键（如 双击 Alt），
       或空值时的「未设置」
     · 点击胶囊进入录制态：
         - 修饰键 + 主键 → 组合键串（Ctrl/Alt/Shift/Win 之一 + 主键）
         - 400ms 内连按两次同一「修饰键 / 不参与打字的键（F1–F12、CapsLock、Tab）」
           → 双击键
         - 只按普通字母 / 数字且无修饰键 → 拒绝，保持录制并给 2 秒轻提示
         - Esc 取消（不发消息）；Backspace / Delete 清空（发空值）
     · 完成后通知宿主 setSettings.globalHotkey；宿主回发后以宿主为准
     · 宿主不可用（浏览器预览）：仍可录制与显示，只更新本地、不发宿主消息
     ====================================================================== */

  /* 是否为纯修饰键（按下它本身不足以构成组合键） */
  function isModifierKey(key) {
    return key === 'Control' || key === 'Alt' || key === 'Shift' ||
           key === 'Meta' || key === 'AltGraph' || key === 'OS';
  }

  /* 是否为"不参与打字"的功能键：F1–F12 / CapsLock / Tab */
  function isDoubleTapKey(key) {
    if (key === 'CapsLock' || key === 'Tab') return true;
    return /^F([1-9]|1[0-2])$/.test(key ? String(key) : '');
  }

  /* 修饰键的显示短名（用于双击键文案） */
  function modifierLabel(key) {
    if (key === 'Control') return 'Ctrl';
    if (key === 'Meta' || key === 'OS') return 'Win';
    if (key === 'AltGraph') return 'AltGr';
    return key;
  }

  /* 双击键的显示文案：如「双击 Alt」「双击 F5」「双击 CapsLock」「双击 Tab」 */
  function doubleTapLabel(key) {
    var name = isModifierKey(key) ? modifierLabel(key) : key;
    return '双击 ' + name;
  }

  /* 主键名归一：单字符大写（c → C）；少数常用键映射为短名 */
  function mainKeyName(key) {
    if (!key) return '';
    if (key.length === 1) return key.toUpperCase();
    var map = {
      ' ': 'Space', 'Spacebar': 'Space',
      'ArrowUp': 'Up', 'ArrowDown': 'Down', 'ArrowLeft': 'Left', 'ArrowRight': 'Right'
    };
    return map[key] ? map[key] : key;
  }

  /* 事件是否携带至少一个修饰键 */
  function hasModifier(event) {
    return !!(event && (event.ctrlKey || event.altKey || event.shiftKey || event.metaKey));
  }

  /* 由修饰键状态 + 主键名拼出组合键字符串（形如 Ctrl+Alt+K、Alt+C） */
  function buildCombo(event, name) {
    var parts = [];
    if (event.ctrlKey) parts.push('Ctrl');
    if (event.altKey) parts.push('Alt');
    if (event.shiftKey) parts.push('Shift');
    if (event.metaKey) parts.push('Win');
    parts.push(name);
    return parts.join('+');
  }

  function consume(event) {
    if (event) {
      if (event.preventDefault) event.preventDefault();
      if (event.stopPropagation) event.stopPropagation();
    }
  }

  /* 当前时间戳（毫秒）；不用 Date.now，兼容旧引擎 */
  function nowMs() {
    return (new Date()).getTime();
  }

  /* 录制轻提示：胶囊下方一行淡红小字，2 秒后自动消失 */
  function showRecordHint(text) {
    if (!hotkeyHintEl) return;
    hotkeyHintEl.textContent = text;
    hotkeyHintEl.hidden = false;
    if (hintTimer) { clearTimeout(hintTimer); hintTimer = null; }
    hintTimer = setTimeout(function () {
      hintTimer = null;
      if (hotkeyHintEl) { hotkeyHintEl.textContent = ''; hotkeyHintEl.hidden = true; }
    }, 2000);
  }

  function hideRecordHint() {
    if (hintTimer) { clearTimeout(hintTimer); hintTimer = null; }
    if (hotkeyHintEl) { hotkeyHintEl.textContent = ''; hotkeyHintEl.hidden = true; }
  }

  /* 开始录制：胶囊显示「按下快捷键…」，document 捕获阶段监听 keydown */
  function startRecording() {
    if (recording || !doc) return;
    recording = true;
    lastKey = null;
    lastKeyTime = 0;
    hideRecordHint();
    if (hotkeyPill) {
      hotkeyPill.textContent = '按下快捷键…';
      hotkeyPill.classList.add('is-recording');
    }
    recordingHandler = function (event) { onRecordingKeyDown(event); };
    /* 捕获阶段：先于面板的 Esc 收起逻辑拿到按键 */
    doc.addEventListener('keydown', recordingHandler, true);
  }

  /* 结束录制（只解绑监听与样式，不改动 current） */
  function stopRecording() {
    if (!recording) return;
    recording = false;
    if (recordingHandler && doc && doc.removeEventListener) {
      doc.removeEventListener('keydown', recordingHandler, true);
    }
    recordingHandler = null;
    lastKey = null;
    lastKeyTime = 0;
    if (hotkeyPill) hotkeyPill.classList.remove('is-recording');
  }

  /* 录制中的按键处理 */
  function onRecordingKeyDown(event) {
    if (!recording) return;
    var key = event ? event.key : null;
    if (!key) { consume(event); return; }

    /* Esc：取消录制（不发任何消息，胶囊恢复原值） */
    if (key === 'Escape') {
      consume(event);
      stopRecording();
      hideRecordHint();
      render();
      return;
    }

    /* Backspace / Delete：清空（通知宿主空值，胶囊显示「未设置」） */
    if (key === 'Backspace' || key === 'Delete') {
      consume(event);
      clearHotkey();
      return;
    }

    var hasMod = hasModifier(event);

    /* 修饰键 / 不参与打字的键（F1–F12、CapsLock、Tab）：
       带其它修饰键时按组合键处理（如 Ctrl+F5）；否则用于双击识别 */
    if (isModifierKey(key) || isDoubleTapKey(key)) {
      if (hasMod && !isModifierKey(key)) {
        consume(event);
        finishRecording(buildCombo(event, mainKeyName(key)));
        return;
      }
      consume(event);
      var t = nowMs();
      if (lastKey === key && (t - lastKeyTime) <= DOUBLE_TAP_MS) {
        finishRecording(doubleTapLabel(key));
        return;
      }
      lastKey = key;
      lastKeyTime = t;
      return;
    }

    /* 普通字母 / 数字 / 其它可打印键：必须有修饰键，否则拒绝并轻提示 */
    var name = mainKeyName(key);
    if (!name) { consume(event); return; }
    if (!hasMod) {
      consume(event);
      showRecordHint('需配合修饰键，或双击修饰键/功能键');
      return;
    }
    consume(event);
    finishRecording(buildCombo(event, name));
  }

  /* 完成录制：记录取值、清除错误与轻提示、通知宿主 */
  function finishRecording(value) {
    stopRecording();
    hideRecordHint();
    current.globalHotkey = value;
    current.hotkeyError = '';
    render();
    if (hostReady() && Host.post) Host.post('setSettings', { globalHotkey: value });
  }

  /* 清空：记录空值、清除错误、通知宿主 */
  function clearHotkey() {
    stopRecording();
    hideRecordHint();
    current.globalHotkey = '';
    current.hotkeyError = '';
    render();
    if (hostReady() && Host.post) Host.post('setSettings', { globalHotkey: '' });
  }

  /* 重置为默认快捷键 Alt+C */
  function resetHotkey() {
    if (recording) stopRecording();
    hideRecordHint();
    current.globalHotkey = 'Alt+C';
    current.hotkeyError = '';
    render();
    if (hostReady() && Host.post) Host.post('setSettings', { globalHotkey: 'Alt+C' });
  }

  /* ======================================================================
     五、宿主设置消息（订阅 Persist.onSettings）
     ====================================================================== */

  function onSettings(msg) {
    if (!msg) return;

    if (msg.angleMode === 'deg' || msg.angleMode === 'rad') current.angleMode = msg.angleMode;
    if (msg.useGrouping === true || msg.useGrouping === false) current.useGrouping = msg.useGrouping;
    /* 开机自启动以宿主回发的实际状态为准（注册表真实状态） */
    if (msg.startOnBoot === true || msg.startOnBoot === false) current.startOnBoot = msg.startOnBoot;

    /* 全局快捷键：以宿主回发为准（含空串 → 「未设置」） */
    if (typeof msg.globalHotkey === 'string') current.globalHotkey = msg.globalHotkey;
    /* 冲突提示：有则红字，宿主未回发（消失）则清除 */
    current.hotkeyError = msg.hotkeyError ? String(msg.hotkeyError) : '';

    render();
  }

  /* ======================================================================
     六、全局收起：Esc / 点击面板外部
     ====================================================================== */

  function onDocumentKeyDown(event) {
    if (!opened) return;
    if (event && event.key === 'Escape') {
      closePanel();
      if (event.preventDefault) event.preventDefault();
      if (event.stopPropagation) event.stopPropagation();
    }
  }

  function onDocumentMouseDown(event) {
    if (!opened) return;
    var target = event ? event.target : null;
    if (panel && isInside(panel, target)) return;       /* 面板内部：不收起 */
    if (trigger && isInside(trigger, target)) return;   /* 设置按钮：交给它自己切换 */
    closePanel();
  }

  /* ======================================================================
     七、初始化
     ====================================================================== */

  function bindAngleOption(opt) {
    onClick(opt.el, function () { chooseAngle(opt.value); });
  }

  function init() {
    if (!doc) return;

    panel = doc.getElementById('settings-card');
    if (!panel) return;   /* 页面上没有设置浮层：不做任何事 */

    trigger = doc.querySelector('[data-action="settings"]');
    closeButton = doc.getElementById('settings-close');
    groupingSwitch = doc.getElementById('settings-grouping');
    bootSwitch = doc.getElementById('settings-boot');
    hotkeyPill = doc.getElementById('settings-hotkey-key');
    hotkeyReset = doc.getElementById('settings-hotkey-reset');
    hotkeyErrorEl = doc.getElementById('settings-hotkey-error');
    hotkeyHintEl = doc.getElementById('settings-hotkey-hint');

    angleOptions = [];
    var degEl = doc.getElementById('settings-angle-deg');
    var radEl = doc.getElementById('settings-angle-rad');
    if (degEl) angleOptions.push({ el: degEl, value: 'deg' });
    if (radEl) angleOptions.push({ el: radEl, value: 'rad' });

    /* 触发按钮 / 关闭按钮 */
    onClick(trigger, function () { togglePanel(); });
    onClick(closeButton, function () { closePanel(); });

    /* 角度 / 弧度 */
    for (var i = 0; i < angleOptions.length; i++) bindAngleOption(angleOptions[i]);

    /* 两个开关 */
    onClick(groupingSwitch, function () { toggleGrouping(); });
    onClick(bootSwitch, function () { toggleBoot(); });
    if (groupingSwitch && groupingSwitch.addEventListener) {
      groupingSwitch.addEventListener('keydown', onSwitchKey(toggleGrouping));
    }
    if (bootSwitch && bootSwitch.addEventListener) {
      bootSwitch.addEventListener('keydown', onSwitchKey(toggleBoot));
    }

    /* 全局快捷键组：取值录制 + 重置 */
    onClick(hotkeyPill, function () { startRecording(); });
    onClick(hotkeyReset, function () { resetHotkey(); });

    /* 全局收起 */
    if (doc.addEventListener) {
      doc.addEventListener('keydown', onDocumentKeyDown);
      doc.addEventListener('mousedown', onDocumentMouseDown);
    }

    /* 跟随宿主回发的真实设置 */
    if (Persist && Persist.onSettings) Persist.onSettings(onSettings);

    /* 初始化回显（此时面板默认隐藏） */
    render();
  }

  /* ======================================================================
     八、对外 API（供其它任务或测试调用）
     ====================================================================== */

  root.SettingsPanel = {
    open: openPanel,
    close: closePanel,
    toggle: togglePanel
  };

  if (doc && doc.readyState === 'loading') {
    doc.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

})(typeof window !== 'undefined' ? window : this);