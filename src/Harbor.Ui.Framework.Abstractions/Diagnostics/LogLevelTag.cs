using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace Harbor.Ui.Framework.Diagnostics;

/// <summary>
///     The 4-character <see cref="LogLevel" /> mnemonic — <c>TRAC</c>, <c>DBUG</c>,
///     <c>INFO</c>, <c>WARN</c>, <c>ERRO</c>, <c>CRIT</c>, <c>NONE</c> — and the only
///     place in the tree that spells it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is a type and not a convention (#563).</b> Six sites used to
///         render the mnemonic from their own <c>LogLevel</c> switch, and they
///         disagreed about both the fallback and the row around it. The panel sent
///         an unrecognised level to a 4-question-mark sentinel; the two
///         <c>FileLogger</c> copies sent it to
///         <c>level.ToString().ToUpperInvariant()</c>. So one event rendered as
///         a 4-question-mark sentinel in the logs panel, and as
///         <c>VERBOSE</c> in the log file.
///     </para>
///     <para>
///         <b>No wildcard arm, on purpose.</b> <see cref="For" /> names every
///         <see cref="LogLevel" /> member and has no <c>_ =&gt;</c> fallback, so
///         adding a member to the enum is a compile error here (CS8509, escalated by
///         <c>TreatWarningsAsErrors</c>) rather than a seventh spelling that
///         appears in one producer and not the others. A value outside the enum —
///         a bad cast — throws <see cref="System.Runtime.CompilerServices.SwitchExpressionException" />
///         instead of rendering a token that means nothing.
///     </para>
///     <para>
///         <b>It is a parsed field, not decoration.</b> <see cref="TryParse" /> is
///         the inverse of <see cref="For" />, and it is what lets
///         <see cref="LogRowFormat" /> read a rendered row back by rules instead of
///         by fixed character offsets. The inverse table is DERIVED from
///         <see cref="For" /> at type initialisation, so there is still exactly one
///         hand-maintained list.
///     </para>
///     <para>
///         Enforced by <c>tests/Harbor.Architecture.Tests/LogLevelMnemonicRule.cs</c>:
///         a second <c>LogLevel</c>-to-text table anywhere under <c>src/</c> or
///         <c>apps/</c> fails the build, as does the return of the
///         4-question-mark sentinel.
///     </para>
/// </remarks>
public static class LogLevelTag
{
    /// <summary>
    ///     Every mnemonic is exactly this many characters, so a column budget
    ///     computed from <see cref="Width" /> cannot drift from the tags it sizes.
    /// </summary>
    public const int Width = 4;

    /// <summary>
    ///     Mnemonic by level, built by reading <see cref="For" /> over the whole
    ///     enum. There is no literal table to keep in step with the switch below.
    /// </summary>
    private static readonly FrozenDictionary<LogLevel, string> TagsByLevel = BuildTagsByLevel();

    /// <summary>
    ///     The reverse index, built from <see cref="TagsByLevel" /> so it cannot
    ///     name a mnemonic <see cref="For" /> does not produce, or miss one it does.
    /// </summary>
    private static readonly FrozenDictionary<string, LogLevel> LevelsByTag = BuildLevelsByTag();

    /// <summary>
    ///     The mnemonic for <paramref name="level" />.
    /// </summary>
    /// <param name="level">The level to render. Must be a declared <see cref="LogLevel" />.</param>
    /// <returns>A mnemonic of exactly <see cref="Width" /> characters.</returns>
    /// <exception cref="System.Runtime.CompilerServices.SwitchExpressionException">
    ///     <paramref name="level" /> is not a declared <see cref="LogLevel" /> value.
    ///     There is no sentinel: an unknown level is a programming error here, not
    ///     a token to render.
    /// </exception>
    public static string For(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRAC",
        LogLevel.Debug => "DBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERRO",
        LogLevel.Critical => "CRIT",
        LogLevel.None => "NONE",
    };

    /// <summary>
    ///     The inverse of <see cref="For" />: the level a rendered mnemonic names.
    /// </summary>
    /// <param name="tag">A candidate mnemonic. Compared ordinally, untrimmed.</param>
    /// <param name="level">The level, when the method returns <see langword="true" />.</param>
    /// <returns>
    ///     <see langword="false" /> for anything <see cref="For" /> does not
    ///     produce — including the retired 4-question-mark sentinel, the empty
    ///     string, and a mnemonic with a bracket or padding attached. A caller
    ///     parsing a row must be able to say "this is not a level" instead of
    ///     falling through to a default colour.
    /// </returns>
    public static bool TryParse(string? tag, out LogLevel level)
    {
        if (tag is not null && LevelsByTag.TryGetValue(tag, out LogLevel parsed))
        {
            level = parsed;
            return true;
        }

        level = default;
        return false;
    }

    private static FrozenDictionary<LogLevel, string> BuildTagsByLevel()
    {
        var builder = new Dictionary<LogLevel, string>(capacity: 8);
        foreach (LogLevel level in Enum.GetValues<LogLevel>())
        {
            builder[level] = For(level);
        }

        return builder.ToFrozenDictionary();
    }

    private static FrozenDictionary<string, LogLevel> BuildLevelsByTag()
    {
        var builder = new Dictionary<string, LogLevel>(capacity: 8, StringComparer.Ordinal);
        foreach (KeyValuePair<LogLevel, string> pair in TagsByLevel)
        {
            builder[pair.Value] = pair.Key;
        }

        return builder.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
