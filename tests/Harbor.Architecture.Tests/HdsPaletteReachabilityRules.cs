// HdsPaletteReachabilityRules.cs — the guard behind #583: the Avalonia app SHIPS
// six HDS palettes, and the desktop path could reach two of them.
//
// THE DEFECT, AS FOUND
// --------------------
// `HdsThemeCatalog.PaletteNames` names six palettes and `HdsThemeCatalogParityTests`
// pins that list to `Themes/Hds/*.axaml`, so the DATA is right and the guard standing
// behind it is green. Two links in the chain from "this palette exists" to "the user
// can be looking at it" were missing, and they were missing in different layers:
//
//   1. NOTHING RENDERS THE PICKER. `ThemeSettingsViewModel.AvailableThemes` is a
//      live collection of six previews, and no .axaml in the app binds it — the
//      whole theme UI is a three-item ComboBox (dark / light / system) plus a
//      Preview button. The click route exists and is DEAD:
//      `SettingsView.OnThemePreviewClick` walks the visual tree for a `Border` whose
//      DataContext is an `HdsThemePreview` and calls `ApplyHdsThemeCommand`, and no
//      markup anywhere raises it. So `HdsThemeCatalog.ReadAll()` fed a collection
//      nothing was bound to, and a command nothing invoked.
//
//   2. THE STARTUP PATH IS A CLOSED SWITCH OVER TWO NAMES. `ThemeService.Apply` has
//      three arms: "light" → ApplyHds("Lumen"), "dark" → ApplyHds("CatppuccinMocha"),
//      everything else → dark with a log line. That is the path
//      `App.OnFrameworkInitializationCompleted` enters through `ApplyFromConfig`, and
//      the persisted `Theme` string is re-applied through it on every Settings save.
//      Four of the six shipped palettes (HarborDesignTokens, Mono, Paper, Vapor)
//      therefore could not be applied at all — and, because `ApplyHdsTheme` never
//      wrote the picked name anywhere, no palette name would have survived a restart
//      even after link 1 was fixed.
//
// WHY THIS IS NOT IN ThemeAxisStaysDataRules (#739)
// --------------------------------------------------
// That guard grades the DATA axis — `~/.harbor/themes/*.json`, `ThemeStore`,
// `ThemeDirectoryWatcher` — and its own header excludes this defect in as many words
// ("The desktop `ThemeService.Apply` 3-arm switch, which can only reach 2 of the 6
// shipped palettes. That is #583, a real and separate defect."), alongside
// `HdsThemeCatalog.PaletteNames` and `HarborTheme.BuiltIn`. Keeping it there was
// right, and this file is the answer to the obvious next question — "then how is the
// green in #739 consistent with half the palettes being unreachable?" — which is
// that they were never the same claim:
//
//   * #739's claim is ADDITIVITY of the user-facing data axis: a file that exists IS
//     the registration, so a new theme costs zero code edits. Nothing about the six
//     HDS palettes participates in that claim, and no user extension depends on it.
//   * The six HDS palettes are a hand-written `IReadOnlyList<string>`, which is the
//     CODE axis by #622's own definition — a new one is a C# edit, and #555 still
//     freezes that. `HdsThemeCatalogParityTests` holds the list to the folder, which
//     is the strongest form available for a compiled Avalonia app whose `avares://`
//     cannot be enumerated.
//
// So the exception was never false. What was broken is the REACH of the built-in
// catalog, and reachability is checkable independently of additivity — which is what
// this file checks. Had the freeze been violated instead, the fix would have been to
// freeze the palette list; it is not, so the fix is to make every palette reachable.
//
// WHAT IS RULED
// -------------
//   1. A palette the catalog offers must be RENDERED by a product view: some .axaml
//      must bind an ItemsSource to the catalog projection. Six previews nobody binds
//      are not an offer, they are a data structure.
//   2. The picker's click route must be WIRED: a product .axaml must route a routed
//      pointer event to a handler whose body resolves an `HdsThemePreview`. Matching
//      on the HANDLER'S BODY rather than on its name is what keeps the rule honest —
//      "some view has a Click handler" is already true of a dozen views and says
//      nothing about themes, while "this view's handler walks to a palette preview"
//      is the link that was missing.
//   3. The STARTUP apply path must consult the palette index.
//      `HdsThemeCatalog.Find` is the one call that answers "is this name a palette,
//      and is it dark?" WITHOUT naming a palette, so a file that owns `Apply` and
//      calls it is a path that can reach the whole catalog, and one that does not is
//      a path closed over the two names it happens to mention.
//   4. Non-vacuity for each of the three probes, in the shape
//      `ThemeAxisStaysDataRules` established: every scanner must still find what it
//      looks for in a file that legitimately has it, so a green result is
//      distinguishable from a regex that reads nothing.
//
// WHAT IS DELIBERATELY NOT RULED
// ------------------------------
//   * That a palette is reachable from the TERMINAL. That is a different axis
//     (`HARBOR_THEME_FILE` / `~/.harbor/theme.json`, the one #622 froze out of the
//     freeze) and it has its own guard.
//   * The CONTENT of a palette, its colours, or which variant it declares. That is
//     `HdsThemeCatalogParityTests` plus `ThemeTokenDuplicationGuardTests`, and
//     duplicating it here would be a second source of truth about design tokens —
//     the exact thing AGENTS.md rule 9 forbids.
//   * Adding a palette. This file does not change the cost of adding one, and the
//     #555 freeze on that is untouched: a new palette is still a C# edit to
//     `PaletteNames` plus a new .axaml, which is what makes it a code axis.
//
// RED ON FIRST RUN — the point of the commit order
// -----------------------------------------------
// All three rules fail on the tree they were written against, and each fails for the
// reason in its own header rather than for "the scanner found nothing": no .axaml
// binds `AvailableThemes`, no .axaml routes an event to a handler that resolves a
// palette, and the file that owns `Apply` never asks the index. That is deliberate
// and it is the same discipline `PresentationCapabilityRules.ResolvedViolations`
// uses — the claim is written down before the fix, so the red is the proof that the
// probe is looking at the real product rather than at nothing. If any of these three
// is green on an unfixed tree, the probe is broken and the fix is the PROBE.
//
// BRITTLENESS, STATED UP FRONT
// -----------------------------
// Rules 1 and 2 are TEXT rules over XAML, and that is a real limitation rather than
// a disguised one: a picker rebuilt in code instead of markup would leave them green
// while grading nothing. They are written anyway because the failure they catch is
// invisible otherwise — a dead handler and an unbound collection both compile, both
// pass every other gate, and neither is reachable by a user. If the picker ever moves
// out of XAML, these two rules must be replaced here by whatever grades the new
// shape, not merely deleted.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #583: every palette the Avalonia app ships is reachable from the desktop
///     path — rendered in a view, applied on a click, and applied again on the next
///     launch. The data was always right; the route to it was missing.
/// </summary>
public sealed class HdsPaletteReachabilityRules
{
    /// <summary>The product whose theme path is graded. An app, so absent from the src-only layer matrix.</summary>
    private const string AvaloniaApp = "apps/Harbor.App.Avalonia";

