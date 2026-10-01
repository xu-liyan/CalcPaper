/* ==========================================================================
   计算稿纸 · 右下角运算菜单抽屉（menu.js）
   --------------------------------------------------------------------------
   · 纯 JavaScript（ES5 风格：只用 var 与 function），零依赖，不使用 ES Module
   · 加载顺序：bridge.js → calc-engine.js → app.js → tooltip.js → confirm.js →
               persist.js → settings.js → menu.js
   · 职责：
       1) 右下角「运算菜单」键（三条横杠，与底栏图标同风格）：单击展开 / 收起右侧抽屉
          （单击立即生效，无任何延迟判定；双击不再作为任何入口）
       2) 抽屉宽度恒定 280px，推开稿纸而非遮挡；开合引起稿纸宽度变化时，
          调用 App.reflow()（与窗口 resize 完全相同的长算式重排 / 行高重测入口）
       3) 抽屉内 23 个运算气泡（圆角矩形），点击按形态插入到当前行表达式光标处，
          并即时求值、触发自动保存（走 App.insertAtCaret → 既有 input 事件路径）
       4) 排序入口 = 抽屉区域内点右键 → 应用风格的自绘菜单（唯一项「符号排序」）→
          进入拖动排序模式；排序模式下从抽屉底部「保存」退出并落盘、「重置」恢复默认序
       5) 全窗口（WebView2 内容区）屏蔽浏览器默认右键菜单；唯一例外是抽屉区域弹出
          我们自己的自绘菜单（托盘图标自身的原生右键菜单不属于网页内容，不受影响）
       6) 顺序写入 settings.json 的 menuOrder —— 见下方「顺序口径」；读取时容错：
          未知 id 忽略、缺失 id 按默认顺序追加到「所在组末尾」、老配置无该字段用默认顺序
       7) 收起逻辑收紧：只有单击菜单键才收起（点稿纸 / 底栏一律不收起）；Esc 不收起
         也不退出排序（退出排序只能靠「保存」或「重置」）；排序模式下菜单键无效
       8) 提示气泡：菜单键在面板收起时提示「运算符号」（单行）、展开且非排序时提示
          「菜单界面右键 / 排序运算符号」（两行，\n 由 style.css 的 .tip-bubble 以
          white-space: pre-line 渲染）；排序模式下菜单键与符号气泡的提示一律不显示
          （既不挂 data-tip，也不显示气泡）
   · 顺序口径（menuOrder = 最终视觉顺序下全部按钮 id 的「平铺数组」）：
       分组行的顺序由「各组首个 id 首次出现的位置」自然推出；组内顺序由 id 在该
       数组中的先后决定。本文件内部始终把 order 规整为「组连续」排列，故渲染出的
       行（= 分组）顺序与平铺数组的首现顺序一一对应，无需改 C#。
   · 全部提示气泡复用 tooltip.js 的自定义气泡（data-tip），不使用原生 title
   ========================================================================== */

