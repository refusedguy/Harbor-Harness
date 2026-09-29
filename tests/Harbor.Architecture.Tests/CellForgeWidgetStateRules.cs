// CellForgeWidgetStateRules.cs — the guard for the widget-state half of #592.
//
// WHY THIS FILE EXISTS
// --------------------
// A CellForge widget lives in the hot repaint path, and a widget has real states:
// running vs completed, closed vs open, no selection, no theme applied yet. Those
// were all spelled `T?`, so a `null` in a public signature meant "one of several
// things" and nothing made any consumer decide which. Five such properties became
// `Maybe<T>` in this wave: `ToolCallBlock.Body`, `WhichKeyHelpOverlay.Context`,
// `ImageViewerOverlay.Source`, `FilePickerView.SelectedItem`,
// `ThemeFileWatcher.LastApplied`, plus `OnboardingFlow.Result` /
// `OnboardingFlow.SelectedProvider`.
//
// `BannedSymbols.txt` cannot express the rule — it names framework symbols, and
// "this property must not be a nullable reference" is not a symbol. Hence this file.
//
// THE LINE, AND WHY IT IS A LIST AND NOT AN AUTOMATIC RULE
// -------------------------------------------------------
// Sixteen nullable public properties in the same namespaces are NOT widget state.
// They are host-supplied hooks (a callback the host may not wire, a store it may
// not inject) or data fields of a value that is already present (`DiffText` on a
// result body means "this tool produced no diff"). Both have a working default, so
// a `Maybe` there would be ceremony: the consumer does not branch, it just reads.
//
// An automatic rule cannot tell the two apart — the difference is intent, not
// shape. So this file does not pretend to: it requires that EVERY nullable public
// property be CLASSIFIED, by name, in the table below. That is the rot being
// prevented. Not "no `T?` allowed" (which would be wrong), but "no `T?` allowed
// without someone having written down which side of the line it is on" — which is
// the #578 rule generalised to a shape that is not a `switch`.
//
// The table is checked in BOTH directions, because a list that only grows is a
// permission slip: a classified entry whose property has been deleted or converted
// to `Maybe` fails the build, so the table cannot rot into a blanket allowance.
//
// NON-VACUITY
// -----------
// Discovery returning nothing would make every rule below pass. The classification
// must stay non-trivial AND the discovery must still find reference-type nullables —
// currently sixteen — so a broken glob or a namespace rename goes red instead of
// quietly enforcing nothing.

using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Requires every nullable public property on a CellForge widget/panel to be
///     classified as either widget state (must be <see cref="Maybe{T}" />) or a
///     host-supplied hook / data field (may stay nullable). See the file header.
/// </summary>
public sealed class CellForgeWidgetStateRules
{
    /// <summary>Repo-relative trees whose widget surface this rule governs.</summary>
    private static readonly string[] GovernedTrees =
    [
        "src/Harbor.Tui.CellForge/Chat/Widgets",
        "src/Harbor.Tui.CellForge/Chat/Panels",
        "src/Harbor.Tui.CellForge/Chat/Onboarding",
    ];

