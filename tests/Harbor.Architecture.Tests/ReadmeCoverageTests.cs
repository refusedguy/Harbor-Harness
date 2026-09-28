// ReadmeCoverageTests.cs — issue #430: per-project README coverage gate.
//
// The README is the only documentation a third party sees before
// `dotnet add package`, and the first thing AGENTS.md points a new agent at for
// a project's shape. Coverage used to live only as prose in docs/ROADMAP.md and
// had already drifted twice (32/51 recorded, 45/54 then 48/54 actually
// measured). A number in a document cannot hold itself true; a check can.
//
// Two gates:
//
//   1. Every src/**/*.csproj directory MUST have a README.md. No exceptions,
//      no grandfathering — this is the gap #430 was raised for.
//
//   2. Every PACKABLE project's README (<IsPackable>true</IsPackable>, i.e. the
//      ones a NuGet consumer will actually see) MUST carry the six required
//      sections from docs/standards/README-template.md. Pre-existing projects
//      that predate the template are recorded in LegacyNonConformantReadmes.
//
// The legacy list is a RATCHET, not a waiver — see
// Assert_LegacyEntriesAreAccurate.
//
// Runs as part of `dotnet build -c Release` via the HarborArchitectureGate
// target in Directory.Build.props, so a violation fails the build on CI without
// a separate test step.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Enforces that every <c>src/</c> project is documented, and that every
///     packable project's README satisfies the standard template.
/// </summary>
public class ReadmeCoverageTests
{
    /// <summary>
    ///     Sections a packable README must have, with the heading substrings
    ///     accepted for each. Kept in sync with docs/standards/README-template.md
    ///     by <see cref="Assert_TemplateExistsAndDocumentsTheSameSixSections" />.
    /// </summary>
    private static readonly (string Concept, string[] Accepted)[] RequiredSections =
    [
        ("what", new[] { "what", "overview", "назнач", "что ", "inside", "when to use" }),
        ("public", new[] { "public api", "public surface", "api surface", "what's in it", "что запускается", "files", "namespaces", "event →", "event ->" }),
        ("wiring", new[] { "wiring", "registration", "configuration", "architecture", "как включить", "how it works", "cycle", "цикл", "event →", "event ->", "platform support" }),
        ("usage", new[] { "usage", "quick start", "consuming", "example", "snippet", "как ", "build" }),
        ("deps", new[] { "dependenc", "зависим" }),
        ("limits", new[] { "limitation", "when not to use", "gotcha", "caveat", "known ", "ограничен", "notes" }),
    ];

