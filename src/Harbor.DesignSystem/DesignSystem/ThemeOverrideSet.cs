namespace Harbor.DesignSystem;

/// <summary>
/// Partial theme patch — any slot left null inherits the base theme.
/// Used by <see cref="ThemeOverrideSet" /> for per-component overrides.
/// </summary>
public sealed record PartialTheme(
    RgbColor? Accent = null,
    RgbColor? Success = null,
    RgbColor? Warning = null,
    RgbColor? Error = null,
    RgbColor? Tool = null,
    RgbColor? System = null,
    RgbColor? User = null,
    RgbColor? Background = null,
    RgbColor? Panel = null,
    RgbColor? Surface = null,
    RgbColor? Surface2 = null,
    RgbColor? Border = null,
    RgbColor? Muted = null,
    RgbColor? Text = null)
{
    /// <summary>Empty patch — every slot inherits.</summary>
    public static readonly PartialTheme None = new();

    /// <summary>Applies the patch over <paramref name="base" />; null slots keep the base value.</summary>
    public HarborTheme Merge(HarborTheme @base)
    {
        ArgumentNullException.ThrowIfNull(@base);
        return @base with
        {
            Accent = Accent ?? @base.Accent,
            Success = Success ?? @base.Success,
            Warning = Warning ?? @base.Warning,
            Error = Error ?? @base.Error,
            Tool = Tool ?? @base.Tool,
            System = System ?? @base.System,
            User = User ?? @base.User,
            Background = Background ?? @base.Background,
            Panel = Panel ?? @base.Panel,
            Surface = Surface ?? @base.Surface,
            Surface2 = Surface2 ?? @base.Surface2,
            Border = Border ?? @base.Border,
            Muted = Muted ?? @base.Muted,
            Text = Text ?? @base.Text,
        };
    }
}

/// <summary>
/// Per-component theme overrides (sprint UI-V2 P3.4): named scopes
/// («sidebar», «composer», «status», …) each patch the active theme with a
/// <see cref="PartialTheme" />. Immutable — install a new set via
/// <see cref="TerminalColorPalette.SetOverrides" />; unresolved scopes and an
/// absent set fall through to the unpatched theme.
/// </summary>
public sealed class ThemeOverrideSet
{
    private readonly Dictionary<string, PartialTheme> _scopes;

    public ThemeOverrideSet(IReadOnlyDictionary<string, PartialTheme>? scopes = null) =>
        _scopes = scopes is null ? [] : new Dictionary<string, PartialTheme>(scopes, StringComparer.OrdinalIgnoreCase);

    /// <summary>Registered scope names (order unspecified).</summary>
    public IEnumerable<string> Scopes => _scopes.Keys;

    /// <summary>Returns a copy with <paramref name="patch" /> installed for <paramref name="scope" /> (replaces any previous patch).</summary>
    public ThemeOverrideSet With(string scope, PartialTheme patch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(patch);
        var scopes = new Dictionary<string, PartialTheme>(_scopes, StringComparer.OrdinalIgnoreCase)
        {
            [scope] = patch,
        };
        return new ThemeOverrideSet(scopes);
    }

    /// <summary>True when the scope has a patch.</summary>
    public bool Has(string scope) =>
        !string.IsNullOrWhiteSpace(scope) && _scopes.ContainsKey(scope);

    /// <summary>Patch for a scope or null.</summary>
    public PartialTheme? PatchFor(string? scope) =>
        scope is not null && _scopes.TryGetValue(scope, out var patch) ? patch : null;

    /// <summary>
    /// Effective theme for a scope: the scope's patch merged over
    /// <paramref name="base" />; no scope / no patch → base unchanged.
    /// </summary>
    public HarborTheme Merge(string? scope, HarborTheme @base) =>
        PatchFor(scope)?.Merge(@base) ?? @base;
}
