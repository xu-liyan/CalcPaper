using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Web.WebView2.Core;

namespace CalcPaper;

/// <summary>
/// JS ↔ C# 消息桥（宿主侧）。
///
/// 收到页面消息（CoreWebView2.WebMessageReceived 的 JSON 原文）后解析并分发；
/// 需要回发或推送给页面时调用 MainForm.PostToJs（即 CoreWebView2.PostWebMessageAsJson）。
///
/// ── 消息协议（后续任务依赖，务必保持稳定）──────────────────────────────
/// JS → C#：
///   { "cmd": "window", "action": "drag|minimize|close|resize|pin", "edge": "l|r|t|b|tl|tr|bl|br", "value": true|false }
///       （close = 隐藏到托盘，不退出；pin 用 value 置顶/取消置顶）
///   { "cmd": "ready" }
///   { "cmd": "ping" }
///   { "cmd": "flushed" }                                  （页面已完成退出前落盘）
///   { "cmd": "exitApp" }                                  （完全退出：先落盘）
///   { "cmd": "saveAs",    "paper": { "rows": [ { "expr": "...", "note": "..." } ] } }
///   { "cmd": "open" }
///   { "cmd": "autosave",  "paper": { "rows": [ ... ] } }
///   { "cmd": "getPaper" }
///   { "cmd": "deletePaper" }                              （把绑定文件送入回收站并解绑）
///   { "cmd": "unbindFile" }                               （仅解绑，不删除文件）
///   { "cmd": "getSettings" }
///   { "cmd": "setSettings", "angleMode": "deg|rad", "useGrouping": true|false, "startOnBoot": true|false,
///                           "globalHotkey": "Alt+C" | "双击 Alt" | "",
///                           "menuOrder": ["^", "x2", ...] }
///       （globalHotkey 为唯一取值，空串表示「未设置」；
///         menuOrder 为运算菜单的稳定 id 数组，缺省表示不改动）
///   { "cmd": "confirm",   "text": "..." }
///   { "cmd": "copyToClipboard", "text": "..." }
/// C# → JS：
///   { "type": "pong" }
///   { "type": "error",   "message": "..." }
///   { "type": "paper",     "rows": [...], "path": "..."|null, "fromAuto": bool, "isNew": bool }
///   { "type": "settings",  "angleMode": "deg|rad", "useGrouping": bool, "startOnBoot": bool,
///                          "globalHotkey": "...", "hotkeyError"?: "...",
///                          "menuOrder": ["^", ...] | null }（null = 无该字段，界面用默认顺序）
///   { "type": "saved",     "ok": bool, "path"?: "...", "canceled"?: true, "error"?: "..." }
///   { "type": "opened",    "ok": bool, "path"?: "...", "paper"?: { rows: [...] },
///                          "canceled"?: true, "error"?: "..." }
///   { "type": "autosaved", "ok": bool, "path"?: "...", "auto"?: bool, "at"?: "HH:mm", "error"?: "..." }
///   { "type": "confirm",   "ok": bool }
///   { "type": "copied",    "ok": bool, "text"?: "...", "error"?: "..." }
///   { "type": "deleted",   "ok": bool, "path"?: "...", "error"?: "..." }
///   { "type": "unbound",   "ok": bool }
///   { "type": "windowState", "state": "hidden" | "minimized" | "restored" }
///   { "type": "pinned",     "value": true|false }          （窗口置顶状态；按钮视觉据此更新）
///   { "type": "flush" }                                   （请在落盘完成后回 {cmd:'flushed'}）
/// ──────────────────────────────────────────────────────────────
/// </summary>
internal sealed class Bridge
{
    /// <summary>回发消息用的 JSON 选项：camelCase + 中文不转义。</summary>
    private static readonly JsonSerializerOptions MessageJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly MainForm _form;

    public Bridge(MainForm form)
    {
        _form = form;
    }

