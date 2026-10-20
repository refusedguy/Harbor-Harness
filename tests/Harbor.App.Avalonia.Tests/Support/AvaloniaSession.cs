// AvaloniaSession.cs — #1819: the ONE owner of the Avalonia headless session
// in this project.
//
// WHAT THIS REPLACES
// ------------------
// Twenty-six call sites across eight files used to open a headless session and
// dispatch into it by hand:
//
//     await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
//     await session.Dispatch((System.Action)(() => { … }), CancellationToken.None);
//
// with three different spellings of "the app" (`typeof(App)` in eighteen of
// them, `typeof(global::Harbor.App.Avalonia.App)` in the other eight), a manual
// `(System.Action)` cast at every site, and — at every site that had something
// to say afterwards — local variables declared before the lambda, assigned
// inside it, and read after it, because the dispatcher hands back nothing.
//
// WHAT ACTUALLY VARIES, AND WHERE IT LIVES NOW
// ---------------------------------------------
// Three things varied, and each is a PARAMETER here rather than a per-site copy:
//
//   * WHAT the body produces  → `RunAsync<T>` returns it, so the
//     "declare → assign inside the lambda → assert after" dance disappears and
//     the assertions stay where they were: OUTSIDE the dispatch.
//   * WHETHER the body suspends → the `RunAsync(Func<Task>)` overload starts the
//     body inside the synchronous dispatch and awaits it OUTSIDE, with the
//     session open until it lands. That is the one site whose body really does
//     suspend (`ConfigDefaultsComeFromCoreTests`), and it is a DIFFERENT overload
//     rather than a flag on this one.
//   * HOW LONG the session lives → `RunWithSessionAsync` hands the caller an
//     open session, for the one site whose assertions have to run while
//     Avalonia is still alive.
//
// The `(System.Action)` cast still happens — here, once, where it can no
// longer be forgotten at some site that needed it. That cast is not
// decoration: `HeadlessUnitTestSession` declares no `Dispatch(Func<Task>)`,
// and the generic `Dispatch<TResult>(Func<TResult>)` instantiated at
// `TResult = Task` accepts an `async` lambda and returns a `Task<Task>` whose
// payload the call site drops — everything after the body's first suspension
// is detached, and the test reports green without having checked anything
// (#972, #766). See `AvaloniaDispatchAsyncVoidRule`, which grades that form.
//
// WHY IT LIVES HERE AND NOT IN Harbor.TestKit
// --------------------------------------------
// `Harbor.TestKit` does not reference `Avalonia.Headless` and must not: twelve
// projects that have nothing to do with UI would start building Avalonia. The
// headless session is a property of THIS project, so the helper belongs to it.
//
// WHAT IT DOES NOT DECIDE
// -----------------------
// Nothing about the body. The body still runs on the UI thread, the assertions
// still run outside the dispatch, and the session is still opened and closed
// around the dispatch. What moved is the boilerplate, and the three answers
// that were open-coded twenty-six times.

using Avalonia.Headless;

namespace Harbor.App.Avalonia.Tests.Support;

/// <summary>
///     The one owner of the Avalonia headless session in this project: it opens the
///     session, runs a body on the headless UI thread, and closes the session.
/// </summary>
internal static class AvaloniaSession
{
    /// <summary>
    ///     The application every headless session boots, named once.
    /// </summary>
    /// <remarks>
    ///     The eight files this helper replaced spelled it three ways, and the
    ///     namespace collision between <c>Harbor.App</c> and <c>Harbor.App.Avalonia</c>
    ///     is what forced the <c>global::</c> spelling in the first place. Here it
    ///     is one line, in one file, and no test has to think about it.
    /// </remarks>
    private static readonly Type AppType = typeof(global::Harbor.App.Avalonia.App);

    /// <summary>
    ///     Runs a body that does NOT suspend, on the headless UI thread, and returns
    ///     what it produced.
    /// </summary>
    /// <remarks>
    ///     The session is closed when this call returns, so the body must hand back
    ///     plain values — anything whose lifetime depends on Avalonia still being
    ///     alive has to come through <see cref="RunWithSessionAsync" /> instead.
    ///     <para>
    ///         A failure inside the body propagates out of the dispatch and fails the
    ///         test, exactly as it did when the dispatch was written out by hand: the
    ///         dispatcher captures the exception in the task it returns, and awaiting
    ///         it rethrows.
    ///     </para>
    ///     <para>
    ///         A body that SUSPENDS belongs to the
    ///         <see cref="RunAsync(Func{Task}, CancellationToken)" /> overload,
    ///         which awaits it — the two overloads are told apart by the
    ///         overload-resolution rule documented on that one, not by the spelling
    ///         of the call.
    ///     </para>
    /// </remarks>
    /// <typeparam name="T">What the body produces.</typeparam>
    /// <param name="body">The work, to run on the headless UI thread.</param>
    /// <param name="ct">Cancellation token for the dispatch.</param>
    public static async Task<T> RunAsync<T>(Func<T> body, CancellationToken ct = default)
    {
        await using var session = HeadlessUnitTestSession.StartNew(AppType);

        T result = default!;
        await session.Dispatch((System.Action)(() => result = body()), ct);
        return result;
    }

