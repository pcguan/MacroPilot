using System.Runtime.InteropServices;
using MacroPilot.Input;

// Blank topmost windows receive drag input instead of the user's desktop/documents.
internal sealed class ProbeDesktop : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public nint Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? Menu; public string Name;
    }
    private delegate nint WindowProcedure(nint hwnd, uint message, nuint wparam, nint lparam);
    private static readonly WindowProcedure Procedure = (hwnd, message, wparam, lparam) =>
        message == 0x84 ? 1 : DefWindowProc(hwnd, message, wparam, lparam); // HTCLIENT, never click-through.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint GetSysColorBrush(int color);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public nint Window; public uint Id; public nuint WParam; public nint LParam;
        public uint Time; public int X, Y; public uint Private;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint hwnd, uint min, uint max, uint flags);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(long point);
    private readonly List<nint> _windows = new();
    public ProbeDesktop(IEnumerable<ScreenInfo.Monitor> screens)
    {
        try
        {
            var cls = new WindowClass { Name = "MacroPilotProbeSurface", Instance = GetModuleHandle(null),
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Background = GetSysColorBrush(5) };
            if (RegisterClass(ref cls) == 0) throw new InvalidOperationException("Cannot register isolated drag surface.");
            foreach (var m in screens)
            {
                var window = CreateWindowEx(0x88, cls.Name, "MacroPilot mouse validation - F11 stops",
                    0x90000000, m.Left, m.Top, m.Width, m.Height, 0, 0, cls.Instance, 0);
                if (window == 0) throw new InvalidOperationException("Cannot create isolated drag surface.");
                _windows.Add(window);
            }
            Pump();
        }
        catch { Dispose(); throw; }
    }
    public bool OwnsPoint(int x, int y) => _windows.Contains(WindowFromPoint(unchecked((long)(uint)x | ((long)y << 32))));
    public static void Pump()
    {
        while (PeekMessage(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
    }
    public void Dispose() { foreach (var window in _windows) DestroyWindow(window); _windows.Clear(); }
}
