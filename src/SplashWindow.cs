using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CalcPaper;

/// <summary>
/// 启动海报：像手机 App 启动海报那样——主窗口**立即显示**，海报是一个盖在
/// 主窗口之上的窗口，**铺满整个窗口**（含标题栏与底部按钮栏区域，底色与稿纸一致的白），
/// 在窗口中央演出「四则符号沿直线连跳 4 次入场 → 变白 → 镜像摆动」动画
/// （动画规格 = splash-animation-preview.svg 最终版，数值 1:1 复刻）。
///
/// 窗口风格：与主窗口**同尺寸同位置**（宿主传入主窗口的客户端矩形 = 圆角 region 形成的
/// 可见盒），自绘与主窗口一致的圆角 region（r=8，见 <see cref="ApplyRoundedRegion"/>），
/// 作为主窗口的 **owned window**（始终盖在主窗口之上），并且
/// <c>WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>
/// —— 不接收鼠标消息、不激活、不进 Alt+Tab 与任务栏。
/// 主窗口移动/缩放时宿主调用 <see cref="FollowOwnerBounds"/> 让海报同步跟随。
///
/// 动画时间线（毫秒，总时长 3.54s，播完保持最终静止态）：
///   符号  0→2.00s：四符号同时沿“起点→落位点”直线连跳 4 次（弧峰朝上，无错峰），颜色为橙黄 #F4B343，
///          opacity 0→1 在前 8% 完成、scale 0.70→1.0 在前 12% 完成（缩放中心=符号中心）；
///   定格  2.00s→2.12s：橙黄符号正立在**白底**上（此时还没有方块），角度 0°；
///   方块  2.12s→2.72s：opacity 0→1、scale 0.94→1.0（延后到落位定格之后；位于符号下层）；
///   变白  2.12s→2.72s（**版本 C：白色自两端向中心收拢 + 中段底色同步变浅 + 前沿柔和过渡**）：
///          底色本体 = Mix(#F4B343, #FFFFFF, p) 线性变浅（p 为 2.12s→2.72s 的线性进度）；
///          另以**同一套几何**叠画纯白覆盖层，但只保留“距符号中心 r = 50(1−p) 之外”的部分，
///          圆边界用 <see cref="ConvergeSteps"/> 级硬裁剪叠出约 <see cref="ConvergeEdge"/>px 柔和过渡；
///          飞行期（t ≤ 2.12s）与 p ≥ 1（终态）都不画该层 —— 防飞行期露白、防终态边缘变厚；
///   描边  2.12s→2.72s：符号下层叠一条同几何、宽 T+4=19.57 的**白色描边**，alpha 0→1（0.12s）
///          →保持→1→0（2.52→2.72s）；用于消除过渡期中段“符号填充与背景必然同色”的隐没；
///          2.72s 起完全消失，最终态与无描边时逐像素一致；
///   定格  2.72s→2.92s：方块满不透明、符号全白、描边已消失，角度 0°；
///   摆动  2.92s→3.54s：绕各符号几何中心，左列（+ ×）角度表
///          0→+9→−9→+6.5→−4→0（0/20/42/62/80/100%），右列（− =）取相反符号（关于 x=520 镜像）。
///   符号一律用**圆头线段**绘制（Pen 圆头），无直角端。
///
/// 时序控制：最短播放 3.54s；动画播完后若页面尚未就绪则**保持最后一帧等待**；
/// 页面就绪 **且** 动画播完后，200ms 逐帧降低整窗 alpha 淡出，随后关闭本窗口。
/// 不显示海报的场景（<c>--exit</c>、第二实例）在 Program 中已提前返回，不会走到这里。
/// </summary>
internal sealed class SplashWindow : Form
{
    // ======================================================================
    // 动画规格常量（设计基准：splash-animation-preview.svg，画布 1040×880）
    // ======================================================================

    /// <summary>设计基准画布宽（px）。</summary>
    private const float CanvasW = 1040f;

    /// <summary>设计基准画布高（px）。</summary>
    private const float CanvasH = 880f;

    // ---- 图标方块 ----
    private const float SquareCx = 520f;
    private const float SquareCy = 440f;
    private const float SquareSide = 240f;
    private const float SquareCorner = 52f;
    private static readonly Color SquareTop = Color.FromArgb(0xF7, 0xBD, 0x52);
    private static readonly Color SquareBottom = Color.FromArgb(0xF1, 0xAC, 0x3C);

