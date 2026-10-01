using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CalcPaper;

internal static class Program
{
    /// <summary>
    /// 启动计时：从 Main 进入开始累计，用于分解「程序启动 / 环境创建 / 初始化完成 / 页面 ready」
    /// 各阶段的毫秒差（见日志 `[启动计时]` 行）。只读使用，不影响任何逻辑。
    /// </summary>
    internal static readonly Stopwatch StartupWatch = Stopwatch.StartNew();

    [STAThread]
    private static void Main(string[] args)
    {
        StartupWatch.Restart();
        ApplicationConfiguration.Initialize();

        // 全局异常兜底：任何未处理异常都要落盘 + 弹窗，不能让程序静默崩溃
        Application.ThreadException += (_, e) => ReportCrash("UI 线程未处理异常", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportCrash("AppDomain 未处理异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ReportCrash("未观察的任务异常", e.Exception);
            e.SetObserved();
        };

        bool exitRequested = args.Any(a =>
            string.Equals(a, ShellIntegration.ExitArgument, StringComparison.OrdinalIgnoreCase));

        // 开机自启动（--autostart）：进入后台静默模式（不显示界面），待快捷键/托盘唤醒
        bool autoStartRequested = args.Any(a =>
            string.Equals(a, ShellIntegration.AutoStartArgument, StringComparison.OrdinalIgnoreCase));

        // 单实例：非首实例只把命令广播给已有实例，随后自身退出
        if (!ShellIntegration.TryAcquireSingleInstance())
        {
            // 开机自启动时若已有实例在运行，静默退出即可，不得唤醒已有界面
            if (!autoStartRequested)
            {
                ShellIntegration.SignalExistingInstance(
                    exitRequested ? ShellIntegration.CommandExit : ShellIntegration.CommandActivate);
            }

            return;
        }

        // 无已有实例且带 --exit：直接退出，不创建窗口
        if (exitRequested)
        {
            return;
        }

        // 数据目录可用性检查（必须在创建 WebView2 之前）；不可写则明确提示并退出
        if (!AppPaths.TryPrepareDataDir(out string? dataError))
        {
            if (autoStartRequested)
            {
                // 静默启动：不弹窗打扰，仅记录日志后退出
                Logger.Error("开机静默启动失败（数据目录不可用）: " + dataError);
                return;
            }

            MessageBox.Show(dataError, "计算稿纸", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Logger.Info($"程序启动（t+{StartupWatch.ElapsedMilliseconds}ms）");
        Logger.Info($"数据目录: {AppPaths.DataDir}");

        // 一次性旧数据迁移（旧 %APPDATA%\CalculationPad 保留不删）
        AppPaths.MigrateLegacyDataIfNeeded();

        // 任务栏跳转列表任务项（右键「完全退出」--exit）：推迟到主窗口显示后再注册
        // （见 MainForm.CompleteStartup），避免启动动画期间出现半成品状态。

        try
        {
            Application.Run(new MainForm(autoStartRequested));
        }
        catch (Exception ex)
        {
            ReportCrash("主循环异常", ex);
        }
        finally
        {
            Logger.Info("程序退出");
        }
    }

    private static void ReportCrash(string title, Exception? ex)
    {
        string detail = ex?.ToString() ?? "未知异常";
        Logger.Error($"{title}: {detail}");
        try
        {
            MessageBox.Show(detail, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // 弹窗失败（例如已在退出流程中）时忽略，日志已记录
        }
    }
}

/// <summary>轻量日志工具：单行追加，格式 `yyyy-MM-dd HH:mm:ss  [LEVEL] message`。</summary>
internal static class Logger
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{level}] {message}";
            lock (Gate)
            {
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写入失败不应影响程序运行
        }
    }
}

/// <summary>
/// 无边框主窗口：承载 WebView2（Dock=Fill），负责
/// · 窗口外观：本机为 Windows 10（build 19042）→ 「按可见盒偏移补偿的圆角 region r=8」
///   （当前实现下**无**系统阴影，用户已确认保持现状、不要阴影）；
/// · 由消息桥触发的窗口控制（拖动、最小化、关闭=隐藏到托盘、边缘缩放）；
/// · 托盘图标、全局快捷键（组合键或双击键）、Alt+F4 兜底；
/// · 关闭语义：点 × / 系统菜单关闭（CloseReason.UserClosing）/ Alt+F4 = 隐藏到托盘（Hide，任务栏不留图标，程序继续运行）；
///   完全退出走「先落盘再退出」流程（托盘「完全退出」/ 跳转列表 --exit / 跨实例退出）。
/// </summary>
internal sealed class MainForm : Form, IMessageFilter
{
    // ---- 窗口风格 ----
    // 只补 WS_THICKFRAME（不加 WS_CAPTION，因此不会出现系统标题栏）。
    // 实测（Win10 19042）：当前带圆角 region 的实现下**没有**系统阴影；该风格保留
    // 以免改动已验收的窗口外观与边缘行为（详见 CreateParams 注释）。
    private const int WS_THICKFRAME = 0x00040000;

    /// <summary>Windows 10 圆角半径（像素，物理）。Windows 11 默认圆角亦为 8px。</summary>
    private const int WindowCornerRadius = 8;

    // WM_NCCALCSIZE 用；左/右厚框保留。
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_ERASEBKGND = 0x0014;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    /// <summary>Windows 11（build ≥ 22000）走 DWM 原生圆角，不做区域裁剪。</summary>
    private static readonly bool IsWin11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    // DWM 属性（Win11 圆角）
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_HOTKEY = 0x0312;
    private const int HTCAPTION = 2;

    /// <summary>鼠标左键虚拟键码（边缘缩放阈值判定用）。</summary>
    private const int VK_LBUTTON = 0x01;

    /// <summary>进入缩放前，指针实际移动的阈值（像素）。</summary>
    private const int ResizeMoveThreshold = 3;

    // 非客户区命中码：只允许「下边线 / 右边线 / 右下角」缩放。
    // 这三种命中码在缩放过程中不会移动窗口原点，可避免「拖上/左边线时原点随帧变化 →
    // Chromium 合成器手里的旧帧整体平移」造成的剧烈抖动（该行为由 Chromium 有意保留、
    // 宿主侧无法消除，故直接从宿主侧禁掉会移动原点的那些方向）。
    private const int HTRIGHT = 11;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMRIGHT = 17;

