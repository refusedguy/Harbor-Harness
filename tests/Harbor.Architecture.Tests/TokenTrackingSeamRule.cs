// TokenTrackingSeamRule.cs — GUARD for issue #471, slice 1: ITokenTracker.
//
// The shape follows ThemeRoleInterfaceTests.cs (#469), which guarded the same
// kind of split for IThemeService. Two things differ and both are deliberate:
//
//   * Types are resolved BY NAME, not with `typeof`. The role interfaces do not
//     exist yet, and a gate that cannot compile is not a gate that can be landed
//     RED. A missing role is a FAILURE with a readable message instead. Same
//     discipline EnforcerIntegrityTests.cs follows for assembly names.
//   * It additionally grades CONSUMERS, not just the interface. #469 could stop at
//     "the roles are narrow", because nothing checked whether anyone was still
//     injecting the aggregate. Here half the rule is a source scan of src/ +
//     apps/ for files that name ITokenTracker, because a split that leaves the
//     callers on the wide type has changed nothing about them.
//
// THE DEFECT THIS GUARDS
// ----------------------
// `ITokenTracker` was seven members wide and no two consumers wanted the same
// seven. Measured per consumer on dev (origin/dev 8c81114d) by the members each
// one actually CALLS:
//
//   consumer                              what it calls
//   CompactionService                     Estimate, EstimateMessage, EstimateTokens
//   TurnRunner                            RecordTurnUsage, RecordAppendedMessage, EstimateTokens
//   CompactionBehavior                    ShouldCompact, EstimateTokens
//   SteeringDrainBehavior                 RecordAppendedMessage
//   BackgroundDrain                       RecordAppendedMessage
//   ReplLifecycle / PromptPipeline (CLI)   GetStats
//   AgentLoop                             nothing — the field is write-only
//
// Four disjoint roles, one union. That is the ISP violation #471 filed, and it
// is the REAL kind rather than the other one: nobody is sitting on the wide
// interface out of laziness, the consumers genuinely do not overlap. Splitting
// costs each consumer only the members it never called, and it costs TokenTracker
// nothing — the class still implements all four roles; only the DECLARED TYPE
// each consumer binds narrows.
//
// Two of the seven were duplicates that already existed under their own name in
// the same namespace: `Estimate` / `EstimateMessage` were byte-identical to
// `ITokenEstimator`'s, and `EstimateTokens(IReadOnlyList<AgentMessage>)` was a
// pure delegation to `EstimateMessages` whose name pointed the reader at the
// tracker's running estimate while it never consults the cache at all. That is
// the defect #630 and #641 fixed by renaming (`NoteUsage` → `NoteRequestSize`,
// `ContextTokens` → `RequestTokens`): the name was part of the contract and it
// was describing the wrong thing.
//
// WHY THE FOUR ROLES ARE THE ONES REVIEWED
// ----------------------------------------
// They are the four disjoint sets the call sites above actually form, not a
// partition invented for symmetry — `RecordTurnUsage` and `RecordAppendedMessage`
// stay together because TurnRunner, the only caller of the first, also calls the
// second, so separating them would narrow nobody. ShouldCompact sits alone
// because CompactionBehavior is its only caller and it wants nothing else.
//
// WHY A TEXT SCAN FOR THE CONSUMER HALF
// --------------------------------------
// It asks whether a consumer in another ASSEMBLY declared its dependency on the
// wide type; the ctor parameter's declared type is the only thing that matters
// and it is a source-level statement, so a text scan answers it without a
// compiler. Comments are stripped first (SourceCommentStripper, shared with
// #563/#663) — otherwise the XML docs that NAME the aggregate to explain it
// (src/Harbor.Abstractions.Contracts/Models/ContextUsage.cs:16) would be graded
// as consumers of it.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

public sealed class TokenTrackingSeamRule
{
    /// <summary>The aggregate this gate governs.</summary>
    private const string AggregateName = "ITokenTracker";

