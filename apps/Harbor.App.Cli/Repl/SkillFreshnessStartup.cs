using Harbor.Abstractions.Tui;
using Harbor.Application.Skills;
using Harbor.Terminal.Abstractions;
using Harbor.Tui.CellForge;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Projection;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     CLI host wiring for skill freshness (KILLER_FEATURES §2.7 Feature 10,
///     issue #23 slice 2 + issue #384): seed the DI-shared
///     <see cref="SkillFreshnessModel" /> from the workspace at startup,
///     re-seed it on <c>/skills refresh</c>, re-resolve stale skills on
///     <c>/skills update</c>, surface the aggregate pill in the status line
///     (default-on) and register the opt-in CellForge detail panel.
///     Best-effort throughout — a workspace without skills (or without a
///     lockfile) is normal, never a startup failure. The update path reuses
///     <see cref="SkillUpdater" />; a failed attempt never touches the model.
/// </summary>
internal static class SkillFreshnessStartup
{
    /// <summary>Project skills root relative to the working directory.</summary>
    internal const string ProjectSkillsRelativeDir = ".harbor/skills";

    /// <summary>Lockfile name probed in the working directory.</summary>
    internal const string LockFileName = "skills-lock.json";

    /// <summary>
    ///     Seed the shared model once at CLI startup. Never throws: a missing
    ///     model (uncomposed host) or any I/O failure yields 0 and the panel
    ///     keeps whatever snapshot it already has.
    /// </summary>
    /// <returns>Number of entries now in the model (0 when skipped).</returns>
    internal static int SeedFromServices(IServiceProvider services)
    {
        try
        {
            var model = services.GetService<SkillFreshnessModel>();
            if (model is null)
            {
                return 0;
            }

            return Refresh(model, Directory.GetCurrentDirectory()).Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    ///     Opt-in panel registration (env <c>HARBOR_SKILL_FRESHNESS=1</c>):
    ///     appends the freshness panel after the 9 builtins so the Alt+1..9
    ///     slot order stays pinned. Runs before the renderer initializes, so
    ///     the normal <c>EnsureSeeded</c> pass picks the panel up. No-op
    ///     unless the active renderer is CellForge. Never throws.
    /// </summary>
    internal static void TryRegisterPanel(IServiceProvider services)
    {
        try
        {
            if (!SkillFreshnessPanelRegistration.SkillFreshnessOptInEnabled())
            {
                return;
            }

            if (services.GetService<ITuiRenderer>() is not CellForgeTuiRenderer renderer)
            {
                return;
            }

            var model = services.GetService<SkillFreshnessModel>();
            if (model is null)
            {
                return;
            }

            renderer.Panels.RegisterSkillFreshness(model);
        }
        catch
        {
            // Opt-in must never break startup — the panel is decorative.
        }
    }

    /// <summary>
    ///     Build a <c>/skills refresh</c> delegate bound to the DI-shared
    ///     model (null when the host has no model — the slash handler then
    ///     reports "not available" instead of failing).
    /// </summary>
    internal static Func<IReadOnlyList<SkillFreshnessEntry>>? RefreshCommand(IServiceProvider services)
    {
        var model = services.GetService<SkillFreshnessModel>();
        if (model is null)
        {
            return null;
        }

        return () => Refresh(model, Directory.GetCurrentDirectory());
    }

    /// <summary>
    ///     Recompute snapshots from the workspace and replace the model
    ///     content. Never throws: on failure the previous snapshot stays and
    ///     is returned as-is.
    /// </summary>
    internal static IReadOnlyList<SkillFreshnessEntry> Refresh(SkillFreshnessModel model, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(model);
        try
        {
            string projectSkills = ResolveProjectSkillsDir(workingDirectory);
            string? globalSkills = ResolveGlobalSkillsDir();
            string lockPath = Path.Combine(workingDirectory, LockFileName);

            var snapshots = SkillFreshnessSeeder.Seed(projectSkills, globalSkills, lockPath);
            var entries = new List<SkillFreshnessEntry>(snapshots.Count);
            for (int i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                entries.Add(new SkillFreshnessEntry(snapshot.Name, snapshot.InstalledHash, snapshot.LockedHash));
            }

            model.SetSkills(entries);
            return entries;
        }
        catch
        {
            return model.GetEntries();
        }
    }

    /// <summary>Global skills root (<c>~/.harbor/skills</c>), or null when the home directory is unknown.</summary>
    private static string? ResolveGlobalSkillsDir()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ProjectSkillsRelativeDir);
    }

    /// <summary>
    ///     Build a <c>/skills update</c> delegate bound to the DI-shared model
    ///     (issue #384). The delegate resolves the target list against the
    ///     current snapshot — no args ⇒ every stale skill, named args ⇒ those
    ///     skills — and hands it to <see cref="SkillUpdater" />. Returns null
    ///     when the host has no model (the slash handler then reports "not
    ///     available"). Never throws: a report with
    ///     <see cref="SkillUpdateOutcome.Failed" /> is the worst outcome, and
    ///     the model is only reseeded after an
    ///     <see cref="SkillUpdateOutcome.Updated" />.
    /// </summary>
    internal static Func<IReadOnlyList<string>, Task<SkillUpdateReport>>? UpdateCommand(IServiceProvider services)
    {
        var model = services.GetService<SkillFreshnessModel>();
        if (model is null)
        {
            return null;
        }

        return names => UpdateAsync(model, names, Directory.GetCurrentDirectory());
    }

    /// <summary>
    ///     Resolve <paramref name="names" /> against the live snapshot and
    ///     re-resolve them. Empty/absent names ⇒ all stale entries; a name the
    ///     snapshot does not know is reported as unknown and never re-resolved.
    ///     The freshness model is left untouched unless the update succeeded.
    /// </summary>
    internal static async Task<SkillUpdateReport> UpdateAsync(
        SkillFreshnessModel model,
        IReadOnlyList<string>? names,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(model);

        var entries = model.GetEntries();
        var targets = new List<string>(names?.Count ?? entries.Count);
        var unknown = new List<string>();
        if (names is null || names.Count == 0)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].IsStale)
                {
                    targets.Add(entries[i].Name);
                }
            }

            if (targets.Count == 0)
            {
                return entries.Count == 0
                    ? SkillUpdateReport.NoOp("no skills installed — nothing to update.")
                    : SkillUpdateReport.NoOp($"all {entries.Count} skill(s) up to date — nothing to update.");
            }
        }
        else
        {
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i] ?? string.Empty;
                if (ContainsName(entries, name))
                {
                    targets.Add(name);
                }
                else
                {
                    unknown.Add(name);
                }
            }

            if (targets.Count == 0)
            {
                return SkillUpdateReport.NoOp($"unknown skill(s): {string.Join(", ", unknown)}");
            }
        }

        var report = await SkillUpdater
            .UpdateAsync(targets, ResolveProjectSkillsDir(workingDirectory), ResolveGlobalSkillsDir())
            .ConfigureAwait(false);

        if (report.Outcome == SkillUpdateOutcome.Updated)
        {
            // Reseed only after a real update — a failed or skipped attempt must
            // never let a stale skill look `current` (#384).
            Refresh(model, workingDirectory);
        }

        return unknown.Count == 0
            ? report
            : report with { Message = $"{report.Message} Skipped unknown: {string.Join(", ", unknown)}." };
    }

    /// <summary>
    ///     Project skills root for a working directory. Built from
    ///     <see cref="ProjectSkillsRelativeDir" /> so the seed path and the
    ///     update path can never drift apart.
    /// </summary>
    private static string ResolveProjectSkillsDir(string workingDirectory) =>
        Path.Combine(workingDirectory, ProjectSkillsRelativeDir);

    /// <summary>Case-insensitive name lookup in the current snapshot.</summary>
    private static bool ContainsName(IReadOnlyList<SkillFreshnessEntry> entries, string name)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
