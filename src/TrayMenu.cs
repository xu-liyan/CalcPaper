using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CalcPaper;

/// <summary>
/// 托盘图标与右键菜单：显示主窗口 / 完全退出；左键单击即显示主窗口（双击同样有效）。
/// 菜单为扁平风格：白底、无渐变、无左侧图标列留白、无边框条纹、圆角选中态（半径 6px）；
/// 菜单整体也做圆角（半径 8px，Region 裁剪），并保留系统投影。
/// 键盘可达性（上/下键、回车、Esc）由 ToolStrip 默认行为保证。
/// 退出时必须 Dispose（否则任务栏区域会残留幽灵图标）。
///
/// 已知边界：任务栏图标右键的 Windows 原生跳转列表「完全退出」由系统绘制，无法自定义样式。
/// </summary>
internal sealed class TrayMenu : IDisposable
{
    /// <summary>菜单整体圆角半径（px）。</summary>
    private const int MenuCornerRadius = 8;

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;

    public TrayMenu(Icon? icon, Action showWindow, Action exitApplication)
    {
        _menu = new ContextMenuStrip
        {
            Renderer = new FlatMenuRenderer(),
            ShowImageMargin = false,      // 去掉左侧图标列留白
            ShowCheckMargin = false,
            DropShadowEnabled = true,     // 保留系统投影
            BackColor = Color.White,
            ForeColor = FlatMenuRenderer.TextColor,
            Font = new Font("Microsoft YaHei UI", 9f),
            AutoSize = true
        };

        // 项目高度约 32px（9pt 文本 + 上下各 8px 内边距）
        var showItem = new ToolStripMenuItem("显示主窗口")
        {
            Padding = new Padding(14, 8, 14, 8)
        };
        showItem.Click += (_, _) => showWindow();

        var exitItem = new ToolStripMenuItem("完全退出")
        {
            Padding = new Padding(14, 8, 14, 8)
        };
        exitItem.Click += (_, _) => exitApplication();

        _menu.Items.Add(showItem);
        _menu.Items.Add(exitItem);

        // 弹出前给菜单整体套圆角 Region（此时尺寸已确定）
        _menu.Opening += (_, _) => ApplyRoundedRegion();

        _notifyIcon = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = "计算稿纸",
            Visible = true,
            ContextMenuStrip = _menu
        };

        // 左键单击即唤醒（复用与双击、热键相同的唤醒逻辑，幂等，不会误弹菜单）
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                Logger.Info("托盘单击唤醒");
                showWindow();
            }
        };

        // 双击保持有效（与单击各自触发，唤醒逻辑幂等）
        _notifyIcon.DoubleClick += (_, _) => showWindow();
    }

    /// <summary>把菜单窗口区域裁剪为圆角矩形，保留系统投影。</summary>
    private void ApplyRoundedRegion()
    {
        int width = _menu.Width;
        int height = _menu.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, MenuCornerRadius * 2, MenuCornerRadius * 2);
        if (region == IntPtr.Zero)
        {
            return;
        }

        Region? previous = _menu.Region;
        try
        {
            // Region.FromHrgn 会复制区域，随后我们自己释放 GDI 句柄
            _menu.Region = Region.FromHrgn(region);
        }
        catch (Exception ex)
        {
            Logger.Error("托盘菜单圆角失败: " + ex.Message);
        }
        finally
        {
            _ = DeleteObject(region);
            previous?.Dispose();
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// 扁平菜单渲染器：白底、无渐变、无边框条纹、圆角选中态（半径 6px）。
    /// 悬停浅灰 #f0f1f3，按下更深 #e3e5e8。
    /// </summary>
    private sealed class FlatMenuRenderer : ToolStripRenderer
    {
        internal const int SelectRadius = 6;
        internal static readonly Color TextColor = Color.FromArgb(0x2F, 0x32, 0x37);
        private static readonly Color HoverColor = Color.FromArgb(0xF0, 0xF1, 0xF3);
        private static readonly Color PressedColor = Color.FromArgb(0xE3, 0xE5, 0xE8);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Color.White);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // 扁平：不画边框（也不画顶部条纹）
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // 扁平：不保留左侧图标列留白
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            // 扁平：分隔线不画（当前菜单也没有分隔线）
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed)
            {
                return;   // 未选中：保持白底
            }

            var rect = new Rectangle(2, 1, Math.Max(0, e.Item.Width - 4), Math.Max(0, e.Item.Height - 2));
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            Color fill = e.Item.Pressed ? PressedColor : HoverColor;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(rect, SelectRadius);
            using var brush = new SolidBrush(fill);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = TextColor;
            base.OnRenderItemText(e);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();

            if (diameter <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}