    /// <summary>WebMessageReceived 的事件处理入口。</summary>
    public void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            Logger.Error("读取页面消息失败: " + ex.Message);
            return;
        }

        Handle(json);
    }

    /// <summary>解析并分发一条来自页面的消息。任何异常都在此兜底，绝不向外抛出。</summary>
    public void Handle(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            // 解析失败兜底：记日志 + 回 error，宿主继续运行
            Logger.Error($"消息解析失败: {ex.Message} | 原文: {Truncate(json)}");
            SendError("消息解析失败");
            return;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("cmd", out JsonElement cmdElement) ||
                cmdElement.ValueKind != JsonValueKind.String)
            {
                Logger.Error($"消息缺少 cmd 字段 | 原文: {Truncate(json)}");
                SendError("消息缺少 cmd 字段");
                return;
            }

            string cmd = cmdElement.GetString() ?? string.Empty;

            // 业务处理统一兜底：单条命令异常只记日志 + 回 error，不影响宿主
            try
            {
                switch (cmd)
                {
                    case "window":
                        HandleWindow(root);
                        break;

                    case "ready":
                        _form.OnPageReady();
                        break;

                    case "ping":
                        SendJson("{\"type\":\"pong\"}");
                        break;

                    case "flushed":
                        Logger.Info("收到页面 flushed（退出前落盘完成）");
                        _form.OnFlushed();
                        break;

                    case "exitApp":
                        _form.ExitApplication();
                        break;

                    case "copyToClipboard":
                        HandleCopyToClipboard(root);
                        break;

                    case "saveAs":
                        HandleSaveAs(root);
                        break;

                    case "open":
                        HandleOpen(root);
                        break;

                    case "autosave":
                        HandleAutosave(root);
                        break;

                    case "getPaper":
                        HandleGetPaper(root);
                        break;

                    case "deletePaper":
                        HandleDeletePaper(root);
                        break;

                    case "unbindFile":
                        HandleUnbindFile(root);
                        break;

                    case "getSettings":
                        HandleGetSettings(root);
                        break;

                    case "setSettings":
                        HandleSetSettings(root);
                        break;

                    case "confirm":
                        HandleConfirm(root);
                        break;

                    default:
                        Logger.Error($"未知命令: {cmd}");
                        SendError($"未知命令: {cmd}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"处理命令 [{cmd}] 失败: {ex}");
                SendError($"命令处理失败: {ex.Message}");
            }
        }
    }

    private void HandleWindow(JsonElement root)
    {
        string action = GetString(root, "action");
        switch (action)
        {
            case "drag":
                _form.BeginDrag();
                break;
            case "minimize":
                _form.MinimizeWindow();
                break;
            case "close":
                // 关闭语义 = 隐藏到托盘（任务栏不留图标，程序继续运行；真正退出走 exitApp / 托盘 / 跳转列表）
                Logger.Info("页面请求关闭，改为隐藏到托盘");
                _form.HideWindow();
                break;
            case "resize":
                _form.BeginResize(GetString(root, "edge"));
                break;
            case "pin":
                // 窗口置顶：value=true 置顶、false 取消；宿主处理 TopMost 后回发 {type:'pinned'}
                HandlePin(root);
                break;
            default:
                Logger.Error($"未知的 window action: {action}");
                SendError($"未知的 window action: {action}");
                break;
        }
    }

    /// <summary>
    /// 窗口置顶命令：value=true 置顶、false 取消置顶。
    /// 参数缺失或类型非法时按 false 处理并记日志（防御性，前端永远发布尔值）。
    /// </summary>
    private void HandlePin(JsonElement root)
    {
        bool value = root.TryGetProperty("value", out JsonElement el)
            && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean()
            : false;

        _form.SetWindowPinned(value);
    }

    // ======================================================================
    // 持久化命令（Task 5）
    // ======================================================================

    /// <summary>另存稿纸：系统保存对话框 → 写入页面传来的 paper → 记为绑定文件。</summary>
    private void HandleSaveAs(JsonElement root)
    {
        List<PaperRow>? rows = ReadPaperRows(root);
        if (rows == null)
        {
            return;   // 参数错误已在 ReadPaperRows 内回包
        }

        string path;
        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = "另存稿纸";
            dialog.Filter = "计算稿纸 (*.json)|*.json|所有文件 (*.*)|*.*";
            dialog.DefaultExt = "json";
            dialog.AddExtension = true;
            dialog.FileName = "计算稿纸.json";
            dialog.OverwritePrompt = true;

            if (dialog.ShowDialog(_form) != DialogResult.OK)
            {
                Logger.Info("另存稿纸已取消");
                Send(new { type = "saved", ok = false, canceled = true });
                return;
            }

            path = dialog.FileName;
        }

        string? error = PaperIo.SavePaper(path, rows);
        if (error != null)
        {
            Logger.Error($"另存稿纸失败: {path} | {error}");
            Send(new { type = "saved", ok = false, error });
            return;
        }

        Logger.Info($"另存稿纸完成: {path}（{rows.Count} 行）");
        BindLastFile(path);
        Send(new { type = "saved", ok = true, path });
    }

    /// <summary>加载稿纸：系统打开对话框 → 校验并回填。非法文件不改动当前稿纸。</summary>
    private void HandleOpen(JsonElement root)
    {
        string path;
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = "加载稿纸";
            dialog.Filter = "计算稿纸 (*.json)|*.json|所有文件 (*.*)|*.*";
            dialog.DefaultExt = "json";
            dialog.CheckFileExists = true;

            if (dialog.ShowDialog(_form) != DialogResult.OK)
            {
                Logger.Info("加载稿纸已取消");
                Send(new { type = "opened", ok = false, canceled = true });
                return;
            }

            path = dialog.FileName;
        }

        var (rows, error, warning) = PaperIo.LoadPaper(path);
        if (rows == null)
        {
            Logger.Error($"加载稿纸失败: {path} | {error}");
            Send(new { type = "opened", ok = false, error = error ?? PaperIo.InvalidPaperMessage });
            return;
        }

        if (!string.IsNullOrEmpty(warning))
        {
            Logger.Info($"加载稿纸提示: {warning}");
        }

        Logger.Info($"加载稿纸: {path}（{rows.Count} 行）");
        BindLastFile(path);
        Send(new { type = "opened", ok = true, path, paper = new { rows } });
    }

    /// <summary>自动保存：优先写绑定文件，否则写 autosave.json。</summary>
    private void HandleAutosave(JsonElement root)
    {
        List<PaperRow>? rows = ReadPaperRows(root);
        if (rows == null)
        {
            return;
        }

        AppSettings settings = PaperIo.LoadSettings(AppPaths.SettingsFile);
        string? bound = settings.LastFilePath;
        bool useBound = !string.IsNullOrWhiteSpace(bound) && File.Exists(bound);

        string target = useBound ? bound! : AppPaths.AutosaveFile;
        bool isAuto = !useBound;

        string? error = PaperIo.SavePaper(target, rows);
        if (error != null)
        {
            Logger.Error($"自动保存失败: {target} | {error}");
            Send(new { type = "autosaved", ok = false, error });
            return;
        }

        Logger.Info($"自动保存完成: {target}（{rows.Count} 行）");
        Send(new
        {
            type = "autosaved",
            ok = true,
            path = target,
            auto = isAuto,
            at = DateTime.Now.ToString("HH:mm")
        });
    }

    /// <summary>
    /// 启动恢复：上次绑定文件 → autosave.json → 空白新稿纸。
    /// </summary>
    private void HandleGetPaper(JsonElement root)
    {
        AppSettings settings = PaperIo.LoadSettings(AppPaths.SettingsFile);
        string? last = settings.LastFilePath;

        if (!string.IsNullOrWhiteSpace(last) && File.Exists(last))
        {
            var (rows, error, warning) = PaperIo.LoadPaper(last);
            if (rows != null)
            {
                if (!string.IsNullOrEmpty(warning))
                {
                    Logger.Info($"加载稿纸提示: {warning}");
                }

                Logger.Info($"加载稿纸: {last}（{rows.Count} 行）");
                Send(new { type = "paper", rows, path = last, fromAuto = false, isNew = false });
                return;
            }

            Logger.Error($"加载绑定稿纸失败: {last} | {error}");
        }

        if (File.Exists(AppPaths.AutosaveFile))
        {
            var (rows, error, warning) = PaperIo.LoadPaper(AppPaths.AutosaveFile);
            if (rows != null)
            {
                if (!string.IsNullOrEmpty(warning))
                {
                    Logger.Info($"加载自动保存提示: {warning}");
                }

                Logger.Info($"恢复自动保存: {AppPaths.AutosaveFile}（{rows.Count} 行）");
                Send(new { type = "paper", rows, path = (string?)null, fromAuto = true, isNew = false });
                return;
            }

            Logger.Error($"读取自动保存失败: {AppPaths.AutosaveFile} | {error}");
        }

        Logger.Info("无历史稿纸，新建空白稿纸");
        var newRows = new List<PaperRow> { new() };
        Send(new { type = "paper", rows = newRows, path = (string?)null, fromAuto = false, isNew = true });
    }

    /// <summary>
    /// 读取应用级设置：angleMode / useGrouping / globalHotkey（空串表示未设置）/ menuOrder
    /// 取自 settings.json；startOnBoot 以注册表实际状态为准（不在 settings.json 中保存）。
    /// hotkeyError 非 null 时表示全局快捷键当前注册失败（设置面板据此提示）。
    /// menuOrder 为 null 表示配置中无该字段（界面按默认顺序渲染）。
    /// </summary>
    private void HandleGetSettings(JsonElement root)
    {
        AppSettings settings = PaperIo.LoadSettings(AppPaths.SettingsFile);

        bool startOnBoot = AutoStart.IsEnabled(out string? bootError);
        if (bootError != null)
        {
            Logger.Error("读取开机自启动状态失败: " + bootError);
        }

        Send(new
        {
            type = "settings",
            angleMode = settings.AngleMode,
            useGrouping = settings.UseGrouping,
            startOnBoot,
            globalHotkey = settings.GlobalHotkey,
            hotkeyError = _form.HotkeyError,
            menuOrder = ReadMenuOrder()
        });
    }

    /// <summary>
    /// 合并写入应用级设置，并回包最终值。
    /// · angleMode / useGrouping / globalHotkey（唯一取值，空串=未设置）/ menuOrder
    ///   （运算菜单顺序，稳定 id 数组）存 settings.json；
    /// · startOnBoot 写 / 删 HKCU 注册项（**不**存 settings.json，避免与实际不一致），
    ///   回包一律以注册表实际状态为准；失败时额外回 error，不静默。
    /// </summary>
    private void HandleSetSettings(JsonElement root)
    {
        AppSettings settings = PaperIo.LoadSettings(AppPaths.SettingsFile);

        // 本次是否改动了会影响热键注册的项（用于决定是否重新注册）
        bool inputChanged = false;

        if (root.TryGetProperty("angleMode", out JsonElement angleElement))
        {
            if (angleElement.ValueKind != JsonValueKind.String)
            {
                Logger.Error("setSettings 参数类型错误: angleMode");
                SendError("参数类型错误: angleMode");
                return;
            }

            string angle = angleElement.GetString() ?? string.Empty;
            if (angle != "deg" && angle != "rad")
            {
                Logger.Error($"setSettings 参数取值非法: angleMode={angle}");
                SendError("参数取值非法: angleMode");
                return;
            }

            settings.AngleMode = angle;
        }

        if (root.TryGetProperty("useGrouping", out JsonElement groupingElement))
        {
            if (groupingElement.ValueKind != JsonValueKind.True &&
                groupingElement.ValueKind != JsonValueKind.False)
            {
                Logger.Error("setSettings 参数类型错误: useGrouping");
                SendError("参数类型错误: useGrouping");
                return;
            }

            settings.UseGrouping = groupingElement.GetBoolean();
        }

        // 全局快捷键：唯一取值（组合键 / "双击 XXX" / 空串=未设置）。
        // 解析失败不在此处拒绝，交由注册阶段以 hotkeyError 告知页面。
        if (root.TryGetProperty("globalHotkey", out JsonElement hotkeyElement))
        {
            if (hotkeyElement.ValueKind != JsonValueKind.String)
            {
                Logger.Error("setSettings 参数类型错误: globalHotkey");
                SendError("参数类型错误: globalHotkey");
                return;
            }

            settings.GlobalHotkey = hotkeyElement.GetString() ?? string.Empty;
            inputChanged = true;
        }

        // 开机自启动：此处只校验类型，稍后单独落到注册表
        bool? bootRequest = null;
        if (root.TryGetProperty("startOnBoot", out JsonElement bootElement))
        {
            if (bootElement.ValueKind != JsonValueKind.True &&
                bootElement.ValueKind != JsonValueKind.False)
            {
                Logger.Error("setSettings 参数类型错误: startOnBoot");
                SendError("参数类型错误: startOnBoot");
                return;
            }

            bootRequest = bootElement.GetBoolean();
        }

        // 运算菜单顺序：类型不对（非数组 / 含非字符串项）直接拒绝；缺省表示不改动
        List<string>? menuOrder = null;
        if (root.TryGetProperty(MenuOrderProperty, out JsonElement menuElement))
        {
            menuOrder = ParseMenuOrder(menuElement, out string? menuError);
            if (menuOrder == null)
            {
                Logger.Error("setSettings " + menuError);
                SendError(menuError ?? "参数类型错误: menuOrder");
                return;
            }
        }

        // 本次生效的顺序：请求里带则用请求值，否则沿用文件中已有的（避免被覆盖丢失）
        List<string>? effectiveOrder = menuOrder ?? ReadMenuOrder();

        string? error = SaveSettingsWithMenuOrder(settings, effectiveOrder);
        if (error != null)
        {
            Logger.Error("保存设置失败: " + error);
            SendError("保存设置失败");
            return;
        }

        // 全局快捷键 / 双击键：先注销再按新设置注册
        if (inputChanged)
        {
            _form.ApplyInputSettings(settings);
        }

        // 开机自启动：写 / 删注册项；失败原因记日志并回 error
        string? bootError = null;
        if (bootRequest.HasValue)
        {
            bootError = AutoStart.SetEnabled(bootRequest.Value);
        }

        // 无论本次是否修改 startOnBoot，回包都以注册表实际状态为准
        bool startOnBoot = AutoStart.IsEnabled(out string? bootReadError);
        if (bootReadError != null)
        {
            Logger.Error("读取开机自启动状态失败: " + bootReadError);
        }

        Logger.Info(
            $"设置已更新: angleMode={settings.AngleMode}, useGrouping={settings.UseGrouping}, "
            + $"startOnBoot={startOnBoot}, globalHotkey={settings.GlobalHotkey}, "
            + $"menuOrder={DescribeMenuOrder(effectiveOrder)}"
            + (bootError != null ? $" | 开机自启动设置失败: {bootError}" : string.Empty));

        // 先回发实际状态（客户端据此回显；注册表写入被回滚时开关会跟着弹回）
        Send(new
        {
            type = "settings",
            angleMode = settings.AngleMode,
            useGrouping = settings.UseGrouping,
            startOnBoot,
            globalHotkey = settings.GlobalHotkey,
            hotkeyError = _form.HotkeyError,
            menuOrder = effectiveOrder
        });

        // 再单独回 error，避免静默失败
        if (bootError != null)
        {
            Logger.Error("开机自启动设置失败: " + bootError);
            SendError("开机自启动设置失败：" + bootError);
        }
    }

    /// <summary>把文本写入系统剪贴板（失败重试一次），并回包 {type:'copied'}。</summary>
    private void HandleCopyToClipboard(JsonElement root)
    {
        if (!root.TryGetProperty("text", out JsonElement textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            Logger.Error("copyToClipboard 参数缺失或类型错误: text");
            Send(new { type = "copied", ok = false, error = "参数缺失或类型错误: text" });
            return;
        }

        string text = textElement.GetString() ?? string.Empty;
        string? error = SetClipboardText(text);
        if (error != null)
        {
            Logger.Error("写入剪贴板失败: " + error);
            Send(new { type = "copied", ok = false, error });
            return;
        }

        Logger.Info($"已复制到剪贴板（{text.Length} 字符）");
        Send(new { type = "copied", ok = true, text });
    }

    /// <summary>
    /// 写剪贴板：Clipboard 偶发被其它进程占用，失败时等待后重试一次
    /// （第二次改用 SetDataObject(text, copy: true) 以延长数据存活）。
    /// </summary>
    private static string? SetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return null;
        }
        catch (Exception first)
        {
            try
            {
                Thread.Sleep(80);
                Clipboard.SetDataObject(text, copy: true);
                return null;
            }
            catch (Exception second)
            {
                return $"写入剪贴板失败：{second.Message}（首次失败：{first.Message}）";
            }
        }
    }

    /// <summary>危险操作二次确认：原生 Yes/No 对话框。</summary>
    private void HandleConfirm(JsonElement root)
    {
        if (!root.TryGetProperty("text", out JsonElement textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            Logger.Error("confirm 参数缺失或类型错误: text");
            SendError("参数缺失或类型错误: text");
            return;
        }

        string text = textElement.GetString() ?? string.Empty;
        DialogResult result = MessageBox.Show(
            _form, text, "计算稿纸", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        bool ok = (result == DialogResult.Yes);
        Send(new { type = "confirm", ok });
    }

    // ======================================================================
    // 内部助手
    // ======================================================================

    /// <summary>
    /// 删除当前绑定稿纸：把 lastFilePath 指向的文件送入系统回收站 → 清空绑定 → 回 {type:'deleted'}。
    /// 文件已不存在也视为成功（仅解绑）；无绑定文件时明确回中文错误。
    /// </summary>
    private void HandleDeletePaper(JsonElement root)
    {
        AppSettings settings = PaperIo.LoadSettings(AppPaths.SettingsFile);
        string? path = settings.LastFilePath;

        if (string.IsNullOrWhiteSpace(path))
        {
            Logger.Info("删除稿纸：当前无绑定文件（临时稿纸）");
            Send(new { type = "deleted", ok = false, error = "当前是临时稿纸，没有可删除的文件" });
            return;
        }

        if (File.Exists(path))
        {
            string? recycleError = RecycleBin.SendToRecycleBin(path);
            if (recycleError != null)
            {
                Logger.Error($"删除稿纸失败: {path} | {recycleError}");
                Send(new { type = "deleted", ok = false, error = recycleError });
                return;
            }

            Logger.Info($"稿纸已送入回收站: {path}");
        }
        else
        {
            Logger.Info($"删除稿纸：文件已不存在，视为成功并解绑: {path}");
        }

        ClearLastFilePath();
        Send(new { type = "deleted", ok = true, path });
    }

    /// <summary>解绑当前文件（只清空 lastFilePath，不删除磁盘文件）。</summary>
    private void HandleUnbindFile(JsonElement root)
    {
        AppSettings settings = PaperIo.LoadSettings(AppPaths.SettingsFile);
        string? path = settings.LastFilePath;

        ClearLastFilePath();
        Logger.Info($"已解绑稿纸文件: {path ?? "(无)"}");
        Send(new { type = "unbound", ok = true });
    }

    /// <summary>把 settings.lastFilePath 置 null 并保存设置（保留 menuOrder）。</summary>
    private static void ClearLastFilePath()
    {
        AppSettings settings = PaperIo.LoadSettings(AppPaths.SettingsFile);
        settings.LastFilePath = null;

        string? error = SaveSettingsWithMenuOrder(settings, ReadMenuOrder());
        if (error != null)
        {
            Logger.Error("清除 lastFilePath 失败: " + error);
        }
    }

    /// <summary>把当前绑定文件写入 settings.lastFilePath（此后自动保存的目标）。</summary>
    private static void BindLastFile(string path)
    {
        AppSettings settings = PaperIo.LoadSettings(AppPaths.SettingsFile);
        settings.LastFilePath = path;

        string? error = SaveSettingsWithMenuOrder(settings, ReadMenuOrder());
        if (error != null)
        {
            Logger.Error("写入设置失败（lastFilePath）: " + error);
            return;
        }

        Logger.Info($"已绑定稿纸文件: {path}");
    }

    /// <summary>
    /// 从消息里取出 paper.rows；缺失或类型不对时记日志 + 回 error 并返回 null。
    /// 单个行元素不是对象时按空行处理（页面自身永远发送对象）。
    /// </summary>
    private List<PaperRow>? ReadPaperRows(JsonElement root)
    {
        if (!root.TryGetProperty("paper", out JsonElement paper) ||
            paper.ValueKind != JsonValueKind.Object ||
            !paper.TryGetProperty("rows", out JsonElement rowsElement) ||
            rowsElement.ValueKind != JsonValueKind.Array)
        {
            Logger.Error("命令参数缺失或类型错误: paper.rows");
            SendError("参数缺失或类型错误: paper.rows");
            return null;
        }

        var rows = new List<PaperRow>();
        foreach (JsonElement item in rowsElement.EnumerateArray())
        {
            // 非对象元素按空行处理，避免 TryGetProperty 在非对象上抛异常
            rows.Add(item.ValueKind == JsonValueKind.Object
                ? new PaperRow { Expr = GetString(item, "expr"), Note = GetString(item, "note") }
                : new PaperRow());
        }

        return rows;
    }

    // ======================================================================
    // 运算菜单顺序（settings.json 的 menuOrder 字段）
    // ----------------------------------------------------------------------
    // AppSettings 只覆盖角度制 / 千分位 / 绑定路径 / 全局快捷键，menuOrder 以
    // 「原始 JSON 合并」的方式读写：先由 PaperIo.SaveSettings 写出其余字段
    // （其序列化会覆盖整份文件），再把 menuOrder 合并进同一份 settings.json，
    // 从而在不改动 PaperIo 的前提下与既有设置共存、且始终随设置一同存续。
    // 读取/写入都做基本校验：必须是字符串数组、去空项、去重（保留首次出现）。
    // 字段缺失或非法时返回 null，由界面按默认顺序渲染（向后兼容老配置）。
    // ======================================================================

    /// <summary>settings.json 中运算菜单顺序的字段名。</summary>
    private const string MenuOrderProperty = "menuOrder";

    /// <summary>
    /// 合并写 menuOrder 专用的 JSON 选项（缩进 + 中文不转义 + 显式反射解析器）。
    /// · 不复用 PaperIo.JsonOptions：避免与其它设置写入相互影响；
    /// · 必须显式指定 TypeInfoResolver —— JsonNode.ToJsonString 会按节点运行时类型
    ///   取 TypeInfo，在未挂解析器的 JsonSerializerOptions 上会直接抛
    ///   「must specify a TypeInfoResolver setting before being marked as read-only」
    ///   （实测：不指定解析器，首次与后续调用都失败）。
    /// </summary>
    private static readonly JsonSerializerOptions MenuOrderJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>从 settings.json 读取 menuOrder；字段缺失 / 非法时返回 null（界面用默认顺序）。</summary>
    private static List<string>? ReadMenuOrder()
    {
        string path = AppPaths.SettingsFile;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(MenuOrderProperty, out JsonElement element) ||
                element.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var raw = new List<string>();
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;                       // 非字符串项忽略
                }

                raw.Add(item.GetString() ?? string.Empty);
            }

            List<string> order = NormalizeMenuOrder(raw);
            return order.Count > 0 ? order : null;
        }
        catch (Exception ex)
        {
            Logger.Error("读取 menuOrder 失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 把 menuOrder 合并写入 settings.json（保留其余字段）。order 为 null 时不改动文件。
    /// 返回 null 表示成功，否则为中文错误信息。
    /// </summary>
    private static string? WriteMenuOrder(List<string>? order)
    {
        if (order == null)
        {
            return null;
        }

        List<string> clean = NormalizeMenuOrder(order);
        string path = AppPaths.SettingsFile;
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var array = new JsonArray();
            foreach (string id in clean)
            {
                array.Add(id);
            }

            root[MenuOrderProperty] = array;

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, root.ToJsonString(MenuOrderJson), new UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return "写入 menuOrder 失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 解析 setSettings 传入的 menuOrder：必须是字符串数组（含非字符串项视为类型错误），
    /// 去空项、去重。返回 null 时 error 为对应中文文案。
    /// </summary>
    private static List<string>? ParseMenuOrder(JsonElement element, out string? error)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            error = "参数类型错误: menuOrder";
            return null;
        }

        var raw = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = "参数类型错误: menuOrder";
                return null;
            }

            raw.Add(item.GetString() ?? string.Empty);
        }

        error = null;
        return NormalizeMenuOrder(raw);
    }

    /// <summary>
    /// 运算菜单顺序的基本校验：逐项去首尾空白、丢弃空项、去重（保留首次出现）。
    /// 读取 / 写入 / setSettings 入参三条路径统一走这里，保证落盘与内存中的顺序都干净。
    /// </summary>
    private static List<string> NormalizeMenuOrder(IEnumerable<string>? ids)
    {
        var order = new List<string>();
        if (ids == null)
        {
            return order;
        }

        foreach (string raw in ids)
        {
            string id = (raw ?? string.Empty).Trim();
            if (id.Length == 0 || order.Contains(id))
            {
                continue;
            }

            order.Add(id);
        }

        return order;
    }

    /// <summary>保存设置后再合并写回 menuOrder（PaperIo.SaveSettings 会覆盖整份文件）。</summary>
    private static string? SaveSettingsWithMenuOrder(AppSettings settings, List<string>? menuOrder)
    {
        string? error = PaperIo.SaveSettings(AppPaths.SettingsFile, settings);
        if (error != null)
        {
            return error;
        }

        return WriteMenuOrder(menuOrder);
    }

    /// <summary>menuOrder 的日志展示（数量 + 前若干项），null 显示为「(未设置)」。</summary>
    private static string DescribeMenuOrder(List<string>? order)
        => order == null ? "(未设置)" : $"({order.Count}) [{string.Join(",", order)}]";

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    private void SendJson(string json) => _form.PostToJs(json);

    private void Send(object payload) => _form.PostToJs(JsonSerializer.Serialize(payload, MessageJson));

    private void SendError(string message) => Send(new { type = "error", message });

    private static string Truncate(string text, int max = 200)
        => text.Length <= max ? text : text[..max] + "…";
}