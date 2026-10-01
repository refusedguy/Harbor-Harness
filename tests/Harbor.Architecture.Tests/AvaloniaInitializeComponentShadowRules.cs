// AvaloniaInitializeComponentShadowRules.cs — source-level guard against a
// hand-written `InitializeComponent` shadowing the one Avalonia's source
// generator emits (issue #973).
//
// THE DEFECT THIS GUARDS
// ----------------------
// With `AvaloniaUseCompiledXaml`, every `.axaml` with an `x:Class` gets a
// generated partial member from `Avalonia.Generators.NameGenerator`. For a
// XAML file that names an element (`x:Name="Dot"`, or an `x:Name`d template
// part) the generator emits BOTH a field and a method that assigns it:
//
//     internal global::Avalonia.Controls.Shapes.Ellipse Dot;
//
//     public void InitializeComponent(bool loadXaml = true)
//     {
//         if (loadXaml) { AvaloniaXamlLoader.Load(this); }
//         var __thisNameScope__ = this.FindNameScope();
//         Dot = __thisNameScope__?.Find<Ellipse>("Dot");   // ← the assignment
//     }
//
// A code-behind that also declares
//
//     private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
//
// does NOT collide, and that is the trap: the generated signature is
// `InitializeComponent(bool loadXaml = true)`, so the two overloads coexist and
// the tree compiles clean. But a parameterless call — `InitializeComponent();`
// in the constructor — binds to the HAND-WRITTEN one by exact match, and the
// generated overload that assigns `Dot` is never entered. `Dot` stays null.
//
// `StatusDot` dereferenced it unguarded on the very next line of the same
// constructor, so every instantiation threw NullReferenceException. That is a
// visible crash, not a latent one: MainWindow.axaml hosts BoardView, BoardView's
// ItemsControl inflates one SessionCardView per stored session, and each card's
// status pill contains `<comp:StatusDot/>` (SessionCardView.axaml). A user with
// one stored session who clicks the "Sessions" tab gets it.
//
// WHY "DECLARE NOTHING" AND NOT "DECLARE IT CORRECTLY"
// ---------------------------------------------------
// The rule is deliberately absolute: no `.cs` file under the guarded project
// declares `InitializeComponent` at all. Three reasons:
//
//   1. The generated overload is always the right one. It does the
//      `AvaloniaXamlLoader.Load` a hand-written copy does, PLUS the field
//      wiring, PLUS the `loadXaml: false` capability. A hand-written copy can
//      only ever be a strictly worse subset of it.
//   2. A correct hand-written copy is not expressible. There is no way to write
//      `private void InitializeComponent()` that also assigns `Dot`, short of
//      calling the overload with an explicit argument — at which point you are
//      calling the generated one and the copy is pointless.
//   3. It is the only rule a text scan can enforce. The generated member is not
//      in the tree. `RepoPaths.EnumerateCsFiles` reads the working tree; the
//      generator's output lives in `obj/Generated/`, which is excluded — exactly
//      the blindness that let `contrib/` slip past `SourceScan.IsBuildOutput`
//      (#877). So a rule phrased as "this file must not disagree with the
//      generated member" would be uncheckable, and a rule that cannot run is not
//      a rule. Forbidding the DECLARATION is checkable, and it happens to be the
//      correct engineering answer anyway.
//
//   `The_Generated_Shape_Is_Not_In_The_Tree` pins that premise, so if a future
//   Avalonia upgrade starts committing generated sources this file's stated
//   justification is re-examined instead of silently outliving it.
//
// SCOPE
// -----
// `apps/Harbor.App.Avalonia` only. Same reasoning as
// AvaloniaFireAndForgetRules: the guard polices the perimeter that was actually
// fixed. `contrib/` holds an unmaintained WPF/Maui/Blazor stack that CI does not
// build, and it is out of bounds for edits as well as for this rule.
//
// NON-VACUITY
// -----------
// This repository has been bitten by guards that pass while enforcing nothing, so
// the rule carries four checks that make it failable:
//
//   * `Scanner_FindsTheGuardedProject` — the walk really reached the app (91
//     .cs files). Without a repo root every rule in this file passes vacuously.
//   * `ProseDescribingTheDefectIsNotADeclaration` — the scanner drops comments,
//     and the regex does match the commented form. That pair is the proof the
//     rule can explain itself: StatusDot's constructor carries a comment naming
//     the overload that used to shadow it, and matching prose is harmless only
//     because prose never reaches the matcher.
//   * `Detector_FiresOnEveryDeclarationFoundInTheGuardedTree` — the six exact
//     (path, line, verbatim line) sites the scan reported before the fix, as
//     literals, so the record survives the fix that deletes them and the claim
//     "this rule was red" stays falsifiable.
//   * `Detector_IgnoresTheCallSitesThatMustKeepWorking` — `InitializeComponent();`
//     is how thirty-odd constructors legitimately CALL the generated member.
//     Flagging a call would be a false-positive machine that gets deleted.
//   * `The_Generated_Shape_Is_Not_In_The_Tree` — the premise in "WHY" above.
//
// A rule that cannot fail is not a rule. These tests are what make this one
// failable.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     §UI guard: no hand-written <c>InitializeComponent</c> in the Avalonia
///     desktop shell (#973). A hand-written copy shadows the generator's
///     overload, so the named-element field it assigns is never wired and the
///     control dereferences null in its own constructor.
/// </summary>
public class AvaloniaInitializeComponentShadowRules
{
    /// <summary>
    ///     The project this rule polices — the one perimeter that was actually
    ///     fixed. Widening it without converting is how a guard becomes a lie.
    /// </summary>
    private static readonly string[] GuardedProjects = ["apps/Harbor.App.Avalonia"];

