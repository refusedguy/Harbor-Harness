// DefaultInterfaceMemberRule.cs — GUARD #2 for docs/PATTERNS.md §"Default
// interface members: a default must fail towards loudly-wrong" (issue #579).
//
// THE CONVENTION BEING ENFORCED
// -----------------------------
// One sentence, quoted from docs/PATTERNS.md:
//
//   A default interface member must fail towards LOUDLY WRONG, never towards
//   SILENTLY FINE.
//
//   Conservative defaults are allowed: `false`, `Mandatory`, `Task.CompletedTask`
//   on a hook nobody calls, an identity fold, a deliberate no-op that says
//   "not my business".
//
//   PERMISSIVE defaults are banned: `Result.Success()` (accepts any input),
//   a `Task` that completes having done nothing while the caller asked for an
//   error callback, and — the sharpest one — a default that ROUTES TO THE
//   OLDER, INFORMATION-LOSING OVERLOAD of the same interface.
//
// That last shape is the `IEventBusMiddleware.SinkKind` trap #496 fixed and
// #579 found unfixed in `IThemeWatcher.Watch(path, onError) => Watch(path)`:
// an implementer written before the new member existed compiles, satisfies the
// new contract, and silently swallows every error the caller explicitly asked
// to see.
//
// WHY A WHITELIST AND NOT A HEURISTIC
// ----------------------------------
// A whitelist of *reviewed members*, each annotated with the direction it
// fails in. Two properties fall out, and they are the whole point:
//
//   * a NEW default is a compile-clean addition that nobody reviewed — the test
//     goes red until somebody writes down which way it fails;
//   * a default that flips direction (a conservative one becomes permissive)
//     is caught by Assert_EveryReviewedDefault_FailsInTheStatedDirection,
//     which re-derives the direction from the method's own signature and
//     return value rather than trusting the annotation.
//
// The annotation is therefore a CLAIM, and the test checks the claim. A
// whitelist that is only a list of names is a comment.
//
// NON-VACUITY — WHY THIS FILE IS NOT A COMMENT
// --------------------------------------------
// The trap, again from PresentationCapabilityRules.cs: NetArchTest's
// `NotHaveDependencyOn(name)` is satisfied by a name that matches nothing, so a
// rule pointing at a typo'd assembly is green forever. Here the same mistake has
// a second, sharper form: a scan that finds NO default interface members is
// indistinguishable from a correct scan, and the whitelist — which is what
// makes a permissive default a failing test — would then be enforcing nothing
// while looking armed.
//
// So:
//   1. AssembliesUnderTest_AreLiveAndResolve — every assembly prefix the
//      convention names must actually be LOADED and must contain interfaces.
//      A typo'd prefix fails here, not vacuously.
//   2. The scan must find at least the known members — asserted by count AND
//      by name, against the eight defaults #579 inventoried.
//   3. NonVacuity_PermissiveDefault_IsDetected — the positive control: a
//      synthetic interface declared in THIS file, with the exact permissive
//      shape the convention bans (`Result Validate(...) => Result.Success()`).
//      The direction classifier MUST call it permissive. If the classifier
//      degrades to "everything is conservative", this test is red before the
//      rule can pass it.

using System.Collections.Concurrent;
using System.Reflection;
using CSharpFunctionalExtensions;

namespace Harbor.Architecture.Tests;

/// <summary>Which way a default interface member fails when the implementer omits it.</summary>
internal enum DimDirection
{
    /// <summary>
    ///     The default is the direction in which NOTHING silently goes wrong:
    ///     <c>false</c>, a conservative enum value, a deliberate no-op whose
    ///     absence is observable. Allowed.
    /// </summary>
    Conservative,

    /// <summary>
    ///     The default is the direction in which SOMETHING silently goes wrong:
    ///     it accepts input it should reject, swallows a requested error
    ///     callback, or reports success for work never done. Banned — each
    ///     instance needs a tracking issue.
    /// </summary>
    Permissive,
}

/// <summary>One default interface member and the direction it fails in.</summary>
/// <param name="TypeName">Full CLR name of the declaring interface.</param>
/// <param name="MemberName">Method or property name.</param>
/// <param name="Direction">The direction the default fails in, as reviewed.</param>
/// <param name="Rationale">Why that direction is the right one, in one line.</param>
/// <param name="TrackedBy">
///     Required for <see cref="DimDirection.Permissive" />: the issue that owns
///     removing it. Empty for conservative defaults.
/// </param>
internal sealed record ReviewedDefault(
    string TypeName,
    string MemberName,
    DimDirection Direction,
    string Rationale,
    string TrackedBy);

