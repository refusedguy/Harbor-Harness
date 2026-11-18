using System.Globalization;
using CSharpFunctionalExtensions;

namespace Harbor.App.Cli.Commands;

/// <summary>One requested model option, as <c>--option name=value</c>.</summary>
internal sealed record OptionAssignment(string Name, string Value);

/// <summary>The parsed preflight request: which model, and which options to check.</summary>
internal sealed record PreflightRequest(
    string ProviderId,
    string ModelId,
    IReadOnlyList<OptionAssignment> Options);

/// <summary>The capability facts a provider reports for one model.</summary>
internal sealed record ModelOptionProfile(
    bool SupportsToolUse,
    bool SupportsVision,
    bool SupportsReasoning,
    int MaxOutputTokens,
    int ContextWindow);

/// <summary>How one option is checked.</summary>
internal enum OptionKind
{
    Choice,
    Number,
}

/// <summary>The descriptor for one option a model can accept.</summary>
internal sealed record OptionDescriptor(
    string Name,
    OptionKind Kind,
    IReadOnlyList<string> Allowed,
    double Minimum,
    double Maximum);

/// <summary>
///     Pure pre-turn validation of model options (#2686 Q07). The provider's
///     capability profile decides which options exist and which values are
///     legal, so an unsupported choice is rejected locally instead of
///     surfacing as an opaque API error after the turn has started.
/// </summary>
internal static class ProviderOptionPreflight
{
    /// <summary>Bounds on one preflight call so a script cannot flood the command line.</summary>
    internal const int MaxOptions = 32;
    internal const int MaxNameChars = 64;
    internal const int MaxValueChars = 256;

    /// <summary>Build the descriptors a model actually supports from its capability facts.</summary>
    internal static IReadOnlyList<OptionDescriptor> DescriptorsFor(ModelOptionProfile profile)
    {
        var descriptors = new List<OptionDescriptor>
        {
            new("toolUse", OptionKind.Choice, BoolChoices(profile.SupportsToolUse), 0, 0),
            new("vision", OptionKind.Choice, BoolChoices(profile.SupportsVision), 0, 0),
            new("reasoning", OptionKind.Choice, BoolChoices(profile.SupportsReasoning), 0, 0),
            new("temperature", OptionKind.Number, Array.Empty<string>(), 0, 2),
            new("topP", OptionKind.Number, Array.Empty<string>(), 0, 1),
        };

        if (profile.MaxOutputTokens > 0)
        {
            descriptors.Add(new OptionDescriptor(
                "maxOutputTokens", OptionKind.Number, Array.Empty<string>(), 1, profile.MaxOutputTokens));
        }

        return descriptors;
    }

    /// <summary>
    ///     Validate every requested option. All problems are collected into one
    ///     failure message so the caller can fix the whole selection in one pass.
    /// </summary>
    internal static Result Validate(
        ModelOptionProfile profile,
        IReadOnlyList<OptionAssignment> options)
    {
        if (options.Count > MaxOptions)
            return Result.Failure($"at most {MaxOptions} options are allowed.");

        IReadOnlyList<OptionDescriptor> descriptors = DescriptorsFor(profile);
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (OptionAssignment option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Name) || option.Name.Length > MaxNameChars)
            {
                errors.Add("an option name is empty or too long.");
                continue;
            }

            if (option.Value.Length > MaxValueChars)
            {
                errors.Add($"option '{option.Name}' has a value longer than {MaxValueChars} characters.");
                continue;
            }

            if (!seen.Add(option.Name))
            {
                errors.Add($"option '{option.Name}' was supplied more than once.");
                continue;
            }

            OptionDescriptor? descriptor = Find(descriptors, option.Name);
            if (descriptor is null)
            {
                errors.Add($"option '{option.Name}' is not supported by this model.");
                continue;
            }

            if (!IsAllowed(descriptor, option.Value))
            {
                errors.Add(DescribeRejection(descriptor, option));
            }
        }

        return errors.Count == 0
            ? Result.Success()
            : Result.Failure(string.Join(" ", errors));
    }

    /// <summary>Parse the verb's <c>--option name=value</c> tail.</summary>
    internal static Result<PreflightRequest> ParseOptions(string[] args)
    {
        string? providerId = null;
        string? modelId = null;
        var options = new List<OptionAssignment>();

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (token == "--option")
            {
                if (i + 1 >= args.Length)
                    return Result.Failure<PreflightRequest>("--option needs a name=value value.");

                string assignment = args[++i];
                int equals = assignment.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0 || equals == assignment.Length - 1)
                    return Result.Failure<PreflightRequest>($"--option '{assignment}' must be name=value.");

                options.Add(new OptionAssignment(
                    assignment[..equals].Trim(),
                    assignment[(equals + 1)..].Trim()));
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
                return Result.Failure<PreflightRequest>($"unexpected argument '{token}'.");

            if (providerId is null)
            {
                providerId = token;
                continue;
            }

            if (modelId is null)
            {
                modelId = token;
                continue;
            }

            return Result.Failure<PreflightRequest>($"unexpected argument '{token}'.");
        }

        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            return Result.Failure<PreflightRequest>("expected <provider> <model>.");

        return Result.Success(new PreflightRequest(providerId, modelId, options));
    }

    /// <summary>Human-readable JSON-safe summary of what was checked.</summary>
    internal static string FormatResult(string providerId, string modelId, Result outcome)
    {
        string valid = outcome.IsSuccess ? "true" : "false";
        string errors = outcome.IsSuccess ? "[]" : $"[{JsonString(outcome.Error)}]";
        return string.Concat(
            "{\"provider\":", JsonString(providerId),
            ",\"model\":", JsonString(modelId),
            ",\"valid\":", valid,
            ",\"errors\":", errors,
            "}");
    }

    private static IReadOnlyList<string> BoolChoices(bool supported) =>
        supported ? new[] { "true", "false" } : new[] { "false" };

    private static OptionDescriptor? Find(IReadOnlyList<OptionDescriptor> descriptors, string name)
    {
        foreach (OptionDescriptor descriptor in descriptors)
        {
            if (string.Equals(descriptor.Name, name, StringComparison.OrdinalIgnoreCase)) return descriptor;
        }

        return null;
    }

    private static bool IsAllowed(OptionDescriptor descriptor, string value)
    {
        if (descriptor.Kind == OptionKind.Choice)
        {
            foreach (string allowed in descriptor.Allowed)
            {
                if (string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return false;

        return parsed >= descriptor.Minimum && parsed <= descriptor.Maximum;
    }

    private static string DescribeRejection(OptionDescriptor descriptor, OptionAssignment option) =>
        descriptor.Kind == OptionKind.Choice
            ? $"option '{option.Name}' must be one of {string.Join(", ", descriptor.Allowed)}."
            : $"option '{option.Name}' must be a number in "
              + descriptor.Minimum.ToString(CultureInfo.InvariantCulture) + ".."
              + descriptor.Maximum.ToString(CultureInfo.InvariantCulture) + ".";

    private static string JsonString(string value)
    {
        var buffer = new System.Text.StringBuilder(value.Length + 2);
        buffer.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': buffer.Append("\\\""); break;
                case '\\': buffer.Append("\\\\"); break;
                case '\n': buffer.Append("\\n"); break;
                case '\r': buffer.Append("\\r"); break;
                case '\t': buffer.Append("\\t"); break;
                default:
                    if (c < ' ') buffer.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else buffer.Append(c);
                    break;
            }
        }

        buffer.Append('"');
        return buffer.ToString();
    }
}
