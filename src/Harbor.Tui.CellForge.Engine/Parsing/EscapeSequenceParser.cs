using System.Text;
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
    internal const byte Esc = 0x1B;
    internal const byte Bel = 0x07;
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
    internal int _stringLength;
    internal bool _stringEscSeen;

    // OSC string capture (auto-theme §3.x): bytes of the current OSC string
    // body, lazily allocated, bounded by MaxStringBytes. Only OSC 11 reports
    // are decoded — every other OSC string stays discarded.
    internal byte[]? _oscBuffer;
    internal int _oscLength;

    // SGR-mouse press context for Click/Drag/Release synthesis (§3.3).
    private MouseButton _mousePressedButton;
    private bool _mouseMovedSincePress;

    // Bracketed-paste payload assembly (§4). The payload buffer is reused
    // across pastes; only the final UTF-8 decode allocates one string.
    internal byte[]? _pasteBuffer;
    internal int _pasteLength;
    internal bool _pasteTruncated;
    internal int _pasteMarkerProgress;

    public EscapeSequenceParser(ParserOptions? options = null)
    {
        _options = options ?? new ParserOptions();
        _csiBuffer = new byte[_options.MaxParamsBytes + _options.MaxIntermediatesBytes];
    }

    public ParserOptions Options => _options;
    public ParserState State => _state;
    public int AvailableEvents => _count;
    public int MalformedSequenceCount { get; internal set; }
    public int IgnoredSequenceCount { get; internal set; }

    /// <summary>True while a bracketed paste block has not been closed yet.</summary>
    public bool IsAwaitingPasteClose { get; internal set; }

    /// <summary>Nested open markers (200~ without close) seen inside paste
    /// blocks — treated as literal content per §4.2 #5.</summary>
    public int NestedPasteMarkerCount { get; internal set; }

    internal void SetState(ParserState value) => _state = value;

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

    /// <summary>Routes a complete CSI sequence to its decoder.
    /// Forwarder over <see cref="CsiDecoder"/> (#1503, slice: verbatim move).</summary>
    private void DecodeCsiFinal(byte finalByte, byte privatePrefix, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> intermediates) =>
        CsiDecoder.DecodeCsiFinal(this, finalByte, privatePrefix, parameters, intermediates, ref _mousePressedButton, ref _mouseMovedSincePress);

    /// <summary>
    /// Forwarder over <see cref="CsiDecoder"/> (#1503, slice: verbatim move).</summary>
    private void EnqueueArrow(KeyCode key, ReadOnlySpan<byte> parameters) =>
        CsiDecoder.EnqueueArrow(this, key, parameters);

    /// <summary>
    /// Forwarder over <see cref="CsiDecoder"/> (#1503, slice: verbatim move).</summary>
    private void DecodeLegacyTilde(ReadOnlySpan<byte> parameters) =>
        CsiDecoder.DecodeLegacyTilde(this, parameters);

    /// <summary>
    /// Legacy CSI/SS3 modifier parameter (xterm encoding).
    /// Forwarder over <see cref="CsiDecoder"/> (#1503, slice: verbatim move).
    /// </summary>
    private static KeyModifiers LegacyModifiers(ReadOnlySpan<byte> parameters) =>
        CsiDecoder.LegacyModifiers(parameters);

    // ── OSC / DCS strings ─────────────────────────────────────────────────
    // Bodies live in <see cref="OscHandler"/> (#1504, slice: verbatim move).

    private void StringBody(byte b, bool belTerminates) =>
        OscHandler.StringBody(this, b, belTerminates);

    /// <summary>Stashes one OSC-string body byte (OscString state only) so an
    /// OSC 11 background report can be decoded on termination.
    /// Forwarder over <see cref="OscHandler"/> (#1504, slice: verbatim move).</summary>
    private void CaptureOscByte(byte b) =>
        OscHandler.CaptureOscByte(this, b);

    /// <summary>Decodes a just-terminated OSC string: «11;rgb:…» background
    /// reports and «99;…» kitty notification capability answers become
    /// capability events; every other OSC string stays discarded.
    /// Forwarder over <see cref="OscHandler"/> (#1504, slice: verbatim move).</summary>
    private void EmitOscReportIfPresent(string terminator) =>
        OscHandler.EmitOscReportIfPresent(this, terminator);

    private void ForceStringAbort() =>
        OscHandler.ForceStringAbort(this);

    // ── Bracketed paste (§4) ──────────────────────────────────────────────
    // Bodies live in <see cref="OscHandler"/> (#1504, slice: verbatim move).

    /// <summary>Anti-injection invariant (§4.2): paste content is copied
    /// verbatim into one atomic PasteEvent — escape bytes and control bytes
    /// inside the block are NEVER decoded as key/mouse events.
    /// Forwarder over <see cref="OscHandler"/> (#1504, slice: verbatim move).</summary>
    internal void StartPaste() =>
        OscHandler.StartPaste(this);

    private void PastePayloadByte(byte b) =>
        OscHandler.PastePayloadByte(this, b);

    private void AppendPasteByte(byte b) =>
        OscHandler.AppendPasteByte(this, b);

    /// <summary>Watchdog hook: force-closes a hung paste block (design §4.2),
    /// emitting whatever was accumulated as a truncated paste.
    /// Forwarder over <see cref="OscHandler"/> (#1504, slice: verbatim move).</summary>
    public void AbortPendingPaste() =>
        OscHandler.AbortPendingPaste(this);

    private void EmitPaste(bool wasTruncated) =>
        OscHandler.EmitPaste(this, wasTruncated);

    // ── Param scanning helpers (scalar, zero-alloc) ───────────────────────
    // Bodies live in <see cref="CsiDecoder"/> (#1503, slice: verbatim move).

    private static int FirstIntParam(ReadOnlySpan<byte> parameters) => CsiDecoder.FirstIntParam(parameters);

    /// <summary>Returns the <paramref name="index"/>-th ';'-separated parameter
    /// (sub-parameters ':' are ignored), or −1 when absent.
    /// Forwarder over <see cref="CsiDecoder"/> (#1503, slice: verbatim move).</summary>
    internal static int IntParamAt(ReadOnlySpan<byte> parameters, int index) =>
        CsiDecoder.IntParamAt(parameters, index);

    // ── Event queue ───────────────────────────────────────────────────────

    internal void EnqueueSimple(KeyCode key, KeyModifiers mods) =>
        Enqueue(InputEvent.FromKey(KeyEvent.Simple(key, mods)));

    private void EnqueueKey(KeyCode key) => EnqueueSimple(key, KeyModifiers.None);

    private void EnqueueChar(Rune rune, KeyModifiers mods) =>
        Enqueue(InputEvent.FromKey(KeyEvent.Char(rune, mods)));

    internal void EnqueueCapability(CapabilityEvent evt) =>
        Enqueue(InputEvent.FromCapability(evt));

    internal void Enqueue(in InputEvent evt)
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
