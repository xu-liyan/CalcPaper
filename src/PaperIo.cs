using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CalcPaper;

/// <summary>稿纸中的一行（纯数据）。</summary>
internal sealed class PaperRow
{
    public string Expr { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;
}

/// <summary>稿纸文档（写入磁盘的结构）：{ version, rows: [{ expr, note }] }。</summary>
internal sealed class PaperDocument
{
    public int Version { get; set; } = 1;

    public List<PaperRow> Rows { get; set; } = new();
}

/// <summary>
/// 应用级设置（settings.json）：
/// { angleMode, useGrouping, lastFilePath, globalHotkey }。
/// globalHotkey 为**唯一**的全局快捷键设置值：组合键串（如 "Alt+C"）、
/// 双击键串（如 "双击 Alt"），空串表示「未设置」（不注册热键、不启用双击键）。
/// 注意：startOnBoot 不写入本文件，以注册表为唯一来源。
/// </summary>
internal sealed class AppSettings
{
    /// <summary>默认全局快捷键。</summary>
    public const string DefaultGlobalHotkey = "Alt+C";

    /// <summary>双击键取值前缀（如 "双击 F5"）。</summary>
    public const string DoubleKeyPrefix = "双击 ";

    public string AngleMode { get; set; } = "deg";

    public bool UseGrouping { get; set; } = true;

    public string? LastFilePath { get; set; }

    /// <summary>唤醒窗口的全局快捷键（组合键串 / "双击 XXX" / 空串表示未设置）。</summary>
    public string GlobalHotkey { get; set; } = DefaultGlobalHotkey;
}

/// <summary>
/// 稿纸 / 设置的纯 IO 与校验逻辑：不依赖任何 UI 与宿主类型（仅依赖 BCL），
/// 因此可以单独抽出来做独立测试。约定：所有方法都不向调用方抛异常，
/// 失败一律以返回值中的中文错误信息表达。
/// </summary>
internal static class PaperIo
{
    /// <summary>结构非法时统一使用的错误文案。</summary>
    public const string InvalidPaperMessage = "文件不是有效的稿纸格式";

    /// <summary>写盘用 JSON 选项：缩进 + 中文不转义 + camelCase 属性名 + 反序列化大小写不敏感。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>UTF-8 不带 BOM。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // ======================================================================
    // 稿纸
    // ======================================================================

    /// <summary>
    /// 读取并校验稿纸文件。成功时 Error 为 null；失败时 Rows 为 null 且 Error 为中文文案。
    /// Warning 用于「版本缺失或非 1」这类可接受但需知会调用方（记日志）的情况。
    /// </summary>
    public static (List<PaperRow>? Rows, string? Error, string? Warning) LoadPaper(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, "文件路径为空", null);
        }

        if (!File.Exists(path))
        {
            return (null, "文件不存在", null);
        }

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return (null, "读取文件失败：" + ex.Message, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch
        {
            return (null, InvalidPaperMessage, null);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, InvalidPaperMessage, null);
            }

            if (!TryGetProperty(root, "rows", out JsonElement rowsElement) ||
                rowsElement.ValueKind != JsonValueKind.Array)
            {
                return (null, InvalidPaperMessage, null);
            }

            var rows = new List<PaperRow>();
            foreach (JsonElement item in rowsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return (null, InvalidPaperMessage, null);
                }

