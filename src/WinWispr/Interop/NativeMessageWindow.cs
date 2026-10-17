using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinWispr.Interop;

internal sealed class NativeMessageWindow : IDisposable
{
    private const uint ErrorClassAlreadyExists = 1410;
    private const int HwndMessage = -3;
    private readonly WindowProc _windowProc;
    private readonly Func<uint, nint, nint, nint> _onMessage;
    private readonly string _className = $"WinWispr.MessageWindow.{Guid.NewGuid():N}";
    private nint _handle;

    internal NativeMessageWindow(Func<uint, nint, nint, nint> onMessage)
    {
        _onMessage = onMessage;
        _windowProc = WindowProcedure;
        nint module = GetModuleHandle(null);
        var windowClass = new WndClass
        {
            lpfnWndProc = _windowProc,
            hInstance = module,
            lpszClassName = _className
        };

        if (RegisterClass(ref windowClass) == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            throw new InvalidOperationException("Could not register the native message window.");

        _handle = CreateWindowEx(
            0,
            _className,
            "WinWispr",
            0,
            0,
            0,
            0,
            0,
            new nint(HwndMessage),
            0,
            module,
            0);
        if (_handle == 0)
            throw new InvalidOperationException("Could not create the native message window.");
    }

    internal nint Handle => _handle;

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
            DestroyWindow(handle);
    }

    private nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam)
    {
        try
        {
            nint result = _onMessage(message, wParam, lParam);
            if (result != 0)
                return result;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"WinWispr native message handler failed: {exception}");
        }

        return DefWindowProc(handle, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint handle, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public WindowProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass([In] ref WndClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint handle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
