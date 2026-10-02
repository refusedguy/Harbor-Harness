// HdsPaletteReachabilityTests.cs — #583: a palette the user can pick is a palette
// that comes back.
//
// The architecture guard (tests/Harbor.Architecture.Tests/HdsPaletteReachabilityRules.cs)
// proves the route exists — a view binds the catalog, a click handler resolves a
// palette, the apply path consults the index. Those are structural claims about
// source, so what is left is the BEHAVIOUR they exist to protect, and it lives
// entirely on the view-model: a name is a palette only if the catalog says so, the
// picked name is what gets persisted, and a name the catalog does not know must
// leave the running app untouched.
//
// `ThemeService.Apply` itself cannot be exercised here: every member of it is
// gated on an `Avalonia.Application` being set (`_app is null` returns), and
// standing one up is a composition-root test rather than a unit of this defect.
// The service's half is the guard's `The_Startup_Apply_Path_Consults_The_Palette_Index`;
// this file covers the half that is testable without a window.

using Avalonia.Headless;
using Harbor.App.Avalonia.Themes;
using Harbor.App.Avalonia.ViewModels;
using Harbor.Ui.Framework.Services;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #583 — a palette chosen in Settings is persisted, restored, and only
///     applied when the catalog ships it.
/// </summary>
[NotInParallel("avalonia-headless")]
public class HdsPaletteReachabilityTests
{
    /// <summary>
    ///     The pick is what a save writes. Before this the ComboBox held the only
    ///     persisted value and it lists VARIANTS, so a palette applied for the rest
    ///     of the session and was gone on the next launch.
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task Picking_A_Palette_Is_What_Settings_Persists()
    {
        // Assertions OUTSIDE the dispatch: `Dispatch(async () => …)` binds to
        // `Dispatch<Task>(Func<Task>)`, whose `Task<Task>` payload this call site
        // dropped, so the body detached at its first `await Assert` — these three
        // checks could not fail the test (#972, #766; see
        // AvaloniaDispatchAsyncVoidRule). The view-model work is synchronous, so
        // it belongs in a synchronous dispatch and the checks outside it.
        string? selected = null;
        string? persisted = null;
        List<string> applied = [];

        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch((System.Action)(() =>
        {
            var applier = new RecordingThemeApplier();
            var vm = new ThemeSettingsViewModel(applier, applier) { Theme = "system" };

            vm.ApplyHdsThemeCommand.Execute("Vapor");

            selected = vm.SelectedPalette;
            persisted = vm.PersistedTheme;
            applied = applier.AppliedPalettes;
        }), CancellationToken.None);

        await Assert.That(selected).IsEqualTo("Vapor");
        await Assert.That(persisted).IsEqualTo("Vapor");
        await Assert.That(applied).Contains("Vapor");
    }

    /// <summary>
    ///     Every shipped palette is offered as a choice, and every one of them can
    ///     be picked — the defect was four of the six being unreachable, so "the
    ///     list is long enough" is the claim worth pinning at the level the user
    ///     meets it.
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task Every_Shipped_Palette_Can_Be_Picked()
    {
        // Every assertion outside the dispatch, for the reason spelled out in
        // Picking_A_Palette_Is_What_Settings_Persists. The per-palette loop runs on
        // the UI thread (it drives the view-model's command) and RECORDS what it
        // saw; the checks are made here, where a failure can land.
        int offered = -1;
        int expectedPalettes = HdsThemeCatalog.PaletteNames.Count;
        var mismatches = new List<string>();
        int variantCalls = -1;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch((System.Action)(() =>
        {
            var applier = new RecordingThemeApplier();
            var vm = new ThemeSettingsViewModel(applier, applier);

            offered = vm.AvailableThemes.Count;

            foreach (HdsThemePreview palette in vm.AvailableThemes)
            {
                vm.ApplyHdsThemeCommand.Execute(palette.Name);

                if (vm.SelectedPalette != palette.Name)
                {
                    mismatches.Add(palette.Name + ": SelectedPalette was " + (vm.SelectedPalette ?? "(null)"));
                }

                if (vm.PersistedTheme != palette.Name)
                {
                    mismatches.Add(palette.Name + ": PersistedTheme was " + vm.PersistedTheme);
                }

                if (!applier.AppliedPalettes.Contains(palette.Name))
                {
                    mismatches.Add(palette.Name + ": never applied");
                }

                if (applier.LastVariant != palette.IsDark)
                {
                    mismatches.Add(palette.Name + ": variant was " + applier.LastVariant);
                }
            }

            // One variant call per palette, and one palette per shipped dictionary
            // — the variant is read off the palette, never off a list of which
            // palettes are dark, so this count is also the count of palettes the
            // view-model could name at all.
            variantCalls = applier.VariantCalls;
        }), CancellationToken.None);

        await Assert.That(offered).IsEqualTo(expectedPalettes);
        await Assert.That(string.Join(" | ", mismatches))
            .IsEqualTo(string.Empty)
            .Because(
                "every shipped palette must be offered, pickable, persistable under its own name, and "
                + "must set the variant the catalog says it is. The four-per-palette sweep below "
                + "records every mismatch by name so a failure says WHICH palette broke and how, "
                + "rather than stopping at the first one. Mismatches: "
                + (mismatches.Count == 0 ? "(none)" : string.Join(" | ", mismatches)));
        await Assert.That(variantCalls).IsEqualTo(expectedPalettes);
    }

