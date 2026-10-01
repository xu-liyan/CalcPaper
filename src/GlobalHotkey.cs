using System.Runtime.InteropServices;

namespace CalcPaper;

/// <summary>
/// 全局快捷键：把设置字符串（如 "Alt+C"）解析为修饰键 + 虚拟键，并用
/// RegisterHotKey 绑定到指定窗口句柄（固定 id）。
///
/// 命中后由窗口的 WndProc 收到 WM_HOTKEY(0x0312) 并唤醒窗口。
/// 所有方法都不抛异常：失败时以返回值中的中文错误信息表达（同时记日志由调用方决定）。
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    /// <summary>固定注册 id（同一窗口内唯一即可）。</summary>
    private const int HotkeyId = 0x43A1;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private IntPtr _hwnd = IntPtr.Zero;
    private bool _registered;

    /// <summary>最近一次注册/解析的错误信息；成功或未启用时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 按设置应用：先注销旧绑定，再按需注册。成功返回 null，失败返回中文错误信息。
    /// enabled=false 时不注册任何热键（返回 null）。
    /// </summary>
    public string? Apply(IntPtr hwnd, string? spec, bool enabled)
    {
        Unregister();

        if (!enabled)
        {
            LastError = null;
            return null;
        }

        if (!TryParse(spec, out uint modifiers, out uint virtualKey, out string parseError))
        {
            LastError = parseError;
            return parseError;
        }

        if (hwnd == IntPtr.Zero ||
            !RegisterHotKey(hwnd, HotkeyId, modifiers | MOD_NOREPEAT, virtualKey))
        {
            LastError = $"全局快捷键 {spec} 注册失败（可能已被其它程序占用）";
            return LastError;
        }

        _hwnd = hwnd;
        _registered = true;
        LastError = null;
        return null;
    }

    /// <summary>注销当前绑定（未注册时什么也不做）。</summary>
    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        try
        {
            _ = UnregisterHotKey(_hwnd, HotkeyId);
        }
        catch
        {
            // 注销失败无需处理
        }

        _hwnd = IntPtr.Zero;
    }

    public void Dispose() => Unregister();

    // ======================================================================
    // 解析
    // ======================================================================

    /// <summary>解析 "Ctrl/Alt/Shift/Win + A-Z/0-9/F1-F12" 形式的快捷键串。</summary>
    private static bool TryParse(string? spec, out uint modifiers, out uint virtualKey, out string error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "全局快捷键为空";
            return false;
        }

        string[] parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? keyPart = null;

        foreach (string part in parts)
        {
            if (TryParseModifier(part, out uint modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (keyPart != null)
            {
                error = $"无法识别的全局快捷键：{spec}";
                return false;
            }

            keyPart = part;
        }

        if (keyPart == null)
        {
            error = $"无法识别的全局快捷键：{spec}（缺少主键，如 Alt+C）";
            return false;
        }

        if (modifiers == 0)
        {
            error = $"无法识别的全局快捷键：{spec}（至少需要一个修饰键）";
            return false;
        }

        if (!TryParseKey(keyPart, out virtualKey))
        {
            error = $"无法识别的全局快捷键：{spec}（不支持的主键 {keyPart}）";
            return false;
        }

        return true;
    }

    private static bool TryParseModifier(string token, out uint modifier)
    {
        switch (token.Trim().ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                modifier = MOD_CONTROL;
                return true;
            case "alt":
                modifier = MOD_ALT;
                return true;
            case "shift":
                modifier = MOD_SHIFT;
                return true;
            case "win":
            case "windows":
                modifier = MOD_WIN;
                return true;
            default:
                modifier = 0;
                return false;
        }
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        string key = token.Trim().ToUpperInvariant();

        if (key.Length == 1)
        {
            char c = key[0];
            if (c >= 'A' && c <= 'Z')
            {
                virtualKey = (uint)c;
                return true;
            }

            if (c >= '0' && c <= '9')
            {
                virtualKey = (uint)c;
                return true;
            }

            return false;
        }

        if (key.Length is 2 or 3 && key[0] == 'F' &&
            int.TryParse(key.AsSpan(1), out int index) && index >= 1 && index <= 12)
        {
            virtualKey = (uint)(0x70 + index - 1);   // VK_F1 = 0x70
            return true;
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}