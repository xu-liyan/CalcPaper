/* ==========================================================================
   计算稿纸 · 全局提示气泡（tooltip.js）
   --------------------------------------------------------------------------
   · 纯 JavaScript（ES5 风格：只用 var 与 function），零依赖，不使用 ES Module
   · 加载顺序：bridge.js → calc-engine.js → app.js → tooltip.js → persist.js → settings.js
   · 职责：全页统一的自定义提示气泡，替代浏览器原生 title
       1) 全页唯一气泡元素（懒创建），position: fixed、pointer-events: none、高 z-index
       2) 事件委托：document 上监听 mouseover / mousemove / mouseout —— 命中任意
          带 data-tip 的祖先元素即可，因此 app.js 运行时动态生成的备注按钮也能生效
       3) 位置：默认鼠标正上方水平居中；上方空间不足时翻到正下方居中；
          左右贴边时夹紧在视口内
       4) 鼠标移出元素或移出窗口立即隐藏
       5) 窗口隐藏 / 最小化 / 失焦 / 标签页切走时立即隐藏，避免"窗口恢复后鼠标
          并不在按钮上却残留气泡"；同时给根元素加 'hover-off' 类中和 :hover 视觉，
          避免"恢复窗口后按钮残留悬停背景色"，真实鼠标移动 / 移入后自动移除
       6) Tooltip.flash(text, x, y, ms)：临时气泡（如双击复制成功的"已复制"），
          复用同一气泡元素与定位规则，额外带 .tip-bubble--flash（浅绿底 + 深绿字），
          到点自动消失；期间若鼠标悬停到别的 [data-tip] 元素，以悬停为准
   · 宿主内（WebView2）与普通浏览器预览均生效
   ========================================================================== */