    /// <summary>
    ///     Declaring type + property name of every nullable public property that is
    ///     <b>not</b> widget state, with the reason it stays <c>T?</c>.
    /// </summary>
    /// <remarks>
    ///     <para><b>Host-supplied hooks</b> — a working default exists and no consumer branches:</para>
    ///     <list type="bullet">
    ///         <item><description>Host-wired typed panel dependencies (#63 replaced the old IServiceProvider/Store pair).</description></item>
    ///         <item><description>Host-wired read-only panel snapshot.</description></item>
    ///         <item><description>Host callback — nothing to branch on, only to invoke.</description></item>
    ///         <item><description>Host-injected clock driving transitions.</description></item>
    ///         <item><description>Plugin-contributed slots; null means "no plugin contributed any".</description></item>
    ///         <item><description>Host-injected image sink.</description></item>
    ///         <item><description>Host callback.</description></item>
    ///         <item><description>Host callback.</description></item>
    ///     </list>
    ///     <para><b>Projection opt-in</b> — null selects the legacy path, it is not a state:</para>
    ///     <list type="bullet">
    ///         <item><description>When set, the status row projects from this snapshot instead of the view model.</description></item>
    ///         <item><description>Precomputed retry text; null when no retry is pending.</description></item>
    ///     </list>
    ///     <para><b>Content values</b> — null is a legitimate value of an already-present object:</para>
    ///     <list type="bullet">
    ///         <item><description>"this tool produced no diff" is data about a present result body.</description></item>
    ///         <item><description>Detected pixel dimensions of a present image; null = not detected yet.</description></item>
    ///         <item><description>Placeholder text; null draws the default prompt.</description></item>
    ///         <item><description>Settable one-shot status suffix; empty draws nothing.</description></item>
    ///     </list>
    ///     <para><b>Deferred to a different form</b>:</para>
    ///     <list type="bullet">
    ///         <item><description>Converted together with the TakePendingMsg() method return, which is its real consumer — a different form than this wave's property rule.</description></item>
    ///     </list>
    /// </remarks>
    private static readonly Dictionary<string, string> AllowedNullableProperties = new(StringComparer.Ordinal)
    {
        ["CellForgeDockPanel.Services"] = "host-wired typed panel dependencies; #63 replaced the IServiceProvider/Store pair",
        ["CellForgeDockPanel.View"] = "host-wired read-only panel snapshot",
        ["CommandPaletteView.OnCommit"] = "host callback — nothing to branch on, only to invoke",
        ["ComposerPanel.Placeholder"] = "placeholder text; null draws the default prompt",
        ["FilePickerView.OnCommit"] = "host callback — nothing to branch on, only to invoke",
        ["ImageBlock.Dimensions"] = "detected dimensions of a present image; null = not detected yet",
        ["LeaderKeyRouter.PendingMsg"] = "converted with the TakePendingMsg() method return — a different form",
        ["SessionTabStripPanel.Dispatch"] = "host callback — nothing to branch on, only to invoke",
        ["SideBarPanel.Slots"] = "plugin-contributed slots; null means no plugin contributed any",
        ["StatusPanel.AnimationClock"] = "host-injected clock driving transitions",
        ["StatusPanel.ProjectedElapsed"] = "optional value type; projection opt-in alongside ProjectedState",
        ["StatusPanel.ProjectedRetry"] = "precomputed retry text; null when no retry is pending",
        ["StatusPanel.ProjectedState"] = "when set, the row projects from this snapshot instead of the view model",
        ["ToolCallBlock.LiveSuffix"] = "settable one-shot status suffix; empty draws nothing",
        ["ToolResultBody.DiffText"] = "'this tool produced no diff' is data about a present result body",
        ["VirtualizedChatTimeline.InlineImages"] = "host-injected image sink",
    };

    /// <summary>
    ///     A public property declared with a <c>?</c> on a reference or value type.
    /// </summary>
    /// <remarks>
    ///     Value types match this pattern too (<c>TimeSpan?</c> is classified below), and
    ///     that is deliberate rather than an oversight: the classification is about
    ///     <b>intent</b>, and <c>StatusPanel.ProjectedElapsed</c> is classified with the
    ///     reason that <c>Nullable&lt;TimeSpan&gt;</c> is already the idiomatic spelling of
    ///     an optional value and <c>Maybe&lt;TimeSpan&gt;</c> would be ceremony. Filtering
    ///     value types out of the pattern would hide that decision instead of recording it.
    ///     <para>
    ///         Known limitation: a declaration split across lines (type on one line,
    ///         <c>{ get; set; }</c> on the next) is not matched. The codebase writes these
    ///         declarations on one line, and a future multi-line spelling is a property this
    ///         rule would miss — recorded rather than pretended away.
    ///     </para>
    /// </remarks>
    private const string NullableReferencePropertyPattern =
        @"^\s*public\s+(?:static\s+|virtual\s+|override\s+|abstract\s+|required\s+)*[A-Za-z_][\w\.]*(?:<[^>]*>)?\?\s+[A-Za-z_]\w*\s*(?:\{|=>)";

    // ── Rule 1: completeness — nothing unclassified ──────────────────────────

