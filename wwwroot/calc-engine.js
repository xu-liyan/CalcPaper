/* ==========================================================================
   计算稿纸 · 表达式计算引擎（calc-engine.js）
   --------------------------------------------------------------------------
   · 纯 JavaScript，零依赖，不联网，不使用 ES Module 语法
     （最终在 WebView2 中以 file:// 加载，ES Module 会被 CORS 拦截）
   · 通过全局对象 window.CalcEngine 暴露 API，供界面代码直接调用
   · 对外 API：
       CalcEngine.normalize(input)              → 归一化后的表达式字符串
       CalcEngine.evaluate(input, options)      → { ok:true, value:Number }
                                                | { ok:false, error:'empty'|'syntax'|'math', message?:String }
                                                （message 为具体数学错误文案，缺省时由调用方按 error 归类）
       CalcEngine.formatNumber(value, options)  → 数字格式化字符串（不含 "="）
       CalcEngine.formatResult(input, options)  → { ok:true, text:'4,175,270' }
                                                | { ok:false, error:..., text:'表达式错误'|'无法计算'|具体数学错误文案 }
     options 形如 { angleMode: 'deg' | 'rad', useGrouping: true }，允许整体缺省：
       angleMode 默认 'deg'，useGrouping 默认 true
   · 语法：+ - * / ^ %、括号、一元正负号、后缀 !（阶乘）与 %（百分比）、
     函数 sqrt/√、cbrt/∛（立方根，支持负数）、sin/cos/tan、
     asin/acos/atan（别名 arcsin/arccos/arctan）、
     abs/round/floor/ceil、log/ln、mod/rem、常量 pi/π/e
   ========================================================================== */