    /// <summary>
    ///     A persisted palette name comes back as the palette, not as a value the
    ///     variant selector cannot show. This is the restart half: without it the
    ///     picker would work and then forget.
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task A_Restored_Palette_Name_Comes_Back_As_The_Palette()
    {
        string? selected = null;
        string? persisted = null;
        string? theme = null;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch((System.Action)(() =>
        {
            var applier = new RecordingThemeApplier();
            var vm = new ThemeSettingsViewModel(applier, applier) { Theme = "system" };

            vm.Restore("HarborDesignTokens");

            selected = vm.SelectedPalette;
            persisted = vm.PersistedTheme;

            // The variant selector still lists variants, so it is left where the
            // user had it rather than pointed at a name it does not contain.
            theme = vm.Theme;
        }), CancellationToken.None);

        await Assert.That(selected).IsEqualTo("HarborDesignTokens");
        await Assert.That(persisted).IsEqualTo("HarborDesignTokens");
        await Assert.That(theme).IsEqualTo("system");
    }

    /// <summary>
    ///     A variant is not a palette. The whole distinction rests on the catalog
    ///     being the only thing that decides, so restoring "dark" must not produce
    ///     a selected palette.
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task Restoring_A_Variant_Is_Not_Mistaken_For_A_Palette()
    {
        string? selected = null;
        string? theme = null;
        string? persisted = null;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch((System.Action)(() =>
        {
            var applier = new RecordingThemeApplier();
            var vm = new ThemeSettingsViewModel(applier, applier);

            vm.Restore("light");

            selected = vm.SelectedPalette;
            theme = vm.Theme;
            persisted = vm.PersistedTheme;
        }), CancellationToken.None);

        await Assert.That(selected).IsNull();
        await Assert.That(theme).IsEqualTo("light");
        await Assert.That(persisted).IsEqualTo("light");
    }

    /// <summary>
    ///     An unknown name changes nothing at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the half of #583 that was a live bug in the other direction.
    ///         <c>ApplyHds</c> swaps the merged dictionary for a
    ///         <c>ResourceInclude</c> built from whatever string it is handed, so
    ///         resolving AFTER the call meant a name the catalog does not know
    ///         replaced a working palette with a dictionary that does not exist.
    ///     </para>
    ///     <para>
    ///         A persisted setting outlives the palette it names, so this is not
    ///         hypothetical: a user whose saved <c>Paper</c> disappeared in an
    ///         upgrade must land on dark, not on a broken resource include.
    ///     </para>
    /// </remarks>
    [Test]
    [Retry(3)]
    public async Task An_Unknown_Palette_Name_Applies_Nothing()
    {
        int appliedCount = -1;
        int variantCalls = -1;
        string? selected = null;
        string? persisted = null;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch((System.Action)(() =>
        {
            var applier = new RecordingThemeApplier();
            var vm = new ThemeSettingsViewModel(applier, applier) { Theme = "dark" };

            vm.ApplyHdsThemeCommand.Execute("NoSuchPalette");

            appliedCount = applier.AppliedPalettes.Count;
            variantCalls = applier.VariantCalls;
            selected = vm.SelectedPalette;
            persisted = vm.PersistedTheme;
        }), CancellationToken.None);

        await Assert.That(appliedCount).IsEqualTo(0);
        await Assert.That(variantCalls).IsEqualTo(0);
        await Assert.That(selected).IsNull();
        await Assert.That(persisted).IsEqualTo("dark");
    }

    /// <summary>
    ///     An <see cref="IThemeApplier" /> + <see cref="IThemeReader" /> that records
    ///     what it was asked to do, so the assertions can be about the ORDER of
    ///     "was it a palette?" and "apply it" rather than about Avalonia internals.
    /// </summary>
    private sealed class RecordingThemeApplier : IThemeApplier, IThemeReader
    {
        public List<string> AppliedPalettes { get; } = [];

        public List<string> AppliedThemes { get; } = [];

        public int VariantCalls { get; private set; }

        /// <summary>The last value handed to <see cref="SetThemeVariant" />.</summary>
        public bool LastVariant { get; private set; }

        public string Current => AppliedThemes.Count > 0 ? AppliedThemes[^1] : "system";

        public bool IsDark { get; private set; }

        public void Apply(string theme) => AppliedThemes.Add(theme);

        public void ApplyDark()
        {
            AppliedThemes.Add("dark");
            IsDark = true;
            LastVariant = true;
        }

        public void ApplyLight()
        {
            AppliedThemes.Add("light");
            IsDark = false;
            LastVariant = false;
        }

        public void Toggle()
        {
            if (IsDark)
            {
                ApplyLight();
            }
            else
            {
                ApplyDark();
            }
        }

        public void ApplyHds(string theme) => AppliedPalettes.Add(theme);

        public void SetThemeVariant(bool isDark)
        {
            VariantCalls++;
            LastVariant = isDark;
            IsDark = isDark;
        }
    }
}
