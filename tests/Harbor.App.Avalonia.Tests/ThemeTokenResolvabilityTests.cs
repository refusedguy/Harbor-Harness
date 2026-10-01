// ThemeTokenResolvabilityTests.cs — #971: the guards before this one prove a token
// is DECLARED. None of them asked whether the app can RESOLVE it.
//
// THE CLASS THIS FILE CLOSES, stated so a future edit cannot quietly widen it.
//
// A theme token has two independent properties and the repo has only ever
// checked one of them:
//
//   DECLARED  — some Themes/**.axaml has an `x:Key="Name"` for it.
//   RESOLVABLE — the running app can hand a value back for that name, through
//                the lookup the app itself performs.
//
// `ThemeParityTests` checks declaration, across files: every palette must carry
// the same key set. That set is exact today — all six palettes declare the same
// 260 keys — and it says nothing about resolvability. `ThemeTokenDuplicationGuardTests`
// checks declaration of VALUES: a hex typed in C# must not duplicate one in a
// dictionary. It never asks whether a name resolves. #948 then found five call
// sites reading `Application.Current.Resources[key]`, which resolves the top
// level only; the top level is empty by construction, so all five returned null,
// and both guards stayed green.
//
// #971 is the other direction of the same gap, and it was not #952's five.
//
//   1. CONSUMED BUT UNRESOLVABLE — six keys. Three are named by a live view
//      through `{DynamicResource}` / `{StaticResource}`; three are named by C#
//      and declared NOWHERE at all. Each one misses under every palette, in
//      every code path, so no palette swap can make it appear. Two of the three
//      XAML cases are `DynamicResource`, i.e. the silent null of #948: the
//      composer input draws no border and the status bar's model label has no
//      foreground. The third is a `StaticResource`, which does not degrade
//      quietly at all.
//
//   2. DECLARED BUT UNRESOLVABLE — 54 keys. `Themes/Dark.axaml`,
//      `Themes/Light.axaml`, `Themes/HarborDark.axaml` and
//      `Themes/HarborLight.axaml` are not reachable from `App.axaml` at any
//      point in the app's life: `Application.Resources` merges only
//      `Hds/BaseTokens.axaml`, one `Hds/<Palette>.axaml` (swapped in place by
//      `ThemeService.ApplyHds`) and `Hds/Icons.axaml`. The 54 are declared
//      anyway. 35 of them are read only by their own dictionary's
//      `{StaticResource}`, which resolves inside that dictionary and is
//      therefore self-consistent rather than broken; 16 are read by nothing at
//      all. Before this change, 3 of the 54 were also read from OUTSIDE their
//      dictionary by a live view — those are the three XAML cases above, and
//      they are the only place the two sets touch.
//
// The shape of the rule follows from that split. It is NOT "the indexer is
// banned" — #952 already banned that form, and a form is a symptom. It is
// "every token name this app asks for resolves at runtime", which is the
// product-visible property and which holds for every lookup path at once
// (XAML markup extensions, the top-level indexer, `TryGetResource`, or a
// converter that wraps one).
//
// HOW THE RUNTIME IS ASKED, which is the whole risk in a guard of this kind.
// Seven guards in this repo's history have been green while proving nothing:
// #901 found zero tokens and passed; #906 and #925 scanned the wrong tree;
// #899 counted itself; #928 passed on a fixture it had just written; #858 was
// green on its own control; #948 was 8/8 green with the bug live. So:
//
//   * Every resolution assertion below asks a REAL Avalonia application
//     (`HeadlessUnitTestSession.StartNew(typeof(App))`) through
//     `TryGetResource` — the same call the product makes. Nothing is asserted
//     against an XML parse, and no file list is compared against another file
//     list as if that were resolution.
//   * The declared token set comes from `XDocument` over the real dictionaries,
//     so a commented-out `x:Key` cannot inflate it and a `ResourceInclude`
//     cannot be mistaken for a token.
//   * `The_Scan_Sees_What_It_Is_Meant_To_See` is the anti-vacuity ratchet: it
//     fails if either scan's regex stops matching, independently of whether any
//     key is currently broken. A guard that reports zero findings because it
//     looked at nothing is the #901 failure and it must not be reachable from
//     an edit to this file.
//   * `No_Token_From_A_Dictionary_The_App_Never_Merges_Is_Consumed_From_Outside_It`
//     keeps set 2 from regrowing a live edge. It states an invariant, not a
//     count, so deleting the legacy dictionaries satisfies it rather than
//     breaking it.

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Headless;
using Harbor.App.Avalonia;
using Harbor.App.Avalonia.Themes;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #971 — a theme token the app asks for must resolve against the running
///     application's resource chain, under every shipped palette.
/// </summary>
/// <remarks>
///     <para>
///         Companion to <see cref="ThemeResourceResolutionTests" />, which pins
///         what Avalonia's indexer does and bans it in app C#. That file covers
///         the five C# call sites of #948 and the form of the lookup. This one
///         covers the name: a view or a converter can ask for a key nothing
///         declares, and no indexer ban can see it.
///     </para>
///     <para>
///         <c>[NotInParallel]</c> is load-bearing for the same reason it is
///         across this suite: <c>Application.Current</c> is process-global, and
///         the palette test rewrites <c>Application.Resources.MergedDictionaries</c>.
///     </para>
/// </remarks>
[NotInParallel("avalonia-headless")]
public class ThemeTokenResolvabilityTests
{
    /// <summary>Root of the Avalonia app, relative to the repo root.</summary>
    private const string AppRelative = "apps/Harbor.App.Avalonia";

    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    ///     The two markup extensions whose key argument names a resource:
    ///     <c>{DynamicResource Name}</c> and <c>{StaticResource Name}</c>.
    /// </summary>
    /// <remarks>
    ///     Both are included deliberately, and they fail differently, which is
    ///     why the two cannot be lumped together in the report: a
    ///     <c>DynamicResource</c> miss is a null and paints nothing (#948's
    ///     silent case), while a <c>StaticResource</c> miss is raised during
    ///     load. A guard that only read one of them would see half the class.
    /// </remarks>
    private static readonly Regex MarkupResourceKey =
        new(@"\{\s*(?:Dynamic|Static)Resource\s+(?<key>[^\s},]+)\s*[,}]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     A design-token name in C#: PascalCase ending in the suffix the HDS
    ///     tokens use. Deliberately shape-based rather than call-site-based —
    ///     the app hands these names to <c>ThemeBrushResolver.Resolve</c> from
    ///     <c>switch</c> arms and tuple tables (<c>StatusDot</c>,
    ///     <c>HdsDiffCompact</c>, the toast and stepper converters), so there
    ///     is no single call site to match on.
    /// </summary>
    private static readonly Regex TokenShapedLiteral =
        new("\"(?<key>[A-Z][A-Za-z0-9]*(?:Brush|Color|Font|Radius|Thickness|Height|Width|Padding|Margin|Spacing|Variant))\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ---------------------------------------------------------------------
    // 1. A token a live view asks for must resolve.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Every <c>{DynamicResource}</c> / <c>{StaticResource}</c> key written
    ///     in a live view or in a style the app loads resolves to a value.
    /// </summary>
    /// <remarks>
    ///     Scoped to files that actually participate: the views under
    ///     <c>Views/</c>, plus every <c>StyleInclude</c> named by
    ///     <c>Application.Styles</c> in <c>App.axaml</c> (read from the file, so
    ///     the scan follows the wiring instead of a hand-kept list that could
    ///     drift from it). Keys a file declares in its own
    ///     <c>&lt;UserControl.Resources&gt;</c> are excluded: a converter
    ///     declared in the view that uses it resolves there and correctly.
    /// </remarks>
    [Test]
    [Retry(3)]
    public async Task Every_Theme_Key_A_View_Asks_For_Resolves_At_Runtime()
    {
        var asked = AskedMarkupResourceKeys();
        List<string> unresolved = [];

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            foreach ((string key, IReadOnlyList<string> sites) in asked)
            {
                if (!TryResolve(key))
                    unresolved.Add($"{key} — asked at {string.Join("; ", sites)}, resolves to nothing");
            }
        }), CancellationToken.None);

