// ProviderPayloadSerializationRules.cs — issue #475, "[GoF-A8] Provider
// payload builders: untyped Dictionary/anonymous types + reflection".
//
// THE RULE
// --------
// A file in a provider project (`src/Harbor.Providers.*`) may not build a wire
// payload out of untyped carriers and hand it to `JsonSerializer`.
//
// Two named rules, because the issue names two distinct things and they fail
// for two different reasons:
//
//   PROVIDER-PAYLOAD-MUST-NOT-BE-SERIALIZED-BY-REFLECTION
//       A `JsonSerializer.Serialize*` write. Serializing through the static
//       generic entry point requires a `JsonTypeInfo` for the RUNTIME type of
//       every value, which is reflection. Under a trimmed / NativeAOT publish
//       reflection-based serialization is DISABLED BY DEFAULT — the .NET
//       runtime turns `JsonSerializer.IsReflectionEnabledByDefault` off
//       whenever `PublishTrimmed` is on — so an options object with no
//       source-generated `TypeInfoResolver` throws `InvalidOperationException`
//       at the first request instead of writing bytes. Source:
//       dotnet/docs, standard/serialization/system-text-json/source-generation.
//
//   PROVIDER-PAYLOAD-MUST-NOT-CARRY-UNTYPED-PAYLOADS
//       `Dictionary<string, object?>` / `List<object>` / `object[]` as a
//       declared type. Even with a serializer that works, this shape moves the
//       wire contract into runtime values: nothing type-checks a key name, and
//       the anti-corruption layer that maps `LlmMessage` to the provider's
//       dialect disappears into an anonymous type that no test can name.
//
// WHY THE FIRST RULE IS NOT A PERF RULE
// -------------------------------------
// The obvious objection is frequency: these builders run ONCE PER LLM REQUEST,
// not per token, so "reflection on a hot path" is the wrong argument and this
// file does not make it. AOT-safety is not frequency-dependent — a throw that
// happens once per turn is still a throw.
//
// THE HONEST PART: THIS IS NOT BROKEN TODAY, AND SAYING OTHERWISE WOULD BE WRONG
// ---------------------------------------------------------------------------
// `apps/Harbor.App.Cli` has the NativeAOT switch, but the release and CI
// publish steps do not use it: `HarborWithAllProviders` DEFAULTS TO `true`
// (Harbor.App.Cli.csproj), which leaves the `HARBOR_MINIMAL` MSBuild property
// unset, which is the only thing that turns `HarborWithAot` on. So the
// published artifact is self-contained JIT, untrimmed, reflection enabled, and
// every one of these three providers works in the shipped binary.
//
// The break is conditional on the publish that #413 is going to switch on, and
// on the second half of the same switch that is already written down: the CLI
// csproj demotes IL2026 to a warning (`WarningsNotAsErrors`), and no provider
// project sets `IsAotCompatible`, so the analyzer never even looks at these
// three files. The issue's "so reflection + IL2026" is therefore true about
// the mechanism and unobservable as stated — no build in this repo emits
// IL2026 for these lines today.
//
// That makes this a LANDING-GROUND guard rather than a bug report, and it is
// worth being precise about which it is: the payload builders are the last
// reflection-serialized path in the provider layer, and #413's gate turns the
// question from hypothetical into immediate.
//
// WHY PROVIDERS AND NOT ALL OF src/
// --------------------------------
// Because this is the only place the convention is already written down. The
// CLI csproj says it ("JSON serialization hot paths are migrated to
// JsonSerializerContext source generation (§PERF-002/§PERF-005) so AOT
// analysis passes cleanly"), `OpenAiImageContent.ToDataUrl` says it in prose
// ("a `byte[]` is never handed to the reflection-based
// `JsonSerializer.Serialize(Dictionary<string, object?>)` payload path"), and
// `OpenAiCompatibleLlmClient` already demonstrates the alternative by writing
// straight to a `Utf8JsonWriter`. Three statements of one convention, none of
// them a rule.
//
// Widening the scan to every project in `src/` would fire on the
// `JsonSerializerContext` users that are already correct, and a ban that fires
// on correct code gets suppressed wholesale — which is the failure mode
// `ReflectionConventionRule` documents at length and the reason it deliberately
// did NOT ban string-based member access. This scope is the perimeter the issue
// actually describes, and it is small enough to read in full.
//
// NO EXEMPTION TABLE YET, DELIBERATELY
// ------------------------------------
// The baseline exists so a violation nobody has fixed yet can be tolerated
// WITH A REASON (`ExemptionReason`). It is empty here, and the rule lands RED
// against three real files rather than green-with-three-grandf-rows. A baseline
// row says "this may stay"; the honest state of this debt is "this has not been
// fixed", and the difference between those two is the whole point of landing a
// guard before the fix. `Baseline_Is_Empty_Because_The_Rule_Is_Armed` checks the
// emptiness is a fact rather than an omission.
//
// NON-VACUITY
// -----------
// A guard scoped to a path nobody occupies is green forever — the NetArchTest
// trap `PresentationCapabilityRules` documents ("a name that matches nothing").
// Closed here in four places:
//   * `NonVacuity_Discovery_Sees_The_Provider_Trees` — the scope is non-empty
//     and contains the files the rule is about.
//   * `NonVacuity_The_Matchers_Fire_On_Planted_Offenders_Only` — synthetic
//     source, ten planted snippets per rule; the correct neighbouring shapes must
//     all stay silent. This one earned its place on its first CI run: the carrier
//     matcher closed each generic branch twice and matched nothing, so the rule
//     was green on the very code it exists to be red about, and the control said
//     "expected 3, found 1" instead of the rule saying nothing.
//   * `NonVacuity_Comments_Are_Stripped_Before_Matching` — the prose form is
//     explicitly shown to be silent, because `OpenAiImageContent.cs` contains
//     `JsonSerializer.Serialize(Dictionary<string, object?>)` inside a `///` and
//     a rule that flags its own documentation is a rule that gets deleted.
//   * `Baseline_Is_Empty_Because_The_Rule_Is_Armed` + `ExemptionReason` wiring
//     — the escape hatch exists and is disciplined from the first row.
//
// SCOPE EXCLUSIONS ARE THE SHARED ONES
// ------------------------------------
// `SourceScan.EnumerateCsFiles` already drops `obj/`, `bin/`, `tests/`,
// `contrib/` and `.worktrees/`. `NonVacuity_Scope_Excludes_TestsAndContrib_On
// _Purpose` proves that exclusion is load-bearing for THIS rule rather than
// inherited and untested.
//
// MECHANISM (#1086, step 2)
// -------------------------
// This rule is the TABULAR etalon for the ScanRule engine: two sub-rules
// sharing one scope, each its own row of (SubId, Pattern, Instead) plus its
// own planted controls. Enumeration, stripping, matching, baseline and control
// verdicts are ScanRunner's; this file keeps the issue prose and the test names.

