using System.Text;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #2219: OSC 7501 / OSC 9;4 / OSC 7 / DECSCUSR / underline styles+color.
/// Each protocol has a formatter byte-pin, a capability probe pin and an
/// emitter fallback pin. The frame tests prove the escape bytes are zero-width:
/// strip style/OSC/DECSCUSR bytes from an enabled frame and it is byte-for-byte
/// the frame a terminal without support would have received.
/// </summary>
public class CheapOscProtocolsTests
{
    private const string Esc = "\u001B";
    private const char EscChar = '\u001B';

    // ── OSC 7501 ───────────────────────────────────────────────────────────

    [Test]
    public async Task Osc7501_Formatter_ExactBytes()
    {
        string working = Encoding.ASCII.GetString(Osc7501Status.Format(ProgramStatusState.Working, "harbor"));
        await Assert.That(working).IsEqualTo($"{Esc}]7501;state=working:app=harbor{Esc}\\");
        await Assert.That(Encoding.ASCII.GetString(Osc7501Status.Clear)).IsEqualTo($"{Esc}]7501;state=clear{Esc}\\");
        await Assert.That(Osc7501Status.SupportQuery).IsEqualTo($"{Esc}]7501;?{Esc}\\");
    }

    [Test]
    public async Task Osc7501_Formatter_SanitizesApp()
    {
        string seq = Encoding.ASCII.GetString(Osc7501Status.Format(ProgramStatusState.Error, "bad;app"));
        await Assert.That(seq).IsEqualTo($"{Esc}]7501;state=error:app=bad_app{Esc}\\");
    }

