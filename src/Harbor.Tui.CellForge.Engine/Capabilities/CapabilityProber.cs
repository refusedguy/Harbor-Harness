using Harbor.Tui.CellForge.Input;

namespace Harbor.Tui.CellForge.Capabilities;

/// <summary>Transport seam so probing is testable without a real terminal:
/// writes sequences to the tty, waits for capability events routed by the
/// parser out of the input stream.</summary>
public interface ICapabilityProbeTransport
{
    Task SendAsync(string sequence, CancellationToken cancellationToken = default);

    /// <summary>Returns the next capability event or null on timeout.</summary>
    Task<CapabilityEvent?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// Kitty detection with explicit degradation ladder (design §2.4):
/// 1. send CSI ? u and wait (default 150 ms, configurable for slow SSH);
/// 2. answer → kitty active, remember reported flags;
/// 3. silence → fallback DECRQM ?2004$p: answer ⇒ VT-responsive legacy
///    terminal (degradation is deliberate and recorded, never silent);
/// 4. nothing ⇒ conservative defaults.
/// Multiplexer guardrail: inside tmux/screen the kitty push is skipped
/// entirely — passthrough wrappers are out of scope (§2.5).
/// </summary>
public sealed class CapabilityProber
{
    public static readonly TimeSpan DefaultKittyTimeout = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan DefaultFallbackTimeout = TimeSpan.FromMilliseconds(150);

    private readonly Func<string, string?> _environmentLookup;

    public CapabilityProber(Func<string, string?>? environmentLookup = null)
    {
        _environmentLookup = environmentLookup ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>True inside tmux/screen — kitty must not be pushed (§2.5/§6).</summary>
    public bool IsInsideMultiplexer()
    {
        var tmux = _environmentLookup("TMUX");
        var screen = _environmentLookup("STY");
        return !string.IsNullOrEmpty(tmux) || !string.IsNullOrEmpty(screen);
    }

    /// <summary>Pure mapper: capability events → capabilities. Testable golden path.</summary>
    public static TerminalCapabilities Evaluate(IReadOnlyList<CapabilityEvent> responses)
    {
        var caps = TerminalCapabilities.Unprobed() with { Probed = true };

        foreach (var response in responses)
        {
            switch (response.Kind)
            {
                case CapabilityEventKind.KittyFlagsReport:
                    caps = caps with { Kitty = true, VtResponsive = true, KittyFlags = response.Flags };
                    break;
                case CapabilityEventKind.DecRqmReport when response.Mode == TerminalQueries.BracketedPasteMode:
                    var confirmed = response.Value is 1 or 2;
                    caps = caps with { VtResponsive = true, BracketedPasteConfirmed = confirmed };
                    break;
                case CapabilityEventKind.DecRqmReport when response.Mode == TerminalQueries.SyncUpdatesMode:
                    var syncConfirmed = response.Value is 1 or 2;
                    caps = caps with { VtResponsive = true, SyncUpdates = syncConfirmed };
                    break;
                case CapabilityEventKind.DecRqmReport:
                    caps = caps with { VtResponsive = true };
                    break;
                case CapabilityEventKind.DeviceAttributes:
                case CapabilityEventKind.CursorPositionReport:
                    caps = caps with { VtResponsive = true };
                    break;
            }
        }

        return caps;
    }

    /// <summary>Runs the full detection ladder against the transport.</summary>
    public async Task<TerminalCapabilities> ProbeAsync(
        ICapabilityProbeTransport transport,
        TimeSpan? kittyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsInsideMultiplexer())
        {
            await transport.SendAsync(TerminalQueries.KittyQuery, cancellationToken).ConfigureAwait(false);
            var kittyAnswer = await transport.WaitAsync(kittyTimeout ?? DefaultKittyTimeout, cancellationToken).ConfigureAwait(false);
            if (kittyAnswer is { Kind: CapabilityEventKind.KittyFlagsReport } kittyReport)
            {
                return Evaluate([kittyReport]);
            }
        }

        // Fallback: distinguish "VT-aware without kitty" from "totally silent".
        await transport.SendAsync(TerminalQueries.DecRqmBracketedPaste, cancellationToken).ConfigureAwait(false);
        var decrqm = await transport.WaitAsync(DefaultFallbackTimeout, cancellationToken).ConfigureAwait(false);

        return decrqm is null
            ? TerminalCapabilities.Unprobed() with { Probed = true }
            : Evaluate([decrqm.GetValueOrDefault()]);
    }
}
