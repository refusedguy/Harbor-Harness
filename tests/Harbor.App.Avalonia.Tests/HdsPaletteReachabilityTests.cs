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
        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch(async () =>
        {
            var applier = new RecordingThemeApplier();
            var vm = new ThemeSettingsViewModel(applier, applier) { Theme = "system" };

            vm.ApplyHdsThemeCommand.Execute("Vapor");

            await Assert.That(vm.SelectedPalette).IsEqualTo("Vapor");
            await Assert.That(vm.PersistedTheme).IsEqualTo("Vapor");
            await Assert.That(applier.AppliedPalettes).Contains("Vapor");
        }, CancellationToken.None);
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
        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch(async () =>
        {
            var applier = new RecordingThemeApplier();
            var vm = new ThemeSettingsViewModel(applier, applier);

            await Assert.That(vm.AvailableThemes.Count)
                .IsEqualTo(HdsThemeCatalog.PaletteNames.Count);

            foreach (HdsThemePreview palette in vm.AvailableThemes)
            {
                vm.ApplyHdsThemeCommand.Execute(palette.Name);

                await Assert.That(vm.SelectedPalette).IsEqualTo(palette.Name);
                await Assert.That(vm.PersistedTheme).IsEqualTo(palette.Name);
                await Assert.That(applier.AppliedPalettes).Contains(palette.Name);
                await Assert.That(applier.LastVariant).IsEqualTo(palette.IsDark);
            }

            // One variant call per palette, and one palette per shipped dictionary
            // — the variant is read off the palette, never off a list of which
            // palettes are dark, so this count is also the count of palettes the
            // view-model could name at all.
            await Assert.That(applier.VariantCalls)
                .IsEqualTo(HdsThemeCatalog.PaletteNames.Count);
        }, CancellationToken.None);
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
        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch(async () =>
        {
            var applier = new RecordingThemeApplier();
            var vm = new ThemeSettingsViewModel(applier, applier) { Theme = "system" };

            vm.Restore("HarborDesignTokens");

            await Assert.That(vm.SelectedPalette).IsEqualTo("HarborDesignTokens");
            await Assert.That(vm.PersistedTheme).IsEqualTo("HarborDesignTokens");

            // The variant selector still lists variants, so it is left where the
            // user had it rather than pointed at a name it does not contain.
            await Assert.That(vm.Theme).IsEqualTo("system");
        }, CancellationToken.None);
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
        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch(async () =>
        {
            var applier = new RecordingThemeApplier();
            var vm = new ThemeSettingsViewModel(applier, applier);

            vm.Restore("light");

            await Assert.That(vm.SelectedPalette).IsNull();
            await Assert.That(vm.Theme).IsEqualTo("light");
            await Assert.That(vm.PersistedTheme).IsEqualTo("light");
        }, CancellationToken.None);
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
        await using var session = HeadlessUnitTestSession.StartNew(typeof(global::Harbor.App.Avalonia.App));
        await session.Dispatch(async () =>
        {
            var applier = new RecordingThemeApplier();
            var vm = new ThemeSettingsViewModel(applier, applier) { Theme = "dark" };

            vm.ApplyHdsThemeCommand.Execute("NoSuchPalette");

            await Assert.That(applier.AppliedPalettes.Count).IsEqualTo(0);
            await Assert.That(applier.VariantCalls).IsEqualTo(0);
            await Assert.That(vm.SelectedPalette).IsNull();
            await Assert.That(vm.PersistedTheme).IsEqualTo("dark");
        }, CancellationToken.None);
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