/// <summary>
///     One default interface member found by reflection, with everything the
///     classifier needs to decide its direction.
/// </summary>
/// <param name="TypeName">Full CLR name of the declaring interface.</param>
/// <param name="MemberName">
///     Property or method name. A default PROPERTY is reported under its
///     property name, never its accessor: reflection only ever sees
///     <c>get_SinkKind</c>, and normalising in exactly one place keeps the
///     reviewed list reading like the source does.
/// </param>
/// <param name="ReturnType">Return type, for the classifier.</param>
/// <param name="ParameterCount">
///     Declared parameter count. Carried so the forwarder-overload rule can
///     decide "this richer overload has a poorer sibling to delegate to"
///     without reading a method body.
/// </param>
/// <param name="IsAbstract">
///     True for the interface's own contract members. The forwarder rule NEEDS
///     these: <c>IThemeWatcher.Watch(string)</c> is abstract, and it is the
///     poorer contract the richer default routes to — a scan that kept only the
///     defaults would never see the pair and would score the live defect as
///     conservative.
/// </param>
/// <param name="IsVoid">Whether the member returns void.</param>
/// <param name="IsStatic">Whether the member is static.</param>
internal sealed record FoundDefault(
    string TypeName,
    string MemberName,
    Type ReturnType,
    int ParameterCount,
    bool IsAbstract,
    bool IsVoid,
    bool IsStatic);

