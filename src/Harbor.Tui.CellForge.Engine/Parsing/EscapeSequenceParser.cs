using System.Text;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Parsing;

/// <summary>
/// Pure byte-level terminal input state machine (design §5).
///
/// Feeds raw stdin bytes in arbitrarily split chunks and produces typed
/// <see cref="InputEvent"/>s. All cross-chunk state (half-received CSI,
/// UTF-8 tails, paste payload) lives inside the parser instance.
///
/// Allocation budget (§5.4): key/mouse/resize/capability events are enqueued
/// into a reused ring buffer — zero allocations steady-state. Char events
/// are allocation-free as well (<see cref="Rune"/> is a struct); the sole
/// heap allocation is the payload string of a completed <see cref="PasteEvent"/>.
/// </summary>
public sealed class EscapeSequenceParser
{
    private const byte Esc = 0x1B;
    private const byte Bel = 0x07;
    private const byte Can = 0x18;
    private const byte Sub = 0x1A;

    private readonly ParserOptions _options;
    private readonly byte[] _csiBuffer;

    // Event queue — reused ring buffer, grows on demand only.
    private InputEvent[] _queue = new InputEvent[64];
    private int _head;
    private int _count;

    private ParserState _state;
    private int _csiLength;
    private int _csiIntermediateStart = -1;
    private byte _csiPrivatePrefix;
    private Utf8IncrementalDecoder _utf8 = new();
    private KeyModifiers _utf8PendingMods;

    // OSC / DCS / APC / PM string consumption guard.
    private int _stringLength;
    private bool _stringEscSeen;

    // OSC string capture (auto-theme §3.x): bytes of the current OSC string
    // body, lazily allocated, bounded by MaxStringBytes. Only OSC 11 reports
    // are decoded — every other OSC string stays discarded.
    private byte[]? _oscBuffer;
    private int _oscLength;

    // SGR-mouse press context for Click/Drag/Release synthesis (§3.3).
    private MouseButton _mousePressedButton;
    private bool _mouseMovedSincePress;

    // Bracketed-paste payload assembly (§4). The payload buffer is reused
    // across pastes; only the final UTF-8 decode allocates one string.
    private byte[]? _pasteBuffer;
    private int _pasteLength;
    private bool _pasteTruncated;
    private int _pasteMarkerProgress;

    private static readonly byte[] PasteClose = [(byte)0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~'];

    public EscapeSequenceParser(ParserOptions? options = null)
    {
        _options = options ?? new ParserOptions();
        _csiBuffer = new byte[_options.MaxParamsBytes + _options.MaxIntermediatesBytes];
    }

    public ParserOptions Options => _options;
    public ParserState State => _state;
    public int AvailableEvents => _count;
    public int MalformedSequenceCount { get; private set; }
    public int IgnoredSequenceCount { get; private set; }

    /// <summary>True while a bracketed paste block has not been closed yet.</summary>
    public bool IsAwaitingPasteClose { get; private set; }

    /// <summary>Nested open markers (200~ without close) seen inside paste
    /// blocks — treated as literal content per §4.2 #5.</summary>
    public int NestedPasteMarkerCount { get; private set; }

    /// <summary>Feeds a chunk of raw stdin bytes. Chunks may split escape
    /// sequences or UTF-8 characters at any byte boundary.</summary>
    public void Parse(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            Step(bytes[i]);
        }
    }

    /// <summary>
    /// ESC-timeout policy (§2.4): a lone ESC at a chunk boundary is emitted as
    /// the Escape key once no continuation arrived in time. Called by the
    /// input source when its flush timer fires; never needed for in-chunk ESC.
    /// </summary>
    public void FlushPendingEscape()
    {
        if (_state == ParserState.Escape)
        {
            Enqueue(InputEvent.FromKey(KeyEvent.Simple(KeyCode.Escape)));
            _state = ParserState.Ground;
        }
        else if (_state == ParserState.Ss3)
        {
            // Lone "ESC O" with nothing behind it — drop silently.
            IgnoredSequenceCount++;
            _state = ParserState.Ground;
        }
    }

    /// <summary>Returns every queued event to a reusable list. Allocates only
    /// if the destination list must grow.</summary>
    public void DrainEvents(List<InputEvent> destination)
    {
        for (var i = 0; i < _count; i++)
        {
            destination.Add(_queue[(_head + i) % _queue.Length]);
        }

        ClearEvents();
    }

