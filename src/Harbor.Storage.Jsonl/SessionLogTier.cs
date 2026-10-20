// SessionLogTier.cs — which tier a session log is written in (#1740).
//
// WHY A TIER AND NOT A FLAG
// -------------------------
// A flag says "scrub or not". A tier says "where this log is going". The
// difference is what the log is ALLOWED to hold:
//
//   * LOCAL — the log stays on this machine. It is a private notebook, it may
//     hold anything the agent read (a .env file, a credential pasted into the
//     prompt), and nothing is changed on the way to disk.
//   * KEYLESS — the log is an artifact: it is committed to a repository and
//     replayed later WITHOUT provider keys. A credential in it is a credential
//     in a public repository, so the store scrubs on the way out and the header
//     says which tier wrote the file.
//
// WHAT THE TIER IS NOT
// --------------------
// The tier is a property of the STORE (one process, one tier), not of a
// message and not of a file the store did not write. It changes what leaves
// the machine; it does not change what the agent sees: the in-memory message
// keeps its real text, and only the persisted copy is scrubbed. A run in the
// keyless tier therefore behaves EXACTLY like a local run — the difference is
// visible in the file, never in the loop.
//
// The path axis (absolute working directories, home-relative paths) is NOT
// part of this tier: a path names the machine, not a credential, and
// normalizing it is a different concern from removing a secret.

namespace Harbor.Storage.Jsonl;

/// <summary>
///     The tier a session log is written in (#1740).
/// </summary>
public enum SessionLogTier
{
    /// <summary>
    ///     The log stays on this machine. Nothing is scrubbed on write; the
    ///     file holds the agent's text verbatim.
    /// </summary>
    Local = 0,

    /// <summary>
    ///     The log is meant to leave the machine — committed to a repository and
    ///     replayed without provider keys. Provider credentials are scrubbed
    ///     from every text-bearing field before the message reaches the file,
    ///     and the header records that the file was written in this tier.
    /// </summary>
    Keyless = 1,
}

/// <summary>
///     The markers the header carries for a tier (#1740).
/// </summary>
/// <remarks>
///     <para>
///         The marker is written on the session header only, and only for the
///         keyless tier: a local log's header is byte-identical to what it was
///         before this tier existed, so enabling the tier never rewrites the
///         shape of a log nobody asked to be shared.
///     </para>
///     <para>
///         The marker is DESCRIPTIVE, not a guarantee: it says which tier wrote
///         the file, so a reader can tell a shareable log from a private one.
///         The scrubbing itself is done by the writer, and a log written in the
///         local tier was never scrubbed at all.
///     </para>
/// </remarks>
internal static class SessionLogTierMarkers
{
    /// <summary>
    ///     The value the header carries for a log written in the
    ///     <see cref="SessionLogTier.Keyless" /> tier.
    /// </summary>
    internal const string Keyless = "keyless";
}