    [Test]
    public async Task EveryNullablePublicWidgetProperty_IsClassified()
    {
        IReadOnlyList<(string Key, string Location)> found = FindNullableReferenceProperties();

        await Assert.That(found.Count).IsGreaterThanOrEqualTo(10)
            .Because(
                "Non-vacuity: discovery must still reach the widget surface. Fewer than ten reference-type " +
                "nullables means a glob, a namespace or the property pattern stopped matching, and the " +
                "completeness check below would then be enforcing nothing.");

        List<string> unclassified = [];
        foreach ((string key, string location) in found)
        {
            if (!AllowedNullableProperties.ContainsKey(key))
            {
                unclassified.Add($"{key}  ({location})");
            }
        }

        await Assert.That(unclassified).IsEmpty()
            .Because(
                "A nullable public property on a widget is either widget STATE — in which case it must be " +
                "Maybe<T> so every consumer has to decide, as ToolCallBlock.Body now is — or a host-supplied " +
                "hook with a working default, in which case it belongs in AllowedNullableProperties with a " +
                "reason. Silence is not one of the two options (#592).");
    }

    // ── Rule 2: freshness — the classification cannot rot into a permission ───

    [Test]
    public async Task ClassifiedProperties_StillExistAndAreStillNullableReferences()
    {
        Dictionary<string, string> present = FindNullableReferenceProperties()
            .ToDictionary(x => x.Key, x => x.Location, StringComparer.Ordinal);

        List<string> stale = [];
        foreach (string key in AllowedNullableProperties.Keys.Order(StringComparer.Ordinal))
        {
            if (!present.ContainsKey(key))
            {
                stale.Add(key);
            }
        }

        await Assert.That(stale).IsEmpty()
            .Because(
                "Each entry is an exemption with a written reason. When the property is deleted, " +
                "renamed, or converted to Maybe<T> — as this wave converted seven — the exemption must " +
                "be deleted with it. A list that only ever grows is a permission slip, not a rule.");
    }

    // ── Rule 3: no suppressed dereference of a widget's own nullable state ──

    [Test]
    public async Task WidgetState_IsNeverDereferencedThroughASuppression()
    {
        // `ToolCallBlock` carried four `_body!` in its paint/measure path. Each one was a
        // place where the "no body yet" guard lived in a DIFFERENT method than the deref,
        // so the compiler could not see it and a fourth entry point would have NRE'd with
        // nothing to catch it. The fix was Maybe<T> plus an explicit narrow at each use.
        //
        // This catches the shape, not just the instance: a private nullable field of a
        // widget, dereferenced with `!`, inside a member whose name says it is part of the
        // paint or measure path. A suppression there means some other method is carrying
        // the invariant, which is the thing that rots.
        IReadOnlyList<string> hits = FindSuppressedStateDereferences();

        await Assert.That(hits).IsEmpty()
            .Because(
                "A `!` on a widget's own nullable state inside Paint/Measure/CheapEstimate/HasX means " +
                "the absence guard lives somewhere else. That is how four suppressions survived in " +
                "ToolCallBlock and how a new entry point would inherit an NRE nobody can see. " +
                "Narrow explicitly with HasValue, or make the state a Maybe and read through it (#592).");
    }

    // ── Non-vacuity of the classification table itself ───────────────────────

    [Test]
    public async Task ClassificationTable_IsNotEmptyAndNotBlanket()
    {
        await Assert.That(AllowedNullableProperties.Count).IsGreaterThanOrEqualTo(10)
            .Because("An empty or nearly-empty table would mean the rule above is a blanket ban wearing a list's clothes.");

        List<string> unreasoned =
            [.. AllowedNullableProperties.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key)];

        await Assert.That(unreasoned).IsEmpty()
            .Because("Every exemption carries a reason. A bare name is how a hand-maintained list starts rotting (#578).");
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Substrings that mark a member as part of the paint / measure / projection path.
    /// </summary>
    /// <remarks>
    ///     Substrings, not a list of member names. An earlier draft enumerated names and
    ///     missed <c>ExpandedBodyLineCount</c> — one of the four suppressions this rule
    ///     exists to catch — which is precisely the hand-maintained-union failure this repo
    ///     already tracks as #578. A name list would rot the same way; a marker substring
    ///     catches the sibling the moment it is written.
    /// </remarks>
    private static readonly string[] PaintPathMarkers =
    [
        "Paint", "Measure", "Estimate", "LineCount", "Count", "Diff", "RawText", "ViewModel",
    ];