    /// <summary>
    ///     Runs a body that MAY suspend: it is started inside the synchronous
    ///     dispatch and awaited OUTSIDE it, with the session open until it lands.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This exists for the one site whose body genuinely suspends
    ///         (<c>ConfigDefaultsComeFromCoreTests</c>). Such a body cannot be
    ///         awaited inside the dispatch: the overload that takes a
    ///         <c>Func&lt;Task&gt;</c> returns a <c>Task&lt;Task&gt;</c> whose payload
    ///         the call site drops, so everything after the body's first suspension
    ///         is detached and its failures are discarded (#972, #766 — see
    ///         <c>AvaloniaDispatchAsyncVoidRule</c>).
    ///     </para>
    ///     <para>
    ///         Starting the task inside the synchronous dispatch and awaiting it here
    ///         keeps the awaited task THIS method's, so a failed assertion inside the
    ///         body fails the test, and the session is not disposed while the body is
    ///         still in flight.
    ///     </para>
    ///     <para>
    ///         <b>Which overload an <c>async</c> lambda reaches.</b> This one, and
    ///         not <see cref="RunAsync{T}(Func{T}, CancellationToken)" />: both are
    ///         applicable, their parameter types are equivalent once
    ///         <c>T</c> is inferred as <see cref="Task" />, and the tie-breaking
    ///         rule that then applies is "a non-generic method beats a generic one"
    ///         (§12.6.4.3) — the same reason <c>Task.Run(async …)</c> binds to
    ///         <c>Run(Func&lt;Task&gt;)</c> and not to <c>Run&lt;TResult&gt;</c>. That
    ///         is what makes the hazard structurally impossible here: there is no
    ///         spelling of a call that hands this helper a body returning a
    ///         <see cref="Task" /> and gets a <c>Task&lt;Task&gt;</c> back.
    ///     </para>
    /// </remarks>
    /// <param name="body">The work, to start on the headless UI thread.</param>
    /// <param name="ct">Cancellation token for the dispatch.</param>
    public static async Task RunAsync(Func<Task> body, CancellationToken ct = default)
    {
        await using var session = HeadlessUnitTestSession.StartNew(AppType);

        Task work = Task.CompletedTask;
        await session.Dispatch((System.Action)(() => work = body()), ct);
        await work;
    }

    /// <summary>
    ///     Runs a body against an OPEN session, and keeps the session open until the
    ///     body returns.
    /// </summary>
    /// <remarks>
    ///     For the site whose assertions have to run while Avalonia is still alive:
    ///     closing the session shuts the application down, and a window that has been
    ///     shut down is no longer visible. Every other site should use
    ///     <see cref="RunAsync{T}(Func{T}, CancellationToken)" /> — a session that
    ///     outlives its own body is the shape that makes the next test's bootstrap
    ///     race (#766).
    /// </remarks>
    /// <param name="body">
    ///     The work, to run against the open session. The session is disposed when
    ///     this method returns.
    /// </param>
    /// <param name="ct">Cancellation token for the dispatch.</param>
    public static async Task RunWithSessionAsync(Func<Scope, Task> body, CancellationToken ct = default)
    {
        await using var scope = new Scope(HeadlessUnitTestSession.StartNew(AppType), ct);
        await body(scope);
    }

    /// <summary>
    ///     An open headless session, owned by the caller for as long as the caller
    ///     holds it.
    /// </summary>
    internal sealed class Scope : IAsyncDisposable
    {
        private readonly HeadlessUnitTestSession _session;
        private readonly CancellationToken _ct;

        internal Scope(HeadlessUnitTestSession session, CancellationToken ct)
        {
            _session = session;
            _ct = ct;
        }

        /// <summary>Runs a body on the headless UI thread.</summary>
        /// <param name="body">The work, to run on the headless UI thread.</param>
        internal Task DispatchAsync(Action body) => _session.Dispatch((System.Action)body, _ct);

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _session.DisposeAsync();
    }
}