using System.Text.RegularExpressions;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #475: provider wire payloads may not be built as untyped carriers and
///     written through the reflection-based <c>JsonSerializer</c>.
/// </summary>
public sealed class ProviderPayloadSerializationRules
{
    /// <summary>Project-directory prefix that puts a file in this rule's scope.</summary>
    internal const string ProviderPrefix = "src/Harbor.Providers.";

    private const string NoReflectionWrite = "PROVIDER-PAYLOAD-MUST-NOT-BE-SERIALIZED-BY-REFLECTION";
    private const string NoUntypedCarrier = "PROVIDER-PAYLOAD-MUST-NOT-CARRY-UNTYPED-PAYLOADS";

    /// <summary>
    ///     The <c>JsonSerializer.Serialize*</c> write family, as one alternation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Serialize\b</c> alone would miss <c>SerializeToUtf8Bytes</c> —
    ///         there is no word boundary between <c>Serialize</c> and
    ///         <c>ToUtf8Bytes</c> — and the suffix is spelled out here instead so
    ///         the rule cannot be satisfied by switching from
    ///         <c>SerializeToUtf8Bytes</c> to <c>SerializeToStream</c>.
    ///     </para>
    ///     <para>
    ///         <c>Deserialize</c> is deliberately NOT matched. Reading is the
    ///         other half of the job and the SSE/chunk parsers are hand-written
    ///         over <c>Utf8JsonReader</c> (§PERF-005) precisely because
    ///         deserializing a provider's reply into an anonymous type has the
    ///         same runtime-type problem. A rule that cannot tell a write from a
    ///         read would report the reads that are already correct.
    ///     </para>
    ///     <para>
    ///         Whitespace is tolerated around the dot so
    ///         <c>JsonSerializer . Serialize(</c> is caught too.
    ///     </para>
    /// </remarks>
    private static readonly Regex ReflectionWrite = new(
        @"\bJsonSerializer\s*\.\s*Serialize"
        + @"(ToUtf8Bytes|ToStream|ToDocument|ToElement|ToNode)?\s*[\(\.]",
        RegexOptions.Compiled);

