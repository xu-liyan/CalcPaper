using System.Text;

namespace CalcPaper;

/// <summary>
/// 应用数据目录：集中在程序目录下的 data\（程序目录 = exe 所在目录）。
///
/// 其中放置：settings.json、autosave.json、app.log、WebView2\（WebView2 用户数据目录）。
/// 约定：
/// · 启动早期（创建 WebView2 之前）必须先调用 <see cref="TryPrepareDataDir"/> 做可写性检查，
///   不可写时由调用方明确提示用户并把整个程序文件夹移动到可写位置，**不**静默回退到 %APPDATA%；
/// · 首次运行且新 data\ 内没有任何数据文件时，尝试从旧的
///   %APPDATA%\CalculationPad\ 迁移一次（旧文件保留、不删除）。
/// </summary>
internal static class AppPaths
{
    /// <summary>程序目录（exe 所在目录；dotnet run 时是 bin\&lt;配置&gt;\net8.0-windows\）。</summary>
    public static string BaseDir { get; } = AppContext.BaseDirectory;

    /// <summary>数据目录：&lt;程序目录&gt;\data\。</summary>
    public static string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>日志文件。</summary>
    public static string LogFile => Path.Combine(DataDir, "app.log");

    /// <summary>未绑定稿纸文件时的自动保存落点。</summary>
    public static string AutosaveFile => Path.Combine(DataDir, "autosave.json");

    /// <summary>应用级设置文件（角度制、千分位、全局快捷键、上次绑定路径）。</summary>
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>WebView2 用户数据目录，避免使用默认目录带来的权限问题。</summary>
    public static string WebView2DataDir => Path.Combine(DataDir, "WebView2");

    /// <summary>旧版数据目录：%APPDATA%\CalculationPad（仅用于一次性迁移）。</summary>
    public static string LegacyDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CalculationPad");

    /// <summary>
    /// 数据目录可写性检查：创建目录 → 写探测文件 → 删除。
    /// 成功返回 true；失败返回 false 并给出面向用户的中文提示（含原因），调用方应提示后退出。
    /// </summary>
    public static bool TryPrepareDataDir(out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(DataDir);

            string probe = Path.Combine(DataDir, ".write_probe");
            File.WriteAllText(probe, "probe", Encoding.UTF8);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error =
                "当前程序文件夹不可写，请把整个程序文件夹移动到可写位置（如 D 盘）后再运行。\r\n\r\n"
                + $"数据目录：{DataDir}\r\n原因：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 一次性旧数据迁移：仅当 data\settings.json 与 data\autosave.json **都不存在**、
    /// 且旧的 %APPDATA%\CalculationPad\ 存在时，把其中的 settings.json / autosave.json
    /// 复制到 data\（旧文件保留、不删除）。返回是否发生了迁移。
    /// </summary>
    public static bool MigrateLegacyDataIfNeeded()
    {
        try
        {
            // 迁移后 data 里就有文件了，自然不会重复迁移
            if (File.Exists(SettingsFile) || File.Exists(AutosaveFile))
            {
                return false;
            }

            if (!Directory.Exists(LegacyDataDir))
            {
                return false;
            }

            bool migrated = false;
            foreach (string name in new[] { "settings.json", "autosave.json" })
            {
                string source = Path.Combine(LegacyDataDir, name);
                if (!File.Exists(source))
                {
                    continue;
                }

                File.Copy(source, Path.Combine(DataDir, name), overwrite: false);
                migrated = true;
            }

            if (migrated)
            {
                Logger.Info($"已从旧位置迁移数据: {LegacyDataDir} → {DataDir}");
            }

            return migrated;
        }
        catch (Exception ex)
        {
            Logger.Error("迁移旧数据失败: " + ex.Message);
            return false;
        }
    }
}