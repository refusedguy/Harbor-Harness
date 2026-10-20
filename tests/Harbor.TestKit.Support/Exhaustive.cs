// Exhaustive.cs — the totality check for a table that maps the members of a
// closed enum to their expectations.
//
// WHY THIS EXISTS
// ---------------
// A hand-written test per enum member is exhaustive BY CONSTRUCTION: the
// member and its test arrive in the same commit, and the review that merges
// the member sees the row. Collapsing those methods into one [Arguments]
// table throws that away, because a table is a list and a list does not know
// what it forgot. Adding a member to the enum then leaves the table one row
// short and nothing fails — the coverage drops silently, which is the exact
// failure the per-member tests existed to prevent.
//
// This is the check that gives the property back.
//
// WHY IT READS THE TABLE AND NOT A SECOND LIST
// --------------------------------------------
// The obvious implementation takes the covered members as a parameter, and
// the caller writes them out as a list next to the table. That list is a
// second copy of the answer, and a second copy drifts: it can be updated
// without the table being updated, and then the check is green over a member
// no test covers. The guard would pass and the coverage would still be
// short — the worst of both.
//
// So the covered set is READ from the table's own [Arguments] rows. There is
// nothing to keep in step, because there is only one list.
//
// WHY THE ATTRIBUTES ARE READ THROUGH THEIR METADATA
// ---------------------------------------------------
// This project is the closure-neutral half of the test kit: it reaches
// Harbor.Abstractions and nothing else, so a test project can take the
// helper without dragging a product assembly into its closure. Naming
// TUnit.Core's ArgumentsAttribute would add a package reference to keep that
// true, and the package would have to be pinned in Directory.Packages.props
// — a file every dependency change in the repository touches.
//
// CustomAttributeData reads the same rows without a reference to the
// attribute's assembly. The attribute name is a public contract of TUnit
// (it has been public and stable since 1.0), and a table that carries no
// [Arguments] rows at all reads as EMPTY rather than as "everything is
// covered": the check then reports every member as missing, which is the
// loud failure and not the silent one.
//
// WHERE IT LIVES AND WHY IT SURVIVES THE SPLIT
// ---------------------------------------------
// tests/Harbor.TestKit is being split (#1889): the UI doubles move out of
// the core into a UI-scoped sibling, and the core keeps the fixtures. This
// helper belongs to neither half — it is a fixture with no UI in it — so it
// goes in the closure-neutral sibling, Harbor.TestKit.Support, which the
// split does not touch. A helper in the core would have been fine too, and
// would have dragged the core's product closure into every project that
// only wanted the check.

using System.Reflection;

namespace Harbor.TestKit;

/// <summary>
///     The totality check for a table that maps the members of a closed enum
///     to their expectations.
/// </summary>
/// <remarks>
///     <para>
///         A table-driven test proves what its rows say. It does not prove
///         that it says everything: nothing in
///         <c>[Arguments(ToolCallState.Pending, "pending")]</c> notices that
///         <c>ToolCallState</c> grew a member. This type is what notices.
///     </para>
///     <para>
///         The covered set is read from the table's own rows, so the check
///         cannot be satisfied by a list somebody forgot to update — and a
///         missing member is named in the failure rather than counted.
///     </para>
/// </remarks>
public static class Exhaustive
{
    /// <summary>
    ///     The full name of TUnit's inline-arguments attribute. It is read
    ///     from the metadata rather than named as a type, so that this
    ///     project keeps no reference to the test framework (see the file
    ///     header).
    /// </summary>
    private const string ArgumentsAttributeFullName = "TUnit.Core.ArgumentsAttribute";

    /// <summary>
    ///     Fails when a declared member of <typeparamref name="TEnum" /> has
    ///     no row in one of the tables.
    /// </summary>
    /// <param name="tables">
    ///     The tables to check, as (the class that declares the table, the
    ///     name of the method that carries the <c>[Arguments]</c> rows). A
    ///     <c>Type</c> cannot be an attribute argument, so a table cannot be
    ///     listed as an <c>[Arguments]</c> row of its own; this is the shape
    ///     that replaces it.
    /// </param>
    /// <param name="because">
    ///     The caller's own reason for the rule. It rides along in the
    ///     failure, because "something is wrong" is not a report.
    /// </param>
    /// <exception cref="ExhaustivenessException">
    ///     A declared member of <typeparamref name="TEnum" /> has no row, or
    ///     a table cannot be read at all.
    /// </exception>
    public static void AssertAllCases<TEnum>(
        IReadOnlyList<(Type Owner, string Method)> tables,
        string because)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(tables);