    /// <summary>Repo-relative path of the type that owns <c>Apply</c> — the startup path.</summary>
    private const string ThemeServiceFile = AvaloniaApp + "/Services/ThemeService.cs";

    /// <summary>
    ///     The one palette-index call that answers "is this a palette, and is it
    ///     dark?" without naming a palette. <c>PaletteUri</c> deliberately does not
    ///     qualify: it builds a URI from whatever string it is given, so a file that
    ///     calls only that is not asking the index anything.
    /// </summary>
    private const string PaletteIndexConsultation = "HdsThemeCatalog.Find(";

    /// <summary>
    ///     The catalog projection a view must bind to offer a palette.
    /// </summary>
    private const string PaletteProjection = "AvailableThemes";

    /// <summary>
    ///     An <c>ItemsSource</c> bound to the palette projection, in any
    ///     <c>{Binding …}</c> shape — <c>AvailableThemes</c> on the view-model, or
    ///     <c>ThemeSettings.AvailableThemes</c> through the settings screen.
    /// </summary>
    private static readonly Regex PaletteItemsSource = new(
        @"ItemsSource\s*=\s*""\{[^""]*\b" + PaletteProjection + @"\b[^""]*\}""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     A routed event on a product view, with the handler name it is routed to.
    ///     <c>Click</c> and the two pointer shapes cover what Avalonia markup uses
    ///     for "the user pressed something here"; the handler is then checked for
    ///     substance, so a view's Cancel button cannot satisfy the theme rule.
    /// </summary>
    private static readonly Regex RoutedEvent = new(
        @"\b(?:Click|PointerPressed|PointerReleased|Tapped|DoubleTapped)\s*=\s*""(?<handler>[A-Za-z_][A-Za-z0-9_]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A routed handler is real only if the sibling code-behind declares it.</summary>
    private static readonly Regex HandlerDeclaration = new(
        @"\bvoid\s+(?<handler>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     The preview type a palette-selecting handler must resolve to. Named
    ///     rather than a method name, so renaming the click handler cannot silently
    ///     un-guard the view.
    /// </summary>
    private const string PalettePreviewType = "HdsThemePreview";

    // ── Rule 1: the picker is rendered ───────────────────────────────────────

    /// <summary>
    ///     A palette the catalog offers must be rendered by a product view.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         RED on the tree this was written against, and the red is the finding:
    ///         <c>ThemeSettingsViewModel.AvailableThemes</c> was constructed from
    ///         <c>HdsThemeCatalog.ReadAll()</c> and bound by nothing. The Settings
    ///         screen offered a dark/light/system ComboBox, which is a VARIANT
    ///         choice, so every one of the six palettes was outside the only theme
    ///         affordance in the product.
    ///     </para>
    ///     <para>
    ///         The projection is what is required, not the palette NAMES: after this
    ///         is fixed the markup holds no palette name at all, because
    ///         <c>PaletteNames</c> is the single index and a name typed into a view
    ///         would be a second source of truth about which palettes exist — the
    ///         drift #673 removed. A view that binds nothing is the defect, so a
    ///         view that binds the projection is the fix.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task A_Product_View_Renders_The_Palette_Catalog()
    {
        IReadOnlyList<string> rendered = FindPaletteBindings();

        await Assert.That(rendered.Count).IsGreaterThan(0).Because(
            "HdsThemeCatalog.ReadAll() materialises one preview per shipped palette, and "
            + "before #583 no view in the app bound the collection it produced: the Settings "
            + "screen offered dark/light/system, which is a VARIANT, not a palette. A palette "
            + "nobody can see cannot be selected, so half the catalog — HarborDesignTokens, "
            + "Mono, Paper, Vapor — was unreachable through the product. If the picker is "
            + "rendered from " + PaletteProjection + ", that row is: "
            + (rendered.Count == 0 ? "(no view binds " + PaletteProjection + ")" : string.Join("\n", rendered)));
    }

    // ── Rule 2: the click route is wired ─────────────────────────────────────

    /// <summary>
    ///     A rendered thumbnail is still dead weight unless pressing it applies the
    ///     palette, so a product view must route a pointer event to a handler that
    ///     actually resolves a palette.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         RED on the tree this was written against for a reason worth stating
    ///         precisely, because the two halves both existed: the handler
    ///         (<c>OnThemePreviewClick</c>, which walks up to a
    ///         <c>Border { DataContext: HdsThemePreview }</c> and executes
    ///         <c>ApplyHdsThemeCommand</c>) was written and correct, and NO MARKUP
    ///         raised it. A routed event that no XAML binds does not exist as far as
    ///         a user is concerned, and neither the compiler nor any other gate can
    ///         see that.
    ///     </para>
    ///     <para>
    ///         The check is on the handler's BODY, not its name. A weaker rule —
    ///         "some view routes some event somewhere" — is already satisfied by
    ///         <c>Close_Click</c> and <c>Cancel_Click</c> in the same view, and would
    ///         be green on the unfixed tree while grading nothing, which is the
    ///         failure mode this file exists to avoid. The body must name
    ///         <see cref="PalettePreviewType" />.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task A_Palette_Click_Route_Reaches_A_Declared_Handler()
    {
        IReadOnlyList<string> wired = FindPaletteClickRoutes();

        await Assert.That(wired.Count).IsGreaterThan(0).Because(
            "A palette thumbnail the user cannot press is not offered. The handler that "
            + "selects a palette existed in the Settings code-behind while no markup raised "
            + "it, so " + PalettePreviewType + " was unreachable by pointer even once the "
            + "thumbnails were rendered. A product .axaml must route a pointer event to a "
            + "handler that resolves a " + PalettePreviewType + ". Found: "
            + (wired.Count == 0 ? "(no view routes an event to a palette handler)" : string.Join("\n", wired)));
    }

    // ── Rule 3: the startup path consults the index ──────────────────────────

    /// <summary>
    ///     The path that runs at launch must reach the whole catalog, not the two
    ///     palettes a closed switch happens to name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         RED on the tree this was written against. <c>ThemeService.Apply</c> had
    ///         three arms — "light" → <c>ApplyHds("Lumen")</c>, "dark" →
    ///         <c>ApplyHds("CatppuccinMocha")</c>, default → dark — and
    ///         <c>App.OnFrameworkInitializationCompleted</c> enters that method through
    ///         <c>ApplyFromConfig</c> with the persisted <c>Theme</c> string. Every other
    ///         palette name reaching that switch fell to dark, so even a rendered picker
    ///         would have been undone by the next launch.
    ///     </para>
    ///     <para>
    ///         <c>HdsThemeCatalog.Find</c> is required and <c>HdsThemeCatalog.PaletteUri</c>
    ///         is not, because only the first one asks the index a question: it is
    ///         case-insensitive, it returns the palette's own declared
    ///         <c>ThemeVariant</c>, and it returns null for a name the app does not
    ///         ship. <c>PaletteUri</c> concatenates whatever it is handed and would
    ///         happily build a URI for a palette that does not exist, so a path that
    ///         calls only that is still closed over the names it spells out.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task The_Startup_Apply_Path_Consults_The_Palette_Index()
    {
        IReadOnlyList<string> complaints = FindMissingPaletteIndexConsultation();

        await Assert.That(complaints.Count).IsEqualTo(0).Because(
            "The apply path is what a launch runs and what a saved setting is re-applied "
            + "through, so a palette the closed dark/light switch cannot name is a palette "
            + "the user cannot keep. The file that owns Apply must resolve an unrecognised "
            + "name through " + PaletteIndexConsultation + " — the one call that answers "
            + "'is this a palette, and is it dark?' without naming one. "
            + (complaints.Count == 0 ? "(nothing to report)" : string.Join("\n", complaints)));
    }

    // ── Rule 4: non-vacuity ──────────────────────────────────────────────────

    /// <summary>
    ///     The ItemsSource scanner must still find a real binding where one
    ///     legitimately exists, or "no view binds the projection" is
    ///     indistinguishable from "the regex reads nothing".
    /// </summary>
    /// <remarks>
    ///     The control is <c>OnboardingWindow.axaml</c> rather than
    ///     <c>SettingsView.axaml</c>, deliberately: this file's own fix adds a binding
    ///     to the Settings view, so a control pinned there would be asserting the
    ///     thing under test. Onboarding's two ItemsSource bindings are not this
    ///     file's business and cannot move with it.
    /// </remarks>
    [Test]
    public async Task Palette_Binding_Scanner_Still_Sees_A_Real_Binding()
    {
        int before = CountItemsSourceBindings("Views/OnboardingWindow.axaml");

        await Assert.That(before).IsGreaterThan(0).Because(
            "The scanner behind A_Product_View_Renders_The_Palette_Catalog must be able to "
            + "read a real ItemsSource binding. OnboardingWindow.axaml binds two, and if "
            + "the scanner ever reports none there it is reporting none everywhere, and the "
            + "palette rule is green for no reason.");
    }

    /// <summary>
    ///     The routed-event scanner must still find real handlers, and must still
    ///     distinguish a handler that resolves a palette from one that does not — the
    ///     sensitivity AND the specificity, in one place.
    /// </summary>
    [Test]
    public async Task Routed_Event_Scanner_Still_Sees_A_Real_Handler_And_Ignores_An_Unrelated_One()
    {
        // Close_Click is a real routed handler on a real view, and its body knows
        // nothing about palettes — the "stays quiet on the wrong handler" half.
        IReadOnlyList<string> all = FindRoutedHandlerNames("Views/SettingsView.axaml");
        IReadOnlyList<string> paletteRoutes = FindPaletteClickRoutes();

        await Assert.That(all.Count).IsGreaterThan(0).Because(
            "The scanner behind A_Palette_Click_Route_Reaches_A_Declared_Handler must read "
            + "the markup: SettingsView.axaml routes Backdrop_Click, Close_Click and "
            + "Cancel_Click. If it finds none, it is not reading, and a green palette rule "
            + "means nothing.");
        await Assert.That(paletteRoutes.Contains("Close_Click", StringComparer.Ordinal)).IsFalse().Because(
            "Close_Click is one of the handlers the assertion above just found, and its body "
            + "knows nothing about palettes — that is the specificity half of this control. "
            + "The palette rule looks at HANDLER BODIES, so a view's Cancel/Save button must "
            + "not be counted as a palette route; if this ever goes red the BODY check has "
            + "degraded into a name check, which is the weaker rule this file rejected.");
    }

    /// <summary>
    ///     The palette-index scanner must still find a real consultation where one
    ///     legitimately exists today.
    /// </summary>
    /// <remarks>
    ///     <c>ThemeSettingsViewModel</c> asks the index for a preview's declared
    ///     variant on every click, and #739's fix did not touch it, so it is a
    ///     genuine positive control that stays valid after the fix.
    /// </remarks>
    [Test]
    public async Task Palette_Index_Scanner_Still_Sees_A_Real_Consultation()
    {
        IReadOnlyList<string> hits = FindPaletteIndexConsultations(
            AvaloniaApp + "/ViewModels/ThemeSettingsViewModel.cs");

        await Assert.That(hits.Count).IsGreaterThan(0).Because(
            "The scanner behind The_Startup_Apply_Path_Consults_The_Palette_Index must be "
            + "able to find a real " + PaletteIndexConsultation + " call. "
            + "ThemeSettingsViewModel has one today; if the scanner cannot see it, it sees "
            + "nothing, and a green startup rule is vacuous.");
    }

    /// <summary>
    ///     Every probe above reads the repository through
    ///     <see cref="RepoPaths.RepoRoot" />. Outside a checkout they all report
    ///     "cannot read", which is the right direction to fail in — this test makes
    ///     the precondition explicit instead of leaving it to be inferred from three
    ///     unrelated red messages.
    /// </summary>
    [Test]
    public async Task Palette_Rules_Can_Actually_Read_The_Repository()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull().Because(
            "Every probe in this file walks " + AvaloniaApp + " under RepoPaths.RepoRoot. "
            + "Without a checkout (no Harbor.slnx above the test bin directory) all three "
            + "rules report 'cannot read' and go red for a reason that has nothing to do "
            + "with palettes. Each probe also fails loudly on a null root rather than "
            + "returning an empty verdict; this test names the precondition.");
    }

    // ── Probes ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Product <c>.axaml</c> files under <c>apps/Harbor.App.Avalonia</c>, sorted
    ///     for a deterministic failure message. <c>bin/</c> and <c>obj/</c> hold
    ///     copies of the same markup and would double every hit.
    /// </summary>
    private static IReadOnlyList<string> AvaloniaMarkupFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string appDir = Path.Combine(root, AvaloniaApp);
        if (!Directory.Exists(appDir))
        {
            return [];
        }

        var files = new List<string>();
        files.AddRange(Directory
            .EnumerateFiles(appDir, "*.axaml", SearchOption.AllDirectories)
            .Where(static f => !IsBuildOutput(f)));

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>Build output and generated markup, which is not what a user sees.</summary>
    private static bool IsBuildOutput(string file)
    {
        string normalized = file.Replace('\\', '/');
        foreach (string segment in new[] { "/obj/", "/bin/" })
        {
            if (normalized.Contains(segment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     XAML comments are stripped before matching. The prose that documents this
    ///     defect names the very binding under test, and a scanner that reads
    ///     <c>&lt;!-- … --&gt;</c> would grade the explanation as the implementation.
    /// </summary>
    private static string ReadMarkup(string path) =>
        XamlComment.Replace(File.ReadAllText(path), " ");

    private static readonly Regex XamlComment = new(
        @"<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Every product view whose markup binds an ItemsSource to the palette
    ///     projection, as <c>path:line</c>.
    /// </summary>
    private static IReadOnlyList<string> FindPaletteBindings()
    {
        var hits = new List<string>();
        foreach (string file in AvaloniaMarkupFiles())
        {
            if (!File.Exists(file))
            {
                continue;
            }

            string[] lines = ReadMarkup(file).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (PaletteItemsSource.IsMatch(lines[i]))
                {
                    hits.Add($"{Relative(file)}:{i + 1}");
                }
            }
        }

        return hits;
    }

    /// <summary>
    ///     ItemsSource bindings in one product view, regardless of what they bind —
    ///     the non-vacuity control for the scanner above.
    /// </summary>
    private static int CountItemsSourceBindings(string viewRelativePath)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return 0;
        }

        string path = Path.Combine(root, AvaloniaApp, viewRelativePath);
        if (!File.Exists(path))
        {
            return 0;
        }

        return Regex.Matches(ReadMarkup(path), @"\bItemsSource\s*=\s*""\{").Count;
    }

    /// <summary>
    ///     Every product view that routes a pointer event to a handler whose body
    ///     resolves a palette, as <c>path:line  handler</c>.
    /// </summary>
    /// <remarks>
    ///     The handler is looked up in the SIBLING code-behind (<c>X.axaml</c> →
    ///     <c>X.axaml.cs</c>), which is where Avalonia looks for a routed handler
    ///     declared on a view. A name routed from markup with no declaration in the
    ///     code-behind is an Avalonia load error, not a palette picker, so the pair
    ///     is required and the report says which half is missing.
    /// </remarks>
    private static IReadOnlyList<string> FindPaletteClickRoutes()
    {
        var hits = new List<string>();
        foreach (string file in AvaloniaMarkupFiles())
        {
            if (!File.Exists(file))
            {
                continue;
            }

            string[] lines = ReadMarkup(file).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match routed = RoutedEvent.Match(lines[i]);
                if (!routed.Success)
                {
                    continue;
                }

                string handler = routed.Groups["handler"].Value;
                if (HandlerResolvesAPalette(file, handler))
                {
                    hits.Add($"{Relative(file)}:{i + 1}  {handler}");
                }
            }
        }

        return hits;
    }

    /// <summary>
    ///     Whether the sibling code-behind declares <paramref name="handler" /> with a
    ///     body that resolves a palette. False for an undeclared name, so a view that
    ///     routes a palette handler to nowhere cannot pass.
    /// </summary>
    private static bool HandlerResolvesAPalette(string markupPath, string handler)
    {
        string codeBehind = Path.ChangeExtension(markupPath, ".axaml.cs");
        if (!File.Exists(codeBehind))
        {
            return false;
        }

        string[] lines = SourceCommentStripper.StripAll(File.ReadLines(codeBehind));
        Dictionary<string, string> bodies = ReadMethodBodies(lines);

        // The specific handler's own body, not "some method in this file mentions a
        // palette". A view whose Cancel_Click names HdsThemePreview and whose picker
        // handler does not is exactly the state this rule exists to catch, and a
        // file-wide mention would pass it.
        return bodies.TryGetValue(handler, out string? body)
            && body.Contains(PalettePreviewType, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Every <c>void Name(…)</c> in the file, mapped to its own brace-matched
    ///     body, so a handler's text is its own body rather than the rest of the
    ///     file — a view with a Cancel button and a palette handler would otherwise
    ///     make both of them "reach a palette" as soon as one mentions the type.
    /// </summary>
    /// <remarks>
    ///     A declaration whose body cannot be closed before end-of-file (an
    ///     expression-bodied or truncated handler) is recorded with whatever text
    ///     was available. The failure direction is the safe one: a handler this
    ///     reader cannot reconstruct produces no hit, and a missing hit is a RED
    ///     rule rather than a green one.
    /// </remarks>
    private static Dictionary<string, string> ReadMethodBodies(IReadOnlyList<string> lines)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Count; i++)
        {
            Match declaration = HandlerDeclaration.Match(lines[i]);
            if (!declaration.Success)
            {
                continue;
            }

            var body = new StringBuilder();
            int depth = 0;
            bool opened = false;
            int j = i;
            for (; j < lines.Count; j++)
            {
                foreach (char c in lines[j])
                {
                    if (c == '{')
                    {
                        depth++;
                        opened = true;
                    }
                    else if (c == '}')
                    {
                        depth--;
                    }
                }

                body.Append(lines[j]).Append('\n');
                if (opened && depth <= 0)
                {
                    break;
                }
            }

            bodies[declaration.Groups["handler"].Value] = body.ToString();
            i = Math.Max(i, j);
        }

        return bodies;
    }

    /// <summary>
    ///     Routed handler names declared by one product view's markup — the
    ///     non-vacuity control, which must find real handlers regardless of subject.
    /// </summary>
    private static IReadOnlyList<string> FindRoutedHandlerNames(string viewRelativePath)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string path = Path.Combine(root, AvaloniaApp, viewRelativePath);
        if (!File.Exists(path))
        {
            return [];
        }

        var names = new List<string>();
        foreach (string line in ReadMarkup(path).Split('\n'))
        {
            Match match = RoutedEvent.Match(line);
            if (match.Success)
            {
                names.Add(match.Groups["handler"].Value);
            }
        }

        return names;
    }

    /// <summary>
    ///     <c>HdsThemeCatalog.Find(</c> consultations in the named files, as
    ///     <c>path:line</c>.
    /// </summary>
    private static IReadOnlyList<string> FindPaletteIndexConsultations(string relativePath)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string path = Path.Combine(root, relativePath);
        if (!File.Exists(path))
        {
            return [];
        }

        var hits = new List<string>();
        string[] lines = SourceCommentStripper.StripAll(File.ReadLines(path));
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(PaletteIndexConsultation, StringComparison.Ordinal))
            {
                hits.Add($"{relativePath}:{i + 1}");
            }
        }