    public bool TryTakeEvent(out InputEvent evt)
    {
        if (_count == 0)
        {
            evt = default;
            return false;
        }

        evt = _queue[_head];
        _queue[_head] = default;
        _head = (_head + 1) % _queue.Length;
        _count--;
        return true;
    }

    /// <summary>Full teardown-safety reset: drops all pending state and events.</summary>
    public void Reset()
    {
        ResetSequenceBuffers();
        _utf8.Reset();
        _utf8PendingMods = KeyModifiers.None;
        IsAwaitingPasteClose = false;
        _pasteLength = 0;
        _pasteTruncated = false;
        _pasteMarkerProgress = 0;
        _mousePressedButton = MouseButton.None;
        _mouseMovedSincePress = false;
        _state = ParserState.Ground;
        ClearEvents();
    }

    public void ClearEvents()
    {
        for (var i = 0; i < _count; i++)
        {
            _queue[(_head + i) % _queue.Length] = default;
        }

        _head = 0;
        _count = 0;
    }

    private void Step(byte b)
    {
        switch (_state)
        {
            case ParserState.Ground:
                Ground(b);
                break;
            case ParserState.Escape:
                EscapeReceived(b);
                break;
            case ParserState.Ss3:
                Ss3Final(b);
                break;
            case ParserState.CsiEntry:
            case ParserState.CsiParam:
            case ParserState.CsiIntermediate:
            case ParserState.CsiIgnore:
                CsiByte(b);
                break;
            case ParserState.OscString:
                StringBody(b, belTerminates: true);
                break;
            case ParserState.StringUntilSt:
                StringBody(b, belTerminates: false);
                break;
            case ParserState.PastePayload:
                PastePayloadByte(b);
                break;
        }
    }

    // ── Ground ────────────────────────────────────────────────────────────

    private void Ground(byte b)
    {
        if (b == Esc)
        {
            FlushBrokenUtf8();
            _state = ParserState.Escape;
            return;
        }

        // A control byte (or DEL) in the middle of a multibyte sequence means
        // the sequence is broken: emit U+FFFD once, then process the byte
        // through its normal control path.
        if (_utf8.HasPending && (b < 0x20 || b == 0x7F))
        {
            FlushBrokenUtf8();
        }

        // Printable bytes and all high bytes go through the incremental
        // decoder — it passes ASCII through untouched when nothing is pending.
        if (_utf8.HasPending)
        {
            FeedUtf8(b, _utf8PendingMods);
            return;
        }
        if (b >= 0x80)
        {
            FeedUtf8(b, KeyModifiers.None);
            return;
        }

        if (b == 0x7F)
        {
            EnqueueKey(KeyCode.Backspace);
            return;
        }

        if (b < 0x20)
        {
            EnqueueControlKey(b, KeyModifiers.None);
            return;
        }

        EnqueueChar(new Rune(b), KeyModifiers.None);
    }

    private void FlushBrokenUtf8()
    {
        if (!_utf8.HasPending)
        {
            return;
        }

        var mods = _utf8PendingMods;
        _utf8.Reset();
        _utf8PendingMods = KeyModifiers.None;
        EnqueueChar(Rune.ReplacementChar, mods);
    }

    /// <summary>C0 control byte → logical key (raw-mode legacy encoding).</summary>
    private void EnqueueControlKey(byte b, KeyModifiers extraMods)
    {
        switch (b)
        {
            case 0x0D: // CR — Enter
            case 0x0A: // LF — treated as Enter (crossterm-compatible); paste-embedded LFs stay literal inside paste blocks
                EnqueueSimple(KeyCode.Enter, extraMods);
                return;
            case 0x09:
                EnqueueSimple(KeyCode.Tab, extraMods);
                return;
            case 0x08:
            case 0x7F:
                EnqueueSimple(KeyCode.Backspace, extraMods);
                return;
            case 0x00:
                EnqueueChar(new Rune(' '), KeyModifiers.Ctrl | extraMods);
                return;
            default:
                if (b <= 0x1A)
                {
                    EnqueueChar(new Rune((char)('a' + b - 1)), KeyModifiers.Ctrl | extraMods);
                    return;
                }
                if (b >= 0x1C && b <= 0x1F)
                {
                    EnqueueChar(new Rune((char)(b | 0x40)), KeyModifiers.Ctrl | extraMods);
                    return;
                }
                IgnoredSequenceCount++; // 0x1B handled by callers; anything else is noise
                return;
        }
    }

