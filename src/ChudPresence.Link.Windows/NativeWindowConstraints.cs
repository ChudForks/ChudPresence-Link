using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ChudPresence.Link.Windows;

// WinUI Window has no MinWidth/MinHeight. Constrain the native resize frame in DIPs.
internal sealed class NativeWindowConstraints : IDisposable
{
    private const nuint SubclassId = 0x434C;
    private readonly nint window;
    private readonly int minimumWidth;
    private readonly int minimumHeight;
    private readonly SubclassProcedure procedure;

    public NativeWindowConstraints(nint window, int minimumWidth, int minimumHeight)
    {
        this.window = window;
        this.minimumWidth = minimumWidth;
        this.minimumHeight = minimumHeight;
        procedure = ProcessMessage;
        if (!SetWindowSubclass(window, procedure, SubclassId, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint ProcessMessage(nint handle, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        var result = DefSubclassProc(handle, message, wParam, lParam);
        if (message == 0x24) // WM_GETMINMAXINFO
        {
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var scale = GetDpiForWindow(handle) / 96d;
            limits.MinimumTrackSize.X = (int)Math.Ceiling(minimumWidth * scale);
            limits.MinimumTrackSize.Y = (int)Math.Ceiling(minimumHeight * scale);
            Marshal.StructureToPtr(limits, lParam, false);
        }
        return result;
    }

    public void Dispose()
    {
        RemoveWindowSubclass(window, procedure, SubclassId);
        GC.KeepAlive(procedure);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaximumSize;
        public Point MaximumPosition;
        public Point MinimumTrackSize;
        public Point MaximumTrackSize;
    }

    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
