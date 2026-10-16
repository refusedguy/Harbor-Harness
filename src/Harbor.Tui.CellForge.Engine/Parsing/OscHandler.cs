using System.Text;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Parsing;

/// <summary>
/// OSC / string-body + bracketed-paste path (design §4, auto-theme §3.x),
/// extracted from <see cref="EscapeSequenceParser"/> (#95, one extract per PR).
/// Verbatim move — the parser's <c>StringBody</c> family and paste block,
/// unchanged apart from threading the owning parser explicitly (mutable
/// string/paste state stays owned by the parser: <c>internal</c> fields and
/// sinks, cf. <see cref="CsiDecoder"/>).
/// The parser keeps thin forwarders so <c>Step</c> dispatch is untouched;
/// caller migration and forwarder deletion are tracked in #1392.
/// </summary>
internal static class OscHandler
{
    private static readonly byte[] PasteClose = [(byte)0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~'];

    internal static void StringBody(EscapeSequenceParser parser, byte b, bool belTerminates)
    {
        if (parser._stringEscSeen)
        {
            parser._stringEscSeen = false;
            if (b == (byte)'\\')
            {
                // ST terminator — an OSC string just completed.
                EmitOscReportIfPresent(parser, "\u001B\\");
                parser.SetState(ParserState.Ground);
                return;
            }

            // Embedded non-ST ESC — swallow both bytes, keep consuming.
            parser._stringLength += 2;
            if (parser._stringLength > parser.Options.MaxStringBytes)
            {
                ForceStringAbort(parser);
            }
            return;
        }

        if (belTerminates && b == EscapeSequenceParser.Bel)
        {
            EmitOscReportIfPresent(parser, "\u0007");
            parser.SetState(ParserState.Ground);
            return;
        }

        if (b == EscapeSequenceParser.Esc)
        {
            parser._stringEscSeen = true;
            return;
        }

        CaptureOscByte(parser, b);
        parser._stringLength++;
        if (parser._stringLength > parser.Options.MaxStringBytes)
        {
            ForceStringAbort(parser);
        }
    }

    /// <summary>Stashes one OSC-string body byte (OscString state only) so an
    /// OSC 11 background report can be decoded on termination.</summary>
    internal static void CaptureOscByte(EscapeSequenceParser parser, byte b)
    {
        if (parser.State != ParserState.OscString)
        {
            return;
        }

        if (parser._oscBuffer is null)
        {
            parser._oscBuffer = new byte[parser.Options.MaxStringBytes];
        }

        if (parser._oscLength < parser._oscBuffer.Length)
        {
            parser._oscBuffer[parser._oscLength++] = b;
        }
    }

    /// <summary>Decodes a just-terminated OSC string: «11;rgb:…» background
    /// reports and «99;…» kitty notification capability answers become
    /// capability events; every other OSC string stays discarded.</summary>
    internal static void EmitOscReportIfPresent(EscapeSequenceParser parser, string terminator)
    {
        int len = parser._oscLength;
        parser._oscLength = 0;
        if (parser.State != ParserState.OscString || len < 3 || parser._oscBuffer is null)
        {
            return;
        }

        if (parser._oscBuffer[0] == (byte)'1' && parser._oscBuffer[1] == (byte)'1' && parser._oscBuffer[2] == (byte)';')
        {
            string report = Encoding.ASCII.GetString(parser._oscBuffer, 0, len) + terminator;
            if (TerminalBackgroundProbe.TryParseOsc11(report, out var background))
            {
                parser.EnqueueCapability(CapabilityEvent.Osc11Background(background.R, background.G, background.B));
            }

            return;
        }

        if (parser._oscBuffer[0] == (byte)'9' && parser._oscBuffer[1] == (byte)'9' && parser._oscBuffer[2] == (byte)';')
        {
            // kitty desktop-notification capability answer (osc-sprint §777):
            // the query OSC 99;i=harbor:p=? was answered — notifications exist.
            parser.EnqueueCapability(CapabilityEvent.Osc99Notify());
        }
    }

    internal static void ForceStringAbort(EscapeSequenceParser parser)
    {
        parser.MalformedSequenceCount++;
        parser.Enqueue(InputEvent.Unknown());
        parser._stringEscSeen = false;
        parser._stringLength = 0;
        parser._oscLength = 0;
        parser.SetState(ParserState.Ground);
    }

    /// <summary>Anti-injection invariant (§4.2): paste content is copied
    /// verbatim into one atomic PasteEvent — escape bytes and control bytes
    /// inside the block are NEVER decoded as key/mouse events.</summary>
    internal static void StartPaste(EscapeSequenceParser parser)
    {
        parser._pasteBuffer ??= new byte[parser.Options.MaxPasteBytes];
        parser._pasteLength = 0;
        parser._pasteTruncated = false;
        parser._pasteMarkerProgress = 0;
        parser.IsAwaitingPasteClose = true;
        parser.SetState(ParserState.PastePayload);
    }

    internal static void PastePayloadByte(EscapeSequenceParser parser, byte b)
    {
        if (parser._pasteMarkerProgress > 0 && b == PasteClose[parser._pasteMarkerProgress])
        {
            // Continue matching the closing marker ESC [ 2 0 1 ~.
            if (parser._pasteMarkerProgress == PasteClose.Length - 1)
            {
                EmitPaste(parser, parser._pasteTruncated);
                return;
            }

            parser._pasteMarkerProgress++;
            return;
        }

        // Mismatch: the partial marker match was literal content after all.
        if (parser._pasteMarkerProgress > 0)
        {
            // A nested OPEN marker (…200~) shares its prefix with the closer —
            // count it for diagnostics, content stays literal (§4.2 #5).
            if (parser._pasteMarkerProgress == 4 && b == (byte)'0')
            {
                parser.NestedPasteMarkerCount++;
            }

            for (var i = 0; i < parser._pasteMarkerProgress; i++)
            {
                AppendPasteByte(parser, PasteClose[i]);
            }

            parser._pasteMarkerProgress = 0;
        }

        if (b == PasteClose[0])
        {
            parser._pasteMarkerProgress = 1;
            return;
        }

        AppendPasteByte(parser, b);
    }

    internal static void AppendPasteByte(EscapeSequenceParser parser, byte b)
    {
        if (parser._pasteBuffer is null || parser._pasteLength >= parser._pasteBuffer.Length)
        {
            // Paste-flood guard (§8.3): drop beyond cap but keep scanning so
            // the closing marker still terminates the block cleanly.
            parser._pasteTruncated = true;
            return;
        }

        parser._pasteBuffer[parser._pasteLength++] = b;
    }

    /// <summary>Watchdog hook: force-closes a hung paste block (design §4.2),
    /// emitting whatever was accumulated as a truncated paste.</summary>
    internal static void AbortPendingPaste(EscapeSequenceParser parser)
    {
        if (!parser.IsAwaitingPasteClose)
        {
            return;
        }

        parser._pasteMarkerProgress = 0;
        EmitPaste(parser, wasTruncated: true);
        parser.SetState(ParserState.Ground);
    }

    internal static void EmitPaste(EscapeSequenceParser parser, bool wasTruncated)
    {
        var text = parser._pasteBuffer is null || parser._pasteLength == 0
            ? string.Empty
            : Encoding.UTF8.GetString(parser._pasteBuffer, 0, parser._pasteLength);

        parser._pasteLength = 0;
        parser._pasteTruncated = false;
        parser._pasteMarkerProgress = 0;
        parser.IsAwaitingPasteClose = false;
        parser.SetState(ParserState.Ground);

        parser.Enqueue(InputEvent.FromPaste(new PasteEvent(text, wasTruncated)));
    }
}
