// JsoncConfigParser.cs: #2680 F167. The parse half of "JSONC config": the
// ability to read ~/.harbor/config.json when it carries // comments, /* */
// comments or trailing commas. System.Text.Json already implements both JSONC
// extensions through JsonDocumentOptions, so this leaf does no preprocessing
// of its own: no comment-stripping regex, no third-party package and no call
// into the config store. It reads no file, consults no clock and changes no
// product path. It only turns untrusted text into a validated root element.

namespace Harbor.Tools.Builtin;

/// <summary>
///     Parses JSONC configuration text into a detached JSON root (#2680 F167).
///     Pure, stateless and thread-safe.
/// </summary>
/// <remarks>
///     <para>
///         Both JSONC extensions are delegated to the SDK: comments are
///         skipped (<see cref="JsonCommentHandling.Skip" />) and trailing
///         commas are allowed. Because the reader handles them at the token
///         level, a comment marker inside a string literal is data, not a
///         comment.
///     </para>
///     <para>
///         A configuration root must be a JSON object. Arrays, scalars and
///         <c>null</c> are rejected so a caller can never mistake a valid
///         JSON document for a usable configuration.
///     </para>
///     <para>
///         The returned element is a deep clone and outlives the parse; the
///         caller does not have to dispose a <see cref="JsonDocument" />.
///     </para>
/// </remarks>
public static class JsoncConfigParser
{
    /// <summary>
    ///     The reader options shared by every parse. Exposed so callers that
    ///     deserialize with <c>JsonSerializer</c> can accept the same syntax.
    /// </summary>
    public static JsonDocumentOptions Options { get; } = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    ///     Parses <paramref name="jsonc" /> into a detached root element.
    /// </summary>
    /// <param name="jsonc">JSON or JSONC text; a leading UTF-8 BOM is ignored.</param>
    /// <returns>
    ///     Success with the root object, or a failure describing why the text
    ///     is not a usable configuration. No exception escapes this method.
    /// </returns>
    public static Result<JsonElement> Parse(string? jsonc)
    {
        if (string.IsNullOrWhiteSpace(jsonc))
            return Result.Failure<JsonElement>("Configuration is empty.");

        string text = jsonc.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(text))
            return Result.Failure<JsonElement>("Configuration is empty.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(text, Options);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Result.Failure<JsonElement>("Configuration root must be a JSON object.");

            return Result.Success(document.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            return Result.Failure<JsonElement>($"Invalid configuration JSON: {ex.Message}");
        }
    }

    /// <summary>
    ///     Parses <paramref name="jsonc" /> without throwing.
    /// </summary>
    /// <param name="jsonc">JSON or JSONC text.</param>
    /// <param name="root">
    ///     The detached root object on success, or <see langword="default" /> on
    ///     failure (<see cref="JsonValueKind.Undefined" />).
    /// </param>
    /// <returns><see langword="true" /> when a configuration root was produced.</returns>
    public static bool TryParse(string? jsonc, out JsonElement root)
    {
        Result<JsonElement> parsed = Parse(jsonc);
        if (parsed.IsSuccess)
        {
            root = parsed.Value;
            return true;
        }

        root = default;
        return false;
    }
}
