// SecretRedactor.cs — the keyless tier's scrubber (#1740).
//
// WHAT COUNTS AS A "KEY"
// ----------------------
// The question this file answers is not "which fields may hold a secret" but
// "which LITERALS are secrets". A field-level rule (redact the tool output,
// keep the user text) is the wrong shape: the agent reads a .env file and the
// credential lands in a TOOL RESULT, the user pastes a key into the PROMPT and
// it lands in the user message, and the model echoes either one back into its
// ANSWER. Every text-bearing field is therefore scrubbed the same way, and the
// rule that decides is a rule about the TEXT.
//
// A literal is a secret when it is one of:
//
//   1. A CREDENTIAL-SHAPED LITERAL — a token whose shape is public knowledge:
//      `sk-…` (OpenAI and everything that copies it), `sk-ant-…` (Anthropic),
//      `klo_…` (Kilocode), `gsk_…` (Groq), `csk-…` (Cerebras), `xai-…`,
//      `AIza…` (Google), `gh?_…` / `github_pat_…` (GitHub), `hf_…`
//      (HuggingFace), `xox?-…` (Slack), `npm_…`, `glpat-…` (GitLab),
//      `AKIA…`/`ASIA…` (AWS), `sk_live_…`/`rk_live_…`/`pk_live_…` (Stripe), a
//      JWT (`eyJ….….…`), a PEM private key block, an `Authorization: Bearer …`
//      header.
//
//   2. A LITERAL THE PROCESS ITSELF HOLDS — the value of an environment
//      variable whose NAME names a credential (`*_API_KEY`, `*_TOKEN`,
//      `*_SECRET`, `*_PASSWORD`, …). These are the provider keys Harbor was
//      started with, and the shape rules do not know every provider's shape.
//      The store is given them (see SessionLogEnvironment) and replaces the
//      exact value.
//
//   3. A CREDENTIAL-NAMED ASSIGNMENT — `OPENAI_API_KEY=…`, `"apiKey": "…"`,
//      `password = …`. The NAME says what the value is; the value itself may
//      have any shape at all.
//
// WHAT IS NOT A KEY
// -----------------
// The provider id (`kilocode`), the model id (`tencent/hy3:free`) and the
// base URL of a provider are NOT secrets: they are published in the provider
// catalogue the repository already commits (`providers/*.json`), and a log that
// does not say which model produced it cannot be replayed against anything.
// Session, message and tool-call ids are not secrets either — a replay needs
// them. The absolute working directory names the machine rather than a
// credential, and normalizing paths is a separate axis (see SessionLogTier).
//
// THE PLACEHOLDERS
// ----------------
// A secret is replaced by a STABLE marker, not by a hash and not by nothing:
// the marker says "there was a credential here", it is identical across runs
// (so two recordings of the same scenario diff cleanly), and it is inert —
// no rule below matches it, so scrubbing is idempotent.

using System.Buffers;
using System.Text.RegularExpressions;

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The keyless tier's scrubber: replaces credential literals in everything
///     a session log writes (#1740).
/// </summary>
internal sealed partial class SecretRedactor
{
    /// <summary>Placeholder for a credential with a known shape.</summary>
    internal const string ApiKeyPlaceholder = "<redacted:api-key>";

    /// <summary>Placeholder for an <c>Authorization: Bearer</c> token.</summary>
    internal const string BearerPlaceholder = "<redacted:bearer-token>";

    /// <summary>Placeholder for a JSON web token.</summary>
    internal const string TokenPlaceholder = "<redacted:token>";

    /// <summary>Placeholder for a value a credential-named assignment held.</summary>
    internal const string SecretPlaceholder = "<redacted:secret>";

    /// <summary>Placeholder for a PEM private key block.</summary>
    internal const string PrivateKeyPlaceholder = "<redacted:private-key>";

    /// <summary>
    ///     A literal shorter than this is not worth replacing: a two-character
    ///     "token" is a count, not a credential.
    /// </summary>
    private const int MinimumLiteralLength = 6;