    private void FeedUtf8(byte b, KeyModifiers mods)
    {
        bool isContinuation = _utf8.HasPending;
        // Remember Alt for the continuation bytes so Ground() can recover it.
        if (!isContinuation && b >= 0x80)
        {
            _utf8PendingMods = mods;
        }
        while (true)
        {
            var status = _utf8.DecodeStep(b, out var rune);
            switch (status)
            {
                case Utf8DecodeStatus.NeedMoreData:
                    // keep pending mods for next continuation byte
                    if (!isContinuation)
                    {
                        _utf8PendingMods = mods;
                    }
                    return;
                case Utf8DecodeStatus.Decoded:
                    _utf8PendingMods = KeyModifiers.None;
                    EnqueueChar(rune, mods);
                    return;
                case Utf8DecodeStatus.ReplacementEmitted:
                    _utf8PendingMods = KeyModifiers.None;
                    EnqueueChar(Rune.ReplacementChar, mods);
                    return;
                case Utf8DecodeStatus.ReplacementPendingRetry:
                    _utf8PendingMods = KeyModifiers.None;
                    EnqueueChar(Rune.ReplacementChar, mods);
                    continue; // reprocess current byte from scratch
            }
        }
    }

    // ── ESC ───────────────────────────────────────────────────────────────

    private void EscapeReceived(byte b)
    {
        switch (b)
        {
            case (byte)'[':
                ResetSequenceBuffers();
                _state = ParserState.CsiEntry;
                return;
            case (byte)'O':
                _state = ParserState.Ss3;
                return;
            case (byte)']':
            case (byte)'P':
            case (byte)'X':
            case (byte)'^':
            case (byte)'_':
                _stringLength = 0;
                _stringEscSeen = false;
                _oscLength = 0;
                _state = b == (byte)']' ? ParserState.OscString : ParserState.StringUntilSt;
                return;
            case Esc:
                // Double-ESC: first one stands alone as Escape, second restarts.
                EnqueueSimple(KeyCode.Escape, KeyModifiers.None);
                return;
            case 0x7F:
                return; // ALT-backspace variants ignored
        }

        _state = ParserState.Ground;
        if (b < 0x20)
        {
            EnqueueControlKey(b, KeyModifiers.Alt);
            return;
        }

        if (b < 0x80)
        {
            EnqueueChar(new Rune(b), KeyModifiers.Alt);
            return;
        }

        FeedUtf8(b, KeyModifiers.Alt);
    }

    private void Ss3Final(byte b)
    {
        _state = ParserState.Ground;
        KeyCode key = b switch
        {
            (byte)'A' => KeyCode.Up,
            (byte)'B' => KeyCode.Down,
            (byte)'C' => KeyCode.Right,
            (byte)'D' => KeyCode.Left,
            (byte)'H' => KeyCode.Home,
            (byte)'F' => KeyCode.End,
            (byte)'P' => KeyCode.F1,
            (byte)'Q' => KeyCode.F2,
            (byte)'R' => KeyCode.F3,
            (byte)'S' => KeyCode.F4,
            _ => KeyCode.None,
        };

        if (key == KeyCode.None)
        {
            IgnoredSequenceCount++;
            return;
        }

        EnqueueSimple(key, KeyModifiers.None);
    }

    // ── CSI ───────────────────────────────────────────────────────────────

