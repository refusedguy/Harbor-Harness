using System.Text;
using Harbor.Tui.ConsoleEx.Input;

namespace Harbor.Tui.ConsoleEx.Parsing;

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

    // OSC / DCS / APC / PM string consumption guard.
    private int _stringLength;
    private bool _stringEscSeen;

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
        if (_utf8.HasPending || b >= 0x80)
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

        _utf8.Reset();
        EnqueueChar(Rune.ReplacementChar, KeyModifiers.None);
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
        while (true)
        {
            var status = _utf8.DecodeStep(b, out var rune);
            switch (status)
            {
                case Utf8DecodeStatus.NeedMoreData:
                    return;
                case Utf8DecodeStatus.Decoded:
                    EnqueueChar(rune, mods);
                    return;
                case Utf8DecodeStatus.ReplacementEmitted:
                    EnqueueChar(Rune.ReplacementChar, mods);
                    return;
                case Utf8DecodeStatus.ReplacementPendingRetry:
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
                DecodeSgrMouse(finalByte, parameters);
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
                DecodeKittyKey(parameters);
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

    /// <summary>
    /// Kitty keyboard protocol key decoding (design §2.3):
    /// CSI unicode-key-code : shifted : base-layout ; modifiers : event-type ; text-codepoints u
    /// Modifier bits follow the ConsoleEx design contract: shift=1, ctrl=2,
    /// alt=4 (super/hyper/meta collapse to the Meta bit); modifier value is 1-based.
    /// </summary>
    private void DecodeKittyKey(ReadOnlySpan<byte> parameters)
    {
        var primary = IntSubParamAt(parameters, 0, 0);
        if (primary < 0)
        {
            IgnoredSequenceCount++;
            return;
        }

        var shifted = IntSubParamAt(parameters, 0, 1);
        var text = IntSubParamAt(parameters, 2, 0);
        var modValue = Math.Max(1, IntParamAt(parameters, 1));
        var eventTypeValue = IntSubParamAt(parameters, 1, 1);

        var bits = modValue - 1;
        var mods = KeyModifiers.None;
        if ((bits & 0x01) != 0)
        {
            mods |= KeyModifiers.Shift;
        }
        if ((bits & 0x02) != 0)
        {
            mods |= KeyModifiers.Ctrl;
        }
        if ((bits & 0x04) != 0)
        {
            mods |= KeyModifiers.Alt;
        }
        if ((bits & 0x08) != 0 || (bits & 0x10) != 0 || (bits & 0x20) != 0)
        {
            mods |= KeyModifiers.Meta; // super/hyper/meta collapse (design §2.3, sprint bits 1–4)
        }

        var eventType = eventTypeValue switch
        {
            2 => KeyEventType.Repeat,
            3 => KeyEventType.Release,
            _ => KeyEventType.Press,
        };

        KeyCode key;
        var character = default(Rune);
        uint codepoint = 0;
        switch (primary)
        {
            case 13:
                key = KeyCode.Enter;
                break;
            case 9:
                key = KeyCode.Tab;
                break;
            case 27:
                key = KeyCode.Escape;
                break;
            case 127:
                key = KeyCode.Backspace;
                break;
            case >= 32 and not 127 when primary is < 57344 or > 63743:
                // Printable (excluding the Unicode private-use area where
                // kitty parks its functional keys).
                var scalar = text > 0 ? text : shifted > 0 ? shifted : primary;
                if (IsValidScalar(scalar))
                {
                    key = KeyCode.Char;
                    character = new Rune(scalar);
                }
                else
                {
                    key = KeyCode.Unknown;
                    codepoint = (uint)primary;
                }
                break;
            default:
                // Functional/unmapped codepoints stay lossless.
                key = KeyCode.Unknown;
                codepoint = (uint)primary;
                break;
        }

        Enqueue(InputEvent.FromKey(new KeyEvent(key, character, mods, eventType, isKittyEncoded: true, codepoint)));
    }

    private static bool IsValidScalar(int scalar) =>
        scalar <= 0xD7FF || scalar is >= 0xE000 and <= 0x10FFFF;

    /// <summary>
    /// SGR mouse decoding (design §3.2): CSI &lt; button ; column ; row M|m.
    /// Button bits: id=bits0-1, shift=4, alt(meta)=8, ctrl=16, motion=32,
    /// wheel=64. Coordinates are one-based on the wire, stored zero-based.
    /// Click = clean press→release without motion; Drag = motion with a held
    /// button; Release = release after drag (§3.2/§3.3).
    /// </summary>
    private void DecodeSgrMouse(byte finalByte, ReadOnlySpan<byte> parameters)
    {
        var buttonRaw = IntParamAt(parameters, 0);
        var column = IntParamAt(parameters, 1);
        var row = IntParamAt(parameters, 2);
        if (buttonRaw < 0 || column < 0 || row < 0)
        {
            IgnoredSequenceCount++;
            return;
        }

        var mods = KeyModifiers.None;
        if ((buttonRaw & 0x04) != 0)
        {
            mods |= KeyModifiers.Shift;
        }
        if ((buttonRaw & 0x08) != 0)
        {
            mods |= KeyModifiers.Alt;
        }
        if ((buttonRaw & 0x10) != 0)
        {
            mods |= KeyModifiers.Ctrl;
        }

        // Zero-based viewport coordinates; values may exceed the window
        // (release-after-drag) — consumers clamp before indexing.
        var col = column - 1;
        var r = row - 1;

        var buttonId = buttonRaw & 0x03;

        if ((buttonRaw & 0x40) != 0)
        {
            // Wheel: scroll-id in bits 0-1; 64=up, 65=down; 66+ horizontal —
            // ignored by design (§3.2).
            if (buttonId > 1)
            {
                IgnoredSequenceCount++;
                return;
            }

            var wheel = buttonId == 0 ? MouseEventType.WheelUp : MouseEventType.WheelDown;
            Enqueue(InputEvent.FromMouse(new MouseEvent(wheel, MouseButton.None, col, r, mods)));
            return;
        }

        if (finalByte == (byte)'m')
        {
            // Release: unpaired releases (no press context) are dropped.
            if (_mousePressedButton == MouseButton.None || buttonId > 2)
            {
                IgnoredSequenceCount++;
                return;
            }

            var releasedButton = _mousePressedButton;
            var wasCleanClick = !_mouseMovedSincePress;
            _mousePressedButton = MouseButton.None;
            _mouseMovedSincePress = false;

            Enqueue(InputEvent.FromMouse(new MouseEvent(
                wasCleanClick ? MouseEventType.Click : MouseEventType.Release,
                releasedButton, col, r, mods)));
            return;
        }

        // Final 'M': press or motion(drag).
        if ((buttonRaw & 0x20) != 0)
        {
            // Motion: only meaningful while a tracked button is held (mode 1002).
            if (_mousePressedButton == MouseButton.None || buttonId > 2)
            {
                IgnoredSequenceCount++;
                return;
            }

            _mouseMovedSincePress = true;
            Enqueue(InputEvent.FromMouse(new MouseEvent(MouseEventType.Drag, _mousePressedButton, col, r, mods)));
            return;
        }

        if (buttonId > 2)
        {
            // Legacy "release without button" (id 3) and reserved ids.
            IgnoredSequenceCount++;
            return;
        }

        _mousePressedButton = (MouseButton)(buttonId + 1);
        _mouseMovedSincePress = false;
        Enqueue(InputEvent.FromMouse(new MouseEvent(MouseEventType.Press, _mousePressedButton, col, r, mods)));
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
    /// Legacy CSI/SS3 modifier parameter (xterm encoding): SECOND parameter,
    /// value−1 with shift=bit0, alt=bit1, ctrl=bit2, meta=bit3. NOTE the
    /// different bit order versus kitty CSI-u (kitty: shift=1, ctrl=2, alt=4).
    /// </summary>
    private static KeyModifiers LegacyModifiers(ReadOnlySpan<byte> parameters)
    {
        var bits = IntParamAt(parameters, 1) - 1;
        if (bits <= 0)
        {
            return KeyModifiers.None;
        }

        var mods = KeyModifiers.None;
        if ((bits & 0x01) != 0)
        {
            mods |= KeyModifiers.Shift;
        }
        if ((bits & 0x02) != 0)
        {
            mods |= KeyModifiers.Alt;
        }
        if ((bits & 0x04) != 0)
        {
            mods |= KeyModifiers.Ctrl;
        }
        if ((bits & 0x08) != 0)
        {
            mods |= KeyModifiers.Meta;
        }

        return mods;
    }

    // ── OSC / DCS strings ─────────────────────────────────────────────────

    private void StringBody(byte b, bool belTerminates)
    {
        if (_stringEscSeen)
        {
            _stringEscSeen = false;
            if (b == (byte)'\\')
            {
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
            _state = ParserState.Ground;
            return;
        }

        if (b == Esc)
        {
            _stringEscSeen = true;
            return;
        }

        _stringLength++;
        if (_stringLength > _options.MaxStringBytes)
        {
            ForceStringAbort();
        }
    }

    private void ForceStringAbort()
    {
        MalformedSequenceCount++;
        Enqueue(InputEvent.Unknown());
        _stringEscSeen = false;
        _stringLength = 0;
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

    private static int FirstIntParam(ReadOnlySpan<byte> parameters) => IntParamAt(parameters, 0);

    /// <summary>Returns the sub-parameter at (group, sub) of the ';'/':'
    /// parameter matrix — e.g. kitty CSI unicode:shifted ; mods:event u —
    /// or −1 when absent.</summary>
    private static int IntSubParamAt(ReadOnlySpan<byte> parameters, int groupIndex, int subIndex)
    {
        var group = 0;
        var sub = 0;
        var value = 0;
        var digits = 0;
        foreach (var b in parameters)
        {
            if (b == (byte)';')
            {
                if (group == groupIndex && sub == subIndex)
                {
                    return digits > 0 ? value : -1;
                }
                group++;
                sub = 0;
                value = 0;
                digits = 0;
                continue;
            }
            if (b == (byte)':')
            {
                if (group == groupIndex && sub == subIndex)
                {
                    return digits > 0 ? value : -1;
                }
                if (group > groupIndex)
                {
                    break;
                }
                sub++;
                value = 0;
                digits = 0;
                continue;
            }
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                checked
                {
                    value = value * 10 + (b - (byte)'0');
                }
                digits++;
            }
        }

        return group == groupIndex && sub == subIndex && digits > 0 ? value : -1;
    }

    /// <summary>Returns the <paramref name="index"/>-th ';'-separated parameter
    /// (sub-parameters ':' are ignored), or −1 when absent.</summary>
    private static int IntParamAt(ReadOnlySpan<byte> parameters, int index)
    {
        var group = 0;
        var value = 0;
        var digits = 0;
        foreach (var b in parameters)
        {
            if (b == (byte)';')
            {
                if (group == index && digits > 0)
                {
                    return value;
                }
                group++;
                value = 0;
                digits = 0;
                continue;
            }
            if (b == (byte)':')
            {
                if (group == index)
                {
                    // Target group's first sub-parameter ends here.
                    return digits > 0 ? value : -1;
                }

                // Sub-parameters of an EARLIER group are skipped transparently.
                value = 0;
                digits = 0;
                continue;
            }
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                checked
                {
                    value = value * 10 + (b - (byte)'0');
                }
                digits++;
            }
        }

        return group == index && digits > 0 ? value : -1;
    }

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
    }
}
