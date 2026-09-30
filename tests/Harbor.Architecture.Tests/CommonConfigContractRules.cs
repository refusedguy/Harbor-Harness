// CommonConfigContractRules.cs — the guard for #453.
//
// WHY THIS FILE EXISTS
// --------------------
// #453 was filed as a duplicate: two interfaces modelling the same
// ~/.harbor/config.json read, one in Desktop.Abstractions and one in
// Ui.Framework.Abstractions. Reading both ends says it is NOT a duplicate, and
// that is the finding this file has to hold true:
//
//     ICommonConfigStore        3 members — Load / Save / Update, the WHOLE
//                               CommonConfig, every failure a Result.
//     ICommonConfigReader       1 member  — read only, two fields, absence as a
//                               nullable tuple.
//
// One is a repository you write through; the other is a read-only projection of
// one value out of it. Same subject, different capability — the same shape #574
// found between two `CollapseWhitespace` methods that took text and returned text
// and did opposite things, and which #717 resolved by making them DIFFER IN NAME
// rather than by merging them. Merging is not available here anyway:
// Harbor.Desktop.Abstractions declares a direct ProjectReference to
// Harbor.Ui.Framework (plus six more Ui.Framework.* projects), so the reverse edge
// would be a cycle. The split is load-bearing and both contracts stay.
//
// WHAT #453 FOUND ANYWAY, AND WHY THIS IS NOT ONLY A RENAME
// ----------------------------------------------------------
// The audit's own parenthetical — "one implementation registers twice" — is the
// cheap half. The expensive half is that the narrow contract still returns the
// exact type #729 removed one commit ago:
//
//     Task<(string? ProviderId, string? ModelId)?>
//
// #729 fixed the CONSUMER end of this seam. `SessionFactory.
// ResolveProviderModelFromConfigAsync` now returns `Task<Maybe<ModelRef>>`, and
// ProviderModelAbsenceRules guards its shape plus the two consumer files. The
// PRODUCER end was left alone: the interface still hands the pair over, and the
// half-pair normalisation that fix's own doc calls the only thing holding the
// callers together has simply MOVED to the adapter —
//
//     CommonConfigReaderAdapter:48
//     if (string.IsNullOrEmpty(cfg.DefaultProvider) || string.IsNullOrEmpty(cfg.DefaultModel))
//         return null;
//
// — which is not one of the two files ProviderModelAbsenceRules scans. So the
// rule as landed cannot see the derivation it was written to forbid, and the
// seam still declares four states while the domain has one. This file extends
// the scan to the producer side and pins the carrier.
//
// A tuple of two optional strings, wrapped in another optional, spells FOUR
// states: both, neither, provider-only, model-only. `Maybe<ModelRef>` spells
// two, because `ModelRef` cannot hold half a reference. That is the whole of
// #729's argument, and it applies to the interface verbatim.
//
// Note the states are not even reachable by accident: `CommonConfig.DefaultProvider`
// and `.DefaultModel` are non-nullable `string` with non-empty initialisers
// ("anthropic" / "claude-sonnet-4"), so "half a config" only exists in a
// hand-edited file. The guard does not rely on that — it just means the
// normalisation is guarding a state the writer never produces.
//
// WHY THE NAMES, SPECIFICALLY
// ---------------------------
// "Store" and "Reader" told a new author that one was writable and one was not,
// which is the axis that matters — but not WHAT the reader reads, so the choice
// between them still required opening both. `ICommonConfigModelRefReader` names
// the value, which is what the #717 divergence is for: read the two names side
// by side and the difference is legible without a body. `ICommonConfigStore`
// keeps its name — "store" is the established read/write term, its docs already
// state it reads and writes atomically, and 58 references would churn without
// adding one bit of information. What both get instead is the mutual
// cross-reference, so a reader of either lands on the other and learns why there
// are two.
//
// THE RATIONALE IN THE OLD DOC COMMENT WAS ALSO WRONG
// --------------------------------------------------
// It said Desktop.Abstractions reaches Ui.Framework "via Harbor.Terminal.
// Abstractions", and that Terminal.Abstractions is what closes the cycle. By
// fact the edge is a DIRECT ProjectReference to Harbor.Ui.Framework, with
// Terminal.Abstractions one redundant edge among seven. The cycle is real; the
// path given for it was not. The rewrite names the real edge, because a stated
// reason that is wrong is worse than no reason — the next reader trusts it.
//
// FIVE RULES, FOUR KINDS OF PROOF
// ------------------------------
//   1. NAME (reflection by name, not a type reference): the narrow port IS
//      `ICommonConfigModelRefReader`, and `ICommonConfigReader` is gone. Resolved
//      by string so this file compiles before the rename — a guard for a name
//      that does not exist yet cannot be landed red-first otherwise.
//   2. SHAPE (reflection): its one member returns `Task<Maybe<ModelRef>>`.
//   3. CAPABILITY SPLIT (reflection): the narrow port exposes no write member
//      and the store owns the writes. This is the rule that keeps #453 from
//      being "fixed" by merging, and the rule that catches the opposite
//      regression — the narrow port quietly growing a second copy of the store.
//   4. NO RE-DERIVATION (source): the producer side no longer tests a provider
//      half against a model half. #729's matcher, applied to the files it did
//      not cover.
//   5. SOLE IMPLEMENTER (reflection): one production implementer. A second
//      producer is a second hand-written answer to "is this pair whole?", which
//      is the exact failure #729's doc warns a second producer would introduce.
//
// NON-VACUITY
// -----------
// Rules 3 and 4 are predicates that can pass on a broken matcher or a moved
// file, and rule 3 in particular is a "there is no violation" result that is
// otherwise indistinguishable from "the scan read nothing". So the same
// predicates are driven with synthetic positive and negative controls, and the
// file list is required to resolve.

