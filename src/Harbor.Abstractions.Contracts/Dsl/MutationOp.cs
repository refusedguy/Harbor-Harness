namespace Harbor.Abstractions.Dsl;

/// <summary>
///     The eight operations a mutation batch carries. The set mirrors the
///     retained-tree operations in the GPUIX report: creation, property
///     updates, structural links, root selection and teardown.
/// </summary>
internal enum MutationOp : byte
{
    /// <summary>Allocates an element with a numeric id and a kind.</summary>
    CreateElement,

    /// <summary>Sets the interned style of an element.</summary>
    SetStyle,

    /// <summary>Sets the text of an element.</summary>
    SetText,

    /// <summary>Appends a child to a parent.</summary>
    AppendChild,

    /// <summary>Inserts a child before a sibling of the same parent.</summary>
    InsertBefore,

    /// <summary>Enables or disables a listener for an event type.</summary>
    SetEventListener,

    /// <summary>Selects the root element of the tree.</summary>
    SetRoot,

    /// <summary>Removes an element and its descendants.</summary>
    DestroyElement,
}
