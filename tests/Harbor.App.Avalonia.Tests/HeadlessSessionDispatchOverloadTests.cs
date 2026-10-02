// HeadlessSessionDispatchOverloadTests.cs — the premise behind #972's guard,
// checked against the REAL Avalonia.Headless assembly instead of a comment.
//
// WHY THIS FILE EXISTS AT ALL
// ----------------------------
// `AvaloniaDispatchAsyncVoidRule` (Harbor.Architecture.Tests) bans
// `session.Dispatch(async () => …)`. That rule is correct. The REASON it first
// gave for itself was not, and it is the reason this file exists.
//
// The rule's original premise:
//
//     "HeadlessUnitTestSession declares no Dispatch(Func<Task>). An async
//      () => { … } lambda with no return value is convertible ONLY to the
//      void-returning delegate, so the call binds to Dispatch(Action) and the
//      body runs as async void."
//
// Both halves are false, and the guard's own test of that premise could not
// have noticed: it asserted over a hardcoded `string[]` in the same file, so it
// compared a literal against itself and was green by construction.
//
// The truth, which this file pins:
//
//   * An `async () => { … }` lambda with no `return` statement has natural type
//     `Func<Task>` — that is precisely what the `async` modifier means.
//   * `HeadlessUnitTestSession` declares `Dispatch<TResult>(Func<TResult>)`.
//     Instantiated at `TResult = Task`, that IS a `Dispatch(Func<Task>)` and it
//     returns `Task<Task>`.
//   * Overload resolution therefore binds the lambda to THAT overload, not to
//     `Dispatch(Action)` — for the same reason `Task.Run(async …)` binds to
//     `Func<Task>`. An anonymous function whose inferred return type matches a
//     candidate delegate's return type is the better conversion target.
//
// So the SYMPTOM the rule was written for is real, and the route to it is not
// the one documented:
//
//     Dispatch(async () => { … })  ->  Dispatch<Task>(Func<Task>, ct)
//                                   ->  Task<Task>
//
// and that overload is implemented as `DispatchCore(() => Task.FromResult(
// action()), …)`. At `TResult = Task` the wrapper `Task.FromResult(bodyTask)`
// is ALREADY completed the moment the body returns at its first suspension, so
// `Dispatch`'s task completes there, the caller resumes, and the returned
// `Task<Task>`'s payload — the real body task — is DISCARDED. Every assertion
// after the first `await` is dropped.
//
// This is not a hypothesis. CI confirmed the symptom at commit 03e8cac3:
// `test (ui)` failed `DiffView_ResolvesFrameworkBindings` with `Sequence
// contains no matching element`, thrown by `.Single(b => Content == "Compute")`
// — an assertion that had been unreachable inside the detached body since the
// file was written.
//
// WHY THE TEST IS HERE AND NOT IN THE ARCHITECTURE PROJECT
// --------------------------------------------------------
// Harbor.Architecture.Tests holds no reference to Avalonia.Headless, so a rule
// there cannot reflect over `HeadlessUnitTestSession` — it can only read text.
// `AvaloniaFireAndForgetRules` records that constraint explicitly for the same
// reason. This project already references Avalonia.Headless, so it is the only
// place the overload set can be READ rather than asserted from a string. A
// premise about an API belongs next to a reference to that API.
//
// NON-VACUITY
// -----------
//   * `TheRealOverloadSetIsTheThreeThisFileAssumed` — fails if Avalonia adds,
//     removes, or reshapes a `Dispatch` overload. Reflection, so it tracks the
//     shipped package.
//   * `TheGenericFuncOverloadIsDispatchOfFuncTaskWhenTResultIsTask` — the
//     load-bearing row. It pins that a `Func<Task>` IS an applicable argument
//     for a real overload, which is precisely the claim the original premise
//     denied. If this fails, an async lambda can no longer bind to a
//     Task-returning overload, and the guard's reasoning must be revisited.
//   * `NoNonGenericDispatchOfFuncTaskExists` — the part of the premise that
//     survives. If Avalonia ever adds a NON-generic `Dispatch(Func<Task>)`,
//     this fails, because that overload would genuinely await the body and the
//     guard would then be banning a correct shape.

using System.Reflection;

using Avalonia.Headless;

using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Pins the <see cref="HeadlessUnitTestSession" /> overload set that #972's
///     rule rests on, by reflecting over the shipped Avalonia.Headless assembly.
/// </summary>
public class HeadlessSessionDispatchOverloadTests
{
    /// <summary>The public <c>Dispatch</c> overloads, as signatures read from the assembly.</summary>
    private static string[] DispatchOverloads() =>
        [.. typeof(HeadlessUnitTestSession)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Dispatch")
            .Select(Describe)
            .OrderBy(s => s, StringComparer.Ordinal)];

    /// <summary>
    ///     A stable, readable rendering of one overload's signature — the shape a
    ///     reviewer can compare against the upstream source by eye.
    /// </summary>
    private static string Describe(MethodInfo m)
    {
        string generics = m.IsGenericMethodDefinition
            ? "<" + string.Join(",", m.GetGenericArguments().Select(a => a.Name)) + ">"
            : string.Empty;

        string parameters = string.Join(", ", m.GetParameters().Select(p => Pretty(p.ParameterType)));

        return $"{Pretty(m.ReturnType)} Dispatch{generics}({parameters})";
    }

    /// <summary>Type name with generic arguments spelled out, e.g. <c>Func&lt;Task&gt;</c>.</summary>
    private static string Pretty(Type t)
    {
        if (!t.IsGenericType)
        {
            return t.Name;
        }

        string name = t.Name[..t.Name.IndexOf('`')];
        return name + "<" + string.Join(",", t.GetGenericArguments().Select(Pretty)) + ">";
    }

