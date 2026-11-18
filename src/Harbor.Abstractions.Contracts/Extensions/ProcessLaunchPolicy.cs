using System.Collections;
using System.Diagnostics;

namespace Harbor.Abstractions.Extensions;

/// <summary>
///     Single owner of the child-process <b>environment allowlist</b> policy
///     (#2595). A caller declares the keys it allows; this type is the one
///     place that clears the child's inherited block and copies the declared
///     keys that exist in the parent process.
/// </summary>
/// <remarks>
///     <para>
///         Before this helper, <c>CheckRunner.ApplyEnvironment</c> owned the
///         clear-and-copy mechanism while its hook and <c>git pull</c>
///         counterparts launched children without it, so an env-key policy
///         change had to be re-applied per site. All three now call
///         <see cref="ApplyEnvironment" /> with their own key list: checks
///         pass a minimal list plus contract-declared passthrough, while hooks
///         and git pass <see cref="InheritedEnvironmentKeys" /> to keep their
///         documented "inherit the parent" behaviour.
///     </para>
///     <para>
///         Timeout, kill-on-timeout and pipe draining are deliberately NOT
///         part of this type. They are call-site policies with different
///         shapes (a byte-capped drain for checks vs. <c>ReadToEndAsync</c>
///         for hooks and git); merging them would change observable output.
///         This helper owns only the allowlist step those policies share.
///     </para>
/// </remarks>
public static class ProcessLaunchPolicy
{
    /// <summary>
    ///     Apply <paramref name="allowedKeys" /> to <paramref name="psi" />:
    ///     clear the inherited block, then copy only the declared keys that
    ///     are present in the parent process.
    /// </summary>
    /// <param name="psi">Child start info whose <c>Environment</c> is replaced.</param>
    /// <param name="allowedKeys">
    ///     Caller-owned key list, in any order. Blank names and duplicates are
    ///     ignored; a key absent from the parent is skipped.
    /// </param>
    /// <returns>The keys actually applied, sorted with <see cref="StringComparer.Ordinal" />.</returns>
    public static IReadOnlyList<string> ApplyEnvironment(ProcessStartInfo psi, IReadOnlyList<string> allowedKeys)
    {
        ArgumentNullException.ThrowIfNull(psi);
        List<KeyValuePair<string, string>> copied = Collect(allowedKeys);
        psi.Environment.Clear();
        var keys = new List<string>(copied.Count);
        for (int i = 0; i < copied.Count; i++)
        {
            psi.Environment[copied[i].Key] = copied[i].Value;
            keys.Add(copied[i].Key);
        }
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>
    ///     The keys <see cref="ApplyEnvironment" /> would copy, computed
    ///     without a <see cref="ProcessStartInfo" /> (used when a check is
    ///     cancelled before a process exists, so the record still reports the
    ///     effective environment shape).
    /// </summary>
    /// <param name="allowedKeys">Caller-owned key list, in any order.</param>
    /// <returns>The parent-present keys, sorted with <see cref="StringComparer.Ordinal" />.</returns>
    public static IReadOnlyList<string> EffectiveEnvironmentKeys(IReadOnlyList<string> allowedKeys)
    {
        List<KeyValuePair<string, string>> copied = Collect(allowedKeys);
        var keys = new List<string>(copied.Count);
        for (int i = 0; i < copied.Count; i++)
            keys.Add(copied[i].Key);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>
    ///     Declare "inherit the parent environment" as an explicit key list so
    ///     a caller that intentionally does not scrub still goes through the
    ///     same allowlist mechanism. Hooks and <c>git pull</c> pass this today;
    ///     a future policy that strips a key from inherited children edits
    ///     this one body.
    /// </summary>
    /// <returns>All variable names currently present in the parent process.</returns>
    public static IReadOnlyList<string> InheritedEnvironmentKeys()
    {
        IDictionary variables = Environment.GetEnvironmentVariables();
        var keys = new List<string>(variables.Count);
        foreach (DictionaryEntry variable in variables)
            keys.Add((string)variable.Key);
        return keys;
    }

    /// <summary>
    ///     Read each declared key from the parent once, keeping the first
    ///     occurrence of a duplicate. Both public methods share this body so
    ///     "which keys are effective" cannot drift between them.
    /// </summary>
    private static List<KeyValuePair<string, string>> Collect(IReadOnlyList<string> allowedKeys)
    {
        ArgumentNullException.ThrowIfNull(allowedKeys);
        var copied = new List<KeyValuePair<string, string>>(allowedKeys.Count);
        var added = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < allowedKeys.Count; i++)
        {
            string name = allowedKeys[i];
            if (string.IsNullOrWhiteSpace(name) || !added.Add(name))
                continue;
            string? value = Environment.GetEnvironmentVariable(name);
            if (value is null)
                continue;
            copied.Add(new KeyValuePair<string, string>(name, value));
        }
        return copied;
    }
}
