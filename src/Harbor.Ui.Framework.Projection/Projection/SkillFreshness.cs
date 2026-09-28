using System.Text;

namespace Harbor.Ui.Framework.Projection;

// KILLER_FEATURES §2.7 Feature 10 (Orca `SkillFreshnessStatusPill.tsx`),
// issue #23 slice 1: pure skill-freshness model. A skill is "fresh" when the
// hash of its installed content matches the snapshot recorded in
// `skills-lock.json` (`computedHash`); any mismatch, missing install, or
// missing lock entry surfaces as a pill so the user knows an update (or a
// lockfile sync) is due. BCL-only, zero Harbor dependencies, zero rendering
// — any host (CellForge panel, Spectre overlay, Avalonia flyout) drives the
// model and paints from `PanelRows.SkillFreshnessRows` (per-skill rows) or
// `SkillFreshnessAggregate` (one status-line pill, issue #384).

/// <summary>Freshness of one installed skill against the lockfile snapshot.</summary>
public enum SkillFreshnessStatus
{
    /// <summary>Installed hash matches the locked hash — nothing to do.</summary>
    Current,

    /// <summary>Installed content differs from the locked hash — update or re-sync due.</summary>
    Changed,

    /// <summary>Installed but absent from the lockfile — needs a lock entry.</summary>
    Untracked,

    /// <summary>Locked but not installed — needs installing.</summary>
    Missing,
}

/// <summary>
///     One skill row for the freshness pill (KILLER_FEATURES §2.7 Feature 10).
///     Carries everything a row renders: the skill name plus the installed and
///     locked content hashes — plus the derived <see cref="Status" /> and the
///     preformatted <see cref="PillText" /> so every renderer paints identical
///     pills.
/// </summary>
/// <param name="Name">Skill directory name (e.g. <c>"code-review"</c>).</param>
/// <param name="InstalledHash">Hash of the currently installed skill content, or null when not installed.</param>
/// <param name="LockedHash">Hash recorded in <c>skills-lock.json</c> (<c>computedHash</c>), or null when never locked.</param>
public sealed record SkillFreshnessEntry(string Name, string? InstalledHash, string? LockedHash)
{
    /// <summary>
    ///     Derived status: <c>Missing</c> when nothing is installed,
    ///     <c>Untracked</c> when no lock entry exists, <c>Current</c> on a
    ///     case-insensitive hash match, <c>Changed</c> otherwise. Surrounding
    ///     whitespace is ignored so lockfile formatting never flips the pill.
    /// </summary>
    public SkillFreshnessStatus Status =>
        string.IsNullOrWhiteSpace(InstalledHash) ? SkillFreshnessStatus.Missing
        : string.IsNullOrWhiteSpace(LockedHash) ? SkillFreshnessStatus.Untracked
        : string.Equals(InstalledHash.Trim(), LockedHash.Trim(), StringComparison.OrdinalIgnoreCase)
            ? SkillFreshnessStatus.Current
            : SkillFreshnessStatus.Changed;

    /// <summary>Single-pill text: <c>✓ current</c>, <c>● changed</c>, <c>? untracked</c>, <c>✗ missing</c>.</summary>
    public string PillText => Status switch
    {
        SkillFreshnessStatus.Current => "✓ current",
        SkillFreshnessStatus.Changed => "● changed",
        SkillFreshnessStatus.Untracked => "? untracked",
        SkillFreshnessStatus.Missing => "✗ missing",
        _ => "? untracked",
    };

    /// <summary>True when the pill asks for action (anything but <c>Current</c>).</summary>
    public bool IsStale => Status != SkillFreshnessStatus.Current;
}

/// <summary>
///     Pure skill-freshness model (KILLER_FEATURES §2.7 Feature 10, Orca
///     <c>SkillFreshnessStatusPill.tsx</c>). The host refreshes the snapshot
///     via <see cref="SetSkills" /> (installed hashes vs lockfile hashes);
///     the panel paints from <see cref="GetEntries" />. Internally locked so
///     <c>Build</c> (render thread) and host refreshes stay thread-safe.
/// </summary>
public sealed class SkillFreshnessModel
{
    private readonly object _gate = new();
    private List<SkillFreshnessEntry> _entries = new();

    /// <summary>Current snapshot (defensive copy).</summary>
    public IReadOnlyList<SkillFreshnessEntry> GetEntries()
    {
        lock (_gate)
        {
            return new List<SkillFreshnessEntry>(_entries);
        }
    }

    /// <summary>Number of entries whose pill asks for action.</summary>
    public int StaleCount
    {
        get
        {
            lock (_gate)
            {
                int stale = 0;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].IsStale)
                    {
                        stale++;
                    }
                }