(function (root) {
  'use strict';

  /* ======================================================================
     一、符号归一化
     ====================================================================== */

  /* 界面输入可能出现的符号别名 → 引擎可识别的半角符号 */
  var CHAR_MAP = {
    '\u00d7': '*',  // ×  乘号
    '\u2715': '*',  // ✕  乘号
    '\u2716': '*',  // ✖  粗乘号
    'x': '*',       // x  字母 x 作乘号
    'X': '*',       // X
    '\uff0a': '*',  // ＊ 全角星号
    '\u00f7': '/',  // ÷  除号
    '\uff0f': '/',  // ／ 全角斜杠
    '\uff08': '(',  // （ 全角左括号
    '\uff09': ')',  // ） 全角右括号
    '\uff0b': '+',  // ＋ 全角加号
    '\uff0d': '-',  // － 全角减号
    '\uff3e': '^',  // ＾ 全角脱字符
    '\u2212': '-'   // −  Unicode 数学减号
  };

  /* 空白字符集合：显式列出 Unicode 码点，不依赖 /\s/
     （不同 JS 引擎对 \s 的 Unicode 覆盖范围不一致，例如旧引擎不认 U+3000 全角空格） */
  var WHITESPACE_RE = /[\x09\x0a\x0b\x0c\x0d\x20\u00a0\u1680\u2000-\u200a\u200b\u2028\u2029\u202f\u205f\u3000\ufeff]/;

  /* 归一化：字符别名转半角 + 删除全部空白（含全角空格），仅用于求值内部 */
  function normalize(input) {
    var s = (input === null || input === undefined) ? '' : String(input);
    var out = '';
    for (var i = 0; i < s.length; i++) {
      var ch = s.charAt(i);
      if (WHITESPACE_RE.test(ch)) continue;              // 空白一律忽略
      out += (CHAR_MAP[ch] !== undefined) ? CHAR_MAP[ch] : ch;
    }
    return out;
  }

  /* ======================================================================
     二、错误工具
     ====================================================================== */

  /* 抛出带分类的内部错误；可由 evaluate 统一捕获，绝不允许泄漏到调用方
     message 为可选的具体文案（如"除数不能为 0"），缺省时按 kind 归类 */ 
  function fail(kind, message) {
    var err = new Error(kind);
    err.calcError = kind;
    if (message) err.calcMessage = message;
    throw err;
  }

  /* ======================================================================
     三、词法分析
     ====================================================================== */

  function isLetter(c) {
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c === '_';
  }
  function isNameChar(c) {
    return isLetter(c) || (c >= '0' && c <= '9');
  }

  /* 把归一化后的字符串切成 token 序列：num / name / op */
  function tokenize(src) {
    var tokens = [];
    var i = 0;
    var n = src.length;

    while (i < n) {
      var ch = src.charAt(i);

      /* Unicode 常量与根号别名：π → 常量 pi，√ → 函数 sqrt，∛ → 函数 cbrt（均需带括号） */
      if (ch === '\u03c0') { tokens.push({ type: 'name', value: 'pi' }); i++; continue; }
      if (ch === '\u221a') { tokens.push({ type: 'name', value: 'sqrt' }); i++; continue; }
      if (ch === '\u221b') { tokens.push({ type: 'name', value: 'cbrt' }); i++; continue; }

      /* 数字：整数、小数（含 .5 / 5. 这类宽松写法） */
      if ((ch >= '0' && ch <= '9') || ch === '.') {
        var start = i;
        var dotSeen = false;
        while (i < n) {
          var c = src.charAt(i);
          if (c >= '0' && c <= '9') {
            i++;
          } else if (c === '.') {
            if (dotSeen) break;                          // 第二个小数点终止当前数字
            dotSeen = true;
            i++;
          } else {
            break;
          }
        }
        var text = src.substring(start, i);
        if (text === '.') fail('syntax');
        tokens.push({ type: 'num', value: parseFloat(text) });
        continue;
      }

      /* 标识符：函数名与常量名 */
      if (isLetter(ch)) {
        var st = i;
        while (i < n && isNameChar(src.charAt(i))) i++;
        tokens.push({ type: 'name', value: src.substring(st, i).toLowerCase() });
        continue;
      }

      /* 运算符与括号（% 既作后缀百分比又作二元取模；! 为后缀阶乘；, 分隔函数参数） */
      if (ch === '+' || ch === '-' || ch === '*' || ch === '/' ||
          ch === '^' || ch === '%' || ch === '!' || ch === ',' ||
          ch === '(' || ch === ')') {
        tokens.push({ type: 'op', value: ch });
        i++;
        continue;
      }

      fail('syntax');                                    // 非法字符
    }

    return tokens;
  }

  /* ======================================================================
     四、语法分析 + 求值（递归下降，边解析边计算）
     ----------------------------------------------------------------------
     文法（优先级由低到高）：
       expr    := term (('+' | '-') term)*
       term    := unary (('*' | '/' | '%') unary)*      % 此处为二元取模
       unary   := ('+' | '-') unary | power
       power   := postfix ('^' unary)?                 右结合，且指数可带一元符号
       postfix := primary ('!' | '%')*                 后缀，优先级高于 ^，可连续
       primary := num | 常量 | 函数 '(' args ')' | '(' expr ')'
     由此保证：* / 高于 + -；^ 右结合且高于一元负号，
       即 -2^2 = -(2^2) = -4，2^-1 = 0.5；
       后缀 ! / % 绑定高于 ^，即 2^3! = 2^(3!) = 64、5!^2 = (5!)^2 = 14400。
     '%' 的两种用法在语法层区分：postfix 中若 % 之后紧跟数字、常量（π/pi/e）
       或左括号，说明 % 两侧都有操作数 → 不在后缀处消费，留给 term 作二元取模；
       否则（+ - * / ^ % 等运算符、右括号、逗号、函数名、表达式结束）一律视为后缀
       百分比。见 isPostfixPercent。
     ====================================================================== */

  /* 函数表：键为函数名，值为允许的参数个数列表（用于校验元数） */
  var FUNCTIONS = {
    sqrt:   [1], cbrt: [1],
    sin:    [1], cos: [1], tan: [1],
    asin:   [1], acos: [1], atan: [1],
    arcsin: [1], arccos: [1], arctan: [1],
    abs:    [1], round: [1], floor: [1], ceil: [1],
    ln:     [1], log: [1, 2],
    mod:    [2], rem: [2]
  };

  /* 只认自有键，避免 constructor 之类原型链上的名字被当成函数 */
  function isFunction(name) {
    return Object.prototype.hasOwnProperty.call(FUNCTIONS, name);
  }

  /* 角度 → 弧度（angleMode 为 'rad' 时原样返回） */
  function toRadians(x, useDeg) {
    return useDeg ? (x * Math.PI / 180) : x;
  }

  /* 弧度 → 角度（angleMode 为 'rad' 时原样返回）：反三角函数结果用 */
  function fromRadians(x, useDeg) {
    return useDeg ? (x * 180 / Math.PI) : x;
  }

  var TRIG_EPS = 1e-12;    // 三角函数结果的近零吸附阈值

  /* 三角函数结果的近零吸附：整 90° 倍角（如 cos(90)、sin(180)、tan(180)）会残留
     ~1e-17 量级的浮点误差，这里把绝对值小于 TRIG_EPS 的结果归为 0。
     仅用于 sin/cos/tan，不用于 sqrt 与四则、幂运算，以免掩盖真实的极小值。 */
  function snapTrig(r) {
    return (Math.abs(r) < TRIG_EPS) ? 0 : r;
  }

  /* 截断取整（向 0 取整），等价于 Math.trunc，显式实现以兼容旧引擎 */
  function trunc(v) {
    return (v < 0) ? Math.ceil(v) : Math.floor(v);
  }

  /* 数学取模：结果符号随除数，即 a - b * floor(a/b) */
  function mathMod(a, b) {
    if (b === 0) fail('math', '除数不能为 0');
    return a - b * Math.floor(a / b);
  }

  /* 取余：结果符号随被除数，即 a - b * trunc(a/b)（C#/JS 风格） */
  function mathRem(a, b) {
    if (b === 0) fail('math', '除数不能为 0');
    return a - b * trunc(a / b);
  }

  /* 阶乘：仅非负整数（含 0! = 1）；>170 时超出双精度可表示范围 */
  function factorial(n) {
    if (!isFinite(n) || n < 0 || n !== Math.floor(n)) fail('math', '阶乘仅支持非负整数');
    if (n > 170) fail('math', '结果过大，超出计算范围');
    var r = 1;
    for (var i = 2; i <= n; i++) r *= i;
    return r;
  }

  /* 以 10 为底的对数 */
  function log10(v) {
    return Math.log(v) / Math.LN10;
  }

  var ASIN_ACOS_DOMAIN_MSG = '反正弦/反余弦的输入需在 -1 到 1 之间';
  var LOG_ARG_MSG = '对数的真数必须大于 0';
  var LOG_BASE_MSG = '对数的底数必须大于 0 且不等于 1';

  /* 函数求值；args 为参数数组，遇到未定义情形抛出 math 类错误 */
  function evalFunction(name, args, useDeg) {
    var arity = FUNCTIONS[name];
    var matched = false;
    for (var k = 0; k < arity.length; k++) {
      if (arity[k] === args.length) { matched = true; break; }
    }
    if (!matched) fail('syntax');                        // 参数个数不符

    for (var j = 0; j < args.length; j++) {
      if (!isFinite(args[j])) fail('math');
    }

    var a = args[0];
    var r;
    switch (name) {
      case 'sqrt':
        if (a < 0) fail('math');                         // 负数开方未定义
        r = Math.sqrt(a);
        break;
      case 'cbrt':
        r = Math.cbrt(a);                                // 立方根，负数有实根（cbrt(-8) = -2）
        break;
      case 'sin':
        r = snapTrig(Math.sin(toRadians(a, useDeg)));
        break;
      case 'cos':
        r = snapTrig(Math.cos(toRadians(a, useDeg)));
        break;
      case 'tan':
        var rad = toRadians(a, useDeg);
        /* 无定义判定必须早于近零吸附：90°、270°（弧度制 pi/2）处 cos 为 0，tan 无定义 */
        if (Math.abs(Math.cos(rad)) < TRIG_EPS) fail('math');
        r = snapTrig(Math.tan(rad));
        break;
      case 'asin':
      case 'arcsin':
        if (Math.abs(a) > 1) fail('math', ASIN_ACOS_DOMAIN_MSG);
        r = fromRadians(Math.asin(a), useDeg);
        break;
      case 'acos':
      case 'arccos':
        if (Math.abs(a) > 1) fail('math', ASIN_ACOS_DOMAIN_MSG);
        r = fromRadians(Math.acos(a), useDeg);
        break;
      case 'atan':
      case 'arctan':
        r = fromRadians(Math.atan(a), useDeg);
        break;
      case 'abs':
        r = Math.abs(a);
        break;
      case 'round':
        r = (a < 0 ? -1 : 1) * Math.round(Math.abs(a));  // 四舍五入，半数远离 0
        break;
      case 'floor':
        r = Math.floor(a);
        break;
      case 'ceil':
        r = Math.ceil(a);
        break;
      case 'ln':
        if (!(a > 0)) fail('math', LOG_ARG_MSG);
        r = Math.log(a);
        break;
      case 'log':
        if (args.length === 2) {
          var base = args[0], xtrue = args[1];
          if (!(xtrue > 0)) fail('math', LOG_ARG_MSG);
          if (!(base > 0) || base === 1) fail('math', LOG_BASE_MSG);
          r = Math.log(xtrue) / Math.log(base);
        } else {
          if (!(a > 0)) fail('math', LOG_ARG_MSG);
          r = log10(a);
        }
        break;
      case 'mod':
        r = mathMod(args[0], args[1]);
        break;
      case 'rem':
        r = mathRem(args[0], args[1]);
        break;
      default:
        fail('syntax');
    }

    if (!isFinite(r)) fail('math');
    return r;
  }

  /* 解析并求值 token 序列；angleMode 决定三角函数按角度还是弧度解释 */
  function parseAndEval(tokens, angleMode) {
    var pos = 0;
    var useDeg = (angleMode !== 'rad');                  // 缺省角度制

    function peek() {
      return (pos < tokens.length) ? tokens[pos] : null;
    }
    function isOp(t, v) {
      return !!t && t.type === 'op' && t.value === v;
    }
    function eatOp(op) {
      if (!isOp(peek(), op)) fail('syntax');
      pos++;
    }

    function parseExpr() {
      var v = parseTerm();
      for (;;) {
        var t = peek();
        if (isOp(t, '+') || isOp(t, '-')) {
          pos++;
          var rhs = parseTerm();
          v = isOp(t, '+') ? (v + rhs) : (v - rhs);
        } else {
          break;
        }
      }
      return v;
    }

    function parseTerm() {
      var v = parseUnary();
      for (;;) {
        var t = peek();
        if (isOp(t, '*') || isOp(t, '/') || isOp(t, '%')) {
          pos++;
          var rhs = parseUnary();
          if (isOp(t, '*')) {
            v = v * rhs;
          } else if (isOp(t, '/')) {
            if (rhs === 0) fail('math', '除数不能为 0'); // 除数为 0
            v = v / rhs;
          } else {
            v = mathMod(v, rhs);                         // 二元 % 为数学取模
          }
        } else {
          break;
        }
      }
      return v;
    }

    function parseUnary() {
      var t = peek();
      if (isOp(t, '+') || isOp(t, '-')) {
        pos++;
        var v = parseUnary();
        return isOp(t, '-') ? -v : v;
      }
      return parsePower();
    }

    function parsePower() {
      var base = parsePostfix();
      if (isOp(peek(), '^')) {
        pos++;
        var exp = parseUnary();                          // 右结合；允许 2^-1
        var r = Math.pow(base, exp);
        if (!isFinite(r)) fail('math');                  // 溢出 / 未定义（如负数开偶次方）
        return r;
      }
      return base;
    }

    /* 后缀运算符：! 阶乘、% 百分比；绑定优先级高于 ^，可连续后缀 */
    function parsePostfix() {
      var v = parsePrimary();
      for (;;) {
        var t = peek();
        if (isOp(t, '!')) {
          pos++;
          v = factorial(v);
        } else if (isOp(t, '%') && isPostfixPercent()) {
          pos++;
          v = v / 100;
        } else {
          break;
        }
      }
      return v;
    }

    /* 区分后缀百分比与二元取模：pos 当前指向 '%'。
       仅当其后紧跟数字、常量（π/pi/e）或左括号时，才视为二元取模
       （交给 term 处理）；其余情形（+ - * / ^ % 等运算符、右括号、逗号、
       函数名、表达式结束）一律视为后缀百分比。
       由此：10%3、-7%3、(10%)%3 按二元取模；10%、200+10%、50%*2、
       10%+3、50%+25%、7%-3 均按后缀百分比。 */
    function isPostfixPercent() {
      var nxt = (pos + 1 < tokens.length) ? tokens[pos + 1] : null;
      if (!nxt) return true;                             // 表达式结束 → 后缀
      if (nxt.type === 'num') return false;              // 后接数字 → 二元取模
      if (nxt.type === 'name' && (nxt.value === 'pi' || nxt.value === 'e')) return false; // 后接常量 → 二元取模
      if (nxt.type === 'op' && nxt.value === '(') return false; // 后接左括号 → 二元取模
      return true;                                       // 其它运算符 / 右括号 / 逗号 / 函数名 → 后缀
    }

    function parsePrimary() {
      var t = peek();
      if (!t) fail('syntax');                            // 表达式不完整

      if (t.type === 'num') {
        pos++;
        return t.value;
      }

      if (t.type === 'name') {
        pos++;
        var name = t.value;
        if (name === 'pi') return Math.PI;
        if (name === 'e') return Math.E;
        if (isFunction(name)) {
          eatOp('(');                                    // 函数必须带括号
          var args = [parseExpr()];
          while (isOp(peek(), ',')) {                    // 多参数：log(b,x)/mod(a,b)/rem(a,b)
            pos++;
            args.push(parseExpr());
          }
          eatOp(')');
          return evalFunction(name, args, useDeg);
        }
        fail('syntax');                                  // 未知标识符
      }

      if (isOp(t, '(')) {
        pos++;
        var v = parseExpr();
        eatOp(')');                                      // 括号不闭合 → syntax
        return v;
      }

      fail('syntax');
    }

    var value = parseExpr();
    if (pos !== tokens.length) fail('syntax');           // 有剩余 token → 非法表达式
    if (!isFinite(value)) fail('math');
    return value;
  }

  /* ======================================================================
     五、对外：求值
     ====================================================================== */

  function evaluate(input, options) {
    options = options || {};
    try {
      var src = normalize(input);
      if (src === '') return { ok: false, error: 'empty' };
      var tokens = tokenize(src);
      var value = parseAndEval(tokens, options.angleMode);
      if (!isFinite(value)) return { ok: false, error: 'math' };
      return { ok: true, value: value };
    } catch (err) {
      /* 引擎内部任何异常都不允许抛给调用方，一律归类返回；
         若错误带有具体文案（如"除数不能为 0"）则一并返回，供界面直接显示 */
      var kind = (err && err.calcError) ? err.calcError : 'syntax';
      var out = { ok: false, error: kind };
      if (err && err.calcMessage) out.message = err.calcMessage;
      return out;
    }
  }

  /* ======================================================================
     六、对外：数字格式化
     ====================================================================== */

  var SCI_HIGH = 1e15;    // 绝对值 ≥ 1e15 用科学计数法
  var SCI_LOW = 1e-9;     // 非 0 且绝对值 < 1e-9 用科学计数法

  /* 浮点容差归整：消除 0.1+0.2 之类的末位误差 */
  function tidy(v) {
    return Number(v.toPrecision(12));
  }

  /* 科学计数法；裁掉尾数多余的 0，保证输出简洁（如 1e+21 而非 1.000000e+21） */
  function toSciString(v) {
    var s = v.toExponential();
    s = s.replace(/(\.\d*?)0+e/, '$1e');
    s = s.replace(/\.e/, 'e');
    return s;
  }

  /* 仅对整数部分加千分位（3 位一组的逗号），小数部分原样保留 */
  function groupInteger(intDigits) {
    var sign = '';
    var s = intDigits;
    if (s.charAt(0) === '-') {
      sign = '-';
      s = s.substring(1);
    }
    var out = '';
    var count = 0;
    for (var i = s.length - 1; i >= 0; i--) {
      out = s.charAt(i) + out;
      count++;
      if (count % 3 === 0 && i > 0) out = ',' + out;
    }
    return sign + out;
  }

  function formatNumber(value, options) {
    options = options || {};
    var useGrouping = (options.useGrouping !== false);    // 默认开启

    var v = (typeof value === 'number') ? value : Number(value);
    if (!isFinite(v)) return String(v);                  // 非有限值：正常流程不会走到这里

    v = tidy(v);                                         // 浮点容差归整
    v = (v === 0) ? 0 : v;                               // -0 显示为 0

    var abs = Math.abs(v);
    if (abs >= SCI_HIGH || (abs > 0 && abs < SCI_LOW)) {
      return toSciString(v);
    }

    var s;
    if (abs < 1e-6) {
      /* 极小值（1e-9 ~ 1e-6）JS 会输出指数形式，这里展开成小数再去掉多余尾随零 */
      s = v.toFixed(20).replace(/0+$/, '').replace(/\.$/, '');
    } else {
      /* 最短往返表示：天然不含多余尾随零（4.500 → 4.5） */
      s = String(v);
    }

    var dot = s.indexOf('.');
    var intPart = (dot < 0) ? s : s.substring(0, dot);
    var fracPart = (dot < 0) ? '' : s.substring(dot);
    return (useGrouping ? groupInteger(intPart) : intPart) + fracPart;
  }

  /* ======================================================================
     七、对外：便捷组合
     ====================================================================== */

  function formatResult(input, options) {
    var r = evaluate(input, options);
    if (!r.ok) {
      if (r.error === 'empty') {
        return { ok: false, error: 'empty', text: '' };  // 界面只显示 "="，不显示文案
      }
      if (r.message) {
        return { ok: false, error: r.error, text: r.message };  // 具体数学错误文案
      }
      if (r.error === 'math') {
        return { ok: false, error: 'math', text: '无法计算' };
      }
      return { ok: false, error: 'syntax', text: '表达式错误' };
    }
    return { ok: true, text: formatNumber(r.value, options) };
  }

  /* ======================================================================
     八、导出
     ====================================================================== */

  root.CalcEngine = {
    normalize: normalize,
    evaluate: evaluate,
    formatNumber: formatNumber,
    formatResult: formatResult
  };

})(typeof window !== 'undefined' ? window : this);