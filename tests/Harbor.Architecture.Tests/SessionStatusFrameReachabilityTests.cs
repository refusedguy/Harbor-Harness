// SessionStatusFrameReachabilityTests.cs — the tests for #861.
//
// Read the reasoning in SessionStatusFrameReachabilityRules.cs first; the short
// version: the #687 frame test is named after a guarantee its own project cannot
// reach, and the production comment at ChatViewModel.cs:139-141 makes the same
// claim with nothing behind it. If the frame inlines a status the presenter dies
// and the suite stays green.
//
// The rules here are deliberately RED in the first commit. `FramePublishesTheReducerDecision_AndThePresenterStaysReachable`
// is the gate; the other three are what make a green from it mean something.

using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>#861: the presenter's read is only a frame guarantee if a frame still calls it.</summary>
public class SessionStatusFrameReachabilityTests
{
    private static readonly SessionStatusFrameReport Report = SessionStatusFrameProbe.Scan(
        RepoPaths.RepoRoot);

    /// <summary>
    ///     The seam named by #861, and the one the issue names as the thing that
    ///     would die: the presenter's read, called by the render engine, called by
    ///     the frame. Reachable today — that is the fact this file pins.
    /// </summary>
    [Test]
    public async Task ThePresentersRead_IsReachable_FromAFrameInTheProduct()
    {
        SessionStatusReader? reader = Report.Readers
            .FirstOrDefault(r => r.File.EndsWith("ChatStreamingPresenter.cs", StringComparison.Ordinal));

        await Assert.That(reader).IsNotNull().Because(
            "the presenter is the last writer standing on a session's status, and #687's guard "
            + "is a rule about how a status is DECIDED — it cannot see whether anyone CALLS the "
            + "method. If the reader is gone entirely, the frame inlined its own verdict and this "
            + "file must say so");

        await Assert.That(reader!.IsReachable).IsTrue().Because(
            "ChatViewModel.RenderFrameTick (apps/Harbor.App.Avalonia/ViewModels/ChatViewModel.cs:142) "
            + "writes _renderEngine.DeriveStatus(state) into the tracker on every 16 ms frame, and "
            + "UiRenderEngine.DeriveStatus forwards to the presenter "
            + "(apps/Harbor.App.Avalonia/Services/UiRenderEngine.cs:156). That is the chain #687 "
            + "built, and the frame-named test could never have checked it. If this fails, the "
            + "rewritten line is _sessionManager.SetStatus(activeSession.Id, SessionStatus.Idle) "
            + "— an inline verdict — and the presenter is now dead code with a green suite around "
            + "it. Callers found: "
            + (reader.Callers.Count == 0 ? "(none)" : string.Join(" | ", reader.Callers)));
    }

    /// <summary>
    ///     THE GATE. Every reader that publishes the reducer's decision into the
    ///     tracker must be called by product code.
    /// </summary>
    /// <remarks>
    ///     A read of the TRACKER's own stored value is a different question and is
    ///     not ruled here: <c>SessionStatusService.GetStatus</c> and
    ///     <c>SessionManager.GetStatus</c> read what was written, and are reached
    ///     from the session list and the board. What must not happen is the frame's
    ///     READ — the one that carries the reducer's answer — losing its caller.
    /// </remarks>
    [Test]
    public async Task EveryReader_ThatPublishesTheReducersDecision_IsReachable_FromProductCode()
    {
        IReadOnlyList<SessionStatusReader> orphaned =
            [.. Report.Readers.Where(r => r.PublishesTheDecision && !r.IsReachable)];

        await Assert.That(orphaned).IsEmpty().Because(
            "a status the reducer decided is handed to the tracker through a read, and a read with "
            + "no product caller means something upstream started computing its own answer. #857's "
            + "rule makes the same argument for key hand-offs and holds two such routers in a "
            + "ledger; this seam is alive today, so there is nothing to ledger. Unreachable: "
            + (orphaned.Count == 0
                ? "(none)"
                : string.Join(" | ", orphaned.Select(o => $"{o.File}:{o.Line} {o.Method}"))));
    }

    /// <summary>
    ///     THE GATE #861 IS ABOUT. A test that names a frame its own project
    ///     cannot reference is promising a guarantee its reference graph cannot
    ///     enter.
    /// </summary>
    /// <remarks>
    ///     This is the assertion that goes red on the first commit and green on the
    ///     second, because the offending test is
    ///     <c>SessionStatusSingleSourceTests.WhateverTheReducerDecided_IsWhatTheNextFramePushes</c>
    ///     and the second commit renames it. The two ways to make it pass are
    ///     deliberately both available — the test project takes an <c>apps/</c>
    ///     reference, or the test stops claiming the frame — and this gate does not
    ///     adjudicate which, because #555 and the issue's own perimeter discussion
    ///     make that the owner's call.
    /// </remarks>
    [Test]
    public async Task NoTest_NamesAFrame_ThatItsOwnProjectCannotReference()
    {
        await Assert.That(Report.Claims).IsEmpty().Because(
            "a test whose name or comment promises a frame guarantee, in a project with no apps/ "
            + "reference, cannot fail when the frame is bypassed — the frame is not in its reference "
            + "graph at all. #861's test is the live instance: it says \"ChatViewModel.RenderFrameTick "
            + "calls DeriveStatus on every 16 ms frame … so the presenter is the last writer standing\" "
            + "and then asserts Presenter.DeriveStatus(state), which its project can reach but the "
            + "frame cannot. Offenders: "
            + (Report.Claims.Count == 0
                ? "(none)"
                : string.Join(" | ", Report.Claims.Select(c => $"{c.Test}:{c.Line} names {c.FrameMethod}"))));
    }

