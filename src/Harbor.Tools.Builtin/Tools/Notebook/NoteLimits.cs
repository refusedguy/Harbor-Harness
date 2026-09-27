namespace Harbor.Tools.Builtin;

/// <summary>
///     Quota limits shared by notebook validation and commands. Kept in one
///     place so the requirement matrix and the enforcement sites cannot drift.
/// </summary>
internal static class NoteLimits
{
    /// <summary>Maximum note content length in characters.</summary>
    internal const int MaxContentChars = 16_384;

    /// <summary>Maximum note key length in characters.</summary>
    internal const int MaxKeyChars = 128;

    /// <summary>Maximum notes stored per session.</summary>
    internal const int MaxNotesPerSession = 256;
}