    /// <summary>
    ///     Packable projects whose README predates the standard template, each
    ///     mapped to the sections it is still missing. Generated from the same
    ///     check on 2026-09-28; the audit trail is docs/standards/README-AUDIT.md.
    /// </summary>
    /// <remarks>
    ///     This list may only SHRINK. Two tests enforce that:
    ///     <list type="bullet">
    ///     <item>Assert_LegacyEntriesAreAccurate — an entry naming a section the README has since
    ///     gained, or a project that no longer exists / is no longer packable, fails.</item>
    ///     <item>Assert_PackableReadmesHaveRequiredSections — a project with no entry fails
    ///     immediately, so new projects are held to the full template.</item>
    ///     </list>
    ///     When you touch a listed README, drop its entry in the same PR.
    /// </remarks>
    private static readonly (string Project, string[] Missing)[] LegacyNonConformantReadmes =
    [
        ("Harbor.Abstractions", new[] { "wiring", "limits" }),
        ("Harbor.Abstractions.Contracts", new[] { "wiring" }),
        ("Harbor.Application", new[] { "wiring", "usage", "limits" }),
        ("Harbor.DesignSystem", new[] { "public", "wiring", "deps", "limits" }),
        ("Harbor.Desktop.Abstractions", new[] { "public", "wiring", "limits" }),
        ("Harbor.Desktop.Animations", new[] { "public", "wiring", "limits" }),
        ("Harbor.Desktop.Shared", new[] { "public", "wiring", "limits" }),
        ("Harbor.Diagnostics.Abstractions", new[] { "wiring" }),
        ("Harbor.Extensions", new[] { "wiring", "usage", "limits" }),
        ("Harbor.Hosting", new[] { "wiring" }),
        ("Harbor.Ipc.Abstractions", new[] { "public", "wiring", "usage", "deps", "limits" }),
        ("Harbor.Ipc.Client", new[] { "usage", "deps" }),
        ("Harbor.Ipc.InProcess", new[] { "usage", "deps" }),
        ("Harbor.Ipc.Server", new[] { "usage", "deps" }),
        ("Harbor.Plugins.Abstractions", new[] { "what", "wiring", "limits" }),
        ("Harbor.Plugins.Compilation", new[] { "what", "wiring", "limits" }),
        ("Harbor.Plugins.Hosting", new[] { "what", "wiring", "limits" }),
        ("Harbor.Plugins.Instantiation", new[] { "what", "wiring", "limits" }),
        ("Harbor.Plugins.Registration", new[] { "what", "wiring", "limits" }),
        ("Harbor.Plugins.Runtime", new[] { "what", "wiring", "limits" }),
        ("Harbor.Plugins.Storage", new[] { "what", "wiring", "limits" }),
        ("Harbor.Providers.Anthropic", new[] { "what", "limits" }),
        ("Harbor.Providers.Ollama", new[] { "what", "limits" }),
        ("Harbor.Providers.OpenAI", new[] { "what", "limits" }),
        ("Harbor.Providers.OpenAiCompatible", new[] { "what", "limits" }),
        ("Harbor.Registries", new[] { "wiring", "usage", "limits" }),
        ("Harbor.Storage.Jsonl", new[] { "what", "wiring", "limits" }),
        ("Harbor.Storage.Memory", new[] { "what", "wiring", "limits" }),
        ("Harbor.Storage.Sqlite", new[] { "what", "wiring", "limits" }),
        ("Harbor.Telemetry.Core", new[] { "wiring" }),
        ("Harbor.Telemetry.Otlp", new[] { "wiring" }),
        ("Harbor.Terminal.Abstractions", new[] { "wiring" }),
        ("Harbor.Tools.Builtin", new[] { "what", "wiring" }),
        ("Harbor.Tui.CellForge", new[] { "deps", "limits" }),
        ("Harbor.Ui.Framework", new[] { "wiring" }),
    ];

    /// <summary>
    ///     Every <c>src/</c> project ships a README. This is the gate that stops
    ///     the #430 gap from reopening: 6 projects had none, all of them the
    ///     newest and least documented.
    /// </summary>
    [Test]
    public async Task Assert_NoNewProjectLacksAReadme()
    {
        var missing = new List<string>();

        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            string dir = Path.GetDirectoryName(csproj)!;
            if (!File.Exists(RepoPaths.ReadmeFor(dir)))
            {
                missing.Add(Path.GetFileName(dir));
            }
        }

