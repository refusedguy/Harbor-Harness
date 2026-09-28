using Harbor.Tui.CellForge.Onboarding;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// First-run onboarding flow ([UX8]): step machine over DialogOverlay
/// primitives. Pure state transitions — no painting, no backends.
/// </summary>
public class OnboardingFlowTests
{
    private static readonly OnboardingProvider Kilo =
        new("kilocode", "Kilo Code Gateway", "tencent/hy3:free", true, "Get a free key at https://kilo.ai");

    private static readonly OnboardingProvider Ollama =
        new("ollama", "Ollama (local)", "llama3.2", false, null);

    private static readonly OnboardingProvider Anthropic =
        new("anthropic", "Anthropic (Claude)", "claude-sonnet-4-20250514", true, "Get a key at https://console.anthropic.com");

    private static readonly ConsoleKeyInfo EnterKey = new('\r', ConsoleKey.Enter, false, false, false);
    private static readonly ConsoleKeyInfo EscKey = new('\x1b', ConsoleKey.Escape, false, false, false);
    private static readonly ConsoleKeyInfo TabKey = new('\t', ConsoleKey.Tab, false, false, false);

    private static OnboardingFlow NewFlow(
        IReadOnlyCollection<string>? configured = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? liveModels = null) =>
        new([Kilo, Ollama, Anthropic], configured, liveModels);

    // DialogOverlay prompt editing only inspects Key for Enter/Backspace —
    // any other Key value types KeyChar verbatim.
    private static ConsoleKeyInfo CharKey(char c) => new(c, ConsoleKey.A, false, false, false);

    private static void Type(OnboardingFlow flow, string text)
    {
        foreach (char c in text)
        {
            flow.HandleKey(CharKey(c));
        }
    }

    private static void AdvanceToProvider(OnboardingFlow flow)
    {
        flow.Start();
        flow.HandleKey(EnterKey);
    }

