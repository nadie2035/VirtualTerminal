using System.Diagnostics;
using System.IO;
using System.Text;
using VirtualTerminal.Extensions;
using VirtualTerminal.Interop;
using VirtualTerminal.Session;

namespace VirtualTerminal;

/// <summary>
/// Handler for <see cref="CommandLineSession.RawOutputReceived"/>.
/// </summary>
/// <param name="data">Bytes just read from the ConPTY.</param>
public delegate void RawOutputHandler(ReadOnlySpan<byte> data);

/// <summary>
/// A <see cref="TerminalSession"/> implementation backed by Windows ConPTY (pseudo console).
/// It starts a child process attached to a pseudoconsole and forwards IO between the process and
/// the session <see cref="TerminalSession.Buffer"/>.
/// </summary>
public sealed class CommandLineSession : TerminalSession
{
    private CancellationTokenSource readLoopToken;
    private PseudoConsole pseudoConsole;

    /// <inheritdoc />
    public override string Title
    {
        get => base.Title;
        set => base.Title = value;
    }

    /// <summary>
    /// Gets the underlying ConPTY wrapper (process + pipes).
    /// </summary>
    public PseudoConsole PseudoConsole => pseudoConsole;

    /// <summary>
    /// Raised with every chunk of bytes read from the ConPTY, right after it was fed to the decoder.
    /// It runs on the read thread while the decoder lock (<c>Decoder.Buffer.SyncRoot</c>) is held, so a
    /// subscriber that takes a screen snapshot under that same lock sees snapshot and stream in order.
    /// Handlers must be quick and must not keep the span.
    /// </summary>
    public event RawOutputHandler? RawOutputReceived;

    /// <summary>
    /// Raised when the grid is resized (columns, rows), under the decoder lock and before the ConPTY is told,
    /// so it is ordered before the repaint output the new size provokes.
    /// </summary>
    public event Action<ushort, ushort>? Resized;

    /// <summary>
    /// Gets the OS process id (PID) of the associated child process, or 0 if not started.
    /// </summary>
    public int ProcessId => pseudoConsole?.ProcessId ?? 0;

    /// <summary>
    /// Starts a ConPTY session running the provided command line.
    /// </summary>
    /// <param name="process"></param>
    public CommandLineSession(ProcessCreationInfo process) : base()
    {
        Title = Path.GetFileNameWithoutExtension(process.CommandLine ?? process.ApplicationName ?? string.Empty).ToLower();

        readLoopToken = new CancellationTokenSource();
        pseudoConsole = PseudoConsoleFactory.Start(Buffer, process);

        _ = Task.Factory.StartNew(ReadOutputLoop, TaskCreationOptions.LongRunning);
    }
    /// <summary>
    /// Starts a ConPTY session running the provided command line.
    /// </summary>
    /// <param name="application">Command line to start (for example, <c>cmd.exe</c> or PowerShell).</param>
    public CommandLineSession(string application) : base()
    {
        Title = Path.GetFileNameWithoutExtension(application).ToLower();

        readLoopToken = new CancellationTokenSource();
        pseudoConsole = PseudoConsoleFactory.Start(Buffer, new ProcessCreationInfo()
        {
            CommandLine = application,
            CurrentDirectory = Path.GetDirectoryName(application)
        });

        _ = Task.Factory.StartNew(ReadOutputLoop, TaskCreationOptions.LongRunning);
    }

    /// <summary>
    /// Starts a default ConPTY session using <c>cmd.exe</c>.
    /// </summary>
    public CommandLineSession()
        : this(@"C:\Windows\System32\cmd.exe") { }

    /// <inheritdoc />
    public override void Resize(ushort columns, ushort rows, bool pushScrollback = false)
    {
        // For ConPTY we let the pseudo-console own reflow. Resize only the local buffer
        // geometry without moving rows into scrollback; ConPTY will repaint/overwrite
        // the visible area itself, so avoid a full clear that causes blank-frame flicker.
        // The lock only covers the local grid and the notification; the ConPTY resize stays outside
        // because it can block while conhost waits for the read loop, which needs this same lock.
        lock (InternalDecoder.Buffer.SyncRoot)
        {
            base.Resize(columns, rows, pushScrollback: false);
            Resized?.Invoke(columns, rows);
        }

        PseudoConsole.Resize(columns, rows);
    }

    private async Task ReadOutputLoop()
    {
        if (PseudoConsole?.Reader == null)
            return;

        await Task.Yield();
        Span<byte> data = stackalloc byte[8192];

        try
        {
            while (readLoopToken?.IsCancellationRequested is false)
            {
                try
                {
                    int bytesRead = PseudoConsole.Reader.Read(data);
                    if (bytesRead == 0)
                        break;

                    ReadOnlySpan<byte> readed = data.Slice(0, bytesRead);
                    lock (InternalDecoder.Buffer.SyncRoot)
                    {
                        Decoder.WriteFromEncoding(InputEncoding, readed);
                        RawOutputReceived?.Invoke(readed);
                    }

                    NotifyBufferUpdated();
                }
                catch (Exception exc)
                {
                    // Log so we can see when the read/decode loop misbehaves instead of silently looping.
                    Debug.WriteLine($"[{GetType().Name}] ReadOutputLoop inner error: {exc}");
                }
            }
        }
        catch (Exception exc)
        {
            Debug.WriteLine(exc);
            throw;
        }
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> data)
    {
        if (PseudoConsole?.Writer == null)
            return;

        try
        {
            PseudoConsole.Writer.Write(data);
            NotifyBufferUpdated();
        }
        catch
        {
            // fucked up somewhere
            _ = 0xBAD + 0xC0DE;
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;

        readLoopToken?.Cancel();
        readLoopToken?.Dispose();
        readLoopToken = null!;

        pseudoConsole?.Dispose();
        pseudoConsole = null!;
    }
}
