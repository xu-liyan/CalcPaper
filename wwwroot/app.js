/* ==========================================================================
   计算稿纸 · 条格行模型与键盘交互（app.js）
   --------------------------------------------------------------------------
   · 纯 JavaScript（ES5 之前的风格：只有 var 与 function），零依赖，不使用
     ES Module；脚本加载顺序固定为 bridge.js → calc-engine.js → app.js
   · 职责：
       1) 行数据模型 { expr, note } 与条格的动态渲染（替代设计稿里的静态条格）
       2) 输入实时求值：只更新当前行的结果节点，不整表重渲染（避免光标丢失）
       3) Enter 结算当前行并带入下一行；↑ / ↓ 在条格之间切换焦点
       4) 备注：铅笔入口 → 输入框 → 灰色胶囊
       5) 结果区左键双击：复制纯数值（交给宿主 copyToClipboard，浏览器用剪贴板兜底）；
          有有效结果时结果区带 data-tip="双击复制"，复制成功回包后在鼠标位置
          上方短暂显示"已复制"气泡（见 tooltip.js 的 Tooltip.flash）
       6) 窗口宽度变化时按 rAF 合并重算全部行高（长算式换行数随宽度变化）
       7) 对外 API：window.App（供 Task 5/6 调用）
   · 说明：刻意不使用 Array.prototype.map/forEach/indexOf、String.prototype.trim
     等 ES5 内置方法，以便能用 Windows 自带 cscript（JScript）直接加载校验。
   ========================================================================== */