                rows.Add(new PaperRow
                {
                    Expr = ReadString(item, "expr"),
                    Note = ReadString(item, "note")
                });
            }

            // version 缺失或非 1 也接受，仅以 Warning 形式告知调用方
            bool versionOk =
                TryGetProperty(root, "version", out JsonElement versionElement) &&
                versionElement.ValueKind == JsonValueKind.Number &&
                versionElement.TryGetInt32(out int version) &&
                version == 1;

            string? warning = versionOk ? null : "稿纸版本缺失或非 1，已按当前格式读取";
            return (rows, null, warning);
        }
    }

    /// <summary>原子写入稿纸文件。返回 null 表示成功，否则为中文错误信息。</summary>
    public static string? SavePaper(string? path, IEnumerable<PaperRow>? rows)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "文件路径为空";
        }

        var document = new PaperDocument
        {
            Version = 1,
            Rows = NormalizeRows(rows)
        };

        try
        {
            WriteJsonAtomic(path, document);
            return null;
        }
        catch (Exception ex)
        {
            return "写入文件失败：" + ex.Message;
        }
    }

    // ======================================================================
    // 设置
    // ======================================================================

    /// <summary>
    /// 读取设置；文件缺失、为空或损坏时一律返回默认值（deg / true / null / Alt+C），不抛异常。
    /// 一次性迁移：若 json 中不存在 <c>globalHotkey</c> 字段（旧版双开关格式），则按旧字段
    /// <c>globalHotkeyEnabled</c> / <c>doubleAltEnabled</c> 推导出单值并**回写** settings.json
    /// （此后只写 globalHotkey 一个字段）。
    /// </summary>
    public static AppSettings LoadSettings(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new AppSettings();
        }

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch
        {
            return new AppSettings();
        }

        AppSettings settings;
        bool migrated = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new AppSettings();
            }

            AppSettings? parsed = JsonSerializer.Deserialize<AppSettings>(text, JsonOptions);
            settings = parsed ?? new AppSettings();

            // 旧格式（无 globalHotkey 字段）→ 由旧的双开关推导单值，并标记需要回写
            if (!TryGetProperty(root, "globalHotkey", out _))
            {
                settings.GlobalHotkey = MigrateGlobalHotkey(root);
                migrated = true;
            }
        }
        catch
        {
            return new AppSettings();
        }

        settings = NormalizeSettings(settings);

        if (migrated)
        {
            WriteMigratedSettings(path, settings);
        }

        return settings;
    }

    /// <summary>原子写入设置。返回 null 表示成功，否则为中文错误信息。</summary>
    public static string? SaveSettings(string? path, AppSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "文件路径为空";
        }

        try
        {
            WriteJsonAtomic(path, NormalizeSettings(settings));
            return null;
        }
        catch (Exception ex)
        {
            return "写入设置失败：" + ex.Message;
        }
    }

    // ======================================================================
    // 内部实现
    // ======================================================================

    private static List<PaperRow> NormalizeRows(IEnumerable<PaperRow>? rows)
    {
        var list = new List<PaperRow>();
        if (rows == null)
        {
            return list;
        }

        foreach (PaperRow row in rows)
        {
            if (row == null)
            {
                list.Add(new PaperRow());
                continue;
            }

            list.Add(new PaperRow
            {
                Expr = row.Expr ?? string.Empty,
                Note = row.Note ?? string.Empty
            });
        }

        return list;
    }

    /// <summary>
    /// 把设置规整到合法取值：angleMode ∈ {deg, rad}，useGrouping 为布尔，lastFilePath 空即 null，
    /// globalHotkey 去首尾空白（空串表示「未设置」，原样保留，不回落默认值）。
    /// </summary>
    private static AppSettings NormalizeSettings(AppSettings? settings)
    {
        if (settings == null)
        {
            return new AppSettings();
        }

        return new AppSettings
        {
            AngleMode = (settings.AngleMode == "rad") ? "rad" : "deg",
            UseGrouping = settings.UseGrouping,
            LastFilePath = string.IsNullOrWhiteSpace(settings.LastFilePath) ? null : settings.LastFilePath,
            GlobalHotkey = settings.GlobalHotkey?.Trim() ?? string.Empty
        };
    }

    /// <summary>
    /// 旧格式（globalHotkeyEnabled / doubleAltEnabled 双开关）→ 新单值 globalHotkey 的一次性推导：
    /// · 两字段都缺省 → 默认 "Alt+C"；
    /// · globalHotkeyEnabled=true → 旧 globalHotkey（缺失则默认 "Alt+C"）；
    /// · 否则 doubleAltEnabled=true → "双击 Alt"；
    /// · 其余（旧字段都被显式关闭）→ 空串（未设置）。
    /// </summary>
    private static string MigrateGlobalHotkey(JsonElement root)
    {
        bool? hotkeyEnabled = ReadBoolOrNull(root, "globalHotkeyEnabled");
        bool? doubleAltEnabled = ReadBoolOrNull(root, "doubleAltEnabled");

        if (hotkeyEnabled == null && doubleAltEnabled == null)
        {
            return AppSettings.DefaultGlobalHotkey;
        }

        if (hotkeyEnabled == true)
        {
            string old = ReadString(root, "globalHotkey");
            return string.IsNullOrWhiteSpace(old) ? AppSettings.DefaultGlobalHotkey : old.Trim();
        }

        if (doubleAltEnabled == true)
        {
            return AppSettings.DoubleKeyPrefix + "Alt";
        }

        return string.Empty;
    }

    /// <summary>把迁移结果回写 settings.json（只写新字段），并记录日志。</summary>
    private static void WriteMigratedSettings(string path, AppSettings settings)
    {
        string display = settings.GlobalHotkey.Length == 0 ? "未设置" : settings.GlobalHotkey;
        Logger.Info($"设置迁移: 全局快捷键 → {display}");

        string? error = SaveSettings(path, settings);
        if (error != null)
        {
            Logger.Error("设置迁移回写失败: " + error);
        }
    }

    /// <summary>原子写入：先写 path + ".tmp"，成功后 File.Move 覆盖目标；失败清理临时文件。</summary>
    private static void WriteJsonAtomic<T>(string path, T value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json, Utf8NoBom);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理临时文件失败不影响错误上报
        }
    }

    /// <summary>读取字符串字段；字段缺失或类型不是字符串时按空串处理。</summary>
    private static string ReadString(JsonElement obj, string name)
    {
        if (TryGetProperty(obj, name, out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>读取布尔字段；字段缺失或类型不是布尔时返回 null。</summary>
    private static bool? ReadBoolOrNull(JsonElement obj, string name)
    {
        if (TryGetProperty(obj, name, out JsonElement element) &&
            (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False))
        {
            return element.GetBoolean();
        }

        return null;
    }

    /// <summary>大小写不敏感地取属性（兼容手工编辑过的 json）。</summary>
    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>
/// 把文件送入系统回收站（Shell 的 SHFileOperationW + FOF_ALLOWUNDO）。
/// 不抛异常：成功返回 null，失败返回中文错误信息。
/// </summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;

    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>把 path 指向的文件送入回收站。文件不存在视为成功（返回 null）。</summary>
    public static string? SendToRecycleBin(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "文件路径为空";
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                // pFrom 为双 null 结尾的多字符串（本处仅一个路径）
                pFrom = path + "\0\0",
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
            };

            int result = SHFileOperation(ref op);
            if (result != 0)
            {
                return $"送入回收站失败（Shell 错误码 {result}）";
            }

            if (op.fAnyOperationsAborted)
            {
                return "删除操作被中止";
            }

            return null;
        }
        catch (Exception ex)
        {
            return "送入回收站失败：" + ex.Message;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;

        public ushort fFlags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;

        public IntPtr hNameMappings;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}