(function (root) {
  'use strict';

  var doc = root.document || null;
  if (!doc) return;

  var Host = root.Host || null;
  var App = root.App || null;
  var Persist = root.Persist || null;

  /* ======================================================================
     一、按钮定义
     ----------------------------------------------------------------------
     稳定 id（持久化到 menuOrder）：^ x2 x3 sqrt cbrt % ! mod rem sin cos tan
       asin acos atan log ln abs round floor ceil pi e
     insert：点击后插入的文本；back：光标距插入文本末尾回退的字符数
       （函数类为 1，光标落在括号内）。
     group：视觉分区（= 一「行」；渲染时分组之间插入一条细分隔线）。
     ====================================================================== */

  /* 数组顺序 = 默认顺序（同时驱动：无保存顺序时的初始渲染 / 排序模式「重置」的目标）：
       行序 power → trig → atrig → log → const → calc → func，行内按本数组先后。
       即幂/根(x² x³ ^ √ ³√) → 三角 → 反三角 → 对数 → 常量(π e) → 运算(% ! mod rem) → 通用函数。 */
  var BUTTONS = [
    { id: 'x2',    label: 'x²',    insert: '^2',     back: 0, group: 'power',  tip: '平方（^2）' },
    { id: 'x3',    label: 'x³',    insert: '^3',     back: 0, group: 'power',  tip: '立方（^3）' },
    { id: '^',     label: '^',     insert: '^',      back: 0, group: 'power',  tip: '幂运算（如 2^5）' },
    { id: 'sqrt',  label: '√',     insert: 'sqrt()', back: 1, group: 'power',  tip: '平方根 sqrt(x)' },
    { id: 'cbrt',  label: '³√',    insert: 'cbrt()', back: 1, group: 'power',  tip: 'cbrt(x) 立方根' },

    { id: 'sin',   label: 'sin',   insert: 'sin()',  back: 1, group: 'trig',   tip: '正弦 sin(x)' },
    { id: 'cos',   label: 'cos',   insert: 'cos()',  back: 1, group: 'trig',   tip: '余弦 cos(x)' },
    { id: 'tan',   label: 'tan',   insert: 'tan()',  back: 1, group: 'trig',   tip: '正切 tan(x)' },

    { id: 'asin',  label: 'asin',  insert: 'asin()', back: 1, group: 'atrig',  tip: '反正弦 asin(x)' },
    { id: 'acos',  label: 'acos',  insert: 'acos()', back: 1, group: 'atrig',  tip: '反余弦 acos(x)' },
    { id: 'atan',  label: 'atan',  insert: 'atan()', back: 1, group: 'atrig',  tip: '反正切 atan(x)' },

    { id: 'log',   label: 'log',   insert: 'log()',  back: 1, group: 'log',    tip: 'log(x) 10 底；log(b,x) 指定底数' },
    { id: 'ln',    label: 'ln',    insert: 'ln()',   back: 1, group: 'log',    tip: '自然对数 ln(x)' },

    { id: 'pi',    label: 'π',     insert: 'π',      back: 0, group: 'const',  tip: '圆周率 π' },
    { id: 'e',     label: 'e',     insert: 'e',      back: 0, group: 'const',  tip: '自然常数 e' },

    { id: '%',     label: '%',     insert: '%',      back: 0, group: 'calc',   tip: '百分号（50% = 0.5）；写在两个数之间则为取模' },
    { id: '!',     label: '!',     insert: '!',      back: 0, group: 'calc',   tip: '阶乘（如 5! = 120）' },
    { id: 'mod',   label: 'mod',   insert: 'mod()',  back: 1, group: 'calc',   tip: 'mod(a,b) 取模（结果随除数）' },
    { id: 'rem',   label: 'rem',   insert: 'rem()',  back: 1, group: 'calc',   tip: 'rem(a,b) 取余（结果随被除数）' },

    { id: 'abs',   label: 'abs',   insert: 'abs()',  back: 1, group: 'func',   tip: '绝对值 abs(x)' },
    { id: 'round', label: 'round', insert: 'round()', back: 1, group: 'func',  tip: '四舍五入 round(x)' },
    { id: 'floor', label: 'floor', insert: 'floor()', back: 1, group: 'func',  tip: '向下取整 floor(x)' },
    { id: 'ceil',  label: 'ceil',  insert: 'ceil()', back: 1, group: 'func',   tip: '向上取整 ceil(x)' }
  ];

  /* id → 定义（渲染与容错时查表） */
  var DEF_BY_ID = {};
  for (var bi = 0; bi < BUTTONS.length; bi++) DEF_BY_ID[BUTTONS[bi].id] = BUTTONS[bi];

  /* 拖动激活阈值（像素）：位移超过它才真正开始拖动 */
  var DRAG_THRESHOLD = 4;
  /* 抽屉开合动画时长（与 style.css 的 320ms 一致），用于驱动期间的行高重排 */
  var ANIM_MS = 320;

  /* 面板收起时菜单键的提示文案（单行） */
  var TIP_COLLAPSED = '运算符号';

  /* 面板展开且非排序时菜单键的提示文案（两行：\n 由 .tip-bubble 的
     white-space: pre-line 渲染为换行；气泡宽度按最长行自适应、高度容纳两行） */
  var TIP_OPEN = '菜单界面右键\n排序运算符号';

  /* ======================================================================
     二、状态与元素
     ====================================================================== */

  var toggle = null;      /* #menu-toggle */
  var drawer = null;      /* #menu-drawer */
  var workspace = null;   /* #workspace */
  var chipsBox = null;    /* #menu-chips */
  var hintEl = null;      /* #menu-drawer-hint */
  var sortFoot = null;    /* #menu-drawer-foot（排序模式底部 重置 / 保存） */
  var sortResetBtn = null;
  var sortSaveBtn = null;

  var open = false;       /* 抽屉是否展开 */
  var sorting = false;    /* 是否处于拖动排序模式 */
  var order = [];         /* 当前按钮 id 平铺顺序（恒为「组连续」排列） */

  var drag = null;        /* 气泡拖动状态：{ chip, id, group, startX, startY, active, grabX, grabY } */
  var rowDrag = null;     /* 整行拖动状态：{ index, group, startX, startY, active } */
  var dragging = false;   /* 是否正处于拖动中（用于避免拖动期间被设置回包打断渲染） */
  var indicator = null;   /* 气泡插入位置指示条（行内竖向） */
  var rowIndicator = null;/* 整行插入位置指示条（行间横向） */

  var contextMenu = null; /* 抽屉内右键自绘菜单（懒创建） */

  var reflowRaf = null;   /* 开合动画期间的逐帧重排句柄 */

  /* ======================================================================
     三、小工具
     ====================================================================== */

  function clearNode(node) {
    if (!node) return;
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function hasClass(el, name) {
    return !!(el && el.classList && el.classList.contains && el.classList.contains(name));
  }

  function contains(list, value) {
    for (var i = 0; i < list.length; i++) {
      if (list[i] === value) return true;
    }
    return false;
  }

  function isInside(node, target) {
    var cur = target;
    while (cur) {
      if (cur === node) return true;
      cur = cur.parentNode || null;
    }
    return false;
  }

  function nowMs() {
    return (new Date()).getTime();
  }

  /* ======================================================================
     四、顺序模型：分组行 / 平铺规整
     ----------------------------------------------------------------------
     · 一行 = 一个分组（group）；分组行的顺序由「各组首个 id 首次出现的位置」推出。
     · order 恒为「组连续」排列（同一分组的全部 id 相邻），故行序 = 平铺数组首现序。
     ====================================================================== */

  /* 默认分组顺序（按 BUTTONS 中各组首次出现的位置） */
  function defaultGroupOrder() {
    var out = [];
    for (var i = 0; i < BUTTONS.length; i++) {
      if (!contains(out, BUTTONS[i].group)) out.push(BUTTONS[i].group);
    }
    return out;
  }

  function groupOf(id) {
    var d = DEF_BY_ID[id];
    return d ? d.group : null;
  }

  function rowOf(rows, group) {
    for (var i = 0; i < rows.length; i++) {
      if (rows[i].group === group) return rows[i];
    }
    return null;
  }

  /* 由平铺数组推出「行（组）」数组：保持各行在数组中的先后与组内先后 */
  function rowsFromOrder(flat) {
    var rows = [];
    for (var i = 0; i < flat.length; i++) {
      var g = groupOf(flat[i]);
      if (!g) continue;
      var row = rowOf(rows, g);
      if (!row) { row = { group: g, ids: [] }; rows.push(row); }
      row.ids.push(flat[i]);
    }
    return rows;
  }

  function flattenRows(rows) {
    var out = [];
    for (var r = 0; r < rows.length; r++) {
      for (var i = 0; i < rows[r].ids.length; i++) out.push(rows[r].ids[i]);
    }
    return out;
  }

  /* 把外部传入的 menuOrder 归一为合法顺序：
       · 非数组 / 含非字符串项 / 未知 id → 忽略该项
       · 重复 id → 去重（保留首次出现）
       · 配置中缺失的按钮 id → 按默认顺序追加到「所在组末尾」
     结果恒为「组连续」平铺数组；行顺序由各组首个 id 首次出现的位置自然推出。 */
  function normalizeOrder(raw) {
    var seen = [];
    var flat = [];
    if (raw && typeof raw.length === 'number') {
      for (var i = 0; i < raw.length; i++) {
        var id = raw[i];
        if (typeof id !== 'string') continue;
        if (DEF_BY_ID[id] === undefined) continue;
        if (contains(seen, id)) continue;
        seen.push(id);
        flat.push(id);
      }
    }
    var rows = rowsFromOrder(flat);
    /* raw 未提到的分组：按默认分组顺序补在末尾 */
    var groups = defaultGroupOrder();
    for (var g = 0; g < groups.length; g++) {
      if (!rowOf(rows, groups[g])) rows.push({ group: groups[g], ids: [] });
    }
    /* 每组补上缺失的默认 id（追加到组末） */
    for (var r = 0; r < rows.length; r++) {
      for (var b = 0; b < BUTTONS.length; b++) {
        if (BUTTONS[b].group !== rows[r].group) continue;
        if (!contains(rows[r].ids, BUTTONS[b].id)) rows[r].ids.push(BUTTONS[b].id);
      }
    }
    return flattenRows(rows);
  }

  function defaultOrder() {
    return normalizeOrder(null);
  }

  /* ======================================================================
     五、渲染
     ----------------------------------------------------------------------
     每行（分组）行首插入一个淡色抓手（⠿，仅排序模式由 CSS 显示），其后为该行气泡。
     行与行之间插入一条占满整行的细分隔线（组分隔线）。
     ====================================================================== */

  function makeChip(def) {
    var chip = doc.createElement('button');
    chip.type = 'button';
    chip.className = 'menu-chip';
    chip.setAttribute('data-id', def.id);
    /* 排序模式下不挂提示（data-tip），避免拖动时弹出气泡；退出排序后重建恢复 */
    if (!sorting) {
      chip.setAttribute('data-tip', def.tip);
      chip.setAttribute('aria-label', def.tip);
    }
    chip.textContent = def.label;
    return chip;
  }

  function makeRowHandle(group) {
    var h = doc.createElement('span');
    h.className = 'menu-row-handle';
    h.setAttribute('aria-hidden', 'true');
    h.setAttribute('data-group', group);
    h.textContent = '⠿';
    return h;
  }

  function renderChips() {
    if (!chipsBox) return;
    clearNode(chipsBox);
    var rows = rowsFromOrder(order);
    for (var r = 0; r < rows.length; r++) {
      if (r > 0) {
        var br = doc.createElement('div');
        br.className = 'menu-group-break';
        chipsBox.appendChild(br);
      }
      chipsBox.appendChild(makeRowHandle(rows[r].group));
      for (var i = 0; i < rows[r].ids.length; i++) {
        var def = DEF_BY_ID[rows[r].ids[i]];
        if (def) chipsBox.appendChild(makeChip(def));
      }
    }
  }

  /* 气泡内任意子节点 → 命中的 .menu-chip 元素 */
  function chipFromNode(node) {
    var el = node;
    while (el) {
      if (hasClass(el, 'menu-chip')) return el;
      el = el.parentNode || null;
    }
    return null;
  }

  /* 行首抓手：从事件目标向上找 .menu-row-handle */
  function handleFromNode(node) {
    var el = node;
    while (el) {
      if (hasClass(el, 'menu-row-handle')) return el;
      el = el.parentNode || null;
    }
    return null;
  }

  /* 取某个 id 对应的气泡元素（首个匹配） */
  function chipElById(id) {
    if (!chipsBox) return null;
    var kids = chipsBox.childNodes;
    for (var i = 0; i < kids.length; i++) {
      var el = kids[i];
      if (el.nodeType !== 1) continue;
      if (!hasClass(el, 'menu-chip')) continue;
      if (el.getAttribute('data-id') === id) return el;
    }
    return null;
  }

  /* 某行内仍留在流布局里的气泡（排除被拖起的那个），按 DOM 顺序 */
  function flowRowChips(group) {
    var out = [];
    if (!chipsBox) return out;
    var kids = chipsBox.childNodes;
    for (var i = 0; i < kids.length; i++) {
      var el = kids[i];
      if (el.nodeType !== 1) continue;
      if (!hasClass(el, 'menu-chip')) continue;
      if (hasClass(el, 'menu-chip--floating')) continue;
      if (groupOf(el.getAttribute('data-id')) !== group) continue;
      out.push(el);
    }
    return out;
  }

  /* 某行全部气泡的包围盒（用于整行拖动的空白判定与插入位置计算） */
  function rowBox(ids) {
    var minTop = Infinity, maxBottom = -Infinity, minLeft = Infinity, maxRight = -Infinity;
    var found = false;
    for (var i = 0; i < ids.length; i++) {
      var chip = chipElById(ids[i]);
      if (!chip) continue;
      var rc = chip.getBoundingClientRect();
      found = true;
      if (rc.top < minTop) minTop = rc.top;
      if (rc.bottom > maxBottom) maxBottom = rc.bottom;
      if (rc.left < minLeft) minLeft = rc.left;
      if (rc.right > maxRight) maxRight = rc.right;
    }
    return found ? { top: minTop, bottom: maxBottom, left: minLeft, right: maxRight } : null;
  }

  /* ======================================================================
     六、开合 + 宽度变化重排
     ====================================================================== */

  /* 开合动画期间逐帧触发 App.reflow()（与窗口 resize 同一入口）：
     抽屉宽度在 320ms 内渐变，稿纸可用宽度随之变化，长算式换行数与行高必须
     跟着重算，否则会被 overflow:hidden 裁掉。动画结束后再兜底一次。 */
  function startReflowLoop() {
    if (!App || !App.reflow) return;
    var endAt = nowMs() + ANIM_MS;

    function step() {
      reflowRaf = null;
      if (App && App.reflow) App.reflow();
      if (nowMs() < endAt) scheduleReflowFrame(step);
    }

    if (reflowRaf) return;                        /* 已在驱动中 */
    scheduleReflowFrame(step);
  }

  function scheduleReflowFrame(fn) {
    if (root.requestAnimationFrame) {
      reflowRaf = root.requestAnimationFrame(fn);
    } else {
      reflowRaf = setTimeout(function () { fn(); }, 16);
    }
  }

  /* 用一次合成 mouseout（relatedTarget 为 null）触发 tooltip.js 自身的隐藏逻辑。
     tooltip.js 未对外暴露 hide()，而展开 / 进入排序时鼠标仍停在原处、不会产生
     mousemove，若不主动触发，刚刚显示的「运算符号」气泡会残留。 */
  function suppressCurrentTip() {
    if (!doc.createEvent) return;
    try {
      var ev = doc.createEvent('MouseEvents');
      if (ev.initMouseEvent) {
        ev.initMouseEvent('mouseout', true, true, root, 0, 0, 0, 0, 0,
          false, false, false, false, 0, null);
        doc.dispatchEvent(ev);
      }
    } catch (e) { /* 忽略 */ }
  }

  /* 菜单键提示：按「开合 + 是否排序」决定挂 / 撤 data-tip ——
       · 收起          → 「运算符号」（单行）
       · 展开且非排序  → 「菜单界面右键 / 排序运算符号」（两行；\n 由 .tip-bubble 的
                         white-space: pre-line 渲染为换行）
       · 排序模式      → 撤掉 data-tip（悬停菜单键不显示任何气泡，与「排序模式下
                         所有气泡都不显示」的规则一致）
     文案 / 显隐发生变化时派发一次合成 mouseout（relatedTarget 为 null）让
     tooltip.js 清掉旧气泡并把 current 置空 —— 否则展开 / 收起 / 进出排序时鼠标仍
     停在菜单键上、不会产生 mousemove，气泡文案与显隐不会刷新。 */
  function applyToggleTip() {
    if (!toggle) return;
    var want = sorting ? null : (open ? TIP_OPEN : TIP_COLLAPSED);
    var cur = toggle.getAttribute('data-tip');
    if (want === null) {
      if (cur === null) return;                /* 已无 tip：无需刷新 */
      suppressCurrentTip();
      toggle.removeAttribute('data-tip');
      return;
    }
    if (cur === want) return;
    suppressCurrentTip();
    toggle.setAttribute('data-tip', want);
  }

  function openPanel() {
    if (open) return;
    open = true;
    if (workspace) workspace.classList.add('menu-open');
    if (toggle) toggle.setAttribute('aria-expanded', 'true');
    if (drawer) drawer.setAttribute('aria-hidden', 'false');
    applyToggleTip();
    startReflowLoop();
  }

  function closePanel() {
    exitSort();
    if (dragging) cancelDrag();
    hideContextMenu();
    if (!open) return;
    open = false;
    if (workspace) workspace.classList.remove('menu-open');
    if (toggle) toggle.setAttribute('aria-expanded', 'false');
    if (drawer) drawer.setAttribute('aria-hidden', 'true');
    applyToggleTip();
    startReflowLoop();
  }

  /* ======================================================================
     七、排序模式
     ====================================================================== */

  /* 进入排序模式：隐藏气泡提示（data-tip，含菜单键）、显示提示条与底部按钮、
     显示行首抓手 */
  function enterSort() {
    if (sorting) return;
    sorting = true;
    if (workspace) workspace.classList.add('menu-sorting');
    if (hintEl) hintEl.hidden = false;
    if (sortFoot) sortFoot.hidden = false;
    removeChipTips();
    suppressCurrentTip();
    applyToggleTip();                            /* 排序模式：撤掉菜单键的 data-tip */
  }

  /* 退出排序模式：恢复气泡提示（重建气泡 + 菜单键两行文案）、隐藏提示条与底部按钮 */
  function exitSort() {
    if (!sorting) return;
    sorting = false;
    if (workspace) workspace.classList.remove('menu-sorting');
    if (hintEl) hintEl.hidden = true;
    if (sortFoot) sortFoot.hidden = true;
    removeIndicator();
    removeRowIndicator();
    renderChips();
    applyToggleTip();                            /* 恢复菜单键的 data-tip（展开态两行） */
  }

  /* 排序模式下移除全部气泡的 data-tip（悬停不再弹气泡） */
  function removeChipTips() {
    if (!chipsBox || !chipsBox.childNodes) return;
    var kids = chipsBox.childNodes;
    for (var i = 0; i < kids.length; i++) {
      var el = kids[i];
      if (el.nodeType !== 1) continue;
      if (!hasClass(el, 'menu-chip')) continue;
      if (el.removeAttribute) el.removeAttribute('data-tip');
    }
  }

  /* 排序模式底部「保存」：持久化当前顺序后退出排序 */
  function onSortSave() {
    persistOrder();
    exitSort();
  }

  /* 排序模式底部「重置」：恢复默认顺序（23 个按钮默认次序 + 分组行默认次序），
     立即持久化，然后退出排序。 */
  function onSortReset() {
    order = defaultOrder();
    renderChips();
    persistOrder();
    exitSort();
  }

  /* ======================================================================
     八、菜单键：单击立即展开 / 收起（无延迟、无双击判定）
     ----------------------------------------------------------------------
     · 收起状态单击 → 立即展开；
     · 展开状态单击 → 立即收起；
     · 排序模式下菜单键无效（点击不收起、不做任何事）。
     ====================================================================== */

  function onToggleClick() {
    if (sorting) return;                          /* 排序模式：菜单键无效 */
    if (open) closePanel();
    else openPanel();
  }

  /* ======================================================================
     九、气泡点击插入
     ====================================================================== */

  function insertChip(id) {
    var def = DEF_BY_ID[id];
    if (!def) return;
    if (!App || !App.insertAtCaret) return;
    App.insertAtCaret(def.insert, def.back);
  }

  function onChipsClick(event) {
    var chip = chipFromNode(event ? event.target : null);
    if (!chip) return;
    if (sorting) return;                          /* 排序模式：单击气泡不插入 */
    insertChip(chip.getAttribute('data-id'));
  }

  /* 气泡按下：一律 preventDefault，避免按钮抢走表达式输入框的焦点
     （否则会丢失光标位置，插入只能落到行尾）。
     排序模式下：气泡按下 → 行内拖动；行首抓手 / 行尾空白处按下 → 整行拖动。 */
  function onChipsMouseDown(event) {
    if (!event || event.button !== 0) return;
    var target = event.target;

    if (!sorting) {
      if (chipFromNode(target) && event.preventDefault) event.preventDefault();
      return;
    }

    var chip = chipFromNode(target);
    if (chip) {
      if (event.preventDefault) event.preventDefault();
      beginChipDrag(chip, event.clientX, event.clientY);
      return;
    }
    var handle = handleFromNode(target);
    if (handle) {
      if (event.preventDefault) event.preventDefault();
      beginRowDragByGroup(handle.getAttribute('data-group'), event.clientX, event.clientY);
      return;
    }
    /* 行尾空白处（本行的气泡右缘之外 / 左缘之外）→ 整行拖动 */
    var hit = rowAtPoint(event.clientX, event.clientY);
    if (hit && (event.clientX > hit.box.right || event.clientX < hit.box.left)) {
      if (event.preventDefault) event.preventDefault();
      beginRowDrag(hit.index, event.clientX, event.clientY);
    }
  }

  /* ======================================================================
     十、行内气泡拖动排序
     ====================================================================== */

  function beginChipDrag(chip, x, y) {
    drag = {
      chip: chip,
      id: chip.getAttribute('data-id'),
      group: groupOf(chip.getAttribute('data-id')),
      startX: x,
      startY: y,
      grabX: 0,
      grabY: 0,
      active: false
    };
  }

  /* 位移超过阈值 → 真正拖起：气泡脱离流布局（position: fixed）跟随指针，
     并创建插入位置指示条。 */
  function activateChipDrag(x, y) {
    var rect = drag.chip.getBoundingClientRect();
    drag.active = true;
    dragging = true;
    drag.grabX = drag.startX - rect.left;
    drag.grabY = drag.startY - rect.top;

    drag.chip.classList.add('menu-chip--floating');
    drag.chip.style.width = rect.width + 'px';
    drag.chip.style.height = rect.height + 'px';
    drag.chip.style.left = rect.left + 'px';
    drag.chip.style.top = rect.top + 'px';

    indicator = doc.createElement('div');
    indicator.className = 'menu-drop-indicator';
  }

  function unfloatChip(chip) {
    if (!chip) return;
    chip.classList.remove('menu-chip--floating');
    chip.style.left = '';
    chip.style.top = '';
    chip.style.width = '';
    chip.style.height = '';
  }

  function removeIndicator() {
    if (indicator && indicator.parentNode) indicator.parentNode.removeChild(indicator);
    indicator = null;
  }

  /* 按「本行内最近的气泡中心」求插入位置：返回该行（排除被拖起气泡）内的插入下标。
     由于只在本行气泡里取最近点，拖到行尾空白处时最近点必为本行最后一个气泡，
     且其右侧判定（x > 中心 / y 越过下缘）会给出「之后」→ 覆盖「落在最后一个
     气泡之后」的情形，修复「拖不到本行最后一位」的缺陷。 */
  function computeDropIndex(group, x, y) {
    var list = flowRowChips(group);
    if (list.length === 0) return 0;
    var bestIndex = 0;
    var bestDist = Infinity;
    var bestRect = null;
    for (var i = 0; i < list.length; i++) {
      var r = list[i].getBoundingClientRect();
      var cx = r.left + r.width / 2;
      var cy = r.top + r.height / 2;
      var d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
      if (d < bestDist) { bestDist = d; bestIndex = i; bestRect = r; }
    }
    var after;
    if (bestRect && y > bestRect.bottom) after = true;
    else if (bestRect && y < bestRect.top) after = false;
    else after = !!(bestRect && x > bestRect.left + bestRect.width / 2);
    return bestIndex + (after ? 1 : 0);
  }

  /* 把竖向指示条放到本行指定下标之前（下标等于长度则放到本行最后一个气泡之后） */
  function placeIndicator(group, pos) {
    if (!indicator || !chipsBox) return;
    var list = flowRowChips(group);
    if (list.length === 0) { chipsBox.appendChild(indicator); return; }
    if (pos >= 0 && pos < list.length) {
      chipsBox.insertBefore(indicator, list[pos]);
    } else {
      var last = list[list.length - 1];
      chipsBox.insertBefore(indicator, last.nextSibling);
    }
  }

  /* 行内重排：把 dragId 放到其所在行的第 pos 位（pos 基于「排除被拖起气泡」的列表） */
  function reorderWithinRow(dragId, pos) {
    var rows = rowsFromOrder(order);
    var row = rowOf(rows, groupOf(dragId));
    if (!row) return;
    var ids = [];
    for (var i = 0; i < row.ids.length; i++) {
      if (row.ids[i] !== dragId) ids.push(row.ids[i]);
    }
    if (pos < 0) pos = 0;
    if (pos > ids.length) pos = ids.length;
    ids.splice(pos, 0, dragId);
    row.ids = ids;
    order = flattenRows(rows);
    renderChips();
  }

  /* ======================================================================
     十之二、整行拖动排序
     ====================================================================== */

  function beginRowDrag(index, x, y) {
    var rows = rowsFromOrder(order);
    if (index < 0 || index >= rows.length) return;
    rowDrag = {
      index: index,
      group: rows[index].group,
      startX: x,
      startY: y,
      active: false
    };
  }

  function beginRowDragByGroup(group, x, y) {
    var rows = rowsFromOrder(order);
    for (var r = 0; r < rows.length; r++) {
      if (rows[r].group === group) { beginRowDrag(r, x, y); return; }
    }
  }

  function activateRowDrag() {
    rowDrag.active = true;
    dragging = true;
    setRowDragStyle(rowDrag.group, true);
    rowIndicator = doc.createElement('div');
    rowIndicator.className = 'menu-row-indicator';
  }

  function setRowDragStyle(group, on) {
    var rows = rowsFromOrder(order);
    var row = rowOf(rows, group);
    if (!row) return;
    for (var i = 0; i < row.ids.length; i++) {
      var chip = chipElById(row.ids[i]);
      if (!chip) continue;
      if (on) chip.classList.add('menu-chip--row-dragging');
      else chip.classList.remove('menu-chip--row-dragging');
    }
  }

  function removeRowIndicator() {
    if (rowIndicator && rowIndicator.parentNode) rowIndicator.parentNode.removeChild(rowIndicator);
    rowIndicator = null;
  }

  /* 命中最近的行：返回 { index, group, box } */
  function rowAtPoint(x, y) {
    var rows = rowsFromOrder(order);
    var best = null;
    var bestDist = Infinity;
    for (var r = 0; r < rows.length; r++) {
      var box = rowBox(rows[r].ids);
      if (!box) continue;
      var cx = (box.left + box.right) / 2;
      var cy = (box.top + box.bottom) / 2;
      var d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
      if (d < bestDist) { bestDist = d; best = { index: r, group: rows[r].group, box: box }; }
    }
    return best;
  }

  /* 整行插入位置：按指针纵坐标落在各行中心之上 / 之下的第一处间隙。
     返回值为「原行数组」中的插入下标（0..行数）。 */
  function computeRowDropIndex(y) {
    var rows = rowsFromOrder(order);
    var idx = rows.length;
    for (var r = 0; r < rows.length; r++) {
      var box = rowBox(rows[r].ids);
      if (!box) continue;
      if (y < (box.top + box.bottom) / 2) { idx = r; break; }
    }
    return idx;
  }

  /* 该行行首锚点：抓手元素（若存在），否则第一个气泡 */
  function rowFirstEl(ids) {
    if (!chipsBox || !ids || ids.length === 0) return null;
    var firstChip = null;
    var kids = chipsBox.childNodes;
    for (var i = 0; i < kids.length; i++) {
      var el = kids[i];
      if (el.nodeType !== 1) continue;
      if (hasClass(el, 'menu-chip') && el.getAttribute('data-id') === ids[0]) { firstChip = el; break; }
    }
    if (!firstChip) return null;
    var prev = firstChip.previousSibling;
    if (prev && prev.nodeType === 1 && hasClass(prev, 'menu-row-handle')) return prev;
    return firstChip;
  }

  function rowLastEl(ids) {
    var last = null;
    for (var i = 0; i < ids.length; i++) {
      var c = chipElById(ids[i]);
      if (c) last = c;
    }
    return last;
  }

  function placeRowIndicator(idx) {
    if (!rowIndicator || !chipsBox) return;
    var rows = rowsFromOrder(order);
    var anchor = (idx >= 0 && idx < rows.length) ? rowFirstEl(rows[idx].ids) : null;
    if (anchor) { chipsBox.insertBefore(rowIndicator, anchor); return; }
    var lastEl = rows.length ? rowLastEl(rows[rows.length - 1].ids) : null;
    if (lastEl) chipsBox.insertBefore(rowIndicator, lastEl.nextSibling);
    else chipsBox.appendChild(rowIndicator);
  }

  /* 整行重排：把 fromIndex 行移动到 toIndex 处（toIndex 基于原行数组的插入下标） */
  function reorderRows(fromIndex, toIndex) {
    var rows = rowsFromOrder(order);
    if (fromIndex < 0 || fromIndex >= rows.length) return;
    var moved = rows.splice(fromIndex, 1)[0];
    if (toIndex > fromIndex) toIndex -= 1;
    if (toIndex < 0) toIndex = 0;
    if (toIndex > rows.length) toIndex = rows.length;
    rows.splice(toIndex, 0, moved);
    order = flattenRows(rows);
    renderChips();
  }

  /* ======================================================================
     十一、拖动事件分发
     ====================================================================== */

  function onDragMove(event) {
    if (drag) {
      if (!drag.active) {
        var dx = event.clientX - drag.startX;
        var dy = event.clientY - drag.startY;
        if (Math.abs(dx) < DRAG_THRESHOLD && Math.abs(dy) < DRAG_THRESHOLD) return;
        activateChipDrag(event.clientX, event.clientY);
      }
      drag.chip.style.left = (event.clientX - drag.grabX) + 'px';
      drag.chip.style.top = (event.clientY - drag.grabY) + 'px';
      placeIndicator(drag.group, computeDropIndex(drag.group, event.clientX, event.clientY));
      return;
    }
    if (rowDrag) {
      if (!rowDrag.active) {
        var rdx = event.clientX - rowDrag.startX;
        var rdy = event.clientY - rowDrag.startY;
        if (Math.abs(rdx) < DRAG_THRESHOLD && Math.abs(rdy) < DRAG_THRESHOLD) return;
        activateRowDrag();
      }
      placeRowIndicator(computeRowDropIndex(event.clientY));
    }
  }

  function onDragUp(event) {
    if (drag) { endChipDrag(event); return; }
    if (rowDrag) { endRowDrag(event); return; }
  }

  function endChipDrag(event) {
    var d = drag;
    drag = null;
    if (d && d.active && event) {
      var pos = computeDropIndex(d.group, event.clientX, event.clientY);
      removeIndicator();
      unfloatChip(d.chip);
      dragging = false;
      reorderWithinRow(d.id, pos);
      persistOrder();
      return;
    }
    removeIndicator();
    dragging = false;
  }

  function endRowDrag(event) {
    var d = rowDrag;
    rowDrag = null;
    removeRowIndicator();
    if (d) setRowDragStyle(d.group, false);
    dragging = false;
    if (d && d.active && event) {
      var idx = computeRowDropIndex(event.clientY);
      reorderRows(d.index, idx);
      persistOrder();
    }
  }

  /* 拖动被打断（收起面板等）时复位拖动痕迹 */
  function cancelDrag() {
    if (drag && drag.active) unfloatChip(drag.chip);
    if (rowDrag && rowDrag.group) setRowDragStyle(rowDrag.group, false);
    drag = null;
    rowDrag = null;
    dragging = false;
    removeIndicator();
    removeRowIndicator();
  }

  /* 顺序写入 settings.json（字段 menuOrder = 全部 id 的平铺数组）；宿主不可用时只更新本地 */
  function persistOrder() {
    if (!Host || !Host.post || !Host.isAvailable || !Host.isAvailable()) return;
    Host.post('setSettings', { menuOrder: order.slice() });
  }

  /* ======================================================================
     十二、抽屉内右键自绘菜单（「符号排序」入口）
     ====================================================================== */

  function ensureContextMenu() {
    if (contextMenu) return contextMenu;
    contextMenu = doc.createElement('div');
    contextMenu.className = 'menu-context';
    contextMenu.hidden = true;
    contextMenu.setAttribute('role', 'menu');

    var item = doc.createElement('button');
    item.type = 'button';
    item.className = 'menu-context__item';
    item.setAttribute('role', 'menuitem');
    item.textContent = '符号排序';
    item.addEventListener('click', function () {
      hideContextMenu();
      enterSort();
    });
    contextMenu.appendChild(item);

    var parent = doc.body || doc.documentElement;
    if (parent && parent.appendChild) parent.appendChild(contextMenu);
    return contextMenu;
  }

  function showContextMenu(x, y) {
    var el = ensureContextMenu();
    el.hidden = false;
    /* 先按 0,0 定位量尺寸，再夹紧到视口内 */
    el.style.left = '0px';
    el.style.top = '0px';
    var w = el.offsetWidth || 0;
    var h = el.offsetHeight || 0;
    var vw = root.innerWidth || 0;
    var vh = root.innerHeight || 0;
    if (x + w > vw - 4) x = vw - 4 - w;
    if (y + h > vh - 4) y = vh - 4 - h;
    if (x < 4) x = 4;
    if (y < 4) y = 4;
    el.style.left = x + 'px';
    el.style.top = y + 'px';
  }

  function hideContextMenu() {
    if (contextMenu) contextMenu.hidden = true;
  }

  function isContextMenuOpen() {
    return !!(contextMenu && contextMenu.hidden === false);
  }

  /* 全窗口右键：一律 preventDefault 屏蔽浏览器默认菜单（输入框 / 结果 / 条格 / 底栏
     均在 WebView2 内容区内）；唯一例外是「已展开且非排序」的抽屉区域 → 弹我们的菜单。 */
  function onContextMenu(event) {
    if (!event) return;
    if (event.preventDefault) event.preventDefault();
    var target = event ? event.target : null;
    if (open && !sorting && drawer && isInside(drawer, target)) {
      showContextMenu(event.clientX, event.clientY);
    } else {
      hideContextMenu();
    }
    if (event.stopPropagation) event.stopPropagation();
  }

  /* ======================================================================
     十三、全局事件：Esc 关右键菜单（不收起面板）；点击别处关右键菜单
     ====================================================================== */

  function onDocKeyDown(event) {
    if (!event || event.key !== 'Escape') return;
    /* Esc 不再收起面板、排序模式也不响应；仅用于关闭右键自绘菜单（若有）。 */
    if (isContextMenuOpen()) {
      hideContextMenu();
      if (event.preventDefault) event.preventDefault();
      if (event.stopPropagation) event.stopPropagation();
    }
  }

  function onDocMouseDown(event) {
    if (!isContextMenuOpen()) return;
    var target = event ? event.target : null;
    if (contextMenu && isInside(contextMenu, target)) return;   /* 点在菜单内部：交给其点击 */
    hideContextMenu();
  }

  /* ======================================================================
     十四、宿主设置回包（读取 menuOrder）
     ====================================================================== */

  function onSettingsMsg(msg) {
    if (!msg) return;
    if (dragging) return;                        /* 拖动中不打断 */
    if (msg.menuOrder === undefined || msg.menuOrder === null) return;
    order = normalizeOrder(msg.menuOrder);
    renderChips();
  }

  /* ======================================================================
     十五、初始化
     ====================================================================== */

  function init() {
    toggle = doc.getElementById('menu-toggle');
    drawer = doc.getElementById('menu-drawer');
    workspace = doc.getElementById('workspace');
    chipsBox = doc.getElementById('menu-chips');
    hintEl = doc.getElementById('menu-drawer-hint');
    sortFoot = doc.getElementById('menu-drawer-foot');
    sortResetBtn = doc.getElementById('menu-sort-reset');
    sortSaveBtn = doc.getElementById('menu-sort-save');
    if (!toggle || !drawer || !workspace || !chipsBox) return;

    /* 先用默认顺序渲染（普通浏览器预览 / 宿主尚未回包时都能用）；
       宿主回发 settings 后再按 menuOrder 容错归一重排。 */
    order = normalizeOrder(null);
    renderChips();
    applyToggleTip();                            /* 收起态：提示「运算符号」 */

    /* 动画结束（宽度落定）后再兜底重排一次：即使逐帧重排被浏览器节流
       （窗口被遮挡 / 后台标签等 rAF 停摆情形），也保证最终行高与宽度一致。 */
    if (drawer.addEventListener) {
      drawer.addEventListener('transitionend', function (event) {
        if (event && event.propertyName && event.propertyName !== 'width') return;
        if (App && App.reflow) App.reflow();
      });
    }

    toggle.addEventListener('click', onToggleClick);
    chipsBox.addEventListener('click', onChipsClick);
    chipsBox.addEventListener('mousedown', onChipsMouseDown);
    if (sortResetBtn) sortResetBtn.addEventListener('click', onSortReset);
    if (sortSaveBtn) sortSaveBtn.addEventListener('click', onSortSave);
    doc.addEventListener('keydown', onDocKeyDown);
    doc.addEventListener('mousedown', onDocMouseDown);
    doc.addEventListener('contextmenu', onContextMenu);
    doc.addEventListener('mousemove', onDragMove);
    doc.addEventListener('mouseup', onDragUp);

    if (Persist && Persist.onSettings) Persist.onSettings(onSettingsMsg);
  }

  /* ======================================================================
     十六、对外 API（自测 / 其它任务）
     ====================================================================== */

  root.OperationMenu = {
    open: openPanel,
    close: closePanel,
    isOpen: function () { return open; },
    isSorting: function () { return sorting; },
    enterSort: enterSort,
    exitSort: exitSort,
    saveSort: onSortSave,
    resetSort: onSortReset,
    getOrder: function () { return order.slice(); },
    _internal: {
      BUTTONS: BUTTONS,
      defaultOrder: defaultOrder,
      normalizeOrder: normalizeOrder,
      rowsFromOrder: rowsFromOrder,
      defaultGroupOrder: defaultGroupOrder
    }
  };

  if (doc.readyState === 'loading') {
    doc.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }

})(typeof window !== 'undefined' ? window : this);