    /// <summary>
    ///     A DECLARATION of <c>InitializeComponent</c> — a modifier, a
    ///     <c>void</c> return, the name, then an open paren. The paren is what
    ///     separates a declaration from the <c>InitializeComponent();</c> call
    ///     sites that must stay legal, and from prose about it.
    /// </summary>
    /// <remarks>
    ///     The parameter list is deliberately unanchored: it matches the
    ///     parameterless <c>()</c> form that is the defect, and equally a
    ///     hypothetical <c>InitializeComponent(bool)</c> copy, which is the same
    ///     mistake wearing a different hat. It does NOT need to model the
    ///     generated signature, because the generated member is not in the tree
    ///     — see <see cref="The_Generated_Shape_Is_Not_In_The_Tree" />.
    /// </remarks>
    private static readonly Regex Declaration = new(
        @"\b(?:private|public|protected|internal)\s+void\s+InitializeComponent\s*\(",
        RegexOptions.Compiled);

    // ── the rule ──────────────────────────────────────────────────────────

    [Test]
    public async Task AvaloniaShell_DeclaresNoInitializeComponentOfItsOwn()
    {
        var violations = new List<string>();

        foreach ((string file, int line, string text) in ScanGuardedFiles())
        {
            if (!Declaration.IsMatch(text))
            {
                continue;
            }

            violations.Add(
                $"{Relative(file)}:{line} — hand-written InitializeComponent shadows the overload "
                + $"Avalonia's NameGenerator emits, so the named-element field it assigns is never wired. "
                + $"Delete this method: the generated `public void InitializeComponent(bool loadXaml = true)` "
                + $"is the one a constructor should call. {text.Trim()}");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "§UI (#973). The generated overload and this hand-written copy coexist because their "
                + "signatures differ (`InitializeComponent(bool loadXaml = true)` vs `InitializeComponent()`), "
                + "so the tree compiles — and a parameterless call in the constructor binds to the hand-written "
                + "one, skipping the `Dot = FindNameScope()?.Find<Ellipse>(\"Dot\")` assignment. Any control that "
                + "then reads that field NREs inside its own constructor. In this app that is StatusDot, which "
                + "MainWindow reaches through BoardView → SessionCardView, so it is a visible crash on the "
                + "Sessions tab rather than a latent one.");
    }

    // ── non-vacuity ───────────────────────────────────────────────────────

    [Test]
    public async Task Scanner_FindsTheGuardedProject()
    {
        // Without a repository root every rule in this file passes vacuously.
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "This guard walks the working tree. With no Harbor.slnx above AppContext.BaseDirectory "
                + "the scan yields nothing and the rule reports green while enforcing nothing.");

        if (root is null)
        {
            return;
        }

        int files = GuardedProjects
            .SelectMany(p => Directory.GetFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Count(f => !IsBuildOutput(f));

        await Assert.That(files).IsGreaterThan(50)
            .Because($"The guarded Avalonia shell holds well over 50 source files; found {files}. "
                     + "A near-zero count means the path is stale and the rule guards nothing.");
    }