/// <summary>Finds default interface members and decides which way each one fails.</summary>
internal static class DefaultInterfaceMemberProbe
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<FoundDefault>> Cache =
        new(StringComparer.Ordinal);

    /// <summary>
    ///     A default interface member is an interface method that is NOT
    ///     abstract — i.e. one the interface itself provides a body for.
    ///     Reflection is exact here: Roslyn compiles a DIM to a non-abstract
    ///     method on the interface, and an interface method with no body to an
    ///     abstract one. There is no source-text heuristic to get wrong.
    /// </summary>
    public static IReadOnlyList<FoundDefault> Scan(Assembly asm)
    {
        return Cache.GetOrAdd(asm.GetName().Name ?? asm.FullName ?? asm.ToString(), _ => ScanAll(SafeGetTypes(asm)));
    }

    /// <summary>
    ///     Every interface method on <paramref name="types" /> — defaults AND
    ///     contract members — so the forwarder rule can see a default standing
    ///     next to the abstract overload it delegates to.
    /// </summary>
    /// <param name="types">Types to walk.</param>
    public static IReadOnlyList<FoundDefault> ScanAll(IEnumerable<Type> types)
    {
        var found = new List<FoundDefault>();
        foreach (Type type in types)
        {
            if (!type.IsInterface)
            {
                continue;
            }

            string typeName = type.FullName ?? type.Name;

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                // Contract members are scanned too, not skipped: the forwarder
                // rule is about a DEFAULT standing next to the ABSTRACT poorer
                // overload it delegates to, so both halves have to be here.
                // IsAbstract on the record is what separates them.
                //
                // A default property's getter and a default method both arrive
                // here. Only accessors are renamed, and only when the declaring
                // interface really declares that property — a get_/set_ method
                // with no property behind it is left alone rather than given an
                // invented name.
                string memberName = method.Name;
                if (method.IsSpecialName
                    && (memberName.StartsWith("get_", StringComparison.Ordinal)
                        || memberName.StartsWith("set_", StringComparison.Ordinal)))
                {
                    string propertyName = memberName[4..];
                    bool isProperty = type
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.DeclaredOnly)
                        .Any(p => p.Name == propertyName);
                    if (isProperty)
                    {
                        memberName = propertyName;
                    }
                }

                found.Add(new FoundDefault(
                    typeName,
                    memberName,
                    method.ReturnType,
                    method.GetParameters().Length,
                    method.IsAbstract,
                    method.ReturnType == typeof(void),
                    method.IsStatic));
            }
        }

        return found;
    }

    /// <summary>
    ///     Classifies a default by its own shape. Four permissive shapes are
    ///     decidable from the signature and the interface's own member list —
    ///     no source parsing, no guessing:
    ///
    ///     <b>1. A forwarder overload.</b> The member is an overload of a
    ///     sibling member of the SAME interface and takes strictly MORE
    ///     parameters. That is the <c>IThemeWatcher.Watch(path, onError)
    ///     =&gt; Watch(path)</c> shape, and it is the same defect class as the
    ///     <c>IEventBusMiddleware.SinkKind</c> trap #496 fixed: a richer
    ///     contract whose default routes to the poorer one, so an implementer
    ///     written before the richer member existed compiles and silently drops
    ///     the extra information. This is the shape the convention names first
    ///     and the only one decidable without reading a body.
    ///
    ///     <b>2. Returns <c>Result</c>.</b> The member is a validator and the
    ///     only sane default is <c>Success</c> — accept any input. This is
    ///     <c>ITool.ValidateArguments</c> (#579 row 3).
    ///
    ///     <b>3. Returns <c>Task</c>/<c>ValueTask</c> and the name reads as a
    ///     handler</b> (<c>Handle*</c>, <c>On*</c>, <c>Run*</c>) — a
    ///     completed-without-doing-anything default. This is
    ///     <c>ITuiView.OnEventAsync</c> (#579 row 4), whose hook has ZERO
    ///     callers, so it is not merely permissive but dead, and
    ///     <c>ITuiRenderer.RunInteractiveAsync</c>, which reports exit code 0
    ///     for a loop it never ran.
    ///
    ///     <b>4. Nothing else is permissive.</b> A static default is a pure
    ///     derivation whatever it returns; <c>false</c>, a conservative enum, an
    ///     identity fold and a void no-op that only flips its own state are the
    ///     direction #579 rows 1/5/7 bless.
    /// </summary>
    /// <param name="member">The default to classify.</param>
    /// <param name="siblings">
    ///     Every OTHER member of the same interface — defaults AND contract
    ///     members, which is what makes the forwarder rule work.
    /// </param>
    public static DimDirection Classify(FoundDefault member, IReadOnlyList<FoundDefault> siblings)
    {
        // (1) Forwarder overload: a richer default with a poorer sibling of the
        // SAME name to route to. Parameter counts are carried on the record
        // precisely so this never has to read a method body.
        if (siblings.Any(other =>
                other.MemberName == member.MemberName
                && other.ParameterCount < member.ParameterCount))
        {
            return DimDirection.Permissive;
        }

        if (member.IsStatic)
        {
            return DimDirection.Conservative;
        }

        bool isResult = member.ReturnType == typeof(Result)
            || (member.ReturnType.IsGenericType
                && member.ReturnType.GetGenericTypeDefinition() == typeof(Result<>));
        if (isResult)
        {
            return DimDirection.Permissive;
        }

        bool returnsTask = member.ReturnType == typeof(Task)
            || member.ReturnType == typeof(ValueTask)
            || (member.ReturnType.IsGenericType
                && (member.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                    || member.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>)));

        if (returnsTask
            && (member.MemberName.StartsWith("Handle", StringComparison.Ordinal)
                || member.MemberName.StartsWith("On", StringComparison.Ordinal)
                || member.MemberName.StartsWith("Run", StringComparison.Ordinal)))
        {
            return DimDirection.Permissive;
        }

        return DimDirection.Conservative;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Types that DID load are still worth classifying; the ones that did
            // not are a broken input, and the liveness test reports the
            // resulting shortfall rather than hiding it.
            return ex.Types.Where(static t => t is not null).Select(static t => t!);
        }
        catch (FileNotFoundException)
        {
            // An assembly whose dependencies are not next to the test host
            // cannot be read. Returning nothing here would make the rule pass
            // vacuously, so the governing liveness test
            // (NonVacuity_Scan_FindsTheKnownDefaults) is what keeps that honest.
            return [];
        }
    }
}

