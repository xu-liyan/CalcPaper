using System.Runtime.InteropServices;

namespace CalcPaper;

/// <summary>
/// Windows Shell 集成：单实例、跨实例命令广播、任务栏跳转列表任务项。
///
/// ── 单实例 ────────────────────────────────────────────────────────────
/// 命名互斥体 Local\CalcPaper_SingleInstance：只要首实例进程持有句柄，互斥体对象就存在。
/// 后续实例发现对象已存在，就把命令通过
///   PostMessage(HWND_BROADCAST, RegisterWindowMessage("CalcPaper.Command"), cmd, 0)
/// 广播给已有实例，然后自身退出（cmd 1 = 唤醒窗口，2 = 完全退出）。
///
/// ── 跳转列表 ──────────────────────────────────────────────────────────
/// 手写 COM 互操作（ICustomDestinationList + IShellLinkW + IPropertyStore +
/// IObjectCollection），避免引入 WPF 依赖。未显式设置 AppUserModelID，
/// 沿用系统为进程分配的默认 AUMID，与任务栏按钮的身份保持一致。
/// </summary>
internal static class ShellIntegration
{
    /// <summary>广播命令：唤醒窗口。</summary>
    public const int CommandActivate = 1;

    /// <summary>广播命令：完全退出。</summary>
    public const int CommandExit = 2;

    /// <summary>启动参数：带此参数启动时请求已有实例完全退出（无实例则直接退出）。</summary>
    public const string ExitArgument = "--exit";

    /// <summary>
    /// 启动参数：开机自启动（后台静默启动）。
    /// 命中时主窗口**完全静默**——不显示窗口、不进任务栏、不播放启动动画，
    /// 仅保留托盘图标与全局快捷键，等待快捷键 / 单击托盘唤醒；手动双击（无此参数）仍正常显示 GUI。
    /// 该参数同时由 <see cref="AutoStart"/> 写入注册表 Run 项。
    /// </summary>
    public const string AutoStartArgument = "--autostart";

    private const int HWND_BROADCAST = 0xFFFF;

    /// <summary>自定义广播消息（注册消息，全局唯一）。</summary>
    public static readonly int CommandMessage = (int)RegisterWindowMessage("CalcPaper.Command");

    private static Mutex? _instanceMutex;

    /// <summary>取得单实例所有权。返回 false 表示已有实例在运行（调用方应广播命令后退出）。</summary>
    public static bool TryAcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: false, @"Local\CalcPaper_SingleInstance", out bool createdNew);
            if (createdNew)
            {
                return true;
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        catch (Exception ex)
        {
            // 无法判定时按首实例处理，避免程序完全无法启动
            Logger.Error("创建单实例互斥体失败: " + ex.Message);
            return true;
        }
    }

    /// <summary>把命令广播给已有实例（幂等；接收方在自己的 WndProc 里处理注册消息）。</summary>
    public static void SignalExistingInstance(int command)
    {
        try
        {
            _ = PostMessage((IntPtr)HWND_BROADCAST, CommandMessage, (IntPtr)command, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.Error("向已有实例发送命令失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 为任务栏图标的右键菜单创建「完全退出」任务项（JumpTask，参数 --exit）。
    /// 成功返回 null，失败返回中文原因（绝不抛异常——跳转列表失败不应影响主程序）。
    /// </summary>
    public static string? SetupJumpList()
    {
        try
        {
            string exe = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrEmpty(exe))
            {
                return "无法确定程序路径，未创建跳转列表任务";
            }

            var list = (ICustomDestinationList)new CDestinationList();

            // BeginList 返回的“已移除目标”此处无需回填
            Guid iidObjectArray = typeof(IObjectArray).GUID;
            list.BeginList(out _, ref iidObjectArray, out object _);

            var link = (IShellLinkW)new CShellLink();
            link.SetPath(exe);
            link.SetArguments(ExitArgument);
            link.SetIconLocation(exe, 0);
            link.SetDescription("完全退出计算稿纸");

            // 任务项显示名取自 PKEY_Title
            var store = (IPropertyStore)link;
            var title = PKEY_Title;
            var value = new PROPVARIANT
            {
                vt = VT_LPWSTR,
                p = Marshal.StringToCoTaskMemUni("完全退出")
            };
            try
            {
                store.SetValue(ref title, ref value);
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.p);
            }

            store.Commit();

            var collection = (IObjectCollection)new CEnumerableObjectCollection();
            IntPtr unknown = Marshal.GetIUnknownForObject(link);
            try
            {
                collection.AddObject(unknown);
            }
            finally
            {
                Marshal.Release(unknown);
            }

            list.AddUserTasks((IObjectArray)(object)collection);
            list.CommitList();

            return null;
        }
        catch (Exception ex)
        {
            return "创建跳转列表任务失败：" + ex.Message;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // ======================================================================
    // COM 互操作（vtable 顺序必须与原生接口严格一致）
    // ======================================================================

    private const ushort VT_LPWSTR = 31;

    private static readonly PROPERTYKEY PKEY_Title = new()
    {
        fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
        pid = 2   // PIDSI_TITLE
    };

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>PROPVARIANT：x64 下 24 字节、x86 下 16 字节（仅使用 VT_LPWSTR 分支）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public IntPtr padding;
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint pcObjects);

        void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        // ⚠ 这里刻意把 IObjectArray 的成员平铺进来、**不**使用接口继承：
        // .NET 对 ComImport 接口继承的 vtable 槽位计算曾导致进程内 coreclr 访问冲突
        // (0xc0000005)，平铺后槽位与原生 IDL 完全一致。
        // IObjectArray 部分（槽位 3、4）
        void GetCount(out uint pcObjects);

        void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        // IObjectCollection 部分（槽位 5 起）
        void AddObject(IntPtr pvObject);

        void AddFromArray(IObjectArray poaSource);

        void RemoveObjectAt(uint uiIndex);

        void Clear();
    }

    [ComImport]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);

        void BeginList(out uint pcMinSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);

        void AppendKnownCategory(int category);

        void AddUserTasks(IObjectArray poa);

        void CommitList();

        [PreserveSig]
        int GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);

        void AbortList();
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);

        void GetAt(uint iProp, out PROPERTYKEY pkey);

        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);

        void Commit();
    }

    /// <summary>IShellLinkW：成员顺序即 vtable 顺序，不可调整（未使用的成员也不会被调用）。</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out ushort pwHotkey);

        void SetHotkey(ushort wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    private class CDestinationList
    {
    }

    [ComImport]
    [Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    private class CEnumerableObjectCollection
    {
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink
    {
    }
}