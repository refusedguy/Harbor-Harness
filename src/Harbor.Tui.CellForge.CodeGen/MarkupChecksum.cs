using System;
using System.Security.Cryptography;
using System.Text;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>
///     Deterministic checksum of a markup source, emitted beside the
///     generated code for issue #2431 so dev mode can reject stale output.
/// </summary>
/// <remarks>
///     SHA-256 over UTF-8 bytes is stable across hosts and needs no compiler
///     or runtime state. The generator computes it inside the incremental
///     transform, so the value travels with the source through the pipeline.
/// </remarks>
internal static class MarkupChecksum
{
    /// <summary>Computes the prefixed hex digest of the source text.</summary>
    /// <param name="text">Whole markup file contents.</param>
    internal static string Compute(string text)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}