    /// <summary>
    ///     The untyped carriers, as one alternation: a dictionary whose value type
    ///     is <c>object?</c>, and the two array-shaped ones.
    /// </summary>
    /// <remarks>
    ///     One closing angle per branch. The first version of this pattern closed
    ///     each generic branch twice (<c>object?&gt;\s*&gt;</c>), which matches
    ///     nothing at all — and the planted-offender control caught it on its first
    ///     CI run, reporting three expected hits and one found. That is the control
    ///     doing the job it exists for: the rule was live and silently matching only
    ///     its <c>object[]</c> branch, i.e. green on the real code it was supposed
    ///     to be red about.
    /// </remarks>
    private static readonly Regex UntypedCarrier = new(
        @"\bDictionary\s*<\s*string\s*,\s*object\s*\??>"
        + @"|\bList\s*<\s*object\s*>"
        + @"|\bobject\s*\[\s*\]",
        RegexOptions.Compiled);

    /// <summary>The write rule as data: one banned shape, ten planted controls.</summary>
    private static readonly ScanRule ReflectionRule = new()
    {
        Id = "ProviderPayloadSerialization.ReflectionWrite",
        Trees = ["src"],
        InScope = static p => p.StartsWith(ProviderPrefix, StringComparison.Ordinal),
        Forbidden =
        [
            new ScanForbidden(
                NoReflectionWrite,
                ReflectionWrite,
                "write with a Utf8JsonWriter (the pattern OpenAiCompatibleLlmClient already uses) "
                + "or through a source-generated context."),
        ],
        Controls =
        [
            new ScanControl("src/Harbor.Providers.X/A.cs", "var b = JsonSerializer.SerializeToUtf8Bytes(payload, o);", NoReflectionWrite),
            new ScanControl("src/Harbor.Providers.X/B.cs", "JsonSerializer.Serialize(value, options);", NoReflectionWrite),
            new ScanControl("src/Harbor.Providers.X/C.cs", "JsonSerializer . SerializeToStream(v, s, o);", NoReflectionWrite),
            new ScanControl("src/Harbor.Providers.X/D.cs", "JsonSerializer.SerializeToElement(v, o).ToString();", NoReflectionWrite),
            new ScanControl("src/Harbor.Providers.X/E.cs", "var x = JsonSerializer.Deserialize<T>(bytes, o);", null),
            new ScanControl("src/Harbor.Providers.X/F.cs", "internal sealed partial class Ctx : JsonSerializerContext;", null),
            new ScanControl("src/Harbor.Providers.X/G.cs", "writer.WriteString(\"model\", request.Model);", null),
            new ScanControl("src/Harbor.Providers.X/H.cs",
                "/// The base64 never reaches JsonSerializer.Serialize(Dictionary<string, object?>).", null),
            new ScanControl("src/Harbor.Providers.X/I.cs",
                "// JsonSerializer.SerializeToUtf8Bytes was the old shape.", null),
            new ScanControl("src/Harbor.Providers.X/J.cs", "o.TypeInfoResolver = OpenAiWireContext.Default;", null),
        ],
        MinHits = 1,
        MustContain =
        [
            "src/Harbor.Providers.Anthropic/AnthropicRequestBuilder.cs",
            "src/Harbor.Providers.OpenAI/OpenAiRequestBuilder.cs",
            "src/Harbor.Providers.Ollama/OllamaLlmClient.cs",
        ],
    };