    /// <summary>Alt+F4 拦截用（WebView2 持有焦点时 Chromium 会消费该键，窗体收不到 FormClosing）。</summary>
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_F4 = 0x73;
    private const int VK_MENU = 0x12;
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;

    private const int SW_RESTORE = 9;

    // SetWindowPos 标志
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    // RedrawWindow 标志（同步重绘新暴露条带用）
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW = 0x0100;

    /// <summary>退出前等待页面落盘（flushed）的超时时间。</summary>
    private const int FlushTimeoutMs = 1500;

    private readonly WebView2 _webView = new();
    private readonly GlobalHotkey _hotkey = new();
    private readonly DoubleAltWatcher _doubleAlt;
    private readonly ManualResetEvent _flushed = new(initialState: false);

    /// <summary>
    /// 缩放门槛：收到 resize 后先不进入系统缩放循环，等指针实际移动超过阈值才进入，
    /// 从而保证「边缘单击（无移动）不改变窗口尺寸」。
    /// </summary>
    private readonly System.Windows.Forms.Timer _resizeGate = new() { Interval = 10 };

    private Bridge? _bridge;
    private TrayMenu? _tray;
    private SplashWindow? _splash;
    private bool _startupComplete;
    private bool _pageReadyReceived;

    // ---- 开机静默启动 ----
    // _startSilent：本次为开机自启动（--autostart）进入，界面默认不显示；
    // _allowVisible：一旦被唤醒（快捷键/托盘/跨实例）置 true，之后 SetVisibleCore 不再拦截。
    private readonly bool _startSilent;
    private bool _allowVisible;

    private bool _flushInProgress;
    private bool _reallyExit;
    private bool _exitInProgress;

    // ---- 边缘缩放门槛状态 ----
    private Point _resizeStartCursor;
    private int _resizeHit;          // 待进入系统缩放循环的命中码（HTRIGHT/HTBOTTOM/HTBOTTOMRIGHT）；0 = 无
    private bool _resizing;          // 系统缩放循环进行中（用于抑制逐帧外观日志）

    // ---- 圆角/重绘状态 ----
    private Size _lastClientSize;
    private readonly SolidBrush _eraseBrush = new(Color.White);

    // 可见盒四边内缩量（窗口矩形 − 客户区矩形）的缓存；-1 表示尚未实测
    private int _rgnLeft = -1;
    private int _rgnTop;
    private int _rgnRight;
    private int _rgnBottom;

    /// <summary>最近一次全局快捷键的错误信息（供设置面板回显）。</summary>
    public string? HotkeyError { get; private set; }

    /// <summary>当前窗口是否置顶（启动为未置顶；不做持久化）。</summary>
    public bool IsPinned { get; private set; }

    /// <summary>
    /// 主窗口。startSilent = true 表示本次由开机自启动（--autostart）进入：
    /// 界面完全不显示（不显示窗口、不进任务栏、不播启动动画），仅创建托盘与全局快捷键待唤醒；
    /// 手动双击 exe（无该参数）时 startSilent = false，正常显示 GUI。
    /// </summary>
    public MainForm(bool startSilent = false)
    {
        _startSilent = startSilent;
        _allowVisible = !startSilent;

        Text = "计算稿纸";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;

        // 目标：实际客户端区域 = 1000×700。
        //
        // ⚠ 关键换算（实测复核，2026-09，2560×1440 @100%，Win10 build 19042）：本窗口是
        // FormBorderStyle.None + CreateParams 补 WS_THICKFRAME，实测
        // · 设置 ClientSize = 1040×880 时 → GetWindowRect = (760,280,1040×880)、左右内缩各 7px、
        //   实际客户端 = 1026×880（即 WinForms 把 ClientSize 直接当作**窗口矩形**尺寸设置，
        //   随后 WndProc 的 WM_NCCALCSIZE 只扣左/右各 7px 厚框、顶/底归零）；
        // · 于是：实际客户端宽 = 窗口矩形宽 − 14，实际客户端高 = 窗口矩形高。
        // 反推目标「实际客户端 = 1000×700」：窗口矩形宽 = 1000 + 14 = 1014 → 设置 ClientSize = 1014×700
        // （实测客户端 1000×700、窗口矩形 1014×700；详见报告「窗口尺寸推导与实测」）。
        ClientSize = new Size(1014, 700);
        MinimumSize = new Size(720, 560);
        BackColor = Color.White;
        Icon = LoadAppIcon();   // 必须显式设置，否则 WinForms 使用默认图标

        _doubleAlt = new DoubleAltWatcher(OnDoubleKeyTriggered, OnAltF4Triggered);
        _resizeGate.Tick += OnResizeGateTick;

        // 用户数据目录必须在 CoreWebView2 初始化之前指定
        _webView.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = AppPaths.WebView2DataDir
        };
        _webView.Dock = DockStyle.Fill;

        // 页面主底色：app-mode 下 .window / body 的背景色 = --window-bg = #ffffff（见 wwwroot/style.css:8,96）。
        // 设为同一色值后，「未渲染 → 渲染完成」不再出现白屏之外的色差跳变。
        _webView.DefaultBackgroundColor = Color.FromArgb(0xFF, 0xFF, 0xFF);
        Controls.Add(_webView);

        // Alt+F4 兜底：WebView2 持有键盘焦点时 Chromium 会消费该键，窗体收不到 FormClosing，
        // 这里在消息派发前拦截（退出时在 Dispose 中移除）。
        Application.AddMessageFilter(this);

        // 启动海报（需求 6）：主窗口**立即显示**（与更早行为一致），海报是一个与主窗口
        // 同尺寸同位置、作为主窗口 owned window 的窗口，铺满整个窗口（含标题栏与底部按钮栏），
        // 在其上播放「四则符号弹跳/旋转/拼合」动画；页面就绪且动画播完后 200ms 淡出关闭。
        // 创建见 OnShown → StartPoster。

        // 强制创建主窗口句柄：OnHandleCreated 会执行外观 / 输入初始化，
        // 并由它把 WebView2 初始化排入消息队列。
        _ = Handle;
    }