using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #453: the shared common config is read through two contracts, and
///     they are two capabilities rather than one duplicated contract. The narrow
///     one hands out ONE atomic reference with ONE absence. See the file header
///     for the evidence that the split is load-bearing and for the
///     <c>(string?, string?)</c> carrier #729 removed from the receiving end and
///     this file removes from the producing end.
/// </summary>
public sealed class CommonConfigContractRules
{
    /// <summary>The whole-config, read/write repository. Name unchanged by #453.</summary>
    private const string StoreName = "ICommonConfigStore";

    /// <summary>
    ///     The read-only projection of one value out of it, after the rename. The
    ///     post-fix name on purpose: <see cref="NarrowSeamName" /> is what this
    ///     file asserts, and asserting it by string is the only way the guard can
    ///     be committed red before the rename lands.
    /// </summary>
    private const string NarrowSeamName = "ICommonConfigModelRefReader";

    /// <summary>The pre-rename name, which #453 retires: "Reader" said what it could not do, not what it read.</summary>
    private const string RetiredSeamName = "ICommonConfigReader";

    /// <summary>The narrow port's one member, after the rename.</summary>
    private const string NarrowSeamMethod = "ReadModelRefAsync";

    /// <summary>
    ///     The whole seam — the port, the one adapter that implements it, and the
    ///     registration that wires it. Rule 4 walks all three because the
    ///     derivation can sit on either side of the port: the port's own doc
    ///     comment, the producer, or the composition root's fallback.
    /// </summary>
    private static readonly string[] SeamFiles =
    [
        "src/Harbor.Ui.Framework.Abstractions/Configuration/ICommonConfigModelRefReader.cs",
        "apps/Harbor.App.Avalonia/Services/CommonConfigReaderAdapter.cs",
        "apps/Harbor.App.Avalonia/Hosting/ConfigRegistration.cs",
    ];

    /// <summary>
    ///     #729's matcher, verbatim in substance: an emptiness test on a
    ///     provider-or-model-named value, joined by <c>&amp;&amp;</c>/<c>||</c> to
    ///     the same kind of test on another one. Requiring BOTH operands to be
    ///     provider/model-named is what keeps this from firing on every unrelated
    ///     two-optional-string test; requiring the OPERATOR is what keeps it from
    ///     firing on a lone per-field guard, which is a different question.
    /// </summary>
    private static readonly Regex HalfPairProbe = new(
        """
        string\.IsNullOr(?:Empty|WhiteSpace)\s*\(\s*(?:\w*[Pp]rovider\w*|\w*[Mm]odel\w*)\s*\)\s*(?:&&|\|\|)\s*(?:!\s*)?string\.IsNullOr(?:Empty|WhiteSpace)\s*\(\s*(?:\w*[Pp]rovider\w*|\w*[Mm]odel\w*)\s*\)
        """,
        RegexOptions.Compiled);

