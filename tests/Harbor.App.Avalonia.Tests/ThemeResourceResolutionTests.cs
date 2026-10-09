// ThemeResourceResolutionTests.cs — #948: a declared theme key is not the same
// thing as a reachable one.
//
// MEASURED FACT (this file's reason to exist). Avalonia's resource indexer is
// not the resolver people assume. In Avalonia 12.1.0 — the version pinned in
// Directory.Packages.props — `ResourceDictionary`'s getter is
//
//     public object? this[object key] {
//         get { TryGetValue(key, out var value); return value; }   // bool DISCARDED
//
// (src/Avalonia.Base/Controls/ResourceDictionary.cs:35-41) and `TryGetValue`
// check-doc-cites: record-drift src/Avalonia.Base/Controls/ResourceDictionary.cs:35 now="unresolved" [no tracked file, and none in git history: the citation names a DEPENDENCY's source, which no rule in this repo can resolve] -->
// (:239-275) reads `_inner` and nothing else. The walk over
// `MergedDictionaries` and `ThemeDictionary` lives in `TryGetResource`
// (:188-237), which the indexer never calls. So the comment at
// Views/Converters.cs:25-26 is CORRECT, and `Resources[key]` resolves the top
// check-doc-cites: record-drift Views/Converters.cs:25 now="unresolved" [resolver blind spot — apps/Harbor.App.Avalonia/Views/Converters.cs is in the tree and PROJECT_ROOTS cannot reach it] -->
// level only.
//
// Two things follow, and both are load-bearing:
//
//   1. It misses. Every brush this app defines lives in a merged
//      ResourceInclude (App.axaml declares `Application.Resources` with
//      MergedDictionaries and NO direct entries), so the top level is empty and
//      every `Resources[key]` lookup in the app is a guaranteed miss.
//      `ThemeService.ApplyHds` swaps an entry *inside* MergedDictionaries, so it
//      never populates the top level either — swapping palettes does not
//      resurrect the indexer.
//
//   2. A miss is SILENT. The indexer returns null. It does not throw and it
//      does not substitute a default colour. `... as IBrush` on that null is
//      null again, so the converter hands XAML a null brush and Avalonia draws
//      the control with no brush at all. A thrown exception would have been
//      noticed in a screenshot review; this is invisible.
//
// WHY THE EXISTING GUARDS COULD NOT SEE IT. ThemeTokenDuplicationGuardTests
// reads hex literals out of Themes/**.axaml and compares them against hex
// literals in the app's C#. ThemeParityTests compares key SETS across theme
// files. Both assert that a token is DECLARED, and both are satisfiable while
// the app cannot RESOLVE the token through the lookup it actually uses. That is
// the general shape of the defect, and it is why these shipped.
//
// SHAPE OF EVERY HEADLESS TEST HERE, and the reason it is this shape:
// `session.Dispatch(async () => { ... })` binds to
// `Dispatch<TResult>(Func<TResult>)` at `TResult = Task` — a
// `Dispatch(Func<Task>)` returning `Task<Task>`, for the same reason
// `Task.Run(async …)` binds to `Func<Task>` and not `Action`. That overload
// wraps the body in `Task.FromResult(...)`, already complete when the lambda
// yields at its first `await`; so `Dispatch`'s task completes there, the caller
// resumes, and the `Task<Task>`'s payload — the real body task — is DISCARDED.
// Every assertion after that point is detached: its failure is discarded and
// the test passes. So this file does all its WORK inside a deliberately
// synchronous `Dispatch` and every `await Assert` OUTSIDE it, where a failure
// cannot be lost. The cast to System.Action below is there to make that
// overload choice explicit rather than something a future edit can quietly
// undo. Several existing tests in this suite are written the other way and
// assert nothing; see the note at the bottom of this file.