    /// <summary>The carrier rule as data: one banned shape, seven planted controls.</summary>
    private static readonly ScanRule CarrierRule = new()
    {
        Id = "ProviderPayloadSerialization.UntypedCarrier",
        Trees = ["src"],
        InScope = static p => p.StartsWith(ProviderPrefix, StringComparison.Ordinal),
        Forbidden =
        [
            new ScanForbidden(
                NoUntypedCarrier,
                UntypedCarrier,
                "the anti-corruption layer from LlmMessage to a provider dialect is named code, "
                + "not a bag of runtime values."),
        ],
        Controls =
        [
            new ScanControl("src/Harbor.Providers.X/A.cs", "var payload = new Dictionary<string, object?>(12);", NoUntypedCarrier),
            new ScanControl("src/Harbor.Providers.X/B.cs", "public static List<object> BuildMessages(LlmRequest r)", NoUntypedCarrier),
            new ScanControl("src/Harbor.Providers.X/C.cs", "object[] converted = new object[blocks.Count];", NoUntypedCarrier),
            new ScanControl("src/Harbor.Providers.X/G.cs", "Dictionary<string, object> map = new();", NoUntypedCarrier),
            new ScanControl("src/Harbor.Providers.X/D.cs", "var messages = new List<LlmMessage>();", null),
            new ScanControl("src/Harbor.Providers.X/E.cs", "Dictionary<string, string> headers;", null),
            new ScanControl("src/Harbor.Providers.X/F.cs",
                "/// was a Dictionary<string, object?> before #475", null),
        ],
        MinHits = 1,
    };

    // =====================================================================
    // 1. The rules.
    // =====================================================================

