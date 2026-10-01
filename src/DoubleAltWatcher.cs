using System.Runtime.InteropServices;

namespace CalcPaper;

/// <summary>
/// 全局键盘监听（WH_KEYBOARD_LL 低级键盘钩子），两项职责：
/// · **Alt+F4 → 最小化到任务栏**（始终生效，独立于任何设置）；
/// · **400ms 内双击指定单键 → 唤醒窗口**（受设置的 globalHotkey 控制；目标键可为
///   修饰键 Alt/Ctrl/Shift/Win，或功能键 F1–F12 / CapsLock / Tab）。
///
/// 为什么 Alt+F4 必须走钩子：WebView2 内容的键盘输入窗口（Chrome_WidgetWin_1）属于
/// WebView2 浏览器进程，按键消息不会进入本线程的消息队列，因此窗体的 FormClosing 与
/// Application.AddMessageFilter 都收不到 Alt+F4（已实测验证）。
///
/// ── 约定 ────────────────────────────────────────────────────────────
/// · 回调必须极简快速：只做少量算术判断，命中后把动作转投到 UI 消息队列（BeginInvoke）；
/// · **只监听、绝不拦截**：始终返回 CallNextHookEx 的结果（不吞键，避免影响其它程序）；
/// · 忽略按键自动重复（用“是否已按下”的状态位判断），并用 KBDLLHOOKSTRUCT.flags 的
///   LLKHF_UP 位区分按下 / 抬起；双击期间出现其它按键则重置计数；
/// · 钩子在整个运行期保持安装（Alt+F4 兜底不依赖任何设置），仅在退出时 UnhookWindowsHookEx；
/// · 已知限制：个别安全软件环境下键盘钩子可能被拦截，此时上述功能不生效。
/// ──────────────────────────────────────────────────────────────────
/// </summary>
internal sealed class DoubleAltWatcher : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const uint VK_TAB = 0x09;
    private const uint VK_SHIFT = 0x10;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_MENU = 0x12;
    private const uint VK_CAPITAL = 0x14;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_F4 = 0x73;

    // 左右变体：低级钩子下修饰键可能以左右键码上报
    private const uint VK_LSHIFT = 0xA0;
    private const uint VK_RSHIFT = 0xA1;
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;
    private const uint VK_LMENU = 0xA4;
    private const uint VK_RMENU = 0xA5;

    private const uint LLKHF_UP = 0x80;

    private const int DoubleKeyWindowMs = 400;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly Action _onDoubleKey;
    private readonly Action _onAltF4;
    private readonly HookProc _proc;   // 必须保持强引用，否则委托被 GC 回收后钩子回调会崩溃

    private IntPtr _hook = IntPtr.Zero;
    private bool _doubleKeyEnabled;
    private uint[] _targetKeys = Array.Empty<uint>();
    private bool _altDown;                               // Alt 是否按下（Alt+F4 兜底用，独立于目标键）
    private bool _targetDown;                            // 目标键是否按下（用于忽略自动重复）
    private long _lastTargetDownTicks = long.MinValue;   // 上一次目标键按下的时间

    public DoubleAltWatcher(Action onDoubleKey, Action onAltF4)
    {
        _onDoubleKey = onDoubleKey;
        _onAltF4 = onAltF4;
        _proc = HookCallback;
    }

    /// <summary>钩子是否已安装。</summary>
    public bool IsEnabled => _hook != IntPtr.Zero;

    /// <summary>
    /// 按设置启用 / 停用「双击指定键」逻辑（幂等）。钩子本身保持安装
    /// —— Alt+F4 兜底不依赖设置，只有 <see cref="Dispose"/> 才会卸载钩子。
    /// enabled=false 或目标键集合为空时不进行双击判定。
    /// </summary>
    public void SetDoubleKey(bool enabled, uint[]? targetKeys)
    {
        _doubleKeyEnabled = enabled && targetKeys != null && targetKeys.Length > 0;
        _targetKeys = _doubleKeyEnabled ? targetKeys! : Array.Empty<uint>();
        _targetDown = false;
        _lastTargetDownTicks = long.MinValue;
        Start();
    }

    /// <summary>
    /// 解析双击键名（如 "Alt" / "F5" / "CapsLock" / "Tab"），得到可匹配的虚拟键码集合
    /// （修饰键包含左右变体）。无法识别时返回 false。
    /// </summary>
    public static bool TryParseTarget(string? name, out uint[] virtualKeys)
    {
        virtualKeys = Array.Empty<uint>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string key = name.Trim();
        switch (key.ToLowerInvariant())
        {
            case "alt":
                virtualKeys = new[] { VK_MENU, VK_LMENU, VK_RMENU };
                return true;
            case "ctrl":
            case "control":
                virtualKeys = new[] { VK_CONTROL, VK_LCONTROL, VK_RCONTROL };
                return true;
            case "shift":
                virtualKeys = new[] { VK_SHIFT, VK_LSHIFT, VK_RSHIFT };
                return true;
            case "win":
            case "windows":
                virtualKeys = new[] { VK_LWIN, VK_RWIN };
                return true;
            case "capslock":
                virtualKeys = new[] { VK_CAPITAL };
                return true;
            case "tab":
                virtualKeys = new[] { VK_TAB };
                return true;
        }

        // F1–F12（VK_F1 = 0x70）
        if (key.Length is 2 or 3 && (key[0] == 'F' || key[0] == 'f') &&
            int.TryParse(key.AsSpan(1), out int index) && index >= 1 && index <= 12)
        {
            virtualKeys = new[] { (uint)(0x70 + index - 1) };
            return true;
        }

        return false;
    }

    private void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        try
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Logger.Error($"安装键盘钩子失败（Alt+F4 兜底与双击键唤醒均不可用），Win32 错误码 {Marshal.GetLastWin32Error()}");
            }

            _altDown = false;
            _targetDown = false;
            _lastTargetDownTicks = long.MinValue;
        }
        catch (Exception ex)
        {
            Logger.Error("安装键盘钩子异常: " + ex.Message);
        }
    }

    private void Stop()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = UnhookWindowsHookEx(_hook);
        }
        catch
        {
            // 卸载失败无需处理（进程退出时系统会清理）
        }

        _hook = IntPtr.Zero;
    }

    public void Dispose() => Stop();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                Detect(wParam.ToInt32(), lParam);
            }
        }
        catch
        {
            // 钩子回调内绝不抛出异常
        }

        // 只监听、绝不拦截
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void Detect(int message, IntPtr lParam)
    {
        KBDLLHOOKSTRUCT data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        uint vk = data.vkCode;

        bool isUp = (data.flags & LLKHF_UP) != 0
                    || message == WM_KEYUP
                    || message == WM_SYSKEYUP;

        if (isUp)
        {
            if (IsAlt(vk))
            {
                _altDown = false;
            }

            if (IsTarget(vk))
            {
                _targetDown = false;
            }

            return;
        }

        bool isDown = (data.flags & LLKHF_UP) == 0
                      && (message == WM_KEYDOWN || message == WM_SYSKEYDOWN);
        if (!isDown)
        {
            return;
        }

        // Alt+F4 → 最小化（独立于全局快捷键设置，始终生效；排除 Ctrl/Shift 组合）
        if (vk == VK_F4 && _altDown &&
            (GetAsyncKeyState((int)VK_CONTROL) & 0x8000) == 0 &&
            (GetAsyncKeyState((int)VK_SHIFT) & 0x8000) == 0)
        {
            // 置回未按下状态，避免按住 F4 的自动重复反复触发
            _altDown = false;
            _targetDown = false;
            _lastTargetDownTicks = long.MinValue;
            _onAltF4();
            return;
        }

        if (IsAlt(vk))
        {
            _altDown = true;
        }

        if (!IsTarget(vk))
        {
            // 未启用双击键，或按下的不是目标键：清零计数（其它按键会重置双击识别）
            _targetDown = false;
            _lastTargetDownTicks = long.MinValue;
            return;
        }

        if (_targetDown)
        {
            // 按键自动重复，忽略
            return;
        }

        _targetDown = true;

        long now = Environment.TickCount64;
        if (_lastTargetDownTicks != long.MinValue && now - _lastTargetDownTicks <= DoubleKeyWindowMs)
        {
            _lastTargetDownTicks = long.MinValue;
            _onDoubleKey();
        }
        else
        {
            _lastTargetDownTicks = now;
        }
    }

    private static bool IsAlt(uint vk) => vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU;

    private bool IsTarget(uint vk)
    {
        if (!_doubleKeyEnabled)
        {
            return false;
        }

        uint[] keys = _targetKeys;
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] == vk)
            {
                return true;
            }
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}