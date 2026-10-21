// WellKnownToolsRule.cs — the guard for issue #1564.
//
// WHY THIS FILE EXISTS
// --------------------
// #1564 moved the builtin tool names out of two hundred and ninety-two string
// literals and into one catalog, `WellKnownTools`. Two places still cannot read
// the catalog and keep a literal, because C# requires a compile-time constant
// there:
//
//   * a tool's own `[ToolInfo("name", ...)]` attribute argument, and
//   * a `case` label in a switch over a tool name.
//
// The attribute is the load-bearing holdout. It is the name the tool REGISTERS
// under, and the catalog is the name every other site looks the tool up by. If
// the two disagree, a permission rule about "bash" stops matching the tool
// that calls itself "bash" — silently, because a rule that matches nothing
// looks exactly like a rule that was never meant to fire.
//
// So this file asserts that the two agree, reading the tools' own declarations
// and the catalog's own values. The other half of the defect — a catalog name
// no tool registers — is the dead-row shape, and
// `BuiltinToolSafetyDeclarationsTests` already grades it against the composed
// host.
//
// WHY THE CATALOG IS READ BY REFLECTION
// -------------------------------------
// A hand-written list of the catalog's members would be a second copy of the
// thing this file exists to keep honest: a tool added to the catalog and
// forgotten here would pass every assertion below while the list said
// otherwise. Reflection reads the type itself, so the list cannot drift.
//
// NON-VACUITY
// -----------
// A guard that matches nothing is indistinguishable from a guard that is
// broken, and a broken guard is worse than none because it is believed. Two
// checks close that: the catalog must hold a real number of members, and the
// scan must find the tool implementation files that carry the declarations. The
// matcher is a regex over the attribute's own argument, so a planted
// declaration proves it can still fire.

using System.Text.RegularExpressions;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #1564: the catalog of builtin tool names and the tools' own
///     declarations must name the same tools, or a permission rule about a tool
///     stops matching the tool.
/// </summary>
public sealed class WellKnownToolsRule
{
    /// <summary>
    ///     A tool's own name declaration: the first argument of the
    ///     <c>[ToolInfo]</c> attribute. The argument list is written across
    ///     lines, so the whitespace between the parenthesis and the literal is
    ///     part of the shape.
    /// </summary>
    private static readonly Regex ToolInfoName = new(
        @"\[ToolInfo\(\s*""(?<name>[a-z][a-z0-9_]*)""",
        RegexOptions.Compiled);

    /// <summary>
    ///     The catalog's members and the names they hold, read off the type
    ///     itself so that this file cannot carry a second, drifting list.
    /// </summary>
    private static readonly (string Member, string Name)[] Catalog = ReadCatalog();

    /// <summary>
    ///     Every public <see cref="ToolName" /> field of the catalog, with the
    ///     name it holds.
    /// </summary>
    private static (string Member, string Name)[] ReadCatalog()
    {
        var rows = new List<(string, string)>();
        foreach (FieldInfo field in typeof(WellKnownTools).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(ToolName))
            {
                continue;
            }

            if (field.GetValue(null) is ToolName value)
            {
                rows.Add((field.Name, value.Value));
            }
        }

        return [.. rows];
    }

    /// <summary>
    ///     The catalog and the safety table describe the same set of tools.
    /// </summary>
    /// <remarks>
    ///     The safety table is the table every tool must have a row in, and its
    ///     tool column now reads the catalog. So the two agreeing means there
    ///     is exactly one place a builtin tool's name is written down: a tool
    ///     added to the catalog without a row in the table, or a row in the
    ///     table without a catalog member, is a tool whose name exists in two
    ///     places and can drift in one of them.
    /// </remarks>
    [Test]
    public async Task The_Catalog_And_The_Safety_Table_Name_The_Same_Tools()
    {
        HashSet<string> catalog = Catalog.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> table = BuiltinToolSafetyProfiles.All
            .Select(declaration => declaration.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        string[] missing = table.Except(catalog).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        string[] extra = catalog.Except(table).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        await Assert.That(missing).IsEmpty()
            .Because("the safety table declares a tool the catalog does not know: " + string.Join(", ", missing)
                     + ". The table reads its names from the catalog, so a name in one and not the other is a "
                     + "name written down twice — which is what this issue removed.");
        await Assert.That(extra).IsEmpty()
            .Because("the catalog declares a tool the safety table has no row for: " + string.Join(", ", extra)
                     + ". A tool with no row in the safety table is a tool no permission rule can guard, so the "
                     + "catalog and the table have to move together.");
    }

    /// <summary>
    ///     Every tool's own name declaration is a name the catalog knows.
    /// </summary>
    /// <remarks>
    ///     The <c>[ToolInfo]</c> argument is the one place a tool's name cannot
    ///     read the catalog, because an attribute argument has to be a
    ///     compile-time constant. This is the assertion that keeps the two
    ///     spellings of the same name in step: a tool that declares a name the
    ///     catalog does not have registers under a name that no permission rule
    ///     about it will ever match.
    /// </remarks>
    [Test]
    public async Task Every_Tool_Declares_A_Name_The_Catalog_Knows()
    {
        HashSet<string> catalog = Catalog.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (string file in ToolNameInventory.ToolImplementationFiles())
        {
            string? source = SourceScan.TryReadAllText(file);
            if (source is null)
            {
                continue;
            }

            foreach (Match match in ToolInfoName.Matches(SourceScan.StripComments(source)))
            {
                string name = match.Groups["name"].Value;
                if (catalog.Contains(name))
                {
                    continue;
                }

                problems.Add(
                    $"{SourceScan.Relative(file)} declares the tool \"{name}\", which the catalog does not "
                    + "know. The catalog is where every permission rule and every safety declaration looks "
                    + "the tool up, so a tool registered under a name the catalog does not have is a tool no "
                    + "rule about it can match.");
            }
        }

        await Assert.That(problems).IsEmpty().Because(string.Join("\n", problems));
    }

    /// <summary>
    ///     The catalog is not empty, the scan finds the tools, and the matcher
    ///     still fires.
    /// </summary>
    [Test]
    public async Task Discovery_Finds_The_Catalog_The_Tools_And_A_Matcher_That_Fires()
    {
        await Assert.That(Catalog.Length).IsGreaterThan(15)
            .Because("the catalog holds one member per builtin tool plus the plugin-vocabulary names; an "
                     + "almost-empty catalog means the rules above are satisfied by a tree that says nothing");

        await Assert.That(ToolNameInventory.ToolImplementationFiles().Count).IsGreaterThan(10)
            .Because("no tool implementation files were found, so the declaration scan read nothing and every "
                     + "assertion above passed for the wrong reason");

        const string planted = """
            [ToolInfo(
                "planted_tool",
                "Planted",
                "A declaration the matcher must see.")]
            """;

        await Assert.That(ToolInfoName.Matches(planted).Count).IsEqualTo(1)
            .Because("the matcher reads the tool's own name declaration; if it stopped matching that shape, the "
                     + "declaration scan would pass on an empty set and the guard would be decorative");
    }
}
