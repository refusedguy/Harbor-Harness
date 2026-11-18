using System;
using System.Collections.Generic;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Interns style payloads into stable integer ids. Interning runs before
///     the tree changes; a failed interning is rolled back with
///     <see cref="Sweep"/> so the tree never observes a partial table.
/// </summary>
internal sealed class StyleTable
{
    private readonly List<MutationStyle> _styles = [];
    private readonly Dictionary<MutationStyle, int> _ids = new(MutationStyleComparer.Instance);
    private readonly int _limit;

    internal StyleTable(int limit = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        _limit = limit;
    }

    internal int Count => _styles.Count;

    internal MutationStyle this[int styleId] => _styles[styleId];

    /// <summary>Returns the current entry count as a rollback point.</summary>
    internal int Mark() => _styles.Count;

    /// <summary>Interns a style. Returns false when the table is full.</summary>
    internal bool TryIntern(MutationStyle style, out int styleId)
    {
        if (_ids.TryGetValue(style, out styleId))
            return true;
        if (_styles.Count >= _limit)
        {
            styleId = 0;
            return false;
        }

        styleId = _styles.Count;
        _styles.Add(style);
        _ids.Add(style, styleId);
        return true;
    }

    /// <summary>Drops everything interned after a <see cref="Mark"/>.</summary>
    internal void Sweep(int mark)
    {
        if ((uint)mark > (uint)_styles.Count)
            throw new ArgumentOutOfRangeException(nameof(mark));

        for (int i = mark; i < _styles.Count; i++)
            _ids.Remove(_styles[i]);
        _styles.RemoveRange(mark, _styles.Count - mark);
    }
}
