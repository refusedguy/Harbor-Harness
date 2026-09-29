using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Projection;

/// <summary>
///     The status bar's <b>cells</b>, projected from <see cref="UiState" />
///     exactly once per snapshot — issue #488.
/// </summary>
/// <remarks>
///     <para>
///         Two renderers paint the status bar from the same
///         <see cref="UiState" />: <see cref="StatusProjector" /> (the
///         <c>UiStatusBarModel</c> consumed by Avalonia / Termina /
///         TerminalGui / SpectreTui) and <c>StatusProjectorPanel</c> (the
///         CellForge <c>StatusSeg</c> footer row). They used to disagree: the
///         second one called the first, read four of the six segments back out
///         by their <c>(Align, Importance)</c> coordinates, then rebuilt tokens
///         and cost from the raw state — throwing the two cells it had just
///         allocated away, under a second formatting rule that contradicted the
///         first.
///     </para>
///     <para>
///         This record is the fix: one derivation, one set of strings. Both
///         surfaces read the same cells here, so they cannot drift — the parity
///         is enforced by <c>StatusBarFactsParityTests</c>. Cell sizes and
///         styles remain each renderer's business; only the text is shared.
///     </para>
/// </remarks>
/// <param name="Chrome">Left route cell ("provider/model"), or null when the session has neither.</param>
/// <param name="Status">Center status cell with its glyph ("▌ running").</param>
/// <param name="StatusStyle">Style for <paramref name="Status" />; renderers map it to their own accent.</param>
/// <param name="Agent">Right agent cell ("agent code"), or null when no agent is named.</param>
/// <param name="Tokens">
///     Right token cell ("10.3K↑ 300↓") — the context the session occupies and
///     the output generated, or null when neither moved (#651). The input side
///     is deliberately NOT the session-cumulative input total: that is what the
///     provider bills, and printing it as "how full am I" made the counter grow
///     with the turn count while the prompt stood still.
/// </param>
/// <param name="Cost">Right cost cell ("$0.0042"), or null when nothing was spent.</param>
/// <param name="Scroll">Right scroll cell ("live" / "scroll 50%"); always present.</param>
public readonly record struct StatusBarFacts(
    string? Chrome,
    string Status,
    UiSpanStyle StatusStyle,
    string? Agent,
    string? Tokens,
    string? Cost,
    string Scroll)
{
    /// <summary>Projects the status-bar cells from a UI snapshot.</summary>
    /// <param name="state">Projected UI snapshot (source of truth).</param>
    public static StatusBarFacts Of(UiState state)
    {
        var cost = state.Chat.Cost;

        // No data ⇒ no cell: never a bare "/" separator, never a dangling
        // "provider/" or "/model". The projector used to emit the pair verbatim
        // and let the footer strip the empty half afterwards.
        string? chrome = string.IsNullOrEmpty(state.Chat.Provider) && string.IsNullOrEmpty(state.Chat.Model)
            ? null
            : string.IsNullOrEmpty(state.Chat.Provider)
                ? state.Chat.Model
                : string.IsNullOrEmpty(state.Chat.Model)
                    ? state.Chat.Provider
                    : state.Chat.Provider + "/" + state.Chat.Model;

        string status = state.Chat.Status;
        string glyph = status switch
        {
            "running" => "▌",
            "compacting" => "◐",
            "error" => "✗",
            _ => "○"
        };
        UiSpanStyle statusStyle = status switch
        {
            "running" => UiSpanStyle.Accent,
            "error" => UiSpanStyle.Danger,
            _ => UiSpanStyle.Default
        };

        int maxScroll = Math.Max(0, state.Ui.TotalLines - Math.Max(1, state.Ui.ViewportLines));

        return new StatusBarFacts(
            Chrome: chrome,
            Status: glyph + " " + status,
            StatusStyle: statusStyle,
            Agent: string.IsNullOrEmpty(state.Chat.AgentName) ? null : "agent " + state.Chat.AgentName,

            // #651: the input side of the cell is what the context OCCUPIES (the
            // last request's prompt tokens, the figure the ctx bar beside it has
            // read since #630), not the sum of every request's full input — that
            // sum is the bill, and on turn N it reads as N × the context. The
            // output side stays cumulative: nothing re-reads generated tokens,
            // so their total is a true one. The cost cell keeps the core's money
            // over the same sum (#653) — a cell that hid the sum would be
            // understating what the provider charged.
            Tokens: StatusBarText.TokensCell(
                ContextUsage.DisplayedInputTokens(cost.ContextTokens, cost.TokensIn),
                cost.TokensOut),
            Cost: StatusBarText.CostCell(cost.CostUsd, cost.IsCostUnpriced),
            Scroll: maxScroll == 0 ? "live" : $"scroll {state.Ui.ScrollOffset * 100 / maxScroll}%");
    }
}
