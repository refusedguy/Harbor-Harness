using System;
using System.Collections.Generic;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Immutable style payload with structural equality. Properties are sorted
///     by name with an ordinal comparer, so two payloads built in a different
///     order compare equal. The queue and the interning table rely on that
///     equality to drop redundant set-style commands.
/// </summary>
internal sealed class MutationStyle
{
    private readonly MutationStyleProperty[] _properties;
    private readonly int _hash;

    private MutationStyle(MutationStyleProperty[] properties)
    {
        _properties = properties;
        var hash = new HashCode();
        hash.Add(properties.Length);
        foreach (var property in properties)
        {
            hash.Add(property.Name, StringComparer.Ordinal);
            hash.Add(property.Value, StringComparer.Ordinal);
        }

        _hash = hash.ToHashCode();
    }

    /// <summary>An empty payload.</summary>
    internal static MutationStyle Empty { get; } = Create();

    /// <summary>Count of properties in the payload.</summary>
    internal int Count => _properties.Length;

    /// <summary>Property at an index, in sorted order.</summary>
    internal MutationStyleProperty this[int index] => _properties[index];

    /// <summary>All properties, in sorted order.</summary>
    internal IReadOnlyList<MutationStyleProperty> Properties => _properties;

    /// <summary>Creates a payload from name/value pairs.</summary>
    internal static MutationStyle Create(params (string Name, string Value)[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var sorted = new MutationStyleProperty[properties.Length];
        for (int i = 0; i < properties.Length; i++)
        {
            var pair = properties[i];
            sorted[i] = new MutationStyleProperty(
                pair.Name ?? throw new ArgumentException("A style property name cannot be null.", nameof(properties)),
                pair.Value ?? throw new ArgumentException("A style property value cannot be null.", nameof(properties)));
        }

        Array.Sort(sorted, static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return new MutationStyle(sorted);
    }

    /// <summary>Compares payloads by content, not by reference.</summary>
    internal bool StructurallyEquals(MutationStyle? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || other._hash != _hash || other._properties.Length != _properties.Length)
            return false;

        for (int i = 0; i < _properties.Length; i++)
        {
            if (!_properties[i].Equals(other._properties[i]))
                return false;
        }

        return true;
    }

    /// <summary>Hash of the payload content, for the interning table.</summary>
    internal int StructureHash => _hash;
}

/// <summary>Structural comparer for <see cref="MutationStyle"/> in intern tables.</summary>
internal sealed class MutationStyleComparer : IEqualityComparer<MutationStyle>
{
    internal static readonly MutationStyleComparer Instance = new();

    bool IEqualityComparer<MutationStyle>.Equals(MutationStyle? left, MutationStyle? right) =>
        left is null ? right is null : left.StructurallyEquals(right);

    int IEqualityComparer<MutationStyle>.GetHashCode(MutationStyle style) => style.StructureHash;
}