(function (root) {
  'use strict';

  var doc = root.document || null;
  if (!doc) return;

  /* 气泡与鼠标之间的间距（px） */
  var GAP = 24;
  /* 距视口边缘的最小留白（px） */
  var EDGE = 6;

  /* 悬停抑制类名：窗口隐藏 / 最小化 / 失焦 / 标签页切走时加到 <html>，
     由 style.css 中和所有 :hover 视觉；真实鼠标移动 / 移入后移除。 */
  var HOVER_OFF_CLASS = 'hover-off';

  /* 全页唯一气泡元素（懒创建） */
  var bubble = null;
  /* 当前正在显示气泡的目标元素 */
  var current = null;

  /* 最近一次鼠标位置：flash 缺少坐标时兜底使用 */
  var lastX = null;
  var lastY = null;

  /* 临时气泡（flash）状态：flashing 为真时气泡由计时器负责收起 */
  var flashing = false;
  var flashTimer = null;

  /* ======================================================================
     一、位置计算（纯函数，便于独立校验）
     输入：鼠标坐标 x/y、气泡尺寸 w/h、视口尺寸 viewW/viewH
     输出：{ left, top, below }
       · 默认：鼠标正上方水平居中（left = x - w/2，top = y - h - GAP）
       · 上方空间不足（top 越过上边缘留白）→ 翻到鼠标正下方居中
       · 左右越界 → 夹紧在视口内
     ====================================================================== */
  function computePosition(x, y, w, h, viewW, viewH) {
    var left = x - w / 2;
    var top = y - h - GAP;
    var below = false;

    /* 上方放不下：翻到鼠标正下方 */
    if (top < EDGE) {
      top = y + GAP;
      below = true;
    }

    /* 左右夹紧在视口内 */
    var maxLeft = viewW - EDGE - w;
    if (left > maxLeft) left = maxLeft;
    if (left < EDGE) left = EDGE;
    /* 视口比气泡还窄：退化为左对齐 EDGE，避免负偏移 */
    if (maxLeft < EDGE) left = EDGE;

    /* 下方也越界时，尽量不出视口下沿 */
    if (top + h > viewH - EDGE) {
      var clamped = viewH - EDGE - h;
      top = (clamped < EDGE) ? EDGE : clamped;
    }

    return { left: left, top: top, below: below };
  }

  /* ======================================================================
     二、气泡元素
     ====================================================================== */

  function ensureBubble() {
    if (bubble) return bubble;
    bubble = doc.createElement('div');
    bubble.className = 'tip-bubble';
    if (bubble.setAttribute) bubble.setAttribute('role', 'tooltip');
    if (bubble.style) {
      bubble.style.position = 'fixed';
      bubble.style.display = 'none';
    }
    var parent = doc.body || doc.documentElement;
    if (parent && parent.appendChild) parent.appendChild(bubble);
    return bubble;
  }

  /* 切换气泡形态：普通提示用 'tip-bubble'，「已复制」临时气泡额外带
     'tip-bubble--flash'（浅绿底 + 深绿字）。两者共用同一元素，靠类名区分配色。 */
  function setBubbleVariant(extra) {
    if (!bubble) return;
    bubble.className = extra ? ('tip-bubble ' + extra) : 'tip-bubble';
  }

  /* 按鼠标坐标重新定位（读取气泡实际尺寸后再算） */
  function place(x, y) {
    if (!bubble || !bubble.style) return;
    var w = bubble.offsetWidth || 0;
    var h = bubble.offsetHeight || 0;
    var pos = computePosition(x, y, w, h, root.innerWidth || 0, root.innerHeight || 0);
    bubble.style.left = pos.left + 'px';
    bubble.style.top = pos.top + 'px';
  }

  function show(el, x, y) {
    ensureBubble();
    if (!bubble) return;
    var text = el.getAttribute ? el.getAttribute('data-tip') : null;
    if (!text) { hide(); return; }

    /* 真实悬停优先于临时气泡：抢占 flash 的气泡并取消它的收起计时器，
       同时把气泡切回普通形态（去掉「已复制」的浅绿配色） */
    stopFlash();
    current = el;
    setBubbleVariant(null);
    if (bubble.textContent !== undefined) bubble.textContent = text;
    /* 先隐藏量尺寸（避免在旧位置闪一帧），定位后再显示 */
    if (bubble.style) {
      bubble.style.visibility = 'hidden';
      bubble.style.display = 'block';
    }
    place(x, y);
    if (bubble.style) bubble.style.visibility = 'visible';
  }

  function hide() {
    stopFlash();
    current = null;
    if (bubble && bubble.style) bubble.style.display = 'none';
  }

  /* 结束 flash（只清状态与计时器，不碰 DOM 显隐） */
  function stopFlash() {
    flashing = false;
    if (flashTimer) {
      clearTimeout(flashTimer);
      flashTimer = null;
    }
  }

  /* ======================================================================
     二之二、临时气泡（flash）
     用途：不依赖鼠标悬停的短暂提示（如"已复制"）。
       · 复用同一个气泡元素与同一套定位规则（含"上方空间不足翻到下方"与左右夹紧）
       · text 为空则不显示；x / y 缺失（非有限数）时退回最近一次鼠标位置
       · ms 缺省 1500ms，到点自动消失
       · 期间若鼠标悬停到别的 [data-tip] 元素 → 由 show() 抢占，定时器被取消
     ====================================================================== */
  function flash(text, x, y, ms) {
    ensureBubble();
    if (!bubble) return;
    var str = (text === null || text === undefined) ? '' : String(text);
    if (!str) return;

    stopFlash();
    flashing = true;
    current = null;
    /* 「已复制」等临时提示使用浅绿配色（独立类名，不影响普通提示） */
    setBubbleVariant('tip-bubble--flash');

    var px = (typeof x === 'number' && isFinite(x)) ? x : lastX;
    var py = (typeof y === 'number' && isFinite(y)) ? y : lastY;
    /* 连最近一次鼠标位置都没有（如键盘触发的复制）→ 不显示，避免定位到 0,0 */
    if (px === null || py === null) { flashing = false; return; }

    if (bubble.textContent !== undefined) bubble.textContent = str;
    if (bubble.style) {
      bubble.style.visibility = 'hidden';
      bubble.style.display = 'block';
    }
    place(px, py);
    if (bubble.style) bubble.style.visibility = 'visible';

    var duration = (typeof ms === 'number' && ms > 0) ? ms : 1500;
    flashTimer = setTimeout(function () {
      flashTimer = null;
      if (flashing) hide();
    }, duration);
  }

  /* 当前光标（或给定坐标）下的元素是否属于某个带 data-tip 的元素。
     用于 mousemove 校验：窗口从最小化/隐藏恢复后，事件目标可能仍停留在
     旧按钮上，必须用 elementFromPoint 复核真实命中。 */
  function tipAt(x, y, node) {
    var el = findTipTarget(node);
    if (el) return el;
    if (doc.elementFromPoint && typeof x === 'number' && isFinite(x) &&
        typeof y === 'number' && isFinite(y)) {
      return findTipTarget(doc.elementFromPoint(x, y));
    }
    return null;
  }

  /* ======================================================================
     二之三、悬停抑制（hover-off）
     ----------------------------------------------------------------------
     现象：最小化 / 隐藏后经任务栏或托盘恢复窗口，鼠标其实并未停在按钮上，
     但最小化 / 关闭等按钮仍保留 :hover 背景色（气泡已正确消失）。
     做法：收到 windowState=hidden|minimized、window.blur、document.hidden 时给
     根元素加 'hover-off' 类，由 style.css 中和所有 :hover 视觉；直到真实鼠标
     移动（mousemove）或真实移入（mouseover）后移除。
     ====================================================================== */

  function rootEl() {
    return doc ? doc.documentElement : null;
  }

  function hoverOff() {
    var el = rootEl();
    if (el && el.classList && el.classList.add) el.classList.add(HOVER_OFF_CLASS);
  }

  function hoverOn() {
    var el = rootEl();
    if (el && el.classList && el.classList.remove) el.classList.remove(HOVER_OFF_CLASS);
  }

  /* ======================================================================
     三、目标解析：从事件目标沿 parentNode 向上找带 data-tip 的元素
     （不用 closest，兼容旧引擎；文本节点无 getAttribute 会自动跳过）
     ====================================================================== */
  function findTipTarget(node) {
    var el = node;
    while (el && el.getAttribute) {
      var text = el.getAttribute('data-tip');
      if (text) return el;
      el = el.parentNode || null;
    }
    return null;
  }

  /* ======================================================================
     四、事件委托（document 级，动态生成的元素同样命中）
     ====================================================================== */

  function onMouseOver(event) {
    /* 真实鼠标移入：解除悬停抑制 */
    hoverOn();
    var el = tipAt(event ? event.clientX : null, event ? event.clientY : null,
                   event ? event.target : null);
    if (!el) { hide(); return; }
    if (el !== current) show(el, event.clientX, event.clientY);
  }

  function onMouseMove(event) {
    /* 真实鼠标移动：解除悬停抑制 */
    hoverOn();
    var x = event ? event.clientX : null;
    var y = event ? event.clientY : null;
    if (typeof x === 'number' && isFinite(x)) { lastX = x; }
    if (typeof y === 'number' && isFinite(y)) { lastY = y; }

    var el = tipAt(x, y, event ? event.target : null);
    /* 指针已不在任何带 data-tip 的元素上（含元素被移除、窗口恢复后事件目标
       仍停留在旧按钮的情形）→ 隐藏，避免残留气泡 */
    if (!el) { hide(); return; }
    if (el !== current) show(el, x, y);
    else place(x, y);
  }

  function onMouseOut(event) {
    /* 移到另一个仍带 data-tip 的元素（或其子节点）上时，交给
       mouseover / mousemove 更新；真正离开则立即隐藏 */
    var el = findTipTarget(event ? event.relatedTarget : null);
    if (!el) hide();
  }

  /* 窗口隐藏 / 最小化 / 标签页切走 / 失焦时立即收起气泡，并抑制 :hover 残留。
     （"窗口已不可见却残留提示"与"恢复后残留悬停底色"的根治手段；不依赖鼠标事件。） */
  function onWindowState(message) {
    if (!message) return;
    if (message.state === 'hidden' || message.state === 'minimized') {
      hide();
      hoverOff();
    }
    /* restored：不主动显示，等鼠标真实命中再显示 */
  }

  function onVisibilityChange() {
    if (doc.hidden) {
      hide();
      hoverOff();
    }
  }

  /* 窗口失焦：收起气泡并抑制悬停底色 */
  function onBlur() {
    hide();
    hoverOff();
  }

  if (doc.addEventListener) {
    doc.addEventListener('mouseover', onMouseOver);
    doc.addEventListener('mousemove', onMouseMove);
    doc.addEventListener('mouseout', onMouseOut);
    doc.addEventListener('mouseleave', hide);
    doc.addEventListener('visibilitychange', onVisibilityChange);
  }
  if (root.addEventListener) {
    root.addEventListener('blur', onBlur);
  }
  if (root.Host && root.Host.on) {
    root.Host.on('windowState', onWindowState);
  }

  /* 对外：定位纯函数 / 目标解析（自测用）+ 临时气泡 */
  root.Tooltip = {
    flash: flash,
    _internal: {
      computePosition: computePosition,
      findTipTarget: findTipTarget
    }
  };

})(typeof window !== 'undefined' ? window : this);