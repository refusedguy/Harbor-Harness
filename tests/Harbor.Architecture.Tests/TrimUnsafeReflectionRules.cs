// TrimUnsafeReflectionRules.cs — issue #414 ("48/S3"): trim-safety of the
// hot paths, and why "zero allocations" is not the claim.
//
// WHAT THIS FILE IS NOT
// ---------------------
// It is NOT a second `ReflectionConventionRule`. #626 is merged, working, and
// deliberately scoped: it bans the DYNAMIC-CODE family — `Assembly.Load*`,
// `AssemblyLoadContext`, `Reflection.Emit`, `TypeBuilder`, `DynamicMethod`,
// `AppDomain.DefineDynamicAssembly` — everywhere outside `src/Harbor.Plugins.*`.
// Nothing here repeats that, and nothing here edits that file's rule. This is a
// separate guard on a separate axis, filed beside it. The one thing shared is
// the plugin allowance table, because two copies of the same architectural fact
// is the drift `SourceScan` was written to end.
//
// THE AXIS: WHY ZERO ALLOCATIONS SAYS NOTHING ABOUT TRIMMING
// ---------------------------------------------------------
// The performance claim in `docs/BENCHMARKS.md` is measured with
// BenchmarkDotNet and it is sound — for what it measures. Allocations are a
// property of the running code: you can count them. Trimming is a property of
// what the TRIMMER could PROVE at publish time, and it cuts a different thing:
// reflection-based serialization, `dynamic`, types named only as strings,
// `MethodInfo`/`Type.GetType` by name, `Activator.CreateInstance`, generic
// virtual methods, and anything reached through an `XAttribute`-style
// by-name lookup. A path that allocates zero bytes can depend on all six.
// So this file is the other half of the same claim, and it had no gate.
//
// The shape that matters, and it is a shape rather than a smell: a hot path
// written so it allocates nothing, and falling back to a reflection call when a
// precomputed handle is absent. `JsonAppConfigStore` is exactly that — the
// `JsonTypeInfo` path first, `JsonSerializer.Deserialize<T>(json, JsonOptions)`
// on the other branch. The fallback allocates nothing either. Under
// `PublishTrimmed` it throws on the first call instead, because
// `JsonSerializer.IsReflectionEnabledByDefault` is off whenever the trimmer
// runs. That is the whole failure mode in one ternary: it works, it is fast,
// and it is invisible until someone switches the publish on.
//
// THE FIRST MEASUREMENT, AND IT GOVERNS THE REST
// ----------------------------------------------
// Asked whether there is anything to trim, the answer decides how big a guard
// this can honestly be, so it was measured first:
//
//   * `PublishTrimmed` — 0 occurrences in the repository. `TrimMode=full`
//     appears exactly once, inside
//     `Condition="'$(HarborWithAot)' == 'true'"` in Harbor.App.Cli.csproj, and
//     `HarborWithAot` DEFAULTS TO false (Directory.Build.props sets
//     `PublishAot=false` repo-wide as "NativeAOT opt-in per project; default
//     off"). No trimmer runs.
//   * `TrimmerRootAssembly` — 0. `ILLink.Descriptors.xml` / `link.xml` /
//     `rd.xml` — 0 files. There is no root set for a trimmer to be fed.
//   * `IsAotCompatible=true` — 4 projects: `Harbor.Tui.CellForge`,
//     `Harbor.Tui.CellForge.Engine`, `Harbor.Ui.Framework.Rendering`,
//     `Harbor.DesignSystem`. (Two further hits for the string are prose in
//     comments.) That flag defaults `IsTrimmable`, `EnableTrimAnalyzer`,
//     `EnableSingleFileAnalyzer` and `EnableAotAnalyzer` to true, so IL2xxx
//     IS machine-checked — inside those four projects only. Three of them
//     contain no `JsonSerializer` call at all; `Harbor.DesignSystem` has one
//     file and it already goes through `ThemeJsonContext`.
//
// So the honest count is small, and the guard is sized to the measured count
// rather than to the size of the fear. Turning the publish on is #413's job and
// this file does not touch `.github/workflows/`.
//
// WHAT #626 DOES NOT COVER — MEASURED, NOT ASSUMED
// -------------------------------------------------
// #626's header names the gap itself: "STRING-BASED MEMBER ACCESS IS NOT
// BANNED, AND THAT IS A DECISION", because the overwhelming majority of
// `GetProperty("…")` is `JsonElement.GetProperty` — a JSON key, where the
// string IS the call. It also records one real Type-level site as "out of
// scope, not as approved". Everything below was swept for and classified:
//
//   construct                                   raw   real   where
//   -----------------------------------------   ---   ---   ---------------------------------
//   JsonSerializer reflection serialize/deserialize 56     3   JsonAppConfigStore x2, FileTrustPolicy
//   typeof(T).GetMethods()/GetXxx(name)         35     1   ViewModelLocator (34 of the 35 are JSON keys)
//   MakeGenericMethod / MakeGenericType           1     1   ViewModelLocator
//   Activator.CreateInstance                      2     1   Plugins.Instantiation (allowed family)
//   Type.GetType("name")                          0     0   —
//   MethodInfo.Invoke                             77     0   76 of 77 are `?.Invoke()` delegates
//   dynamic                                       1     0   the word "dynamic" inside a log message
//   XAttribute / XElement / XName.Get             0     0   —
//   System.Linq.Expressions + .Compile()          1     1   ViewModelLocator
//
// The 56→3 and 35→1 collapses are the point. A naive scan reports 56 and 35;
// those numbers are wrong twice over, in the way merged #970 measured (245 → 87
// for the same reason). Seven of the 56 use an options object that DOES install
// a `TypeInfoResolver` and are therefore source-generated; five more are
// converter-internal calls that pass the ambient `options` parameter and inherit
// the caller's resolver; and all 34 of the string-literal `GetProperty` calls
// are `JsonElement`. A guard built on the raw numbers would be red on code that
// is correct, and a guard that cries wolf gets deleted rather than fixed.
//
// THE THREE RULES
// ---------------
//   TRIM-SERIALIZATION-MAY-NOT-DEPEND-ON-AN-UNPROVEN-TYPE
//       A `JsonSerializer.Serialize*`/`Deserialize*` call whose argument list
//       carries no `JsonTypeInfo` and whose options object is a resolver-free
//       field is relying on the trimmer to have proven a type it cannot see.
//   TRIM-MAY-NOT-RESOLVE-A-CLR-MEMBER-BY-A-STRING-NAME
//       `typeof(…).GetMethod/GetProperty/GetField/GetMember/GetNestedType/
//       GetConstructor/GetMethods`, `Type.GetType("…")`,
//       `Activator.CreateInstance`, `MakeGenericMethod`, `MakeGenericType`.
//       NOT bare `x.GetProperty("key")` — see the JSON-key note above.
//   TRIM-MAY-NOT-COMPILE-AN-EXPRESSION-TREE-AT-RUN-TIME
//       `System.Linq.Expressions` in product code. The AOT-safe use of an
//       expression tree is to hand it to a query provider that compiles it at
//       build time; Harbor has no such provider, so every tree here ends in
//       `LambdaExpression.Compile()`, which is `RequiresDynamicCode`.
//
// NO ALLOWANCE TABLE, ON PURPOSE
// ------------------------------
// There is no `KnownViolations` table in this file. #847 closed the "declarative
// list of known-unsafe constructs" shape and #921 deleted an allowance that had
// no reader. The three sites below were FIXED rather than grandfathered: a rule
// with a baseline cannot distinguish "this debt is accepted" from "this rule
// matches nothing", and #591 is the precedent — an instrument that halved its
// own rule and reported plausible zeros. A guard with no escape hatch has to be
// honest, which is the property worth having here.
//
// NON-VACUITY
// -----------
//   * `NonVacuity_The_Serialization_Matcher_Fires_On_Planted_Offenders_Only`
//     — twelve planted snippets; the four resolver-free calls and the eight
//     correct shapes, INCLUDING `JsonElement.GetProperty("models")` and a file
//     that installs a resolver.
//   * `NonVacuity_The_String_Named_Member_Matcher_Ignores_Json_Keys`
//     — the specific #626 carve-out, asserted rather than assumed: a JSON key is
//     silent, `typeof(T).GetMethod("M")` is not.
//   * `NonVacuity_The_Expression_Tree_Matcher_Fires_On_Compile_Only`
//   * `NonVacuity_Comments_Are_Stripped_Before_Matching`
//   * `NonVacuity_Discovery_Sees_The_Product_Trees`

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #414: the hot paths must not depend on the trimmer having proven a
///     type, which is the half of the AOT claim that allocation benchmarks do not
///     measure. Sits beside <see cref="ReflectionConventionRule" /> and shares its
///     plugin allowance rather than restating it.
/// </summary>
public sealed class TrimUnsafeReflectionRules
{
    private const string NoUnprovenType = "TRIM-SERIALIZATION-MAY-NOT-DEPEND-ON-AN-UNPROVEN-TYPE";
    private const string NoStringNamedMember = "TRIM-MAY-NOT-RESOLVE-A-CLR-MEMBER-BY-A-STRING-NAME";
    private const string NoExpressionCompile = "TRIM-MAY-NOT-COMPILE-AN-EXPRESSION-TREE-AT-RUN-TIME";