    /// <summary>
    ///     A provider must not write its wire payload through
    ///     <c>JsonSerializer.Serialize*</c>. Write with a
    ///     <see cref="System.Text.Json.Utf8JsonWriter" /> (the pattern
    ///     <c>OpenAiCompatibleLlmClient</c> already uses) or through a
    ///     source-generated context.
    /// </summary>
    [Test]
    public async Task Provider_MustNot_SerializePayloadsByReflection()
    {
        List<string> failures = ScanRunner.Evaluate(ReflectionRule);
        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    /// <summary>
    ///     A provider must not declare its payload as
    ///     <c>Dictionary&lt;string, object?&gt;</c>, <c>List&lt;object&gt;</c> or
    ///     <c>object[]</c>. The anti-corruption layer from <c>LlmMessage</c> to a
    ///     provider dialect is named code, not a bag of runtime values.
    /// </summary>
    [Test]
    public async Task Provider_MustNot_CarryUntypedPayloads()
    {
        List<string> failures = ScanRunner.Evaluate(CarrierRule);
        await Assert.That(failures).IsEmpty().Because(string.Join("\n", failures));
    }

    // =====================================================================
    // 2. Non-vacuity. A rule nobody can fail is a comment.
    // =====================================================================

    /// <summary>
    ///     The scope must contain real files, and specifically the three the issue
    ///     names. A scope filter that matches nothing makes both rules green
    ///     forever, which is the NetArchTest trap in a new coat.
    /// </summary>
    [Test]
    public async Task NonVacuity_Discovery_Sees_The_Provider_Trees()
    {
        List<string> failures = ScanRunner.CheckDiscovery(ReflectionRule);

        await Assert.That(failures).IsEmpty()
            .Because("a provider scope that discovers no files makes every rule here green for no "
                   + "reason. " + string.Join("; ", failures));
    }

    /// <summary>
    ///     Sensitivity control over synthetic source: the planted offenders must all be
    ///     hit and the correct neighbouring shapes must all stay silent, so the rule
    ///     cannot later be "fixed" by widening the matcher until everything is a
    ///     violation.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Matchers_Fire_On_Planted_Offenders_Only()
    {
        List<string> failures =
        [
            .. ScanRunner.CheckControls(ReflectionRule),
            .. ScanRunner.CheckControls(CarrierRule),
        ];

        await Assert.That(failures).IsEmpty()
            .Because("planted offenders must be caught and the correct neighbouring shapes must stay "
                   + "silent — including the comment forms, or the rule reports its own documentation. "
                   + string.Join("; ", failures));
    }

    /// <summary>
    ///     <see cref="SourceScan.StripComments" /> is load-bearing for this rule,
    ///     not hygiene: <c>src/Harbor.Providers.Shared/OpenAiImageContent.cs</c>
    ///     quotes <c>JsonSerializer.Serialize(Dictionary&lt;string, object?&gt;)</c>
    ///     inside a <c>///</c> remark explaining why that path is bad. A rule that
    ///     flagged it would fail on the sentence written to agree with it.
    /// </summary>
    [Test]
    public async Task NonVacuity_Comments_Are_Stripped_Before_Matching()
    {
        const string Prose = "/// JsonSerializer.Serialize(Dictionary<string, object?>) was the old shape.";

        await Assert.That(ReflectionWrite.IsMatch(Prose)).IsTrue()
            .Because("the raw prose does contain the forbidden text, so the test below is "
                   + "proving the stripper and not that the matcher is blind");

        await Assert.That(ScanRunner.EvaluateOver(
                [("src/Harbor.Providers.X/A.cs", Prose)], ReflectionRule))
            .IsEmpty().Because("comment stripping has to run before matching");

        await Assert.That(ScanRunner.EvaluateOver(
                [("src/Harbor.Providers.X/A.cs", Prose)], CarrierRule))
            .IsEmpty().Because("the same prose names the untyped carrier");
    }

    /// <summary>
    ///     The <c>tests/</c> and <c>contrib/</c> exclusions are deliberate here:
    ///     this test file itself names every construct the rules ban, and
    ///     <c>contrib/</c> is not compiled by CI at all.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scope_Excludes_TestsAndContrib_On_Purpose()
    {
        IReadOnlyList<string> files = ScanRunner.ScopeFiles(ReflectionRule);

        await Assert.That(files.Any(p => p.StartsWith("tests/", StringComparison.Ordinal))).IsFalse()
            .Because("tests/ is where the planted offenders above live; a scope that "
                   + "included it could never pass");

        await Assert.That(files.Any(p => p.StartsWith("contrib/", StringComparison.Ordinal))).IsFalse()
            .Because("contrib/ is unmaintained and out of CI (AGENTS.md); a violation there "
                   + "describes code that cannot break the build");

        await Assert.That(SourceScan.IsBuildOutput("src/Harbor.Providers.X/obj/Debug/net10.0/Copy.cs"))
            .IsTrue().Because("a stale obj/ copy would be counted as a second occurrence of "
                            + "every finding");
    }

    // =====================================================================
    // 3. The baseline table.
    // =====================================================================

    /// <summary>
    ///     The baseline is empty, and that is a claim being checked rather than a
    ///     table nobody filled in. When the first row appears, this test is the
    ///     thing that makes it a decision instead of a shrug.
    /// </summary>
    [Test]
    public async Task Baseline_Is_Empty_Because_The_Rule_Is_Armed()
    {
        await Assert.That(ReflectionRule.Baseline.Length + CarrierRule.Baseline.Length).IsEqualTo(0)
            .Because("both rules are fully armed — there is no tolerated reflection write "
                   + "in the provider layer. A row here means a provider deliberately "
                   + "chose reflection over Utf8JsonWriter; the rule is red until then.");
    }

    /// <summary>
    ///     Every row states why it is tolerated, in the row itself. Vacuous while
    ///     the table is empty, and deliberately so: it is wired from the first row
    ///     so the first row cannot skip the argument.
    /// </summary>
    [Test]
    public async Task Baseline_Rows_AllHaveReasons()
    {
        List<string> failures =
        [
            .. ScanRunner.CheckReasons(ReflectionRule),
            .. ScanRunner.CheckReasons(CarrierRule),
        ];

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
        List<(string DisplayPath, string Source)> sources =
            ScanRunner.ReadSources(ScanRunner.ScopeFiles(ReflectionRule));

        List<string> stale =
        [
            .. ScanRunner.StaleBaselineKeys(ReflectionRule, sources),
            .. ScanRunner.StaleBaselineKeys(CarrierRule, sources),
        ];

        await Assert.That(stale).IsEmpty()
            .Because("a baseline row with no violation behind it is a permission for a "
                   + "problem that no longer exists: " + string.Join(", ", stale));
    }
}