    /// <summary>
    ///     The role interfaces the aggregate is allowed to be composed from, and
    ///     the members each one owns. The member lists are the point: they are what
    ///     lets <see cref="Role_DoesNot_Leak_AnotherRolesMembers" /> prove no role
    ///     carries another's surface, and they are checked for presence so a role
    ///     cannot be hollowed out to make the leak test vacuous.
    /// </summary>
    private static readonly Dictionary<string, string[]> OwnedMembers = new(StringComparer.Ordinal)
    {
        ["ITokenEstimator"] = ["Estimate", "EstimateMessage", "EstimateMessages"],
        ["IUsageRecorder"] = ["RecordTurnUsage", "RecordAppendedMessage"],
        ["ICompactionHeuristic"] = ["ShouldCompact"],
        ["IUsageStats"] = ["GetStats"],
    };

    /// <summary>The role names, in a stable order for failure messages.</summary>
    private static readonly string[] RoleNames = [.. OwnedMembers.Keys.OrderBy(static r => r, StringComparer.Ordinal)];

    /// <summary>
    ///     Product files permitted to name <see cref="AggregateName" />. Every row is
    ///     a place that legitimately holds ALL FOUR roles and hands them out; a leaf
    ///     consumer has no such reason and must bind the role it uses. Kept as an
    ///     explicit list rather than a pattern because each row is a separate
    ///     argument, and <see cref="CompositionRoots_AreAllStillNamed" /> fails the
    ///     gate when a row goes stale rather than letting it rot into a permission.
    /// </summary>
    private static readonly Dictionary<string, string> CompositionRoots = new(StringComparer.Ordinal)
    {
        ["src/Harbor.Abstractions/Sessions/ITokenTracker.cs"] =
            "the aggregate declaration itself",
        ["src/Harbor.Application/Sessions/TokenTracker.cs"] =
            "the sole implementor — one class, all four roles",
        ["src/Harbor.Hosting/Modules/CoreModule.cs"] =
            "the DI registration: one singleton, bound once",
        ["src/Harbor.Application/Agents/AgentLoop.cs"] =
            "the composition root: fans the one instance out to CompactionBehavior / "
            + "SteeringDrainBehavior / BackgroundDrain / TurnRunner, each of which binds its own role",
        ["src/Harbor.Hosting/Modules/IntelligenceModule.cs"] =
            "resolves that instance to hand it to AgentLoop",
    };

    /// <summary>Matches a line that names the aggregate, with word boundaries.</summary>
    private static readonly Regex AggregateMention = new($@"\b{AggregateName}\b", RegexOptions.Compiled);