    [Test]
    public async Task Osc7501_Emitter_GatedAndFallback()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);

        var off = new ProgramStatusEmitter { Enabled = false };
        off.Set(ProgramStatusState.Working);
        off.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEmpty();

        var on = new ProgramStatusEmitter { Enabled = true };
        on.Set(ProgramStatusState.Blocked, "harbor");
        on.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEqualTo($"{Esc}]7501;state=blocked:app=harbor{Esc}\\");

        // A second emit without a new state sends nothing (dirty-on-Set).
        backend.Reset();
        on.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEmpty();
    }

    // ── OSC 9;4 ────────────────────────────────────────────────────────────

    [Test]
    public async Task Osc94_Formatter_StatesAndClamp()
    {
        await Assert.That(Encoding.ASCII.GetString(Osc94Progress.Format(ProgressState.Normal, 42)))
            .IsEqualTo($"{Esc}]9;4;1;42{Esc}\\");
        await Assert.That(Encoding.ASCII.GetString(Osc94Progress.Format(ProgressState.Normal, 999)))
            .IsEqualTo($"{Esc}]9;4;1;100{Esc}\\");
        await Assert.That(Encoding.ASCII.GetString(Osc94Progress.Format(ProgressState.Indeterminate, 0)))
            .IsEqualTo($"{Esc}]9;4;3{Esc}\\");
        await Assert.That(Encoding.ASCII.GetString(Osc94Progress.Format(ProgressState.None, 0)))
            .IsEqualTo($"{Esc}]9;4;0{Esc}\\");
    }

    [Test]
    public async Task Osc94_Emitter_GatedAndFallback()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);

        var off = new ProgressEmitter { Enabled = false };
        off.Set(ProgressState.Indeterminate);
        off.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEmpty();

        var on = new ProgressEmitter { Enabled = true };
        on.Set(ProgressState.Indeterminate);
        on.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEqualTo($"{Esc}]9;4;3{Esc}\\");

        backend.Reset();
        on.Clear();
        on.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEqualTo($"{Esc}]9;4;0{Esc}\\");
    }

    // ── OSC 7 ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Osc7_Formatter_FileUriWithEscaping()
    {
        string seq = Encoding.UTF8.GetString(Osc7WorkingDirectory.Format("/tmp/opencode/a b/ütf8"));
        // Space → %20, UTF-8 'ü' → %C3%BC; '/' stays as the URI separator.
        await Assert.That(seq).IsEqualTo($"{Esc}]7;file:///tmp/opencode/a%20b/%C3%BCtf8{Esc}\\");
    }

    [Test]
    public async Task Osc7_Formatter_CannotBreakOutOfEnvelope()
    {
        string seq = Encoding.UTF8.GetString(Osc7WorkingDirectory.Format($"/tmp/{Esc}]52;c;AAAA\u0007"));
        await Assert.That(seq.Contains($"{Esc}]52")).IsFalse();
        await Assert.That(seq.Contains("\u0007")).IsFalse();
    }

    [Test]
    public async Task Osc7_Emitter_GatedFallbackAndDedupe()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);

        var off = new WorkingDirectoryEmitter { Enabled = false };
        off.Set("/tmp/x");
        off.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEmpty();

        var on = new WorkingDirectoryEmitter { Enabled = true };
        on.Set("/tmp/x");
        on.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEqualTo($"{Esc}]7;file:///tmp/x{Esc}\\");

        backend.Reset();
        on.Set("/tmp/x"); // same path → no re-emit
        on.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEmpty();
    }

    // ── DECSCUSR ───────────────────────────────────────────────────────────

    [Test]
    public async Task Decscusr_Formatter_ExactBytes()
    {
        await Assert.That(Encoding.ASCII.GetString(DecscusrCursorShape.Format(DecscusrCursorShape.Shape.SteadyBlock)))
            .IsEqualTo($"{Esc}[2q");
        await Assert.That(Encoding.ASCII.GetString(DecscusrCursorShape.Format(DecscusrCursorShape.Shape.SteadyBar)))
            .IsEqualTo($"{Esc}[6q");
    }

    [Test]
    public async Task Decscusr_Emitter_GatedFallbackAndDedupe()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);

        var off = new CursorShapeEmitter { Enabled = false };
        off.Set(DecscusrCursorShape.Shape.SteadyBlock);
        off.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEmpty();

        var on = new CursorShapeEmitter { Enabled = true };
        on.Set(DecscusrCursorShape.Shape.SteadyBlock);
        on.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEqualTo($"{Esc}[2q");

        backend.Reset();
        on.Set(DecscusrCursorShape.Shape.SteadyBlock);
        on.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEmpty();

        on.Set(DecscusrCursorShape.Shape.SteadyBar);
        on.Emit(writer);
        writer.FlushSync();
        await Assert.That(backend.Text).IsEqualTo($"{Esc}[6q");
    }

    // ── Capability probes ──────────────────────────────────────────────────

    [Test]
    public async Task Probes_OverridesAndMultiplexer()
    {
        string? None(string _) => null;
        string? Tmux(string k) => k == "TMUX" ? "/tmp/t" : null;

        await Assert.That(TerminalProtocolProbe.DetectOsc7501(None)).IsFalse();
        await Assert.That(TerminalProtocolProbe.DetectOsc94Progress(None)).IsFalse();
        await Assert.That(TerminalProtocolProbe.DetectOsc7Cwd(None)).IsFalse();
        await Assert.That(TerminalProtocolProbe.DetectDecscusr(None)).IsFalse();
        await Assert.That(TerminalProtocolProbe.DetectUnderlineStyles(None)).IsFalse();

        await Assert.That(TerminalProtocolProbe.DetectOsc7501(Tmux)).IsFalse();
        await Assert.That(TerminalProtocolProbe.DetectDecscusr(Tmux)).IsFalse();

        string? overrideOn(string k) => k is "HARBOR_OSC7501" or "HARBOR_OSC94_PROGRESS"
            or "HARBOR_OSC7_CWD" or "HARBOR_DECSCUSR" or "HARBOR_UNDERLINE_STYLES" ? "on" : null;
        await Assert.That(TerminalProtocolProbe.DetectOsc7501(overrideOn)).IsTrue();
        await Assert.That(TerminalProtocolProbe.DetectOsc94Progress(overrideOn)).IsTrue();
        await Assert.That(TerminalProtocolProbe.DetectOsc7Cwd(overrideOn)).IsTrue();
        await Assert.That(TerminalProtocolProbe.DetectDecscusr(overrideOn)).IsTrue();
        await Assert.That(TerminalProtocolProbe.DetectUnderlineStyles(overrideOn)).IsTrue();

        // An "on" override beats the multiplexer guard (same as OSC 8).
        string? tmuxButOn(string k) => k == "TMUX" ? "/tmp/t" : overrideOn(k);
        await Assert.That(TerminalProtocolProbe.DetectOsc7Cwd(tmuxButOn)).IsTrue();
    }

    // ── Underline styles / color ───────────────────────────────────────────

    [Test]
    public async Task UnderlineStyle_PacksIntoReservedBits()
    {
        var attrs = EngineCells.StyleAttr.Underline | EngineCells.UnderlineStyle.Encode(EngineCells.UnderlineKind.Curly);
        await Assert.That(EngineCells.UnderlineStyle.Decode(attrs)).IsEqualTo(EngineCells.UnderlineKind.Curly);
        await Assert.That((int)attrs).IsEqualTo((1 << 3) | (2 << 8));
        await Assert.That(EngineCells.Cell.SizeBytes).IsEqualTo(16);
    }

    [Test]
    public async Task UnderlineStyle_WriterEmitsColonAndDoubleCodes()
    {
        var (writer, backend) = MakeWriter();
        writer.BeginFrame();
        writer.SetStyle(new EngineCells.CellStyle(attrs:
            EngineCells.StyleAttr.Underline | EngineCells.UnderlineStyle.Encode(EngineCells.UnderlineKind.Curly)));
        writer.SetStyle(new EngineCells.CellStyle(attrs:
            EngineCells.StyleAttr.Underline | EngineCells.UnderlineStyle.Encode(EngineCells.UnderlineKind.Double)));
        writer.SetStyle(new EngineCells.CellStyle(attrs:
            EngineCells.StyleAttr.Underline | EngineCells.UnderlineStyle.Encode(EngineCells.UnderlineKind.Dotted)));
        writer.SetStyle(new EngineCells.CellStyle(attrs:
            EngineCells.StyleAttr.Underline | EngineCells.UnderlineStyle.Encode(EngineCells.UnderlineKind.Dashed)));
        writer.EndFrame();

        await Assert.That(backend.Text).IsEqualTo(
            $"{Esc}[0m{Esc}[4:3m{Esc}[24;21m{Esc}[24;4:4m{Esc}[24;4:5m");
    }

    [Test]
    public async Task UnderlineColor_WriterEmits58And59()
    {
        var (writer, backend) = MakeWriter();
        writer.BeginFrame();
        writer.SetStyle(
            new EngineCells.CellStyle(attrs: EngineCells.StyleAttr.Underline),
            EngineCells.PackedColor.Indexed(1));
        writer.SetStyle(new EngineCells.CellStyle(attrs: EngineCells.StyleAttr.Underline)); // default color → 59
        writer.EndFrame();

        await Assert.That(backend.Text).IsEqualTo($"{Esc}[0m{Esc}[4;58;5;1m{Esc}[59m");
    }

    [Test]
    public async Task UnderlineColor_SideTableGatedAndGeometryNeutral()
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend);
        var engine = new EngineCells.DiffEngine(4, 1);
        var next = new EngineCells.ScreenBuffer(4, 1);
        next.SetText(0, 0, "ab", EngineCells.CellStyle.Plain);

        var colors = new EngineCells.UnderlineColorTable(4, 1) { Enabled = false };
        colors.Set(0, 0, EngineCells.PackedColor.Rgb(255, 0, 0));
        writer.BeginFrame();
        engine.Flush(next, writer, underlineColors: colors);
        writer.EndFrame();
        string fallback = backend.Text;

        backend.Reset();
        colors.Enabled = true;
        var engine2 = new EngineCells.DiffEngine(4, 1);
        writer.BeginFrame();
        engine2.Flush(next, writer, underlineColors: colors);
        writer.EndFrame();
        string enabled = backend.Text;

        await Assert.That(enabled).Contains($"{Esc}[58;2;255;0;0m");
        // Strip style bytes; the cursor moves and runes must be identical.
        await Assert.That(StripStyleAndProtocol(enabled)).IsEqualTo(StripStyleAndProtocol(fallback));
    }

    // ── Whole-frame zero-width contract ────────────────────────────────────

    [Test]
    public async Task AllProtocols_EnabledFrame_StripsToClassicFrame()
    {
        string plain = Frame(enableProtocols: false);
        string full = Frame(enableProtocols: true);

        await Assert.That(full).Contains($"{Esc}]7501;state=working");
        await Assert.That(full).Contains($"{Esc}]9;4;3{Esc}\\");
        await Assert.That(full).Contains($"{Esc}]7;file:///tmp/harbor");
        await Assert.That(full).Contains($"{Esc}[2q");
        await Assert.That(full).Contains($"{Esc}[4:3m");

        // The one thing that MUST NOT change: the geometry-producing bytes
        // (sync wrapper, MoveTo, visible runes). Everything else is style or
        // zero-width protocol bytes and is stripped from both frames.
        await Assert.That(StripStyleAndProtocol(full)).IsEqualTo(StripStyleAndProtocol(plain));
    }

    private static string Frame(bool enableProtocols)
    {
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var session = new Harbor.Tui.CellForge.Streaming.ScreenSession(writer, 8, 1)
        {
            ProgramStatus = { Enabled = enableProtocols },
            Progress = { Enabled = enableProtocols },
            WorkingDirectory = { Enabled = enableProtocols },
            CursorShape = { Enabled = enableProtocols },
            UnderlineColors = { Enabled = enableProtocols },
        };
        // Exercise an extended underline style in the same frame so the
        // SGR 4:3 bytes are covered by the zero-width contract too.
        // Painter-side buffer speaks the design-system vocabulary (#436).
        session.PaintBuffer.SetText(0, 0, "hello", new CellStyle(
            attrs: StyleAttr.Underline
                | Harbor.Ui.Framework.Rendering.UnderlineStyle.Encode(Harbor.Ui.Framework.Rendering.UnderlineKind.Curly)));

        session.ProgramStatus.Set(ProgramStatusState.Working, "harbor");
        session.Progress.Set(ProgressState.Indeterminate);
        session.WorkingDirectory.Set("/tmp/harbor");
        session.CursorShape.Set(DecscusrCursorShape.Shape.SteadyBlock);

        session.BeginFrame();
        session.FlushFrame();
        return backend.Text;
    }

    private static (AnsiWriter Writer, RecordingBackend Backend) MakeWriter()
    {
        var backend = new RecordingBackend();
        return (new AnsiWriter(backend), backend);
    }

    /// <summary>
    /// Removes SGR, OSC envelopes and DECSCUSR from a frame, leaving only the
    /// bytes that can move the pen or draw a cell (CSI H, sync wrapper, runes).
    /// </summary>
    private static string StripStyleAndProtocol(string frame)
    {
        var sb = new StringBuilder(frame.Length);
        for (int i = 0; i < frame.Length;)
        {
            if (frame[i] != EscChar)
            {
                sb.Append(frame[i++]);
                continue;
            }

            if (i + 1 < frame.Length && frame[i + 1] == ']')
            {
                // Every protocol here uses the ESC \ string terminator.
                int j = i + 2;
                while (j + 1 < frame.Length && !(frame[j] == EscChar && frame[j + 1] == '\\'))
                {
                    j++;
                }

                i = j + 2 <= frame.Length ? j + 2 : frame.Length;
                continue;
            }

            if (i + 1 < frame.Length && frame[i + 1] == '[')
            {
                int j = i + 2;
                while (j < frame.Length && !char.IsLetter(frame[j]))
                {
                    j++;
                }

                if (j < frame.Length && frame[j] is 'm' or 'q')
                {
                    i = j + 1;
                    continue;
                }
            }

            sb.Append(frame[i++]);
        }

        return sb.ToString();
    }
}
