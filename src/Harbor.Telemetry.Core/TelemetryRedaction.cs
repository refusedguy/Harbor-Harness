using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Telemetry;

/// <summary>
///     The single gate every exported telemetry value passes through
///     (#1741 — the first slice of the ledger mirror: a redaction waterfall).
/// </summary>
/// <remarks>
///     <para>
///         <b>One choke point.</b> <see cref="TelemetryTextExporter" /> never
///         turns a value into text on its own: every attribute it writes goes
///         through <see cref="Apply" />, and every name it writes goes through
///         <see cref="SanitizeName" />. There is no second path and no
///         "export raw" switch — a value that is not describable by this gate
///         does not reach the sink.
///     </para>
///     <para>
///         <b>Fail-closed by default.</b> The gate redacts rather than
///         decides:
///         <list type="bullet">
///             <item>
///                 <term>sensitive key</term>
///                 <description>
///                     a key that names a credential (<c>api_key</c>,
///                     <c>authorization</c>, <c>client_secret</c>, …) is
///                     redacted whatever its value is — see
///                     <see cref="IsSensitiveKey" />.
///                 </description>
///             </item>
///             <item>
///                 <term>secret-shaped value</term>
///                 <description>
///                     a value that carries a known credential prefix
///                     (<c>sk-…</c>, <c>ghp_…</c>, <c>AKIA…</c>, a JWT, …) or
///                     that assigns a credential by name is redacted even
///                     under a benign key — see <see cref="LooksLikeSecret" />.
///                 </description>
///             </item>
///             <item>
///                 <term>unknown type</term>
///                 <description>
///                     a value of a type outside the known set is summarised by
///                     its type name (<c>[MyType]</c>), never rendered. An
///                     arbitrary object graph may hold anything.
///                 </description>
///             </item>
///             <item>
///                 <term>failure</term>
///                 <description>
///                     a value that cannot even be described is redacted; the
///                     gate never lets an exception turn into "export raw".
///                 </description>
///             </item>
///         </list>
///     </para>
///     <para>
///         This is deliberately NOT the whole #1741 waterfall. The waterfall
///         (per-source rules, an allow-list of exporters, a ledger of what was
///         withheld) is still open; this class is the one place every exported
///         byte must pass through, so the waterfall has a seam to grow into
///         instead of a second, parallel decision point.
///     </para>
/// </remarks>
public static class TelemetryRedaction
{
    /// <summary>What a withheld value is replaced with. Never contains any part of the value.</summary>
    public const string Redacted = "[redacted]";

    /// <summary>Default cap on the length of one exported value, in characters.</summary>
    public const int DefaultMaxValueLength = 512;

    /// <summary>Default cap on the length of one exported name (span or instrument), in characters.</summary>
    public const int DefaultMaxNameLength = 128;

    /// <summary>Substring markers that make a key sensitive, compared against the flattened key.</summary>
    private static readonly string[] SensitiveKeyMarkers =
    [
        "apikey",
        "password",
        "passwd",
        "passphrase",
        "secret",
        "accesstoken",
        "refreshtoken",
        "idtoken",
        "authtoken",
        "authorization",
        "credential",
        "privatekey",
        "sessionkey",
        "cookie",
        "bearer",
    ];

    /// <summary>
    ///     Key names that are sensitive on their own. Matched against the LAST
    ///     segment only, so <c>token.type</c> (a legitimate tag) stays visible
    ///     while a bare <c>token</c> does not.
    /// </summary>
    private static readonly string[] SensitiveLastSegments =
    [
        "token",
        "secret",
        "password",
        "passwd",
        "passphrase",
        "credential",
        "credentials",
        "authorization",
        "cookie",
    ];

    /// <summary>Prefixes that mark a value as a credential, whatever key it sits under.</summary>
    private static readonly string[] SecretPrefixes =
    [
        "sk-",
        "sk_",
        "sk-ant-",
        "sk-proj-",
        "ghp_",
        "gho_",
        "ghu_",
        "ghs_",
        "ghr_",
        "github_pat_",
        "glpat-",
        "xoxb-",
        "xoxp-",
        "xoxa-",
        "xoxr-",
        "xokp-",
        "AKIA",
        "ASIA",
        "AIza",
        "ya29.",
        "eyJ",
        "hf_",
        "npm_",
        "pypi-",
        "dop_v1_",
        "klo_",
        "r8_",
        "rsk_",
        "SG.",
        "-----BEGIN",
        "Bearer ",
        "Basic ",
    ];

    /// <summary>
    ///     A credential assigned by name inside a free-form string
    ///     (<c>api_key=…</c>, <c>"password": "…"</c>).
    /// </summary>
    private static readonly Regex SecretAssignment = new(
        @"\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|auth[_-]?token|secret|password|passwd|passphrase|authorization|bearer|credential|tokens?)\b\s*[:=]\s*\S+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    ///     Decide what may be exported for one attribute, and render it.
    /// </summary>
    /// <param name="key">Attribute name — the decision input.</param>
    /// <param name="value">Attribute value — rendered only if the gate allows it.</param>
    /// <param name="maxValueLength">Cap on the rendered value, in characters.</param>
    /// <returns>
    ///     The text to export: the value itself, <see cref="Redacted" /> when
    ///     the gate withholds it, or a type-name summary for an unknown type.
    /// </returns>
    public static string Apply(string key, object? value, int maxValueLength = DefaultMaxValueLength)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (IsSensitiveKey(key))
        {
            return Redacted;
        }

        return RenderValue(value, maxValueLength);
    }

