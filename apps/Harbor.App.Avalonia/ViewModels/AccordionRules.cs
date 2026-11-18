// AccordionRules.cs — K25: the expansion policy behind the HDS Accordion.
//
// The Avalonia control and its tests share this one definition of what a
// section toggle means. It is pure: no Avalonia app, no dispatcher, no XAML and
// no reflection, so the same code is safe under NativeAOT and can be pinned by
// a plain unit test. The TUI has its own AccordionPolicy in
// Harbor.Tui.CellForge; the desktop app does not reference that assembly, so
// the rule is expressed here for the Avalonia side rather than duplicated by
// name in a project that cannot reach it.

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Expansion policy of an HDS accordion.
/// </summary>
public enum AccordionMode
{
    /// <summary>At most one section is open; opening one closes the others.</summary>
    Single,

    /// <summary>Each section opens and closes independently.</summary>
    Multiple,
}

/// <summary>
///     Pure expansion rules for the HDS Accordion control (K25).
/// </summary>
/// <remarks>
///     <para>
///         Every method returns a NEW set and never mutates the one it was
///         given. The control owns the current set, hands it in, and installs
///         the returned set as the single source of truth. A disabled section
///         is a no-op: the control also turns its expander off, but the rule
///         refuses the toggle as well so a caller that bypasses the UI cannot
///         open it.
///     </para>
///     <para>
///         Ordinal comparison is deliberate. Section ids are stable keys, and
///         a culture-sensitive set could treat two distinct ids as one on a
///         machine whose locale folds case or punctuation.
///     </para>
/// </remarks>
public static class AccordionRules
{
    /// <summary>
    ///     The set after toggling <paramref name="id" />.
    /// </summary>
    /// <param name="expanded">The current expanded ids; may be null.</param>
    /// <param name="mode">Single or Multiple.</param>
    /// <param name="id">The id of the section the user toggled.</param>
    /// <param name="disabled">
    ///     Whether that section (or the whole accordion) is disabled. A
    ///     disabled toggle returns an unchanged copy.
    /// </param>
    /// <returns>A fresh set; the input is never modified.</returns>
    public static IReadOnlySet<string> NextExpanded(
        IReadOnlySet<string>? expanded,
        AccordionMode mode,
        string id,
        bool disabled = false)
    {
        var current = expanded ?? new HashSet<string>(StringComparer.Ordinal);
        var next = new HashSet<string>(current, StringComparer.Ordinal);
        if (disabled || string.IsNullOrEmpty(id)) return next;

        if (mode == AccordionMode.Single)
        {
            // Toggling the open section shuts it; toggling any other section
            // makes it the only open one. There is never a second id.
            return next.Contains(id)
                ? Empty()
                : new HashSet<string>([id], StringComparer.Ordinal);
        }

        if (!next.Remove(id)) next.Add(id);
        return next;
    }

    /// <summary>Whether <paramref name="id" /> is in the expanded set.</summary>
    public static bool IsExpanded(IReadOnlySet<string>? expanded, string id) =>
        expanded is not null && !string.IsNullOrEmpty(id) && expanded.Contains(id);

    /// <summary>
    ///     Reduce a set restored from a host (config, saved layout) to one the
    ///     given policy can honour. Single keeps the first id in input order
    ///     and drops the rest; Multiple leaves the set alone.
    /// </summary>
    public static IReadOnlySet<string> Normalize(IEnumerable<string> ids, AccordionMode mode)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (mode == AccordionMode.Multiple)
            return new HashSet<string>(ids, StringComparer.Ordinal);

        foreach (string id in ids)
        {
            if (!string.IsNullOrEmpty(id))
                return new HashSet<string>([id], StringComparer.Ordinal);
        }
        return Empty();
    }

    /// <summary>An empty, ordinal-comparer set, freshly allocated per call.</summary>
    public static IReadOnlySet<string> Empty() => new HashSet<string>(StringComparer.Ordinal);
}
