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
///     issue #23 slice 2): seed the DI-shared <see cref="SkillFreshnessModel" />
///     from the workspace at startup, re-seed it on <c>/skills refresh</c>,
///     and register the opt-in CellForge panel. Best-effort throughout — a
///     workspace without skills (or without a lockfile) is normal, never a
///     startup failure. There is deliberately no update dialog (slice 2 shows
///     pills only; applying updates stays manual).
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
            string projectSkills = Path.Combine(workingDirectory, ".harbor", "skills");
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
}
