using Harbor.Abstractions.Speech;
using Harbor.Tui.CellForge.Generic.Widgets;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering.Input;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// #2541: the composer's public path is the seam under test. A mock backend pushes
// transcripts through ISpeechToText; the assertions read only what a user sees —
// the composer buffer and the DictationBuffer status — never private fields.
public class DictationComposerTests
{
    private static KeyEvent CtrlD() => KeyEvent.Char(new Rune('d'), KeyModifiers.Ctrl);

    [Test]
    public async Task CtrlD_IsClaimedAsCommand_AndStartsSession()
    {
        var composer = new ComposerController(new MockSpeechToText());
        _ = composer.Buffer.InsertText("draft");

        await Assert.That(composer.HandleKey(CtrlD())).IsEqualTo(ComposerAction.DictationToggle);
        await Assert.That(await composer.ToggleDictationAsync(agentStreaming: false)).IsTrue();
        await Assert.That(composer.Dictation.Status).IsEqualTo(DictationStatus.Listening);
        await Assert.That(composer.Dictation.IsActive).IsTrue();
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft");
    }

    [Test]
    public async Task CtrlD_CommandGate_RefusesShiftAndRepeat()
    {
        var composer = new ComposerController(new MockSpeechToText());

        await Assert.That(composer.HandleKey(
            KeyEvent.Char(new Rune('D'), KeyModifiers.Ctrl | KeyModifiers.Shift)))
            .IsEqualTo(ComposerAction.Ignored);
        await Assert.That(composer.HandleKey(new KeyEvent(
            KeyCode.Char, new Rune('d'), KeyModifiers.Ctrl, KeyEventType.Repeat, false)))
            .IsEqualTo(ComposerAction.Ignored);
    }

    [Test]
    public async Task Partials_Replace_PreviousPartial_WithoutDuplicating()
    {
        var stt = new MockSpeechToText();
        var composer = new ComposerController(stt);
        _ = composer.Buffer.InsertText("draft");
        _ = await composer.ToggleDictationAsync(agentStreaming: false);

        stt.Partial("hello");
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft hello");
        stt.Partial("hello world");
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft hello world");
        await Assert.That(composer.Dictation.Text).IsEqualTo("draft hello world");
        await Assert.That(composer.Dictation.Status).IsEqualTo(DictationStatus.Transcribing);
    }

    [Test]
    public async Task Final_CommitsExactlyOnce()
    {
        var stt = new MockSpeechToText();
        var composer = new ComposerController(stt);
        _ = composer.Buffer.InsertText("draft");
        _ = await composer.ToggleDictationAsync(agentStreaming: false);

        stt.Final("hello world");
        stt.Final("must be ignored");

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft hello world");
        await Assert.That(composer.Dictation.CommitCount).IsEqualTo(1);
        await Assert.That(composer.Dictation.IsActive).IsFalse();
        await Assert.That(composer.Dictation.StatusLabel).IsEqualTo("");
    }

    [Test]
    public async Task CtrlDStop_CommitsBackendFinalOnce()
    {
        var stt = new MockSpeechToText(finalOnStop: "captured");
        var composer = new ComposerController(stt);
        _ = composer.Buffer.InsertText("draft");
        _ = await composer.ToggleDictationAsync(agentStreaming: false);
        stt.Partial("captur");

        bool stopped = await composer.ToggleDictationAsync(agentStreaming: false);

        await Assert.That(stopped).IsTrue();
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft captured");
        await Assert.That(composer.Dictation.CommitCount).IsEqualTo(1);
    }

    [Test]
    public async Task Cancel_RestoresBaseline_AndIgnoresLateFinal()
    {
        var stt = new MockSpeechToText(finalOnStop: "late final");
        var composer = new ComposerController(stt);
        _ = composer.Buffer.InsertText("draft");
        _ = await composer.ToggleDictationAsync(agentStreaming: false);
        stt.Partial("half utterance");

        await composer.CancelDictationAsync();

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft");
        await Assert.That(composer.Dictation.CommitCount).IsEqualTo(0);
        await Assert.That(composer.Dictation.IsActive).IsFalse();
    }

    [Test]
    public async Task Start_IsRefusedWhileAgentStreams()
    {
        var composer = new ComposerController(new MockSpeechToText());

        bool started = await composer.ToggleDictationAsync(agentStreaming: true);

        await Assert.That(started).IsFalse();
        await Assert.That(composer.DictationRejection).IsNotNull();
        await Assert.That(composer.Dictation.Status).IsEqualTo(DictationStatus.Idle);
    }

    [Test]
    public async Task OfflineBackend_StartStop_LeavesComposerTextAlone()
    {
        var composer = new ComposerController();
        _ = composer.Buffer.InsertText("draft");

        await Assert.That(composer.SpeechToText.IsAvailable).IsFalse();
        _ = await composer.ToggleDictationAsync(agentStreaming: false);
        _ = await composer.ToggleDictationAsync(agentStreaming: false);

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft");
        await Assert.That(composer.Dictation.Status).IsEqualTo(DictationStatus.Idle);
    }

    private sealed class MockSpeechToText(string? finalOnStop = null) : ISpeechToText
    {
        private readonly TaskCompletionSource<bool> _ended =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<string>? _onPartial;
        private Action<string>? _onFinal;

        public bool IsAvailable => true;

        public Task StartAsync(
            Action<string> onPartial,
            Action<string> onFinal,
            CancellationToken cancellationToken = default)
        {
            _onPartial = onPartial;
            _onFinal = onFinal;
            _ = cancellationToken.Register(() =>
            {
                if (finalOnStop is not null)
                {
                    onFinal(finalOnStop);
                }

                _ended.TrySetResult(true);
            });
            return _ended.Task;
        }

        public void Partial(string text) => _onPartial?.Invoke(text);

        public void Final(string text) => _onFinal?.Invoke(text);
    }
}
