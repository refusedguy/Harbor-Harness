using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Harbor.Tui.CellForge.Generic.Forms;

/// <summary>
///     Editing behaviour of a form row. The primitive never interprets a
///     business scenario; it only decides how a declared value is edited.
/// </summary>
public enum FieldKind
{
    /// <summary>Plain single-line text.</summary>
    Text,

    /// <summary>Single-line text whose display is replaced by a mask rune.</summary>
    Masked,
}

/// <summary>
///     Everything a reusable rule may read: the row's own value and a snapshot
///     of every other row value, keyed by field id.
/// </summary>
/// <param name="FieldId">Id of the row being validated.</param>
/// <param name="Value">Current text of that row.</param>
/// <param name="Values">Snapshot of all row texts, including this one.</param>
public readonly record struct FieldValidationContext(
    string FieldId,
    string Value,
    IReadOnlyDictionary<string, string> Values);

/// <summary>
///     A domain-free validation rule. Implementations return an error message
///     or <see langword="null"/> when the value is acceptable.
/// </summary>
public interface IFieldValidator
{
    /// <summary>Stable rule name, useful for diagnostics.</summary>
    string Name { get; }

    /// <summary>Evaluates the rule against one field snapshot.</summary>
    /// <param name="context">Value and sibling values.</param>
    /// <returns>An error message, or <see langword="null"/> when valid.</returns>
    string? Validate(in FieldValidationContext context);
}

/// <summary>
///     Declarative description of one form row: identity, label, editing kind,
///     optional mask and the ordered rules that validate it.
/// </summary>
/// <remarks>
///     A host supplies descriptors from any scenario. The form container, the
///     masked input and the error display consume this one type; no scenario
///     name appears in the primitive surface.
/// </remarks>
public sealed record FieldDescriptor
{
    /// <summary>Creates a field description.</summary>
    /// <param name="id">Stable id used for values, errors and cross-field rules.</param>
    /// <param name="label">Text painted in the label column.</param>
    /// <param name="kind">Editing behaviour of the row.</param>
    /// <param name="mask">Replacement rune for masked rows; null prints the value.</param>
    /// <param name="maxLength">Maximum rune count; zero means unlimited.</param>
    /// <param name="placeholder">Hint painted while the value is empty.</param>
    /// <param name="validators">Ordered rules; empty means no constraints.</param>
    public FieldDescriptor(
        string id,
        string label,
        FieldKind kind = FieldKind.Text,
        Rune? mask = null,
        int maxLength = 0,
        string placeholder = "",
        IReadOnlyList<IFieldValidator>? validators = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Label = label ?? string.Empty;
        Kind = kind;
        Mask = mask;
        MaxLength = Math.Max(0, maxLength);
        Placeholder = placeholder ?? string.Empty;
        Validators = validators ?? Array.Empty<IFieldValidator>();
    }

    /// <summary>Stable id used for values, errors and cross-field rules.</summary>
    public string Id { get; }

    /// <summary>Text painted in the label column.</summary>
    public string Label { get; }

    /// <summary>Editing behaviour of the row.</summary>
    public FieldKind Kind { get; }

    /// <summary>Replacement rune for masked rows; null prints the value.</summary>
    public Rune? Mask { get; }

    /// <summary>Maximum rune count; zero means unlimited.</summary>
    public int MaxLength { get; }

    /// <summary>Hint painted while the value is empty.</summary>
    public string Placeholder { get; }

    /// <summary>Ordered rules; empty means no constraints.</summary>
    public IReadOnlyList<IFieldValidator> Validators { get; }
}

/// <summary>
///     Reusable rule factories. Every rule is pure, BCL-only and holds no host
///     or scenario state, so the same instance works in any form.
/// </summary>
public static class FieldValidators
{
    /// <summary>Rejects an empty or whitespace-only value.</summary>
    /// <param name="message">Error text.</param>
    /// <returns>A required-value rule.</returns>
    public static IFieldValidator Required(string message = "Value is required.") =>
        new RequiredRule(message);

    /// <summary>Rejects values shorter than <paramref name="minimum"/> runes.</summary>
    /// <param name="minimum">Inclusive lower bound.</param>
    /// <param name="message">Optional error text.</param>
    /// <returns>A minimum-length rule.</returns>
    public static IFieldValidator MinLength(int minimum, string? message = null) =>
        new LengthRule(minimum, int.MaxValue, message ?? $"Use at least {minimum} characters.");

    /// <summary>Rejects values longer than <paramref name="maximum"/> runes.</summary>
    /// <param name="maximum">Inclusive upper bound.</param>
    /// <param name="message">Optional error text.</param>
    /// <returns>A maximum-length rule.</returns>
    public static IFieldValidator MaxLength(int maximum, string? message = null) =>
        new LengthRule(0, maximum, message ?? $"Use at most {maximum} characters.");

    /// <summary>Rejects values that do not match a BCL regular expression.</summary>
    /// <param name="pattern">Pattern passed to <see cref="Regex"/>.</param>
    /// <param name="message">Optional error text.</param>
    /// <returns>A pattern rule.</returns>
    public static IFieldValidator Matches(string pattern, string? message = null) =>
        new PatternRule(pattern, message ?? "Value has an invalid format.");

    /// <summary>Rejects a value that differs from another row's value.</summary>
    /// <param name="otherFieldId">Id of the row to compare with.</param>
    /// <param name="message">Optional error text.</param>
    /// <returns>A cross-field equality rule.</returns>
    public static IFieldValidator MatchesField(string otherFieldId, string? message = null) =>
        new EqualityRule(otherFieldId, message ?? "Values must match.");

    private sealed class RequiredRule(string message) : IFieldValidator
    {
        public string Name => "required";

        public string? Validate(in FieldValidationContext context) =>
            string.IsNullOrWhiteSpace(context.Value) ? message : null;
    }

    private sealed class LengthRule(int minimum, int maximum, string message) : IFieldValidator
    {
        public string Name => minimum > 0 ? "minLength" : "maxLength";

        public string? Validate(in FieldValidationContext context)
        {
            int length = context.Value.Length;
            return length < minimum || length > maximum ? message : null;
        }
    }

    private sealed class PatternRule : IFieldValidator
    {
        private readonly Regex _regex;
        private readonly string _message;

        internal PatternRule(string pattern, string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
            _regex = new Regex(pattern, RegexOptions.CultureInvariant);
            _message = message;
        }

        public string Name => "pattern";

        public string? Validate(in FieldValidationContext context) =>
            _regex.IsMatch(context.Value) ? null : _message;
    }

    private sealed class EqualityRule(string otherFieldId, string message) : IFieldValidator
    {
        public string Name => "matchesField";

        public string? Validate(in FieldValidationContext context) =>
            context.Values.TryGetValue(otherFieldId, out string? other)
            && string.Equals(context.Value, other, StringComparison.Ordinal)
                ? null
                : message;
    }
}
