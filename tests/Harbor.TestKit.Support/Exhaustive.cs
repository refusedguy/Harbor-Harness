// Exhaustive.cs — the totality check for a table that maps the members of a
// closed enum to their expectations.
//
// WHY THIS EXISTS
// ---------------
// A hand-written test per enum member is exhaustive BY CONSTRUCTION: the
// member and its test arrive in the same commit, and the review that merges
// the member sees the row. Collapsing those methods into one data-driven
// table throws that away, because a table is a list and a list does not know
// what it forgot. Adding a member to the enum then leaves the table one row
// short and nothing fails — the coverage drops silently, which is the exact
// failure the per-member tests existed to prevent.
//
// This is the check that gives the property back.
//
// WHY THE COVERED SET IS PASSED IN AND NOT READ
// ----------------------------------------------
// The obvious implementation reads the covered members back out of the
// table's rows. That is not available: the rows of a data-driven test are
// compiled into the framework's own generated code, not into the attribute
// usage the test author wrote, so there is no public surface that carries
// them back.
//
// So the table is built from a DATA ARRAY, and the array is what the check
// is given:
//
//     internal static readonly Case[] Cases = [ (member, expectation), ... ];
//
//     [Test]
//     [MethodDataSource(nameof(Cases))]
//     public async Task TheTable(TEnum member, string expected) { ... }
//
//     [Test]
//     public async Task Every_Member_Has_A_Row()
//     {
//         Exhaustive.AssertAllCases<TEnum>(Cases.Select(c => c.Member).ToHashSet(), "...");
//     }
//
// The array IS the table — the rows come from it — so the set the check is
// given is the table's own membership and not a second list that has to be
// kept in step with the first. A member added to the enum has no row in the
// array, the check says so by name, and the fix is one row.
//
// WHERE IT LIVES AND WHY IT SURVIVES THE SPLIT
// ---------------------------------------------
// tests/Harbor.TestKit is being split (#1889): the UI doubles move out of
// the core into a UI-scoped sibling, and the core keeps the fixtures. This
// helper belongs to neither half — it is a fixture with no UI in it — so it
// goes in the closure-neutral sibling, Harbor.TestKit.Support, which the
// split does not touch. A helper in the core would also have worked, and
// would have dragged the core's product closure into every project that
// only wanted the check.

namespace Harbor.TestKit;

/// <summary>
///     The totality check for a table that maps the members of a closed enum
///     to their expectations.
/// </summary>
/// <remarks>
///     <para>
///         A table-driven test proves what its rows say. It does not prove
///         that it says everything: nothing in a row that names
///         <c>ToolCallState.Pending</c> notices that <c>ToolCallState</c>
///         grew a member. This type is what notices.
///     </para>
///     <para>
///         The covered set is the table's own membership — the members the
///         table's data array names — so the check cannot be satisfied by a
///         list somebody forgot to update, and a missing member is named in
///         the failure rather than counted.
///     </para>
/// </remarks>
public static class Exhaustive
{
    /// <summary>
    ///     Fails when a declared member of <typeparamref name="TEnum" /> is
    ///     missing from <paramref name="covered" />.
    /// </summary>
    /// <param name="covered">
    ///     The members the table covers. Pass the members of the table's own
    ///     data array, so that the set IS the table.
    /// </param>
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

        List<TEnum> missing = [];

        foreach (TEnum member in Enum.GetValues<TEnum>())
        {
            if (!covered.Contains(member))
            {
                missing.Add(member);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        throw new ExhaustivenessException(
            $"The table does not cover every member of {typeof(TEnum).Name}: "
            + $"{string.Join(", ", missing.Select(member => member.ToString()))} "
            + $"{(missing.Count == 1 ? "has" : "have")} no row. {because}");
    }
}