    [Test]
    public async Task TheRealOverloadSetIsTheThreeThisFileAssumed()
    {
        string[] actual = DispatchOverloads();

        await Assert.That(actual.Length)
            .IsEqualTo(3)
            .Because(
                "#972's guard states its premise as exactly three Dispatch overloads, and this test is the "
                + "only one in the repository that reads them rather than assuming them. If Avalonia adds "
                + "or removes an overload the guard's reasoning must be revisited, and this fails first. "
                + "Found: " + string.Join(" ; ", actual));

        // The three signatures the guard names, in the order upstream declares them.
        await Assert.That(string.Join(" ; ", actual))
            .IsEqualTo(
                "Task Dispatch(Action, CancellationToken) ; "
                + "Task<TResult> Dispatch<TResult>(Func<TResult>, CancellationToken) ; "
                + "Task<TResult> Dispatch<TResult>(Func<Task<TResult>>, CancellationToken)")
            .Because(
                "these are the three overloads the #972 guard records as its premise. This is the "
                + "non-vacuity anchor for the premise: it is read from the assembly, so it fails when "
                + "the real API diverges from what the rule claims.");
    }

    [Test]
    public async Task TheGenericFuncOverloadIsDispatchOfFuncTaskWhenTResultIsTask()
    {
        // THE LOAD-BEARING CORRECTION.
        //
        // The original premise said an async lambda "is convertible ONLY to the
        // void-returning delegate". This asserts the opposite is true and
        // available: a Func<Task> IS an applicable argument for a real overload.
        // `Dispatch<TResult>(Func<TResult>)` at `TResult = Task` takes a
        // `Func<Task>` and returns `Task<Task>`.
        //
        // If this test fails, an async lambda can no longer bind to a
        // Task-returning Dispatch, and the whole "dropped Task<Task> payload"
        // account is wrong — the rule would have to be re-derived, not patched.
        MethodInfo? generic = typeof(HeadlessUnitTestSession)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m is { Name: "Dispatch", IsGenericMethodDefinition: true }
                                 && m.GetParameters() is [{ ParameterType: var t }, _]
                                 && t.IsGenericType
                                 && t.GetGenericTypeDefinition() == typeof(Func<>));

        await Assert.That(generic)
            .IsNotNull()
            .Because(
                "HeadlessUnitTestSession must keep a Dispatch overload whose first parameter is a Func<>, "
                + "because that is the overload an async () => { … } lambda binds to (#972). Without it "
                + "the lambda would fall through to Dispatch(Action) instead.");

        Type parameter = generic!.GetParameters()[0].ParameterType;

        await Assert.That(Pretty(parameter))
            .IsEqualTo("Func<TResult>")
            .Because(
                "the row #972's guard calls `Dispatch<TResult>(Func<TResult>)`. Anything else here means "
                + "the recorded premise no longer describes the method it names.");

        // Close the generic over Task - this is the substitution the original
        // premise insisted was impossible.
        //
        // Close it on the METHOD, not on the type. `parameter` is `Func<TResult>`,
        // already constructed over the method's own type parameter, and BOTH
        // `Func<TResult>` and the bare `TResult` are non-generic-type-definitions,
        // so MakeGenericType rejects either one:
        //
        //     InvalidOperationException: System.Func`1[TResult] is not a
        //       GenericTypeDefinition. MakeGenericType may only be called on a type
        //       for which Type.IsGenericTypeDefinition is true.
        //     InvalidOperationException: TResult is not a GenericTypeDefinition.
        //
        // MakeGenericMethod is the operation that matches what overload resolution
        // actually does: it substitutes the method's type argument, and the
        // parameter type comes out substituted with it.
        MethodInfo closed = generic.MakeGenericMethod(typeof(Task));
        Type taskOverload = closed.GetParameters()[0].ParameterType;

        await Assert.That(taskOverload.IsGenericType)
            .IsTrue()
            .Because(
                "closing Dispatch<TResult>(Func<TResult>) over Task must leave a constructed Func<Task> in "
                + "the first parameter. If it does not, the overload's shape is not the one #972's premise "
                + "describes and this account needs re-deriving.");

        await Assert.That(Pretty(taskOverload))
            .IsEqualTo("Func<Task>")
            .Because(
                "closing Dispatch<TResult>(Func<TResult>) over Task yields Func<Task>. So a Func<Task> IS an "
                + "applicable dispatch argument, and the claim that an async lambda can only convert to "
                + "Action is false. This is the whole correction: the defect is real, the stated reason was "
                + "not.");

        await Assert.That(Pretty(generic.ReturnType))
            .IsEqualTo("Task<TResult>")
            .Because(
                "at TResult = Task that overload returns Task<Task>, whose payload the call site drops. That "
                + "is the defect #972 describes, and it is a call-site bug rather than a missing overload.");
    }

    [Test]
    public async Task NoNonGenericDispatchOfFuncTaskExists()
    {
        // The part of the original premise that SURVIVES the correction, and the
        // only part that justifies banning the shape rather than fixing the
        // consumption: there is no overload spelled exactly `Func<Task>`.
        MethodInfo[] direct = [.. typeof(HeadlessUnitTestSession)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m is { Name: "Dispatch", IsGenericMethodDefinition: false })
            .Where(m => m.GetParameters() is [{ ParameterType: var t }, _] && t == typeof(Func<Task>))];

        await Assert.That(direct.Length)
            .IsEqualTo(0)
            .Because(
                "if Avalonia adds a non-generic Dispatch(Func<Task>), an async lambda binds to it, its body "
                + "is genuinely awaited, and AvaloniaDispatchAsyncVoidRule would be banning a correct shape. "
                + "Found: " + string.Join(" ; ", direct.Select(Describe)));
    }
}