    /// <summary>
    ///     A member whose name opens with one of these verbs is a write. Matched
    ///     against the name with a trailing <c>Async</c> removed, and anchored at
    ///     the start, so <c>ReadModelRefAsync</c> and <c>LoadAsync</c> are reads
    ///     and <c>ResetAsync</c> is not caught by the <c>Set</c> entry.
    /// </summary>
    private static readonly Regex WriteMember = new(
        """
        ^(?:Save|Update|Write|Set|Delete|Put|Post|Remove|Add|Insert|Erase|Purge|Clear|Mutate|Patch|Apply|Upsert|Commit|Edit|Persist|Configure|Store|Flush|Replace)
        """,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A file, a 1-based line number, and the offending line.</summary>
    private sealed record ProbeSite(string RelativePath, int Line, string Text);

    // ── Rule 1: the names ────────────────────────────────────────────────────

    [Test]
    public async Task NarrowSeam_IsNamedForTheValue_And_TheOldNameIsGone()
    {
        IReadOnlyList<string> narrow = FindDeclarations(NarrowSeamName);
        IReadOnlyList<string> retired = FindDeclarations(RetiredSeamName);

        await Assert.That(narrow.Count).IsEqualTo(1).Because(
            "there is one narrow read-only port over the shared common config, and after #453 it is "
            + "named for the value it hands out: " + NarrowSeamName + ". \"ICommonConfigReader\" said "
            + "only what the member cannot do, so choosing between it and " + StoreName + " still "
            + "required opening both — the reader turns out to read one ModelRef, not the config. "
            + "Found: " + Describe(narrow) + ". If it moved, re-point this at the new home; a lookup "
            + "that finds nothing is how a guard reports green forever.");

        await Assert.That(retired.Count).IsEqualTo(0).Because(
            RetiredSeamName + " is the pre-#453 name and must not come back: it is the name that "
            + "hides the difference. Found: " + Describe(retired) + ". This is the #717 rule — two "
            + "different things under one name are made to differ in the name, and the name is not "
            + "re-added afterwards.");
    }

    // ── Rule 2: the carrier ──────────────────────────────────────────────────

    [Test]
    public async Task NarrowSeam_ReadsOneModelRef_WithOneAbsence()
    {
        Type? port = FindType(NarrowSeamName);

        // Non-vacuity. A null here would make every line below throw and fail for
        // the wrong reason, telling the next reader nothing about which check broke.
        await Assert.That(port).IsNotNull().Because(
            NarrowSeamName + " must be a public interface in a loaded production assembly. Rule 1 "
            + "already asserts that; this is the shape half, and it needs a non-null type to ask.");

        MethodInfo? method = port!.GetMethod(
            NarrowSeamMethod,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null,
            [typeof(CancellationToken)],
            modifiers: null);

        await Assert.That(method).IsNotNull().Because(
            "the narrow port declares exactly one member, " + NarrowSeamMethod
            + ", and it must be a public instance method taking a CancellationToken. It was "
            + "TryReadProviderModelAsync before #453 — the \"Try\" was a second, redundant way of "
            + "saying \"Maybe\": a Maybe-returning method is asked, not tried.");

        await Assert.That(method!.ReturnType).IsEqualTo(typeof(Task<Maybe<ModelRef>>)).Because(
            "the config names one provider/model or nothing; it never names half. "
            + "Task<(string? ProviderId, string? ModelId)?> is a pair of optional strings inside "
            + "another optional, so it spells FOUR states — both, neither, provider-only, "
            + "model-only — and the type can tell none of them apart. #729 already removed that "
            + "carrier from the receiving end of this very seam and ProviderModelAbsenceRules guards "
            + "it there; Maybe<ModelRef> has one absence, and ModelRef is the only carrier in the "
            + "repo whose halves are non-null by construction and whose provider half is normalized "
            + "(#678/#690). Not Result: \"nothing configured yet\" is the normal pre-onboarding "
            + "state of every install, not a failure. See issues #453 and #598.");
    }

    // ── Rule 3: the split is real ────────────────────────────────────────────

    [Test]
    public async Task NarrowSeam_ExposesNoWrite_AndTheStoreOwnsTheWrites()
    {
        Type? narrow = FindType(NarrowSeamName);
        Type? store = FindType(StoreName);

        await Assert.That(narrow).IsNotNull().Because(NarrowSeamName + " must exist; see rule 1.");
        await Assert.That(store).IsNotNull().Because(
            StoreName + " must exist. It is the whole-config repository: the narrow port is a "
            + "read-only projection of it, and #453 keeps both, so neither may be deleted to make "
            + "the other look tidy.");

        IReadOnlyList<string> narrowWrites = FindWriteMembers(narrow!);
        IReadOnlyList<string> storeWrites = FindWriteMembers(store!);

        await Assert.That(narrowWrites).IsEmpty().Because(
            "the narrow port is the READ half of the split. A write member on it is a second "
            + "answer to \"how does the shared config change?\", next to " + StoreName + " — the "
            + "duplicate #453 was filed about, entering from the other direction. Found: "
            + string.Join(", ", narrowWrites) + ".");

        await Assert.That(storeWrites.Count).IsGreaterThan(0).Because(
            "and the split has to point somewhere: the writes live on " + StoreName + ". If BOTH "
            + "contracts were read-only the pair would be one contract wearing two names, which is "
            + "the duplicate, and the fix would be to merge them. The reason they are not merged is "
            + "a cycle — Harbor.Desktop.Abstractions declares a direct ProjectReference to "
            + "Harbor.Ui.Framework, so the reverse edge cannot be added.");
    }

    // ── Rule 4: the producer does not re-derive it ───────────────────────────

    [Test]
    public async Task Seam_DoesNotReDeriveHalfPairUsability()
    {
        string root = RequireRepoRoot();
        List<ProbeSite> probes = [.. SeamFiles.SelectMany(f => FindProbes(root, f))];

        await Assert.That(probes.Count).IsEqualTo(0).Because(
            "nobody on this seam may decide \"is this pair whole?\" by testing a provider half "
            + "against a model half. #729 removed that rule from the two consumer files and left the "
            + "derivation itself, which had moved to CommonConfigReaderAdapter:48 — a file its "
            + "scan's file list does not contain, so the guard as landed could not see it. A "
            + "hand-written normalisation is the wrong place for the answer because a second "
            + "producer need not know it exists: the value type states it, so the question cannot "
            + "be re-derived, forgotten, or answered the other way. Found: " + Describe(probes)
            + ". See issues #453 and #598.");
    }

    // ── Rule 5: one producer ─────────────────────────────────────────────────

    [Test]
    public async Task NarrowSeam_HasExactlyOneProductionImplementer()
    {
        Type? port = FindType(NarrowSeamName);
        await Assert.That(port).IsNotNull().Because(NarrowSeamName + " must exist; see rule 1.");

        IReadOnlyList<string> implementers = FindImplementers(port!);

        await Assert.That(implementers.Count).IsEqualTo(1).Because(
            "exactly one production type implements the narrow port — CommonConfigReaderAdapter, "
            + "which forwards to " + StoreName + ". A second implementer is a second hand-written "
            + "answer to \"is this reference whole?\", which is the hazard #729's doc names: the "
            + "consumers agreed only because one producer normalised the halves, and nothing "
            + "stopped a second one from forgetting. Test doubles are excluded — a fake proves the "
            + "port is injectable and reads nobody's config. Found: "
            + (implementers.Count == 0 ? "(none — the seam is unwired)" : string.Join(", ", implementers))
            + ".");
    }

    // ── Non-vacuity ──────────────────────────────────────────────────────────

    [Test]
    public async Task WriteVerbRule_FiresOnAWrite_AndStaysSilentOnARead()
    {
        IReadOnlyList<string> writes = FindWriteMembersIn(
        [
            "ReadModelRefAsync", "LoadAsync", "TryReadAsync", "GetAsync", "FetchAsync",
            "TryReadProviderModelAsync",
        ]);

        await Assert.That(writes).IsEmpty().Because(
            "every name above is a read, and reads are what the narrow port is for. If one is "
            + "reported as a write the capability rule is over-broad and would fail on the correct "
            + "code. Got: " + string.Join(", ", writes) + ".");

        IReadOnlyList<string> caught = FindWriteMembersIn(["SaveAsync", "UpdateAsync", "LoadAsync"]);

        await Assert.That(caught.Count).IsEqualTo(2).Because(
            "the matcher must actually fire, or rule 3 is a \"there is no violation\" result that "
            + "cannot be told apart from a scan that read nothing. SaveAsync and UpdateAsync are "
            + StoreName + "'s two writes and must be reported; LoadAsync must not. Got: "
            + string.Join(", ", caught) + ".");

        await Assert.That(FindWriteMembersIn(["ResetAsync", "SettingAsync", "AddressOfAsync"])).IsEmpty().Because(
            "the verb is matched at the START of the name, so a read that merely contains a verb as "
            + "a substring (Reset, Setting, AddressOf) is not a write.");
    }

    [Test]
    public async Task SeamScanner_ReachesEverySeamFile()
    {
        string root = RequireRepoRoot();
        var missing = new List<string>();

        foreach (string file in SeamFiles)
        {
            if (!File.Exists(Path.Combine(root, file)))
            {
                missing.Add(file);
            }
        }

        await Assert.That(missing).IsEmpty().Because(
            "rule 4 is rooted at named files; if one was renamed or moved it silently polices "
            + "nothing and reports green. Point them at the new homes: " + string.Join(", ", missing)
            + ". This is how #729's own guard ended up not seeing CommonConfigReaderAdapter:48 — the "
            + "file list was written from the consumers, and the derivation had already moved to the "
            + "producer.");
    }

    [Test]
    public async Task HalfPairMatcher_StillSeesTheRealSpelling()
    {
        // The line this file exists for, in both operand orders, since the
        // adapter and a future producer could spell it either way.
        await Assert.That(Scan(["if (string.IsNullOrEmpty(cfg.DefaultProvider) || string.IsNullOrEmpty(cfg.DefaultModel))"]).Count)
            .IsGreaterThan(0)
            .Because("this is CommonConfigReaderAdapter:48 as it stands — the matcher must see it");
        await Assert.That(Scan(["if (!string.IsNullOrEmpty(modelId) && !string.IsNullOrEmpty(providerId))"]).Count)
            .IsGreaterThan(0)
            .Because("operand order is an implementation detail, not a distinction the rule draws");

        // And the near-misses that are somebody else's problem.
        await Assert.That(Scan(["if (configured is { } fromConfig)"]).Count).IsEqualTo(0)
            .Because("a test on the whole value is the fix, not a violation");
        await Assert.That(Scan(["if (string.IsNullOrEmpty(providerId)) return;"]).Count).IsEqualTo(0)
            .Because("a lone per-field guard is a different question and stays legal; the rule needs the operator");
        await Assert.That(Scan(["if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(title))"]).Count).IsEqualTo(0)
            .Because("two unrelated optional strings are not a provider/model pair");
        await Assert.That(Scan(["// if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(modelId))"]).Count)
            .IsEqualTo(0)
            .Because("comment lines are prose about the old code, not a live probe — and the "
                     + "post-fix docs on this seam deliberately quote the old spelling");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string RequireRepoRoot()
        => RepoPaths.RepoRoot ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");

    private static string Describe(IReadOnlyList<string> names)
        => names.Count == 0 ? "(none)" : string.Join(", ", names);

    private static string Describe(IReadOnlyList<ProbeSite> sites)
        => sites.Count == 0 ? "(none)" : string.Join(", ", sites.Select(s => s.RelativePath + ":" + s.Line));

    /// <summary>Every public interface named <paramref name="name" />, as assembly-qualified names.</summary>
    private static IReadOnlyList<string> FindDeclarations(string name)
    {
        var found = new List<string>();
        foreach (Type type in LoadableTypes(ProductionAssemblies()))
        {
            if (type.IsInterface && type.IsPublic && string.Equals(type.Name, name, StringComparison.Ordinal))
            {
                found.Add(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>A production type by simple name, or <c>null</c>.</summary>
    private static Type? FindType(string name)
    {
        foreach (Assembly assembly in ProductionAssemblies())
        {
            foreach (Type type in LoadableTypes([assembly]))
            {
                if (type.IsInterface && type.IsPublic && string.Equals(type.Name, name, StringComparison.Ordinal))
                {
                    return type;
                }
            }
        }

        return null;
    }

    /// <summary>Concrete production types implementing <paramref name="port" />, sorted for stable messages.</summary>
    private static IReadOnlyList<string> FindImplementers(Type port)
    {
        var found = new List<string>();
        foreach (Type type in LoadableTypes(ProductionAssemblies()))
        {
            if (type == port || type.IsInterface || type.IsAbstract || !type.IsClass || !port.IsAssignableFrom(type))
            {
                continue;
            }

            found.Add(type.FullName ?? type.Name);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static IReadOnlyList<string> FindWriteMembers(Type contract)
        => FindWriteMembersIn([.. contract.GetMethods().Select(m => m.Name).Distinct(StringComparer.Ordinal)]);

    /// <summary>
    ///     The write-verb predicate over bare member names, separated out so the
    ///     non-vacuity controls can drive the exact matcher rule 3 uses instead of
    ///     a second, near-identical one that could pass while the real one is
    ///     broken.
    /// </summary>
    private static IReadOnlyList<string> FindWriteMembersIn(IEnumerable<string> memberNames)
    {
        var found = new List<string>();
        foreach (string name in memberNames.Distinct(StringComparer.Ordinal))
        {
            string stem = name.EndsWith("Async", StringComparison.Ordinal) ? name[..^"Async".Length] : name;
            if (WriteMember.IsMatch(stem))
            {
                found.Add(name);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static IEnumerable<Assembly> ProductionAssemblies()
    {
        Assembly self = typeof(CommonConfigContractRules).Assembly;
        return ArchitectureTestHelpers.LoadHarborAssemblies()
            .Values
            .Where(a => !ReferenceEquals(a, self) && !IsTestAssembly(a));
    }

    private static bool IsTestAssembly(Assembly assembly)
    {
        string name = assembly.GetName().Name ?? string.Empty;
        return name.EndsWith("Tests", StringComparison.Ordinal)
               || name.EndsWith(".TestKit", StringComparison.Ordinal)
               || string.Equals(name, "Harbor.TestKit", StringComparison.Ordinal)
               || name.EndsWith(".Benchmarks", StringComparison.Ordinal)
               || name.StartsWith("testhost", StringComparison.Ordinal)
               || name.StartsWith("Microsoft.Testing", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> LoadableTypes(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.OfType<Type>()];
            }
            catch (Exception)
            {
                continue;
            }

            foreach (Type type in types)
            {
                yield return type;
            }
        }
    }

    private static List<ProbeSite> FindProbes(string root, string relativePath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Combine(root, relativePath));
        }
        catch (IOException)
        {
            return [];
        }

        return
        [
            .. Scan(lines)
                .Select(index => new ProbeSite(relativePath, index + 1, lines[index].Trim()))
        ];
    }

    /// <summary>
    ///     0-based line numbers holding a half-pair probe. Comment-only lines are
    ///     skipped, with the same bound as #729's rule: a probe split across a
    ///     line break is missed, which is why rule 2 (reflection) carries the load
    ///     and this rule only narrows it.
    /// </summary>
    private static IReadOnlyList<int> Scan(IReadOnlyList<string> lines)
    {
        var hits = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (IsCommentLine(lines[i]))
            {
                continue;
            }

            if (HalfPairProbe.IsMatch(lines[i]))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    private static bool IsCommentLine(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
               || trimmed.StartsWith("/*", StringComparison.Ordinal)
               || trimmed.StartsWith('*');
    }
}
