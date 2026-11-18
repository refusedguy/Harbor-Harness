using Harbor.Tui.CellForge.Generic.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// Domain-neutral core of #398 (a #23 killer-feature slice): the transcript
// contract a composer needs. These tests pin behaviour the host can observe —
// what Text shows, what StatusLabel says, and whether a session was refused —
// not how the buffer stores its strings.
public class DictationBufferTests
{
    [Test]
    public async Task NewBuffer_IsIdleWithBaselineText()
    {
        var buffer = new DictationBuffer("draft");

        await Assert.That(buffer.Status).IsEqualTo(DictationStatus.Idle);
        await Assert.That(buffer.StatusLabel).IsEqualTo("");
        await Assert.That(buffer.IsActive).IsFalse();
        await Assert.That(buffer.Text).IsEqualTo("draft");
        await Assert.That(buffer.CommitCount).IsEqualTo(0);
    }

    [Test]
    public async Task TryStart_CapturesBaselineAndShowsListening()
    {
        var buffer = new DictationBuffer();
        string? rejection = "unset";

        bool started = buffer.TryStart("draft", agentStreaming: false, out rejection);

        await Assert.That(started).IsTrue();
        await Assert.That(rejection).IsNull();
        await Assert.That(buffer.Status).IsEqualTo(DictationStatus.Listening);
        await Assert.That(buffer.StatusLabel).IsEqualTo("listening");
        await Assert.That(buffer.IsActive).IsTrue();
        await Assert.That(buffer.Baseline).IsEqualTo("draft");
        await Assert.That(buffer.Text).IsEqualTo("draft");
    }

    [Test]
    public async Task AcceptPartial_ReplacesPreviousPartialInsteadOfAppending()
    {
        var buffer = new DictationBuffer();
        _ = buffer.TryStart("draft", agentStreaming: false, out _);

        buffer.AcceptPartial("hello");
        await Assert.That(buffer.Text).IsEqualTo("draft hello");
        await Assert.That(buffer.Status).IsEqualTo(DictationStatus.Transcribing);
        await Assert.That(buffer.StatusLabel).IsEqualTo("transcribing");

        buffer.AcceptPartial("hello world");
        await Assert.That(buffer.Text).IsEqualTo("draft hello world");
    }

    [Test]
    public async Task AcceptPartial_WithEmptyBaseline_HasNoLeadingSpace()
    {
        var buffer = new DictationBuffer();
        _ = buffer.TryStart("", agentStreaming: false, out _);

        buffer.AcceptPartial("hello");

        await Assert.That(buffer.Text).IsEqualTo("hello");
    }

    [Test]
    public async Task AcceptFinal_CommitsExactlyOnceAndHidesIndicator()
    {
        var buffer = new DictationBuffer();
        _ = buffer.TryStart("draft", agentStreaming: false, out _);
        buffer.AcceptPartial("hello world");

        buffer.AcceptFinal("hello world");

        await Assert.That(buffer.Text).IsEqualTo("draft hello world");
        await Assert.That(buffer.Status).IsEqualTo(DictationStatus.Idle);
        await Assert.That(buffer.StatusLabel).IsEqualTo("");
        await Assert.That(buffer.IsActive).IsFalse();
        await Assert.That(buffer.CommitCount).IsEqualTo(1);

        buffer.AcceptFinal("ignored");

        await Assert.That(buffer.Text).IsEqualTo("draft hello world");
        await Assert.That(buffer.CommitCount).IsEqualTo(1);
    }

    [Test]
    public async Task Cancel_RestoresPreDictationBufferWithoutCommitting()
    {
        var buffer = new DictationBuffer("draft");
        _ = buffer.TryStart("draft", agentStreaming: false, out _);
        buffer.AcceptPartial("discarded words");

        buffer.Cancel();

        await Assert.That(buffer.Text).IsEqualTo("draft");
        await Assert.That(buffer.Status).IsEqualTo(DictationStatus.Idle);
        await Assert.That(buffer.StatusLabel).IsEqualTo("");
        await Assert.That(buffer.CommitCount).IsEqualTo(0);
    }

    [Test]
    public async Task TryStart_WhileAgentStreaming_RefusesWithoutChangingState()
    {
        var buffer = new DictationBuffer("draft");

        bool started = buffer.TryStart("draft", agentStreaming: true, out string? rejection);

        await Assert.That(started).IsFalse();
        await Assert.That(rejection).IsNotNull();
        await Assert.That(rejection!).Contains("streaming");
        await Assert.That(buffer.Status).IsEqualTo(DictationStatus.Idle);
        await Assert.That(buffer.Text).IsEqualTo("draft");
        await Assert.That(buffer.CommitCount).IsEqualTo(0);
    }

    [Test]
    public async Task TryStart_WhileActive_RefusesSecondSession()
    {
        var buffer = new DictationBuffer("draft");
        _ = buffer.TryStart("draft", agentStreaming: false, out _);
        buffer.AcceptPartial("first");

        bool started = buffer.TryStart("other", agentStreaming: false, out string? rejection);

        await Assert.That(started).IsFalse();
        await Assert.That(rejection).IsNotNull();
        await Assert.That(rejection!).Contains("already");
        await Assert.That(buffer.Text).IsEqualTo("draft first");
        await Assert.That(buffer.Baseline).IsEqualTo("draft");
    }

    [Test]
    public async Task Fail_ShowsErrorAndFallsBackToBaseline()
    {
        var buffer = new DictationBuffer("draft");
        _ = buffer.TryStart("draft", agentStreaming: false, out _);
        buffer.AcceptPartial("partial");

        buffer.Fail("no input device");

        await Assert.That(buffer.Status).IsEqualTo(DictationStatus.Error);
        await Assert.That(buffer.StatusLabel).IsEqualTo("error");
        await Assert.That(buffer.Error).IsEqualTo("no input device");
        await Assert.That(buffer.Text).IsEqualTo("draft");

        buffer.Cancel();

        await Assert.That(buffer.Status).IsEqualTo(DictationStatus.Idle);
        await Assert.That(buffer.Error).IsNull();
        await Assert.That(buffer.StatusLabel).IsEqualTo("");
    }

    [Test]
    public async Task UpdatesWhileIdle_AreNoOps()
    {
        var buffer = new DictationBuffer("draft");

        buffer.AcceptPartial("ignored");
        buffer.AcceptFinal("ignored");
        buffer.Fail("ignored");
        buffer.Cancel();

        await Assert.That(buffer.Text).IsEqualTo("draft");
        await Assert.That(buffer.Status).IsEqualTo(DictationStatus.Idle);
        await Assert.That(buffer.Error).IsNull();
        await Assert.That(buffer.CommitCount).IsEqualTo(0);
    }

    [Test]
    public async Task NullInputs_AreTreatedAsEmpty()
    {
        var buffer = new DictationBuffer(null);
        _ = buffer.TryStart(null, agentStreaming: false, out _);
        buffer.AcceptPartial(null);

        await Assert.That(buffer.Text).IsEqualTo("");

        buffer.AcceptFinal(null);

        await Assert.That(buffer.Text).IsEqualTo("");
        await Assert.That(buffer.CommitCount).IsEqualTo(1);
    }
}