    [Test]
    public async Task ProseDescribingTheDefectIsNotADeclaration()
    {
        // The scanner drops comments, so this rule can explain itself — and so can
        // the controls it fixed. StatusDot's constructor now carries a comment
        // naming the overload that used to shadow it, and ThemeResourceResolutionTests
        // documents the whole mechanism. None of that may be reported.
        //
        // The earlier version of this file asserted "zero lines in the guarded tree
        // both start with // and match the declaration regex". That was the wrong
        // invariant: it forbade the documentation rather than the defect, so the
        // first honest comment explaining the bug would have failed the build. What
        // actually has to hold is that the scanner drops prose, which is what these
        // two cases pin.
        await Assert.That(IsProseOrBlank("        // a hand-written InitializeComponent copy")).IsTrue()
            .Because("A comment naming the defect is prose. If the scanner reported it, a guard would fail on "
                     + "the documentation of its own rule, and the documentation would get deleted instead.");

        await Assert.That(IsProseOrBlank("   ")).IsTrue()
            .Because("A blank line carries no declaration either.");

        await Assert.That(IsProseOrBlank("    private void InitializeComponent()")).IsFalse()
            .Because("This is the defect. If the scanner dropped it, the rule would report nothing and pass.");

        // And the pairing that makes the rule work at all: prose is dropped AND a
        // declaration is kept, so the difference between them is the whole rule.
        await Assert.That(Declaration.IsMatch("        // private void InitializeComponent()")).IsTrue()
            .Because("The regex does match the commented form — which is precisely why the scanner has to drop "
                     + "comments first, and why this pair of assertions is the non-vacuity proof: matching prose "
                     + "is harmless ONLY because prose never reaches the matcher.");
    }

    [Test]
    public async Task Detector_FiresOnEveryDeclarationFoundInTheGuardedTree()
    {
        // THE POSITIVE CONTROL, and the reason the rule is known to have been red
        // before the fix.
        //
        // These six are the complete set of hand-written declarations in
        // apps/Harbor.App.Avalonia at the commit that added this file, recorded
        // as (repo-relative path, line, verbatim line). They are literals on
        // purpose: re-reading them from the tree cannot work, because the fix
        // deletes all six, and a positive control that disappears with the bug
        // it pins proves nothing. A literal keeps the pre-fix state reviewable
        // forever, and keeps the rule failable on a tree it has never seen.
        //
        // Only StatusDot of the six was a live crash: it is the only one whose
        // XAML names an element the code-behind dereferences (`Dot`, twice,
        // unguarded). EmptyState and ModalHostView generate PART_* fields their
        // code-behind never reads, and Kbd / SegmentedControl / ComponentGalleryView
        // generate no fields at all — so those five had a null field nobody
        // touched. Same latent defect, one live wound. The rule covers all six
        // because the next person to add an x:Name to Kbd would otherwise
        // inherit a null field nobody notices until it is dereferenced.
        (string Path, int Line, string Text)[] sitesFoundBeforeTheFix =
        [
            ("apps/Harbor.App.Avalonia/Views/Overlays/ModalHostView.axaml.cs", 20,
                "    private void InitializeComponent()"),
            ("apps/Harbor.App.Avalonia/Views/Dev/ComponentGalleryView.axaml.cs", 14,
                "    private void InitializeComponent()"),
            ("apps/Harbor.App.Avalonia/Views/Components/EmptyState.axaml.cs", 64,
                "    private void InitializeComponent()"),
            ("apps/Harbor.App.Avalonia/Views/Components/Kbd.axaml.cs", 23,
                "    private void InitializeComponent()"),
            ("apps/Harbor.App.Avalonia/Views/Components/SegmentedControl.axaml.cs", 31,
                "    private void InitializeComponent()"),
            ("apps/Harbor.App.Avalonia/Views/Components/StatusDot.axaml.cs", 69,
                "    private void InitializeComponent()")
        ];

        var missed = new List<string>();
        foreach ((string path, int line, string text) in sitesFoundBeforeTheFix)
        {
            if (!Declaration.IsMatch(text))
            {
                missed.Add($"{path}:{line}");
            }
        }

        await Assert.That(missed).IsEmpty()
            .Because(
                "Every declaration the rule reported before the fix must still be one the detector recognises. "
                + "A miss here means the regex was loosened after the fact and the rule now under-reports: "
                + string.Join(", ", missed));

        // Six recorded sites, and all six are the same shape. Pin the count so a
        // reader can tell a complete record from a partial one.
        await Assert.That(sitesFoundBeforeTheFix.Length).IsEqualTo(6)
            .Because("The pre-fix scan of apps/Harbor.App.Avalonia found exactly six hand-written declarations. "
                     + "If this number is edited, the fix has changed scope and the header claim must change with it.");

        // Indentation must not decide whether a declaration is seen: a rule
        // anchored to a column catches one file and misses the rest.
        await Assert.That(Declaration.IsMatch("        private void InitializeComponent()")).IsTrue()
            .Because("An eight-space-indented declaration is the same defect as a four-space one.");

        // The generator's own signature, if it ever appeared in the tree, would
        // also be a declaration — which is exactly why
        // The_Generated_Shape_Is_Not_In_The_Tree is load-bearing rather than
        // decorative.
        await Assert.That(Declaration.IsMatch("    public void InitializeComponent(bool loadXaml = true)")).IsTrue()
            .Because("The detector matches any declared signature. That is right for a hand-written copy and "
                     + "wrong for the generator's output, so the premise test below has to keep holding.");
    }

