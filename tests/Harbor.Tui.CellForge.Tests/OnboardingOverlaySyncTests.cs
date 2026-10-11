using System.Text;
using Harbor.Tui.CellForge.Onboarding;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #1248, slice 1: the flow's decoded-key ingress and the host's
/// dialog mirror (<see cref="DialogOverlay.SyncFrom"/>). Pure state —
/// no painting, no stores.
/// </summary>
public class OnboardingOverlaySyncTests
{
    private static readonly OnboardingProvider Kilo =
        new("kilocode", "Kilo Code Gateway", "tencent/hy3:free", true, "Get a free key at https://kilo.ai");

    private static readonly OnboardingProvider Ollama =
        new("ollama", "Ollama (local)", "llama3.2", false, null);

    private static OnboardingFlow NewFlow() => new([Kilo, Ollama]);

    private static void Type(OnboardingFlow flow, string text)
    {
        foreach (char c in text)
        {
            flow.HandleKey(KeyEvent.Char(new Rune(c)));
        }
    }

    [Test]
    public async Task KeyEvent_Enter_AdvancesWelcomeToProvider()
    {
        var flow = NewFlow();
        flow.Start();

        await Assert.That(flow.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsTrue();
        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Provider);
        await Assert.That(flow.Dialog.Kind).IsEqualTo(DialogKind.Prompt);
    }

    [Test]
    public async Task KeyEvent_Typing_SelectsProviderByNumber()
    {
        var flow = NewFlow();
        flow.Start();
        flow.HandleKey(KeyEvent.Simple(KeyCode.Enter));

        Type(flow, "2");
        flow.HandleKey(KeyEvent.Simple(KeyCode.Enter));

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Model);
        await Assert.That(flow.SelectedProvider.Value).IsEqualTo(Ollama);
    }

    [Test]
    public async Task KeyEvent_Release_AppliesNothing()
    {
        var flow = NewFlow();
        flow.Start();

        var releaseEnter = new KeyEvent(KeyCode.Enter, default, KeyModifiers.None, KeyEventType.Release, isKittyEncoded: true);
        var releaseEsc = new KeyEvent(KeyCode.Escape, default, KeyModifiers.None, KeyEventType.Release, isKittyEncoded: true);

        await Assert.That(flow.HandleKey(releaseEnter)).IsFalse();
        await Assert.That(flow.HandleKey(releaseEsc)).IsFalse();
        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Welcome);
        await Assert.That(flow.IsComplete).IsFalse();
    }

    [Test]
    public async Task KeyEvent_Escape_Cancels()
    {
        var flow = NewFlow();
        flow.Start();

        await Assert.That(flow.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(flow.IsComplete).IsTrue();
        await Assert.That(flow.Result.Value.Completed).IsFalse();
    }

    [Test]
    public async Task SyncFrom_CopiesPromptInputAndFocus()
    {
        var flow = NewFlow();
        flow.Start();
        flow.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        Type(flow, "2");
        flow.HandleKey(KeyEvent.Simple(KeyCode.Tab)); // focus Cancel

        var screen = new DialogOverlay();
        screen.SyncFrom(flow.Dialog);

        await Assert.That(screen.Visible).IsTrue();
        await Assert.That(screen.Kind).IsEqualTo(flow.Dialog.Kind);
        await Assert.That(screen.Title).IsEqualTo(flow.Dialog.Title);
        await Assert.That(screen.Message).IsEqualTo(flow.Dialog.Message);
        await Assert.That(screen.Input).IsEqualTo("2");
        await Assert.That(screen.FocusedButtonIndex).IsEqualTo(1);
        await Assert.That(screen.Buttons.Count).IsEqualTo(flow.Dialog.Buttons.Count);
    }

    [Test]
    public async Task SyncFrom_HidesTargetWhenFlowDismissed()
    {
        var flow = NewFlow();
        flow.Start();
        flow.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        Type(flow, "2");
        flow.HandleKey(KeyEvent.Simple(KeyCode.Enter)); // local skips auth → model
        flow.HandleKey(KeyEvent.Simple(KeyCode.Enter)); // default model → done
        flow.HandleKey(KeyEvent.Simple(KeyCode.Enter)); // done → finished

        await Assert.That(flow.IsComplete).IsTrue();

        var screen = new DialogOverlay();
        screen.ShowAlert("stale", "stale");
        screen.SyncFrom(flow.Dialog);

        await Assert.That(screen.Visible).IsFalse();
    }
}
