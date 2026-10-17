namespace Harbor.Application.Configuration;

/// <summary>
///     Shared pick helpers for the onboarding interactive flows (#1577).
/// </summary>
/// <remarks>
///     <para>
///         The numeric-pick pattern <c>int.TryParse → 1..list.Count → list[idx-1]</c>
///         used to be hand-copied per list, and trim discipline drifted along the
///         way: the live-list tail trimmed before prefixing while the free-text
///         fallback did not (#1577), so the same whitespace typed at two different
///         prompts produced a clean id in one path and a padded one in the other.
///     </para>
///     <para>
///         Lives next to <see cref="OnboardingWizard" /> so both call sites stay
///         in one place; the CellForge <c>OnboardingFlow</c> copies keep their own
///         sites until a seam visible outside <c>Harbor.Application</c> exists
///         (#1577 records the follow-up).
///     </para>
/// </remarks>
internal static class OnboardingPicks
{
    /// <summary>
    ///     Parse <paramref name="input" /> as a 1-based index into <paramref name="list" />.
    ///     Trims the input once, here, instead of at every printed-numbers prompt.
    /// </summary>
    /// <typeparam name="T">The type of the items being numbered.</typeparam>
    /// <param name="input">The raw user input; may be <c>null</c>.</param>
    /// <param name="list">The list whose printed <c>[1]</c>..<c>[N]</c> numbering is being answered.</param>
    /// <param name="picked">The selected item, valid only when the return is <c>true</c>.</param>
    /// <returns><c>true</c> when <paramref name="input" /> is a number inside <c>1</c>..<c>list.Count</c>.</returns>
    public static bool TryPickByNumber<T>(string? input, IReadOnlyList<T> list, out T picked)
    {
        picked = default!;
        string? trimmed = input?.Trim();
        if (trimmed is null || list.Count == 0 || !int.TryParse(trimmed, out int idx))
            return false;
        if (idx < 1 || idx > list.Count)
            return false;
        picked = list[idx - 1];
        return true;
    }

    /// <summary>
    ///     Normalize a model the user typed free-text: trim once, then prepend
    ///     <c>providerId/</c> unless it is already provider-qualified (has a
    ///     slash). Shared by the live-list tail and the free-text fallback so the
    ///     two prompts stop disagreeing about whitespace (#1577).
    /// </summary>
    /// <param name="input">The raw model id or bare model name; may be <c>null</c>.</param>
    /// <param name="providerId">The provider id to prefix a bare name with.</param>
    /// <returns>The trimmed, provider-qualified model id.</returns>
    public static string NormalizeModelId(string? input, string providerId)
    {
        string trimmed = input?.Trim() ?? string.Empty;
        return trimmed.Contains('/') ? trimmed : $"{providerId}/{trimmed}";
    }
}
