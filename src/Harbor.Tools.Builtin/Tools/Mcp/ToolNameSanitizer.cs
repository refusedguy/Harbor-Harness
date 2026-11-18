using System.Security.Cryptography;
using System.Text;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Provider-safe names for MCP tools exposed to the model (issue #2677, item 2).
/// </summary>
/// <remarks>
///     <para>
///         MCP server names and remote tool names are free-form: a server may advertise
///         <c>db.query.v2</c> or a 200-character name. Most OpenAI-compatible providers
///         reject anything outside <c>[a-zA-Z0-9_-]</c> and cap a tool name at 64
///         characters. <see cref="ToolName" /> is stricter still: <c>^[a-z][a-z0-9_]*$</c>.
///         The old adapter replaced each bad character with <c>_</c>, which made
///         <c>query.v2</c> and <c>query-v2</c> the SAME model-facing tool — the second
///         registration silently shadowed the first.
///     </para>
///     <para>
///         When the original composite name already satisfies <see cref="ToolName" /> and
///         fits the cap, the name is unchanged (so existing catalogs, permission rules and
///         tests keep their stable <c>mcp_server_tool</c> shape). Otherwise a short
///         deterministic digest of the ORIGINAL server/remote pair is appended. Distinct
///         originals that collapse to the same sanitized prefix therefore stay distinct,
///         and the 64-character cap is still respected. The digest is only a collision
///         breaker; the model still sees a readable prefix.
///     </para>
///     <para>
///         The mapping back to the original is kept by the caller:
///         <see cref="McpToolAdapter" /> retains the remote name it received and uses it
///         verbatim in <c>tools/call</c>. Only the model-facing identifier is sanitized.
///     </para>
/// </remarks>
internal static class ToolNameSanitizer
{
    /// <summary>Provider cap for a tool name.</summary>
    internal const int MaxNameLength = 64;

    // "_" + 12 lowercase hex digits of the collision digest.
    private const int DigestHexLength = 12;
    private const int MaxPrefixLength = MaxNameLength - DigestHexLength - 1;

    /// <summary>
    ///     Build the model-facing tool name for one server/tool pair.
    /// </summary>
    internal static ToolName Sanitize(string server, string remoteName)
    {
        string candidate = $"mcp_{Segment(server)}_{Segment(remoteName)}";
        string originalComposite = $"mcp_{server}_{remoteName}";

        // The fast path is also the compatibility path: a name that already
        // fits every provider never gets a digest, so its identity is stable.
        if (candidate.Length <= MaxNameLength
            && string.Equals(candidate, originalComposite, StringComparison.Ordinal))
        {
            return ToolName.Create(candidate);
        }

        string prefix = candidate.Length <= MaxPrefixLength
            ? candidate
            : candidate[..MaxPrefixLength].TrimEnd('_');
        if (prefix.Length == 0)
        {
            prefix = "mcp";
        }

        return ToolName.Create($"{prefix}_{Digest(server, remoteName)}");
    }

    /// <summary>
    ///     Lowercase and keep only characters <see cref="ToolName" /> accepts. An
    ///     empty/free-form-only segment becomes a visible placeholder so the name
    ///     never collapses into a run of underscores.
    /// </summary>
    private static string Segment(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unnamed";
        }

        var chars = new char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char c = char.ToLowerInvariant(value[i]);
            chars[i] = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' ? c : '_';
        }

        return new string(chars);
    }

    /// <summary>
    ///     Deterministic collision breaker over the ORIGINAL pair. The NUL separator
    ///     keeps ("ab", "c") and ("a", "bc") from sharing a digest. SHA-256 is
    ///     BCL/AOT-safe; truncating to 12 hex digits (48 bits) leaves room for a
    ///     readable prefix inside the 64-character provider cap.
    /// </summary>
    private static string Digest(string server, string remoteName)
    {
        byte[] input = Encoding.UTF8.GetBytes($"{server}\0{remoteName}");
        return Convert.ToHexString(SHA256.HashData(input))[..DigestHexLength].ToLowerInvariant();
    }
}
