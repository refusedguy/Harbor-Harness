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

// MECHANISM (#1086, step 2, conveyor)
// -----------------------------------
// The declaration ban below is a ScanRule: one shape, no baseline, thirteen
// planted controls, a discovery floor over the guarded app. Enumeration and the
// control/discovery verdicts are ScanRunner's; this file keeps the issue prose
// and the test names.
//
// One deliberate carry, not a re-decision: the line filter is the old walk's
// own `IsProseOrBlank` (blank and `//` lines dropped, everything else graded
// raw — block comments included, exactly as before), plugged through the
// Func-overload in `ParseShadowDeclaration`. The six pre-fix declaration sites
// survive as literal controls, so "this rule was red" stays falsifiable. The
// generated-shape premise (`The_Generated_Shape_Is_Not_In_The_Tree`) is not a
// forbidden-shape scan and stays handwritten.

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

    private const string SubId = "HANDWRITTEN-INITIALIZE-COMPONENT";

    /// <summary>The declaration ban as data: one shape, no baseline, planted controls, a floor.</summary>
    private static readonly ScanRule Rule = new()
    {
        Id = "AvaloniaInitializeComponentShadow",
        Trees = ["apps/Harbor.App.Avalonia"],
        Forbidden =
        [
            new ScanForbidden(
                SubId,
                Declaration,
                "delete this method: the generated "
                + "`public void InitializeComponent(bool loadXaml = true)` is the one a constructor "
                + "should call. See issue #973."),
        ],
        Controls =
        [
            // The six exact pre-fix declaration sites, as literals: re-reading them
            // from the tree cannot work, because the fix deletes all six.
            new ScanControl("Prefix/ModalHostView.axaml.cs", "    private void InitializeComponent()", SubId),
            new ScanControl("Prefix/ComponentGalleryView.axaml.cs", "    private void InitializeComponent()", SubId),
            new ScanControl("Prefix/EmptyState.axaml.cs", "    private void InitializeComponent()", SubId),
            new ScanControl("Prefix/Kbd.axaml.cs", "    private void InitializeComponent()", SubId),
            new ScanControl("Prefix/SegmentedControl.axaml.cs", "    private void InitializeComponent()", SubId),
            new ScanControl("Prefix/StatusDot.axaml.cs", "    private void InitializeComponent()", SubId),
            // `InitializeComponent();` in a constructor is how every view in this app
            // legitimately inflates its XAML — a call, not a declaration.
            new ScanControl("Calls/Ctor.cs", "        InitializeComponent();", null),
            new ScanControl("Calls/Indented.cs", "            InitializeComponent();", null),
            new ScanControl(
                "Calls/Guarded.cs",
                "        if (global::Avalonia.Application.Current is not null)\n            InitializeComponent();",
                null),
            new ScanControl("Calls/Loader.cs", "        AvaloniaXamlLoader.Load(this);", null),
            new ScanControl("Calls/Qualified.cs", "        _ = view.InitializeComponent();", null),
            // Prose naming the defect and blank lines never reach the matcher.
            new ScanControl("Prose.cs", "        // a hand-written InitializeComponent copy", null),
            new ScanControl("Blank.cs", "   ", null),
        ],
        MinHits = 50,
        MustContain =
        [
            "apps/Harbor.App.Avalonia/Views/Components/StatusDot.axaml.cs",
        ],
        CustomParse = ParseShadowDeclaration,
    };

    /// <summary>
    ///     The custom parser: the old file walk's verdict over one file's raw
    ///     source — blank and <c>//</c> lines dropped, everything else graded raw
    ///     against the declaration shape.
    /// </summary>
    private static IEnumerable<ScanHit> ParseShadowDeclaration(string displayPath, string rawSource)
    {
        string[] lines = rawSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (IsProseOrBlank(lines[i]))
            {
                continue;
            }

            if (Declaration.IsMatch(lines[i]))
            {
                yield return new ScanHit(SubId, displayPath, i + 1, lines[i].Trim());
            }
        }
    }

    // ── the rule ──────────────────────────────────────────────────────────

    [Test]
    public async Task AvaloniaShell_DeclaresNoInitializeComponentOfItsOwn()
    {
        List<string> violations = ScanRunner.Evaluate(Rule);

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

        List<string> discovery = ScanRunner.CheckDiscovery(Rule);

        await Assert.That(discovery).IsEmpty()
            .Because("The guarded Avalonia shell holds well over 50 source files, including the "
                     + "StatusDot view that carried the live crash. "
                     + "A near-zero count means the path is stale and the rule guards nothing. "
                     + string.Join("; ", discovery));
    }

    [Test]
    public async Task ProseDescribingTheDefectIsNotADeclaration()
    {
        // The parser drops comments, so this rule can explain itself — and so can
        // the controls it fixed. StatusDot's constructor now carries a comment
        // naming the overload that used to shadow it, and ThemeResourceResolutionTests
        // documents the whole mechanism. None of that may be reported. The Prose.cs
        // and Blank.cs controls prove it through the REAL parser; the pairing below
        // proves the regex alone is not what saves us.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because("A comment naming the defect is prose, and a blank line carries no "
                      + "declaration either. If the parser reported either, the guard would fail "
                      + "on the documentation of its own rule. "
                      + string.Join("; ", failures));

        await Assert.That(Declaration.IsMatch("        // private void InitializeComponent()")).IsTrue()
            .Because("The regex does match the commented form — which is precisely why the parser has to drop "
                      + "comments first, and why this pair of assertions is the non-vacuity proof: matching prose "
                      + "is harmless ONLY because prose never reaches the matcher.");
    }

    [Test]
    public async Task Detector_FiresOnEveryDeclarationFoundInTheGuardedTree()
    {
        // THE POSITIVE CONTROL, and the reason the rule is known to have been red
        // before the fix.
        //
        // The six pre-fix declaration sites now live as literal controls on the
        // rule (Prefix/*.axaml.cs) — the complete set of hand-written declarations
        // in apps/Harbor.App.Avalonia at the commit that added this file. They are
        // literals on purpose: re-reading them from the tree cannot work, because
        // the fix deletes all six, and a positive control that disappears with the
        // bug it pins proves nothing.
        //
        // Only StatusDot of the six was a live crash: it is the only one whose
        // XAML names an element the code-behind dereferences (`Dot`, twice,
        // unguarded). EmptyState and ModalHostView generate PART_* fields their
        // code-behind never reads, and Kbd / SegmentedControl / ComponentGalleryView
        // generate no fields at all — so those five had a null field nobody
        // touched. Same latent defect, one live wound. The rule covers all six
        // because the next person to add an x:Name to Kbd would otherwise
        // inherit a null field nobody notices until it is dereferenced.
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because(
                "Every declaration the rule reported before the fix must still be one the parser "
                + "recognises. A miss here means the shape was loosened after the fact and the rule "
                + "now under-reports. "
                + string.Join("; ", failures));

        // Six recorded sites, and all six are the same shape. Pin the count so a
        // reader can tell a complete record from a partial one.
        await Assert.That(Rule.Controls.Count(c => c.ExpectSubId == SubId)).IsEqualTo(6)
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
        // must go, and getting it wrong produces a rule that blocks the fix. The
        // five call shapes live as silent controls on the rule (Calls/*).
        List<string> failures = ScanRunner.CheckControls(Rule);

        await Assert.That(failures).IsEmpty()
            .Because("These are calls to the generated member, not declarations of one. Flagging them "
                      + "would make the guard cry wolf on thirty-odd working constructors. "
                      + string.Join("; ", failures));
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

    /// <summary>
    ///     Every baseline row states why it is tolerated, in the row itself. Vacuous
    ///     while the table is empty, and deliberately so: wired from the first row
    ///     so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures = ScanRunner.CheckReasons(Rule);

        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     Every baseline row must still correspond to a real hit, so the table
    ///     cannot rot into a blanket permission: fix the code without deleting the
    ///     row and this fails.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_Are_Not_Stale()
    {
        List<string> stale = ScanRunner.StaleBaselineKeys(
            Rule, ScanRunner.ReadSources(ScanRunner.ScopeFiles(Rule)));

        await Assert.That(stale).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                + "problem that no longer exists: " + string.Join(", ", stale));
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

}
