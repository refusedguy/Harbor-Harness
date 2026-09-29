// ProviderCatalogueBudgetRules.cs — guard for #685.
//
// #671 already settled the two halves of this subject, and this file must not
// reopen either of them:
//
//   * A provider PROBE ("can this provider answer?") spends the canonical
//     IProviderHealthCheck.DefaultTimeout. ProviderConfigViewModel.TestConnectionAsync
//     does, and ProviderAuthSingleAnswerRules holds it there.
//
//   * A UI CATALOGUE WAIT ("what models does this provider list, and how long
//     may the user stare at an empty picker?") is a different question with a
//     deliberately different number. #671 renamed it from the probe-flavoured
//     "ModelFetchTimeout" to "UiFeedbackBudget" and said so in the picker's
//     doc comment.
//
// What #671 did NOT settle is the scope of the rename. Its guard reads exactly
// one file — ProviderModelPickerViewModel.cs — because widening the scan "would
// make the gate red on files this change does not own". So the concept is
// governed where someone remembered and unguarded everywhere else, and the two
// subjects that bound the IDENTICAL registry fan-out through the IDENTICAL
// AsyncFeed<T> kept the old name and their own copy of the number:
//
//   ProviderBrowserViewModel.cs:22          ModelFetchTimeout = 5s   (live surface)
//   ProviderModelPickerViewModelBase.cs:39  ModelFetchTimeout = 5s   (unreferenced)
//
// That is precisely the drift #685 predicts: when the budget moves, whoever
// remembers updates the picker and the browser keeps waiting its own five
// seconds, and the user reports it as "sometimes settings hang".
//
// This file therefore does not invent a home for the number. The budget already
// HAS an owner — ProviderModelPickerViewModel.UiFeedbackBudget, named and
// documented by #671 — so the rule is not "relocate the constant" but "there is
// exactly one declaration, and every other catalogue surface spends it". A new
// shared holder would be the same magic number in a different file, which is
// no better than what shipped.
//
// Subjects are DISCOVERED, not listed. A rule that enumerates its own subjects
// rots: the moment a fourth catalogue surface appears it is silently
// ungoverned, which is the exact failure mode this issue is about. Discovery is
// anchored on the fan-out itself (the AsyncFeed that carries the budget, the
// GetAllModelsAsync call it bounds, the two names the concept has worn), so a
// new surface joins the rule the moment it touches the registry.
//
// SOURCE-level rule, like its #671 sibling: no single runtime call is wrong in
// isolation — the surfaces are each self-consistent and mutually inconsistent.
// Reading the four files side by side is what makes the disagreement visible.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Asserts that a UI surface which loads the provider model catalogue spends
///     one declared budget — <c>UiFeedbackBudget</c> — rather than each surface
///     picking a duration of its own, and that no surface borrows the
///     probe-flavoured name that made the two budgets indistinguishable.
/// </summary>
public class ProviderCatalogueBudgetRules
{
    /// <summary>The project whose view-models load the provider catalogue.</summary>
    private const string ProjectDir = "Harbor.Desktop.Abstractions";

    /// <summary>The name #671 gave the catalogue wait. Exactly one file declares it.</summary>
    private const string CanonicalName = "UiFeedbackBudget";

    /// <summary>The probe-flavoured name #671 retired. No surface may reintroduce it.</summary>
    private const string RetiredName = "ModelFetchTimeout";

    /// <summary>The canonical probe budget, for surfaces that probe rather than list.</summary>
    private const string ProbeCanon = "IProviderHealthCheck.DefaultTimeout";

    /// <summary>
    ///     Any term that marks a file as participating in the catalogue wait:
    ///     the feed that carries the budget, the call it bounds, or either name
    ///     the concept has worn. This is the discovery predicate.
    /// </summary>
    private static readonly string[] CatalogueMarkers =
        ["AsyncFeed", "GetAllModelsAsync", RetiredName, CanonicalName];

    /// <summary>
    ///     A hard-coded duration literal of any unit — the shape that let each
    ///     surface answer "how long may I wait?" for itself.
    /// </summary>
    private static readonly Regex DurationLiteral =
        new(@"TimeSpan\s*\.\s*From(?:Seconds|Milliseconds|Minutes|Hours)\s*\(",
            RegexOptions.Compiled);

    /// <summary>A declaration of the canonical budget — the one owner this rule admits.</summary>
    private static readonly Regex CanonicalDeclaration =
        new(@"static\s+readonly\s+TimeSpan\s+" + CanonicalName, RegexOptions.Compiled);

    /// <summary>Actual code that spends a budget, as opposed to prose that mentions one.</summary>
    private static readonly Regex SpendsABudget =
        new(@"new\s+AsyncFeed|GetAllModelsAsync\s*\(", RegexOptions.Compiled);

