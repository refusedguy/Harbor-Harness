namespace Harbor.Application.Hooks;

/// <summary>
///     User-hook event points (PX4, slice 1). Each name is a key in
///     <c>~/.harbor/hooks.json</c> under <c>"hooks"</c> and a call site on
///     <see cref="IHookRunner" />. Claude-parity naming.
/// </summary>
public static class HookEvents
{
    /// <summary>Runs before a tool executes; may allow, deny, ask, or edit args.</summary>
    public const string PreToolUse = "PreToolUse";

    /// <summary>Runs after a tool executes; advisory, the verdict is ignored.</summary>
    public const string PostToolUse = "PostToolUse";

    /// <summary>Runs once when an agent run ends (normal or cancelled); advisory.</summary>
    public const string SessionEnd = "SessionEnd";
}
