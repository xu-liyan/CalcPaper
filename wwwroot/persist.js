/* ==========================================================================
   计算稿纸 · 持久化调度（persist.js）
   --------------------------------------------------------------------------
   · 纯 JavaScript（ES5 风格：只用 var 与 function），零依赖，不使用 ES Module
   · 加载顺序固定为：bridge.js → calc-engine.js → app.js → persist.js → settings.js
   · 职责（Task 5）：
       1) 启动恢复：先取设置（角度制 / 千分位）应用到界面，再取稿纸内容
       2) 内容变更 → 防抖 700ms 自动保存；状态文字即时反馈
       3) 底部按钮接线：另存稿纸 / 加载稿纸 / 新建稿纸 / 删除稿纸
          （「设置」按钮改由 settings.js 统一接线）
       4) 对外暴露 window.Persist，供设置面板（settings.js）复用
   · 「新建稿纸」先发 unbindFile（宿主把 lastFilePath 置空）再新建成临时稿纸，
     确保新内容只写进 autosave.json，绝不改写之前已命名的文件
   · 「删除稿纸」仅在已绑定文件时可用：未绑定时按钮呈禁用外观（aria-disabled，
     不用原生 disabled，悬停气泡仍可见），点击弹应用内确认，确认后发 deletePaper
   · 宿主不可用（普通浏览器里看设计稿）时：只定义 API，并接线「新建 / 删除稿纸」
     这两个入口，其余宿主相关接线、状态文字保持设计稿原样
   ========================================================================== */