/// <summary>
///     Guard for docs/PATTERNS.md §"Default interface members": every default
///     interface member in <c>Harbor.Abstractions*</c>,
///     <c>Harbor.Terminal.Abstractions</c> and <c>Harbor.Ui.Framework.*</c> is
///     on a reviewed list, and the direction it actually fails in matches the
///     direction the list claims.
/// </summary>
public sealed class DefaultInterfaceMemberRule
{
    /// <summary>
    ///     Assembly prefixes the convention governs, with the reason each is in
    ///     scope. Prefixes rather than exact names on purpose: a new
    ///     <c>Harbor.Ui.Framework.*</c> project is covered the day it lands.
    /// </summary>
    private static readonly string[] GovernedPrefixes =
    [
        "Harbor.Abstractions",
        "Harbor.Abstractions.Contracts",
        "Harbor.Terminal.Abstractions",
        "Harbor.Ui.Framework.",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, Assembly>> LoadedAssemblies =
        new(ArchitectureTestHelpers.LoadHarborAssemblies);

    /// <summary>
    ///     Every governed interface member: defaults AND contract members. The
    ///     rules below filter to the defaults themselves; the forwarder rule
    ///     needs the whole list.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<FoundDefault>> FoundDefaults = new(Collect);

    /// <summary>The defaults only — the set the convention is about.</summary>
    private static IReadOnlyList<FoundDefault> Defaults =>
        FoundDefaults.Value.Where(static m => !m.IsAbstract).ToList();

    /// <summary>
    ///     Classifies a member with its interface siblings in view. Every
    ///     classification in the rules goes through here, so the forwarder rule
    ///     cannot be bypassed by calling the probe with a hand-picked list.
    /// </summary>
    private static DimDirection Classify(FoundDefault member)
    {
        var siblings = FoundDefaults.Value
            .Where(other => other.TypeName == member.TypeName)
            .ToList();

        return DefaultInterfaceMemberProbe.Classify(member, siblings);
    }

    /// <summary>
    ///     The reviewed list. Eight of the nine rows are #579's inventory; the
    ///     ninth (<c>ITokenTracker.RecordAppendedMessage</c>, a documented
    ///     no-op added so existing implementors keep compiling) is the same
    ///     class of member and is classified conservative by the probe.
    ///     <c>ITuiRenderer.RunInteractiveAsync</c> is a tenth: a static-ish
    ///     line-buffered stub that returns 0 — the "worked, nothing to do"
    ///     answer, which is conservative precisely because the value is
    ///     truthful.
    /// </summary>
    private static readonly ReviewedDefault[] Reviewed =
    [
        new(
            "Harbor.Abstractions.Events.IEventBusMiddleware",
            "SinkKind",
            DimDirection.Conservative,
            "Defaults to Mandatory: an undeclared sink keeps the bus on its full path. #496 fixed "
            + "the DIM-forwarder trap here and the verdict table is normative in "
            + "docs/EVENT_BUS_SINKS.md §2-§4.",
            ""),
        new(
            "Harbor.Abstractions.Tools.ITool",
            "ValidateArguments",
            DimDirection.Permissive,
            "Returns Result, so the only sane default is Success — i.e. accept any argument shape. "
            + "Currently harmless because all 26 in-tree ITool implementations override it, which "
            + "is exactly why it is safe to delete rather than reason about.",
            "https://github.com/refusedguy/Harbor-Harness/issues/579"),
        new(
            "Harbor.Abstractions.Sessions.ITokenTracker",
            "RecordAppendedMessage",
            DimDirection.Conservative,
            "Documented no-op added for source compatibility; a tracker that ignores an append "
            + "falls back to its own estimation on the next ShouldCompact, so the drift is visible.",
            ""),
        new(
            "Harbor.Terminal.Abstractions.Views.ITuiView",
            "HandleKey",
            DimDirection.Conservative,
            "Returns false: the key falls through to the next view. Conservative (#579 row 5) — but "
            + "the hook has ZERO callers, since key handling moved to the reducer "
            + "(AppMsg.KeyInput via KeyEventAdapter.cs:23-27), so it is also dead.",
            ""),
        new(
            "Harbor.Terminal.Abstractions.Views.ITuiView",
            "OnEventAsync",
            DimDirection.Permissive,
            "Task.CompletedTask: the agent event is dropped without a sound. #579 found the hook has "
            + "ZERO callers — BaseTuiRenderer only calls view.RenderAsync — so a plugin author who "
            + "implements it ships a view that never updates. The interface is the pattern, the "
            + "implementation is fiction.",
            "https://github.com/refusedguy/Harbor-Harness/issues/579"),
        new(
            "Harbor.Terminal.Abstractions.ITuiRenderer",
            "RunInteractiveAsync",
            DimDirection.Permissive,
            "Returns Task.FromResult(0) — exit code 0 for an interactive loop it never ran. A host "
            + "that delegates to the default gets \"clean exit\" for a REPL that did not happen. "
            + "Currently masked because every full-screen renderer overrides it, which is exactly "
            + "why it is a debt with an owner rather than an accident.",
            "https://github.com/refusedguy/Harbor-Harness/issues/579"),
        new(
            "Harbor.Ui.Framework.State.IAppReducerPlugin",
            "After",
            DimDirection.Conservative,
            "Identity fold: returning state unchanged is the documented contract, and the one arm "
            + "that needs to override it does (#388 AppMsg.Reset).",
            ""),
        new(
            "Harbor.Ui.Framework.Rendering.Widgets.ICollapsibleChatBlock",
            "ToggleExpanded",
            DimDirection.Conservative,
            "Flips its own state via SetExpanded — an exact derivation, not a skipped action. "
            + "The one interface in the governed set that mixes a mutating default with a "
            + "conservative one (#579 row 8).",
            ""),
        new(
            "Harbor.Ui.Framework.Rendering.Widgets.ICollapsibleChatBlock",
            "TryHitHeader",
            DimDirection.Conservative,
            "Returns false: an unpainted block never claims a click.",
            ""),
        new(
            "Harbor.Ui.Framework.Rendering.Widgets.ICollapsibleChatBlock",
            "DefaultCollapsedBodyLines",
            DimDirection.Conservative,
            "A static budget constant (4 lines, the pre-mixin ToolCallBlock value). A static default "
            + "is a pure derivation whatever it returns.",
            ""),
        new(
            "Harbor.Ui.Framework.Rendering.Widgets.ICollapsibleChatBlock",
            "DefaultUniversalBodyLines",
            DimDirection.Conservative,
            "A static budget constant (10 lines).",
            ""),
        new(
            "Harbor.Ui.Framework.Rendering.Widgets.ICollapsibleChatBlock",
            "DefaultExpandedBodyLines",
            DimDirection.Conservative,
            "A static budget constant (20 lines).",
            ""),
        new(
            "Harbor.Ui.Framework.Rendering.Widgets.ICollapsibleChatBlock",
            "OverflowTail",
            DimDirection.Conservative,
            "A static string derivation. No input is skipped and no error is swallowed.",
            ""),
        new(
            "Harbor.Ui.Framework.Services.IThemeWatcher",
            "Watch",
            DimDirection.Permissive,
            "The DIM-forwarder trap, live. `Watch(path, onError) => Watch(path)` routes to the older, "
            + "information-losing overload: an implementation written before this member existed "
            + "compiles, satisfies the contract, and swallows every watcher parse/IO failure the "
            + "caller explicitly asked to see. Same defect class as the one #496 fixed on SinkKind — "
            + "and the one the classifier detects structurally, by the richer default standing next "
            + "to a poorer overload of the same name.",
            "https://github.com/refusedguy/Harbor-Harness/issues/579"),
    ];

    // =====================================================================
    // 1. The rules.
    // =====================================================================

    /// <summary>
    ///     Every default interface member in the governed assemblies is on the
    ///     reviewed list. A new DIM is a compile-clean addition nobody looked
    ///     at; this is what makes it a red build instead.
    /// </summary>
    [Test]
    public async Task EveryDefaultInterfaceMember_IsReviewed()
    {
        IReadOnlyList<FoundDefault> found = Defaults;

        await Assert.That(found.Count).IsGreaterThan(0)
            .Because("the scan found no default interface members at all; a whitelist over an empty "
                   + "set is satisfied by anything, including a typo'd assembly name");

        var reviewed = Reviewed.Select(static r => (r.TypeName, r.MemberName)).ToHashSet();
        var unreviewed = found
            .Where(member => !reviewed.Contains((member.TypeName, member.MemberName)))
            .Select(member => $"{member.TypeName}.{member.MemberName}")
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        await Assert.That(unreviewed).IsEmpty()
            .Because("these default interface members are not on the reviewed list. docs/PATTERNS.md "
                   + "§'Default interface members': every default must be written down with the "
                   + "direction it fails in, so the next reader knows whether omitting it is safe. "
                   + "Add a row stating the direction and — if it is permissive — the issue that "
                   + "owns removing it. Unreviewed: " + string.Join(", ", unreviewed));
    }

    /// <summary>
    ///     Every reviewed row still exists. A whitelist row whose member was
    ///     deleted or renamed is a lie: it grandfathers a name nobody can find
    ///     and hides the fact that the convention's inventory has drifted.
    /// </summary>
    [Test]
    public async Task ReviewedList_HasNoStaleRows()
    {
        var found = Defaults
            .Select(static member => (member.TypeName, member.MemberName))
            .ToHashSet();

        var stale = Reviewed
            .Where(row => !found.Contains((row.TypeName, row.MemberName)))
            .Select(row => $"{row.TypeName}.{row.MemberName}")
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        await Assert.That(stale).IsEmpty()
            .Because("these rows name a default interface member the reflection scan cannot find — "
                   + "it was renamed, deleted, or moved to an assembly outside the governed "
                   + "prefixes. Delete the row and update docs/PATTERNS.md §'Default interface "
                   + "members'. Stale: " + string.Join(", ", stale));
    }

    /// <summary>
    ///     The reviewed direction is a CLAIM, and this checks it. Re-derives
    ///     the direction from each member's own signature and compares it with
    ///     the annotation, so flipping a default from conservative to
    ///     permissive — or hiding a permissive one behind a conservative label —
    ///     is red.
    /// </summary>
    [Test]
    public async Task EveryReviewedDefault_FailsInTheStatedDirection()
    {
        var byKey = Defaults.ToDictionary(static m => (m.TypeName, m.MemberName), static m => m);

        var failures = new List<string>();
        foreach (ReviewedDefault row in Reviewed)
        {
            if (!byKey.TryGetValue((row.TypeName, row.MemberName), out FoundDefault? member))
            {
                continue; // reported by ReviewedList_HasNoStaleRows
            }

            DimDirection actual = Classify(member);
            if (actual != row.Direction)
            {
                failures.Add(
                    $"{row.TypeName}.{row.MemberName}: reviewed as {row.Direction}, actually fails "
                    + $"{actual} (returns {member.ReturnType.Name}, "
                    + $"{member.ParameterCount} parameter(s), static={member.IsStatic}). "
                    + "Either the default changed shape or the review is out of date.");
            }
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     The banned shape, stated as a rule. A default that accepts anything
    ///     on the caller's behalf is the failure mode #579 is about: the
    ///     implementer omits it, the caller's validation silently evaporates.
    /// </summary>
    [Test]
    public async Task NoUnreviewedPermissiveDefault_Exists()
    {
        var permissive = Defaults
            .Where(member => Classify(member) == DimDirection.Permissive)
            .ToList();

        var tracked = Reviewed
            .Where(static row => row.Direction == DimDirection.Permissive)
            .Select(static row => (row.TypeName, row.MemberName))
            .ToHashSet();

        var failures = new List<string>();
        foreach (FoundDefault member in permissive)
        {
            if (tracked.Contains((member.TypeName, member.MemberName)))
            {
                continue;
            }

            failures.Add(
                $"{member.TypeName}.{member.MemberName}: fails PERMISSIVELY (returns "
                + $"{member.ReturnType.Name}, {member.ParameterCount} parameter(s), "
                + $"static={member.IsStatic}) and is not on the reviewed list. A default that fails "
                + "towards silently-fine is the defect class in #579 — do not add one.");
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     THE POSITIVE CONTROL. <see cref="PermissiveProbeContract" /> is
    ///     declared in this file with the exact shape the convention bans. The
    ///     classifier MUST call it permissive. If it degrades to
    ///     "everything is conservative", this is red before the rules above
    ///     can pass vacuously.
    /// </summary>
    [Test]
    public async Task NonVacuity_PermissiveDefault_IsDetected()
    {
        // Scanned for real, out of this assembly's own IL — not synthesised
        // FoundDefault records, which would only test the classifier's switch
        // and not the reflection half that feeds it.
        var self = typeof(DefaultInterfaceMemberRule).Assembly;
        IReadOnlyList<FoundDefault> found = DefaultInterfaceMemberProbe.Scan(self);

        // The control's own interface, and only its members, so the
        // classifications below cannot be influenced by anything else in this
        // assembly.
        var byName = found
            .Where(m => m.TypeName == typeof(PermissiveProbeContract).FullName)
            .GroupBy(static m => m.MemberName, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.ToList(), StringComparer.Ordinal);

        // 1. The banned `Result` shape — ITool.ValidateArguments.
        await Assert.That(byName.ContainsKey("Validate")).IsTrue()
            .Because("PermissiveProbeContract is declared in this file; if reflection cannot see its "
                   + "default members, the scan half of the probe is broken and every rule here is "
                   + "vacuous");

        await Assert.That(byName["Validate"].Count).IsEqualTo(1)
            .Because("the control declares exactly one `Validate`; more than one means the "
                   + "classification below is not the one under test");

        await Assert.That(DefaultInterfaceMemberProbe.Classify(byName["Validate"][0], byName["Validate"]))
            .IsEqualTo(DimDirection.Permissive)
            .Because("a default returning Result can only sensibly be Success, i.e. accept any input — "
                   + "the ITool.ValidateArguments shape from #579. If the classifier misses it, the "
                   + "NoUnreviewedPermissiveDefault rule is enforcing nothing.");

        // 2. The forwarder overload — IThemeWatcher.Watch(path, onError), the
        //    shape the convention names first.
        await Assert.That(byName.ContainsKey("Watch")).IsTrue()
            .Because("PermissiveProbeContract declares the two-overload Watch pair the "
                   + "DIM-forwarder rule is written against");
        await Assert.That(byName["Watch"].Count).IsEqualTo(2)
            .Because("the forwarder rule needs both overloads to see the richer one");

        // The FORWARDER is the richer overload (2 parameters); the poorer one it
        // delegates to takes 1. Both are defaults here, exactly as in
        // IThemeWatcher, where the poorer overload is the contract member.
        var forwarded = byName["Watch"].Where(static m => m.ParameterCount == 2).ToList();
        await Assert.That(forwarded.Count).IsEqualTo(1)
            .Because("exactly one of the two Watch overloads takes the extra parameter");

        await Assert.That(DefaultInterfaceMemberProbe.Classify(forwarded[0], byName["Watch"]))
            .IsEqualTo(DimDirection.Permissive)
            .Because("a richer overload whose default routes to the poorer one is the live "
                   + "IThemeWatcher defect. If the classifier misses it, the whole point of the "
                   + "convention is unenforced.");

        // 3. The blessed shapes must NOT be reported — the classifier has to
        //    discriminate, not flag everything.
        await Assert.That(DefaultInterfaceMemberProbe.Classify(byName["IsModal"][0], byName["IsModal"]))
            .IsEqualTo(DimDirection.Conservative)
            .Because("`false` is the direction in which nothing silently goes wrong");

        await Assert.That(DefaultInterfaceMemberProbe.Classify(byName["Budget"][0], byName["Budget"]))
            .IsEqualTo(DimDirection.Conservative)
            .Because("a static default is a pure derivation, whatever it returns");
    }

    /// <summary>
    ///     The reflection scan really reads the interfaces, and really finds the
    ///     members #579 inventoried. Both the count and the specific names are
    ///     asserted, because a count alone is satisfied by any N members.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scan_FindsTheKnownDefaults()
    {
        IReadOnlyList<FoundDefault> found = Defaults;
        var names = found.Select(static m => $"{m.TypeName}.{m.MemberName}").ToHashSet(StringComparer.Ordinal);

        // The five #579 names that are loadable from this test project, plus
        // the ITokenTracker no-op found while writing the list.
        string[] expected =
        [
            "Harbor.Abstractions.Events.IEventBusMiddleware.SinkKind",
            "Harbor.Abstractions.Tools.ITool.ValidateArguments",
            "Harbor.Abstractions.Sessions.ITokenTracker.RecordAppendedMessage",
            "Harbor.Terminal.Abstractions.Views.ITuiView.HandleKey",
            "Harbor.Terminal.Abstractions.Views.ITuiView.OnEventAsync",
            "Harbor.Terminal.Abstractions.ITuiRenderer.RunInteractiveAsync",
            "Harbor.Ui.Framework.State.IAppReducerPlugin.After",
            "Harbor.Ui.Framework.Services.IThemeWatcher.Watch",
        ];

        var missing = expected.Where(name => !names.Contains(name)).ToHashSet(StringComparer.Ordinal);

        await Assert.That(missing).IsEmpty()
            .Because("these defaults are named in docs/PATTERNS.md and in issue #579. If the scan "
                   + "cannot find them, either the governed prefix is wrong or the assemblies are not "
                   + "loaded — and a scan that finds nothing would satisfy the whitelist vacuously. "
                   + "Missing: " + string.Join(", ", missing));

        await Assert.That(found.Count).IsGreaterThanOrEqualTo(expected.Length)
            .Because("the scan found fewer defaults than #579 inventoried, so it is not reading the "
                   + "whole governed set");
    }

    /// <summary>
    ///     Every governed prefix must resolve to a LOADED assembly that actually
    ///     contains interfaces. This is the direct answer to the NetArchTest
    ///     trap: a prefix that matches nothing would otherwise leave the
    ///     whitelist trivially satisfied.
    /// </summary>
    [Test]
    public async Task GovernedAssemblies_AreLiveAndResolve()
    {
        var loaded = LoadedAssemblies.Value;
        var failures = new List<string>();

        foreach (string prefix in GovernedPrefixes)
        {
            var matched = loaded.Keys
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            if (matched.Count == 0)
            {
                failures.Add(
                    $"no loaded assembly matches the governed prefix '{prefix}'. A prefix that "
                    + "matches nothing leaves every rule here satisfied by an empty scan — this is "
                    + "the NetArchTest trap, and it is the reason this test exists.");
                continue;
            }

            foreach (string name in matched)
            {
                int interfaces = CountInterfaces(loaded[name]);
                if (interfaces == 0)
                {
                    failures.Add(
                        $"{name} matches the governed prefix '{prefix}' but declares no interfaces, "
                        + "so it contributes nothing to the scan and the prefix is over-broad");
                }
            }
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    private static int CountInterfaces(Assembly asm)
    {
        try
        {
            return asm.GetTypes().Count(static t => t.IsInterface);
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Count(static t => t is { IsInterface: true });
        }
    }

    // =====================================================================
    // 3. Reviewed-list integrity.
    // =====================================================================

    /// <summary>
    ///     Every row states why, every permissive row names a tracking issue,
    ///     the table has no duplicate keys, and no row is outside the governed
    ///     prefixes.
    /// </summary>
    [Test]
    public async Task ReviewedList_IsWellFormed()
    {
        var failures = new List<string>();
        var seen = new HashSet<(string, string)>();

        foreach (ReviewedDefault row in Reviewed)
        {
            string key = $"{row.TypeName}.{row.MemberName}";

            if (!seen.Add((row.TypeName, row.MemberName)))
            {
                failures.Add($"duplicate reviewed row '{key}'");
            }

            if (string.IsNullOrWhiteSpace(row.Rationale))
            {
                failures.Add($"'{key}' has no rationale — a whitelist row that does not say which way "
                    + "the default fails is the comment this file exists to replace");
            }

            if (row.Direction == DimDirection.Permissive
                && !row.TrackedBy.Contains("https://github.com/", StringComparison.Ordinal))
            {
                failures.Add($"'{key}' fails PERMISSIVELY and has no tracking issue URL (got "
                    + $"'{row.TrackedBy}') — a permissive default is a debt, and a debt with no owner "
                    + "is how IThemeWatcher's forwarder survived a fix that already existed elsewhere");
            }

            bool governed = GovernedPrefixes.Any(prefix =>
                row.TypeName.StartsWith(prefix, StringComparison.Ordinal));
            if (!governed)
            {
                failures.Add($"'{key}' is outside the governed prefixes — either the row or "
                    + "GovernedPrefixes is wrong");
            }
        }

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    // =====================================================================
    // 4. Plumbing.
    // =====================================================================

    /// <summary>
    ///     Every governed default, tagged with the other defaults on the SAME
    ///     interface so <see cref="DefaultInterfaceMemberProbe.Classify" /> can
    ///     apply the forwarder-overload rule. Keyed by
    ///     <c>(TypeName, MemberName)</c>; a name collision inside one interface
    ///     is impossible for a default, because a default is a single member
    ///     and overloads differ by parameter count.
    /// </summary>
    private static IReadOnlyList<FoundDefault> Collect()
    {
        var found = new List<FoundDefault>();
        foreach ((string name, Assembly asm) in LoadedAssemblies.Value)
        {
            if (!GovernedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            found.AddRange(DefaultInterfaceMemberProbe.Scan(asm));
        }

        return found;
    }

    /// <summary>
    ///     The positive control's contract, declared here so its IL is in this
    ///     assembly. Never implemented and never registered — it exists to be
    ///     measured by <see cref="NonVacuity_PermissiveDefault_IsDetected" />.
    /// </summary>
    private interface PermissiveProbeContract
    {
        /// <summary>The banned shape: the only sane default is Success.</summary>
        Result Validate() => Result.Success();

        /// <summary>
        ///     The forwarder pair, in the shape IThemeWatcher has. The richer
        ///     overload routes to the poorer one, dropping the error callback the
        ///     caller explicitly asked for.
        /// </summary>
        IDisposable Watch(string path) => new NoopSubscription();

        /// <inheritdoc cref="Watch(string)" />
        IDisposable Watch(string path, Action<string>? onError) => Watch(path);

        /// <summary>The blessed shape.</summary>
        bool IsModal() => false;

        /// <summary>A static default is a pure derivation.</summary>
        static int Budget => 4;
    }

    /// <summary>Only so the control's <c>Watch</c> default has something to return.</summary>
    private sealed class NoopSubscription : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