    /// <summary>
    ///     The <c>JsonSerializer</c> call family, reads included. <c>[A-Za-z0-9]*</c>
    ///     rather than an enumerated suffix list, because there is no word
    ///     boundary between <c>Serialize</c> and <c>ToUtf8Bytes</c> — the same
    ///     trap <see cref="ProviderPayloadSerializationRules" /> documents — and a
    ///     list would be satisfied by switching to <c>SerializeToNode</c>.
    ///     <para>
    ///         The character class is <c>[A-Za-z0-9]</c> and not
    ///         <c>[A-Za-z]</c> because <c>SerializeToUtf8Bytes</c> contains a
    ///         DIGIT. The first version of this line said <c>[A-Za-z]*</c>, which
    ///         matched <c>Serialize</c> and then stopped one character short of the
    ///         <c>(</c>, so the matcher was blind to
    ///         <c>JsonSerializer.SerializeToUtf8Bytes</c> — the single most common
    ///         write form in this repository. It reported a plausible 2 instead of 3
    ///         and nothing looked wrong. That is #591's failure mode exactly: an
    ///         instrument that quietly under-matches and returns zeros. It was caught
    ///         by <see cref="NonVacuity_The_Serialization_Matcher_Fires_On_Planted_Offenders_Only" />
    ///         on synthetic source, before the guard was ever pushed, which is the
    ///         only reason that test exists.
    ///     </para>
    ///     <para>
    ///         <c>Deserialize</c> is matched here even though #475 excluded it,
    ///         because that exclusion is exactly the uncovered axis: on this
    ///         repository every real reflection call is a READ.
    ///     </para>
    ///     <para>
    ///         The generic argument tolerates one level of nesting
    ///         (<c>Deserialize&lt;Dictionary&lt;string, JsonElement&gt;&gt;</c>) because
    ///         the call locator must reach the <c>(</c> to read the arguments; the
    ///         decision about what is in them is made by
    ///         <see cref="FileFacts" />, not by this pattern.
    ///     </para>
    /// </summary>
    private static readonly Regex SerializerCall = new(
        @"\bJsonSerializer\s*\.\s*(?:Serialize|Deserialize)[A-Za-z0-9]*\s*"
        + @"(?:<[^<>()]*(?:<[^<>()]*>[^<>()]*)*>)?\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     An argument list that already carries a source-generated
    ///     <see cref="System.Text.Json.Serialization.JsonTypeInfo" />. Two spellings
    ///     count: the type itself, and a context's <c>.Default</c> surface, which is
    ///     what every one of the 41 correct call sites in this repository writes.
    /// </summary>
    private static readonly Regex TypeInfoBearing = new(
        @"\bJsonTypeInfo\b" + @"|\b\w*Context\s*\.\s*Default\b" + @"|\bJsonSerializerContext\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     A <c>JsonSerializerOptions</c> FIELD or local — an identifier followed by
    ///     <c>=</c>. Deliberately distinct from the parameter form below: a
    ///     converter's ambient <c>options</c> arrives carrying its caller's resolver,
    ///     so judging it here would report the five
    ///     <c>JsonConverter&lt;T&gt;</c> internals that inherit correctness as if they
    ///     introduced it.
    /// </summary>
    private static readonly Regex OptionsField = new(
        @"\bJsonSerializerOptions\s+(\w+)\s*=", RegexOptions.Compiled);

    /// <summary>
    ///     A <c>JsonSerializerOptions</c> PARAMETER — an identifier followed by
    ///     <c>,</c> or <c>)</c>, which is the shape
    ///     <c>JsonConverter&lt;T&gt;.Read/Write</c> hands it in.
    /// </summary>
    private static readonly Regex OptionsParameter = new(
        @"\bJsonSerializerOptions\s+(\w+)\s*[,)]", RegexOptions.Compiled);

    /// <summary>
    ///     A local, parameter, property or field declared as a
    ///     <c>JsonTypeInfo&lt;T&gt;</c> — the other way a correct call site supplies
    ///     one without naming a context (<c>_jsonTypeInfo</c> in
    ///     <c>JsonAppConfigStore</c>, <c>typeInfo</c> in <c>SessionFileIO</c>).
    /// </summary>
    private static readonly Regex TypeInfoDeclaration = new(
        @"\bJsonTypeInfo\s*<\s*[^<>]*>\s+(\w+)", RegexOptions.Compiled);

    /// <summary>
    ///     CLR member-by-name, as one alternation. The <c>typeof(…)</c> prefix on
    ///     the <c>GetXxx</c> branches is the load-bearing part: it is what separates
    ///     "look a member up on a <see cref="Type" />" from "read a key out of a
    ///     <c>JsonElement</c>", and those two are syntactically identical otherwise.
    ///     This is the classification #626 declined to do; doing it for the
    ///     <c>typeof</c> form alone is decidable, and the bare
    ///     <c>x.GetProperty("key")</c> form stays out of scope rather than being
    ///     matched by a receiver heuristic that would be wrong often enough to be
    ///     suppressed.
    /// </summary>
    private static readonly Regex StringNamedMember = new(
        @"\btypeof\s*\([^)]*\)\s*\.\s*Get(?:Methods|Fields|Properties|Members|Constructors|Method|Property|Field|Member|NestedType|Constructor)\s*\("
        + @"|(?<![\w.])Type\s*\.\s*GetType\s*\(\s*\x22"
        + @"|(?<![\w.])Activator\s*\.\s*CreateInstance\s*\("
        + @"|\bMakeGenericMethod\s*\("
        + @"|\bMakeGenericType\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     The expression-tree surface: the namespace import, and the static
    ///     <c>Expression</c> factory calls. <c>.Compile()</c> is deliberately NOT in
    ///     this alternation — on its own it is not specific to expression trees —
    ///     and is handled per-file by <see cref="BuildsExpressionTrees" /> instead.
    /// </summary>
    private static readonly Regex ExpressionTree = new(
        @"\busing\s+System\s*\.\s*Linq\s*\.\s*Expressions\s*;"
        + @"|\bExpression\s*\.\s*(?:Lambda|Compile|Convert|Call|Parameter|New|Binary|Member|Property|Field|Constant|TryQuote|TypeAs|Add|Check|Not|And|Or|Equal|Modulo)\s*[<(.]",
        RegexOptions.Compiled);

    /// <summary>Whether a file builds expression trees at all, the precondition for judging <c>.Compile()</c>.</summary>
    private static readonly Regex CompiledTree = new(@"\.\s*Compile\s*\(", RegexOptions.Compiled);

    /// <summary>Whether the file installs a source-generated resolver on any options object it owns.</summary>
    private const string ResolverAssignment = "TypeInfoResolver";

    /// <summary>One violation, located.</summary>
    /// <param name="RuleId">Which rule was violated.</param>
    /// <param name="File">Repo-relative file name.</param>
    /// <param name="Line">1-based line in the comment-stripped source.</param>
    /// <param name="Text">The matched text, trimmed.</param>
    internal readonly record struct Hit(string RuleId, string File, int Line, string Text)
    {
        /// <summary>The line a failure message prints.</summary>
        internal string Report() => $"{File}:{Line}  [{RuleId}]  {Text}";
    }

    /// <summary>Repo-relative project directory of a repo-relative path.</summary>
    private static string ProjectDirOf(string repoRelativeFile) =>
        Path.GetDirectoryName(repoRelativeFile)?.Replace('\\', '/') ?? string.Empty;

    /// <summary>Every product file, as (display path, raw source) pairs.</summary>
    /// <remarks>
    ///     Raw, not pre-stripped. <see cref="ScanLines" /> and
    ///     <see cref="FileFacts.Of" /> strip for themselves, so a caller — including
    ///     the synthetic non-vacuity controls, which hand in prose on purpose —
    ///     cannot reach a matcher with comments still live.
    /// </remarks>
    private static List<(string DisplayPath, string Source)> ProductSource()
    {
        var sources = new List<(string, string)>();
        foreach (string file in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(file) is { } text)
            {
                sources.Add((SourceScan.Relative(file), text));
            }
        }

        return sources;
    }

    /// <summary>
    ///     The argument text of a call whose <c>(</c> is at <paramref name="openParen" />,
    ///     balancing nested parentheses and stopping at the <c>;</c> that ends the
    ///     statement. Multi-line calls are the norm here — every
    ///     <c>JsonSerializer</c> call in this repository that was formatted to fit
    ///     wrapped — so a matcher that worked per line would see an empty argument
    ///     list and pass everything.
    /// </summary>
    private static string BalancedArgs(string source, int openParen)
    {
        int depth = 0;
        for (int i = openParen; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return source[(openParen + 1)..i];
                }
            }
            else if (c == ';' && depth == 0)
            {
                break;
            }
        }

        return source[(openParen + 1)..];
    }