    /// <summary>
    ///     Whether a key names a credential. The key is flattened (lower-cased,
    ///     separators removed) before the marker list is consulted, so
    ///     <c>api_key</c>, <c>apiKey</c> and <c>x-api-key</c> all match; a bare
    ///     <c>token</c> matches on the last segment, while <c>token.type</c> and
    ///     <c>session.id</c> do not.
    /// </summary>
    /// <param name="key">Attribute name.</param>
    /// <returns><see langword="true" /> when the key must never be exported.</returns>
    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        string flattened = Flatten(key);
        foreach (string marker in SensitiveKeyMarkers)
        {
            if (flattened.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return SensitiveLastSegments.Contains(LastSegment(key), StringComparer.Ordinal);
    }

    /// <summary>
    ///     Whether a value looks like a credential, independently of the key it
    ///     sits under: a known credential prefix, or a credential assigned by
    ///     name inside the text.
    /// </summary>
    /// <param name="value">Candidate value.</param>
    /// <returns><see langword="true" /> when the value must never be exported.</returns>
    public static bool LooksLikeSecret(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string trimmed = value.TrimStart();
        foreach (string prefix in SecretPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return SecretAssignment.IsMatch(trimmed);
    }

    /// <summary>
    ///     Render an exported NAME (a span name, an instrument name, an
    ///     attribute key). Names are code-defined, so they are sanitized rather
    ///     than redacted: control characters become <c>_</c> and the length is
    ///     capped. The sensitive-key rule does NOT apply here — a metric
    ///     called <c>llm.tokens</c> must stay legible.
    /// </summary>
    /// <param name="name">Name to export.</param>
    /// <param name="maxNameLength">Cap on the rendered name, in characters.</param>
    /// <returns>The sanitized name.</returns>
    public static string SanitizeName(string name, int maxNameLength = DefaultMaxNameLength)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "unnamed";
        }

        int limit = Math.Min(name.Length, maxNameLength);
        StringBuilder builder = new(limit);
        for (int i = 0; i < limit; i++)
        {
            char c = name[i];
            builder.Append(char.IsControl(c) ? '_' : c);
        }

        return builder.ToString();
    }

    /// <summary>Render one value, or withhold it.</summary>
    private static string RenderValue(object? value, int maxValueLength)
    {
        try
        {
            switch (value)
            {
                case null:
                    return "null";
                case string text:
                    return RenderText(text, maxValueLength);
                case bool flag:
                    return flag ? "true" : "false";
                case byte or sbyte or short or ushort or int or uint or long or ulong
                    or float or double or decimal:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? Redacted;
                case char character:
                    return Escape(character.ToString());
                case Guid id:
                    return id.ToString("D", CultureInfo.InvariantCulture);
                case DateTime timestamp:
                    return timestamp.ToString("O", CultureInfo.InvariantCulture);
                case DateTimeOffset timestamp:
                    return timestamp.ToString("O", CultureInfo.InvariantCulture);
                case TimeSpan duration:
                    return duration.ToString("c", CultureInfo.InvariantCulture);
                case Enum enumeration:
                    return enumeration.ToString();
                default:
                    // Fail-closed: an arbitrary object is summarised by its type
                    // name, never rendered — its graph may hold anything.
                    return "[" + value!.GetType().Name + "]";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Fail-closed: a value we cannot even describe is not exported.
            return Redacted;
        }
    }

    /// <summary>Render one string value: withhold it if it is secret-shaped, else escape and cap it.</summary>
    private static string RenderText(string text, int maxValueLength)
    {
        if (LooksLikeSecret(text))
        {
            return Redacted;
        }

        // Clip BEFORE escaping so the cap counts source characters and an
        // escape sequence is never cut in half.
        string clipped = text.Length <= maxValueLength ? text : text[..maxValueLength];
        return Escape(clipped);
    }

    /// <summary>
    ///     Escape a rendered value so one record stays on one line and cannot
    ///     smuggle a separator into the next field.
    /// </summary>
    private static string Escape(string text)
    {
        StringBuilder builder = new(text.Length + 8);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Lower-case the key and drop every separator, so marker matching is spelling-independent.</summary>
    private static string Flatten(string key)
    {
        StringBuilder builder = new(key.Length);
        foreach (char c in key)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>The last dot/dash/underscore separated segment of the key, lower-cased.</summary>
    private static string LastSegment(string key)
    {
        int end = key.Length;
        for (int i = key.Length - 1; i >= 0; i--)
        {
            char c = key[i];
            if (c is '.' or '-' or '_')
            {
                end = i + 1;
                break;
            }
        }

        return key[end..].ToLowerInvariant();
    }
}