using Avalonia.Headless;
using Avalonia.Media;
using Harbor.App.Avalonia;
using Harbor.App.Avalonia.Views;
using Harbor.App.Avalonia.Views.Controls;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #948 — a theme key the app resolves by hand must actually resolve.
/// </summary>
/// <remarks>
///     <para>
///         The first three tests are measurements, and are expected to hold both
///         before and after any fix: they pin down what Avalonia's indexer does,
///         so the rules below cannot be "fixed" by someone misreading the API.
///     </para>
///     <para>
///         <c>[NotInParallel]</c> is load-bearing here as it is across this
///         suite: <c>Application.Current</c> is process-global, so two headless
///         sessions at once would race for it.
///     </para>
/// </remarks>
[NotInParallel("avalonia-headless")]
public class ThemeResourceResolutionTests
{
    /// <summary>
    ///     The measurement, stated about the app's own wiring:
    ///     <c>StateSuccessBrush</c> is declared — it is what the toast converter
    ///     asks for — and is resolvable, but not through the top-level indexer.
    /// </summary>
    /// <remarks>
    ///     This is the crux of #948. If the indexer returned the brush, the
    ///     converter would be correct and the issue void; if the converter used
    ///     <c>TryGetResource</c> and still got nothing, the fault would lie
    ///     elsewhere. It is the indexer, and that is pinned here rather than
    ///     assumed.
    /// </remarks>
    [Test]
    [Retry(3)]
    public async Task A_Merged_Theme_Key_Is_Resolvable_But_Not_Through_The_Indexer()
    {
        bool resolvedThroughMerged = false;
        bool resolvedThroughIndexer = true;
        int mergedCount = -1;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            var app = global::Avalonia.Application.Current;
            resolvedThroughMerged = app is not null
                && app.TryGetResource("StateSuccessBrush", null, out object? value)
                && value is IBrush;
            resolvedThroughIndexer = app?.Resources["StateSuccessBrush"] is not null;
            mergedCount = app?.Resources.MergedDictionaries.Count ?? -1;
        }), CancellationToken.None);

        await Assert
            .That(resolvedThroughMerged)
            .IsTrue()
            .Because("the key is declared in Themes/Hds/CatppuccinMocha.axaml and merged into App.Resources");
        await Assert.That(mergedCount)
            .IsGreaterThan(0)
            .Because("App.axaml merges BaseTokens, the HDS palette and Icons — so a miss is not an empty app");
        await Assert
            .That(resolvedThroughIndexer)
            .IsFalse()
            .Because("Application.Resources[key] reads the top level only, and the top level is empty");
    }

    /// <summary>
    ///     The top level is empty BY CONSTRUCTION, so this is not one unlucky
    ///     key: every indexer lookup in the app misses, under every theme.
    /// </summary>
    /// <remarks>
    ///     <c>App.axaml</c> declares <c>Application.Resources</c> with a
    ///     <c>MergedDictionaries</c> block and no direct entries, and
    ///     <c>ThemeService.ApplyHds</c> replaces an element of that block rather
    ///     than writing to the dictionary itself. The top level has no contents to
    ///     find at any point in the app's life, palette swaps included.
    /// </remarks>
    [Test]
    [Retry(3)]
    public async Task The_Top_Level_Resource_Dictionary_Holds_Nothing()
    {
        int topLevelCount = -1;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            topLevelCount = global::Avalonia.Application.Current?.Resources.Count ?? -1;
        }), CancellationToken.None);

        await Assert
            .That(topLevelCount)
            .IsEqualTo(0)
            .Because("every token is a ResourceInclude under MergedDictionaries, never a direct entry");
    }

    /// <summary>
    ///     What a miss actually does: null. Not a throw, not a default colour —
    ///     which is precisely why #948 was invisible rather than loud.
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task AMiss_Is_A_Null_Rather_Than_A_Throw_Or_A_Default()
    {
        object? missing = "not-run";

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        // A throw here fails the test on its own — `Dispatch` propagates it — so
        // reaching the assertions below IS the "it does not throw" half.
        await session.Dispatch((System.Action)(() =>
        {
            missing = global::Avalonia.Application.Current?.Resources["DefinitelyNotAThemeKey"];
        }), CancellationToken.None);

        await Assert.That(missing).IsNull();
        await Assert
            .That(missing as IBrush)
            .IsNull()
            .Because("the `as IBrush` every one of these resolvers applies to the miss is null too");
    }

    /// <summary>
    ///     The defect as the user meets it: a toast's 3px kind-coloured left
    ///     border, and its kind-coloured title and icon, resolve to nothing — the
    ///     card renders with no border brush at all.
    /// </summary>
    /// <remarks>
    ///     All four kinds are covered because the converter maps them to four
    ///     different keys; three of four passing would still leave a toast
    ///     silently unstyled.
    /// </remarks>
    [Test]
    [Retry(3)]
    public async Task Every_Toast_Kind_Resolves_Its_Theme_Brush()
    {
        var resolved = new List<string>();

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            foreach (Harbor.Ui.Framework.Services.ToastKind kind in
                     Enum.GetValues<Harbor.Ui.Framework.Services.ToastKind>())
            {
                object? brush = ToastBrushConverter.Instance.Convert(
                    kind, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture);

                if (brush is not null)
                    resolved.Add(kind.ToString());
            }
        }), CancellationToken.None);

        await Assert
            .That(resolved)
            .IsEquivalentTo(Enum.GetNames<Harbor.Ui.Framework.Services.ToastKind>())
            .Because("every toast binds BorderBrush and Foreground to this converter (#948)");
    }

    /// <summary>
    ///     The same defect in the onboarding stepper: done / active / pending
    ///     dots each look up their own key.
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task Every_Stepper_Position_Resolves_Its_Theme_Brush()
    {
        var resolved = new List<string>();

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            // Step 3 against dot 1 is "done", step 1 against dot 1 is "active",
            // step 0 against dot 1 is "pending".
            foreach ((int step, string position) in new[] { (3, "done"), (1, "active"), (0, "pending") })
            {
                object? brush = StepToStepperBrushConverter.Instance.Convert(
                    step, typeof(IBrush), "1", System.Globalization.CultureInfo.InvariantCulture);

                if (brush is not null)
                    resolved.Add(position);
            }
        }), CancellationToken.None);

        await Assert
            .That(resolved)
            .IsEquivalentTo(new[] { "done", "active", "pending" })
            .Because("each stepper dot binds Fill to this converter (#948)");
    }

    /// <summary>
    ///     The session status dot's six keys. Asserted at the resolver rather
    ///     than on the painted <c>Ellipse.Fill</c>, because the thing #948 was
    ///     about is the LOOKUP: <c>Resources[key]</c> versus
    ///     <c>TryGetResource</c>. Measuring the paint would fold a broken
    ///     control into a broken lookup and lose the distinction, and the control
    ///     side is covered on its own terms — see
    ///     <c>ViewInflationTests.SessionCardView_Inflates_WithAnApplicationRunning</c>
    ///     and <c>AvaloniaInitializeComponentShadowRules</c> in
    ///     <c>Harbor.Architecture.Tests</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This test used to carry a second reason for stopping at the
    ///         resolver, and that reason is gone. <c>StatusDot</c>'s code-behind
    ///         declared a <c>private void InitializeComponent()</c> that shadowed
    ///         the overload Avalonia's name generator emits, so the generated
    ///         <c>Dot</c> field was never assigned and the control's constructor
    ///         threw before any brush was looked up (#973). Fixed: the six
    ///         hand-written copies are deleted and
    ///         <c>AvaloniaInitializeComponentShadowRules</c> forbids the shape.
    ///     </para>
    ///     <para>
    ///         The reason that remains is the one above, and it is the reason
    ///         worth keeping: a control that cannot be constructed reports a
    ///         resolver failure as a constructor failure, which is a different
    ///         bug with a different owner.
    ///     </para>
    ///     <para>
    ///         This is the one case where a failed lookup did not merely look wrong.
    ///         <c>Ellipse.StatusDot</c> already sets <c>Fill</c> to
    ///         <c>StateRunningBrush</c>, so the code-behind's assignment is the only
    ///         thing that varies it per state — a missed key would have left every
    ///         dot wearing the RUNNING colour, and the error dot blue.
    ///     </para>
    /// </remarks>
    [Test]
    [Retry(3)]
    public async Task Every_Status_Dot_Key_Resolves_Its_Own_Theme_Brush()
    {
        string[] keys =
        [
            "StateIdleBrush",
            "StateRunningBrush",
            "StateInfoBrush",
            "StatePendingBrush",
            "StatusSuccessBrush",
            "StatusErrorBrush"
        ];

        List<string> unresolved = [];

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            foreach (string key in keys)
            {
                if (ThemeBrushResolver.Resolve(key) is not SolidColorBrush solid)
                    unresolved.Add($"{key}: did not resolve to a brush");
                else if (solid.Color != ResolveColour(key))
                    unresolved.Add($"{key}: resolved to the wrong brush");
            }
        }), CancellationToken.None);

        foreach (string wrong in unresolved)
            Console.WriteLine(wrong);

        await Assert.That(unresolved).IsEmpty();
    }

    /// <summary>
    ///     The generic converter, which #948 found already on the working API —
    ///     but still trailing a dead indexer fallback. Asserted here so that
    ///     fallback cannot be reintroduced as if it were a safety net.
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task The_Generic_Brush_Key_Converter_Resolves_A_Merged_Key()
    {
        object? brush = "not-run";

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            brush = BrushKeyConverter.Instance.Convert(
                "StateSuccessBrush", typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture);
        }), CancellationToken.None);

        await Assert
            .That(brush)
            .IsNotNull()
            .Because("the chat template and status bar resolve their keys through this converter (#948)");
    }

    /// <summary>
    ///     The compact diff view, whose private <c>TryGetBrush</c> no behavioural
    ///     test can call directly — so it is asserted where it shows up instead:
    ///     on the brush each parsed line is actually painted with.
    /// </summary>
    /// <remarks>
    ///     Every branch falls back to a flat grey <c>0x80,0x80,0x80</c>, so a
    ///     failed lookup neither throws nor blanks the diff — it quietly renders
    ///     the whole diff in that fallback grey. Comparing against the token's own
    ///     colour is the only thing that catches it.
    /// </remarks>
    [Test]
    [Retry(3)]
    public async Task Every_Diff_Line_Paints_Its_Own_Theme_Brush()
    {
        (string Marker, string BrushKey)[] lines =
        [
            ("+", "ChatToolResultBrush"),
            ("-", "ChatErrorBrush"),
            ("@", "AccentBrush"),
            (" ", "TextSecondaryBrush")
        ];

        List<string> wrongBrush = [];

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            foreach ((string marker, string brushKey) in lines)
            {
                var diff = new HdsDiffCompact { DiffText = marker + "changed" };
                if (diff.Lines.Count != 1)
                {
                    wrongBrush.Add($"'{marker}': {diff.Lines.Count} lines parsed, want 1");
                    continue;
                }

                if (diff.Lines[0].Brush is not SolidColorBrush solid)
                {
                    wrongBrush.Add($"'{marker}': {diff.Lines[0].Brush?.GetType().Name ?? "null"}, want {brushKey}");
                    continue;
                }

                if (solid.Color != ResolveColour(brushKey))
                    wrongBrush.Add($"'{marker}': painted {solid.Color}, want {brushKey}");
            }
        }), CancellationToken.None);

        foreach (string wrong in wrongBrush)
            Console.WriteLine(wrong);

        await Assert.That(wrongBrush).IsEmpty();
    }

    /// <summary>
    ///     The structural tripwire: reading a theme key through the top-level
    ///     indexer is a guaranteed miss in this app (see
    ///     <see cref="The_Top_Level_Resource_Dictionary_Holds_Nothing" />), so
    ///     the form itself is banned in app C# — including the code-behind that no
    ///     behavioural test above can reach.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately a ratchet, not the proof. A behavioural test only
    ///         covers the call sites someone remembered to write one for; this scan
    ///         covers the shape wherever it appears, which is what reaches the two
    ///         code-behind resolvers the behavioural tests do not touch.
    ///     </para>
    ///     <para>
    ///         It reads checked-out sources the way
    ///         <c>ThemeTokenDuplicationGuardTests</c> already does, from the tree
    ///         rather than from a running app.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task No_App_Source_Reaches_A_Theme_Key_Through_The_Top_Level_Indexer()
    {
        List<string> violations = AppCSharpFiles()
            .SelectMany(file => File.ReadAllLines(file)
                .Select((text, index) => (text, line: index + 1))
                .SelectMany(entry => IndexerLookup
                    .Matches(StripLineComment(entry.text))
                    .Select(_ => $"{Path.GetRelativePath(FindRepoRoot(), file).Replace('\\', '/')}:{entry.line}")))
            .ToList();

        foreach (string violation in violations)
            Console.WriteLine(
                $"{violation}: Application.Current.Resources[key] reads the TOP-LEVEL dictionary only — it does "
                + "not search MergedDictionaries, so a theme key declared in Themes/Hds/*.axaml resolves to null. "
                + "Use Application.Current.TryGetResource(key, null, out var v) instead (see #948).");

        await Assert.That(violations).IsEmpty();
    }

    private static readonly System.Text.RegularExpressions.Regex IndexerLookup =
        new(@"Application\s*\.\s*Current\s*\??\s*\.\s*Resources\s*\[",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    ///     Drops a trailing <c>//</c> comment so a file may NAME the banned form
    ///     in order to explain why it is banned — <c>ThemeBrushResolver</c> does
    ///     exactly that. A guard that fails on its own explanatory prose gets
    ///     muted, and a muted guard catches nothing.
    /// </summary>
    private static string StripLineComment(string text)
    {
        int comment = text.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? text : text[..comment];
    }

    /// <summary>
    ///     Resolves a brush through the working API and reads its colour, so the
    ///     assertions above compare against the token rather than a copied hex —
    ///     which is the mistake <c>ThemeTokenDuplicationGuardTests</c> exists to
    ///     prevent, and which would make this file wrong on a palette retune.
    /// </summary>
    private static Color ResolveColour(string brushKey)
    {
        var app = global::Avalonia.Application.Current;
        if (app is not null
            && app.TryGetResource(brushKey, null, out object? value)
            && value is SolidColorBrush brush)
            return brush.Color;

        throw new InvalidOperationException($"{brushKey} is not resolvable — the headless harness is not set up.");
    }

    private static IReadOnlyList<string> AppCSharpFiles() =>
        Directory
            .EnumerateFiles(Path.Combine(FindRepoRoot(), "apps", "Harbor.App.Avalonia"), "*.cs",
                SearchOption.AllDirectories)
            .Where(static file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !string.Equals(Path.GetFileName(file), "GlobalUsings.cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Harbor.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        return Directory.GetCurrentDirectory();
    }
}