    /// <summary>Source of a view-model file, or <c>null</c> when the directory is absent.</summary>
    private static string? ReadViewModel(string fileName)
    {
        if (RepoPaths.FindProjectDir(ProjectDir) is not { } dir)
        {
            return null;
        }

        string path = Path.Combine(dir, "ViewModels", fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    ///     Every view-model in the governed project that participates in the
    ///     catalogue wait, discovered by <see cref="CatalogueMarkers" /> rather
    ///     than listed by hand. Sorted for a deterministic failure message.
    /// </summary>
    private static IReadOnlyDictionary<string, string> CatalogueSurfaces()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (RepoPaths.FindProjectDir(ProjectDir) is not { } dir)
        {
            return found;
        }

        string viewModels = Path.Combine(dir, "ViewModels");
        if (!Directory.Exists(viewModels))
        {
            return found;
        }

        foreach (string path in Directory.GetFiles(viewModels, "*.cs").OrderBy(p => p, StringComparer.Ordinal))
        {
            string source = File.ReadAllText(path);
            if (CatalogueMarkers.Any(marker => source.Contains(marker, StringComparison.Ordinal)))
            {
                found[Path.GetFileName(path)] = source;
            }
        }

        return found;
    }

    /// <summary>Joins a violation list for a failure message.</summary>
    private static string Offenders(IEnumerable<string> violations)
    {
        string[] list = violations.ToArray();
        return list.Length == 0 ? "(none)" : string.Join("; ", list);
    }

    [Test]
    public async Task TheCatalogueWaitHasExactlyOneOwner()
    {
        string[] owners =
            [.. CatalogueSurfaces()
                .Where(kv => CanonicalDeclaration.IsMatch(kv.Value))
                .Select(kv => kv.Key)
                .OrderBy(name => name, StringComparer.Ordinal)];

        var violations = new List<string>();
        if (owners.Length != 1)
        {
            violations.Add(
                $"expected exactly one declaration of {CanonicalName}, found {owners.Length} ({Offenders(owners)})");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "One question gets one answer. A UI surface that lists the provider catalogue must learn how long it "
                + "may wait from a single declared budget, and that budget must be declared once. Two owners is two "
                + "numbers under one concept again — the exact shape #685 reports as 'sometimes settings hang'. "
                + "Offenders: " + Offenders(violations));
    }

    [Test]
    public async Task NoCatalogueSurfaceDeclaresADurationOfItsOwn()
    {
        var violations = new List<string>();

        foreach ((string file, string source) in CatalogueSurfaces())
        {
            // The owner is the one place the number is allowed to be written down.
            if (CanonicalDeclaration.IsMatch(source))
            {
                continue;
            }

            foreach (Match match in DurationLiteral.Matches(source))
            {
                violations.Add($"{file}: declares a duration literal ('{match.Value}') outside the budget owner");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "Only the budget's owner states a duration. Every other catalogue surface must spend the declared "
                + CanonicalName + " — a surface that picks its own number is a surface that will keep waiting its own "
                + "five seconds after the budget moves, which is the drift #685 is about. Offenders: "
                + Offenders(violations));
    }

    [Test]
    public async Task NoCatalogueSurfaceNamesItsBudgetLikeAProbe()
    {
        var violations = CatalogueSurfaces()
            .Where(kv => kv.Value.Contains(RetiredName, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToArray();

        await Assert.That(violations).IsEmpty()
            .Because(
                "'" + RetiredName + "' is the probe-flavoured name #671 retired: it made a catalogue wait "
                + "indistinguishable from a provider probe, and the two budgets are deliberately different numbers. "
                + "A surface may describe a catalogue wait only as " + CanonicalName + " — or as the probe canon ("
                + ProbeCanon + ") when it is genuinely probing. Offenders: " + Offenders(violations));
    }

    [Test]
    public async Task EveryCatalogueSpenderNamesARealBudget()
    {
        var violations = new List<string>();

        foreach ((string file, string source) in CatalogueSurfaces())
        {
            if (!SpendsABudget.IsMatch(source))
            {
                continue;
            }

            bool namesOwner = source.Contains(CanonicalName, StringComparison.Ordinal);
            bool namesProbeCanon = source.Contains(ProbeCanon, StringComparison.Ordinal);
            if (!namesOwner && !namesProbeCanon)
            {
                violations.Add(
                    $"{file}: loads the catalogue but names neither {CanonicalName} nor {ProbeCanon}");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "A surface that actually runs a catalogue fetch must say whose budget it is spending: "
                + CanonicalName + " for a UI catalogue wait, or " + ProbeCanon + " for a probe. A surface that names "
                + "neither has an unnamed private timeout, which is how four views came to hold four answers. "
                + "Offenders: " + Offenders(violations));
    }

    [Test]
    public async Task TheRuleStillHasLiveSubjects()
    {
        // Anti-rot. A discovered rule can decay into one that matches nothing,
        // and a rule that matches nothing is indistinguishable from a rule that
        // passes. Pin both halves of the discovery: the four subjects that exist
        // today must still be found, and the rule must still have real spenders
        // to judge. If a catalogue surface is renamed or removed, this fails
        // loudly instead of the scan quietly going green.
        string[] expected =
        [
            "ProviderBrowserViewModel.cs",
            "ProviderConfigViewModel.cs",
            "ProviderModelPickerViewModel.cs",
            "ProviderModelPickerViewModelBase.cs"
        ];

        var surfaces = CatalogueSurfaces();
        var violations = new List<string>();

        foreach (string file in expected.Where(file => !surfaces.ContainsKey(file)))
        {
            violations.Add($"{file}: no longer discovered as a catalogue surface — the rule lost a subject");
        }

        int spenders = surfaces.Count(kv => SpendsABudget.IsMatch(kv.Value));
        if (spenders < 2)
        {
            violations.Add(
                $"expected at least 2 catalogue surfaces that actually spend a budget, found {spenders} — "
                + "the discovery predicate has stopped matching real spenders");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "The subjects are discovered, so they can silently drift out of scope. Today four view-models in "
                + ProjectDir + " load the provider catalogue; if that set changes, this rule must be re-pointed "
                + "deliberately rather than left governing nothing. Offenders: " + Offenders(violations));
    }
}