    /// <summary>
    ///     The scan walked a real checkout. Without this, every assertion above
    ///     could pass because the probe read nothing — the same failure as a
    ///     healthy zero, in the same costume #890 measured.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(Report.MissingPerimeterDirectories).IsEmpty().Because(
            "a perimeter directory this gate derived is absent from the checkout, so the scan read "
            + "nothing and every rule above is reporting absence rather than a verdict");

        await Assert.That(Report.FilesScanned).IsGreaterThan(500).Because(
            "the scan must have walked src/ and apps/. A guard whose scan silently returns zero "
            + "files cannot be told apart from a guard that found nothing");

        await Assert.That(Report.Readers.Count).IsGreaterThan(0).Because(
            "the matcher for a SessionStatus-returning declaration stopped working. Read: "
            + Report.FilesScanned);

        await Assert.That(Report.Frames.Count).IsGreaterThan(0).Because(
            "the FRAME must be discoverable by pairing each decision-publish reader with its "
            + "callers. If this is zero the claim gate above has nothing to match against and "
            + "passes vacuously — the exact failure it exists to catch, one level up");
    }

    /// <summary>
    ///     The reader CLASSIFIER must work, or the gate above passes vacuously: a
    ///     matcher that cannot read a parameter type reports <c>null</c> for every
    ///     reader, classifies all of them as tracker reads, and the gate has
    ///     nothing left to rule on.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheMatcherSeparatesTheTwoKindsOfReader()
    {
        SessionStatusReader? publisher = Report.Readers
            .FirstOrDefault(r => r.File.EndsWith("ChatStreamingPresenter.cs", StringComparison.Ordinal));
        SessionStatusReader? trackerRead = Report.Readers
            .FirstOrDefault(r => r.Method == "GetStatus");

        await Assert.That(publisher).IsNotNull().Because(
            "the presenter's read is the decision-publish shape; see the rule above");
        await Assert.That(publisher!.PublishesTheDecision).IsTrue().Because(
            "ChatStreamingPresenter.DeriveStatus takes a UiState — the state the reducer produced — "
            + "so it is a projection of the decision, and it is the read the frame must keep "
            + "calling. Parameter read: " + (publisher.ParameterType ?? "(none)"));

        await Assert.That(trackerRead).IsNotNull().Because(
            "the tree has tracker reads (SessionStatusService.GetStatus, SessionManager.GetStatus); "
            + "if the matcher stopped finding them the classification below is untested");
        await Assert.That(trackerRead!.PublishesTheDecision).IsFalse().Because(
            "a read that takes a sessionId asks the store what it already holds — a different "
            + "question from carrying the reducer's answer, and ruling on it here would demand "
            + "callers it does not need. Parameter read: "
            + (trackerRead.ParameterType ?? "(none)"));
    }

    /// <summary>
    ///     THE CONTROL, and the reason the guard is not green by construction: it
    ///     hands the matcher a reader and a caller that do not exist in this
    ///     checkout and REQUIRES the reader to come back reported AND reachable.
    /// </summary>
    /// <remarks>
    ///     #890's acceptance criterion, reused: an enumeration cannot be shown
    ///     acquiring a file it was not given, so a transcription — a list of the
    ///     two <c>DeriveStatus</c> names — fails this control while a derived scan
    ///     passes it. If this test fails, the matcher does not see synthetic source
    ///     and the green above is not evidence.
    /// </remarks>
    [Test]
    public async Task Probe_CanAcquireACallerItWasNeverGiven()
    {
        const string syntheticReader = "src/Harbor.Ui.Framework.Synthetic/SyntheticStatusReader.cs";
        const string syntheticCaller = "src/Harbor.Ui.Framework.Synthetic/SyntheticFrame.cs";
        string[] readerLines =
        [
            "namespace Harbor.Ui.Framework.Synthetic;",
            "internal sealed class SyntheticStatusReader",
            "{",
            "    public SessionStatus DeriveStatus(UiState state) => state.Chat.SessionStatus;",
            "}",
        ];
        string[] callerLines =
        [
            "namespace Harbor.Ui.Framework.Synthetic;",
            "internal static class SyntheticFrame",
            "{",
            "    internal static void Push(UiState state) => _ = reader.DeriveStatus(state);",
            "}",
        ];

        var readers = new List<SessionStatusReader>();
        SessionStatusFrameProbe.CollectReaders(
            syntheticReader, readerLines, [(syntheticCaller, callerLines)], readers);

        SessionStatusReader? acquired = readers.FirstOrDefault(r => r.Method == "DeriveStatus");

        await Assert.That(acquired).IsNotNull().Because(
            "the control hands the matcher a SessionStatus-returning declaration in a file that is "
            + "not in this checkout. A transcription of today's two real readers cannot produce it; "
            + "only a matcher that reads the source it is given can. Read: "
            + string.Join(" | ", readers.Select(r => $"{r.File}:{r.Line} {r.Method}")));

        await Assert.That(acquired!.IsReachable).IsTrue().Because(
            "the control's second file CALLS DeriveStatus, and the caller must be attributed to the "
            + "reader. A probe that finds the declaration but cannot pair it with its call site "
            + "would report every reader as unreachable — which is the failure the gate exists to "
            + "detect, reported here instead, where it is visible. Callers: "
            + string.Join(" | ", acquired.Callers));

        await Assert.That(acquired.Callers.Any(c => c.StartsWith(syntheticCaller, StringComparison.Ordinal)))
            .IsTrue().Because(
            "the caller the control injected is the ONLY thing that can make this reader reachable, "
            + "so if this is false the pairing came from somewhere other than the file the control "
            + "handed over, and the control is not testing what it claims");
    }
}