using System.IO;
using System.Runtime.InteropServices;

namespace VirtualTerminal.Interop;

// Keep creation, resize and disposal on the same implementation: bundled HPCONs
// must never be passed to kernel32's ClosePseudoConsole/ResizePseudoConsole.
internal static class PseudoConsoleNativeApi
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateConsole(COORD size, IntPtr input, IntPtr output, uint flags, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ResizeConsole(IntPtr handle, COORD size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void CloseConsole(IntPtr handle);

    private static readonly CreateConsole Create;
    private static readonly ResizeConsole Resize;
    private static readonly CloseConsole Close;

    static PseudoConsoleNativeApi()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "conpty");
        bool bundled = Directory.Exists(directory);
        if (bundled && (!File.Exists(Path.Combine(directory, "conpty.dll")) ||
                        !File.Exists(Path.Combine(directory, "OpenConsole.exe"))))
            throw new FileNotFoundException("The bundled ConPTY requires both conpty.dll and OpenConsole.exe.");

        // The module is retained for the process lifetime, as are its delegates.
        IntPtr library = NativeLibrary.Load(bundled
            ? Path.Combine(directory, "conpty.dll")
            : Path.Combine(Environment.SystemDirectory, "kernel32.dll"));
        string prefix = bundled ? "Conpty" : string.Empty;
        Create = Marshal.GetDelegateForFunctionPointer<CreateConsole>(NativeLibrary.GetExport(library, prefix + "CreatePseudoConsole"));
        Resize = Marshal.GetDelegateForFunctionPointer<ResizeConsole>(NativeLibrary.GetExport(library, prefix + "ResizePseudoConsole"));
        Close = Marshal.GetDelegateForFunctionPointer<CloseConsole>(NativeLibrary.GetExport(library, prefix + "ClosePseudoConsole"));
    }

    internal static int CreatePseudoConsole(COORD size, IntPtr input, IntPtr output, out IntPtr handle)
        => Create(size, input, output, 0, out handle);

    internal static int ResizePseudoConsole(IntPtr handle, COORD size) => Resize(handle, size);
    internal static void ClosePseudoConsole(IntPtr handle) => Close(handle);
}
