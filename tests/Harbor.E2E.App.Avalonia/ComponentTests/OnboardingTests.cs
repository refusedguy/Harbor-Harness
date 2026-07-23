using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

using HarborApp = global::Harbor.App.Avalonia.App;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Onboarding wizard component E2E tests — every step + Back + Skip.
/// </summary>
/// <remarks>
///     <para>
///         Tests cover: step 1 (welcome), step 2 (providers), step 3 (API key),
///         step 4 (default model), step 5 (theme), Back button navigation,
///         Skip button. Each test opens a fresh <see cref="OnboardingWindow"/>
///         bound to a fresh <see cref="OnboardingViewModel"/> from the DI
///         container, sets the requested step, and captures a screenshot.
///     </para>
///     <para>
///         The onboarding window is a separate Avalonia <see cref="Window"/>
///         (not a child of <c>MainWindow</c>), so we capture it via
///         <see cref="ComponentTestBase.CaptureOnboardingWindowAsync"/>.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class OnboardingTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync().ConfigureAwait(false);

    /// <summary>Open a fresh onboarding window bound to a fresh VM at the given step.</summary>
    private async Task<(OnboardingWindow window, OnboardingViewModel vm)> OpenOnboardingAsync(int step)
    {
        var services = HarborApp.Services;
        var vm = services.GetRequiredService<OnboardingViewModel>();
        if (step >= 1) UI(() => vm.CurrentStep = step);
        if (step >= 4) UI(() => vm.DefaultModel = vm.SelectedProvider?.DefaultModel ?? "qwen2.5-coder:7b");

        var window = Dispatcher.UIThread.InvokeAsync<OnboardingWindow>(() =>
        {
            var w = new OnboardingWindow();
            w.Bind(vm);
            w.DataContext = vm;
            w.Show();
            return w;
        }).GetAwaiter().GetResult();

        await Task.Delay(180).ConfigureAwait(false);
        return (window, vm);
    }

    /// <summary>Close the onboarding window on the UI thread.</summary>
    private static void CloseWindow(OnboardingWindow window)
    {
        Dispatcher.UIThread
            .InvokeAsync(() => window.Close())
            .GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Step 1: shows the "Welcome to Harbor" title, brand ⚓, and the
    ///     welcome message about a local-first AI coding agent.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_Step1_Welcome()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 1).ConfigureAwait(false);
        try
        {
            var sawBrand = await Driver.WaitForTextAsync("Harbor", TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await Assert.That(sawBrand).IsTrue();
            var step = UI(() => vm.CurrentStep);
            await Assert.That(step).IsEqualTo(1);

            var path = await CaptureOnboardingWindowAsync(window, "onboarding-step1-welcome")
                .ConfigureAwait(false);

            var vlm = await VlmVerifier.VerifyAsync(
                path,
                "Onboarding wizard, Step 1 of 5 — Welcome. Window title 'Harbor — Welcome'. Top-left brand: ⚓ + 'Harbor'. " +
                "Below: a 5-dot progress stepper (dot 1 is filled/accent-coloured, dots 2-5 are muted). " +
                "Step title: 'Welcome to Harbor'. Caption below: 'Step 1 of 5'. " +
                "Body: a 🚀 emoji, the text 'Your local-first AI coding agent.', a longer description, " +
                "and a green-bordered tip '💡 Tip: pick Ollama if you want to run models locally — no API key required.' " +
                "Footer: 'Skip' (left) + 'Next →' button (right, primary).",
                nameof(Onboarding_Step1_Welcome)).ConfigureAwait(false);
            
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>
    ///     Step 2: shows the provider catalogue with checkboxes + icons.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_Step2_Providers()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 2).ConfigureAwait(false);
        try
        {
            var hasAnthropic = await Driver.WaitForTextAsync("Anthropic", TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            var hasOllama = await Driver.WaitForTextAsync("Ollama", TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await Assert.That(hasAnthropic || hasOllama).IsTrue();

            var path = await CaptureOnboardingWindowAsync(window, "onboarding-step2-providers")
                .ConfigureAwait(false);

            var vlm = await VlmVerifier.VerifyAsync(
                path,
                "Onboarding wizard, Step 2 of 5 — Choose your providers. Progress stepper: dot 2 filled, dots 1+3-5 muted " +
                "(dot 1 may also be filled as 'completed'). Step title 'Choose your providers'. " +
                "Body: a scrollable list of provider rows, each with a checkbox, an emoji icon (🤖 Anthropic, 🌐 OpenAI, " +
                "🛰️ OpenRouter, 🐋 DeepSeek, ⚡ Groq, 🌬️ Mistral, ✖️ xAI, 🤝 Together AI, 🎆 Fireworks, 🧠 Cerebras, " +
                "⌨️ Kilo Code, 🦙 Ollama), the display name, an env-var hint or 'local — no key required', and a small " +
                "model-id chip on the right. Ollama's checkbox is pre-selected (default). " +
                "Footer: 'Skip' + '← Back' + 'Next →' buttons.",
                nameof(Onboarding_Step2_Providers)).ConfigureAwait(false);
            
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>
    ///     Step 3: shows the API key input for the selected provider.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_Step3_ApiKey()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 3).ConfigureAwait(false);
        try
        {
            // Select Anthropic (requires key) so the API-key input is visible.
            UI(() =>
            {
                var anthropic = vm.Providers.First(p => p.Id == "anthropic");
                anthropic.IsSelected = true;
                vm.RefreshSelectedProviderCommand.Execute(null);
            });
            await Task.Delay(150).ConfigureAwait(false);

            var hasApiKey = await Driver.WaitForTextAsync("API key", TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await Assert.That(hasApiKey).IsTrue();

            var path = await CaptureOnboardingWindowAsync(window, "onboarding-step3-apikey")
                .ConfigureAwait(false);

            var vlm = await VlmVerifier.VerifyAsync(
                path,
                "Onboarding wizard, Step 3 of 5 — Enter API key. Progress stepper: dot 3 filled. " +
                "Body shows the selected provider's icon (🤖) + display name 'Anthropic' + 'Enter your API key'. " +
                "A text input below for the key (with password masking •). A small helper text mentioning " +
                "'Will be saved to the ANTHROPIC_API_KEY field in ~/.harbor/config.json'. " +
                "Footer: 'Skip' + '← Back' + 'Next →' buttons.",
                nameof(Onboarding_Step3_ApiKey)).ConfigureAwait(false);
            
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>
    ///     Step 4: shows the default-model input prefilled with the selected
    ///     provider's suggested model.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_Step4_Model()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 4).ConfigureAwait(false);
        try
        {
            var hasModel = await Driver.WaitForTextAsync("default model", TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await Assert.That(hasModel).IsTrue();

            var path = await CaptureOnboardingWindowAsync(window, "onboarding-step4-model")
                .ConfigureAwait(false);

            var vlm = await VlmVerifier.VerifyAsync(
                path,
                "Onboarding wizard, Step 4 of 5 — Default model. Progress stepper: dot 4 filled. " +
                "Body shows 'Pick the default model for new sessions:', a small chip showing the selected provider " +
                "(icon + 'Provider: <name>'), and a text input below pre-filled with the suggested model id " +
                "(e.g. 'qwen2.5-coder:7b' for Ollama). Helper text 'You can switch models anytime with /model or in Settings.' " +
                "Footer: 'Skip' + '← Back' + 'Next →' buttons.",
                nameof(Onboarding_Step4_Model)).ConfigureAwait(false);
            
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>
    ///     Step 5: shows the theme picker with Dark / Light / System radio
    ///     buttons.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_Step5_Theme()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 5).ConfigureAwait(false);
        try
        {
            UI(() => vm.DefaultModel = "qwen2.5-coder:7b");
            var hasTheme = await Driver.WaitForTextAsync("Choose your theme", TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await Assert.That(hasTheme).IsTrue();

            var path = await CaptureOnboardingWindowAsync(window, "onboarding-step5-theme")
                .ConfigureAwait(false);

            var vlm = await VlmVerifier.VerifyAsync(
                path,
                "Onboarding wizard, Step 5 of 5 — Theme. Progress stepper: dot 5 filled (all 5 dots now filled). " +
                "Body shows 'Choose your theme:' and 3 radio-button cards stacked vertically: " +
                "1) '🌙  Dark (Catppuccin Mocha)' (pre-selected), " +
                "2) '☀️  Light (Catppuccin Latte)', " +
                "3) '🖥  System'. " +
                "Below them: 'Click Finish to save and start using Harbor.' " +
                "Footer: 'Skip' + '← Back' + 'Finish' (primary, was 'Next' on earlier steps).",
                nameof(Onboarding_Step5_Theme)).ConfigureAwait(false);
            
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>
    ///     Back button: clicking Back from step 3 navigates to step 2.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_BackButton_NavigatesToPreviousStep()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 3).ConfigureAwait(false);
        try
        {
            var stepBefore = UI(() => vm.CurrentStep);
            await Assert.That(stepBefore).IsEqualTo(3);

            UI(() => vm.BackCommand.Execute(null));
            await Task.Delay(150).ConfigureAwait(false);

            var stepAfter = UI(() => vm.CurrentStep);
            await Assert.That(stepAfter).IsEqualTo(2);

            var path = await CaptureOnboardingWindowAsync(window, "onboarding-back-to-step2")
                .ConfigureAwait(false);

            var vlm = await VlmVerifier.VerifyAsync(
                path,
                "Onboarding wizard after the user clicked '← Back' from step 3. The wizard is now on Step 2 of 5 — " +
                "Choose your providers. Step title 'Choose your providers'. Caption 'Step 2 of 5'. " +
                "Provider list visible with checkboxes + icons. Footer shows 'Skip' + 'Next →' (no '← Back' on step 2, " +
                "or it's hidden because we're on the second step).",
                nameof(Onboarding_BackButton_NavigatesToPreviousStep)).ConfigureAwait(false);
            
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>
    ///     Skip button: closes the wizard without saving (IsCompleted=true).
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task Onboarding_SkipButton_ClosesWizard()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);
        var (window, vm) = await OpenOnboardingAsync(step: 2).ConfigureAwait(false);
        try
        {
            UI(() => vm.SkipCommand.Execute(null));
            await Task.Delay(150).ConfigureAwait(false);

            var isCompleted = UI(() => vm.IsCompleted);
            await Assert.That(isCompleted).IsTrue();

            var path = await CaptureOnboardingWindowAsync(window, "onboarding-skip")
                .ConfigureAwait(false);

            var vlm = await VlmVerifier.VerifyAsync(
                path,
                "Onboarding wizard after the user clicked 'Skip'. The wizard's IsCompleted flag is true and the " +
                "window is closing/closed. The screenshot may show the wizard mid-close (blank or partially rendered) " +
                "or the main window behind. The MainViewModel's IsCommandPaletteOpen / IsSettingsOpen / etc. are all false.",
                nameof(Onboarding_SkipButton_ClosesWizard)).ConfigureAwait(false);
            
        }
        finally
        {
            try { CloseWindow(window); } catch { }
        }
    }
}