    /// <summary>
    /// 主窗口首次显示时（<see cref="Application.Run(Form)"/> 已把主窗口立即显示出来）：
    /// 记录「主窗口已显示」计时 → 在其上创建启动海报 → 创建托盘图标 / 注册跳转列表。
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Logger.Info($"[启动计时] 主窗口已显示 t+{Program.StartupWatch.ElapsedMilliseconds}ms");

        // 静默启动（--autostart）后首次被唤醒时不播启动动画：唤醒应当即时呈现界面；
        // 手动双击启动（_startSilent=false）仍照常播放。
        if (!_startSilent)
        {
            StartPoster();
        }

        CompleteStartup();
    }

    /// <summary>主窗口客户端区域在屏幕上的矩形（= 圆角 region 形成的可见盒；海报与它完全重合）。</summary>
    private Rectangle ClientBoundsOnScreen() => RectangleToScreen(ClientRectangle);

    /// <summary>
    /// 创建启动海报：与主窗口同尺寸同位置、作为主窗口的 owned window（始终盖在主窗口之上、
    /// 不进任务栏与 Alt+Tab）、不可交互；创建失败时静默回退（无海报，主窗口照常可用）。
    /// </summary>
    private void StartPoster()
    {
        if (_splash != null || IsDisposed)
        {
            return;
        }

        try
        {
            _splash = new SplashWindow(this, ClientBoundsOnScreen());
            _splash.Completed += OnPosterCompleted;

            // 页面可能已在海报创建前就绪（WebView2 初始化早于 OnShown 完成时）：补一次通知
            if (_pageReadyReceived)
            {
                _splash.NotifyPageReady();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("创建启动海报失败（改为不显示海报）: " + ex);
            _splash = null;
        }
    }

    /// <summary>海报播完（含淡出、已自行 Close）后：延后一帧释放（避免在动画计时器回调内 Dispose 自身）。</summary>
    private void OnPosterCompleted(object? sender, EventArgs e)
    {
        SplashWindow? splash = _splash;
        _splash = null;
        if (splash == null)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                try
                {
                    splash.Dispose();
                }
                catch
                {
                    // 释放失败忽略
                }
            }));
        }
        catch
        {
            // 句柄已销毁时忽略
        }
    }

    /// <summary>关闭并释放海报（隐藏到托盘 / 真正退出时调用）。</summary>
    private void AbortPoster()
    {
        SplashWindow? splash = _splash;
        _splash = null;
        if (splash == null)
        {
            return;
        }

        try
        {
            splash.Abort();
            splash.Dispose();
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>主窗口首次显示后创建托盘图标并注册跳转列表任务项（避免动画期间出现半成品状态）。</summary>
    private void CompleteStartup()
    {
        if (_startupComplete)
        {
            return;
        }

        _startupComplete = true;

        try
        {
            _tray = new TrayMenu(Icon, RestoreWindow, ExitApplication);
        }
        catch (Exception ex)
        {
            Logger.Error("创建托盘图标失败: " + ex.Message);
        }

        string? jumpListError = ShellIntegration.SetupJumpList();
        if (jumpListError != null)
        {
            Logger.Error("跳转列表: " + jumpListError);
        }
        else
        {
            Logger.Info("任务栏跳转列表任务已创建: 完全退出 (--exit)");
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;

            // WS_THICKFRAME 保留：历史上用于取回 DWM 系统阴影（纯 WS_POPUP + CS_DROPSHADOW
            // 不产生投影）。当前实现带圆角 region 时实测**无**系统阴影（跳过 region 时阴影立刻
            // 出现、加回则四面差分为 0），用户已确认保持现状、不要阴影；此风格保留以免改动
            // 已验收的窗口外观/边缘行为（Win11 分支走 DWM 原生圆角）。
            cp.Style |= WS_THICKFRAME;

            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Logger.Info($"[启动计时] 窗口句柄创建 t+{Program.StartupWatch.ElapsedMilliseconds}ms");
        ApplyWindowAppearance();
        ApplyInputSettings(PaperIo.LoadSettings(AppPaths.SettingsFile));

        // WebView2 初始化排入消息队列，等 Application.Run 的消息泵启动后立即执行
        // （不依赖 Load：即使窗口尚未完成首帧，也能尽早开始加载）。
        BeginInvoke(new Action(() => _ = InitializeWebViewAsync()));

        if (_startSilent)
        {
            // 开机静默启动：窗口全程不显示（SetVisibleCore 拦截）、不播启动动画，
            // 仅创建托盘图标 / 跳转列表等待唤醒；因窗口从未显示，OnShown 不会触发。
            BeginInvoke(new Action(() =>
            {
                Logger.Info($"[启动计时] 静默启动（不显示界面）t+{Program.StartupWatch.ElapsedMilliseconds}ms");
                CompleteStartup();
            }));
        }
    }

    /// <summary>
    /// 静默启动时强制不显示窗口（<see cref="Application.Run(Form)"/> 会把主窗口置为可见，
    /// 在此拦截）。仍确保句柄已创建，使托盘 / 全局快捷键 / 后台逻辑照常工作；
    /// 被唤醒（_allowVisible 置 true）后不再拦截。
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (_startSilent && !_allowVisible)
        {
            value = false;
            if (!IsHandleCreated)
            {
                _ = Handle;
            }
        }

        base.SetVisibleCore(value);
    }

    /// <summary>页面发送 <c>ready</c> 时调用：记录计时并通知启动海报（动画播完即可淡出）。</summary>
    public void OnPageReady()
    {
        _pageReadyReceived = true;
        Logger.Info($"[启动计时] 页面已就绪 t+{Program.StartupWatch.ElapsedMilliseconds}ms");
        _splash?.NotifyPageReady();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);

        // 海报与主窗口同尺寸同位置：主窗口移动（拖动标题栏）时同步跟随
        SyncPosterBounds();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // 尺寸变化时：先把「新暴露的条带」标为待擦除，再重新套用圆角 region。
        // 顺序很重要——先让宿主窗体自己把新条带擦成底色（见 WndProc 的 WM_ERASEBKGND），
        // 再套用 region，避免 Chromium 合成器在尺寸追上前的短暂空档里露出未绘制的黑底。
        InvalidateExposedStrip();

        // 窗口尺寸变化以及从最小化恢复时都要重新应用外观
        if (WindowState == FormWindowState.Normal)
        {
            ApplyWindowAppearance();
        }

        // 缩放循环中：强制同步重绘（含 WebView2 子窗口），消除「窗口已放大但新条带未绘制 →
        // 保留窗口后面旧内容（桌面/黑底）」的伪影。
        if (_resizing)
        {
            RepaintNow();
        }

        // 海报与主窗口同尺寸同位置：主窗口缩放时同步跟随
        SyncPosterBounds();
    }

    /// <summary>
    /// 只允许「下边线 / 右边线 / 右下角」缩放，所以新暴露的区域一定在客户区右侧和/或下侧。
    /// 这里把「旧尺寸 → 新尺寸」的差集条带标为待绘制（含擦除），让宿主窗体在 WM_PAINT /
    /// WM_ERASEBKGND 中以窗底白色填充，从而不出现未绘制的黑边。
    /// </summary>
    private void InvalidateExposedStrip()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        Size now = ClientSize;
        Size prev = _lastClientSize;
        _lastClientSize = now;

        if (prev.Width <= 0 || prev.Height <= 0)
        {
            return;   // 首次布局，无需增量重绘
        }

        try
        {
            if (now.Width > prev.Width)
            {
                Invalidate(new Rectangle(prev.Width, 0, now.Width - prev.Width, now.Height), true);
            }

            if (now.Height > prev.Height)
            {
                Invalidate(new Rectangle(0, prev.Height, now.Width, now.Height - prev.Height), true);
            }
        }
        catch
        {
            // 重绘请求失败不影响主流程
        }
    }

    /// <summary>把海报对齐到主窗口当前可见盒（客户端矩形）。</summary>
    private void SyncPosterBounds()
    {
        if (_splash == null || !IsHandleCreated)
        {
            return;
        }

        try
        {
            _splash.FollowOwnerBounds(ClientBoundsOnScreen());
        }
        catch
        {
            // 同步失败不影响主流程
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                // 点 × / 系统菜单关闭：改为隐藏到托盘，不退出（后台自动保存等继续运行）
                e.Cancel = true;
                Logger.Info("收到关闭请求（UserClosing），改为隐藏到托盘");
                HideWindow();
                return;
            }

            // 系统关机 / 任务管理器结束 / Application.Exit：允许真正退出，但退出前尽力落盘
            Logger.Info($"收到退出请求（{e.CloseReason}），开始落盘");
            RequestFlushAndWait(FlushTimeoutMs);
            _reallyExit = true;
        }

        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        // Windows 10：仅去掉「顶部 + 底部」非客户区，左/右厚框保留。
        // 实测（2026-09，2560x1440 @96DPI，build 19042）：
        // · 顶部约 7px 是 DWM 不透明顶框，会让顶部圆角残留方形凸角 → 归零（原有行为）；
        // · 底部同样有约 7px 的**透明**非客户区，客户区因此止于窗口矩形下方 7px 处，
        //   页面无法延伸到窗口下缘，导致「下边线」缺失（详见报告 B 节剖面）。
        //   把底部也归零后，客户区/圆角 region 都延伸到窗口底边，页面可自绘下缘 1px 描边。
        // 四边外的像素剖面在改动前后逐字节相同；隐藏/显示差分测得的系统阴影均为 0
        // （即当前带圆角 region 的实现下无系统阴影，用户已确认保持现状）。
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero && !IsWin11)
        {
            NCCALCSIZE_PARAMS p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            RECT win = p.rc0;

            // 左/右的厚框宽度用系统默认值（随 DPI 自适应），顶部与底部强制为 0
            RECT adjust = default;
            _ = AdjustWindowRectEx(
                ref adjust,
                GetWindowLong(Handle, GWL_STYLE),
                false,
                GetWindowLong(Handle, GWL_EXSTYLE));

            p.rc0.Left = win.Left - adjust.Left;
            p.rc0.Top = win.Top;
            p.rc0.Right = win.Right - adjust.Right;
            p.rc0.Bottom = win.Bottom;

            Marshal.StructureToPtr(p, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }

        // 擦背景：显式用窗底白色填充并返回「已处理」。
        // 目的：宿主窗体自身（WebView2 子窗口之外、以及子窗口尺寸尚未跟上的短暂空档）
        // 一旦有未绘制像素，必须被擦成白色而不是留黑。
        if (m.Msg == WM_ERASEBKGND && m.WParam != IntPtr.Zero)
        {
            using (Graphics g = Graphics.FromHdc(m.WParam))
            {
                g.FillRectangle(_eraseBrush, ClientRectangle);
            }

            m.Result = (IntPtr)1;
            return;
        }

        // 跨实例广播命令（第二次启动的程序发来的）
        if (m.Msg == ShellIntegration.CommandMessage)
        {
            int command = m.WParam.ToInt32();
            if (command == ShellIntegration.CommandExit)
            {
                ExitApplication();
            }
            else if (command == ShellIntegration.CommandActivate)
            {
                Logger.Info("收到跨实例唤醒命令");
                RestoreWindow();
            }

            return;
        }

        // 全局快捷键
        if (m.Msg == WM_HOTKEY)
        {
            Logger.Info("全局快捷键触发，唤醒窗口");
            RestoreWindow();
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            DisposeTrayAndInput();
            AbortPoster();
            _resizeGate.Dispose();
            _eraseBrush.Dispose();
            _flushed.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 消息过滤器（本轮要求实现）：只拦 Alt+F4，命中就隐藏到托盘并吞掉消息；
    /// 其它消息一律返回 false 放行。
    ///
    /// ⚠ 实测说明：本程序中它收不到 Alt+F4 —— WebView2 内容的键盘输入窗口
    /// （Chrome_WidgetWin_1）属于 WebView2 浏览器进程，按键消息不进入本线程的消息队列
    /// （已用全局热键的 WM_HOTKEY 作对照验证：过滤器能看到 WM_HOTKEY，却看不到任何
    /// WM_KEYDOWN/WM_SYSKEYDOWN）。因此 Alt+F4 的实际兜底由 <see cref="DoubleAltWatcher"/>
    /// 的低级键盘钩子完成；此过滤器保留，以便焦点落在本进程窗口上时仍能生效。
    /// </summary>
    bool IMessageFilter.PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_SYSKEYDOWN || m.WParam.ToInt32() != VK_F4)
        {
            return false;
        }

        // 必须是「仅 Alt+F4」（排除 Ctrl/Shift 组合）
        if ((GetKeyState(VK_MENU) & 0x8000) == 0 ||
            (GetKeyState(VK_CONTROL) & 0x8000) != 0 ||
            (GetKeyState(VK_SHIFT) & 0x8000) != 0)
        {
            return false;
        }

        // 已在真正退出流程中时不拦截，交给系统/退出逻辑
        if (_reallyExit)
        {
            return false;
        }

        Logger.Info("Alt+F4 被拦截，改为隐藏到托盘");
        HideWindow();
        return true;   // 吞掉消息，避免系统再走关闭流程
    }

    // ======================================================================
    // 窗口控制
    // ======================================================================

    /// <summary>向页面推送消息（JSON 字符串）。</summary>
    public void PostToJs(string json)
    {
        try
        {
            _webView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Logger.Error("向页面发送消息失败: " + ex.Message);
        }
    }

    /// <summary>标题栏拖动：把光标区域临时当作标题栏，交给系统移动窗口。</summary>
    public void BeginDrag()
    {
        ReleaseCapture();
        _ = SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    /// <summary>
    /// 设置窗口置顶（点标题栏「置顶」按钮走这里，前端走既有 window: 命令协议）。
    /// 要点：
    /// · 同步调整仍存活的启动海报窗口 —— 海报是独立的分层窗口（owned window），
    ///   若只把主窗口置顶而海报未置顶，海报会被压在主窗口之后（启动后约 2.2–3.9s
    ///   用户可能点到置顶按钮），故两者同置顶；
    /// · 状态变化后回发页面（按钮视觉切换/复位由页面据此更新）。
    /// </summary>
    public void SetWindowPinned(bool pinned)
    {
        if (IsPinned == pinned)
        {
            SendPinnedState();
            return;
        }

        IsPinned = pinned;
        TopMost = pinned;

        try
        {
            if (_splash != null && !_splash.IsDisposed)
            {
                _splash.TopMost = pinned;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("同步启动海报置顶状态失败: " + ex.Message);
        }

        Logger.Info(pinned
            ? "窗口置顶已开启（TopMost=true）"
            : "窗口置顶已取消（TopMost=false）");
        SendPinnedState();
    }

    /// <summary>取消置顶（最小化 / 隐藏时自动调用）：仅当前置顶时改动并回发状态。</summary>
    private void ClearPinned()
    {
        if (IsPinned)
        {
            SetWindowPinned(false);
        }
    }

    /// <summary>向页面回发置顶状态（标题栏按钮视觉据此更新）。</summary>
    private void SendPinnedState()
    {
        PostToJs("{\"type\":\"pinned\",\"value\":" + (IsPinned ? "true" : "false") + "}");
    }

    /// <summary>最小化到任务栏（点最小化按钮走这里；任务栏保留图标）。</summary>
    public void MinimizeWindow()
    {
        // 置顶态在最小化时自动取消（并回发状态，按钮视觉同步复位）
        ClearPinned();
        WindowState = FormWindowState.Minimized;
        NotifyWindowState("minimized");
    }

    /// <summary>
    /// 隐藏到托盘（点 × / 系统关闭 / Alt+F4 走这里）：任务栏不再保留图标，程序继续运行。
    /// 与最小化的区别：Hide() 会从任务栏与 Alt+Tab 列表移除本窗口。
    /// </summary>
    public void HideWindow()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            // 置顶态在隐藏（关闭）时自动取消（并回发状态，按钮视觉同步复位）
            ClearPinned();
            // 先通知页面清理残留提示气泡，再隐藏
            NotifyWindowState("hidden");
            Hide();
            // 启动海报尚未结束时一并关闭，避免它留在屏幕上
            AbortPoster();
            Logger.Info("窗口已隐藏到托盘");
        }
        catch (Exception ex)
        {
            Logger.Error("隐藏窗口失败: " + ex.Message);
        }
    }

    /// <summary>唤醒窗口：显示 → 恢复正常状态 → 置前（隐藏态与最小化态都能恢复）。</summary>
    public void RestoreWindow()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            // 静默启动后首次唤醒：解除 SetVisibleCore 的拦截，允许窗口显示
            _allowVisible = true;

            // 系统记录的正常态矩形：用于修正最小化→恢复时的尺寸漂移
            WINDOWPLACEMENT placement = new() { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            bool hasPlacement = GetWindowPlacement(Handle, ref placement);

            Show();
            _ = ShowWindow(Handle, SW_RESTORE);
            _ = SetForegroundWindow(Handle);
            Activate();
            CorrectRestoredSize(hasPlacement, placement);

            // 从隐藏/最小化恢复后重新应用圆角（DWM 可能已重置窗口区域）
            ApplyWindowAppearance();
            NotifyWindowState("restored");
        }
        catch (Exception ex)
        {
            Logger.Error("唤醒窗口失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 修正从最小化恢复时的尺寸漂移。
    ///
    /// 原因（实测）：Windows 10 分支把窗口顶部非客户区归零（见 <see cref="WndProc"/>），
    /// 而 WinForms 在最小化→恢复时会按 SystemInformation 的边框度量（顶部 7px）重算窗口尺寸，
    /// 两者相差一个顶边框高度，导致每次「最小化→恢复」窗口高度累积 +7px。
    /// 这里用系统记录的「正常态矩形」把尺寸拉回（只改尺寸、不动位置）。
    /// </summary>
    private void CorrectRestoredSize(bool hasPlacement, WINDOWPLACEMENT placement)
    {
        if (!hasPlacement || WindowState != FormWindowState.Normal)
        {
            return;
        }

        RECT want = placement.rcNormalPosition;
        GetWindowRect(Handle, out RECT now);
        int wantW = want.Right - want.Left;
        int wantH = want.Bottom - want.Top;
        if (now.Right - now.Left == wantW && now.Bottom - now.Top == wantH)
        {
            return;
        }

        _ = SetWindowPos(
            Handle, IntPtr.Zero, 0, 0, wantW, wantH,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>向页面推送窗口状态（用于清理残留提示气泡）：hidden / minimized / restored。</summary>
    private void NotifyWindowState(string state)
    {
        PostToJs("{\"type\":\"windowState\",\"state\":\"" + state + "\"}");
    }

    /// <summary>
    /// 边缘缩放：只有「下边线 b / 右边线 r / 右下角 br」三种被允许；其余方向（含四角与
    /// 上/左边线）一律忽略并记日志。原因：拖上/左边线会移动窗口原点，Chromium 合成器
    /// 手中的旧帧会随之整体平移，产生剧烈抖动（Chromium 有意保留的行为，宿主侧无法消除）。
    ///
    /// 关键：不在按下瞬间直接进入系统缩放循环（否则边缘「单击」也会吸附尺寸），
    /// 而是先记录指针起点，等实际移动超过阈值才进入（见 <see cref="OnResizeGateTick"/>）。
    /// </summary>
    public void BeginResize(string edge)
    {
        int hit = edge switch
        {
            "b" => HTBOTTOM,
            "r" => HTRIGHT,
            "br" => HTBOTTOMRIGHT,
            _ => 0
        };

        if (hit == 0)
        {
            // 防御性限制：前端热区可能仍请求其它方向，这里明确忽略并留痕，便于核验
            Logger.Info($"[缩放] 忽略不支持的缩放方向: \"{edge}\"（仅允许 b/r/br）");
            return;
        }

        if (!GetCursorPos(out Point cursor))
        {
            Logger.Error("缩放：无法获取光标位置，忽略本次缩放请求");
            return;
        }

        _resizeHit = hit;
        _resizeStartCursor = cursor;
        _resizeGate.Start();
    }

    /// <summary>
    /// 缩放门槛计时器：等待指针移动超过阈值（或左键提前抬起 → 视为单击，什么都不做）。
    /// 越过阈值后进入**系统原生缩放循环**（ReleaseCapture + WM_NCLBUTTONDOWN），
    /// 由系统接管鼠标到窗口的 1:1 跟手缩放，不再有任何附加延迟。
    /// </summary>
    private void OnResizeGateTick(object? sender, EventArgs e)
    {
        // 左键已抬起：单击结束，不进入缩放
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
        {
            _resizeGate.Stop();
            _resizeHit = 0;
            return;
        }

        if (!GetCursorPos(out Point cursor))
        {
            _resizeGate.Stop();
            _resizeHit = 0;
            return;
        }

        int dx = cursor.X - _resizeStartCursor.X;
        int dy = cursor.Y - _resizeStartCursor.Y;
        if (Math.Abs(dx) < ResizeMoveThreshold && Math.Abs(dy) < ResizeMoveThreshold)
        {
            return;   // 尚未形成有效拖动
        }

        _resizeGate.Stop();
        int hit = _resizeHit;
        _resizeHit = 0;
        if (hit == 0)
        {
            return;
        }

        // 系统缩放循环：本调用会一直阻塞到用户松开左键（循环内部由系统自行泵消息）。
        Logger.Info(
            $"[缩放] 越过 {ResizeMoveThreshold}px 阈值，进入系统缩放循环 ht={hit}"
            + $"（dx={dx}, dy={dy}）");

        _resizing = true;
        try
        {
            ReleaseCapture();
            _ = SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)hit, IntPtr.Zero);
        }
        finally
        {
            _resizing = false;
        }

        // 缩放循环期间跳过了外观日志；结束后复核一次（圆角 + region 复核用）
        ApplyWindowAppearance();
        LogAppearance($"Win10: 缩放结束 圆角 region r={WindowCornerRadius}（当前实现下无系统阴影，保持现状）");
    }

    // ======================================================================
    // 输入（全局快捷键：组合键 或 双击键；Alt+F4 兜底）
    // ======================================================================

    /// <summary>
    /// 按设置应用全局快捷键（幂等，设置变化时调用）。globalHotkey 为唯一取值：
    /// · 空串 → 不注册热键、不启用双击键（Alt+F4 兜底仍保留）；
    /// · 以「双击 」开头 → 启用键盘钩子的双击键检测，目标键取后缀；
    /// · 其它 → 走 RegisterHotKey 注册组合键。
    /// 解析 / 注册失败时把中文错误写入 <see cref="HotkeyError"/> 供设置面板回显。
    /// </summary>
    public void ApplyInputSettings(AppSettings settings)
    {
        string spec = (settings.GlobalHotkey ?? string.Empty).Trim();

        // 空串 = 未设置：注销热键、停用双击键
        if (spec.Length == 0)
        {
            _hotkey.Apply(Handle, null, enabled: false);
            _doubleAlt.SetDoubleKey(false, null);
            HotkeyError = null;
            Logger.Info("全局快捷键未设置（已禁用）");
            return;
        }

        // 双击键：「双击 XXX」
        if (spec.StartsWith(AppSettings.DoubleKeyPrefix, StringComparison.Ordinal))
        {
            string suffix = spec[AppSettings.DoubleKeyPrefix.Length..].Trim();
            _hotkey.Apply(Handle, null, enabled: false);   // 双击键不注册热键

            if (DoubleAltWatcher.TryParseTarget(suffix, out uint[] targetKeys))
            {
                _doubleAlt.SetDoubleKey(true, targetKeys);
                HotkeyError = null;
                Logger.Info($"双击键唤醒已启用: {spec}");
            }
            else
            {
                _doubleAlt.SetDoubleKey(false, null);
                HotkeyError = $"无法识别的双击键：{spec}";
                Logger.Error("全局快捷键: " + HotkeyError);
            }

            return;
        }

        // 组合键：走 RegisterHotKey
        _doubleAlt.SetDoubleKey(false, null);
        HotkeyError = _hotkey.Apply(Handle, spec, enabled: true);
        if (HotkeyError != null)
        {
            Logger.Error("全局快捷键: " + HotkeyError);
        }
        else
        {
            Logger.Info($"全局快捷键已注册: {spec}");
        }
    }

    /// <summary>键盘钩子命中双击键：把唤醒动作转投到 UI 消息队列，保持钩子回调极简。</summary>
    private void OnDoubleKeyTriggered()
    {
        try
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(() =>
                {
                    Logger.Info("双击键触发，唤醒窗口");
                    RestoreWindow();
                }));
            }
        }
        catch
        {
            // 窗口正在销毁时忽略
        }
    }

    /// <summary>
    /// 键盘钩子命中 Alt+F4：转投 UI 消息队列后按「关闭语义」隐藏到托盘。
    /// 只处理落在本窗口上的 Alt+F4（钩子是全局的，避免影响其它程序）。
    /// </summary>
    private void OnAltF4Triggered()
    {
        try
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(() =>
                {
                    // 已在真正退出流程中时交给退出逻辑处理
                    if (_reallyExit || IsDisposed)
                    {
                        return;
                    }

                    // 全局钩子：仅当本窗口是前台窗口时才响应
                    if (GetForegroundWindow() != Handle)
                    {
                        return;
                    }

                    Logger.Info("Alt+F4 被拦截，改为隐藏到托盘");
                    HideWindow();
                }));
            }
        }
        catch
        {
            // 窗口正在销毁时忽略
        }
    }

    // ======================================================================
    // 退出（先落盘，再退出）
    // ======================================================================

    /// <summary>页面完成落盘后回包 {cmd:'flushed'} 时调用。</summary>
    public void OnFlushed() => _flushed.Set();

    /// <summary>
    /// 完全退出统一入口（托盘「完全退出」/ 页面 exitApp / 跨实例退出命令 / 跳转列表 --exit）。
    /// 只负责"排队"：真正的退出流程经 BeginInvoke 脱离当前同步栈后执行（见 <see cref="ExitApplicationCore"/>）。
    /// 重复触发由 <see cref="_exitInProgress"/> 忽略。
    /// </summary>
    public void ExitApplication()
    {
        if (_reallyExit || _exitInProgress)
        {
            return;
        }

        _exitInProgress = true;

        try
        {
            // 关键：不能在这里同步执行退出流程。页面 exitApp 是从 CoreWebView2.WebMessageReceived
            // 回调内调进来的，此时同步 PostWebMessageAsJson("flush") 要等回调返回后才投递给页面，
            // 会导致等待 flushed 必然超时。BeginInvoke 到 UI 消息队列后，消息泵才有机会投递 flush
            // 并接收页面的 flushed 回包（三个入口行为因此完全一致）。
            BeginInvoke(new Action(ExitApplicationCore));
        }
        catch
        {
            // 句柄尚未创建 / 已销毁：退回同步执行
            ExitApplicationCore();
        }
    }

    /// <summary>
    /// 退出流程本体：向页面发 {"type":"flush"} → 等待 flushed 或超时 1.5s →
    /// 置 _reallyExit、Dispose 托盘与输入组件、关闭窗口。
    /// 必须在 UI 消息泵可自由运转的上下文（而非 WebView2 事件回调栈内）执行。
    /// </summary>
    private void ExitApplicationCore()
    {
        if (_reallyExit)
        {
            return;
        }

        Logger.Info("开始完全退出流程");

        RequestFlushAndWait(FlushTimeoutMs);

        _reallyExit = true;
        DisposeTrayAndInput();
        Close();
    }

    /// <summary>
    /// 请求页面立即落盘并等待其回包；超时或页面未就绪时直接返回。
    /// 等待期间用 Application.DoEvents 保持消息泵运转，WebView2 的 WebMessageReceived 才能被处理。
    /// </summary>
    private void RequestFlushAndWait(int timeoutMs)
    {
        if (_flushInProgress)
        {
            return;
        }

        _flushInProgress = true;
        try
        {
            if (_webView.CoreWebView2 == null)
            {
                Logger.Info("页面尚未就绪，退出前无需落盘");
                return;
            }

            _flushed.Reset();
            PostToJs("{\"type\":\"flush\"}");

            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (_flushed.WaitOne(50))
                {
                    Logger.Info("退出前落盘完成（收到页面 flushed）");
                    return;
                }

                Application.DoEvents();
            }

            Logger.Info($"退出前落盘等待超时（{timeoutMs}ms），继续退出");
        }
        catch (Exception ex)
        {
            Logger.Error("退出前落盘失败: " + ex.Message);
        }
        finally
        {
            _flushInProgress = false;
        }
    }

    private void DisposeTrayAndInput()
    {
        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _tray = null;

        try
        {
            _hotkey.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _doubleAlt.Dispose();
        }
        catch
        {
            // 忽略
        }
    }

    // ======================================================================
    // WebView2
    // ======================================================================

    private async Task InitializeWebViewAsync()
    {
        try
        {
            Logger.Info($"[启动计时] 开始创建 WebView2 环境 t+{Program.StartupWatch.ElapsedMilliseconds}ms");
            await _webView.EnsureCoreWebView2Async();
            Logger.Info($"[启动计时] WebView2 环境创建完成 t+{Program.StartupWatch.ElapsedMilliseconds}ms");
            CoreWebView2 core = _webView.CoreWebView2;

            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;   // 保留复制/粘贴

            // 用虚拟主机映射加载本地页面，避免 file:// 带来的安全限制
            string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping(
                "calcpaper.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

            _bridge = new Bridge(this);
            core.WebMessageReceived += _bridge.OnWebMessageReceived;

            core.Navigate("https://calcpaper.local/index.html");
            Logger.Info($"[启动计时] WebView2 初始化完成（已发起导航）t+{Program.StartupWatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Logger.Error("WebView2 初始化失败: " + ex);

            // 关掉启动海报（主窗口本身已经在显示），回到原有的错误弹窗流程
            AbortPoster();

            MessageBox.Show(
                "WebView2 初始化失败：\n" + ex.Message + "\n\n请确认已安装 Microsoft Edge WebView2 运行时。",
                "计算稿纸", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ======================================================================
    // 图标与圆角
    // ======================================================================

    /// <summary>
    /// 应用图标：优先从 exe 内嵌图标提取（零文件依赖），失败再回退到
    /// &lt;程序目录&gt;\assets\app.ico。
    /// </summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Icon? embedded = Icon.ExtractAssociatedIcon(exe);
                if (embedded != null)
                {
                    return embedded;
                }
            }
        }
        catch
        {
            // 继续尝试回退路径
        }

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "assets", "app.ico");
            if (File.Exists(path))
            {
                return new Icon(path);
            }
        }
        catch
        {
            // 无可用图标时交给 WinForms 默认处理
        }

        return null;
    }

    /// <summary>
    /// 应用窗口外观（在创建句柄 / 尺寸变化 / 从隐藏或最小化恢复时调用）：
    /// · Windows 11（build ≥ 22000）→ 交给 DWM 原生圆角（本机为 Win10，该分支未实测）；
    /// · Windows 10 → 按「可见盒」偏移补偿的圆角 region r=8（见 <see cref="ApplyRoundedRegion"/>）。
    ///   实测：当前带圆角 region 的实现下**没有** DWM 系统阴影（跳过 region 时阴影立刻出现、
    ///   加回则四面差分为 0），用户已确认保持现状、不要阴影。
    /// 每次应用都写日志（含窗口矩形与 GetWindowRgn 返回值）便于复核。
    /// </summary>
    private void ApplyWindowAppearance()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            if (IsWin11)
            {
                int preference = DWMWCP_ROUND;
                _ = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                LogAppearance("Win11: DWM 圆角（本机为 Win10，该分支未实测）");
                return;
            }

            // Windows 10：偏移补偿圆角 region（当前实现下无系统阴影，保持现状）。
            ApplyRoundedRegion();

            // 缩放循环期间每一步都会走到这里：跳过外观日志（GetWindowRect + 建区 + 落盘日志
            // 都是每步的额外开销），缩放结束时由 OnResizeGateTick 统一补记一次。
            if (!_resizing)
            {
                LogAppearance($"Win10: 圆角 region r={WindowCornerRadius}（当前实现下无系统阴影，保持现状）");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("应用窗口外观失败: " + ex.Message);
        }
    }

    /// <summary>
    /// Windows 10 圆角：把圆角矩形区域按「可见盒」偏移补偿后应用。
    ///
    /// 关键（实测）：窗口带 WS_THICKFRAME 时，GetWindowRect 里含约 7px 的透明厚框，
    /// 而 SetWindowRgn 的坐标基准正是这个「含厚框的窗口矩形」——若直接从 (0,0,w,h) 算，
    /// 半径 ≤ 30 的圆弧会整段落在厚框内、视觉完全无效。因此这里运行时实测四边内缩量
    /// （窗口矩形 − 客户区矩形：Win10 实测 左/右 = 7px、顶部/底部 = 0px，见 WndProc 的
    /// WM_NCCALCSIZE 处理），把 region 对齐到可见内容盒，圆弧才真正切到可见像素。
    ///
    /// 四边内缩量只在**非缩放**状态下实测并缓存，缩放中沿用缓存值：缩放途中
    /// GetClientRect 可能尚未反映最新尺寸（实测会让底部内缩量瞬时变成约 7px），
    /// 若每步都重算，region 会比窗口矮/窄一截，新暴露的条带就落到 region 之外 →
    /// 露出窗口后面的桌面（深色壁纸下即用户看到的黑边）。
    /// </summary>
    private void ApplyRoundedRegion()
    {
        GetWindowRect(Handle, out RECT wr);
        int w = wr.Right - wr.Left;
        int h = wr.Bottom - wr.Top;

        if (!_resizing || _rgnLeft < 0)
        {
            GetClientRect(Handle, out RECT cr);
            POINT origin = new() { X = 0, Y = 0 };
            _ = ClientToScreen(Handle, ref origin);

            _rgnLeft = origin.X - wr.Left;
            _rgnTop = origin.Y - wr.Top;
            _rgnRight = wr.Right - (origin.X + cr.Right);
            _rgnBottom = wr.Bottom - (origin.Y + cr.Bottom);
        }

        int diameter = WindowCornerRadius * 2;
        IntPtr rgn = CreateRoundRectRgn(
            _rgnLeft, _rgnTop, w - _rgnRight + 1, h - _rgnBottom + 1, diameter, diameter);

        // redraw:true —— 让系统在套用新区域后立即重绘「新暴露的条带」，
        // 避免该条带短暂露出窗口后面未绘制的内容。
        _ = SetWindowRgn(Handle, rgn, redraw: true);
    }

    /// <summary>记录窗口外观复核数据：窗口矩形、GetWindowRgn 返回的区域类型。</summary>
    private void LogAppearance(string tag)
    {
        _ = GetWindowRect(Handle, out RECT rect);

        // GetWindowRgn 需要一个目标区域句柄；用一个 1x1 占位区域只为取返回值（区域类型）
        IntPtr probe = CreateRectRgn(0, 0, 1, 1);
        int regionType = probe == IntPtr.Zero ? -1 : GetWindowRgn(Handle, probe);
        if (probe != IntPtr.Zero)
        {
            _ = DeleteObject(probe);
        }

        Logger.Info(
            $"窗口外观: {tag} | 窗口矩形=({rect.Left},{rect.Top},{rect.Right - rect.Left}x{rect.Bottom - rect.Top}) "
            + $"| 内缩量=({_rgnLeft},{_rgnTop},{_rgnRight},{_rgnBottom}) "
            + $"| GetWindowRgn={regionType}(0=ERROR(无区域),1=NULL,2=SIMPLE,3=COMPLEX)");
    }

    /// <summary>
    /// 同步重绘窗口自身与其全部子窗口（含 WebView2 的宿主子窗口）。
    /// 缩放中每步 Windows 会把窗口矩形先改大，但新扩大的条带不会自动重绘——
    /// 该条带若位于 WebView2 子窗口尚未跟上的区域，就会保留屏幕上的旧内容
    /// （即窗口后面的桌面；深色壁纸下呈现为黑边）。用它强制立刻重绘以消除该条带。
    /// </summary>
    private void RepaintNow()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        _ = RedrawWindow(
            Handle, IntPtr.Zero, IntPtr.Zero,
            RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
    }

    /// <summary>RedrawWindow：同步重绘窗口（含子窗口）。</summary>
    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool AdjustWindowRectEx(ref RECT lpRect, int dwStyle, bool bMenu, int dwExStyle);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>WM_NCCALCSIZE（wParam = TRUE）时 lParam 指向的结构。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NCCALCSIZE_PARAMS
    {
        public RECT rc0;
        public RECT rc1;
        public RECT rc2;
        public IntPtr lppos;
    }

    /// <summary>GetWindowPlacement 的窗口位置信息（用于取回正常态矩形）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }
}