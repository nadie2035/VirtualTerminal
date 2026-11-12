using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using VirtualTerminal.Buffer;

namespace VirtualTerminal.Interop;

/// <summary>
/// Factory for creating ConPTY (<c>CreatePseudoConsole</c>) instances wired to a <see cref="TerminalScreenBuffer"/>.
/// </summary>
public static partial class PseudoConsoleFactory
{
    /// <summary>
    /// Creates a new ConPTY instance for the given buffer size and starts a child process attached to it.
    /// </summary>
    /// <param name="buffer">Terminal buffer (its dimensions are used as the initial console size).</param>
    /// <param name="processInfo">Child process configuration.</param>
    /// <returns>A <see cref="PseudoConsole"/> wrapper containing handles, pipes and the child process.</returns>
    public static PseudoConsole Start(TerminalScreenBuffer buffer, ProcessCreationInfo processInfo)
    {
        Stream writer = Win32PipeFactory.CreateChildStdInPipe(out IntPtr hStdInput);   // Con reads from
        Stream reader = Win32PipeFactory.CreateChildStdOutPipe(out IntPtr hStdOutput); // Con writes to

        IntPtr handle = IntPtr.Zero;
        try
        {
            int hResult = PseudoConsoleNativeApi.CreatePseudoConsole(new COORD((ushort)buffer.GridSize.Width, (ushort)buffer.GridSize.Height), hStdInput, hStdOutput, out handle);

            Marshal.ThrowExceptionForHR(hResult);
            if (NativeMethods.IsInvalidHandleValue(handle))
                throw new InvalidOperationException("ConPTY returned an invalid console handle.");

            Win32Process process = Win32ProcessFactory.Start(processInfo, handle);
            return new PseudoConsole(handle, process, writer, reader);
        }
        catch
        {
            reader.Dispose();
            writer.Dispose();
            if (!NativeMethods.IsInvalidHandleValue(handle))
                PseudoConsoleNativeApi.ClosePseudoConsole(handle);
            throw;
        }
        finally
        {
            // The parent process doesn't need the reading end of the input pipe; it's given to the child.
            // We close it to avoid keeping an extra handle.

            if (hStdInput != IntPtr.Zero)
                NativeMethods.CloseHandle(hStdInput);

            if (hStdOutput != IntPtr.Zero)
                NativeMethods.CloseHandle(hStdOutput);
        }
    }

    private static partial class NativeMethods
    {
        public const int S_OK = 0;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        public static bool IsInvalidHandleValue(IntPtr handle)
            => handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(IntPtr hObject);
    }
}
