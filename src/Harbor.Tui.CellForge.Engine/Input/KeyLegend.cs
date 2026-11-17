// #2325: the '?' legend is DATA projected from the bindings registry, not a
// second hand-written keymap. BCL-only, zero Harbor references, like its
// sibling BindingsMap.cs — a panel builds a legend from its own (or a merged)
// BindingsMap and hands it to WhichKeyHelpOverlay; the key itself is still
// gated by WhichKeyHelpOverlay.HandleKey, the site declared in
// KeyGateFamilyRules (#833). No key is routed here, so this file adds no new
// gate site to that table and no new extension axis (#555).

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// One row of a registry-derived legend: the display key, its registered
/// description, and the binding's which-key group when it has one.
/// </summary>
public sealed record KeyLegendRow(string Key, string Description, string? Group);

/// <summary>
/// A key legend built from a <see cref="BindingsMap"/>: the rows a help
/// overlay prints and the compact bottom hint a panel prints for itself.
/// Everything comes from registered <see cref="Binding"/> metadata
/// (<see cref="Binding.DisplayKey"/>, <see cref="Binding.Description"/>,
/// <see cref="Binding.Group"/>) — there is no hardcoded hotkey text here.
/// <para>
/// The projection is a snapshot: rebinds in the source map need a new
/// <see cref="KeyLegend"/>, the same way <c>WhichKeyHelpOverlay</c> copies its
/// leader rows. Call with <c>BindingsChain.Merge(stack, app)</c> to show the
/// bindings the focused scope actually resolves.
/// </para>
/// Not thread-safe — render thread only, like <see cref="BindingsMap"/>.
/// </summary>
public sealed class KeyLegend
{
    /// <summary>
    /// The action id that identifies the help/dismiss binding in the registry.
    /// Only the lookup uses this id; the key and label printed are the binding's
    /// own <see cref="Binding.DisplayKey"/> and <see cref="Binding.Description"/>.
    /// </summary>
    public const string HelpAction = "help";

    private readonly List<KeyLegendRow> _rows;

    /// <summary>
    /// Projects <paramref name="bindings"/>' visible keys into legend rows and
    /// finds the help binding by its action id.
    /// </summary>
    /// <param name="bindings">The registry to project (focused-to-app merged by the caller).</param>
    /// <param name="helpAction">Action id of the help/dismiss binding.</param>
    public KeyLegend(BindingsMap bindings, string helpAction = HelpAction)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(helpAction);

        _rows = new List<KeyLegendRow>(bindings.Count);
        string? helpKey = null;
        string? helpDescription = null;

        // ShownKeys is the registry's own ordered view (priority desc, id asc),
        // so the legend cannot disagree with what a footer would print.
        foreach (Binding binding in bindings.ShownKeys())
        {
            _rows.Add(new KeyLegendRow(binding.DisplayKey, binding.Description, binding.Group));
            if (helpKey is null && string.Equals(binding.Action, helpAction, StringComparison.Ordinal))
            {
                helpKey = binding.DisplayKey;
                helpDescription = binding.Description;
            }
        }

        HelpKey = helpKey;
        HelpDescription = helpDescription;
    }

    /// <summary>Visible bindings in registry display order.</summary>
    public IReadOnlyList<KeyLegendRow> Rows => _rows;

    /// <summary>The registered gesture for the help action, null when unbound.</summary>
    public string? HelpKey { get; }

    /// <summary>The registered label for the help action, null when unbound.</summary>
    public string? HelpDescription { get; }

    /// <summary>
    /// Compact one-line hint (<c>"?  toggle this help panel"</c>) for a panel
    /// footer or status bar. Empty when the registry has no help binding, so a
    /// caller never has to invent a fallback key.
    /// </summary>
    public string Hint => HelpKey is null
        ? string.Empty
        : string.IsNullOrEmpty(HelpDescription)
            ? HelpKey
            : $"{HelpKey}  {HelpDescription}";
}
