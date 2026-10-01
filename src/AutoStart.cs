using System.Diagnostics;
using Microsoft.Win32;

namespace CalcPaper;

/// <summary>
/// 开机自启动（当前用户级）。
///
/// ── 约定 ────────────────────────────────────────────────────────────
/// · 位置：HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
///   （当前用户级，写入无需管理员权限）
/// · 值名：「计算稿纸」
/// · 值内容：&lt;exe 路径（双引号包裹，容忍空格）&gt; + 空格 + --autostart
///   —— 登录时由系统按此命令行启动，主程序据此进入「静默后台」模式（不显示窗口与动画；
///   需使用时用全局快捷键或单击托盘唤醒；手动双击无参数启动则正常显示 GUI）
/// · 判定口径：**仅认新格式**（必须同时指向自身 exe 且带 --autostart 参数）；
///   历史遗留的旧格式（只有 exe 路径）视为「未启用」，需在设置里重新勾选一次
/// · 纯逻辑，不依赖任何 UI 与宿主类型，便于独立测试
/// · 所有方法都不抛异常；失败时以返回值中的中文错误信息表达，
///   由调用方（Bridge）决定记日志 / 提示用户
/// ──────────────────────────────────────────────────────────────────
/// </summary>
internal static class AutoStart
{
    /// <summary>当前用户级 Run 键路径。</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>注册表值名。</summary>
    public const string ValueName = "计算稿纸";

    // ======================================================================
    // 读取
    // ======================================================================

    /// <summary>读取注册表实际状态：值与当前进程可执行文件路径一致才算已启用。</summary>
    public static bool IsEnabled() => IsEnabled(out _);

    /// <summary>读取注册表实际状态；失败时 error 返回中文原因（不抛异常）。</summary>
    public static bool IsEnabled(out string? error)
    {
        error = null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key == null)
            {
                // 键不存在 = 未启用（属正常情况，不算错误）
                return false;
            }

            string? stored = key.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(stored))
            {
                return false;
            }

            string expected = ResolveExePath(null);
            if (string.IsNullOrWhiteSpace(expected))
            {
                // 无法确定自身路径时无从比对，按未启用处理
                return false;
            }

            // 仅认新格式：命令行必须为「自身 exe 路径（双引号包裹）+ --autostart」。
            // 历史遗留的旧格式（只有 exe 路径、无 --autostart）一律视为未启用，需重新勾选一次。
            string expectedCommand = BuildCommandLine(expected);
            return string.Equals(stored.Trim(), expectedCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            error = "读取开机自启动注册项失败：" + ex.Message;
            return false;
        }
    }

    // ======================================================================
    // 写入
    // ======================================================================

    /// <summary>开启 / 关闭开机自启动（使用当前进程 exe 路径）。成功返回 null。</summary>
    public static string? SetEnabled(bool enabled) => SetEnabled(enabled, null);

    /// <summary>
    /// 开启 / 关闭开机自启动。exePath 为 null/空时取当前进程可执行文件路径；
    /// 显式传入可覆盖路径（供独立测试注入非法路径等）。成功返回 null，
    /// 失败返回中文错误信息，绝不抛异常。
    /// </summary>
    public static string? SetEnabled(bool enabled, string? exePath)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                return "无法打开开机自启动注册项，设置未生效（可能权限不足）";
            }

            if (!enabled)
            {
                // 关闭：删除该值（值不存在也不报错）
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return null;
            }

            string path = ResolveExePath(exePath);
            if (string.IsNullOrWhiteSpace(path))
            {
                return "无法确定程序路径，开机自启动未生效";
            }

            if (!IsValidExePath(path))
            {
                return "程序路径非法，开机自启动未生效：" + path;
            }

            // 写入命令行：exe 路径（双引号包裹，容忍空格）+ --autostart
            key.SetValue(ValueName, BuildCommandLine(path), RegistryValueKind.String);
            return null;
        }
        catch (Exception ex)
        {
            return "写入开机自启动注册项失败：" + ex.Message;
        }
    }

    // ======================================================================
    // 内部助手
    // ======================================================================

    /// <summary>取可执行文件路径：显式覆盖 &gt; Environment.ProcessPath &gt; MainModule。</summary>
    private static string ResolveExePath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath!;
        }

        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return processPath!;
        }

        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>路径可写性校验：必须为绝对路径且不含非法字符。</summary>
    private static bool IsValidExePath(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path))
            {
                return false;
            }

            return path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>拼装开机自启动命令行：「exe 路径」+ 空格 + --autostart。</summary>
    private static string BuildCommandLine(string path) =>
        Quote(path) + " " + ShellIntegration.AutoStartArgument;

    private static string Quote(string path) => "\"" + path + "\"";
}