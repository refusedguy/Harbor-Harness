using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// Raw mode via direct termios P/Invoke (design §5.2) — replaces the spec-07
/// `stty`-spawn approach (process spawn per toggle, coreutils dependency,
/// restore race). Classic [DllImport] with fully blittable signatures
/// (AOT-safe, zero marshalling): the design doc prefers [LibraryImport], but
/// its source generator requires AllowUnsafeBlocks=true which the repo
/// forbids project-wide — same resolution as Mcp ProcessTree.cs.
/// Flag layout targets Linux (the CE-0 host platform); BSD/macOS share the
/// core ICANON/ECHO/ISIG bits, exotic-bit tuning is a follow-up.
/// </summary>
public sealed class UnixTermiosModeController : ITerminalModeController
{
    private const int StdinFd = 0;
    private const int Tcsanow = 0;

    // input flags
    private const uint Ignbrk = 0x001;
    private const uint Brkint = 0x002;
    private const uint Parmrk = 0x008;
    private const uint Istrip = 0x020;
    private const uint Inlcr = 0x040;
    private const uint Igncr = 0x080;
    private const uint Icrnl = 0x100;
    private const uint Ixon = 0x400;
    // output flags
    private const uint Opost = 0x001;
    // local flags
    private const uint Isig = 0x001;
    private const uint Icanon = 0x002;
    private const uint Echo = 0x008;
    private const uint Echonl = 0x040;
    private const uint Iexten = 0x8000;
    // control flags
    private const uint Csize = 0x030;
    private const uint Parenb = 0x100;
    private const uint Cs8 = 0x030;

    public bool IsRaw { get; private set; }

    private Termios _original;
    private bool _hasOriginal;

    public void Enter()
    {
        if (IsRaw)
        {
            return;
        }

        var current = new Termios();
        if (tcgetattr(StdinFd, ref current) != 0)
        {
            throw new InvalidOperationException("tcgetattr(stdin) failed — stdin is not a terminal.");
        }

        if (!_hasOriginal)
        {
            _original = current;
            _hasOriginal = true;
        }

        current.CIflag &= ~(Ignbrk | Brkint | Parmrk | Istrip | Inlcr | Igncr | Icrnl | Ixon);
        current.COflag &= ~Opost;
        current.CLflag &= ~(Echo | Echonl | Icanon | Isig | Iexten);
        current.CCflag &= ~(Csize | Parenb);
        current.CCflag |= Cs8;
        // VMIN=1 / VTIME=0 — classic blocking cbreak read: the input reader
        // thread is long-lived and dedicated, so a read must WAIT for bytes.
        // (CE-5 PTY-suite finding: VMIN=0 makes read(2) return 0 whenever no
        // bytes are pending, which any plain-stream reader interprets as EOF
        // and tears the whole REPL down on a real terminal.)
        current.ControlCharacters[6] = 1; // VMIN  = 1 → blocking read
        current.ControlCharacters[5] = 0; // VTIME = 0

        if (tcsetattr(StdinFd, Tcsanow, ref current) != 0)
        {
            throw new InvalidOperationException("tcsetattr(stdin, raw) failed.");
        }

        IsRaw = true;
    }

    public void Restore()
    {
        if (!IsRaw || !_hasOriginal)
        {
            return;
        }

        var restored = _original;
        _ = tcsetattr(StdinFd, Tcsanow, ref restored);
        IsRaw = false;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fileDescriptor, ref Termios termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fileDescriptor, int optionalActions, ref Termios termios);

    [StructLayout(LayoutKind.Sequential)]
    private struct Termios
    {
        public uint CIflag;
        public uint COflag;
        public uint CCflag;
        public uint CLflag;
        public byte CLine;
        public ControlCharacters ControlCharacters;
        // kernel termios (asm-generic/termbits.h) is 60 bytes on Linux x64:
        // 4×tcflag_t + cc_t c_line + cc_t c_cc[32] + speed_t c_ispeed/c_ospeed.
        // Missing trailing fields let tcgetattr write 11 bytes past the struct
        // → stack corruption → AccessViolationException (CE-4 live-run bug).
        public uint CIspeed;
        public uint COspeed;
    }

    [InlineArray(32)]
    private struct ControlCharacters
    {
        private byte _element0;
    }
}