    private void CsiByte(byte b)
    {
        switch (_state)
        {
            case ParserState.CsiEntry:
                if (b is >= (byte)'0' and <= (byte)'9' or (byte)';' or (byte)':')
                {
                    AppendParamByte(b);
                    _state = ParserState.CsiParam;
                    return;
                }
                if (b is >= 0x3C and <= 0x3F)
                {
                    AppendParamByte(b);
                    _csiPrivatePrefix = b;
                    _state = ParserState.CsiParam;
                    return;
                }
                if (b is >= 0x20 and <= 0x2F)
                {
                    AppendIntermediateByte(b);
                    _state = ParserState.CsiIntermediate;
                    return;
                }
                break;

            case ParserState.CsiParam:
                if (b is >= (byte)'0' and <= (byte)'9' or (byte)';' or (byte)':' or >= 0x3C and <= 0x3F)
                {
                    AppendParamByte(b);
                    return;
                }
                if (b is >= 0x20 and <= 0x2F)
                {
                    AppendIntermediateByte(b);
                    _state = ParserState.CsiIntermediate;
                    return;
                }
                break;

            case ParserState.CsiIntermediate:
                if (b is >= 0x20 and <= 0x2F)
                {
                    AppendIntermediateByte(b);
                    return;
                }
                if (b is >= 0x30 and <= 0x3F)
                {
                    EnterIgnore(); // parameter bytes after intermediates — malformed per ECMA-48
                    return;
                }
                break;

            case ParserState.CsiIgnore:
                if (b is >= 0x40 and <= 0x7E)
                {
                    _state = ParserState.Ground;
                    return;
                }
                ControlInsideCsi(b);
                return;
        }

        // Common tail shared by entry/param/intermediate states.
        if (b is >= 0x40 and <= 0x7E)
        {
            DispatchCsi(b);
            return;
        }

        if (b < 0x20)
        {
            ControlInsideCsi(b);
            return;
        }

        if (b == 0x7F)
        {
            return; // DEL inside CSI is skipped per ECMA-48
        }

        EnterIgnore(); // 0x80+ inside CSI — cannot be valid
    }

    private void ControlInsideCsi(byte b)
    {
        switch (b)
        {
            case Esc:
                ResetSequenceBuffers();
                _state = ParserState.Escape;
                break;
            case Can:
            case Sub:
                ResetSequenceBuffers();
                _state = ParserState.Ground;
                break;
            default:
                break; // other C0 execute-and-ignore inside CSI
        }
    }

    private void AppendParamByte(byte b)
    {
        var limit = _csiIntermediateStart < 0 ? _csiBuffer.Length : Math.Min(_csiIntermediateStart, _options.MaxParamsBytes);
        if (_csiLength >= limit)
        {
            EnterIgnore();
            return;
        }

        _csiBuffer[_csiLength++] = b;
    }

    private void AppendIntermediateByte(byte b)
    {
        if (_csiIntermediateStart < 0)
        {
            _csiIntermediateStart = _csiLength;
        }

        if (_csiLength - _csiIntermediateStart >= _options.MaxIntermediatesBytes || _csiLength >= _csiBuffer.Length)
        {
            EnterIgnore();
            return;
        }

        _csiBuffer[_csiLength++] = b;
    }

    private void EnterIgnore()
    {
        MalformedSequenceCount++;
        Enqueue(InputEvent.Unknown());
        _state = ParserState.CsiIgnore;
    }

    private void DispatchCsi(byte finalByte)
    {
        var paramSpan = _csiIntermediateStart < 0
            ? _csiBuffer.AsSpan(0, _csiLength)
            : _csiBuffer.AsSpan(0, _csiIntermediateStart);
        var intermediateSpan = _csiIntermediateStart < 0
            ? ReadOnlySpan<byte>.Empty
            : _csiBuffer.AsSpan(_csiIntermediateStart, _csiLength - _csiIntermediateStart);

        var prefixByte = _csiPrivatePrefix;
        ResetSequenceBuffers();
        _state = ParserState.Ground;

        DecodeCsiFinal(finalByte, prefixByte, paramSpan, intermediateSpan);
    }