(function (root) {
  'use strict';

  var doc = root.document || null;
  var Host = root.Host || null;
  var App = root.App || null;

  /* 只有真正运行在 WebView2 宿主里才做持久化 */
  var hostReady = !!(Host && Host.isAvailable && Host.isAvailable());

  /* 自动保存防抖间隔（毫秒） */
  var AUTOSAVE_DELAY = 700;

  /* 防抖计时器 */
  var saveTimer = null;

  /* 当前已绑定的稿纸文件路径；未绑定为 null（自动保存落到 autosave.json） */
  var boundPath = null;

  /* 「删除稿纸」按钮（data-action="deletePaper"） */
  var deleteBtn = null;

  /* 未绑定（临时稿纸）时的悬停提示：按钮仍可悬停，只是点击不生效。
     临时稿纸没有可删除的文件，改为引导用户用「新建稿纸」清空内容。 */
  var TIP_DELETE = '删除稿纸';
  var TIP_DELETE_DISABLED = '如需清空内容，请点击左侧“新建稿纸”';

  /* 程序化载入内容时，抑制这一次的自动保存（刚恢复/刚加载无需立刻回写） */
  var suppressAutosave = false;

  /* 「另存为」是新建确认弹窗里的入口：置真后等 {type:'saved', ok:true} 到达再执行新建；
     保存对话框里取消（canceled）或保存失败（ok:false）则放弃该状态。 */
  var pendingNewAfterSave = false;

  /* 设置订阅者（供设置面板等后续任务复用） */
  var settingsHandlers = [];

  /* 启动恢复是否仍在等待设置返回 */
  var restorePending = true;

  /* ======================================================================
     一、状态提示
     ====================================================================== */

  function statusBox() {
    return doc ? doc.querySelector('.toolbar__status') : null;
  }

  function statusTextNode() {
    return doc ? doc.querySelector('.toolbar__status .status-text') : null;
  }

  /* 只改文字、不动颜色：用于「正在保存…」，避免抹掉上一次的错误红点 */
  function setStatusText(text) {
    var node = statusTextNode();
    if (node) node.textContent = text;
  }

  /* 临时提示（flash）用：气泡期间记住的正式状态 */
  var flashState = null;
  var flashTimer = null;

  /* 改文字并切换错误态（错误为红点红字，正常为绿点）
     说明：一旦有正式状态更新（refreshStatus 被调用），立即结束任何临时提示，
     以最新状态为准。 */
  function refreshStatus(text, isError) {
    if (flashTimer) {
      clearTimeout(flashTimer);
      flashTimer = null;
    }
    flashState = null;

    var node = statusTextNode();
    if (node && text !== null && text !== undefined) node.textContent = text;

    var box = statusBox();
    if (box) {
      if (isError) box.classList.add('is-error');
      else box.classList.remove('is-error');
    }
  }

  /* 短暂显示一段提示（如「已复制 4175270」），到点恢复为原来的正式状态。
     · 首次调用时记录当前正式状态（文字 + 是否错误态）
     · 期间若有正式状态变化（refreshStatus / autoSaved 等），以最新状态为准并取消恢复
     · ms 缺省 1800ms */
  function flash(text, ms) {
    var node = statusTextNode();
    if (!node) return;

    if (!flashState) {
      var box = statusBox();
      flashState = {
        text: node.textContent,
        isError: !!(box && box.classList && box.classList.contains('is-error'))
      };
    }
    if (flashTimer) clearTimeout(flashTimer);

    node.textContent = text;
    var liveBox = statusBox();
    if (liveBox) liveBox.classList.remove('is-error');

    var duration = (typeof ms === 'number' && ms > 0) ? ms : 1800;
    flashTimer = setTimeout(function () {
      var restore = flashState;
      flashTimer = null;
      flashState = null;
      if (restore) refreshStatus(restore.text, restore.isError);
    }, duration);
  }

  /* 取文件名（兼容 \ 与 / 两种分隔符） */
  function baseName(path) {
    if (!path) return '';
    var parts = String(path).split(/[\\\/]/);
    var last = parts[parts.length - 1];
    return last ? last : String(path);
  }

  /* 去首尾空白（含全角空格）；不用 String.prototype.trim，兼容旧引擎 */
  function trimText(s) {
    var str = (s === null || s === undefined) ? '' : String(s);
    return str.replace(/^[\s\u3000]+|[\s\u3000]+$/g, '');
  }

  /* ======================================================================
     一之二、当前稿纸文件名行
     ====================================================================== */

  /* 文件名行的统一前缀（需求 2：底栏显示「当前稿纸：<名字>」） */
  var FILENAME_PREFIX = '当前稿纸：';

  function filenameNode() {
    return doc ? doc.querySelector('.toolbar__filename') : null;
  }

  /* 已绑定文件 → 显示「当前稿纸：文件名」（不含路径）；未绑定（临时稿纸）→
     显示「当前稿纸：临时稿纸」。超长文件名由 CSS 以省略号截断，
     完整路径放进悬停提示便于查看（提示同样带「当前稿纸：」前缀）。 */
  function updateFilename() {
    var node = filenameNode();
    if (!node) return;
    if (boundPath) {
      node.textContent = FILENAME_PREFIX + baseName(boundPath);
      if (node.setAttribute) node.setAttribute('data-tip', FILENAME_PREFIX + String(boundPath));
    } else {
      node.textContent = FILENAME_PREFIX + '临时稿纸';
      if (node.removeAttribute) node.removeAttribute('data-tip');
    }
  }

  /* 绑定状态变化的统一刷新入口：删除按钮可用性 + 文件名行。
     调用点与原 updateDeleteButton 完全一致，保证刷新时机相符。 */
  function syncBoundUi() {
    updateDeleteButton();
    updateFilename();
  }

  /* ======================================================================
     二、自动保存
     ====================================================================== */

  /* 立即保存（跳过防抖）——新建 / 清空 / 外部调用时使用 */
  function saveNow() {
    if (!hostReady || !App) return;
    if (saveTimer) {
      clearTimeout(saveTimer);
      saveTimer = null;
    }
    if (Host && Host.post) Host.post('autosave', { paper: App.getPaper() });
  }

  /* 内容变化：立刻提示「正在保存…」，700ms 后写入 */
  function onRowsChanged() {
    if (suppressAutosave) return;
    setStatusText('正在保存…');
    if (saveTimer) clearTimeout(saveTimer);
    saveTimer = setTimeout(function () {
      saveTimer = null;
      saveNow();
    }, AUTOSAVE_DELAY);
  }

  /* ======================================================================
     三、宿主消息处理
     ====================================================================== */

  function onSettingsMessage(msg) {
    if (!msg) return;

    if (App) App.applySettings({ angleMode: msg.angleMode, useGrouping: msg.useGrouping });

    for (var i = 0; i < settingsHandlers.length; i++) {
      try {
        settingsHandlers[i](msg);
      } catch (e) {
        /* 单个订阅者异常不影响其它订阅者 */
      }
    }

    /* 启动恢复第二步：设置就绪后再取稿纸 */
    if (restorePending) {
      restorePending = false;
      if (Host && Host.post) Host.post('getPaper');
    }
  }

  /* 「删除稿纸」按钮可用性：只有绑定了文件才可删；未绑定时给出对应悬停提示。
     使用 aria-disabled（而非原生 disabled），保证禁用态下仍能悬停出气泡。 */
  function updateDeleteButton() {
    if (!deleteBtn) return;
    var bound = !!boundPath;
    deleteBtn.setAttribute('aria-disabled', bound ? 'false' : 'true');
    deleteBtn.setAttribute('data-tip', bound ? TIP_DELETE : TIP_DELETE_DISABLED);
  }

  function onPaperMessage(msg) {
    if (!msg || !App) return;

    boundPath = msg.path ? msg.path : null;
    syncBoundUi();

    suppressAutosave = true;
    try {
      App.setPaper({ rows: msg.rows ? msg.rows : null });
    } finally {
      suppressAutosave = false;
    }

    if (msg.isNew) refreshStatus('新建稿纸', false);
    else if (msg.fromAuto) refreshStatus('已恢复自动保存内容', false);
    else refreshStatus('已加载 ' + baseName(msg.path), false);
  }

  function onAutosavedMessage(msg) {
    if (!msg) return;

    if (msg.ok) {
      if (msg.auto === false && msg.path) {
        boundPath = msg.path;
        syncBoundUi();
      }
      refreshStatus('已自动保存 ' + (msg.at ? msg.at : ''), false);
    } else {
      refreshStatus('保存失败：' + (msg.error ? msg.error : '未知错误'), true);
    }
  }

  function onSavedMessage(msg) {
    if (!msg) return;

    if (msg.ok) {
      boundPath = msg.path ? msg.path : boundPath;
      syncBoundUi();
      refreshStatus('已保存到 ' + baseName(msg.path), false);
      /* 新建确认里的「另存为」：另存成功后继续完成新建（解绑 + 清空 + 落盘） */
      if (pendingNewAfterSave) {
        pendingNewAfterSave = false;
        performNew();
      }
    } else if (msg.canceled) {
      /* 用户取消：保持原状态文字，并放弃挂起的「另存后新建」 */
      pendingNewAfterSave = false;
    } else {
      pendingNewAfterSave = false;
      refreshStatus('保存失败：' + (msg.error ? msg.error : '未知错误'), true);
    }
  }

  function onOpenedMessage(msg) {
    if (!msg || !App) return;

    if (msg.ok) {
      boundPath = msg.path ? msg.path : null;
      syncBoundUi();
      suppressAutosave = true;
      try {
        App.setPaper(msg.paper);
      } finally {
        suppressAutosave = false;
      }
      refreshStatus('已加载 ' + baseName(msg.path), false);
    } else if (msg.canceled) {
      /* 用户取消：保持原状态文字 */
    } else {
      refreshStatus(msg.error ? msg.error : '加载稿纸失败', true);
    }
  }

  /* 解绑回执（新建稿纸的第一步）：清空本地绑定路径，之后自动保存落到 autosave.json */
  function onUnboundMessage(msg) {
    if (!msg || !msg.ok) return;
    boundPath = null;
    syncBoundUi();
  }

  /* 删除回执：成功后清空绑定 → 回到空白临时稿纸并立即落盘到 autosave.json */
  function onDeletedMessage(msg) {
    if (!msg) return;

    if (!msg.ok) {
      refreshStatus('删除失败：' + (msg.error ? msg.error : '未知错误'), true);
      return;
    }
    boundPath = null;
    syncBoundUi();
    if (!App) return;
    App.newPaper();
    saveNow();
  }

  function onHostErrorMessage(msg) {
    if (!msg) return;
    refreshStatus(msg.message ? msg.message : '宿主错误', true);
  }

  /* 双击结果复制：成功提示改由 app.js 在鼠标上方显示「已复制」气泡（Tooltip.flash），
     底部状态栏始终显示自动保存状态；仅复制失败仍用底部状态提示。 */
  function onCopiedMessage(msg) {
    if (!msg) return;
    if (msg.ok) return;
    refreshStatus('复制失败：' + (msg.error ? msg.error : '未知错误'), true);
  }

  /* 宿主完全退出前的落盘请求：立即保存，然后回执 flushed（宿主最多等 1500ms） */
  function onFlushMessage() {
    saveNow();
    if (Host && Host.post) Host.post('flushed');
  }

  /* ======================================================================
     四、底部按钮
     ====================================================================== */

  function onClickSaveAs() {
    if (!App || !Host || !Host.post) return;
    /* 先立即落盘到「当前」绑定文件：避免加载/另存前挂起的防抖自动保存被延迟到
       之后才写入 —— 那时宿主解析到的绑定路径可能已指向新文件，把旧内容回写过去。 */
    saveNow();
    Host.post('saveAs', { paper: App.getPaper() });
  }

  /* 直接进入「加载稿纸」流程（无提醒）：切换文件之前先把当前内容落到原绑定文件，
     清掉待写的防抖计时器，否则延迟写入会以「新路径」落盘，导致刚加载的文件被旧内容覆盖。
     注意：saveNow() → open 的先后顺序不可颠倒。 */
  function performOpen() {
    if (Host && Host.post) {
      saveNow();
      Host.post('open');
    }
  }

  /* 加载稿纸入口：
       · 临时稿纸且确有内容 → 先弹应用内确认（内容未另存，加载后会被清空）
       · 空白临时稿纸 / 已绑定文件 → 保持原有「直接加载」行为，无任何提示 */
  function onClickOpen() {
    if (!boundPath && paperHasContent()) {
      askOpenWithContent();
      return;
    }
    performOpen();
  }

  /* 临时稿纸有内容时的加载确认：「另存为」（次要）/「仍然加载」（危险）。
       点「另存为」→ 只走既有另存流程，不继续加载（与新建提醒「另存成功后自动新建」
       有意不同）；保存对话框里取消则什么都不做。
       点「仍然加载」→ 进入加载流程，仍保持「先落盘再 open」的顺序。 */
  function askOpenWithContent() {
    var box = root.ConfirmBox;
    if (!box || !box.open) { performOpen(); return; }   /* 无弹窗能力：退化为直接加载 */
    box.open({
      title: '加载稿纸',
      message: '当前为临时稿纸，内容未另存，将会被清空！',
      cancelText: '另存为',
      confirmText: '仍然加载',
      onCancel: function () {
        pendingNewAfterSave = false;   /* 只另存、不继续加载 */
        onClickSaveAs();
      },
      onConfirm: function () {
        performOpen();                 /* 保持 saveNow() → open 的顺序 */
      }
    });
  }

  /* 新建稿纸：先解绑当前文件（宿主把 lastFilePath 置空），再新建成空白临时稿纸。
     顺序很重要 —— 先 unbindFile 再 autosave，宿主才会把内容写进 autosave.json，
     绝不会改写之前已命名的文件。 */
  function performNew() {
    if (!App) return;
    if (Host && Host.post) Host.post('unbindFile');
    App.newPaper();
    saveNow();   /* 新建后立即落盘，不等防抖 */
  }

  /* 稿纸内是否有内容（任意非空算式或非空备注）；空白稿纸视为无内容 */
  function paperHasContent() {
    if (!App || !App.getPaper) return false;
    var paper = App.getPaper();
    var list = (paper && paper.rows) ? paper.rows : [];
    for (var i = 0; i < list.length; i++) {
      var r = list[i] || {};
      if (trimText(r.expr) !== '' || trimText(r.note) !== '') return true;
    }
    return false;
  }

  /* 新建稿纸入口：
       · 临时稿纸且确有内容 → 先弹应用内确认（内容会被清空且可能没另存）
       · 空白临时稿纸 / 已绑定文件 → 保持原有「直接新建」行为，无任何提示 */
  function onClickNew() {
    if (!App) return;
    if (!boundPath && paperHasContent()) {
      askNewWithContent();
      return;
    }
    performNew();
  }

  /* 临时稿纸有内容时的新建确认：「另存为」（次要）/「仍然新建」（危险）。
       点「另存为」→ 走既有另存流程，另存成功后自动执行新建；保存对话框里取消则什么都不做。
       点「仍然新建」→ 直接执行新建。 */
  function askNewWithContent() {
    var box = root.ConfirmBox;
    if (!box || !box.open) { performNew(); return; }   /* 无弹窗能力：退化为直接新建 */
    box.open({
      title: '新建稿纸',
      message: '当前为临时稿纸，内容未另存，将会被清空！',
      cancelText: '另存为',
      confirmText: '仍然新建',
      onCancel: function () {
        pendingNewAfterSave = true;   /* 由 onSavedMessage 在保存成功后继续新建 */
        onClickSaveAs();
      },
      onConfirm: function () {
        pendingNewAfterSave = false;
        performNew();
      }
    });
  }

  /* 删除稿纸：仅当已绑定文件时可用（未绑定时按钮 aria-disabled，点击直接返回）。
     用应用内确认弹窗；确认后发 deletePaper，由宿主把文件放入回收站。 */
  function onClickDelete() {
    if (!boundPath) return;                   /* 临时稿纸：没有可删除的文件 */
    var box = root.ConfirmBox;
    if (!box || !box.open) return;
    box.open(
      '确定删除当前稿纸文件吗？文件会被放入回收站（可从回收站恢复），界面将回到临时稿纸。',
      function () {
        if (Host && Host.post) Host.post('deletePaper');
      },
      '删除稿纸'
    );
  }

  /* 「新建 / 删除稿纸」两个入口：任何环境都接线（普通浏览器预览里同样可用）；
     删除事件自身会按绑定状态判断是否需要走宿主。
     注意：「设置」按钮仍由 settings.js 统一接线，这里不重复接线。 */
  function wireLocalButtons() {
    if (!doc) return;
    deleteBtn = doc.querySelector('[data-action="deletePaper"]');

    var handlers = {
      'new': onClickNew,
      'deletePaper': onClickDelete
    };
    for (var action in handlers) {
      if (!Object.prototype.hasOwnProperty.call(handlers, action)) continue;
      var button = doc.querySelector('[data-action="' + action + '"]');
      if (button) button.addEventListener('click', handlers[action]);
    }

    syncBoundUi();   /* 初始：未绑定 → 禁用外观 + 对应悬停提示 + 文件名行「临时稿纸」 */
  }

  /* 必须走宿主的两个动作（另存稿纸 / 加载稿纸）：仅在宿主内接线 */
  function wireHostButtons() {
    if (!doc) return;
    var handlers = {
      'saveAs': onClickSaveAs,
      'open': onClickOpen
    };
    for (var action in handlers) {
      if (!Object.prototype.hasOwnProperty.call(handlers, action)) continue;
      var button = doc.querySelector('[data-action="' + action + '"]');
      if (button) button.addEventListener('click', handlers[action]);
    }
  }

  /* ======================================================================
     五、启动
     ====================================================================== */

  function subscribeHostMessages() {
    if (!Host || !Host.on) return;
    Host.on('settings', onSettingsMessage);
    Host.on('paper', onPaperMessage);
    Host.on('autosaved', onAutosavedMessage);
    Host.on('saved', onSavedMessage);
    Host.on('opened', onOpenedMessage);
    Host.on('unbound', onUnboundMessage);
    Host.on('deleted', onDeletedMessage);
    Host.on('error', onHostErrorMessage);
    Host.on('copied', onCopiedMessage);
    Host.on('flush', onFlushMessage);
  }

  function boot() {
    subscribeHostMessages();
    wireHostButtons();   /* 新建 / 删除稿纸已在早退前由 wireLocalButtons 接线，此处不重复 */

    if (App && App.onRowsChanged) App.onRowsChanged(onRowsChanged);

    /* 启动恢复：先设置后稿纸（保证首次渲染就用对的角度制与千分位） */
    if (Host && Host.post) Host.post('getSettings');
  }

  /* ======================================================================
     六、对外 API（设置面板等后续任务复用）
     ====================================================================== */

  root.Persist = {
    saveNow: saveNow,
    refreshStatus: refreshStatus,
    flash: flash,
    getBoundPath: function () {
      return boundPath;
    },
    onSettings: function (handler) {
      if (typeof handler !== 'function') return function () {};
      settingsHandlers.push(handler);
      return function off() {
        for (var i = 0; i < settingsHandlers.length; i++) {
          if (settingsHandlers[i] === handler) {
            settingsHandlers.splice(i, 1);
            break;
          }
        }
      };
    },
    requestSettings: function () {
      if (Host && Host.post) Host.post('getSettings');
    }
  };

  /* 「新建 / 删除稿纸」不依赖宿主环境即可接线（普通浏览器预览里同样可用） */
  wireLocalButtons();

  /* 宿主不可用（普通浏览器预览）：不再做宿主相关接线、不改状态文字，保持设计稿原样 */
  if (!hostReady || !Host || !App) return;

  if (doc && doc.readyState === 'loading') {
    doc.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }

})(typeof window !== 'undefined' ? window : this);