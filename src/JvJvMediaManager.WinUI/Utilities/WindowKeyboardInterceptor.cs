using System.Runtime.InteropServices;

namespace JvJvMediaManager.Utilities;

public sealed class WindowKeyboardInterceptor : IDisposable
{
    public const uint WmKeyDown = 0x0100;
    public const uint WmSysKeyDown = 0x0104;
    public const uint WmNcDestroy = 0x0082;

    private readonly NativeWindowSubclassProc _subclassProc;
    private readonly UIntPtr _subclassId;
    private readonly Func<IntPtr, uint, UIntPtr, IntPtr, bool> _handleMessage;
    private readonly string _logCategory;
    private IntPtr _windowHandle;
    private bool _installed;

    public WindowKeyboardInterceptor(
        UIntPtr subclassId,
        Func<IntPtr, uint, UIntPtr, IntPtr, bool> handleMessage,
        string logCategory)
    {
        _subclassId = subclassId;
        _handleMessage = handleMessage;
        _logCategory = string.IsNullOrWhiteSpace(logCategory) ? "WindowKeyboardInterceptor" : logCategory;
        _subclassProc = WindowSubclassProcImpl;
    }

    public bool IsInstalled => _installed;

    public bool Install(IntPtr windowHandle)
    {
        if (_installed)
        {
            return true;
        }

        if (windowHandle == IntPtr.Zero)
        {
            AppTraceLogger.Log(_logCategory, "Keyboard subclass skipped because window handle is missing.");
            return false;
        }

        _windowHandle = windowHandle;
        _installed = SetWindowSubclass(windowHandle, _subclassProc, _subclassId, UIntPtr.Zero);
        AppTraceLogger.Log(
            _logCategory,
            _installed
                ? $"Keyboard subclass installed. Hwnd=0x{windowHandle.ToInt64():X}."
                : $"Keyboard subclass install failed. Hwnd=0x{windowHandle.ToInt64():X}, Error={Marshal.GetLastWin32Error()}.");
        return _installed;
    }

    public void Uninstall()
    {
        if (!_installed)
        {
            return;
        }

        var handle = _windowHandle;
        if (handle != IntPtr.Zero)
        {
            try
            {
                _ = RemoveWindowSubclass(handle, _subclassProc, _subclassId);
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException(_logCategory, "RemoveWindowSubclass failed.", ex);
            }
        }

        _installed = false;
        _windowHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        Uninstall();
    }

    private IntPtr WindowSubclassProcImpl(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (_handleMessage(hWnd, msg, wParam, lParam))
        {
            return IntPtr.Zero;
        }

        if (msg == WmNcDestroy)
        {
            Uninstall();
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private delegate IntPtr NativeWindowSubclassProc(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, NativeWindowSubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, NativeWindowSubclassProc pfnSubclass, UIntPtr uIdSubclass);
}