    [Test]
    public async Task Detector_IgnoresTheCallSitesThatMustKeepWorking()
    {
        // `InitializeComponent();` in a constructor is how every view in this app
        // legitimately inflates its XAML. The paren-after-the-name is the whole
        // difference between the call that must stay and the declaration that
        // must go, and getting it wrong produces a rule that blocks the fix.
        string[] mustPass =
        [
            "        InitializeComponent();",
            "            InitializeComponent();",
            "        if (global::Avalonia.Application.Current is not null)\n            InitializeComponent();",
            "        AvaloniaXamlLoader.Load(this);",
            "        _ = view.InitializeComponent();"
        ];

        foreach (string good in mustPass)
        {
            await Assert.That(Declaration.IsMatch(good)).IsFalse()
                .Because("This is a call to the generated member, not a declaration of one. Flagging it would "
                         + "make the guard cry wolf on thirty-odd working constructors: " + good.Trim());
        }
    }

    [Test]
    public async Task The_Generated_Shape_Is_Not_In_The_Tree()
    {
        // The premise behind phrasing the rule as "declare nothing".
        //
        // The rule is absolute because the alternative — checking that a
        // hand-written copy agrees with the generated member — is not checkable
        // from source. The generator writes into `obj/Generated/`, which every
        // walker here excludes. This is the same blindness that let `contrib/`
        // pass `SourceScan.IsBuildOutput` (#877): a path-based filter, an
        // unexamined assumption, and a guard that could not see part of the
        // repository.
        //
        // If an Avalonia upgrade ever commits generated sources into the tree,
        // this test goes red and the rule's justification gets re-argued on the
        // merits rather than outliving its own premise.
        int generatedInTree = GuardedProjects
            .Where(p => RepoPaths.RepoRoot is not null)
            .SelectMany(p => Directory.GetFiles(Path.Combine(RepoPaths.RepoRoot!, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsBuildOutput(f))
            .SelectMany(ScanFile)
            .Count(hit => hit.Text.Contains("CodeDom.Compiler.GeneratedCode", StringComparison.Ordinal)
                          || hit.Text.Contains("auto-generated", StringComparison.Ordinal));

        await Assert.That(generatedInTree).IsEqualTo(0)
            .Because(
                "This rule's stated basis is that the generated InitializeComponent is invisible to a source "
                + "scan, so 'declare nothing' is the only enforceable phrasing. Generated code is now present in "
                + "the guarded tree — re-derive the rule against what is actually checkable, because the current "
                + "one would now flag the generator's own members.");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    ///     Whether the scanner drops this line as blank or as prose. The single
    ///     place that decision is made, so the rule and
    ///     <see cref="ProseDescribingTheDefectIsNotADeclaration" /> cannot disagree
    ///     about it.
    /// </summary>
    internal static bool IsProseOrBlank(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>Every non-comment, non-blank source line of the guarded projects.</summary>
    private static IEnumerable<(string File, int Line, string Text)> ScanGuardedFiles()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            yield break;
        }

        foreach (string project in GuardedProjects)
        {
            string dir = Path.Combine(root, project);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file))
                {
                    continue;
                }

                foreach ((int line, string text) in ScanFile(file))
                {
                    yield return (file, line, text);
                }
            }
        }
    }

    /// <summary>One file, one-based line numbers, comment and blank lines dropped.</summary>
    private static IEnumerable<(int Line, string Text)> ScanFile(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            yield break;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string text = lines[i];
            if (IsProseOrBlank(text))
            {
                continue;
            }

            yield return (i + 1, text);
        }
    }

    /// <summary>Repo-relative, forward-slashed path for stable failure messages.</summary>
    private static string Relative(string absolutePath) =>
        (RepoPaths.RepoRoot is null ? absolutePath : Path.GetRelativePath(RepoPaths.RepoRoot, absolutePath))
        .Replace('\\', '/');
}