(function (root) {
  'use strict';

  /* 当前文档（浏览器与 WebView2 宿主共用同一套逻辑） */
  var doc = root.document || null;

  /* 备注入口的铅笔图标：与设计稿 index.html 中的那段 SVG 逐字一致 */
  var PENCIL_SVG = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M4.8 19.2l4.3-1.1 8.9-8.9a1.9 1.9 0 0 0 0-2.7l-.5-.5a1.9 1.9 0 0 0-2.7 0l-8.9 8.9z"/><path d="M13.6 6.8l3.6 3.6"/></svg>';

  /* 普通浏览器（设计稿预览）打开时的示例内容：
     设计稿里的 3 行示例，外加一个空行 —— 第 4 行不预填内容，由「空行 + 聚焦」呈现，
     这样浏览器里仍能看到与设计稿完全一致的视觉。 */
  var SAMPLE_ROWS = [
    { expr: '55+88+7689*543', note: '' },
    { expr: '4270-55+855+5525+88+66+4556+3.25*9825-5875/5+85+5369.55+83.45', note: '备注说明 ABC' },
    { expr: '2^5+888+99x878.467', note: '' },
    { expr: '', note: '' }
  ];

  /* ======================================================================
     一、内部状态
     ====================================================================== */

  var paperEl = null;      /* <main class="paper" id="paper"> */
  var rows = [];           /* 行对象数组：{ expr, note, el, input, result, value, foot, ... } */
  var activeRow = null;    /* 当前聚焦行（同一时刻最多一行） */
  var subscribers = [];    /* onRowsChanged 订阅者 */
  var initialized = false; /* 是否已完成初始化 */

  /* 应用级设置：初始为角度制 + 千分位开启 */
  var settings = { angleMode: 'deg', useGrouping: true };

  /* ======================================================================
     二、小工具（ES3 兼容，避免 ES5 内置方法）
     ====================================================================== */

  /* 去首尾空白（含全角空格）；不用 String.prototype.trim，兼容 JScript */
  function trim(s) {
    var str = (s === null || s === undefined) ? '' : String(s);
    return str.replace(/^[\s\u3000]+|[\s\u3000]+$/g, '');
  }

  function indexOfRow(row) {
    for (var i = 0; i < rows.length; i++) {
      if (rows[i] === row) return i;
    }
    return -1;
  }

  /* 清空一个元素的所有子节点（不依赖 innerHTML 解析） */
  function clearNode(node) {
    if (!node) return;
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  /* 把「行对象」或「行元素」统一解析为行对象（供 setActiveRow / focusRow 复用） */
  function resolveRow(ref) {
    if (!ref) return null;
    if (ref.el) return ref;                       /* 传入的是行对象 */
    for (var i = 0; i < rows.length; i++) {
      if (rows[i].el === ref) return rows[i];     /* 传入的是行元素 */
    }
    return null;
  }

  /* 元素是否带某个类（不依赖 closest，兼容旧引擎） */
  function hasClass(el, name) {
    if (!el) return false;
    if (el.classList && el.classList.contains) return el.classList.contains(name);
    return false;
  }

  /* 备注控件（铅笔入口 / 胶囊 / 编辑输入框），含其内部子节点（如 svg、path）。
     判断方式：从事件目标沿 parentNode 向上找，任一层命中即视为"备注控件内部" */
  var NOTE_CONTROL_CLASSES = ['note-add', 'note-pill', 'note-input'];
  function isNoteControl(node) {
    var el = node;
    while (el) {
      for (var i = 0; i < NOTE_CONTROL_CLASSES.length; i++) {
        if (hasClass(el, NOTE_CONTROL_CLASSES[i])) return true;
      }
      el = el.parentNode;
    }
    return false;
  }

  /* ======================================================================
     三、求值与格式化（薄封装，完全复用 calc-engine.js）
     ====================================================================== */

  /* 当前行算式是否"可带入下一行"：
     有效表达式 → 返回不带千分位的数值文本；空表达式或错误 → 返回 null */
  function nextInputFromExpr(expr, options) {
    var angleMode = (options && options.angleMode) ? options.angleMode : settings.angleMode;
    var evaluated = root.CalcEngine.evaluate(expr, { angleMode: angleMode });
    if (!evaluated.ok) return null;
    return root.CalcEngine.formatNumber(evaluated.value, { useGrouping: false, angleMode: angleMode });
  }

  /* 供结果区显示的展示信息（带 "=" 之外的一切由结果节点承担） */
  function formatForRow(expr) {
    return root.CalcEngine.formatResult(expr, { angleMode: settings.angleMode, useGrouping: settings.useGrouping });
  }

  /* 双击结果区：复制"纯数值"（去掉千分位与 "="）。
     仅当该行有有效结果时才复制（空行 / 错误态不复制）；
     不阻止浏览器默认的文本选择行为。
     宿主可用时交给宿主写剪贴板，否则用 navigator.clipboard 兜底（失败静默）。
     宿主回包 { type:'copied', ok:true } 时，在双击处的鼠标位置上方提示"已复制"
     （见 flashCopied）。 */
  function copyResult(row) {
    if (!row || !row.input) return;
    var text = nextInputFromExpr(row.input.value, settings);
    if (text === null) return;                            /* 空行 / 错误态：不复制 */

    if (root.Host && root.Host.post && root.Host.isAvailable && root.Host.isAvailable()) {
      root.Host.post('copyToClipboard', { text: text });
      return;
    }
    var clipboard = root.navigator ? root.navigator.clipboard : null;
    if (clipboard && clipboard.writeText) {
      try {
        var p = clipboard.writeText(text);
        if (p && p['catch']) p['catch'](function () { /* 静默 */ });
      } catch (e) { /* 静默 */ }
    }
  }

  /* 最近一次双击复制的鼠标位置（供 copied 回包定位"已复制"气泡） */
  var lastCopyPoint = null;

  /* 复制成功：鼠标上方短暂显示"已复制"，1.5s 后自动消失。
     坐标优先用双击位置；缺失时 Tooltip.flash 会退回最近一次鼠标位置。 */
  function flashCopied() {
    if (!root.Tooltip || !root.Tooltip.flash) return;
    var x = lastCopyPoint ? lastCopyPoint.x : null;
    var y = lastCopyPoint ? lastCopyPoint.y : null;
    root.Tooltip.flash('已复制', x, y, 1500);
  }

  function onCopiedMessage(message) {
    if (!message || !message.ok) return;   /* 失败提示仍由 persist.js 走底部状态 */
    flashCopied();
  }

  /* ======================================================================
     四、数据模型
     ====================================================================== */

  /* 纯数据行：{ expr, note } */
  function plainRow(expr, note) {
    return {
      expr: (expr === undefined || expr === null) ? '' : String(expr),
      note: (note === undefined || note === null) ? '' : String(note)
    };
  }

  /* 内部行对象：在纯数据行基础上挂上 DOM 引用与备注编辑态 */
  function makeRow(expr, note) {
    var r = plainRow(expr, note);
    r.el = null;             /* <section class="row"> */
    r.input = null;          /* <textarea class="row__expr"> */
    r.result = null;         /* <div class="row__result"> */
    r.value = null;          /* <span class="row__value"> */
    r.foot = null;           /* <div class="row__foot"> */
    r.editingNote = false;   /* 是否正在编辑备注 */
    r.noteInput = null;      /* 备注输入框 */
    r.noteBeforeEdit = '';   /* 编辑前的备注（用于取消） */
    return r;
  }

  /* 规整外部传入的稿纸对象：至少保留 1 行，字段统一为字符串 */
  function sanitizePaper(paper) {
    var out = [];
    var list = (paper && paper.rows) ? paper.rows : null;
    if (list && list.length) {
      for (var i = 0; i < list.length; i++) {
        var item = list[i] || {};
        out.push(plainRow(item.expr, item.note));
      }
    }
    if (out.length === 0) out.push(plainRow('', ''));
    return { rows: out };
  }

  /* ======================================================================
     五、渲染
     ====================================================================== */

  /* textarea 随内容自动增高（长算式自动换行，不出现内部滚动条）。
   注意：字宽固定、可用宽度变化时换行数会变，故窗口宽度变化时必须重算
   （见 scheduleReflow）。若此刻元素尚未完成布局（clientWidth 为 0，例如
   宿主刚创建 WebView2 / 窗口刚从最小化恢复），按"最后一字符一行"量出的
   scrollHeight 会是畸形巨值并被永久保留；此时改为 height:auto 交给浏览器
   自适应，等下一次重算再落定。 */
  function autoGrow(row) {
    var el = row.input;
    if (!el || !el.style) return;
    if (el.clientWidth === 0) {
      el.style.height = 'auto';
      return;
    }
    el.style.height = 'auto';
    el.style.height = (el.scrollHeight || 0) + 'px';
  }

  /* 重算全部行高（只写 height，不触碰会引起滚动的属性） */
  function reflowAllRows() {
    for (var i = 0; i < rows.length; i++) autoGrow(rows[i]);
  }

  /* 把"宽度变化后的行高重算"合并到下一帧：拖动缩放时每帧只重算一次，
     且不在 resize 事件里直接读写布局，避免抖动。 */
  var reflowFrame = null;
  function scheduleReflow() {
    if (reflowFrame) return;
    if (root.requestAnimationFrame) {
      reflowFrame = root.requestAnimationFrame(function () {
        reflowFrame = null;
        reflowAllRows();
      });
    } else {
      reflowFrame = setTimeout(function () {
        reflowFrame = null;
        reflowAllRows();
      }, 16);
    }
  }

  /* 只更新该行的结果节点（含"双击复制"提示的开关） */
  function syncRowResult(row) {
    if (!row.result || !row.value) return;
    var r = formatForRow(row.input.value);
    row.result.classList.remove('row__result--error');
    if (r.ok) {
      row.result.classList.remove('row__result--idle');
      row.value.textContent = r.text;
      /* 有有效结果才挂"双击复制"提示（空行与错误态不显示） */
      if (row.result.setAttribute) row.result.setAttribute('data-tip', '双击复制');
    } else if (r.error === 'empty') {
      /* 空表达式：结果区只显示 "=" */
      row.result.classList.add('row__result--idle');
      row.value.textContent = '';
      if (row.result.removeAttribute) row.result.removeAttribute('data-tip');
    } else {
      /* 语法/数学错误：显示错误文案并进入错误态（同时退出 idle，避免 "=" 仍是浅灰） */
      row.result.classList.remove('row__result--idle');
      row.result.classList.add('row__result--error');
      row.value.textContent = r.text;
      if (row.result.removeAttribute) row.result.removeAttribute('data-tip');
    }
  }

  /* 刷新某行的占位文字：仅当该行是"当前行（active）"且内容为空时显示 */
  function updatePlaceholder(row) {
    if (!row.input) return;
    var isActive = (activeRow === row);
    var empty = (row.input.value === '');
    row.input.placeholder = (isActive && empty) ? '计算公式' : '';
  }

  /* 设置当前行（高亮的唯一权威入口，保证"始终恰好一行 active"）：
       1) 给目标行加 row--active，移除其它行的 row--active；
       2) 刷新目标行的占位文字，并保证其它行占位都为空。
     说明：高亮与占位都不依赖 focus / focusout 事件 —— 窗口未获得系统焦点时
     （document.hasFocus() 为 false）这些事件并不可靠，只靠事件会出现
     "第 4 行没有高亮、placeholder 为空" 的缺陷。 */
  function setActiveRow(ref) {
    var targetRow = resolveRow(ref);                 /* 兼容传入行对象或行元素 */
    var targetEl = targetRow ? targetRow.el : null;
    var previous = activeRow;
    var index = -1;
    for (var i = 0; i < rows.length; i++) {
      var el = rows[i].el;
      if (!el) continue;
      if (targetEl && el === targetEl) { el.classList.add('row--active'); index = i; }
      else { el.classList.remove('row--active'); }
    }
    activeRow = (index >= 0) ? rows[index] : null;
    for (var j = 0; j < rows.length; j++) updatePlaceholder(rows[j]);

    /* 切换活动行时顺带规整：若上一个活动行已被清空（且不是末尾那行空白），
       离开它即清理掉。normalizeBlankRows 不会删除新的活动行，故不会递归。 */
    if (previous && previous !== activeRow &&
        isBlankRow(previous) && indexOfRow(previous) !== rows.length - 1) {
      normalizeBlankRows();
    }
  }

  /* 备注区：空备注 → 铅笔按钮；非空备注 → 灰色胶囊 */
  function renderFoot(row) {
    clearNode(row.foot);
    if (trim(row.note) !== '') {
      var pill = doc.createElement('button');
      pill.type = 'button';
      pill.className = 'note-pill';
      pill.setAttribute('data-tip', '点击编辑备注');
      pill.setAttribute('aria-label', '点击编辑备注');
      pill.textContent = row.note;
      pill.addEventListener('click', function () { startNoteEdit(row, true); });
      row.foot.appendChild(pill);
    } else {
      var add = doc.createElement('button');
      add.type = 'button';
      add.className = 'note-add';
      add.setAttribute('data-tip', '添加备注');
      add.setAttribute('aria-label', '添加备注');
      add.innerHTML = PENCIL_SVG;
      add.addEventListener('click', function () { startNoteEdit(row, false); });
      row.foot.appendChild(add);
    }
  }

  /* 构建一条格的 DOM 并接线事件（结构严格对齐设计稿） */
  function buildRowEl(row) {
    var section = doc.createElement('section');
    section.className = 'row';

    var input = doc.createElement('textarea');
    input.className = 'row__expr';
    input.rows = 1;
    input.spellcheck = false;
    input.value = row.expr;

    var result = doc.createElement('div');
    result.className = 'row__result';
    var eq = doc.createElement('span');
    eq.className = 'row__eq';
    eq.textContent = '=';
    var value = doc.createElement('span');
    value.className = 'row__value';
    result.appendChild(eq);
    result.appendChild(value);

    var foot = doc.createElement('div');
    foot.className = 'row__foot';

    section.appendChild(input);
    section.appendChild(result);
    section.appendChild(foot);

    row.el = section;
    row.input = input;
    row.result = result;
    row.value = value;
    row.foot = foot;

    /* 算式输入框 */
    input.addEventListener('input', function () { onInput(row); });
    input.addEventListener('keydown', function (event) { onKeyDown(row, event); });
    /* 失焦：做一次空白行规整（整行被删空后，离开它即清理） */
    input.addEventListener('blur', function (event) { onRowBlur(row, event); });

    /* 结果区：左键双击复制纯数值（不 preventDefault，保留默认文本选择）。
       记下双击处的鼠标位置：宿主回包成功时用它定位"已复制"气泡。 */
    result.addEventListener('dblclick', function (event) {
      var x = event ? event.clientX : null;
      var y = event ? event.clientY : null;
      lastCopyPoint = (typeof x === 'number' && isFinite(x) &&
                       typeof y === 'number' && isFinite(y)) ? { x: x, y: y } : null;
      copyResult(row);
    });

    /* 行级焦点：用户用鼠标点到本行（含备注输入框）时同步高亮。
       高亮与占位的权威来源是 setActiveRow（见 focusRow / focusLastRow / onRowMouseDown），
       这里只在 focusin 时补一次；focusout 有意不做清除 —— 始终保持
       "恰好一行 active"，因为程序化聚焦后窗口若尚未获得系统焦点，
       blur / focusout 并不可靠（会误把高亮清空）。 */
    section.addEventListener('focusin', function () { setActiveRow(row); });
    section.addEventListener('focusout', function () { /* 见上：不在此清除高亮 */ });

    /* 鼠标按下：不依赖焦点事件地切换当前行（文档未获系统焦点时 focusin 不可靠） */
    section.addEventListener('mousedown', function (event) { onRowMouseDown(row, event); });

    renderFoot(row);
    return section;
  }

  /* 鼠标按下（仅左键）：不依赖 focusin/focus 事件完成「切换当前行」。
     规则：
       · 目标落在备注控件内部（铅笔 / 胶囊 / 编辑框）→ 不处理，交给备注逻辑；
       · 目标不是本行 textarea → 立即设高亮 + 聚焦到行尾
         （并 preventDefault，避免默认行为把刚拿到的焦点再抢走）；
       · 目标就是本行 textarea → 只由浏览器处理焦点与光标，绝不移动光标
         （用户可能点在文本中间）。 */
  function onRowMouseDown(row, event) {
    if (event && typeof event.button === 'number' && event.button !== 0) return;
    var target = event ? event.target : null;
    if (isNoteControl(target)) return;
    setActiveRow(row.el);
    if (!target || target !== row.input) {
      if (event && event.preventDefault) event.preventDefault();
      focusRow(row.el, true);
    }
  }

  /* 挂载一条格：构建 → 追加 → 自适应高度 → 同步结果 */
  function appendRow(row) {
    buildRowEl(row);
    paperEl.appendChild(row.el);
    autoGrow(row);
    syncRowResult(row);
    return row;
  }

  /* 整表重建（仅在载入稿纸 / 新建 / 清空时使用） */
  function rebuildFromData(dataRows) {
    clearNode(paperEl);
    rows = [];
    activeRow = null;
    var list = dataRows || [];
    for (var i = 0; i < list.length; i++) {
      var row = makeRow(list[i].expr, list[i].note);
      rows.push(row);
      appendRow(row);
    }
    if (rows.length === 0) {
      var only = makeRow('', '');
      rows.push(only);
      appendRow(only);
    }
    /* 载入 / 新建 / 清空后按不变式规整：去掉多余空白行 + 末尾补一行空白 */
    normalizeBlankRows();
  }

  /* 重算全部行的结果（不改变行内容） */
  function recalcAll() {
    for (var i = 0; i < rows.length; i++) syncRowResult(rows[i]);
  }

  /* 在某行之后插入一行：只插入单个 DOM 节点，不整表重建 */
  function insertRowAfter(row, expr) {
    var i = indexOfRow(row);
    var newRow = makeRow(expr, '');
    rows.splice(i + 1, 0, newRow);
    buildRowEl(newRow);
    paperEl.insertBefore(newRow.el, row.el.nextSibling);
    autoGrow(newRow);
    syncRowResult(newRow);
    return newRow;
  }

  /* ======================================================================
     五之二、空白行不变式
     ----------------------------------------------------------------------
     不变式：整张稿纸最多只有一行"空白行"（算式与备注皆为空），且它必须是
     最后一行；任意有内容的行下方必定跟着一行空白行。据此：
       · 初始 / 新建 / 清空后：恰好一行空白输入行
       · 某行首次变为非空且其下方无空白行：在其下方补一行空白（焦点不动）
       · 失焦 / 切换活动行 / 结构性操作（Enter、载入、清空）后：规整为
         "末尾恰好一行空白"
     ====================================================================== */

  /* 该行是否为"空白行"（算式去空白后为空、且备注去空白后为空）。
     备注非空的行不算空白行 —— 规整时绝不删除带备注的行，避免丢备注。 */
  function isBlankRow(row) {
    if (!row) return true;
    var expr = row.input ? row.input.value : row.expr;
    return trim(expr) === '' && trim(row.note) === '';
  }

  /* 从行数组中移除某行（连同其 DOM 节点） */
  function removeRowAt(index) {
    var row = rows[index];
    if (!row) return;
    rows.splice(index, 1);
    if (row.el && row.el.parentNode) row.el.parentNode.removeChild(row.el);
    if (row === activeRow) activeRow = null;
  }

  /* 规整空白行：只保留末尾一行空白，其余空白行一律删除。
     若被删除的恰好是当前行，把焦点转移到相邻行（优先上一行，其次下一行），
     光标置于末尾。 */
  function normalizeBlankRows() {
    if (rows.length === 0) return;

    /* 先按"对象引用"记录当前行删除后应落到哪一行，避免索引随删除漂移 */
    var activeIndex = activeRow ? indexOfRow(activeRow) : -1;
    var prevRef = null;
    var nextRef = null;
    if (activeIndex >= 0) {
      for (var p = activeIndex - 1; p >= 0; p--) {
        if (!(isBlankRow(rows[p]) && p !== rows.length - 1)) { prevRef = rows[p]; break; }
      }
      for (var q = activeIndex + 1; q < rows.length; q++) {
        if (!(isBlankRow(rows[q]) && q !== rows.length - 1)) { nextRef = rows[q]; break; }
      }
    }

    var activeRemoved = false;
    /* 1) 删除除末尾外的所有空白行（末尾那行空白始终保留） */
    for (var i = rows.length - 1; i >= 0; i--) {
      if (i === rows.length - 1) continue;
      if (isBlankRow(rows[i])) {
        if (rows[i] === activeRow) activeRemoved = true;
        removeRowAt(i);
      }
    }
    /* 2) 末尾必须是空白行，否则补一行 */
    if (rows.length === 0 || !isBlankRow(rows[rows.length - 1])) {
      var tail = makeRow('', '');
      rows.push(tail);
      appendRow(tail);
    }
    /* 3) 当前行被删除 → 焦点转移到相邻行（优先上一行） */
    if (activeRemoved) {
      var dest = prevRef || nextRef || rows[rows.length - 1];
      if (dest && dest.input) focusRow(dest, true);
    }
  }

  /* 某行首次变为非空时调用：其下方若不存在空白行，则补一行空白（焦点不动） */
  function ensureBlankRowBelow(row) {
    if (!row) return;
    var i = indexOfRow(row);
    if (i < 0) return;
    var below = rows[i + 1];
    if (below && isBlankRow(below)) return;
    insertRowAfter(row, '');
  }

  /* 把算式文本写入某行（模型 + DOM + 高度 + 结果 + 占位） */
  function setRowExpr(row, value) {
    if (!row || !row.input) return;
    row.input.value = value;
    row.expr = value;
    autoGrow(row);
    syncRowResult(row);
    updatePlaceholder(row);
  }

  /* ======================================================================
     六、焦点
     ====================================================================== */

  /* 聚焦某行并把光标置于文本末尾。
     所有程序化移动焦点都经过这里，因此在最前面显式设置为当前行
     （高亮 + 占位刷新），不依赖窗口获得系统焦点后触发的 focus 事件。
     调用方覆盖：init / focusLastRow / setPaper / newPaper / clearAll /
     Enter 插入新行 / ↑↓ 切换行 / 鼠标点击行内非 textarea 区域。
     参数 ref 可为行对象或行元素。 */
  function focusRow(ref, toEnd) {
    var row = resolveRow(ref);
    if (!row || !row.input) return;
    setActiveRow(row);
    try { row.input.focus(); } catch (e) { /* 忽略 */ }
    if (toEnd) {
      var len = row.input.value ? row.input.value.length : 0;
      try {
        if (row.input.setSelectionRange) row.input.setSelectionRange(len, len);
      } catch (e) { /* 忽略 */ }
    }
  }

  function scrollRowIntoView(row) {
    if (!row || !row.el) return;
    try { row.el.scrollIntoView({ block: 'nearest' }); } catch (e) { /* 忽略 */ }
  }

  function focusLastRow() {
    if (!rows.length) return;
    var last = rows[rows.length - 1];
    focusRow(last, true);
    scrollRowIntoView(last);
  }

  /* ======================================================================
     六之二、光标处插入（运算菜单的气泡按钮调用）
     ----------------------------------------------------------------------
     目标行：优先「当前获得焦点的表达式输入框」所在行；无焦点时取当前行
     （activeRow）；都取不到时退到末行。
     插入位置：输入框获得焦点时取光标 / 选区（有选区则替换选区）；无焦点时按需求
     插到该行末尾（不读可能不可靠的 selectionStart）。
     插入后：显式置为当前行 → 走 onInput()，即与用户键盘输入完全同一条事件路径
     （实时求值、自动增高、空白行不变式、通知自动保存）→ 复位焦点与光标。
     caretBack 为「光标距插入文本末尾回退的字符数」：函数类传 1，落在括号内。
     ====================================================================== */
  function insertAtCaret(text, caretBack) {
    var str = (text === null || text === undefined) ? '' : String(text);
    var back = (typeof caretBack === 'number' && caretBack > 0) ? caretBack : 0;

    var target = null;
    var focused = doc ? doc.activeElement : null;
    if (focused) {
      for (var i = 0; i < rows.length; i++) {
        if (rows[i].input === focused) { target = rows[i]; break; }
      }
    }
    if (!target) target = activeRow;
    if (!target && rows.length) target = rows[rows.length - 1];
    if (!target || !target.input) return false;

    var input = target.input;
    var value = (input.value === null || input.value === undefined) ? '' : String(input.value);
    var start = value.length;
    var end = value.length;
    if (focused === input) {
      try {
        if (typeof input.selectionStart === 'number') start = input.selectionStart;
        if (typeof input.selectionEnd === 'number') end = input.selectionEnd;
      } catch (e) { /* 忽略：退回末尾插入 */ }
    }
    if (start > value.length) start = value.length;
    if (end > value.length) end = value.length;
    if (end < start) end = start;

    input.value = value.substring(0, start) + str + value.substring(end);
    setActiveRow(target);
    onInput(target);
    try { input.focus(); } catch (e) { /* 忽略 */ }

    var caret = start + str.length - back;
    if (caret < 0) caret = 0;
    if (caret > input.value.length) caret = input.value.length;
    try {
      if (input.setSelectionRange) input.setSelectionRange(caret, caret);
    } catch (e) { /* 忽略 */ }
    return true;
  }

  /* ======================================================================
     七、键盘交互
     ====================================================================== */

  function onInput(row) {
    /* 记录修改前是否为空：用于判断"首次变为非空" */
    var wasBlank = (trim(row.expr) === '' && trim(row.note) === '');
    row.expr = row.input.value;
    autoGrow(row);
    syncRowResult(row);
    updatePlaceholder(row);
    /* 首次变为非空 → 若其下方无空白行，立即补一行（焦点与光标保持在原行） */
    if (wasBlank && !isBlankRow(row)) ensureBlankRowBelow(row);
    notifyChanged();
  }

  /* 算式输入框失焦：同步模型后做一次空白行规整。
     若焦点只是转移到同一行的备注输入框（如点铅笔进入备注编辑），则跳过 ——
     否则会把正准备编辑备注的空行删掉。浏览器里 blur 的 relatedTarget 常为
     null，故再用 document.activeElement 兜底判断。 */
  function onRowBlur(row, event) {
    if (row.input) row.expr = row.input.value;
    var to = event ? event.relatedTarget : null;
    if (!to && doc) to = doc.activeElement;
    if (to && isNoteControl(to)) return;
    normalizeBlankRows();
  }

  function onKeyDown(row, event) {
    var key = event.key;
    if (key === 'Enter') { handleEnter(row, event); return; }
    if (key === 'ArrowUp') { event.preventDefault(); moveFocus(row, -1); return; }
    if (key === 'ArrowDown') { event.preventDefault(); moveFocus(row, 1); return; }
    /* 其余按键保持浏览器默认行为 */
  }

  /* Enter：结算当前行 → 把结果（不带千分位）带入下一行。
     · 当前行是"末位非空行"（其下方就是那唯一的末尾空白行）：
       结果直接填入下面那行空白，并在其后补一行空白；
     · 其它情况（中间行）：在当前位置之后插入一行并填入结果。
     两种情况都保持"末尾恰好一行空白"。算式为空或错误则不新建、不改动、焦点不动。 */
  function handleEnter(row, event) {
    event.preventDefault();                                  /* 一律不换行（含 Shift+Enter） */
    var carried = nextInputFromExpr(row.input.value, settings);
    if (carried === null) return;                            /* 空表达式或错误：不新建、不改动、焦点不动 */

    var i = indexOfRow(row);
    var below = (i >= 0 && i + 1 < rows.length) ? rows[i + 1] : null;
    var target = null;

    if (below && isBlankRow(below) && (i + 1 === rows.length - 1)) {
      /* 末位非空行：把结果填进下面那行唯一的空白行，再在其后补一行空白 */
      setRowExpr(below, carried);
      target = below;
      insertRowAfter(below, '');
    } else {
      /* 中间行：在当前位置之后插入一行并填入结果 */
      target = insertRowAfter(row, carried);
    }

    normalizeBlankRows();                                    /* 兜底：末尾仍只有一行空白 */
    focusRow(target, true);
    scrollRowIntoView(target);
    notifyChanged();
  }

  /* ↑ / ↓：以「条格」为单位切换（不进入折行内部），光标置于目标行文本末尾 */
  function moveFocus(row, delta) {
    var i = indexOfRow(row);
    if (i < 0) return;
    var j = i + delta;
    if (j < 0 || j >= rows.length) return;                   /* 到头保持不动 */
    var target = rows[j];
    focusRow(target, true);
    scrollRowIntoView(target);
  }

  /* ======================================================================
     八、备注交互
     ====================================================================== */

  /* 把备注入口原地替换为输入框；selectAll 为真时全选（点击胶囊再编辑） */
  function startNoteEdit(row, selectAll) {
    if (row.editingNote) return;
    row.editingNote = true;
    row.noteBeforeEdit = row.note;

    var input = doc.createElement('input');
    input.className = 'note-input';
    input.type = 'text';
    input.spellcheck = false;
    input.value = row.note;

    input.addEventListener('keydown', function (event) {
      if (event.key === 'Enter') { event.preventDefault(); commitNote(row); }
      else if (event.key === 'Escape') { event.preventDefault(); cancelNote(row); }
    });
    input.addEventListener('blur', function () { commitNote(row); normalizeBlankRows(); });

    clearNode(row.foot);
    row.foot.appendChild(input);
    row.noteInput = input;

    try { input.focus(); } catch (e) { /* 忽略 */ }
    var len = input.value ? input.value.length : 0;
    try {
      if (selectAll && input.select) input.select();
      else if (input.setSelectionRange) input.setSelectionRange(len, len);
    } catch (e) { /* 忽略 */ }
  }

  /* 提交备注：trim 后为空则回落为铅笔入口，否则显示胶囊 */
  function commitNote(row) {
    if (!row.editingNote) return;
    row.editingNote = false;
    var input = row.noteInput;
    row.noteInput = null;
    var wasBlank = (trim(row.noteBeforeEdit) === '' && trim(row.expr) === '');
    var next = trim(input ? input.value : row.note);
    var changed = (next !== row.noteBeforeEdit);
    row.note = next;
    renderFoot(row);
    /* 备注使该行首次变为非空 → 其下方若无空白行则补一行（焦点不动） */
    if (wasBlank && !isBlankRow(row)) ensureBlankRowBelow(row);
    if (changed) notifyChanged();
  }

  /* 取消本次编辑，恢复编辑前状态（不触发内容变化回调） */
  function cancelNote(row) {
    if (!row.editingNote) return;
    row.editingNote = false;
    row.noteInput = null;
    row.note = row.noteBeforeEdit;
    renderFoot(row);
  }

  /* ======================================================================
     九、内容变化通知（供自动保存防抖使用）
     ====================================================================== */

  function notifyChanged() {
    for (var i = 0; i < subscribers.length; i++) {
      try { subscribers[i](); } catch (e) { /* 单个订阅者异常不影响其它订阅者 */ }
    }
  }

  /* ======================================================================
     十、初始化
     ====================================================================== */

  function init() {
    paperEl = doc.getElementById('paper');
    if (!paperEl) return;

    var inHost = !!(root.Host && root.Host.isAvailable && root.Host.isAvailable());
    /* 宿主中先渲染一个空行（真实内容由宿主推送覆盖）；
       普通浏览器里用设计稿的示例内容，便于直接预览完整视觉 */
    var initial = inHost ? [{ expr: '', note: '' }] : SAMPLE_ROWS;

    rebuildFromData(initial);
    initialized = true;
    focusLastRow();

    /* 复制成功回包 → 鼠标上方闪一下"已复制"（底部状态不再闪） */
    if (root.Host && root.Host.on) root.Host.on('copied', onCopiedMessage);

    /* 窗口宽度变化 → 下一帧重算全部行高（长算式换行数变了必须重算，
       否则 overflow:hidden 会把多出来的行裁掉）。 */
    if (root.addEventListener) root.addEventListener('resize', scheduleReflow);

    /* 首帧兜底：首次渲染可能发生在布局宽度尚未确定时（autoGrow 会退化为
       height:auto），此处在下一帧按真实宽度落定一次行高。 */
    scheduleReflow();
  }

  /* ======================================================================
     十一、对外 API（Task 5 / 6 直接调用）
     ====================================================================== */

  var App = {
    /* 导出当前稿纸（算式取自输入框，保证与界面一致） */
    getPaper: function () {
      var out = [];
      for (var i = 0; i < rows.length; i++) {
        var r = rows[i];
        out.push({ expr: r.input ? r.input.value : r.expr, note: r.note });
      }
      return { rows: out };
    },

    /* 载入稿纸：替换全部内容并重算，结束后聚焦最后一行 */
    setPaper: function (paper) {
      var data = sanitizePaper(paper);
      rebuildFromData(data.rows);
      focusLastRow();
      notifyChanged();
    },

    /* 新建：只剩一个空行并聚焦 */
    newPaper: function () {
      rebuildFromData([{ expr: '', note: '' }]);
      focusLastRow();
      notifyChanged();
    },

    /* 全部清空：所有行清空为单个空行（保留 1 行） */
    clearAll: function () {
      rebuildFromData([{ expr: '', note: '' }]);
      focusLastRow();
      notifyChanged();
    },

    /* 订阅内容变化（行插入、输入、备注变更），返回取消订阅函数 */
    onRowsChanged: function (handler) {
      if (typeof handler !== 'function') return function () { };
      subscribers.push(handler);
      return function () {
        for (var i = 0; i < subscribers.length; i++) {
          if (subscribers[i] === handler) { subscribers.splice(i, 1); break; }
        }
      };
    },

    getSettings: function () {
      return { angleMode: settings.angleMode, useGrouping: settings.useGrouping };
    },

    /* 局部覆盖设置并立即重算全部行结果（不改变行内容、不触发内容变化回调） */
    applySettings: function (next) {
      if (next) {
        if (next.angleMode === 'deg' || next.angleMode === 'rad') settings.angleMode = next.angleMode;
        if (next.useGrouping === true || next.useGrouping === false) settings.useGrouping = next.useGrouping;
      }
      recalcAll();
    },

    focusLastRow: focusLastRow,

    /* 抽屉开合等引起稿纸宽度变化时的重排入口：与窗口 resize 用的是同一套
       「下一帧重算全部行高」逻辑（长算式换行数随可用宽度变化必须重算）。 */
    reflow: scheduleReflow,

    /* 在当前行表达式光标处插入文本（运算菜单的气泡按钮调用）；
       caretBack 为光标回退字符数（函数类传 1，光标落在括号内）。 */
    insertAtCaret: insertAtCaret,

    /* 无浏览器环境下可独立校验的纯函数 */
    _internal: {
      evaluateExpr: function (expr, options) { return root.CalcEngine.evaluate(expr, options); },
      nextInputFromExpr: nextInputFromExpr,
      sanitizePaper: sanitizePaper,
      isBlankRow: isBlankRow,
      normalizeBlankRows: normalizeBlankRows
    }
  };

  root.App = App;

  /* 初始化：脚本位于 body 末尾，通常 DOM 已就绪 */
  if (!doc) return;
  if (doc.readyState === 'loading') {
    doc.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

})(typeof window !== 'undefined' ? window : this);