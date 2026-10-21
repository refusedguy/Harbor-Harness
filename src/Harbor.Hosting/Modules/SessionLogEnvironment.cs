// SessionLogEnvironment.cs — the process-side inputs of the session-log tier
// (#1740).
//
// WHY THE COMPOSITION ROOT AND NOT THE STORE
// ------------------------------------------
// The tier is a DECISION ("this run's logs are meant to be committed"), and a
// decision belongs where the rest of the process's decisions are made. The
// JSONL store is a library: it takes the tier as an argument and never looks
// at the environment, so a host that wants a different policy (a test, a
// plugin, the daemon) passes its own.
//
// The credential VALUES, on the other hand, are the one thing the store cannot
// know on its own: a provider key has no shape the store could recognise (a
// Groq key and a Cerebras key and a private gateway key are three different
// shapes), and the store must be told what this process holds in order to
// remove it. The process's own environment is where that lives.

using System.Collections;
using Harbor.Abstractions.Env;
using Harbor.Storage.Jsonl;

namespace Harbor.Hosting;

/// <summary>
///     Reads the two things the keyless session-log tier needs from the
///     process: which tier to write in, and which credential values the
///     process holds (#1740).
/// </summary>
internal static class SessionLogEnvironment
{
    /// <summary>
    ///     The name fragments that make an environment variable a credential.
    ///     The list is about NAMES, not values: a variable called
    ///     <c>OPENAI_API_KEY</c> holds a credential whatever it holds, and the
    ///     value is what gets redacted.
    /// </summary>
    private static readonly string[] CredentialNameMarkers =
    [
        "API_KEY",
        "APIKEY",
        "ACCESS_KEY",
        "ACCESSKEY",
        "TOKEN",
        "SECRET",
        "PASSWORD",
        "PASSWD",
        "CREDENTIAL",
        "PRIVATE_KEY",
        "SESSION_KEY",
        "SIGNING_KEY",
    ];

    /// <summary>
    ///     Read the tier from <c>HARBOR_SESSION_TIER</c>.
    /// </summary>
    /// <remarks>
    ///     An unknown value is a FAILURE, not a fallback. The default is
    ///     <see cref="SessionLogTier.Local" /> — an unscubbed log — so a typo
    ///     in the variable name would silently write a log full of credentials
    ///     into a repository. Failing loudly is the only safe reading of a
    ///     misspelled security setting.
    /// </remarks>
    /// <returns>The tier the session log is written in.</returns>
    internal static SessionLogTier ReadTier()
    {
        string raw = HarborEnv.Get(HarborEnv.SessionTier) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SessionLogTier.Local;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "local" => SessionLogTier.Local,
            "keyless" => SessionLogTier.Keyless,
            _ => throw new ArgumentException(
                $"Unknown {HarborEnv.SessionTier}: '{raw}'. Expected one of: local, keyless."),
        };
    }

    /// <summary>
    ///     The credential values the process holds: the value of every
    ///     environment variable whose NAME names a credential.
    /// </summary>
    /// <returns>The values to redact, in the order the environment lists them.</returns>
    internal static IReadOnlyCollection<string> ReadSecretValues()
    {
        List<string> secrets = [];
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string name = entry.Key as string ?? entry.Key?.ToString() ?? string.Empty;
            if (!NamesACredential(name))
            {
                continue;
            }

            string value = entry.Value as string ?? entry.Value?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            secrets.Add(value);
        }

        return secrets;
    }

    /// <summary>
    ///     Whether an environment variable's name says it holds a credential.
    /// </summary>
    private static bool NamesACredential(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        string upper = name.ToUpperInvariant();
        for (int i = 0; i < CredentialNameMarkers.Length; i++)
        {
            if (upper.Contains(CredentialNameMarkers[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