    /// <summary>Routes a complete CSI sequence to its decoder. Zone З.2 adds
    /// mouse M/m, zone З.3 adds paste ~ markers.</summary>
    private void DecodeCsiFinal(byte finalByte, byte privatePrefix, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> intermediates)
    {
        if (privatePrefix == (byte)'<')
        {
            // SGR mouse encoding (design §3.2): CSI < button ; col ; row M|m.
            if (finalByte is (byte)'M' or (byte)'m')
            {
                if (SgrMouseDecoder.TryDecode(
                    finalByte,
                    parameters,
                    _mousePressedButton,
                    _mouseMovedSincePress,
                    out var mouseEvent,
                    out var nextPressedButton,
                    out var nextMovedSincePress))
                {
                    _mousePressedButton = nextPressedButton;
                    _mouseMovedSincePress = nextMovedSincePress;
                    Enqueue(InputEvent.FromMouse(mouseEvent));
                }
                else
                {
                    IgnoredSequenceCount++;
                }
                return;
            }

            IgnoredSequenceCount++;
            return;
        }

        if (privatePrefix != 0)
        {
            switch (finalByte)
            {
                case (byte)'u':
                    // Answer to our kitty probe CSI ? u → CSI ? flags u.
                    EnqueueCapability(CapabilityEvent.KittyFlags((uint)Math.Max(0, FirstIntParam(parameters))));
                    return;
                case (byte)'y' when intermediates.Length == 1 && intermediates[0] == (byte)'$':
                    // DECRQM answer: CSI ? Ps ; Pv $ y.
                    EnqueueCapability(CapabilityEvent.DecRqm(
                        Math.Max(0, FirstIntParam(parameters)),
                        Math.Max(0, IntParamAt(parameters, 1))));
                    return;
                case (byte)'c':
                    // Primary device attributes: CSI [ ? Ps ; … c.
                    EnqueueCapability(CapabilityEvent.Da(Math.Max(0, FirstIntParam(parameters))));
                    return;
                default:
                    // Private-mode reports we did not request (h/l and friends).
                    IgnoredSequenceCount++;
                    return;
            }
        }

        switch (finalByte)
        {
            case (byte)'A':
                EnqueueArrow(KeyCode.Up, parameters);
                return;
            case (byte)'B':
                EnqueueArrow(KeyCode.Down, parameters);
                return;
            case (byte)'C':
                EnqueueArrow(KeyCode.Right, parameters);
                return;
            case (byte)'D':
                EnqueueArrow(KeyCode.Left, parameters);
                return;
            case (byte)'H':
                EnqueueArrow(KeyCode.Home, parameters);
                return;
            case (byte)'F':
                EnqueueArrow(KeyCode.End, parameters);
                return;
            case (byte)'~':
                if (FirstIntParam(parameters) == 200)
                {
                    // Bracketed-paste open marker: CSI 200 ~ (§4.1).
                    StartPaste();
                    return;
                }

                DecodeLegacyTilde(parameters);
                return;
            case (byte)'u':
                if (KittyKeyDecoder.TryDecode(parameters, out var kittyKey))
                {
                    Enqueue(InputEvent.FromKey(kittyKey));
                }
                else
                {
                    IgnoredSequenceCount++;
                }
                return;
            case (byte)'R':
                // Cursor position report: CSI row ; col R (probe-routable).
                var row = IntParamAt(parameters, 0);
                var col = IntParamAt(parameters, 1);
                if (row > 0 && col > 0)
                {
                    EnqueueCapability(CapabilityEvent.CursorPosition(row, col));
                    return;
                }
                IgnoredSequenceCount++;
                return;
            default:
                IgnoredSequenceCount++;
                return;
        }
    }

    private void EnqueueArrow(KeyCode key, ReadOnlySpan<byte> parameters)
    {
        var mods = LegacyModifiers(parameters);
        EnqueueSimple(key, mods);
    }

    private void DecodeLegacyTilde(ReadOnlySpan<byte> parameters)
    {
        var code = FirstIntParam(parameters);
        var mods = LegacyModifiers(parameters);
        KeyCode key = code switch
        {
            1 => KeyCode.Home,
            2 => KeyCode.Insert,
            3 => KeyCode.Delete,
            4 => KeyCode.End,
            5 => KeyCode.PageUp,
            6 => KeyCode.PageDown,
            7 => KeyCode.Home,
            8 => KeyCode.End,
            11 => KeyCode.F1,
            12 => KeyCode.F2,
            13 => KeyCode.F3,
            14 => KeyCode.F4,
            15 => KeyCode.F5,
            17 => KeyCode.F6,
            18 => KeyCode.F7,
            19 => KeyCode.F8,
            20 => KeyCode.F9,
            21 => KeyCode.F10,
            23 => KeyCode.F11,
            24 => KeyCode.F12,
            _ => KeyCode.None,
        };

        if (key == KeyCode.None)
        {
            IgnoredSequenceCount++;
            return;
        }

        EnqueueSimple(key, mods);
    }

