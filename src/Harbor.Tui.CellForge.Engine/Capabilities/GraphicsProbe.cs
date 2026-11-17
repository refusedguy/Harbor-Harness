namespace Harbor.Tui.CellForge.Capabilities;

/// <summary>
/// Raw-answer transport seam for the inline-graphics wire probe. Mirrors
/// <see cref="ICapabilityProbeTransport" />: the renderer owns the tty and
/// routes probe traffic here, while the probe itself stays a pure, testable
/// decision layer. Answers are the raw terminal strings because graphics
/// replies are APC/DA2 strings that are deliberately not key events.
/// </summary>
public interface IGraphicsProbeTransport
{
    Task SendAsync(string sequence, CancellationToken cancellationToken = default);

    /// <summary>Returns the next raw answer or null on timeout.</summary>
    Task<string?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// Protocol probe for the two graphics families that have a wire answer
/// (issue #2222): kitty graphics (<c>a=q</c> query, «OK» reply) and Sixel
/// (secondary device attributes, first parameter ≥ 1).
///
/// The environment-only <see cref="InlineImageProbe" /> stays the fallback:
/// a silent terminal can never be talked into a protocol it did not answer.
/// Both the sequence formatting and the answer parsing are pure functions, so
/// the probe is fully testable without a tty.
/// </summary>
public static class GraphicsProbe
{
    /// <summary>Default per-query answer window (slow SSH can raise it).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Maps the two raw answers onto a protocol. The kitty answer wins: a
    /// terminal that answers both prefers its native, cell-addressable APC
    /// graphics over the pixel-only Sixel fallback.
    /// </summary>
    /// <param name="kittyResponse">Raw kitty graphics query answer, or null on silence.</param>
    /// <param name="sixelResponse">Raw secondary device attributes answer, or null on silence.</param>
    /// <param name="fallback">Protocol to keep when neither answer confirms support.</param>
    public static InlineImageKind Evaluate(
        string? kittyResponse,
        string? sixelResponse,
        InlineImageKind fallback = InlineImageKind.None)
    {
        if (IsKittyGraphicsAnswer(kittyResponse))
        {
            return InlineImageKind.KittyApc;
        }

        return IsSixelAnswer(sixelResponse) ? InlineImageKind.Sixel : fallback;
    }

    /// <summary>Runs both queries against the transport. A confirmed kitty
    /// answer short-circuits the Sixel query — no reason to ask a second
    /// question once the first one answered.</summary>
    public static async Task<InlineImageKind> ProbeAsync(
        IGraphicsProbeTransport transport,
        InlineImageKind fallback = InlineImageKind.None,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);

        TimeSpan wait = timeout ?? DefaultTimeout;
        await transport.SendAsync(TerminalQueries.KittyGraphicsProbe, cancellationToken).ConfigureAwait(false);
        string? kitty = await transport.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        if (IsKittyGraphicsAnswer(kitty))
        {
            return InlineImageKind.KittyApc;
        }

        await transport.SendAsync(TerminalQueries.SixelProbe, cancellationToken).ConfigureAwait(false);
        string? sixel = await transport.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        return IsSixelAnswer(sixel) ? InlineImageKind.Sixel : fallback;
    }

    /// <summary>A kitty graphics answer carries the status token «OK».</summary>
    public static bool IsKittyGraphicsAnswer(string? response) =>
        response is not null && response.Contains("OK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// DA2 answer «CSI &gt; Ps ; … c»: the first parameter is 1 or greater when
    /// the terminal implements Sixel (xterm reports 1, 0 means "not built in").
    /// Anything else is silence or an unrelated report.
    /// </summary>
    public static bool IsSixelAnswer(string? response)
    {
        if (response is null)
        {
            return false;
        }

        int start = response.IndexOf('>');
        if (start < 0)
        {
            return false;
        }

        int value = 0;
        int digits = 0;
        for (int i = start + 1; i < response.Length; i++)
        {
            char c = response[i];
            if (c is >= '0' and <= '9')
            {
                value = (value * 10) + (c - '0');
                digits++;
                continue;
            }

            break;
        }

        return digits > 0 && value >= 1;
    }
}
