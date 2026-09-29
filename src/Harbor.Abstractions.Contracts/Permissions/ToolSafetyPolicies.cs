namespace Harbor.Abstractions.Permissions;

/// <summary>
///     How the permission system must treat a tool's rule-matched argument
///     (issue #557). Declared by the tool itself, on <c>ITool.SafetyProfile</c>.
/// </summary>
/// <remarks>
///     <para>
///         The permission system used to be fail-open in a way only a hand-maintained
///         name list could keep closed. A path-taking tool missing from
///         <see cref="PathGuardSafetyPolicy" />'s tool set gets
///         <c>AppliesTo() == false</c>, so the suppression that stops
///         <c>new("write", "src/*", Allow)</c> from authorising
///         <c>src/../../../etc/passwd</c> never runs — a permission bypass that
///         nothing reports.
///     </para>
///     <para>
///         This type is the declaration that removes the list: the policies a
///         ruleset consults are built from what tools actually registered
///         (<see cref="ToolSafetyPolicies.Build" />), so there is nothing to forget
///         to update. It lives on <c>ITool</c> as a REQUIRED member — a tool cannot
///         be written without stating what its arguments are.
///     </para>
/// </remarks>
public enum ToolArgKind
{
    /// <summary>
    ///     The rule-matched argument carries nothing a path/exec guard could reason
    ///     about (a name, a URL, a free-form blob); rules match against <c>"*"</c>.
    ///     This is an EXPLICIT opt-out: picking it for a tool that does take a path
    ///     re-opens #557, and the registration guard test says so.
    /// </summary>
    Opaque,

    /// <summary>
    ///     The rule-matched argument is a workspace-relative file path
    ///     (<c>read</c>, <c>write</c>, <c>edit</c>, <c>patch</c>, <c>ls</c>,
    ///     <c>tree</c>, <c>lsp</c>, …). The traversal guard applies: a glob Allow
    ///     rule must not authorise a rooted or <c>..</c>-bearing argument.
    /// </summary>
    Path,

    /// <summary>
    ///     The rule-matched argument is a shell command (<c>bash</c>, a git tool).
    ///     The destructive-command deny and the token-wise allow prefix apply; the
    ///     path traversal guard does not (a command is not a path).
    /// </summary>
    Command
}

/// <summary>
///     A tool's declaration of how its arguments are matched against
///     <see cref="PermissionRuleset" /> rules — the self-declared replacement for
///     the hand-maintained tool-name lists audited in issue #557.
/// </summary>
/// <param name="ArgKind">Which argument-safety strategy applies to this tool.</param>
/// <param name="ArgumentName">
///     The JSON property holding the rule-matched argument (<c>"path"</c> for a
///     file tool, <c>"command"</c> for a shell tool). <see langword="null" /> for
///     <see cref="ToolArgKind.Opaque" />, where rules match against <c>"*"</c>.
/// </param>
public sealed record ToolSafetyProfile(ToolArgKind ArgKind, string? ArgumentName)
{
    /// <summary>
    ///     The tool takes no rule-matched argument worth guarding: a name, a URL, a
    ///     free-form blob. Rules are matched against <c>"*"</c>.
    /// </summary>
    public static ToolSafetyProfile Opaque { get; } = new(ToolArgKind.Opaque, null);

    /// <summary>
    ///     The rule-matched argument is a workspace-relative file path held in
    ///     <paramref name="argumentName" /> (<c>"path"</c> for every builtin file
    ///     tool). The traversal guard applies.
    /// </summary>
    /// <param name="argumentName">The JSON property carrying the path.</param>
    public static ToolSafetyProfile Path(string argumentName = "path") => new(ToolArgKind.Path, argumentName);

    /// <summary>
    ///     The rule-matched argument is a shell command held in
    ///     <paramref name="argumentName" /> (<c>"command"</c>). The
    ///     destructive-command deny and the token-wise allow prefix apply; the path
    ///     traversal guard does not.
    /// </summary>
    /// <param name="argumentName">The JSON property carrying the command.</param>
    public static ToolSafetyProfile Command(string argumentName = "command") => new(ToolArgKind.Command, argumentName);
}