        return hits;
    }

    /// <summary>
    ///     Violations for rule 3: the theme service missing, unreadable, or holding
    ///     an apply path that never consults the palette index. A missing file is a
    ///     VIOLATION, not an empty result — the same choice
    ///     <c>ThemeAxisStaysDataRules.FindThemeNameTables</c> makes, because a guard
    ///     that cannot read its input must not report a clean one.
    /// </summary>
    private static IReadOnlyList<string> FindMissingPaletteIndexConsultation()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return ["the repository is not readable from this test host — every probe in this "
                    + "file would be vacuous (see Palette_Rules_Can_Actually_Read_The_Repository)"];
        }

        string path = Path.Combine(root, ThemeServiceFile);
        if (!File.Exists(path))
        {
            return [$"{ThemeServiceFile}  (missing — the guard cannot grade the apply path of a "
                    + "file it cannot read)"];
        }

        IReadOnlyList<string> hits = FindPaletteIndexConsultations(ThemeServiceFile);
        return hits.Count > 0
            ? []
            : [$"{ThemeServiceFile}  (no {PaletteIndexConsultation} — an unrecognised theme name "
                + "cannot be told apart from an unknown one, so the path is closed over the "
                + "palettes it spells out)"];
    }

    private static string Relative(string absolute) =>
        RepoPaths.RepoRoot is { } root
            ? Path.GetRelativePath(root, absolute).Replace('\\', '/')
            : Path.GetFileName(absolute);
}
