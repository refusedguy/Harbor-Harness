// TestContextOutputDiagnosticsTests.cs — #1856: the channel a test's
// diagnostics travel on, proven from the inside.
//
// WHAT THIS FILE CLAIMS
// ---------------------
// #1856 moved the test tree's diagnostics off `Console.WriteLine` and onto
// `TestContext.Output`. The claim behind that move is a claim about the
// REPORT, not about the console: a diagnostic written through
// `TestContext.Output` is part of the failing test's OWN output block in the
// TRX/HTML report, so a red test says WHY it went red.
//
// A comment cannot establish that, so this file states it as tests:
//
//   1. a diagnostic written through `TestContext.Output` is in the captured
//      standard output of the running test — the same buffer the engine reads
//      when it publishes the test's output for the report;
//   2. it lands in the STANDARD output, not the error output, so a diagnostic
//      never turns a passing test red;
//   3. a write with NO ambient test context — a background callback that
//      outlived its test — is DROPPED, not thrown. A diagnostic must never be
//      the thing that fails an unrelated test.
//
// (1) and (2) are what make the conversion worth anything; (3) is what makes
// it safe to convert a writer that runs off the test's own thread.
//
// WHAT THIS FILE DOES NOT CLAIM
// -----------------------------
// It does not claim that `Console.WriteLine` is invisible to the report. TUnit
// 1.75 installs a console interceptor that routes `Console.Out` writes into
// the current context's output sink, so a `Console.WriteLine` from the test's
// own thread also reaches the report. What the interceptor cannot do is
// ATTRIBUTE a write reliably: it resolves the current context on the WRITING
// thread, so a write from a thread that does not flow the ExecutionContext
// lands in the global context — the runner's stdout, past every test's
// report — and a test that replaced `Console.Out` (the stdout-capture suites
// under tests/Harbor.App.Cli.Tests) has no interceptor left at all.
// `TestContext.Output` is addressed rather than ambient, so neither applies.
//
// The source-level half of the control is
// tests/Harbor.Architecture.Tests/TestDiagnosticsOutputRule.cs: it fails if a
// converted call site is reverted to `Console.WriteLine`.

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #1856 — the self-test for the diagnostics channel: a diagnostic written
///     through <see cref="TestContext" /> reaches the captured output the
///     report is built from, and a diagnostic with nowhere to go is dropped
///     rather than thrown.
/// </summary>
public class TestContextOutputDiagnosticsTests
{
    private const string Marker = "#1856-diagnostic-marker";
    private const string DroppedMarker = "#1856-dropped-write";

    [Test]
    public async Task A_Diagnostic_Written_Through_TestContextOutput_Is_In_The_Captured_Output()
    {
        var context = TestContext.Current;
        await Assert.That(context).IsNotNull()
            .Because("the conversion #1856 made is only meaningful inside a test, where a context exists");

        context!.Output.WriteLine(Marker);

        await Assert.That(context!.GetStandardOutput()).Contains(Marker)
            .Because(
                "the engine publishes the test's captured standard output as the report's output block, so a "
                + "diagnostic written this way is part of the failing test's own report instead of the "
                + "runner's stdout");
    }

    [Test]
    public async Task A_Diagnostic_Written_Through_TestContextOutput_Reaches_The_Report_Interface()
    {
        var context = TestContext.Current;
        await Assert.That(context).IsNotNull();

        context!.Output.WriteLine(Marker);

        // The reporter reads the output through the ITestOutput surface, not
        // through the concrete context — so assert on that surface too.
        await Assert.That(context!.Output.GetStandardOutput()).Contains(Marker)
            .Because(
                "the report is assembled from ITestOutput.GetStandardOutput(); a diagnostic that is only "
                + "visible through the concrete context would not reach it");
        await Assert.That(context!.GetErrorOutput()).DoesNotContain(Marker)
            .Because(
                "a diagnostic is not an error: routing it to the error output would mark a passing test as "
                + "failed over a warning");
    }

    [Test]
    public async Task A_Write_With_No_Ambient_Test_Context_Is_Dropped_Not_Thrown()
    {
        var context = TestContext.Current;
        await Assert.That(context).IsNotNull();

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? thrown = null;

        // ThreadPool.UnsafeQueueUserWorkItem does NOT flow the ExecutionContext,
        // so the callback below has no TestContext.Current — the shape a
        // background writer has once its test has already ended.
        ThreadPool.UnsafeQueueUserWorkItem(new WaitCallback(_ =>
        {
            try
            {
                TestContext.Current?.Output.WriteLine(DroppedMarker);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                done.SetResult(true);
            }
        }), null);

        await done.Task;

        await Assert.That(thrown).IsNull()
            .Because(
                "a diagnostic that cannot be attributed has to be dropped, not thrown: throwing from a "
                + "callback that outlived its test would fail an unrelated test with a stack trace about "
                + "output plumbing");
        await Assert.That(context!.GetStandardOutput()).DoesNotContain(DroppedMarker)
            .Because("an unattributable write must not be attached to whichever test happens to be running");
    }
}