        foreach ((Type owner, string method) in tables)
        {
            MethodInfo tableMethod = ResolveTableMethod(owner, method, because);

            IReadOnlySet<TEnum> covered = CasesInTable<TEnum>(tableMethod);
            IReadOnlyList<TEnum> missing = MissingCases(covered);

            if (missing.Count == 0)
            {
                continue;
            }

            throw new ExhaustivenessException(
                $"The table '{owner.Name}.{method}' does not cover every member of "
                + $"{typeof(TEnum).Name}: {Describe(missing)} "
                + $"{(missing.Count == 1 ? "has" : "have")} no row. {because}");
        }
    }

    /// <summary>
    ///     Fails when a declared member of <typeparamref name="TEnum" /> is
    ///     missing from <paramref name="covered" />.
    /// </summary>
    /// <param name="covered">The members the table names.</param>
    /// <param name="because">
    ///     The caller's own reason for the rule. It rides along in the
    ///     failure, because "something is wrong" is not a report.
    /// </param>
    /// <exception cref="ExhaustivenessException">
    ///     A declared member of <typeparamref name="TEnum" /> has no row.
    /// </exception>
    public static void AssertAllCases<TEnum>(IReadOnlySet<TEnum> covered, string because)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(covered);

        IReadOnlyList<TEnum> missing = MissingCases(covered);

        if (missing.Count == 0)
        {
            return;
        }

        throw new ExhaustivenessException(
            $"The table does not cover every member of {typeof(TEnum).Name}: "
            + $"{Describe(missing)} {(missing.Count == 1 ? "has" : "have")} no row. {because}");
    }

    /// <summary>
    ///     The members of <typeparamref name="TEnum" /> named by the
    ///     <c>[Arguments]</c> rows of <paramref name="tableMethod" />.
    /// </summary>
    private static IReadOnlySet<TEnum> CasesInTable<TEnum>(MethodInfo tableMethod)
        where TEnum : struct, Enum
    {
        var covered = new HashSet<TEnum>();

        foreach (CustomAttributeData attribute in tableMethod.CustomAttributes)
        {
            if (!IsArgumentsAttribute(attribute.AttributeType))
            {
                continue;
            }

            foreach (CustomAttributeTypedArgument argument in attribute.ConstructorArguments)
            {
                // `ArgumentsAttribute(params object?[] values)` — the metadata
                // records ONE constructor argument: the array itself.
                if (argument.Value is object?[] values)
                {
                    foreach (object? value in values)
                    {
                        Add(value);
                    }
                }
                else
                {
                    Add(argument.Value);
                }
            }
        }

        return covered;

        void Add(object? value)
        {
            if (value is TEnum member)
            {
                covered.Add(member);
            }
        }
    }

    /// <summary>
    ///     The declared members of <typeparamref name="TEnum" /> that
    ///     <paramref name="covered" /> does not name.
    /// </summary>
    private static IReadOnlyList<TEnum> MissingCases<TEnum>(IReadOnlySet<TEnum> covered)
        where TEnum : struct, Enum
    {
        List<TEnum> missing = [];

        foreach (TEnum member in Enum.GetValues<TEnum>())
        {
            if (!covered.Contains(member))
            {
                missing.Add(member);
            }
        }

        return missing;
    }

    /// <summary>
    ///     The one method of <paramref name="tableOwner" /> named
    ///     <paramref name="tableMethodName" />.
    /// </summary>
    /// <remarks>
    ///     Exactly one, because two methods with the same name make the table
    ///     ambiguous — and an ambiguous table read as "no rows" would turn
    ///     the check into a failure that names a member nobody can find.
    /// </remarks>
    private static MethodInfo ResolveTableMethod(Type tableOwner, string tableMethodName, string because)
    {
        MethodInfo[] candidates =
        [
            .. tableOwner
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(method => string.Equals(method.Name, tableMethodName, StringComparison.Ordinal)),
        ];

        if (candidates.Length != 1)
        {
            throw new ExhaustivenessException(
                $"'{tableOwner.Name}' declares {candidates.Length} methods named '{tableMethodName}', "
                + $"so the table cannot be read. {because}");
        }

        return candidates[0];
    }

    /// <summary>
    ///     Whether <paramref name="attributeType" /> is TUnit's inline
    ///     arguments attribute, in either its plain or its generic form.
    /// </summary>
    private static bool IsArgumentsAttribute(Type attributeType) =>
        attributeType.FullName is { } fullName
        && (fullName == ArgumentsAttributeFullName
            || fullName.StartsWith(ArgumentsAttributeFullName + "`", StringComparison.Ordinal));

    /// <summary>
    ///     The missing members, as one readable list.
    /// </summary>
    private static string Describe<TEnum>(IReadOnlyList<TEnum> missing) =>
        string.Join(", ", missing.Select(member => member.ToString()));
}
