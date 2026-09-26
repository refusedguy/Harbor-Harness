namespace Harbor.Ui.Framework.Projection;

// KILLER_FEATURES §2.7 Feature 10 (Orca `SkillFreshnessStatusPill.tsx`),
// issue #23 slice 1: pure skill-freshness model. A skill is "fresh" when the
// hash of its installed content matches the snapshot recorded in
// `skills-lock.json` (`computedHash`); any mismatch, missing install, or
// missing lock entry surfaces as a pill so the user knows an update (or a
// lockfile sync) is due. BCL-only, zero Harbor dependencies, zero rendering
// — any host (CellForge panel, Spectre overlay, Avalonia flyout) drives the
// model and paints from `PanelRows.SkillFreshnessRows`.

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

    /// <summary>Replace the snapshot (resets counts).</summary>
    public void SetSkills(IReadOnlyList<SkillFreshnessEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            _entries = new List<SkillFreshnessEntry>(entries);
        }
    }

    /// <summary>Drop the snapshot (empty panel).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries = new();
        }
    }
}