    /// <summary>
    /// Legacy CSI/SS3 modifier parameter (xterm encoding).
    /// Forwarder over <see cref="CsiDecoder"/> (#1503, slice: verbatim move).
    /// </summary>
    private static KeyModifiers LegacyModifiers(ReadOnlySpan<byte> parameters) =>
        CsiDecoder.LegacyModifiers(parameters);

    // ── OSC / DCS strings ─────────────────────────────────────────────────

    private void StringBody(byte b, bool belTerminates)
    {
        if (_stringEscSeen)
        {
            _stringEscSeen = false;
            if (b == (byte)'\\')
            {
                // ST terminator — an OSC string just completed.
                EmitOscReportIfPresent("\u001B\\");
                _state = ParserState.Ground;
                return;
            }

            // Embedded non-ST ESC — swallow both bytes, keep consuming.
            _stringLength += 2;
            if (_stringLength > _options.MaxStringBytes)
            {
                ForceStringAbort();
            }
            return;
        }

        if (belTerminates && b == Bel)
        {
            EmitOscReportIfPresent("\u0007");
            _state = ParserState.Ground;
            return;
        }

        if (b == Esc)
        {
            _stringEscSeen = true;
            return;
        }

        CaptureOscByte(b);
        _stringLength++;
        if (_stringLength > _options.MaxStringBytes)
        {
            ForceStringAbort();
        }
    }

    /// <summary>Stashes one OSC-string body byte (OscString state only) so an
    /// OSC 11 background report can be decoded on termination.</summary>
    private void CaptureOscByte(byte b)
    {
        if (_state != ParserState.OscString)
        {
            return;
        }

        if (_oscBuffer is null)
        {
            _oscBuffer = new byte[_options.MaxStringBytes];
        }

        if (_oscLength < _oscBuffer.Length)
        {
            _oscBuffer[_oscLength++] = b;
        }
    }

    /// <summary>Decodes a just-terminated OSC string: «11;rgb:…» background
    /// reports and «99;…» kitty notification capability answers become
    /// capability events; every other OSC string stays discarded.</summary>
    private void EmitOscReportIfPresent(string terminator)
    {
        int len = _oscLength;
        _oscLength = 0;
        if (_state != ParserState.OscString || len < 3 || _oscBuffer is null)
        {
            return;
        }

        if (_oscBuffer[0] == (byte)'1' && _oscBuffer[1] == (byte)'1' && _oscBuffer[2] == (byte)';')
        {
            string report = Encoding.ASCII.GetString(_oscBuffer, 0, len) + terminator;
            if (TerminalBackgroundProbe.TryParseOsc11(report, out var background))
            {
                EnqueueCapability(CapabilityEvent.Osc11Background(background.R, background.G, background.B));
            }

            return;
        }

        if (_oscBuffer[0] == (byte)'9' && _oscBuffer[1] == (byte)'9' && _oscBuffer[2] == (byte)';')
        {
            // kitty desktop-notification capability answer (osc-sprint §777):
            // the query OSC 99;i=harbor:p=? was answered — notifications exist.
            EnqueueCapability(CapabilityEvent.Osc99Notify());
        }
    }

    private void ForceStringAbort()
    {
        MalformedSequenceCount++;
        Enqueue(InputEvent.Unknown());
        _stringEscSeen = false;
        _stringLength = 0;
        _oscLength = 0;
        _state = ParserState.Ground;
    }

    // ── Bracketed paste (§4) ──────────────────────────────────────────────

    /// <summary>Anti-injection invariant (§4.2): paste content is copied
    /// verbatim into one atomic PasteEvent — escape bytes and control bytes
    /// inside the block are NEVER decoded as key/mouse events.</summary>
    private void StartPaste()
    {
        _pasteBuffer ??= new byte[_options.MaxPasteBytes];
        _pasteLength = 0;
        _pasteTruncated = false;
        _pasteMarkerProgress = 0;
        IsAwaitingPasteClose = true;
        _state = ParserState.PastePayload;
    }

