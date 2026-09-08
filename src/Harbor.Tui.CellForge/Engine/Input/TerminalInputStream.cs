using System.Runtime.InteropServices;

namespace Harbor.Tui.CellForge.Input;

/// <summary>
///     Opens the terminal stdin stream WITHOUT .NET's console-stream
///     machinery. Found by the CE-5 PTY suite: the first read from the stream
///     returned by <see cref="Console.OpenStandardInput" /> makes the runtime
///     rewrite slave-side termios — it re-enables ISIG and zeroes iflag,
///     silently undoing <see cref="UnixTermiosModeController.Enter"/> raw
///     mode. With ISIG back on, Ctrl+C becomes SIGINT and kills the process
///     instead of arriving as the 0x03 byte the ChatAction path expects.
///     Reading fd 0 through a plain <c>FileStream</c>/<c>SafeFileHandle</c>
///     bypasses that rewrite entirely (verified empirically on glibc 2.44);
///     BCL-only, AOT-safe, no P/Invoke.
/// </summary>
public static class TerminalInputStream
{
    /// <summary>Stdin as a non-owning stream over file descriptor 0.</summary>
    public static Stream Open()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                return new FileStream(
                    new Microsoft.Win32.SafeHandles.SafeFileHandle(0, ownsHandle: false),
                    FileAccess.Read,
                    bufferSize: 4096,
                    isAsync: false);
            }
            catch (Exception ex)
            {
                // Fall back below — e.g., exotic sandbox denying handle wrap.
                _ = ex;
            }
        }

        return Console.OpenStandardInput();
    }
}
