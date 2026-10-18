namespace Harbor.Application;

/// <summary>
///     Single parsing point for the front-matter field reads shared by
///     <see cref="Harbor.Application.Sessions.WorkspaceContextSource" /> and
///     <see cref="Harbor.Application.Skills.SkillFreshnessSeeder" />: the
///     <c>line.TrimStart() → StartsWith("name:", OrdinalIgnoreCase) →
///     trimmed[prefix.Length..].Trim() → empty → null</c> chain used to live
///     verbatim in three call sites (issue #1580) — a prefix-syntax or
///     trim-policy change would have to be applied in two files in sync.
/// </summary>
public static class FrontMatter
{
    /// <summary>
    ///     Value of a top-level front-matter field on <paramref name="line" />:
    ///     "<c>&lt;fieldName&gt;:</c> value" — any leading indentation is
    ///     tolerated and the prefix is matched case-insensitively. Returns
    ///     <c>null</c> when the line carries a different field, or the value
    ///     is absent/empty; trimming of the extracted value is inclusive.
    /// </summary>
    public static string? GetFrontMatterField(string line, string fieldName)
    {
        string trimmed = line.TrimStart();
        if (!trimmed.StartsWith(fieldName + ":", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string value = trimmed[(fieldName.Length + 1)..].Trim();
        return value.Length == 0 ? null : value;
    }
}