    /// <summary>
    ///     Resolves <c>Harbor.Abstractions.Sessions.{name}</c> against every loaded
    ///     Harbor assembly, or <c>null</c> when no assembly declares it.
    /// </summary>
    private static Type? Resolve(string name)
    {
        foreach (var asm in ArchitectureTestHelpers.LoadHarborAssemblies().Values)
        {
            if (asm.GetType($"Harbor.Abstractions.Sessions.{name}", throwOnError: false) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    ///     Every product file that names the aggregate, as repo-relative paths.
    ///     Comments are stripped first so that documentation ABOUT the aggregate is
    ///     not graded as a consumer of it.
    /// </summary>
    private static IReadOnlyList<string> ProductFilesNamingTheAggregate()
    {
        var found = new List<string>();
        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(path) is not { } text)
            {
                continue;
            }

            foreach (string line in SourceCommentStripper.StripAll(text.Split('\n')))
            {
                if (AggregateMention.IsMatch(line))
                {
                    found.Add(SourceScan.Relative(path));
                    break;
                }
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    ///     The aggregate declares no members of its own. Its whole surface is
    ///     inherited from the roles, so it cannot grow: the next member someone adds
    ///     to <c>ITokenTracker</c> is a member declared here, and the gate goes red
    ///     until it is moved onto the role that owns it. Without this, the rest of
    ///     the file could be satisfied by an interface nobody was ever fat about.
    /// </summary>
    [Test]
    public async Task Aggregate_DeclaresNoMembersOfItsOwn()
    {
        Type? aggregate = Resolve(AggregateName);
        await Assert.That(aggregate).IsNotNull()
            .Because($"Harbor.Abstractions.Sessions.{AggregateName} must exist for this gate to grade "
                   + "anything. If it does not, the aggregate was renamed or deleted and every row of the "
                   + "CompositionRoots allowlist is stale.");

        string[] own = [.. aggregate!
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static m => m.MemberType is not MemberTypes.Constructor)
            .Select(static m => m.Name)
            .OrderBy(static n => n, StringComparer.Ordinal)];

        await Assert.That(own).IsEmpty()
            .Because($"{AggregateName} is a DI composition alias over its roles, not a place to declare "
                   + "surface: every member a consumer can see must be inherited from the role that owns "
                   + "it, so a consumer needing one role cannot be handed the other three. Declared directly "
                   + "on the aggregate: " + (own.Length == 0 ? "(none)" : string.Join(", ", own)));
    }

    /// <summary>
    ///     The aggregate is composed from exactly the reviewed role set — a seal
    ///     against an extra base, and the liveness check that makes a missing role a
    ///     named failure rather than a silently smaller allowlist.
    /// </summary>
    [Test]
    public async Task Aggregate_DerivesFromExactlyTheReviewedRoles()
    {
        Type? aggregate = Resolve(AggregateName);
        await Assert.That(aggregate).IsNotNull()
            .Because($"Harbor.Abstractions.Sessions.{AggregateName} must exist — see "
                   + "Aggregate_DeclaresNoMembersOfItsOwn.");

        var missing = new List<string>();
        var bases = new List<string>();
        foreach (string role in RoleNames)
        {
            Type? resolved = Resolve(role);
            if (resolved is null)
            {
                missing.Add(role);
                continue;
            }

            if (aggregate!.IsAssignableFrom(resolved))
            {
                bases.Add(role);
            }
        }

        await Assert.That(missing).IsEmpty()
            .Because("every role this gate expects must exist as a type. A missing one means the split was "
                   + "only half done and the aggregate is silently narrower than what its consumers assume. "
                   + "Missing: " + string.Join(", ", missing));

        // Both sides are sorted, so an ordered comparison says the same thing as a
        // set comparison and cannot be satisfied by a reordering.
        bases.Sort(StringComparer.Ordinal);
        await Assert.That(string.Join(",", bases)).IsEqualTo(string.Join(",", RoleNames))
            .Because($"{AggregateName} is the DI alias for the token-tracking roles, so its base list IS the "
                   + "published role set: a role it does not derive from is a role a consumer cannot reach "
                   + "through DI, and a base outside the reviewed set is unreviewed surface riding along.");
    }

    /// <summary>Every role still owns the members it was cut with.</summary>
    [Test]
    public async Task EveryRole_OwnsItsReviewedMembers()
    {
        var missing = new List<string>();
        foreach ((string role, string[] members) in OwnedMembers)
        {
            Type? resolved = Resolve(role);
            if (resolved is null)
            {
                missing.Add($"{role} (type missing)");
                continue;
            }

            foreach (string member in members)
            {
                if (resolved.GetMember(member).Length == 0)
                {
                    missing.Add($"{role}.{member}");
                }
            }
        }

        await Assert.That(missing).IsEmpty()
            .Because("a role that quietly loses a member breaks the consumer that was narrowed onto it, and "
                   + "doing so would also hollow out the leak test below into a rule that finds nothing. "
                   + "Missing: " + string.Join(", ", missing));
    }

    /// <summary>
    ///     No role carries another's members. This is what makes the roles roles and
    ///     not a reshuffle: IUsageRecorder must not be able to answer "should we
    ///     compact", and IUsageStats must not be able to record anything.
    /// </summary>
    [Test]
    public async Task Role_DoesNotLeak_AnotherRolesMembers()
    {
        var leaked = new List<string>();
        foreach ((string owner, string[] members) in OwnedMembers)
        {
            Type? ownerType = Resolve(owner);
            if (ownerType is null)
            {
                leaked.Add($"{owner} (type missing)");
                continue;
            }

            foreach ((string other, string[] otherMembers) in OwnedMembers)
            {
                if (string.Equals(other, owner, StringComparison.Ordinal))
                {
                    continue;
                }

                Type? otherType = Resolve(other);
                if (otherType is null)
                {
                    continue;
                }

                foreach (string member in otherMembers)
                {
                    if (ownerType.GetMember(member).Length > 0)
                    {
                        leaked.Add($"{owner}.{member} (belongs to {other})");
                    }
                }
            }
        }

        await Assert.That(leaked).IsEmpty()
            .Because("each role is the slice its consumer actually holds; a member reachable through more "
                   + "than one role is a member no consumer can be prevented from depending on. Leaked: "
                   + string.Join(", ", leaked));
    }

    /// <summary>
    ///     The roles are mutually independent — no role derives from another. This is
    ///     the #469 JsonThemeLoader trap in the other direction: there, a reader was
    ///     forced to implement an applier it could not honour. Here, a role inheriting
    ///     another would drag the second role's members back into the first.
    /// </summary>
    [Test]
    public async Task Roles_AreMutuallyIndependent()
    {
        var coupled = new List<string>();
        foreach ((string role, _) in OwnedMembers)
        {
            Type? roleType = Resolve(role);
            if (roleType is null)
            {
                continue;
            }

            foreach ((string other, _) in OwnedMembers)
            {
                Type? otherType = Resolve(other);
                if (otherType is null || ReferenceEquals(roleType, otherType))
                {
                    continue;
                }

                if (otherType.IsAssignableFrom(roleType))
                {
                    coupled.Add($"{role} -> {other}");
                }
            }
        }

        await Assert.That(coupled).IsEmpty()
            .Because("a consumer narrowed onto one role must not be handed another by inheritance, or the "
                   + "aggregate it was split out of reassembles itself at the type level. Coupled: "
                   + string.Join(", ", coupled));
    }

    /// <summary>
    ///     Only composition roots name the aggregate. This is the half that makes the
    ///     split reach the callers: a leaf consumer that names <c>ITokenTracker</c> has
    ///     the union in its constructor signature whatever its body happens to touch,
    ///     and every implementor and test double has to satisfy all of it again.
    /// </summary>
    [Test]
    public async Task OnlyCompositionRoots_NameTheAggregate()
    {
        string[] offenders = [.. ProductFilesNamingTheAggregate()
            .Where(static f => !CompositionRoots.ContainsKey(f))];

        await Assert.That(offenders).IsEmpty()
            .Because("a leaf consumer must declare the role it uses — ITokenEstimator to count, "
                   + "IUsageRecorder to record, ICompactionHeuristic to ask about compaction, IUsageStats to "
                   + "read the totals. Naming the aggregate re-couples it to the other roles and re-imposes "
                   + "all four on every implementor and test double. Offenders: "
                   + (offenders.Length == 0 ? "(none)" : string.Join(", ", offenders)));
    }

    /// <summary>
    ///     Non-vacuity for the scan, and the anti-rot check for the allowlist. A scan
    ///     that matched nothing would satisfy
    ///     <see cref="OnlyCompositionRoots_NameTheAggregate" /> perfectly, and an
    ///     allowlist whose rows no longer exist enforces nothing. So both directions
    ///     are asserted: the aggregate really is out there, and every reviewed
    ///     composition root still names it.
    /// </summary>
    [Test]
    public async Task CompositionRoots_AreAllStillNamed()
    {
        HashSet<string> actual = [.. ProductFilesNamingTheAggregate()];

        await Assert.That(actual.Count).IsGreaterThan(0)
            .Because("the scan found no product file naming ITokenTracker. Either the aggregate moved out of "
                   + "src/+apps/ or this scan stopped matching — and a rule that matches nothing is green.");

        string[] stale = [.. CompositionRoots.Keys
            .Where(k => !actual.Contains(k))
            .OrderBy(static k => k, StringComparer.Ordinal)];

        await Assert.That(stale).IsEmpty()
            .Because("each allowlisted row was justified by what that file does with the aggregate. A row that "
                   + "no longer names it is either a stale entry to delete, or a composition root that has "
                   + "started bypassing the roles. Stale: " + string.Join(", ", stale));
    }
}