    /// <summary>Whether a member name belongs to the paint / measure / projection path.</summary>
    private static bool IsPaintPathMember(string name) =>
        PaintPathMarkers.Any(marker => name.Contains(marker, StringComparison.Ordinal));

    /// <summary>
    ///     Locations where a private nullable field is dereferenced through <c>!</c> inside
    ///     a paint/measure-path member.
    /// </summary>
    /// <remarks>
    ///     Returns an empty list both when the codebase is clean AND when the scan reaches
    ///     no files at all, so this rule alone cannot be trusted to be non-vacuous — which
    ///     is why <see cref="EveryNullablePublicWidgetProperty_IsClassified" /> carries the
    ///     non-vacuity burden for the whole file.
    /// </remarks>
    internal static IReadOnlyList<string> FindSuppressedStateDereferences()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        // A `!` immediately before a `.` on an identifier starting with `_` that is declared
        // `private T? _name` in the same file: the widget's own state, not a parameter and
        // not a host-injected hook.
        var fieldDecl = new Regex(@"\bprivate\s+[A-Za-z_][\w\.]*(?:<[^>]*>)?\?\s+(_[A-Za-z_]\w*)\s*[;=]", RegexOptions.CultureInvariant);
        var suppress = new Regex(@"\b(_[A-Za-z_]\w*)!\s*[.;)]", RegexOptions.CultureInvariant);
        var member = new Regex(@"\b(?:public|private|internal|protected)\s+[\w<>,\[\]\. ]*?\b([A-Za-z_]\w*)\s*\(", RegexOptions.CultureInvariant);

        var hits = new List<string>();

        foreach (string tree in GovernedTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string text = File.ReadAllText(file);
                var nullableFields = new HashSet<string>(
                    fieldDecl.Matches(text).Select(m => m.Groups[1].Value), StringComparer.Ordinal);

                if (nullableFields.Count == 0)
                {
                    continue;
                }

                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                string? currentMember = null;
                int lineNumber = 0;

                foreach (string line in File.ReadLines(file))
                {
                    lineNumber++;

                    Match declaration = member.Match(line);
                    if (declaration.Success)
                    {
                        currentMember = declaration.Groups[1].Value;
                    }

                    if (currentMember is null || !IsPaintPathMember(currentMember))
                    {
                        continue;
                    }

                    foreach (Match hit in suppress.Matches(line))
                    {
                        if (nullableFields.Contains(hit.Groups[1].Value))
                        {
                            hits.Add($"{relative}:{lineNumber}  {hit.Groups[1].Value}! in {currentMember}");
                        }
                    }
                }
            }
        }

        return hits;
    }

    /// <summary>Every nullable public property on a type in the governed trees.</summary>
    internal static IReadOnlyList<(string Key, string Location)> FindNullableReferenceProperties()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var pattern = new Regex(NullableReferencePropertyPattern, RegexOptions.CultureInvariant);
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (string tree in GovernedTrees)
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                string? currentType = null;
                int lineNumber = 0;

                foreach (string line in File.ReadLines(file))
                {
                    lineNumber++;

                    Match declaration = TypeDeclaration.Match(line);
                    if (declaration.Success)
                    {
                        currentType = declaration.Groups["name"].Value;
                    }

                    if (currentType is null || !pattern.IsMatch(line))
                    {
                        continue;
                    }

                    Match property = PropertyName.Match(line);
                    if (!property.Success)
                    {
                        continue;
                    }

                    string key = $"{currentType}.{property.Groups["name"].Value}";
                    found.TryAdd(key, $"{relative}:{lineNumber}");
                }
            }
        }

        return [.. found.Select(kv => (kv.Key, kv.Value))];
    }

    /// <summary>Matches a type declaration and captures its name, so properties get attributed to a type.</summary>
    private static readonly Regex TypeDeclaration = new(
        @"\b(?:class|record|struct)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Captures the property name from a line already accepted by <see cref="NullableReferencePropertyPattern" />.</summary>
    private static readonly Regex PropertyName = new(
        @"\?\s+(?<name>[A-Za-z_]\w*)\s*(?:\{|=>)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
