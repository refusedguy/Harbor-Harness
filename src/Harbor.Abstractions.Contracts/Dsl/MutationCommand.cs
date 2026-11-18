using System;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     One command in a mutation batch. A value type so a batch holds many
///     commands without an allocation per command. Numeric ids identify
///     elements; <see cref="SecondaryId"/> is the parent for append and insert
///     and <see cref="ExtraId"/> is the sibling to insert before.
/// </summary>
internal readonly struct MutationCommand
{
    private MutationCommand(
        MutationOp op,
        int id,
        int secondaryId,
        int extraId,
        SpecKind kind,
        string? text,
        MutationStyle? style,
        string? eventType,
        bool enabled)
    {
        Op = op;
        Id = id;
        SecondaryId = secondaryId;
        ExtraId = extraId;
        Kind = kind;
        Text = text;
        Style = style;
        EventType = eventType;
        Enabled = enabled;
    }

    internal MutationOp Op { get; }
    internal int Id { get; }
    internal int SecondaryId { get; }
    internal int ExtraId { get; }
    internal SpecKind Kind { get; }
    internal string? Text { get; }
    internal MutationStyle? Style { get; }
    internal string? EventType { get; }
    internal bool Enabled { get; }

    internal static MutationCommand CreateElement(int id, SpecKind kind) =>
        new(MutationOp.CreateElement, id, 0, 0, kind, null, null, null, false);

    internal static MutationCommand SetStyle(int id, MutationStyle style) =>
        new(MutationOp.SetStyle, id, 0, 0, default, null,
            style ?? throw new ArgumentNullException(nameof(style)), null, false);

    internal static MutationCommand SetText(int id, string text) =>
        new(MutationOp.SetText, id, 0, 0, default,
            text ?? throw new ArgumentNullException(nameof(text)), null, null, false);

    internal static MutationCommand AppendChild(int parentId, int childId) =>
        new(MutationOp.AppendChild, parentId, childId, 0, default, null, null, null, false);

    internal static MutationCommand InsertBefore(int parentId, int childId, int beforeId) =>
        new(MutationOp.InsertBefore, parentId, childId, beforeId, default, null, null, null, false);

    internal static MutationCommand SetEventListener(int id, string eventType, bool enabled) =>
        new(MutationOp.SetEventListener, id, 0, 0, default, null, null,
            eventType ?? throw new ArgumentNullException(nameof(eventType)), enabled);

    internal static MutationCommand SetRoot(int id) =>
        new(MutationOp.SetRoot, id, 0, 0, default, null, null, null, false);

    internal static MutationCommand DestroyElement(int id) =>
        new(MutationOp.DestroyElement, id, 0, 0, default, null, null, null, false);

    /// <summary>Compares commands by payload, including structural style equality.</summary>
    internal bool IsEquivalentTo(in MutationCommand other) =>
        Op == other.Op
        && Id == other.Id
        && SecondaryId == other.SecondaryId
        && ExtraId == other.ExtraId
        && Kind == other.Kind
        && string.Equals(Text, other.Text, StringComparison.Ordinal)
        && (Style is null ? other.Style is null : Style.StructurallyEquals(other.Style))
        && string.Equals(EventType, other.EventType, StringComparison.Ordinal)
        && Enabled == other.Enabled;
}