    [Test]
    public async Task Start_ShowsWelcomeAlert()
    {
        var flow = NewFlow();
        flow.Start();

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Welcome);
        await Assert.That(flow.Dialog.Visible).IsTrue();
        await Assert.That(flow.Dialog.Kind).IsEqualTo(DialogKind.Alert);
        await Assert.That(flow.Dialog.Title).IsEqualTo("Welcome to Harbor");
        await Assert.That(flow.IsComplete).IsFalse();
        await Assert.That(flow.Result).IsNull();
    }

    [Test]
    public async Task Welcome_Enter_OpensProviderPromptWithCatalogue()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Provider);
        await Assert.That(flow.Dialog.Kind).IsEqualTo(DialogKind.Prompt);
        await Assert.That(flow.Dialog.Message.Contains("kilocode")).IsTrue();
        await Assert.That(flow.Dialog.Message.Contains("ollama")).IsTrue();
    }

    [Test]
    public async Task Provider_ByNumber_SelectsKeyProvider_AndAsksAuth()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);

        Type(flow, "1");
        flow.HandleKey(EnterKey);

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Auth);
        await Assert.That(flow.SelectedProvider).IsEqualTo(Kilo);
        await Assert.That(flow.Dialog.Title.Contains("kilocode")).IsTrue();
    }

    [Test]
    public async Task Provider_ById_CaseInsensitive_LocalSkipsAuth()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);

        Type(flow, "OLLAMA");
        flow.HandleKey(EnterKey);

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Model);
        await Assert.That(flow.SelectedProvider).IsEqualTo(Ollama);
    }

    [Test]
    public async Task Provider_InvalidInput_StaysWithError()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);

        Type(flow, "nope");
        flow.HandleKey(EnterKey);

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Provider);
        await Assert.That(flow.Dialog.Message.Contains("Unknown provider")).IsTrue();
    }

    [Test]
    public async Task Provider_EmptyInput_StaysWithHint()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);

        flow.HandleKey(EnterKey);

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Provider);
        await Assert.That(flow.Dialog.Message.Contains("number or id")).IsTrue();
    }

    [Test]
    public async Task Auth_EmptyKey_StaysWithError()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);
        Type(flow, "1");
        flow.HandleKey(EnterKey);

        flow.HandleKey(EnterKey);

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Auth);
        await Assert.That(flow.Dialog.Message.Contains("must not be empty")).IsTrue();
    }

    [Test]
    public async Task Auth_ValidKey_AdvancesToModelPrefilledWithDefault()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);
        Type(flow, "1");
        flow.HandleKey(EnterKey);

        Type(flow, "klo_secret");
        flow.HandleKey(EnterKey);

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Model);
        await Assert.That(flow.Dialog.Input).IsEqualTo("tencent/hy3:free");
    }

    [Test]
    public async Task Auth_Skipped_WhenAlreadyConfigured()
    {
        var flow = NewFlow(configured: ["anthropic"]);
        AdvanceToProvider(flow);
        Type(flow, "3");
        flow.HandleKey(EnterKey);

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Model);

        flow.HandleKey(EnterKey); // default model
        flow.HandleKey(EnterKey); // done

        await Assert.That(flow.IsComplete).IsTrue();
        await Assert.That(flow.Result!.KeyAlreadyConfigured).IsTrue();
        await Assert.That(flow.Result.ApiKey).IsNull();
        await Assert.That(flow.Result.Model).IsEqualTo("anthropic/claude-sonnet-4-20250514");
    }

    [Test]
    public async Task FullFlow_LocalProvider_DefaultModel_Completes()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);
        Type(flow, "2");
        flow.HandleKey(EnterKey);

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Model);
        flow.HandleKey(EnterKey); // accept default

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Done);
        await Assert.That(flow.Dialog.Kind).IsEqualTo(DialogKind.Alert);
        flow.HandleKey(EnterKey);

        await Assert.That(flow.IsComplete).IsTrue();
        await Assert.That(flow.Dialog.Visible).IsFalse();
        await Assert.That(flow.Result!.Completed).IsTrue();
        await Assert.That(flow.Result.ProviderId).IsEqualTo("ollama");
        await Assert.That(flow.Result.Model).IsEqualTo("ollama/llama3.2");
        await Assert.That(flow.Result.ApiKey).IsNull();
        await Assert.That(flow.Result.KeyAlreadyConfigured).IsFalse();
    }

    [Test]
    public async Task FullFlow_KeyProvider_CustomModel_PrefixedWithProvider()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);
        Type(flow, "kilocode");
        flow.HandleKey(EnterKey);
        Type(flow, "klo_123");
        flow.HandleKey(EnterKey);

        Type(flow, "my-model");
        flow.HandleKey(EnterKey);
        flow.HandleKey(EnterKey);

        await Assert.That(flow.Result!.Completed).IsTrue();
        await Assert.That(flow.Result.ProviderId).IsEqualTo("kilocode");
        await Assert.That(flow.Result.Model).IsEqualTo("kilocode/my-model");
        await Assert.That(flow.Result.ApiKey).IsEqualTo("klo_123");
    }

    [Test]
    public async Task Model_SlashedId_KeptAsIs()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);
        Type(flow, "2");
        flow.HandleKey(EnterKey);

        Type(flow, "otherprov/some-model");
        flow.HandleKey(EnterKey);
        flow.HandleKey(EnterKey);

        await Assert.That(flow.Result!.Model).IsEqualTo("otherprov/some-model");
    }

    [Test]
    public async Task Model_LiveList_NumberSelects()
    {
        var flow = NewFlow(liveModels: new Dictionary<string, IReadOnlyList<string>>
        {
            ["kilocode"] = ["alpha", "beta"],
        });
        AdvanceToProvider(flow);
        Type(flow, "1");
        flow.HandleKey(EnterKey);
        Type(flow, "sk_test");
        flow.HandleKey(EnterKey);

        await Assert.That(flow.Dialog.Message.Contains("[1] alpha")).IsTrue();

        Type(flow, "2");
        flow.HandleKey(EnterKey);
        flow.HandleKey(EnterKey);

        await Assert.That(flow.Result!.Model).IsEqualTo("kilocode/beta");
    }

    [Test]
    public async Task Esc_Cancels_FromWelcome()
    {
        var flow = NewFlow();
        flow.Start();
        flow.HandleKey(EscKey);

        await Assert.That(flow.IsComplete).IsTrue();
        await Assert.That(flow.Result!.Completed).IsFalse();
        await Assert.That(flow.Dialog.Visible).IsFalse();
    }

    [Test]
    public async Task Esc_Cancels_FromAuth()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);
        Type(flow, "1");
        flow.HandleKey(EnterKey);

        flow.HandleKey(EscKey);

        await Assert.That(flow.IsComplete).IsTrue();
        await Assert.That(flow.Result!.Completed).IsFalse();
        await Assert.That(flow.Result.ProviderId).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Esc_Cancels_FromDone()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);
        Type(flow, "2");
        flow.HandleKey(EnterKey);
        flow.HandleKey(EnterKey);

        await Assert.That(flow.Step).IsEqualTo(OnboardingStep.Done);
        flow.HandleKey(EscKey);

        await Assert.That(flow.Result!.Completed).IsFalse();
    }

    [Test]
    public async Task CancelButtonFocused_Enter_Cancels()
    {
        var flow = NewFlow();
        AdvanceToProvider(flow);

        flow.HandleKey(TabKey); // focus Cancel
        flow.HandleKey(EnterKey);

        await Assert.That(flow.IsComplete).IsTrue();
        await Assert.That(flow.Result!.Completed).IsFalse();
    }

    [Test]
    public async Task HandleKey_AfterFinish_ReturnsFalse()
    {
        var flow = NewFlow();
        flow.Start();
        flow.HandleKey(EscKey);

        await Assert.That(flow.HandleKey(EnterKey)).IsFalse();
    }

    [Test]
    public async Task Start_EmptyCatalogue_FinishesCancelled()
    {
        var flow = new OnboardingFlow([]);
        flow.Start();

        await Assert.That(flow.IsComplete).IsTrue();
        await Assert.That(flow.Result!.Completed).IsFalse();
    }
}