    private void PastePayloadByte(byte b)
    {
        if (_pasteMarkerProgress > 0 && b == PasteClose[_pasteMarkerProgress])
        {
            // Continue matching the closing marker ESC [ 2 0 1 ~.
            if (_pasteMarkerProgress == PasteClose.Length - 1)
            {
                EmitPaste(_pasteTruncated);
                return;
            }

            _pasteMarkerProgress++;
            return;
        }

        // Mismatch: the partial marker match was literal content after all.
        if (_pasteMarkerProgress > 0)
        {
            // A nested OPEN marker (…200~) shares its prefix with the closer —
            // count it for diagnostics, content stays literal (§4.2 #5).
            if (_pasteMarkerProgress == 4 && b == (byte)'0')
            {
                NestedPasteMarkerCount++;
            }

            for (var i = 0; i < _pasteMarkerProgress; i++)
            {
                AppendPasteByte(PasteClose[i]);
            }

            _pasteMarkerProgress = 0;
        }

        if (b == PasteClose[0])
        {
            _pasteMarkerProgress = 1;
            return;
        }

        AppendPasteByte(b);
    }

    private void AppendPasteByte(byte b)
    {
        if (_pasteBuffer is null || _pasteLength >= _pasteBuffer.Length)
        {
            // Paste-flood guard (§8.3): drop beyond cap but keep scanning so
            // the closing marker still terminates the block cleanly.
            _pasteTruncated = true;
            return;
        }

        _pasteBuffer[_pasteLength++] = b;
    }

    /// <summary>Watchdog hook: force-closes a hung paste block (design §4.2),
    /// emitting whatever was accumulated as a truncated paste.</summary>
    public void AbortPendingPaste()
    {
        if (!IsAwaitingPasteClose)
        {
            return;
        }

        _pasteMarkerProgress = 0;
        EmitPaste(wasTruncated: true);
        _state = ParserState.Ground;
    }

    private void EmitPaste(bool wasTruncated)
    {
        var text = _pasteBuffer is null || _pasteLength == 0
            ? string.Empty
            : Encoding.UTF8.GetString(_pasteBuffer, 0, _pasteLength);

        _pasteLength = 0;
        _pasteTruncated = false;
        _pasteMarkerProgress = 0;
        IsAwaitingPasteClose = false;
        _state = ParserState.Ground;

        Enqueue(InputEvent.FromPaste(new PasteEvent(text, wasTruncated)));
    }

    // ── Param scanning helpers (scalar, zero-alloc) ───────────────────────
    // Bodies live in <see cref="CsiDecoder"/> (#1503, slice: verbatim move).

    private static int FirstIntParam(ReadOnlySpan<byte> parameters) => CsiDecoder.FirstIntParam(parameters);

    /// <summary>Returns the <paramref name="index"/>-th ';'-separated parameter
    /// (sub-parameters ':' are ignored), or −1 when absent.
    /// Forwarder over <see cref="CsiDecoder"/> (#1503, slice: verbatim move).</summary>
    internal static int IntParamAt(ReadOnlySpan<byte> parameters, int index) =>
        CsiDecoder.IntParamAt(parameters, index);

    // ── Event queue ───────────────────────────────────────────────────────

    private void EnqueueSimple(KeyCode key, KeyModifiers mods) =>
        Enqueue(InputEvent.FromKey(KeyEvent.Simple(key, mods)));

    private void EnqueueKey(KeyCode key) => EnqueueSimple(key, KeyModifiers.None);

    private void EnqueueChar(Rune rune, KeyModifiers mods) =>
        Enqueue(InputEvent.FromKey(KeyEvent.Char(rune, mods)));

    private void EnqueueCapability(CapabilityEvent evt) =>
        Enqueue(InputEvent.FromCapability(evt));

    private void Enqueue(in InputEvent evt)
    {
        if (_count == _queue.Length)
        {
            GrowQueue();
        }

        _queue[(_head + _count) % _queue.Length] = evt;
        _count++;
    }

    private void GrowQueue()
    {
        var grown = new InputEvent[_queue.Length * 2];
        for (var i = 0; i < _count; i++)
        {
            grown[i] = _queue[(_head + i) % _queue.Length];
        }

        _queue = grown;
        _head = 0;
    }

    private void ResetSequenceBuffers()
    {
        _csiLength = 0;
        _csiIntermediateStart = -1;
        _csiPrivatePrefix = 0;
        _stringEscSeen = false;
        _stringLength = 0;
        _oscLength = 0;
    }
}
