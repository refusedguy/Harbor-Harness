namespace Harbor.ChangeTracking;

/// <summary>
///     Shared run-id validation for the verified-change pipeline. A run id must
///     be a bare file name so it cannot escape the runs root when combined into
///     a path. The error text is the one the pipeline has always returned.
/// </summary>
internal static class RunIdValidation
{
    internal static Result<string> ValidateRunId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<string>("Run ID cannot be empty.");
        if (value != Path.GetFileName(value) || value is "." or "..")
            return Result.Failure<string>($"Invalid run id '{value}': path separators are not allowed.");
        return Result.Success(value);
    }
}