    /// <summary>
    ///     Values that are obviously not credentials even when a credential-ish
    ///     name sits in front of them.
    /// </summary>
    private static readonly HashSet<string> InertValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "none", "nil", "true", "false", "yes", "no", "0", "1",
        "changeme", "change_me", "change-me", "example", "placeholder", "dummy",
        "redacted", "todo", "tbd", "secret", "password", "token", "apikey",
        "your_api_key", "your-api-key", "yourapikey", "your_token", "your-token",
        "yourtoken", "your_password", "your-password", "yourpassword",
    };

    private readonly string[] _literalSecrets;

    /// <summary>
    ///     Create a scrubber.
    /// </summary>
    /// <param name="literalSecrets">
    ///     The credential values the process holds. Occurrences of each are
    ///     replaced; a null or empty set leaves the shape rules alone.
    /// </param>
    internal SecretRedactor(IReadOnlyCollection<string>? literalSecrets)
    {
        if (literalSecrets is null || literalSecrets.Count == 0)
        {
            _literalSecrets = [];
            return;
        }

        List<string> kept = new(literalSecrets.Count);
        foreach (string secret in literalSecrets)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < MinimumLiteralLength)
            {
                continue;
            }

            kept.Add(secret);
        }

        _literalSecrets = [.. kept];
    }

    /// <summary>
    ///     Scrub one message: every text-bearing field is passed through
    ///     <see cref="ScrubText" />, and a tool call's arguments are walked as
    ///     JSON so a scrubbed value stays a value.
    /// </summary>
    /// <param name="message">The message about to be written.</param>
    /// <returns>The scrubbed message, or the same instance when nothing changed.</returns>
    internal AgentMessage Scrub(AgentMessage message) => message switch
    {
        UserMessage user => user with { Content = ScrubText(user.Content) },
        AssistantMessage assistant => assistant with { Parts = ScrubParts(assistant.Parts) },
        ToolResultMessage toolResult => toolResult with { Results = ScrubResults(toolResult.Results) },
        _ => message,
    };

    /// <summary>
    ///     Scrub one piece of text.
    /// </summary>
    /// <param name="text">The text to scrub.</param>
    /// <returns>The scrubbed text; the same instance when there was nothing to scrub.</returns>
    internal string ScrubText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string result = text;

        // The literals this process holds come first: they are the provider's
        // own key, and no shape rule knows every provider's shape.
        for (int i = 0; i < _literalSecrets.Length; i++)
        {
            result = result.Replace(_literalSecrets[i], SecretPlaceholder, StringComparison.Ordinal);
        }

        result = PrivateKeyBlock().Replace(result, PrivateKeyPlaceholder);
        result = BearerToken().Replace(result, BearerPlaceholder);
        result = UrlUserInfo().Replace(
            result,
            match => $"{match.Groups["scheme"].Value}{match.Groups["user"].Value}:{SecretPlaceholder}@");
        result = KnownCredentialShape().Replace(result, ApiKeyPlaceholder);
        result = JsonWebToken().Replace(result, TokenPlaceholder);
        result = CredentialAssignment().Replace(result, ScrubAssignment);
        return result;
    }

    /// <summary>
    ///     One credential-named assignment. The value is replaced; the name,
    ///     the separator and the quoting are kept, so the line still reads as
    ///     the same line.
    /// </summary>
    private static string ScrubAssignment(Match match)
    {
        string value = match.Groups["value"].Value;
        if (!IsSecretValue(value))
        {
            return match.Value;
        }

        string quote = match.Groups["quote"].Value;
        return $"{match.Groups["name"].Value}{match.Groups["separator"].Value}{quote}{SecretPlaceholder}{quote}";
    }

    /// <summary>
    ///     Whether a value behind a credential-ish name is worth replacing.
    ///     A value that reads as a reference (an environment lookup, a shell
    ///     variable) is not a credential, and a value on the inert list is a
    ///     placeholder, not a secret.
    /// </summary>
    private static bool IsSecretValue(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (InertValues.Contains(value))
        {
            return false;
        }

        if (value[0] is '$' or '%')
        {
            return false;
        }

        return !value.Contains("os.environ", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("process.env", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("getenv", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("GetEnvironmentVariable", StringComparison.Ordinal)
            && !value.Contains("env[", StringComparison.Ordinal);
    }

    private IReadOnlyList<ContentPart> ScrubParts(IReadOnlyList<ContentPart> parts)
    {
        if (parts.Count == 0)
        {
            return parts;
        }

        ContentPart[] copy = new ContentPart[parts.Count];
        bool changed = false;
        for (int i = 0; i < parts.Count; i++)
        {
            ContentPart part = parts[i];
            switch (part)
            {
                case TextPart text:
                {
                    string scrubbed = ScrubText(text.Text);
                    if (scrubbed == text.Text)
                    {
                        copy[i] = part;
                        break;
                    }

                    copy[i] = text with { Text = scrubbed };
                    changed = true;
                    break;
                }

                case ThinkingPart thinking:
                {
                    string scrubbed = ScrubText(thinking.Text);
                    if (scrubbed == thinking.Text)
                    {
                        copy[i] = part;
                        break;
                    }

                    copy[i] = thinking with { Text = scrubbed };
                    changed = true;
                    break;
                }

                case ToolCallPart call:
                {
                    // An Undefined element has no document behind it and cannot
                    // be walked; it stays as it is.
                    if (call.Args.ValueKind == JsonValueKind.Undefined)
                    {
                        copy[i] = part;
                        break;
                    }

                    copy[i] = ToolCallPart.Create(call.Id, call.ToolName, ScrubArgs(call.Args));
                    changed = true;
                    break;
                }

                default:
                    copy[i] = part;
                    break;
            }
        }

        return changed ? copy : parts;
    }

    private IReadOnlyList<ToolResultEntry> ScrubResults(IReadOnlyList<ToolResultEntry> results)
    {
        if (results.Count == 0)
        {
            return results;
        }

        ToolResultEntry[] copy = new ToolResultEntry[results.Count];
        bool changed = false;
        for (int i = 0; i < results.Count; i++)
        {
            ToolResultEntry entry = results[i];
            string scrubbed = ScrubText(entry.Output);
            if (scrubbed == entry.Output)
            {
                copy[i] = entry;
                continue;
            }

            copy[i] = entry with { Output = scrubbed };
            changed = true;
        }

        return changed ? copy : results;
    }

    /// <summary>
    ///     Scrub a tool call's arguments. The arguments are JSON, and a
    ///     credential sits in them as a STRING value — so the walk visits the
    ///     strings and leaves the structure alone. Replacing the text of the
    ///     whole document would turn <c>{"count": 42}</c> into broken JSON the
    ///     moment a rule matched a number.
    /// </summary>
    private JsonElement ScrubArgs(JsonElement args)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteScrubbed(args, writer);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private void WriteScrubbed(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(ScrubText(element.GetString() ?? string.Empty));
                break;

            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteScrubbed(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteScrubbed(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    // --- The shapes ---------------------------------------------------------

    /// <summary>A PEM private key block, header to footer.</summary>
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKeyBlock();

    /// <summary>An <c>Authorization: Bearer</c> header value.</summary>
    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]{8,}=*")]
    private static partial Regex BearerToken();

    /// <summary>
    ///     A credential whose shape is public knowledge. The lookbehind keeps
    ///     the rule off the tail of a longer word, and the <c>sk-</c> branch
    ///     deliberately does not match the <c>sk-</c> inside <c>csk-</c>: each
    ///     provider with its own prefix has its own branch.
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:sk-[A-Za-z0-9_-]{16,}|sk_live_[A-Za-z0-9]{16,}|rk_live_[A-Za-z0-9]{16,}"
        + @"|pk_live_[A-Za-z0-9]{16,}|klo_[A-Za-z0-9]{16,}|gsk_[A-Za-z0-9]{16,}|csk-[A-Za-z0-9_-]{16,}"
        + @"|xai-[A-Za-z0-9]{16,}|nvapi-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}"
        + @"|github_pat_[A-Za-z0-9_]{20,}|hf_[A-Za-z0-9]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}"
        + @"|glpat-[A-Za-z0-9_-]{16,}|npm_[A-Za-z0-9]{30,}|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}"
        + @"|AIza[A-Za-z0-9_-]{30,})")]
    private static partial Regex KnownCredentialShape();

    /// <summary>A JSON web token.</summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex JsonWebToken();

    /// <summary>A URL that carries a password: <c>scheme://user:password@host</c>.</summary>
    [GeneratedRegex(@"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)(?<user>[^/\s:@]+):(?<password>[^/\s@]+)@")]
    private static partial Regex UrlUserInfo();

    /// <summary>
    ///     A credential-named assignment. The name has to END with a
    ///     credential word — <c>token_count</c> and <c>max_tokens</c> are counts
    ///     and must survive, and a name that merely CONTAINS one of the words
    ///     (<c>author</c>, <c>tokenizer</c>) is not a credential either. The
    ///     rule is case-insensitive because the names that matter most are
    ///     environment variables, and those are spelled in caps.
    /// </summary>
    [GeneratedRegex(
        @"(?i)(?<![A-Za-z0-9])(?<name>[A-Za-z0-9_\-]*(?:api[_\-]?key|secret[_\-]?key|client[_\-]?secret"
        + @"|access[_\-]?key|private[_\-]?key|session[_\-]?key|signing[_\-]?key|secret|token"
        + @"|password|passwd|credential))(?<separator>\s*[:=]\s*)(?<quote>[""']?)(?<value>[^\s""',;}\]\)<>]+)")]
    private static partial Regex CredentialAssignment();
}