    // ---- 符号几何 ----
    /// <summary>线条粗细 T（圆头半径 = T/2 = 7.785）。</summary>
    private const float Stroke = 15.57f;

    /// <summary>'+' / '−' / '=' 的轴向端点半长（±(L−T)/2 = ±28.215，圆头补足到总长 L=72）。</summary>
    private const float AxialHalf = 28.215f;

    /// <summary>白色描边线宽（= T + 4 = 19.57，等效每侧 2px）。</summary>
    private const float OutlineStroke = Stroke + 4f;

    /// <summary>'=' 两条中心线相对中心的偏移（中心距 31.70，内边距 16.13）。</summary>
    private const float EqualsHalf = 15.85f;

    /// <summary>'×' 每条线的端点半长（±39.902，rotate(±45°) 后外接盒 = 72×72）。</summary>
    private const float CrossHalf = 39.902f;

    // 符号飞入时的颜色（方块渐变 #F7BD52→#F1AC3C 的中间主色；与图标同族）
    private static readonly Color SymbolOrange = Color.FromArgb(0xF4, 0xB3, 0x43);
    private static readonly Color SymbolWhite = Color.White;

    // ---- 时间轴（毫秒）----
    private const int SymEnterMs = 2000;     // 符号入场总时长
    private const int SquareInStartMs = 2120; // 方块淡入起点（落位定格之后）
    private const int SquareInEndMs = 2720;  // 方块淡入终点（0.6s，与变白同步同时长）
    private const int WhitenStartMs = 2120;  // 变白起点（与方块同时开始）
    private const int WhitenEndMs = 2720;    // 变白终点（0.6s）
    private const int WobbleStartMs = 2920;  // 摆动起点（变色后定格 0.2s 再摆）
    private const int TotalMs = 3540;        // 总时长（摆动终点）
    private const int FadeMs = 200;          // 淡出时长
    private const int FrameIntervalMs = 15;  // 帧间隔 ~66fps（≤16ms）

    // ---- 白色描边不透明度 ramp（仅变色过渡期；2.72s 起归零）----
    private const int OutlineInStartMs = 2120;   // 0 -> 1 起点
    private const int OutlineInEndMs = 2240;     // 0 -> 1 终点（0.12s）
    private const int OutlineHoldEndMs = 2520;   // 保持 1 结束
    private const int OutlineOutEndMs = 2720;    // 1 -> 0 终点（0.20s；此后完全消失）

    // ---- 版本 C：白色自两端向中心收拢（白色本体覆盖层）----
    /// <summary>收拢圆的初值半径（p=0 时）。取 50 &gt; '×' 端部延展 47.687，保证飞行期不漏白。</summary>
    private const float ConvergeMaxRadius = 50f;

    /// <summary>柔和过渡的级数（逐级硬裁剪按 alpha 递进叠加）。</summary>
    private const int ConvergeSteps = 6;

    /// <summary>柔和过渡的总跨度（px），跨 r±1.0px。取 2px 使像素级最大跳变 ≈255/2≈128/255 以内（1.5px 会到 ~170）。</summary>
    private const float ConvergeEdge = 2.0f;

    /// <summary>与主窗口一致的圆角半径（px）。</summary>
    private const int WindowCornerRadius = 8;

    // ---- 弹跳分段（占入场 2.0s 的 s / t 比例与弧峰高度 H）----
    private static readonly float[] SegS = { 0.00f, 0.42f, 0.72f, 0.90f, 1.00f };
    private static readonly float[] SegT = { 0.00f, 0.40f, 0.66f, 0.85f, 1.00f };
    private static readonly float[] SegH = { 85f, 52f, 28f, 12f };

    // ---- 摆动关键帧（左列角度表；右列取相反符号）----
    private static readonly float[] WobT = { 0.00f, 0.20f, 0.42f, 0.62f, 0.80f, 1.00f };
    private static readonly float[] WobA = { 0f, 9f, -9f, 6.5f, -4f, 0f };

    // ---- 扩展窗口样式 ----
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint LWA_ALPHA = 0x00000002;

    /// <summary>四个符号的形态。</summary>
    private enum SymbolKind
    {
        Plus,
        Minus,
        Times,
        Equals,
    }

    /// <summary>一个符号的入场参数（起点、落位点、摆动符号）。</summary>
    private sealed class SymbolDef
    {
        public required SymbolKind Kind;

        /// <summary>起点（设计画布坐标）。</summary>
        public float StartX;
        public float StartY;

