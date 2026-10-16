using System.Text;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Parsing;

/// <summary>
/// OSC / string-body path (design §5, auto-theme §3.x),
/// extracted from <see cref="EscapeSequenceParser"/> (#95, one extract per PR).
/// Verbatim move — the parser's <c>StringBody</c> family, unchanged apart from
/// threading the owning parser explicitly (mutable string state stays owned
/// by the parser: <c>internal</c> fields and sinks, cf. <see cref="CsiDecoder"/>).
/// The parser keeps thin forwarders so <c>Step</c> dispatch is untouched;
/// caller migration and forwarder deletion are tracked in #1392.
/// (Bracketed-paste half follows in the next commit.)</summary>
internal static class OscHandler
{
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
}