        await Assert.That(missing).IsEmpty()
            .Because($"every src/**/*.csproj needs a README.md per {RepoPaths.ReadmeTemplateRelativePath}. Missing: {string.Join(", ", missing)}");
    }

    /// <summary>
    ///     Every packable project's README carries all six required sections,
    ///     unless it is recorded in <see cref="LegacyNonConformantReadmes" />.
    /// </summary>
    [Test]
    public async Task Assert_PackableReadmesHaveRequiredSections()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in LegacyNonConformantReadmes)
        {
            known.Add(entry.Project);
        }

        var violations = new List<string>();

        foreach (string csproj in RepoPaths.EnumerateSrcProjects())
        {
            if (!RepoPaths.IsPackable(csproj))
            {
                continue;
            }

            string dir = Path.GetDirectoryName(csproj)!;
            string name = Path.GetFileName(dir);
            string readme = RepoPaths.ReadmeFor(dir);

            // The missing-file case belongs to the first gate; do not double-report.
            if (!File.Exists(readme) || known.Contains(name))
            {
                continue;
            }

            List<string> missing = MissingSections(File.ReadAllText(readme));
            if (missing.Count > 0)
            {
                violations.Add($"{name}: missing {string.Join(", ", missing)}");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because($"packable READMEs must carry the sections from {RepoPaths.ReadmeTemplateRelativePath}. Violations: {string.Join("; ", violations)}");
    }

    /// <summary>
    ///     The grandfather list cannot rot. An entry that names a section the
    ///     README has since gained must be removed; an entry for a project that
    ///     no longer exists, or that is no longer packable, must be removed too.
    ///     This is what makes the list shrink instead of accumulate.
    /// </summary>
    [Test]
    public async Task Assert_LegacyEntriesAreAccurate()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string project, string[] claimedMissing) in LegacyNonConformantReadmes)
        {
            if (!seen.Add(project))
            {
                problems.Add($"{project}: duplicated in LegacyNonConformantReadmes");
                continue;
            }

            string? csproj = RepoPaths.FindSrcProject(project);
            if (csproj is null)
            {
                problems.Add($"{project}: no src/ project with this name — drop the stale entry");
                continue;
            }

            if (!RepoPaths.IsPackable(csproj))
            {
                problems.Add($"{project}: is no longer packable — drop the stale entry");
                continue;
            }

            string readme = RepoPaths.ReadmeFor(Path.GetDirectoryName(csproj)!);
            if (!File.Exists(readme))
            {
                problems.Add($"{project}: has no README — the coverage gate owns that, drop the entry");
                continue;
            }

            List<string> actual = MissingSections(File.ReadAllText(readme));

            // Bidirectional: the recorded set must equal the measured set exactly.
            List<string> stale = claimedMissing.Where(m => !actual.Contains(m)).ToList();
            if (stale.Count > 0)
            {
                problems.Add($"{project}: now has {string.Join(", ", stale)} — fix the README and drop the entry");
            }

            List<string> unrecorded = actual.Where(m => !claimedMissing.Contains(m)).ToList();
            if (unrecorded.Count > 0)
            {
                problems.Add($"{project}: actually also missing {string.Join(", ", unrecorded)} — the entry understates the debt");
            }
        }

        await Assert.That(problems).IsEmpty()
            .Because($"LegacyNonConformantReadmes must be exact. Problems: {string.Join("; ", problems)}");
    }

    /// <summary>
    ///     The template is the source of truth the gate validates against, so it
    ///     has to exist and actually describe the same six concepts.
    /// </summary>
    [Test]
    public async Task Assert_TemplateExistsAndDocumentsTheSameSixSections()
    {
        string template = Path.Combine(RepoPaths.RepoRoot ?? ".", RepoPaths.ReadmeTemplateRelativePath);

        await Assert.That(File.Exists(template)).IsTrue()
            .Because($"{RepoPaths.ReadmeTemplateRelativePath} must exist as the source this gate validates against");

        string text = File.ReadAllText(template);
        List<string> undocumented = RequiredSections
            .Where(s => !text.Contains(s.Concept, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Concept)
            .ToList();

        await Assert.That(undocumented).IsEmpty()
            .Because($"{RepoPaths.ReadmeTemplateRelativePath} must name every concept the gate enforces. Undocumented: {string.Join(", ", undocumented)}");
    }

    /// <summary>
    ///     Which of the six required sections a README is missing. Headings are
    ///     matched per-heading, never against the joined document: an anchored
    ///     pattern applied to a joined list silently only ever matches the first
    ///     heading, which then reads as "everything is missing".
    /// </summary>
    private static List<string> MissingSections(string markdown)
    {
        var headings = new List<string>();
        foreach (Match m in Regex.Matches(markdown, @"^#{2,4}\s+(.*)$", RegexOptions.Multiline))
        {
            headings.Add(m.Groups[1].Value.Trim().ToLowerInvariant());
        }

        var missing = new List<string>();
        foreach ((string concept, string[] accepted) in RequiredSections)
        {
            bool found = headings.Any(h => accepted.Any(a => h.Contains(a, StringComparison.Ordinal)));
            if (!found)
            {
                missing.Add(concept);
            }
        }

        return missing;
    }
}
