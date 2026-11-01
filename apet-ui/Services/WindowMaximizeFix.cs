using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace APet.UI.Services;

/// <summary>
/// 修复无边框窗口(WindowStyle=None + WindowChrome)最大化时铺满整个显示器、
/// 底部伸进任务栏区域被任务栏遮挡的问题:挂钩 WM_GETMINMAXINFO,
/// 把最大化目标尺寸/位置钳制到所在显示器的工作区(即去掉任务栏后的区域)。
/// </summary>
public static class WindowMaximizeFix
{
    /// <summary>窗口创建后调用一次(SourceInitialized 时挂上消息钩子)。</summary>
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var source = (HwndSource?)PresentationSource.FromVisual(window);
            source?.AddHook(Hook);
        };
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO)
            return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return IntPtr.Zero;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(monitor, ref info))
            return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition = new POINT(info.rcWork.left - info.rcMonitor.left,
                                      info.rcWork.top - info.rcMonitor.top);
        mmi.ptMaxSize = new POINT(info.rcWork.right - info.rcWork.left,
                                  info.rcWork.bottom - info.rcWork.top);

        // 最小尺寸:无边框窗口的原生拖拽缩放不理会 WPF 的 MinWidth/MinHeight
        // (WindowChrome 接管后 WPF 不再下发默认的 WM_GETMINMAXINFO 约束),
        // 需在此显式给 ptMinTrackSize(物理像素;MinWidth/MinHeight 为 DIP,按 DPI 换算)。
        // 未设最小值(MinWidth=0)时保持 0,行为与从前一致。
        if (HwndSource.FromHwnd(hwnd)?.RootVisual is Window w && (w.MinWidth > 0 || w.MinHeight > 0))
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
            mmi.ptMinTrackSize = new POINT((int)Math.Ceiling(w.MinWidth * dpi.DpiScaleX),
                                           (int)Math.Ceiling(w.MinHeight * dpi.DpiScaleY));
        }

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;

        public POINT(int x, int y)
        {
            this.x = x;
            this.y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