        /// <summary>落位点（设计画布坐标）。</summary>
        public float EndX;
        public float EndY;

        /// <summary>摆动角度符号：左列 +1，右列 −1（关于 x=520 镜像）。</summary>
        public float WobSign;
    }

    private static readonly SymbolDef[] Symbols =
    {
        new() { Kind = SymbolKind.Plus,   StartX = 60f,  StartY = 60f,  EndX = 464f, EndY = 384f, WobSign = 1f },
        new() { Kind = SymbolKind.Minus,  StartX = 980f, StartY = 60f,  EndX = 576f, EndY = 384f, WobSign = -1f },
        new() { Kind = SymbolKind.Times,  StartX = 60f,  StartY = 820f, EndX = 464f, EndY = 496f, WobSign = 1f },
        new() { Kind = SymbolKind.Equals, StartX = 980f, StartY = 820f, EndX = 576f, EndY = 496f, WobSign = -1f },
    };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = FrameIntervalMs };
    private readonly Stopwatch _watch = new();

    private bool _pageReady;
    private bool _fading;
    private bool _holding;
    private bool _finished;
    private bool _minPlayLogged;
    private long _fadeStart;
    private long _readyAt = -1;
    private float _lastAlpha = 1f;

    /// <summary>动画播完（含淡出）后触发；宿主在此时释放本窗口。</summary>
    public event EventHandler? Completed;

    public SplashWindow(Form owner, Rectangle ownerClientBounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;   // 与稿纸底色一致
        Text = "计算稿纸";
        Owner = owner;             // owned window：始终盖在主窗口之上、随其最小化而隐藏

        // 双缓冲自绘，避免闪烁
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer,
            true);

        // 就位（同尺寸同位置 + 圆角 region）
        FollowOwnerBounds(ownerClientBounds);

        // 强制创建句柄：触发 OnHandleCreated 完成 DPI / 尺寸 / region 初始化
        _ = Handle;

        // 先绘制首帧再显示，避免显示瞬间的空/黑块
        RenderNow();
        Show();

        _watch.Start();
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        Logger.Info($"[启动计时] 启动动画开始 t+{Program.StartupWatch.ElapsedMilliseconds}ms");
    }

    /// <summary>页面 ready：记录时刻；动画播完后即可开始淡出。</summary>
    public void NotifyPageReady()
    {
        if (_pageReady)
        {
            return;
        }

        _pageReady = true;
        _readyAt = _watch.ElapsedMilliseconds;
    }

    /// <summary>立即关闭（不淡出）：WebView2 初始化失败 / 隐藏到托盘等路径使用。</summary>
    public void Abort()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _timer.Stop();
        try
        {
            Close();
        }
        catch
        {
            // 关闭失败忽略（进程会回收）
        }
    }

    /// <summary>把海报对齐到主窗口当前可见盒（宿主在主窗口移动/缩放时调用）。</summary>
    public void FollowOwnerBounds(Rectangle bounds)
    {
        if (!IsHandleCreated)
        {
            // 句柄尚未创建（构造期）也先把尺寸/位置记下来
            Bounds = bounds;
            return;
        }

        if (Bounds == bounds)
        {
            return;
        }

        Bounds = bounds;
        ApplyRoundedRegion();
        Invalidate();   // 尺寸变化后整窗重绘（画布按比例缩放居中）
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedRegion();
        SetAlpha(1f);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyRoundedRegion();
        Invalidate();
    }

    /// <summary>
    /// 与主窗口一致的圆角：海报无厚框，其外框即可见盒，直接在 (0,0,w,h) 上做 r=8 圆角即可
    /// 与主窗口「可见盒圆角 region」完全重合（避免海报四角露出直角）。
    /// </summary>
    private void ApplyRoundedRegion()
    {
        int w = Width;
        int h = Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        int diameter = WindowCornerRadius * 2;
        IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, diameter, diameter);
        _ = SetWindowRgn(Handle, rgn, redraw: false);
    }

    /// <summary>设置整窗 alpha（淡出用；WS_EX_LAYERED + LWA_ALPHA）。</summary>
    private void SetAlpha(float alpha)
    {
        _lastAlpha = Math.Clamp(alpha, 0f, 1f);
        if (!IsHandleCreated)
        {
            return;
        }

        byte a = (byte)Math.Round(255 * _lastAlpha);
        _ = SetLayeredWindowAttributes(Handle, 0, a, LWA_ALPHA);
    }

    // ======================================================================
    // 时序推进
    // ======================================================================

    private void OnTick()
    {
        long t = _watch.ElapsedMilliseconds;

        if (!_minPlayLogged && t >= TotalMs)
        {
            _minPlayLogged = true;
            Logger.Info($"[启动计时] 启动动画最短时长已满 t+{Program.StartupWatch.ElapsedMilliseconds}ms");
        }

        if (!_fading)
        {
            // 动画播完（TotalMs）且页面已就绪 → 开始 200ms 淡出；
            // 动画先播完而页面未就绪 → 保持最后一帧等待；页面先就绪而动画未播完 → 动画继续播完。
            if (t >= TotalMs && _pageReady)
            {
                _fading = true;
                _fadeStart = t;
                _holding = false;
                Logger.Info(
                    $"启动海报开始淡出（动画 t+{t}ms，页面就绪 t+{_readyAt}ms，"
                    + $"程序 t+{Program.StartupWatch.ElapsedMilliseconds}ms）");
                Logger.Info($"[启动计时] 海报开始淡出 t+{Program.StartupWatch.ElapsedMilliseconds}ms");
            }
            else if (t >= TotalMs && _holding)
            {
                return;   // 动画已播完但页面未就绪：保持最后一帧，不再重绘
            }
            else if (t >= TotalMs)
            {
                _holding = true;
            }
        }

        if (_fading)
        {
            float u = (t - _fadeStart) / (float)FadeMs;
            SetAlpha(1f - u);
            if (u >= 1f)
            {
                Finish();
            }

            return;
        }

        RenderNow();
    }

    /// <summary>重绘当前帧（整窗，弹跳弧线覆盖到四角）。</summary>
    private void RenderNow()
    {
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        DrawFrame(e.Graphics, _watch.ElapsedMilliseconds);
    }

    private void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _timer.Stop();
        try
        {
            Close();
        }
        catch
        {
            // 关闭失败忽略
        }

        Logger.Info($"[启动计时] 海报关闭 t+{Program.StartupWatch.ElapsedMilliseconds}ms");
        Completed?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    // ======================================================================
    // 绘制（按时间的关键帧计算 + GDI+）
    // ======================================================================

    /// <summary>按给定时间绘制一帧（设计坐标 1040×880，按窗口尺寸等比缩放居中）。</summary>
    private void DrawFrame(Graphics g, long tMs)
    {
        // 白底（与稿纸底色一致）
        using (var brush = new SolidBrush(Color.White))
        {
            g.FillRectangle(brush, ClientRectangle);
        }

        float k = Math.Min(Width / CanvasW, Height / CanvasH);
        if (k <= 0f)
        {
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float ox = (Width - CanvasW * k) / 2f;
        float oy = (Height - CanvasH * k) / 2f;

        GraphicsState state = g.Save();
        g.TranslateTransform(ox, oy);
        g.ScaleTransform(k, k);

        DrawSquare(g, tMs);
        foreach (SymbolDef sym in Symbols)
        {
            DrawSymbol(g, sym, tMs);
        }

        g.Restore(state);
    }

    /// <summary>橙色圆角方块：2.12s→2.72s opacity 0→1、scale 0.94→1.0（0.6s；延后到落位定格之后；位于符号下层）。</summary>
    private static void DrawSquare(Graphics g, long tMs)
    {
        if (tMs < SquareInStartMs)
        {
            return;
        }

        float u = Math.Clamp((tMs - SquareInStartMs) / (float)(SquareInEndMs - SquareInStartMs), 0f, 1f);
        if (u <= 0.001f)
        {
            return;
        }

        float scale = Lerp(0.94f, 1f, u);
        float side = SquareSide * scale;
        var rect = new RectangleF(SquareCx - side / 2f, SquareCy - side / 2f, side, side);
        int a = (int)Math.Round(255 * u);

        using var path = RoundedRect(rect, SquareCorner * scale);
        using var brush = new LinearGradientBrush(
            rect,
            Color.FromArgb(a, SquareTop),
            Color.FromArgb(a, SquareBottom),
            LinearGradientMode.Vertical);
        g.FillPath(brush, path);
    }

    /// <summary>一个符号：位置（连跳）+ 淡入/缩放 + 配色（变白）+ 白色描边（仅过渡期）+ 摆动。</summary>
    private static void DrawSymbol(Graphics g, SymbolDef sym, long tMs)
    {
        float tNorm = Math.Clamp(tMs / (float)SymEnterMs, 0f, 1f);

        (float px, float py) = BouncePosition(sym, tNorm);
        float alpha = Math.Clamp(tNorm / 0.08f, 0f, 1f);                        // 前 8% 完成淡入
        float scale = Lerp(0.70f, 1f, Math.Clamp(tNorm / 0.12f, 0f, 1f));       // 前 12% 完成缩放
        float white = Math.Clamp((tMs - WhitenStartMs) / (float)(WhitenEndMs - WhitenStartMs), 0f, 1f);
        float angle = WobbleAngle(tMs) * sym.WobSign;

        if (alpha <= 0.001f)
        {
            return;
        }

        GraphicsState state = g.Save();
        g.TranslateTransform(px, py);   // 落位/运动位置（= 摆动中心 = 符号几何中心）
        g.RotateTransform(angle);       // 摆动
        g.ScaleTransform(scale, scale); // 入场缩放（含线宽）

        // 白色描边（下层）：同几何、宽 Stroke+4（等效每侧 2px），仅变色过渡期按 ramp 出现
        float outlineAlpha = OutlineAlpha(tMs);
        if (outlineAlpha > 0.002f)
        {
            using var outlinePen = new Pen(
                Color.FromArgb((int)Math.Round(255 * outlineAlpha), 255, 255, 255), OutlineStroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            DrawSymbolShape(g, sym.Kind, outlinePen);
        }

        // 符号本体（上层）：底色 = 由橙黄线性变浅到白
        Color color = Mix(SymbolOrange, SymbolWhite, white);
        color = Color.FromArgb((int)Math.Round(255 * alpha), color.R, color.G, color.B);
        using var pen = new Pen(color, Stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        DrawSymbolShape(g, sym.Kind, pen);

        // 版本 C：白色本体覆盖层 —— 只保留"距符号中心 r = 50(1−p) 之外"的白色部分，
        // 观感即"白色自两端（圆头处）向中心收拢，同时中段底色逐渐变浅"。
        // 飞行期（t ≤ 2.12s）与 p ≥ 1（终态）都不绘制：前者避免飞行期露白，
        // 后者避免终态双层重叠把边缘画厚（终态底色自身已是纯白）。
        if (tMs > WhitenStartMs && white < 1f)
        {
            DrawConvergingWhite(g, sym.Kind, ConvergeMaxRadius * (1f - white));
        }

        g.Restore(state);
    }

    /// <summary>
    /// 版本 C 的白色本体：以同一套几何画纯白，但只保留"距符号中心 r 之外"的部分。
    /// 用 <see cref="ConvergeSteps"/> 级硬裁剪（矩形 + 同心圆洞，Alternate 填充）按 alpha 递进叠加，
    /// 在圆边界叠出约 <see cref="ConvergeEdge"/> px 的柔和过渡（避免硬边）。
    /// </summary>
    private static void DrawConvergingWhite(Graphics g, SymbolKind kind, float r)
    {
        float prev = 0f;
        for (int i = 1; i <= ConvergeSteps; i++)
        {
            // 目标累计不透明度按 i/Steps 线性递进；反解本次该叠加的 alpha（source-over 累积）
            float target = i / (float)ConvergeSteps;
            int alpha = (int)Math.Round(255 * ((target - prev) / (1f - prev)));
            prev = target;
            if (alpha <= 0)
            {
                continue;
            }

            float radius = r + (((i - 1) / (float)(ConvergeSteps - 1)) - 0.5f) * ConvergeEdge;
            using var path = new GraphicsPath(FillMode.Alternate);
            path.AddRectangle(new RectangleF(-700f, -700f, 1400f, 1400f));
            if (radius > 0.05f)
            {
                path.AddEllipse(-radius, -radius, radius * 2f, radius * 2f);
            }

            GraphicsState st = g.Save();
            g.SetClip(path, CombineMode.Replace);
            using var whitePen = new Pen(Color.FromArgb(alpha, 255, 255, 255), Stroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            DrawSymbolShape(g, kind, whitePen);
            g.Restore(st);
        }
    }

    /// <summary>按符号形态画出圆头线段（描边与本体共用同一套几何）。</summary>
    private static void DrawSymbolShape(Graphics g, SymbolKind kind, Pen pen)
    {
        switch (kind)
        {
            case SymbolKind.Plus:
                g.DrawLine(pen, -AxialHalf, 0f, AxialHalf, 0f);
                g.DrawLine(pen, 0f, -AxialHalf, 0f, AxialHalf);
                break;

            case SymbolKind.Minus:
                g.DrawLine(pen, -AxialHalf, 0f, AxialHalf, 0f);
                break;

            case SymbolKind.Times:
                DrawRotated(g, pen, 45f);
                DrawRotated(g, pen, -45f);
                break;

            case SymbolKind.Equals:
                g.DrawLine(pen, -AxialHalf, -EqualsHalf, AxialHalf, -EqualsHalf);
                g.DrawLine(pen, -AxialHalf, EqualsHalf, AxialHalf, EqualsHalf);
                break;
        }
    }

    /// <summary>
    /// 白色描边的不透明度：0→1（2.12→2.24s）、保持 1（2.24→2.52s）、1→0（2.52→2.72s）；
    /// 2.72s 起恒为 0（最终态与无描边时逐像素一致）。
    /// </summary>
    private static float OutlineAlpha(long tMs)
    {
        if (tMs <= OutlineInStartMs || tMs >= OutlineOutEndMs)
        {
            return 0f;
        }

        if (tMs < OutlineInEndMs)
        {
            return (tMs - OutlineInStartMs) / (float)(OutlineInEndMs - OutlineInStartMs);
        }

        if (tMs <= OutlineHoldEndMs)
        {
            return 1f;
        }

        return 1f - (tMs - OutlineHoldEndMs) / (float)(OutlineOutEndMs - OutlineHoldEndMs);
    }

    private static void DrawRotated(Graphics g, Pen pen, float degrees)
    {
        GraphicsState s = g.Save();
        g.RotateTransform(degrees);
        g.DrawLine(pen, -CrossHalf, 0f, CrossHalf, 0f);
        g.Restore(s);
    }

    /// <summary>
    /// 入场位置：沿"起点→落位点"直线连跳 4 次。
    /// 位置 = base(s) + H·4q(1−q)·perp；base(s)=起点+s·(落位−起点)；s 在段内随 t 线性；
    /// perp 取 (dx,dy) 两个垂直单位向量中 y 为负的那个（四条弧线统一朝屏幕上方拱起）。
    /// </summary>
    private static (float X, float Y) BouncePosition(SymbolDef sym, float tNorm)
    {
        int seg = SegH.Length - 1;
        for (int i = 0; i < SegH.Length; i++)
        {
            if (tNorm < SegT[i + 1])
            {
                seg = i;
                break;
            }
        }

        float u = Math.Clamp((tNorm - SegT[seg]) / (SegT[seg + 1] - SegT[seg]), 0f, 1f);
        float s = SegS[seg] + (SegS[seg + 1] - SegS[seg]) * u;
        float q = u;

        float dx = sym.EndX - sym.StartX;
        float dy = sym.EndY - sym.StartY;
        float bx = sym.StartX + dx * s;
        float by = sym.StartY + dy * s;

        // 垂直单位向量：(-dy,dx) 与 (dy,-dx) 中 y 分量为负的那个
        float nx = -dy;
        float ny = dx;
        if (ny > 0f)
        {
            nx = dy;
            ny = -dx;
        }

        float len = MathF.Sqrt(nx * nx + ny * ny);
        if (len > 0.0001f)
        {
            nx /= len;
            ny /= len;
        }

        float h = SegH[seg] * 4f * q * (1f - q);
        return (bx + h * nx, by + h * ny);
    }

    /// <summary>摆动角度（度）：2.92s→3.54s，左列用 WobA 表（关键帧间线性插值），其它时间 0。</summary>
    private static float WobbleAngle(long tMs)
    {
        if (tMs <= WobbleStartMs || tMs >= TotalMs)
        {
            return 0f;
        }

        float u = (tMs - WobbleStartMs) / (float)(TotalMs - WobbleStartMs);
        for (int i = 0; i < WobT.Length - 1; i++)
        {
            if (u <= WobT[i + 1])
            {
                float f = (u - WobT[i]) / (WobT[i + 1] - WobT[i]);
                return WobA[i] + (WobA[i + 1] - WobA[i]) * f;
            }
        }

        return WobA[^1];
    }

    // ======================================================================
    // 绘制助手
    // ======================================================================

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2f;
        if (d <= 0.5f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.A + (b.A - a.A) * t),
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    // ======================================================================
    // Win32
    // ======================================================================

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
}