/// <summary>
///     One tool's name paired with the safety profile it declares — the unit
///     <see cref="ToolSafetyPolicies" /> consumes to assemble the
///     <see cref="IArgSafetyPolicy" /> set a ruleset consults.
/// </summary>
/// <param name="ToolName">The tool's stable name (case-insensitive at lookup).</param>
/// <param name="Profile">The safety profile the tool declares.</param>
public sealed record ToolSafetyDeclaration(string ToolName, ToolSafetyProfile Profile)
{
    /// <summary>
    ///     The approval class this tool belongs to, or <see langword="null" /> when it
    ///     belongs to none — a tool no category rule should ever match.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This rides along on the declaration rather than introducing a second
    ///         one. A sibling <c>ToolAttribute</c> record would be the same names
    ///         written down twice, and issue #595 is precisely that failure mode: a
    ///         second table, kept in step by hand, that a new tool joins one table of
    ///         and not the other.
    ///     </para>
    ///     <para>
    ///         It is OPTIONAL on purpose. <see cref="ToolSafetyProfile" /> is a
    ///         required member of <c>ITool</c> because a tool cannot exist without
    ///         saying what its arguments are — there is no safe default for that. A
    ///         category has one: "belongs to no class", which is fail-closed, because
    ///         an unclassified tool simply does not match a category rule. Making it
    ///         required would add a second mandatory member for a fact that is
    ///         genuinely optional, and would have forced a decision on tools that
    ///         have no business in a category at all.
    ///     </para>
    /// </remarks>
    public ToolCategory? Category { get; init; }
}

/// <summary>
///     Builds the <see cref="IArgSafetyPolicy" /> set a ruleset consults from what
///     tools actually registered, instead of from a hand-maintained list of names
///     (issue #557).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="PathGuardSafetyPolicy" /> used to carry a private
///         <c>DefaultTools</c> literal that a new path-taking write-tool silently
///         failed to join; the omission was a permission bypass with no test, no
///         warning and no compile error. The tool set is now derived: every
///     <see cref="ToolArgKind.Path" /> tool that registered is guarded, every
///     <see cref="ToolArgKind.Command" /> tool is deny-listed against destructive
///     commands, and <see cref="ToolArgKind.Opaque" /> tools are deliberately
///     excluded.
///     </para>
///     <para>
///         <b>Fails loud, by construction.</b> A blank tool name, a blank
///         argument name on a guarded tool, or two declarations of the same tool
///         with different profiles throw <see cref="InvalidOperationException" />
///         here rather than producing a policy set that quietly guards nothing.
///     </para>
/// </remarks>
public static class ToolSafetyPolicies
{
    /// <summary>
    ///     Assembles the safety policies implied by <paramref name="declarations" />.
    /// </summary>
    /// <param name="declarations">
    ///     One entry per tool, normally read straight off the tool registry
    ///     (<c>tool.SafetyProfile</c>).
    /// </param>
    /// <returns>
    ///     Zero, one or two policies: a <see cref="BashSafetyPolicy" /> over the
    ///     command tools and a <see cref="PathGuardSafetyPolicy" /> over the path
    ///     tools, in that order.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     A declaration is blank, declares a blank argument name for a guarded
    ///     kind, or contradicts an earlier declaration of the same tool. A
    ///     contradictory set must not degrade into a weaker policy.
    /// </exception>
    public static IReadOnlyList<IArgSafetyPolicy> Build(IEnumerable<ToolSafetyDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);

        List<string>? pathTools = null;
        List<string>? commandTools = null;
        var seen = new Dictionary<string, ToolSafetyProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (ToolSafetyDeclaration declaration in declarations)
        {
            string name = declaration.ToolName;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    "[tool-safety] a tool declared a safety profile with a blank name — a "
                    + "guard cannot be attached to it.");
            }

            ToolSafetyProfile profile = declaration.Profile;
            if (profile is null)
            {
                throw new InvalidOperationException(
                    $"[tool-safety] tool '{name}' has no safety profile. Every ITool must declare one.");
            }

            if (profile.ArgKind is ToolArgKind.Path or ToolArgKind.Command
                && string.IsNullOrWhiteSpace(profile.ArgumentName))
            {
                throw new InvalidOperationException(
                    $"[tool-safety] tool '{name}' declares a {profile.ArgKind} argument with a blank name.");
            }

            if (seen.TryGetValue(name, out ToolSafetyProfile? previous))
            {
                if (!previous.Equals(profile))
                {
                    throw new InvalidOperationException(
                        $"[tool-safety] tool '{name}' is declared twice with conflicting profiles "
                        + $"({previous.ArgKind} then {profile.ArgKind}).");
                }

                continue;
            }

            seen.Add(name, profile);

            switch (profile.ArgKind)
            {
                case ToolArgKind.Path:
                    (pathTools ??= []).Add(name);
                    break;
                case ToolArgKind.Command:
                    (commandTools ??= []).Add(name);
                    break;
                default:
                    break;
            }
        }

        var policies = new List<IArgSafetyPolicy>(2);
        if (commandTools is { Count: > 0 })
        {
            policies.Add(new BashSafetyPolicy(commandTools));
        }

        if (pathTools is { Count: > 0 })
        {
            policies.Add(new PathGuardSafetyPolicy(pathTools));
        }

        return policies;
    }
}
