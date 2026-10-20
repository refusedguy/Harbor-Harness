// PluginAllowance.cs — the ONE table that says "the plugin family may do the
// thing the rest of the product may not".
//
// WHY THIS FILE EXISTS
// --------------------
// Two rules need the same fact, and they key it differently:
//
//   1. The DYNAMIC-CODE BAN (#626, now RS0030 via BannedSymbols.DynamicCode.txt)
//      is a compiler rule. The compiler exempts a project by NAME, so the
//      predicate is `$(MSBuildProjectName.StartsWith('Harbor.Plugins.'))` in
//      Directory.Build.props. The exemption is expressed BY THE TOOL: the ban
//      file is not added to the plugin projects' AdditionalFiles at all.
//
//   2. The TRIM-SAFETY RULES (#414, TrimUnsafeReflectionRules) are a text scan.
//      A file scan exempts a file by PATH, so the predicate is a DIRECTORY
//      prefix — `src/Harbor.Plugins.`.
//
// Two spellings of one architectural fact is a drift waiting to happen: the
// compiler could exempt nine projects while the scan exempts eight, and the
// ninth would be a build error nobody could explain. So the fact is stated
// here, once, and `ReflectionConventionRule` asserts that the two spellings
// agree on the projects that actually exist.
//
// WHAT THE ALLOWANCE IS NOT
// -------------------------
// It is not a debt to be cleaned up. CS-source plugins are compiled in-memory
// by Roslyn and DLL plugins are loaded so a swap needs no host restart, which
// is why `Harbor.Plugins.Host` exists as a separate process. The allowance is
// a deliberate permission with a reason, so the next contributor reads it as a
// decision and does not "fix" it like a bug.
//
// It is also not a wide hole: one row, because the exemption is one
// architectural fact — plugins run code the host did not compile — not one per
// project. A new prefix is a new row, and a new row has to be justified in
// review (see `ReflectionConventionRule.The_Plugin_Allowance_Is_NonEmpty_Scoped_And_Explained`).

namespace Harbor.Architecture.Tests;

/// <summary>
///     The plugin family's exemption from the product-wide reflection rules,
///     shared by the rules that need it so there is one table and not two.
/// </summary>
internal static class PluginAllowance
{
    /// <summary>
    ///     The project-directory prefix the convention exempts, and why. One row
    ///     because the exemption is one architectural fact — plugins run code the
    ///     host did not compile — not one per project. The rule therefore cannot
    ///     be widened by appending a row: a new prefix is a new row, and a new row
    ///     has to be justified in review (see
    ///     <see cref="ReflectionConventionRule.The_Plugin_Allowance_Is_NonEmpty_Scoped_And_Explained" />).
    /// </summary>
    /// <remarks>
    ///     The prefix is a DIRECTORY prefix because the reader of this table is a
    ///     file scan. The compiler-side spelling of the same fact is a PROJECT-NAME
    ///     prefix and lives in Directory.Build.props, where the compiler reads it.
    ///     <see cref="ReflectionConventionRule" /> asserts the two agree.
    /// </remarks>
    internal static readonly (string Prefix, ExemptionReason.Row Allowance)[] AllowedProjectPrefixes =
    [
        (
            "src/Harbor.Plugins.",
            new ExemptionReason.Row(
                "Loading code at run time IS the plugin product: CS-source plugins are compiled "
                + "in-memory by Roslyn and DLL plugins are loaded so a swap needs no host restart, "
                + "which is why Harbor.Plugins.Host exists as a separate process. Not debt — the one "
                + "place the capability is wanted. Docs: docs/ARCHITECTURE_LAYERS.md §5.8.",
                TrackedBy: null)),
    ];

    /// <summary>Whether <paramref name="projectDir" /> sits under one of the allowed prefixes.</summary>
    /// <remarks>
    ///     internal since #414 — see <see cref="AllowedProjectPrefixes" />. The RULE
    ///     is unchanged and no construct was added; only the reader set grew, so
    ///     #626's own tests and its first-finding behaviour are untouched.
    /// </remarks>
    internal static bool IsAllowed(string projectDir) =>
        AllowedProjectPrefixes.Any(a => projectDir.StartsWith(a.Prefix, StringComparison.Ordinal));
}
