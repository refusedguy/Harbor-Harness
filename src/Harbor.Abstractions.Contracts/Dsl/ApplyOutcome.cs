using System;
using System.Collections.Generic;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     State returned by a successful batch apply. The failure axis lives in
///     the CSharpFunctionalExtensions Result that Apply returns, not here.
/// </summary>
internal readonly struct ApplyOutcome
{
    private static readonly int[] NoFreedIds = [];

    internal ApplyOutcome(int revision, bool dirty, int[] freedIds)
    {
        Revision = revision;
        Dirty = dirty;
        FreedIds = freedIds ?? throw new ArgumentNullException(nameof(freedIds));
    }

    /// <summary>Revision after the batch, advanced only when state changed.</summary>
    internal int Revision { get; }

    /// <summary>Whether this batch changed tree state.</summary>
    internal bool Dirty { get; }

    /// <summary>Element ids freed by destroy commands in this batch.</summary>
    internal IReadOnlyList<int> FreedIds { get; }
}
