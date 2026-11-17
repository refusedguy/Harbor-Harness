// TestCancellation.cs — the CTS shapes a test writes by hand (#1569).
//
// WHAT THE TREE LOOKED LIKE
// -------------------------
// A test that needs a cancellation source wrote it out, every time:
//
//     using var cts = new CancellationTokenSource();
//     await cts.CancelAsync();
//     ... cts.Token ...
//
// That is two lines and one fact ("this token is ALREADY cancelled") spread
// over both of them. The census on `origin/dev` counted 76 hand-rolled
// `new CancellationTokenSource(...)` sites in `tests/`, and 13 of them in
// `Harbor.Application.Tests` alone had exactly the shape above.
//
// THE THREE SHAPES, AND WHAT EACH ONE OWNS
// -----------------------------------------
//   1. "the test cancels it LATER" — the source is the test's instrument
//      (the test cancels it mid-run, or a collaborator cancels it). There is
//      nothing to share: the source must stay writable. Keep writing it out.
//
//   2. "the token is ALREADY cancelled" — this file. One factory, one name.
//
//   3. "the test needs a BOUND" — that is NOT a CTS, it is TUnit's
//      `[Timeout(ms)]` plus the runner-injected `CancellationToken`, which the
//      runner cancels when the budget elapses. A hand-rolled
//      `new CancellationTokenSource(TimeSpan.FromSeconds(5))` fires its token
//      but the runner keeps waiting for the test, so a hung body eats the
//      shard's wall clock instead of failing as a timeout. See
//      `docs/TEST_PATTERNS.md` §"Timeouts and cancellation".
//
// WHERE IT LIVES AND WHY
// ----------------------
// tests/Harbor.TestKit.Support, NOT tests/Harbor.TestKit.
//
// The factory needs no product assembly and no TUnit package — it is the BCL's
// `CancellationTokenSource` and nothing else — so it belongs in the part every
// test project can take without widening its product-assembly closure. The
// NAMESPACE is `Harbor.TestKit`, so a consumer's `using` line does not change.

namespace Harbor.TestKit;

/// <summary>
///     The cancellation-source factories a test reaches for (#1569).
/// </summary>
/// <remarks>
///     <para>
///         Prefer these over spelling out <c>new CancellationTokenSource()</c>
///         plus a <c>Cancel()</c> call: the factory says in one name what the
///         two lines said in two.
///     </para>
///     <para>
///         This is NOT the place for a test that needs a bound. A bound is
///         <c>[Timeout(ms)]</c> and the runner-injected
///         <see cref="CancellationToken" />, because the runner then cancels
///         the token and fails the test — a manual CTS only fires its own
///         token and leaves the runner waiting.
///     </para>
/// </remarks>
public static class TestCancellation
{
    /// <summary>
    ///     A source whose token is ALREADY cancelled — the "the caller was
    ///     cancelled before we got here" probe.
    /// </summary>
    /// <remarks>
    ///     The source is returned, not the token, so the caller keeps the
    ///     <c>using</c> that disposes it and reads <c>.Token</c> where it
    ///     always did.
    /// </remarks>
    public static CancellationTokenSource Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source;
    }
}