                return stale;
            }
        }
    }

    /// <summary>
    ///     Monotonic snapshot counter (#384): bumped on every
    ///     <see cref="SetSkills" /> / <see cref="Clear" /> so a renderer can
    ///     re-derive the aggregate pill only when the snapshot actually moved
    ///     instead of rebuilding it on every frame. Read lock-free (volatile)
    ///     — a torn read only costs one frame of lag, never a wrong pill.
    /// </summary>
    public int Revision => Volatile.Read(ref _revision);

    private int _revision;

    /// <summary>Replace the snapshot (resets counts).</summary>
    public void SetSkills(IReadOnlyList<SkillFreshnessEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            _entries = new List<SkillFreshnessEntry>(entries);
            _ = Interlocked.Increment(ref _revision);
        }
    }

    /// <summary>Drop the snapshot (empty panel).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries = new();
            _ = Interlocked.Increment(ref _revision);
        }
    }
}

/// <summary>
///     One aggregate freshness pill for chrome/status-line surfaces (issue
///     #384): the count of skills asking for action, collapsed into a single
///     segment. Carries the style hint instead of a renderer-specific accent
///     so the Projection layer stays UI-vocabulary free — each renderer maps
///     <see cref="Style" /> to its own accent.
/// </summary>
/// <param name="Text">Compact label, e.g. <c>"skills ●2 changed"</c>.</param>
/// <param name="Style">Style hint for the label (Danger ⇒ something is missing).</param>
/// <param name="Changed">Skills whose installed hash differs from the lockfile.</param>
/// <param name="Missing">Locked skills that are not installed.</param>
/// <param name="Untracked">Installed skills with no lockfile entry.</param>
public sealed record SkillFreshnessSummary(
    string Text,
    UiSpanStyle Style,
    int Changed,
    int Missing,
    int Untracked)
{
    /// <summary>Total number of skills asking for action.</summary>
    public int Stale => Changed + Missing + Untracked;
}

/// <summary>
///     Builds the aggregate <see cref="SkillFreshnessSummary" /> from a
///     snapshot (issue #384). <b>Nothing stale ⇒ no pill at all</b> (returns
///     <see langword="null" />), matching the status line's no-data-means-no-
///     segment contract: a clean workspace never loses a single cell of the
///     footer. Text is <c>skills</c> followed by the non-zero buckets in a
///     fixed order — <c>skills ●2 changed ✗1 missing ?3 untracked</c>.
/// </summary>
public static class SkillFreshnessAggregate
{
    /// <summary>Label prefix of the aggregate pill.</summary>
    public const string Label = "skills";

    /// <summary>
    ///     Aggregate over <paramref name="entries" />, or <see langword="null" />
    ///     when every entry is <see cref="SkillFreshnessStatus.Current" />.
    /// </summary>
    public static SkillFreshnessSummary? Of(IReadOnlyList<SkillFreshnessEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        int changed = 0;
        int missing = 0;
        int untracked = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            switch (entries[i].Status)
            {
                case SkillFreshnessStatus.Changed:
                    changed++;
                    break;
                case SkillFreshnessStatus.Missing:
                    missing++;
                    break;
                case SkillFreshnessStatus.Untracked:
                    untracked++;
                    break;
                case SkillFreshnessStatus.Current:
                default:
                    // Current skills never contribute to the pill — that is what
                    // makes a clean snapshot render nothing at all.
                    break;
            }
        }

        if (changed + missing + untracked == 0)
        {
            return null;
        }

        // Capacity-only ctor: the label is appended below, together with the
        // separator that keeps it off the first bucket's glyph.
        var text = new StringBuilder(Label.Length + 32);
        text.Append(Label).Append(' ');
        Append(text, "●", changed, "changed");
        Append(text, "✗", missing, "missing");
        Append(text, "?", untracked, "untracked");

        // Missing wins the accent (a locked-but-absent skill is the strongest
        // "your workspace is broken" signal), then changed, then untracked.
        var style = missing > 0
            ? UiSpanStyle.Danger
            : changed > 0
                ? UiSpanStyle.Accent
                : UiSpanStyle.Dim;

        return new SkillFreshnessSummary(text.ToString(), style, changed, missing, untracked);
    }

    /// <summary>
    ///     Appends one <c>glyph N bucket</c> part, space-separated from the
    ///     already-written ones. The label is written by the caller together
    ///     with the first separator, so the label can never run into a glyph.
    /// </summary>
    private static void Append(StringBuilder sb, string glyph, int count, string bucket)
    {
        if (count <= 0)
        {
            return;
        }

        if (sb.Length > Label.Length + 1)
        {
            sb.Append(' ');
        }

        sb.Append(glyph).Append(count).Append(' ').Append(bucket);
    }
}
