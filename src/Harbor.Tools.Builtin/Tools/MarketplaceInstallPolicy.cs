// MarketplaceInstallPolicy.cs: #2680 F153. The pure decision layer for a safe
// marketplace install: validate a package identity before any I/O and verify a
// downloaded payload against its pinned SHA-256. No download, file write,
// registry or product wiring lives here, so the slice stays AOT-safe and
// golden-neutral. A future `harbor marketplace install` command can own the
// network and filesystem while this type remains a testable value.

using System.Security.Cryptography;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Pure identity and integrity policy for marketplace packages (#2680 F153).
///     Stateless, thread-safe and free of I/O.
/// </summary>
public static class MarketplaceInstallPolicy
{
    /// <summary>Maximum length of a marketplace package id.</summary>
    public const int MaxIdLength = 64;

    /// <summary>Maximum length of an optional <c>@version</c> suffix.</summary>
    public const int MaxVersionLength = 32;

    private const int HashLength = 32;
    private const int HexLength = HashLength * 2;
    private const string Sha256Prefix = "sha256:";

    /// <summary>
    ///     Whether <paramref name="id"/> is a safe, canonical marketplace id:
    ///     1-<see cref="MaxIdLength" /> lowercase ASCII letters and digits,
    ///     separated by single hyphens, never starting or ending with a hyphen.
    ///     Traversal, whitespace, uppercase, empty segments and non-ASCII
    ///     look-alikes are all rejected.
    /// </summary>
    public static bool IsSafeId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
            return false;
        if (id[0] == '-' || id[^1] == '-')
            return false;

        var previousWasHyphen = false;
        foreach (var c in id)
        {
            if (c == '-')
            {
                if (previousWasHyphen)
                    return false;
                previousWasHyphen = true;
                continue;
            }

            if (!IsLowerAsciiLetterOrDigit(c))
                return false;
            previousWasHyphen = false;
        }

        return true;
    }

    /// <summary>
    ///     Splits an optional <c>id@version</c> install spec into its parts.
    ///     Returns <see langword="false" /> for a malformed or unsafe id, a
    ///     second <c>@</c>, or an unsafe or empty version suffix.
    /// </summary>
    public static bool TryParseSpec(string? spec, out string id, out string? version)
    {
        id = string.Empty;
        version = null;
        if (string.IsNullOrEmpty(spec))
            return false;

        var at = spec.IndexOf('@');
        if (at < 0)
        {
            if (!IsSafeId(spec))
                return false;
            id = spec;
            return true;
        }

        var candidateId = spec[..at];
        var candidateVersion = spec[(at + 1)..];
        if (!IsSafeId(candidateId) || !IsSafeVersion(candidateVersion))
            return false;

        id = candidateId;
        version = candidateVersion;
        return true;
    }

    /// <summary>
    ///     Whether the SHA-256 of <paramref name="payload"/> matches
    ///     <paramref name="expectedHex"/>. Accepts upper or lowercase hex and
    ///     an optional <c>sha256:</c> prefix; rejects null, wrong lengths,
    ///     non-hex characters and a wrong digest. The final comparison is
    ///     constant-time so a bad payload cannot leak the expected digest.
    /// </summary>
    public static bool VerifySha256(ReadOnlySpan<byte> payload, string? expectedHex)
    {
        if (expectedHex is null)
            return false;

        var hex = expectedHex.AsSpan();
        if (hex.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
            hex = hex[Sha256Prefix.Length..];

        if (hex.Length != HexLength)
            return false;

        Span<byte> expected = stackalloc byte[HashLength];
        for (var i = 0; i < expected.Length; i++)
        {
            var high = HexValue(hex[i * 2]);
            var low = HexValue(hex[i * 2 + 1]);
            if (high < 0 || low < 0)
                return false;
            expected[i] = (byte)((high << 4) | low);
        }

        Span<byte> actual = stackalloc byte[HashLength];
        SHA256.HashData(payload, actual);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool IsSafeVersion(string version)
    {
        if (version.Length is < 1 or > MaxVersionLength)
            return false;

        foreach (var c in version)
        {
            if (!IsLowerAsciiLetterOrDigit(c) && c != '.' && c != '-')
                return false;
        }

        return true;
    }

    private static bool IsLowerAsciiLetterOrDigit(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
