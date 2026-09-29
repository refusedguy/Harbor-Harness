// ToolNameInventory.cs — the ONE place the builtin tool-name vocabulary is read
// out of the product, shared by every gate that has to reason about tool names.
//
// WHY THIS FILE EXISTS
// --------------------
// #595 audited "eight hand-maintained tool-name lists". A gate that hunts for
// those lists needs to know the vocabulary, and a gate that hard-codes the
// vocabulary is itself one more hand-maintained list that goes stale on the day
// a tool is added — the exact defect the gates exist to catch. So the vocabulary
// is DERIVED here, once, from the two sources that cannot drift silently:
//
//   * `BuiltinToolSafetyProfiles.All` — every builtin's declared safety profile.
//     Adding a tool without a row there is already a build-breaking test failure
//     (`BuiltinToolSafetyDeclarationsTests`).
//   * every `ToolName.Create("…")` literal in the tool implementations and the
//     sample plugins — the name a tool actually registers under, which is the
//     thing a lookup keyed by the wrong name misses.
//
// `ToolGlyphTableRule` (#680) grew the same pair of readers privately. Rather
// than let a second gate copy them, the readers live here and both gates call
// them. This is the same decision `SourceNullabilityScan.cs` records for the
// `null!` matchers: a gate that copies the scanner is free to drift from the
// rule it claims to enforce.
//
// WHAT IS DELIBERATELY NOT HERE
// -----------------------------
// No enumeration of the 20 builtin names, and no "known tools" list. A test
// that needs a specific name should say so in its own failure message; the
// vocabulary itself is read, never written.

using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Harbor.Abstractions.Permissions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The builtin tool-name vocabulary, derived from the product rather than
///     written down.
/// </summary>
internal static class ToolNameInventory
{
    /// <summary>
    ///     Every <c>ToolName.Create("…")</c> literal, so a plugin tool is covered
    ///     by name even though no builtin table mentions it.
    /// </summary>
    private static readonly Regex ToolNameLiteral = new(
        @"ToolName\.Create\(\s*""(?<name>[a-z][a-z0-9_]*)""",
        RegexOptions.Compiled);

    /// <summary>
    ///     Roots whose <c>*.cs</c> files declare tool names. <c>samples/</c> is in
    ///     the set on purpose: the CS-source plugins under <c>samples/plugins-cs</c>
    ///     are where <c>session_broadcast</c> / <c>session_inbox</c> live, and a
    ///     vocabulary that missed them would call two real tools nonexistent.
    /// </summary>
    private static readonly string[] ToolImplementationTrees =
    [
        "src/Harbor.Tools.Builtin",
        "samples/plugins",
        "samples/plugins-cs",
    ];

    /// <summary>
    ///     Every tool name the product knows, frozen and cached. Ordinal: a tool
    ///     name is lowercase by construction and the gates compare it exactly.
    /// </summary>
    internal static FrozenSet<string> Names { get; } = Collect();

    /// <summary>
    ///     The declared <see cref="ToolArgKind" /> per builtin name, for gates that
    ///     reason about the safety axis (e.g. "which tools take a path"). Reads the
    ///     same table the runtime fallback is built from, so a gate and the product
    ///     cannot disagree about it.
    /// </summary>
    internal static FrozenDictionary<string, ToolArgKind> ArgKinds { get; } = CollectArgKinds();

    /// <summary>
    ///     The <c>*.cs</c> files that declare tool names, sorted for a stable
    ///     failure message. Empty when the repository root is not discoverable
    ///     (a published test host) — every gate pairs its use with a
    ///     discoverability self-check so an empty set cannot pass vacuously.
    /// </summary>
    internal static IReadOnlyList<string> ToolImplementationFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var files = new List<string>();
        foreach (string relative in ToolImplementationTrees)
        {
            string dir = Path.Combine(root, relative);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            files.AddRange(Directory
                .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !SourceScan.IsBuildOutput(p)));
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static FrozenSet<string> Collect()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (ToolSafetyDeclaration declaration in BuiltinToolSafetyProfiles.All)
        {
            if (!string.IsNullOrWhiteSpace(declaration.ToolName))
            {
                names.Add(declaration.ToolName);
            }
        }

        foreach (string file in ToolImplementationFiles())
        {
            string? source = SourceScan.TryReadAllText(file);
            if (source is null)
            {
                continue;
            }

            foreach (Match match in ToolNameLiteral.Matches(source))
            {
                string name = match.Groups["name"].Value;
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }
        }

        return names.ToFrozenSet(StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, ToolArgKind> CollectArgKinds()
    {
        var kinds = new Dictionary<string, ToolArgKind>(StringComparer.OrdinalIgnoreCase);

        foreach (ToolSafetyDeclaration declaration in BuiltinToolSafetyProfiles.All)
        {
            if (!string.IsNullOrWhiteSpace(declaration.ToolName))
            {
                kinds[declaration.ToolName] = declaration.Profile.ArgKind;
            }
        }

        return kinds.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
