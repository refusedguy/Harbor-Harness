// EffectiveInterfaceSurface.cs — the EFFECTIVE member count of an interface:
// every member a consumer can reach through a reference typed as that
// interface, whether the member is declared on it or inherited from a base
// interface.
//
// WHY THIS FILE EXISTS
// --------------------
// The fat-interface ratchet (#471, `FatInterfaceShapeRatchet.cs`) counts
// DECLARED members. #1831's audit measured 124 public interfaces and found
// the hole this file closes:
//
//   ISessionManager   0 declared, 23 effective — a wide alias over four
//                     roles (ISessionQueries, ISessionLifecycle,
//                     ISessionStatusTracker, IPanelSessionGateway) that the
//                     ratchet called "narrow", exactly as it calls
//                     IThemeService narrow. The audit's words: "the ratchet
//                     counts DeclaredOnly → sees 0 and calls the alias
//                     'narrow' (precisely like IThemeService, which it
//                     praises)".
//
// The ratchet is not wrong about IThemeService — it declares nothing — but
// the number it freezes is the wrong number. A type that inherits a wide
// surface looks small, and a ratchet that only counts what a type declares
// cannot see the shape a consumer actually holds.
//
// A PACKAGE WAS CONSIDERED AND REJECTED
// -------------------------------------
// `aweXpect.Reflection` 3.1.0 (Testably, MIT) computes the same thing with
// one argument: `ContainsMethods(filter, MemberScope.IncludingInherited)`.
// It was not adopted; the reasons are in the PR body of #1879 rather than
// here: the package is a runtime ASSERTION library (a 1.3 MB DLL plus a
// 1.0 MB XML doc, no analyzer in it), it is single-authored, it drags
// `aweXpect.Core` and the full `aweXpect` package with it, and the
// capability being bought here is one `BindingFlags` flag and one walk over
// `Type.GetInterfaces()`. The IDEA is worth taking; the dependency is not.
//
// THE COUNTING RULE
// -----------------
// The effective member set of an interface T is the union, over T and every
// interface T transitively inherits, of T's INSTANCE members:
//
//   * a METHOD contributes its NAME;
//   * a PROPERTY contributes its NAME;
//   * an EVENT contributes its NAME.
//
// Four kinds are deliberately NOT counted, and each exclusion has a reason:
//
//   * STATIC members (static methods, static fields, constants). A static
//     member is reached through the TYPE, not through an instance of the
//     contract, and it is not an obligation on an implementor. The audit's
//     own table has the case: ICollapsibleChatBlock carries five static
//     helpers (DefaultCollapsedBodyLines, OverflowTail, CountLogicalLines
//     and two more) that are statics by design and are not part of the
//     instance surface.
//   * OPERATORS. C# requires an operator to be `public static`, so an
//     operator falls under the static exclusion; it is also sugar over a
//     named method, and the sugar is not an obligation anyone writes.
//   * NESTED TYPES. A nested type is a type, not a member of the contract;
//     it has its own name, its own file and its own lifetime.
//   * ACCESSORS. Reflection surfaces a property's `get_X`/`set_X` and an
//     event's `add_E`/`remove_E` as methods. Counting them would report
//     `IThemeReader` as four members instead of two and would make a
//     property look like it gained a member when it gained nothing.
//
// The unit is the member NAME, not the signature. Two reasons:
//
//   1. The audit's numbers are names, so the two can be compared: the audit
//      says ISessionManager is 23 effective and IThemeService is 12, and
//      this measurement reproduces both.
//   2. The ratchet already separates the two questions. The name count is
//      "how many capabilities does this contract carry", which is the ISP
//      question; the signature count is "how many obligations does an
//      implementor carry", and the ratchet grades that separately and
//      explicitly for the one type whose overload pairs matter
//      (IApprovalCoordinator, `ApprovalDistinctSignatures`).
//
// WHERE THE MEASUREMENT COMES FROM
// --------------------------------
// Two paths, because the tree has one interface the reflection path cannot
// reach:
//
//   * REFLECTION for a type in an assembly this test project references.
//     `Type.GetInterfaces()` is the transitive closure, so every base
//     interface's own members are included without a manual walk.
//   * SOURCE for an interface that lives in an APPLICATION (IReplHost is
//     `internal` to Harbor.App.Cli, which this test project does not
//     reference — the ratchet explains why at length). The source path
//     follows the base list BY NAME through the product tree and reports
//     INCOMPLETE when a base cannot be resolved, so a base that appears
//     later cannot silently shrink the measurement.
//
// The member DECLARATIONS come from one scanner, the ratchet's own
// (`FatInterfaceShapeRatchet.MeasureMembersFromSource`), so the two files
// cannot drift on what a declaration is.

