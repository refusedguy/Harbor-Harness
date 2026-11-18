using System.Collections.Generic;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Ordered list of commands produced by a render and consumed by one
///     atomic apply. The payload is structural, not JSON: source generation
///     emits command factory calls and the applier reads them directly, so no
///     reflection or JSON parser is needed on the hot path.
/// </summary>
internal sealed class MutationBatch
{
    private readonly List<MutationCommand> _commands;

    internal MutationBatch(IEnumerable<MutationCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = new List<MutationCommand>(commands);
    }

    internal IReadOnlyList<MutationCommand> Commands => _commands;

    internal int Count => _commands.Count;
}