        foreach (string violation in unresolved)
            Console.WriteLine(violation);

        await Assert
            .That(unresolved)
            .IsEmpty()
            .Because(
                "a key a view asks for and nothing declares paints nothing (DynamicResource) or fails the load "
                + "(StaticResource) — see #971, the same silent-null shape as #948 reached through the name "
                + "instead of through the lookup");
    }

    // ---------------------------------------------------------------------
    // 2. A token app C# names must resolve.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Every theme-token name written as a literal in the app's C# resolves
    ///     to a value.
    /// </summary>
    /// <remarks>
    ///     This is the direction no markup can express, so nothing else in the
    ///     suite covers it. A <c>switch</c> arm naming a key is invisible to
    ///     every XAML scan, invisible to <c>ThemeParityTests</c> (which compares
    ///     dictionaries to each other), and invisible to the duplication guard
    ///     (which compares values, not names).
    /// </remarks>
    [Test]
    [Retry(3)]
    public async Task Every_Theme_Key_App_Csharp_Names_Resolves_At_Runtime()
    {
        var asked = TokenShapedKeysInCSharp();
        List<string> unresolved = [];

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            foreach ((string key, IReadOnlyList<string> sites) in asked)
            {
                if (!TryResolve(key))
                    unresolved.Add($"{key} — named at {string.Join("; ", sites)}, resolves to nothing");
            }
        }), CancellationToken.None);

        foreach (string violation in unresolved)
            Console.WriteLine(violation);

        await Assert
            .That(unresolved)
            .IsEmpty()
            .Because(
                "a converter that names a token no dictionary declares silently falls through to whatever "
                + "fallback it happens to carry — the #971 C#-side of the same class");
    }

    // ---------------------------------------------------------------------
    // 3. Anti-vacuity. A guard that sees nothing must fail.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Both scans are proven to still match, independently of whether any
    ///     key is currently broken.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the #901 failure ("Found 0" and a green run) made
    ///         unreachable: an edit that narrows either regex, changes the scan
    ///         root, or breaks the walked path turns the tests above into
    ///         vacuous passes. Here that shows up as a failure instead.
    ///     </para>
    ///     <para>
    ///         The floors are the numbers measured when #971 landed, not round
    ///         numbers: 118 distinct keys across 49 chain files for the markup
    ///         scan, 31 for the C# scan. They are floors, so they may only go up
    ///         without this failing.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task The_Scan_Sees_What_It_Is_Meant_To_See()
    {
        var markup = AskedMarkupResourceKeys();
        var csharp = TokenShapedKeysInCSharp();
        var chain = ChainStyleFiles();
        var themeDictionaries = ThemeDictionaryFiles();

        foreach (string key in markup.Keys)
            Console.WriteLine($"markup: {key} <- {string.Join("; ", markup[key])}");
        foreach (string key in csharp.Keys)
            Console.WriteLine($"csharp: {key} <- {string.Join("; ", csharp[key])}");

        await Assert
            .That(chain.Count)
            .IsGreaterThanOrEqualTo(10)
            .Because("App.axaml declares ten StyleIncludes; a scan that sees fewer is not walking Application.Styles");
        await Assert
            .That(themeDictionaries.Count)
            .IsGreaterThanOrEqualTo(9)
            .Because("Themes/ holds nine ResourceDictionary files; finding fewer means the parse stopped working");
        await Assert
            .That(markup.Count)
            .IsGreaterThanOrEqualTo(118)
            .Because("118 distinct theme keys were asked for in markup when #971 landed; fewer means the regex stopped matching");
        await Assert
            .That(csharp.Count)
            .IsGreaterThanOrEqualTo(31)
            .Because("31 theme-token literals were named in app C# when #971 landed; fewer means the shape regex stopped matching");
    }

    // ---------------------------------------------------------------------
    // 4. The declared direction, asked of the runtime: every palette token
    //    resolves under EVERY shipped palette.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Every key the HDS dictionaries declare resolves under every palette
    ///     the app can apply.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the resolvability twin of <c>ThemeParityTests</c>, and it
    ///         is green as it stands — parity across the six palettes is exact.
    ///         It is here because it is the check the repo does not have: parity
    ///         compares six files to each other, so all six can agree on a name
    ///         that no reachable dictionary actually provides, and the
    ///         comparison stays green. Asking a running app closes that.
    ///     </para>
    ///     <para>
    ///         Palettes are swapped the way the product swaps them — replacing
    ///         the element at the same slot <c>ThemeService.ApplyHds</c> writes
    ///         — so the walk is the real one rather than a reimplementation of
    ///         it. All six are checked, not just the default, because a token
    ///         that resolves under CatppuccinMocha and not under Paper is
    ///         exactly the retune-time regression.
    ///     </para>
    /// </remarks>
    [Test]
    [Retry(3)]
    public async Task Every_Declared_Hds_Token_Resolves_Under_Every_Shipped_Palette()
    {
        var keys = DeclaredThemeTokenNames();
        List<string> unresolved = [];

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            var app = global::Avalonia.Application.Current;
            if (app is null)
            {
                unresolved.Add("Application.Current is null — the headless harness did not start");
                return;
            }

            foreach (string palette in HdsThemeCatalog.PaletteNames)
            {
                ApplyPalette(app, palette);

                foreach (string key in keys)
                {
                    if (!app.TryGetResource(key, null, out object? value) || value is null)
                        unresolved.Add($"{key}: declared in Themes/Hds, but does not resolve under {palette}");
                }
            }
        }), CancellationToken.None);

        foreach (string violation in unresolved)
            Console.WriteLine(violation);

        await Assert.That(keys.Count)
            .IsGreaterThanOrEqualTo(260)
            .Because("the six palettes declare 260 keys in total; fewer means the declaration parse stopped working");
        await Assert
            .That(unresolved)
            .IsEmpty()
            .Because("ThemeParityTests compares palettes to each other, which all six can agree on while none resolves");
    }

    // ---------------------------------------------------------------------
    // 5. The 54. A token in a dictionary the app never merges must not be
    //    read from outside that dictionary.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     No token declared in a theme dictionary that the app never merges is
    ///     consumed from a file other than the one declaring it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Four dictionaries are unreachable: <c>Themes/Dark.axaml</c>,
    ///         <c>Themes/Light.axaml</c>, <c>Themes/HarborDark.axaml</c> and
    ///         <c>Themes/HarborLight.axaml</c>. Nothing includes them;
    ///         <c>Application.Resources</c> merges only
    ///         <c>Hds/BaseTokens.axaml</c>, one <c>Hds/&lt;Palette&gt;.axaml</c>
    ///         and <c>Hds/Icons.axaml</c>, and <c>ThemeService.ApplyHds</c>
    ///         replaces the palette slot rather than adding to it. Between them
    ///         the four declare 54 keys the running app cannot resolve.
    ///     </para>
    ///     <para>
    ///         The invariant is deliberately NOT "the count is 54" and NOT "these
    ///         four files must exist". Either would make the guard a ratchet on
    ///         debt nobody has agreed to keep: deleting the legacy dictionaries
    ///         is the likely eventual fix, and a count-based guard would fail on
    ///         the fix. What must never hold is a live consumer reaching into a
    ///         dictionary the app does not load — that is the condition that made
    ///         three keys silently unresolvable while every declaration guard
    ///         stayed green.
    ///     </para>
    ///     <para>
    ///         A self-reference is not a violation: a
    ///         <c>{StaticResource TextPrimaryColor}</c> inside
    ///         <c>HarborDark.axaml</c> resolves inside that dictionary, so those
    ///         35 keys are dead but self-consistent rather than broken. Only an
    ///         edge that leaves the declaring file is reported.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task No_Token_From_A_Dictionary_The_App_Never_Merges_Is_Consumed_From_Outside_It()
    {
        var orphans = UnmergedThemeDictionaries();
        var declared = orphans.ToDictionary(
            static file => file,
            file => DeclaredKeysIn(file).ToArray());

        // Everything that could consume a name: the chain files, plus app C#.
        var consumers = new List<(string File, int Line, string Key)>();
        foreach (string file in ChainStyleFiles().Concat(ViewFiles()))
        {
            string text = ReadText(Path.Combine(AppDir, file));
            foreach ((string key, int line) in MarkupKeysIn(text))
                consumers.Add((file, line, key));
        }

        foreach (string file in AppCSharpFiles())
        {
            string relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
            foreach ((string key, int line) in TokenShapedKeysIn(ReadText(file)))
                consumers.Add((relative, line, key));
        }

        List<string> violations = [];
        foreach ((string file, int line, string key) in consumers)
        {
            foreach ((string orphan, string[] keys) in declared)
            {
                if (!keys.Contains(key, StringComparer.Ordinal))
                    continue;

                // A reference from inside the declaring dictionary resolves there.
                if (string.Equals(file, orphan, StringComparison.Ordinal))
                    continue;

                violations.Add(
                    $"{file}:{line} asks '{key}', which is declared only in {orphan} — a dictionary nothing in " +
                    "App.axaml merges, so it resolves to nothing at runtime (#971)");
            }
        }

        foreach (string file in orphans)
            Console.WriteLine($"unmerged dictionary: {file} — {declared[file].Length} declared keys");

        foreach (string violation in violations)
            Console.WriteLine(violation);

        await Assert
            .That(orphans.Count)
            .IsGreaterThanOrEqualTo(4)
            .Because(
                "Themes/Dark.axaml, Themes/Light.axaml, Themes/HarborDark.axaml and Themes/HarborLight.axaml are "
                + "not merged by App.axaml; finding fewer means the merge-closure walk changed shape");
        await Assert
            .That(violations)
            .IsEmpty()
            .Because(
                "a live consumer reaching into a dictionary the app never loads is exactly the defect both theme "
                + "guards were blind to — they read declarations, and a declaration in an unloaded file is still "
                + "a declaration");
    }

    // =====================================================================
    // Runtime access
    // =====================================================================

    /// <summary>
    ///     The product's own question, asked the product's own way.
    ///     <c>TryGetResource</c> is what <c>ThemeBrushResolver</c>, the toast
    ///     converters, the markdown resolver and every code-behind use; it walks
    ///     the merged dictionaries, which the top-level indexer does not.
    /// </summary>
    private static bool TryResolve(string key)
    {
        var app = global::Avalonia.Application.Current;
        return app is not null && app.TryGetResource(key, null, out object? value) && value is not null;
    }

    /// <summary>
    ///     Swaps the palette slot exactly as <c>ThemeService.ApplyHds</c> does:
    ///     by replacing the element at the same index.
    /// </summary>
    private static void ApplyPalette(global::Avalonia.Application app, string palette)
    {
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0)
            throw new InvalidOperationException(
                "App.axaml declares Application.Resources with MergedDictionaries and none are present; "
                + "the palette slot the app relies on does not exist.");

        merged[1] = new Avalonia.Markup.Xaml.Styling.ResourceInclude(HdsThemeCatalog.BaseUri)
        {
            Source = HdsThemeCatalog.PaletteUri(palette)
        };
    }

    // =====================================================================
    // Scans
    // =====================================================================

    /// <summary>
    ///     Every key asked for by a live view or by a style the app loads, with
    ///     the file:line sites, for a failure message that names where.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> AskedMarkupResourceKeys()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string file in ChainStyleFiles().Concat(ViewFiles()))
        {
            string text = ReadText(Path.Combine(AppDir, file));
            HashSet<string> own = DeclaredKeysIn(file).ToHashSet(StringComparer.Ordinal);

            foreach ((string key, int line) in MarkupKeysIn(text))
            {
                // A key the file declares in its own <UserControl.Resources>
                // resolves there. Only the app chain is this guard's subject.
                if (own.Contains(key))
                    continue;

                Add(result, key, $"{file}:{line}");
            }
        }

        return result.ToDictionary(static e => e.Key, static e => (IReadOnlyList<string>)e.Value);
    }

    private static IEnumerable<(string Key, int Line)> MarkupKeysIn(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // A file may NAME a markup extension in an XML comment in order to
            // explain it. Themes/Hds/Icons.axaml and BaseTokens.axaml both do,
            // and a scan that cannot tell a comment from markup invents keys
            // nobody asked for. Lines are dropped rather than trimmed because an
            // XML comment may open and close inside one line.
            if (lines[i].Contains("<!--", StringComparison.Ordinal))
                continue;

            foreach (Match match in MarkupResourceKey.Matches(lines[i]))
            {
                string key = match.Groups["key"].Value;
                if (key.Length > 0)
                    yield return (key, i + 1);
            }
        }
    }

    private static Dictionary<string, IReadOnlyList<string>> TokenShapedKeysInCSharp()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string file in AppCSharpFiles())
        {
            string relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
            foreach ((string key, int line) in TokenShapedKeysIn(ReadText(file)))
                Add(result, key, $"{relative}:{line}");
        }

        return result.ToDictionary(static e => e.Key, static e => (IReadOnlyList<string>)e.Value);
    }

    private static IEnumerable<(string Key, int Line)> TokenShapedKeysIn(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // A file may NAME a token in a comment in order to explain it — the
            // #952 guard fails on its own prose for exactly this reason.
            int comment = lines[i].IndexOf("//", StringComparison.Ordinal);
            string code = comment < 0 ? lines[i] : lines[i][..comment];

            foreach (Match match in TokenShapedLiteral.Matches(code))
            {
                string key = match.Groups["key"].Value;
                if (key.Length > 0)
                    yield return (key, i + 1);
            }
        }
    }

    private static void Add(Dictionary<string, List<string>> into, string key, string site)
    {
        if (!into.TryGetValue(key, out List<string>? sites))
        {
            sites = [];
            into[key] = sites;
        }

        sites.Add(site);
    }

    // =====================================================================
    // Dictionary graph
    // =====================================================================

    /// <summary>
    ///     The styles <c>Application.Styles</c> loads, read from
    ///     <c>App.axaml</c> so the scan follows the wiring instead of a
    ///     hand-kept list.
    /// </summary>
    private static IReadOnlyList<string> ChainStyleFiles()
    {
        var result = new List<string>();
        var app = XDocument.Load(Path.Combine(AppDir, "App.axaml"));

        foreach (XElement include in app.Descendants()
                     .Where(static e => e.Name.LocalName == "StyleInclude"))
        {
            string? source = include.Attribute("Source")?.Value;
            if (source is null)
                continue;

            string relative = source.Replace("avares://Harbor.App.Avalonia/", "");
            if (File.Exists(Path.Combine(AppDir, relative)))
                result.Add(relative);
        }

        return result;
    }

    /// <summary>Every <c>.axaml</c> under <c>Views/</c> — the live views.</summary>
    private static IReadOnlyList<string> ViewFiles() =>
        Directory
            .EnumerateFiles(Path.Combine(AppDir, "Views"), "*.axaml", SearchOption.AllDirectories)
            .Select(static file => Path.GetRelativePath(AppDir, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    ///     The <c>ResourceDictionary</c> files under <c>Themes/</c>, by root
    ///     element name — not by filename, so a dictionary named something new
    ///     is still counted.
    /// </summary>
    private static IReadOnlyList<string> ThemeDictionaryFiles() =>
        Directory
            .EnumerateFiles(Path.Combine(AppDir, "Themes"), "*.axaml", SearchOption.AllDirectories)
            .Where(static file => DictionaryRootIs(file))
            .Select(static file => Path.GetRelativePath(AppDir, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool DictionaryRootIs(string file)
    {
        try
        {
            return XDocument.Load(file).Root?.Name.LocalName == "ResourceDictionary";
        }
        catch (System.Xml.XmlException)
        {
            // An unparsable dictionary is a build error the XAML compiler owns;
            // the guard's job is resolvability, not XML well-formedness.
            return false;
        }
    }

    /// <summary>
    ///     The theme dictionaries <c>Application.Resources</c> reaches, closing
    ///     over <c>ResourceInclude</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The palette slot is seeded from <c>HdsThemeCatalog.PaletteNames</c>,
    ///         not from <c>App.axaml</c> alone, and that distinction is the whole
    ///         reason this walk is not one line. <c>App.axaml</c> names ONE
    ///         palette, but <c>ThemeService.ApplyHds</c> replaces that slot's
    ///         element with any of the six, so the other five are reachable at
    ///         runtime too. Seeding only from the file would classify five live
    ///         palettes as unmerged, and every token they declare as a phantom
    ///         violation — a guard red for a reason that is not a defect, which
    ///         is how a correct one gets switched off.
    ///     </para>
    /// </remarks>
    private static HashSet<string> MergedThemeDictionaries()
    {
        var app = XDocument.Load(Path.Combine(AppDir, "App.axaml"));
        var roots = app.Descendants()
            .Where(static e => e.Name.LocalName == "ResourceInclude")
            .Select(static e => e.Attribute("Source")?.Value ?? string.Empty)
            .Where(static source => source.Length > 0)
            .Select(static source => source.Replace("avares://Harbor.App.Avalonia/", ""))
            .Where(static relative => File.Exists(Path.Combine(AppDir, relative)))
            // The slot ThemeService rewrites holds whichever palette is applied;
            // all six are reachable over the app's life.
            .Concat(HdsThemeCatalog.PaletteNames.Select(static name => $"Themes/Hds/{name}.axaml"))
            .ToArray();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots);

        while (pending.Count > 0)
        {
            string relative = pending.Pop();
            if (!seen.Add(relative))
                continue;

            foreach (string nested in ResourceIncludesIn(relative))
                pending.Push(nested);
        }

        return seen;
    }

    private static IEnumerable<string> ResourceIncludesIn(string relativePath)
    {
        string path = Path.Combine(AppDir, relativePath);
        if (!File.Exists(path))
            yield break;

        foreach (XElement include in XDocument.Load(path).Descendants()
                     .Where(static e => e.Name.LocalName == "ResourceInclude"))
        {
            string? source = include.Attribute("Source")?.Value;
            if (source is null)
                continue;

            string nested = source.Replace("avares://Harbor.App.Avalonia/", "");
            if (File.Exists(Path.Combine(AppDir, nested)))
                yield return nested;
        }
    }

    /// <summary>Theme dictionaries that <c>App.axaml</c> never merges.</summary>
    private static IReadOnlyList<string> UnmergedThemeDictionaries()
    {
        var merged = MergedThemeDictionaries();
        return ThemeDictionaryFiles().Where(file => !merged.Contains(file)).ToArray();
    }

    /// <summary>
    ///     The <c>x:Key</c> declarations in one file, read through
    ///     <see cref="XDocument" />.
    /// </summary>
    /// <remarks>
    ///     XML comments are nodes in <c>XDocument</c> and are not elements, so a
    ///     commented-out <c>x:Key</c> cannot reach this set. A
    ///     <c>ResourceInclude</c> carries no <c>x:Key</c>, so merging a
    ///     dictionary in cannot inflate it either.
    /// </remarks>
    private static IEnumerable<string> DeclaredKeysIn(string relativePath)
    {
        string path = Path.Combine(AppDir, relativePath);
        if (!File.Exists(path))
            yield break;

        XNamespace x = XamlNamespace;
        foreach (XElement element in XDocument.Load(path).Descendants())
        {
            string? key = element.Attribute(x + "Key")?.Value;
            if (!string.IsNullOrEmpty(key))
                yield return key;
        }
    }

    /// <summary>
    ///     Every token name the HDS dictionaries declare: the six palettes plus
    ///     the theme-independent <c>BaseTokens</c>, <c>Elevation</c> and
    ///     <c>Icons</c> that <c>App.axaml</c> merges alongside them.
    /// </summary>
    private static IReadOnlyList<string> DeclaredThemeTokenNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (string palette in HdsThemeCatalog.PaletteNames)
        {
            foreach (string key in DeclaredKeysIn($"Themes/Hds/{palette}.axaml"))
                names.Add(key);
        }

        foreach (string structural in new[] { "BaseTokens.axaml", "Elevation.axaml", "Icons.axaml" })
        {
            foreach (string key in DeclaredKeysIn($"Themes/Hds/{structural}"))
                names.Add(key);
        }

        return names.Order(StringComparer.Ordinal).ToArray();
    }

    // =====================================================================
    // Paths
    // =====================================================================

    private static string AppCSharpFiles() =>
        Directory
            .EnumerateFiles(Path.Combine(RepoRoot, AppRelative), "*.cs", SearchOption.AllDirectories)
            .Where(static file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !string.Equals(Path.GetFileName(file), "GlobalUsings.cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string ReadText(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static string RepoRoot
    {
        get
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

    private static string AppDir => Path.Combine(RepoRoot, AppRelative);
}