// System.Text.RegularExpressions is not a global using in this project. The
// other rule files import it explicitly and so does this one.
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     The effective member surface of an interface: declared members plus the
///     members it inherits, under the counting rule documented in this file.
/// </summary>
internal static class EffectiveInterfaceSurface
{
    /// <summary>
    ///     The effective member NAMES of <paramref name="type" /> and of every
    ///     interface it transitively inherits, ordinal-sorted.
    /// </summary>
    /// <param name="type">The interface to measure.</param>
    /// <returns>The distinct member names. Empty when the type declares none and inherits none.</returns>
    internal static IReadOnlyList<string> Measure(Type type)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string name in DeclaredMemberNames(type))
        {
            names.Add(name);
        }

        // GetInterfaces() is the TRANSITIVE closure: the members of a base of
        // a base are included without a manual walk, and no member is counted
        // twice because the set collapses by name.
        foreach (var inherited in type.GetInterfaces())
        {
            foreach (string name in DeclaredMemberNames(inherited))
            {
                names.Add(name);
            }
        }

        return [.. names];
    }

    /// <summary>
    ///     The effective member NAMES of an interface declared in the product
    ///     tree, measured from source, for the interfaces that reflection
    ///     cannot reach.
    /// </summary>
    /// <param name="typeName">The simple name of the interface.</param>
    /// <returns>
    ///     The distinct member names, or <see langword="null" /> when the
    ///     measurement is INCOMPLETE — the interface was not found, or one of
    ///     its base interfaces could not be resolved. A caller must treat
    ///     <see langword="null" /> as a failure, never as "no members": a
    ///     measurement that silently returns nothing is a ratchet that is
    ///     green forever.
    /// </returns>
    internal static IReadOnlyList<string>? MeasureFromSource(string typeName)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        if (!CollectFromSource(typeName, names, visited))
        {
            return null;
        }

        return [.. names];
    }

    /// <summary>
    ///     Collects one interface's declared members and, recursively, its base
    ///     interfaces' members.
    /// </summary>
    /// <param name="typeName">The simple name of the interface.</param>
    /// <param name="names">The accumulator.</param>
    /// <param name="visited">The interfaces already visited, which stops a malformed tree from recursing forever.</param>
    /// <returns><see langword="false" /> when the interface or one of its bases could not be resolved.</returns>
    private static bool CollectFromSource(string typeName, SortedSet<string> names, HashSet<string> visited)
    {
        if (!visited.Add(typeName))
        {
            return true;
        }

        if (!SourceInterfaceMembers.IsDeclared(typeName))
        {
            return false;
        }

        foreach (string member in FatInterfaceShapeRatchet.MeasureMembersFromSource(typeName))
        {
            names.Add(member);
        }

        foreach (string baseName in SourceInterfaceMembers.BaseInterfaces(typeName))
        {
            if (!CollectFromSource(baseName, names, visited))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     The instance members a type DECLARES, by the counting rule: methods,
    ///     properties and events, with the property and event ACCESSORS and
    ///     every STATIC member excluded.
    /// </summary>
    /// <param name="type">The type to read.</param>
    /// <returns>The distinct member names, ordinal-sorted.</returns>
    private static IReadOnlyList<string> DeclaredMemberNames(Type type)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        // No BindingFlags.Static: statics are excluded by the counting rule,
        // and asking for them would only mean filtering them out again.
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            switch (member.MemberType)
            {
                case MemberTypes.Method:
                    // IsSpecialName is true for a property's accessors, an
                    // event's add/remove and every operator — all four are
                    // excluded by the counting rule.
                    if (member is MethodInfo method && method.IsSpecialName)
                    {
                        continue;
                    }

                    names.Add(member.Name);
                    break;

                case MemberTypes.Property:
                case MemberTypes.Event:
                    names.Add(member.Name);
                    break;

                default:
                    // Nested types and fields are not interface members, and
                    // the counting rule excludes them anyway.
                    break;
            }
        }

        return [.. names];
    }
}