    /// <summary>
    ///     Per-file facts the serialization rule needs, computed once per file so
    ///     the decision does not depend on which line the call happens to sit on.
    /// </summary>
    /// <param name="Source">Raw file source; comments are stripped here.</param>
    private sealed record FileFacts(
        string Source,
        bool InstallsResolver,
        HashSet<string> TypeInfoNames,
        HashSet<string> OptionsFields,
        HashSet<string> OptionsParameters)
    {
        /// <summary>Reads the facts out of one file.</summary>
        internal static FileFacts Of(string rawSource)
        {
            string source = SourceScan.StripComments(rawSource);
            var typeInfos = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in TypeInfoDeclaration.Matches(source))
            {
                typeInfos.Add(m.Groups[1].Value);
            }

            var parameters = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in OptionsParameter.Matches(source))
            {
                parameters.Add(m.Groups[1].Value);
            }

            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in OptionsField.Matches(source))
            {
                // An options field initialised FROM a context is that context's
                // options, resolver and all, whatever the local is named:
                // `JsonlSessionStore.JsonOptions = JsonlCodecContext.JsonOptions`.
                // Deciding on the field's own name would report that as a
                // violation, which is how a matcher ends up matching correct code.
                // The initializer is read to the statement's `;` so a wrapped
                // declaration is classified the same as a one-line one.
                if (!AliasedFromAContext(source, m.Index + m.Length))
                {
                    fields.Add(m.Groups[1].Value);
                }
            }

            return new FileFacts(
                source,
                source.Contains(ResolverAssignment, StringComparison.Ordinal),
                typeInfos,
                fields,
                parameters);
        }

        /// <summary>
        ///     Whether an argument list already names a source-generated type, by
        ///     either spelling: a context's <c>.Default</c> surface, or an identifier
        ///     this file declares as a <c>JsonTypeInfo&lt;T&gt;</c>.
        /// </summary>
        internal bool CarriesTypeInfo(string args)
        {
            if (TypeInfoBearing.IsMatch(args))
            {
                return true;
            }

            foreach (string name in TypeInfoNames)
            {
                if (args.Contains(name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     The last argument, when it is a bare identifier naming a
        ///     resolver-free options field declared in this file. Anything else —
        ///     a parameter, an inline <c>new JsonSerializerOptions</c>, a
        ///     <c>JsonSerializerContext</c> default — returns <see langword="null" />
        ///     and is not judged.
        /// </summary>
        internal string? UnprovenOptionsField(string args)
        {
            string trimmed = args.TrimEnd();
            int lastComma = trimmed.LastIndexOf(',');
            string last = lastComma >= 0 ? trimmed[(lastComma + 1)..].Trim() : trimmed.Trim();

            if (last.Length == 0 || !char.IsLetter(last[0]) || last.Contains(' '))
            {
                return null;
            }

            return OptionsFields.Contains(last)
                   && !OptionsParameters.Contains(last)
                   && !InstallsResolver
                ? last
                : null;
        }
    }

    /// <summary>Whether a file references the expression-tree surface at all.</summary>
    private static bool BuildsExpressionTrees(string source) =>
        ExpressionTree.IsMatch(SourceScan.StripComments(source));

    /// <summary>An identifier bound to a source-generated context's options surface.</summary>
    private static readonly Regex ContextAlias = new(@"\b\w*Context\b", RegexOptions.Compiled);

    /// <summary>
    ///     Whether the statement starting at <paramref name="afterEquals" /> initialises
    ///     from a context. Scans to the terminating <c>;</c> at paren-depth zero, so a
    ///     wrapped initializer such as
    ///     <c>= new(JsonSerializerDefaults.Web) { … }</c> is read whole.
    /// </summary>
    private static bool AliasedFromAContext(string source, int afterEquals)
    {
        int depth = 0;
        for (int i = afterEquals; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ';' && depth == 0)
            {
                return ContextAlias.IsMatch(source[afterEquals..i]);
            }
        }

        return false;
    }

    /// <summary>
    ///     Line-by-line scan for a single-line matcher, reporting the first match
    ///     per line. Comments are stripped HERE, so no caller can hand a matcher a
    ///     file whose <c>///</c> prose still counts as code — which is the control
    ///     <see cref="NonVacuity_Comments_Are_Stripped_Before_Matching" /> asserts.
    /// </summary>
    private static List<Hit> ScanLines(
        IEnumerable<(string DisplayPath, string Source)> sources,
        string ruleId,
        Regex matcher)
    {
        var hits = new List<Hit>();
        foreach ((string displayPath, string rawSource) in sources)
        {
            string[] lines = SourceScan.StripComments(rawSource).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = matcher.Match(lines[i]);
                if (match.Success)
                {
                    hits.Add(new Hit(ruleId, displayPath, i + 1, match.Value.Trim()));
                }
            }
        }

        return hits;
    }

    // =====================================================================
    // 1. The rules.
    // =====================================================================

    /// <summary>
    ///     A hot path may not hand <c>JsonSerializer</c> only a
    ///     <c>JsonSerializerOptions</c>. With no <see cref="System.Text.Json.Serialization.JsonTypeInfo" />
    ///     and no <c>TypeInfoResolver</c> on the options object, the serializer
    ///     resolves the contract reflectively at run time — and under
    ///     <c>PublishTrimmed</c> reflection-based serialization is off by default,
    ///     so the call throws on first use instead of writing bytes. Not a
    ///     performance property: these paths allocate nothing today and are exactly
    ///     as unsafe.
    /// </summary>
    [Test]
    public async Task Hot_Paths_May_Not_Serialize_Through_A_Reflection_Contract()
    {
        var failures = new List<string>();

        foreach ((string displayPath, string rawSource) in ProductSource())
        {
            string projectDir = ProjectDirOf(displayPath);
            if (ReflectionConventionRule.IsAllowed(projectDir))
            {
                continue;
            }

            FileFacts facts = FileFacts.Of(rawSource);
            foreach (Match match in SerializerCall.Matches(facts.Source))
            {
                string args = BalancedArgs(facts.Source, match.Index + match.Length - 1);
                if (facts.CarriesTypeInfo(args))
                {
                    continue;
                }

                if (facts.UnprovenOptionsField(args) is not { } optionsField)
                {
                    continue;
                }

                int line = facts.Source[..match.Index].Count(c => c == '\n') + 1;
                failures.Add(new Hit(NoUnprovenType, displayPath, line, optionsField).Report());
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because("a `JsonSerializer` call that is handed only a JsonSerializerOptions resolves "
                   + "its contract by reflection at run time. Under PublishTrimmed — which "
                   + "JsonSerializer.IsReflectionEnabledByDefault turns reflection off for — that is "
                   + "an InvalidOperationException on the first call, on a path that allocates "
                   + "nothing and therefore looks clean in every benchmark in docs/BENCHMARKS.md. Pass "
                   + "a source-generated JsonTypeInfo, or set TypeInfoResolver on the options object. "
                   + "Offenders: " + string.Join(" | ", failures));
    }

    /// <summary>
    ///     A hot path may not resolve a CLR member, a type, or an instance from a
    ///     string the trimmer never saw. This is the family #626's header names as
    ///     "recorded as out of scope, not as approved", plus the
    ///     <c>Activator.CreateInstance</c> and <c>Type.GetType("…")</c> forms that
    ///     table did not enumerate.
    /// </summary>
    [Test]
    public async Task Hot_Paths_May_Not_Resolve_A_Clr_Member_By_A_String_Name()
    {
        List<Hit> hits = ScanLines(ProductSource(), NoStringNamedMember, StringNamedMember);
        var failures = new List<string>();
        foreach (Hit hit in hits)
        {
            if (ReflectionConventionRule.IsAllowed(ProjectDirOf(hit.File)))
            {
                continue;
            }

            failures.Add(hit.Report());
        }

        await Assert.That(failures).IsEmpty()
            .Because("a member, type or instance named only by string is invisible to the trimmer: "
                   + "nothing in a ProjectReference proves the name resolves, so the link step may "
                   + "remove it and the failure surfaces as a null or a TypeLoadException at the "
                   + "moment the feature is first used. Where a generic member is needed, pass the "
                   + "closed delegate or the Type itself from a call site where it is statically "
                   + "known. Offenders: " + string.Join(" | ", failures));
    }

    /// <summary>
    ///     Product code may not build or compile a LINQ expression tree. The
    ///     AOT-safe use of one is to hand it to a query provider that compiles it
    ///     ahead of time; Harbor has none, so a tree here always terminates in
    ///     <c>LambdaExpression.Compile()</c>, which emits code at run time — the
    ///     same capability #626 bans, reached through an API it cannot name.
    /// </summary>
    [Test]
    public async Task Hot_Paths_May_Not_Compile_An_Expression_Tree_At_Run_Time()
    {
        var failures = new List<string>();

        foreach ((string displayPath, string source) in ProductSource())
        {
            if (!BuildsExpressionTrees(source))
            {
                continue;
            }

            foreach (Hit hit in ScanLines([(displayPath, source)], NoExpressionCompile, ExpressionTree))
            {
                failures.Add(hit.Report());
            }

            foreach (Hit hit in ScanLines([(displayPath, source)], NoExpressionCompile, CompiledTree))
            {
                failures.Add(hit.Report());
            }
        }

        await Assert.That(failures).IsEmpty()
            .Because("System.Linq.Expressions in product code ends in LambdaExpression.Compile(), "
                   + "which is RequiresDynamicCode: it builds IL at run time, so it cannot be AOT "
                   + "compiled and cannot be analysed ahead of the publish. Where the goal was to "
                   + "avoid a per-call dictionary lookup, call the closed generic directly — the "
                   + "type is statically known at the call site, so the lookup was never needed. "
                   + "Offenders: " + string.Join(" | ", failures));
    }

    // =====================================================================
    // 2. Non-vacuity. A rule nobody can fail is a comment.
    // =====================================================================

    /// <summary>
    ///     Sensitivity and specificity over synthetic source. The four
    ///     resolver-free calls must be caught; the eight correct shapes must stay
    ///     silent — including the JSON-key form, the converter's ambient
    ///     parameter, and a file that installs a resolver. Without the negatives a
    ///     matcher widened until everything is a violation would still pass.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Serialization_Matcher_Fires_On_Planted_Offenders_Only()
    {
        const string ResolverFreeRead =
            "private static readonly JsonSerializerOptions Opts = new();\n"
            + "var v = JsonSerializer.Deserialize<T>(json, Opts);";
        const string ResolverFreeWrite =
            "private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };\n"
            + "string s = JsonSerializer.Serialize(cfg, Opts);";
        const string ResolverFreeFieldOnThreeLines =
            "private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);\n"
            + "var cfg = _info is not null\n"
            + "    ? JsonSerializer.Deserialize(json, _info)\n"
            + "    : JsonSerializer.Deserialize<T>(json, Opts);";
        // `SerializeToUtf8Bytes` carries a DIGIT. With `[A-Za-z]*` instead of
        // `[A-Za-z0-9]*` the matcher stopped one character short of the `(` and
        // was blind to this form — which is the write call this repository uses
        // most. Planted explicitly so a future narrowing of the character class
        // is caught here rather than by a caller.
        const string ResolverFreeUtf8Bytes =
            "private static readonly JsonSerializerOptions Opts = new();\n"
            + "byte[] b = JsonSerializer.SerializeToUtf8Bytes(entry, Opts);";
        const string ResolverFreeSpacedUtf8Bytes =
            "private static readonly JsonSerializerOptions Opts = new();\n"
            + "JsonSerializer . SerializeToUtf8Bytes(payload, Opts);";
        const string ResolverFreeUtf8Text =
            "private static readonly JsonSerializerOptions Opts = new();\n"
            + "string s = JsonSerializer.SerializeToUtf8Text(entry, Opts);";
        const string ResolverFreeNestedGeneric =
            "private static readonly JsonSerializerOptions Opts = new();\n"
            + "var v = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(j, Opts);";

        const string ContextDefault =
            "var b = JsonSerializer.Serialize(entry, JsonlCodecContext.Default.MessageEntry);";
        const string ContextDefaultRead =
            "var e = JsonSerializer.Deserialize(line, JsonlCodecContext.Default.MessageEntry);";
        const string DeclaredTypeInfo =
            "JsonTypeInfo<Foo> _info = Ctx.Default.Foo;\nvar x = JsonSerializer.Deserialize(json, _info);";
        const string ConverterAmbientParameter =
            "public override JsonTypeInfo<T> Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions options)\n"
            + "    => (JsonTypeInfo<T>)JsonSerializer.Deserialize(ref r, options);";
        const string FileThatInstallsAResolver =
            "private static readonly JsonSerializerOptions Opts = new() { TypeInfoResolver = Ctx.Default };\n"
            + "var x = JsonSerializer.Deserialize<ProviderConfig>(text, Opts);";
        const string FieldAliasedFromAContext =
            "private static readonly JsonSerializerOptions Opts = JsonlCodecContext.JsonOptions;\n"
            + "var x = JsonSerializer.Deserialize(json, Opts);";
        const string PlainWriter =
            "writer.WriteString(\"model\", request.Model);";
        const string ContextDeclaration =
            "[JsonSerializable(typeof(Foo))]\ninternal sealed partial class Ctx : JsonSerializerContext;";

        var offenders = ScanSerialization([
            ("src/Harbor.X/A.cs", ResolverFreeRead),
            ("src/Harbor.X/B.cs", ResolverFreeWrite),
            ("src/Harbor.X/C.cs", ResolverFreeUtf8Bytes),
            ("src/Harbor.X/D.cs", ResolverFreeFieldOnThreeLines),
            ("src/Harbor.X/M.cs", ResolverFreeSpacedUtf8Bytes),
            ("src/Harbor.X/N.cs", ResolverFreeUtf8Text),
            ("src/Harbor.X/O.cs", ResolverFreeNestedGeneric),
        ]);

        await Assert.That(offenders).IsEquivalentTo(new[]
        {
            "src/Harbor.X/A.cs",
            "src/Harbor.X/B.cs",
            "src/Harbor.X/C.cs",
            "src/Harbor.X/D.cs",
            "src/Harbor.X/M.cs",
            "src/Harbor.X/N.cs",
            "src/Harbor.X/O.cs",
        }).Because("seven planted resolver-free calls must be caught, including the one that only "
                   + "appears on the third line of a ternary (the form JsonAppConfigStore actually "
                   + "uses) and the digit-bearing SerializeToUtf8Bytes — which this matcher got wrong "
                   + "on its first run, reporting a plausible 3 of 4. Found: "
                   + string.Join(", ", offenders));

        var correct = ScanSerialization([
            ("src/Harbor.X/E.cs", ContextDefault),
            ("src/Harbor.X/F.cs", ContextDefaultRead),
            ("src/Harbor.X/G.cs", DeclaredTypeInfo),
            ("src/Harbor.X/H.cs", ConverterAmbientParameter),
            ("src/Harbor.X/I.cs", FileThatInstallsAResolver),
            ("src/Harbor.X/J.cs", FieldAliasedFromAContext),
            ("src/Harbor.X/K.cs", PlainWriter),
            ("src/Harbor.X/L.cs", ContextDeclaration),
        ]);

        await Assert.That(correct).IsEmpty()
            .Because("the eight correct shapes must stay silent. A source-generated JsonTypeInfo, an "
                   + "options object that installs a TypeInfoResolver, a field aliased from a context, "
                   + "and a converter's ambient parameter are all trim-safe, and reporting them is the "
                   + "false positive that gets a guard deleted instead of fixed. Reported: "
                   + string.Join(", ", correct));
    }

    /// <summary>
    ///     The string-named-member matcher's whole reason for existing is that it
    ///     tells a CLR member apart from a JSON key. Asserted directly, because the
    ///     two are one token apart and a matcher that cannot separate them would
    ///     fire on all 34 tool-argument readers in this repository.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_String_Named_Member_Matcher_Ignores_Json_Keys()
    {
        var jsonKeys = new[]
        {
            "src/Harbor.X/A.cs",
            "src/Harbor.X/B.cs",
            "src/Harbor.X/C.cs",
            "src/Harbor.X/D.cs",
            "src/Harbor.X/E.cs",
        };

        var reported = ScanLines(
        [
            ("src/Harbor.X/A.cs", "string p = args.GetProperty(\"path\").GetString()!;"),
            ("src/Harbor.X/B.cs", "if (!element.TryGetProperty(\"items\", out var v)) return;"),
            ("src/Harbor.X/C.cs", "var r = response.RootElement.GetProperty(\"result\");"),
            ("src/Harbor.X/D.cs", "() => element.GetProperty(field).GetString() ?? string.Empty,"),
            ("src/Harbor.X/E.cs", "var m = typeof(ServiceProviderServiceExtensions).GetMethods();"),
        ], NoStringNamedMember, StringNamedMember).Select(static h => h.File).ToList();

        await Assert.That(reported).IsEquivalentTo(new[] { "src/Harbor.X/E.cs" })
            .Because("only the typeof(…) form resolves a CLR member. The other four read a key out of "
                   + "a JsonElement, where the string IS the call — which is precisely why #626 "
                   + "declined to match the bare form and this file matches the typeof form instead. "
                   + "Reported: " + string.Join(", ", reported));

        await Assert.That(reported).DoesNotContain(jsonKeys[0])
            .Because("a JSON key reported as reflection by name is the failure mode that would make "
                   + "this guard a liability: 34 call sites in src/Harbor.Tools.Builtin alone are "
                   + "that shape, and a rule that reports them gets suppressed rather than obeyed");
    }

    /// <summary>
    ///     The expression-tree rule fires on the namespace and the factories, and
    ///     on <c>.Compile()</c> only inside a file that builds trees. A
    ///     <c>.Compile()</c> somewhere else is somebody else's method.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Expression_Tree_Matcher_Fires_On_Compile_Only()
    {
        const string BuildsATree =
            "using System.Linq.Expressions;\n"
            + "var e = Expression.Lambda<Func<int>>(body).Compile();";
        const string TreeNoCompile =
            "using System.Linq.Expressions;\nExpression<Func<T, bool>> pred = x => x.A == x.B;";
        const string UnrelatedCompile =
            "private static Regex Re() => new(\"a\", RegexOptions.Compiled);";

        await Assert.That(ExpressionTree.IsMatch(BuildsATree)).IsTrue()
            .Because("the raw text contains the namespace import, so what follows is testing the "
                   + "rule and not the stripper");

        await Assert.That(ExpressionTree.IsMatch(UnrelatedCompile)).IsFalse()
            .Because("System.Text.RegularExpressions.RegexOptions.Compiled is not an expression tree "
                   + "and matching it would put every regex in the repository on the list");

        await Assert.That(ScanLines([("src/Harbor.X/A.cs", BuildsATree)], NoExpressionCompile, ExpressionTree))
            .IsNotEmpty()
            .Because("the using directive is the entry point into the capability");

        await Assert.That(ScanLines([("src/Harbor.X/A.cs", BuildsATree)], NoExpressionCompile, CompiledTree))
            .IsNotEmpty()
            .Because(".Compile() is the terminus, and it is what actually needs the JIT");

        await Assert.That(ScanLines([("src/Harbor.X/B.cs", TreeNoCompile)], NoExpressionCompile, CompiledTree))
            .IsEmpty()
            .Because("building a tree without compiling it does not need dynamic code — a query "
                   + "provider may compile it ahead of time, which is the AOT-safe use");

        await Assert.That(ScanLines([("src/Harbor.X/C.cs", UnrelatedCompile)], NoExpressionCompile, CompiledTree))
            .IsEmpty()
            .Because("the rule is scoped to files that build expression trees, so an unrelated "
                   + ".Compile() is not judged by it");
    }

    /// <summary>
    ///     <see cref="SourceScan.StripComments" /> is load-bearing for all three
    ///     rules, not hygiene. The CLI csproj explains at length that the AOT block
    ///     configures the opposite of what it claims, in prose containing
    ///     <c>Reflection.Emit</c>-adjacent wording, and `ProviderConfig` names
    ///     <c>JsonSerializer.Deserialize&lt;T&gt;(string, options)</c> inside a
    ///     <c>///</c> remark explaining why reflection is off under trimming. A rule
    ///     that graded its own documentation would fail on the sentence written to
    ///     agree with it.
    /// </summary>
    [Test]
    public async Task NonVacuity_Comments_Are_Stripped_Before_Matching()
    {
        const string JsonProse =
            "/// overloads (JsonSerializer.Deserialize<T>(json, options)), which crash or";
        const string MemberProse = "/// var m = typeof(X).GetMethods(); was the old shape.";
        const string ExpressionProse = "/// using System.Linq.Expressions; is no longer needed.";

        await Assert.That(SerializerCall.IsMatch(JsonProse)).IsTrue()
            .Because("the raw prose does contain the call, so the assertions below prove the "
                   + "stripper rather than a blind matcher");

        await Assert.That(ScanLines([("src/Harbor.X/A.cs", JsonProse)], NoUnprovenType, SerializerCall))
            .IsEmpty().Because("comment stripping has to run before matching");
        await Assert.That(ScanLines([("src/Harbor.X/A.cs", MemberProse)], NoStringNamedMember, StringNamedMember))
            .IsEmpty().Because("the member prose names the typeof form too");
        await Assert.That(ScanLines([("src/Harbor.X/A.cs", ExpressionProse)], NoExpressionCompile, ExpressionTree))
            .IsEmpty().Because("the expression prose is a using directive in a /// line");
    }

    /// <summary>
    ///     The scope must see the product trees and must still contain the sites the
    ///     issue is about. A discovery filter that finds nothing makes all three
    ///     rules green for no reason, which is the NetArchTest trap
    ///     <see cref="ProviderPayloadSerializationRules" /> documents.
    /// </summary>
    [Test]
    public async Task NonVacuity_Discovery_Sees_The_Product_Trees_And_The_Named_Files()
    {
        var sources = ProductSource();
        var paths = sources.Select(static s => s.DisplayPath).ToList();

        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("without a repository root the scan finds no files and every rule in this file "
                   + "passes for the wrong reason");

        await Assert.That(paths.Count).IsGreaterThan(100)
            .Because("src/ and apps/ together hold close to a thousand files; a count this low means "
                   + "discovery is broken rather than the code being clean");

        foreach (string expected in new[]
                 {
                     "src/Harbor.Storage.Jsonl/JsonlSessionStore.cs",
                     "src/Harbor.Tools.Builtin/Tools/Read/ReadTool.cs",
                     "apps/Harbor.App.Cli/Hosting/HostBuilder.cs",
                 })
        {
            await Assert.That(paths).Contains(expected)
                .Because($"{expected} is product code and the rules are about product code; if the "
                       + "scope stopped seeing it, the scope stopped seeing the product");
        }

        await Assert.That(paths.Any(static p => p.StartsWith("tests/", StringComparison.Ordinal))).IsFalse()
            .Because("tests/ is where every planted offender in this file lives; a scope that "
                   + "included it could never pass");

        await Assert.That(paths.Any(static p => p.StartsWith("contrib/", StringComparison.Ordinal))).IsFalse()
            .Because("contrib/ is unmaintained and out of CI (AGENTS.md); a violation there "
                   + "describes code that cannot break the build");
    }

    /// <summary>
    ///     The rule ids are three distinct claims, not one matcher renamed three
    ///     times — and the plugin allowance this file shares with #626 must still be
    ///     occupied, so a rename cannot turn the rules into no-ops while they read as
    ///     enforced.
    /// </summary>
    [Test]
    public async Task The_Rule_Ids_Are_Distinct_And_The_Shared_Allowance_Is_Occupied()
    {
        var ids = new[] { NoUnprovenType, NoStringNamedMember, NoExpressionCompile };

        await Assert.That(ids.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(3)
            .Because("three different capabilities are being described; two rules sharing an id "
                   + "makes a failure message ambiguous about which claim was violated");

        await Assert.That(ReflectionConventionRule.IsAllowed("src/Harbor.Plugins.Compilation")).IsTrue()
            .Because("this file reads #626's table instead of restating it, so the shared fact has to "
                   + "stay reachable — if the table moved, the plugin family would start reporting here");

        await Assert.That(ReflectionConventionRule.IsAllowed("src/Harbor.Storage.Jsonl")).IsFalse()
            .Because("a prefix that matched everything would exempt the entire product tree, which is "
                   + "the widening #626's own allowance test refuses");
    }

    /// <summary>
    ///     The serialization rule, reduced to the file names it reports, so the
    ///     non-vacuity control can assert on it directly.
    /// </summary>
    private static List<string> ScanSerialization(IEnumerable<(string DisplayPath, string Source)> sources)
    {
        var reported = new List<string>();
        foreach ((string displayPath, string rawSource) in sources)
        {
            FileFacts facts = FileFacts.Of(rawSource);
            foreach (Match match in SerializerCall.Matches(facts.Source))
            {
                string args = BalancedArgs(facts.Source, match.Index + match.Length - 1);
                if (facts.CarriesTypeInfo(args))
                {
                    continue;
                }

                if (facts.UnprovenOptionsField(args) is not null)
                {
                    reported.Add(displayPath);
                }
            }
        }

        return reported;
    }
}