/// <summary>
///     The source-side half of the effective-member measurement: it reads an
///     interface's declaration and its base list out of the product tree.
/// </summary>
/// <remarks>
///     This is deliberately narrow. It answers two questions — "which members
///     does this interface declare" (through the ratchet's own scanner, so
///     there is one definition of a declaration) and "which interfaces does
///     it inherit" — and nothing else.
/// </remarks>
internal static class SourceInterfaceMembers
{
    /// <summary>
    ///     Matches an interface declaration and captures its name and its base
    ///     list.
    /// </summary>
    private static readonly Regex InterfaceDeclaration = new(
        @"\binterface\s+(?<name>\w+)\s*(?:<[^>{;]*>)?\s*(?::\s*(?<bases>[^{;]+))?",
        RegexOptions.Compiled);

    /// <summary>Matches an identifier.</summary>
    private static readonly Regex Identifier = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    /// <summary>
    ///     Every interface the product tree declares, mapped to the interfaces
    ///     it inherits. One walk, computed once.
    /// </summary>
    /// <remarks>
    ///     The architecture suite pays for every tree walk: #1791 measures the
    ///     suite's cost and this file must not multiply it. The base lists are
    ///     collected in ONE pass and cached; the member declarations come from
    ///     the ratchet's own scanner, so there is still exactly one definition
    ///     of what a declaration is.
    /// </remarks>
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> BaseMap = new(BuildBaseMap);

    /// <summary>Every interface the product tree declares, and what it inherits.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Bases => BaseMap.Value;

    /// <summary>
    ///     Whether a product file declares an interface with this simple name.
    /// </summary>
    /// <param name="typeName">The simple name of the interface.</param>
    internal static bool IsDeclared(string typeName) => BaseMap.Value.ContainsKey(typeName);

    /// <summary>
    ///     The simple names of the interfaces <paramref name="typeName" />
    ///     inherits, read from the base list of its declaration.
    /// </summary>
    /// <param name="typeName">The simple name of the interface.</param>
    /// <remarks>
    ///     A base list can carry generic arguments and constraints; only the
    ///     identifiers are returned, and a base that names an interface the
    ///     tree does not declare makes the whole measurement INCOMPLETE
    ///     (the caller fails) rather than silently shrinking it.
    /// </remarks>
    internal static IReadOnlyList<string> BaseInterfaces(string typeName) =>
        BaseMap.Value.TryGetValue(typeName, out IReadOnlyList<string>? bases) ? bases : [];

    /// <summary>
    ///     Collects the base list of every interface the product tree declares.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildBaseMap()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(path) is not { } text)
            {
                continue;
            }

            string stripped = SourceScan.StripComments(text);
            foreach (Match declaration in InterfaceDeclaration.Matches(stripped))
            {
                string name = declaration.Groups["name"].Value;

                // The FIRST declaration of a name wins, the same way the
                // ratchet's own scanner picks the first file that declares an
                // interface.
                if (map.ContainsKey(name))
                {
                    continue;
                }

                var bases = new List<string>();
                if (declaration.Groups["bases"].Success)
                {
                    foreach (Match token in Identifier.Matches(declaration.Groups["bases"].Value))
                    {
                        bases.Add(token.Value);
                    }
                }

                map[name] = bases;
            }
        }

        return map;
    }
}
