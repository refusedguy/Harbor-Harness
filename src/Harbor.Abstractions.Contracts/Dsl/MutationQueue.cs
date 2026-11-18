using System.Collections.Generic;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Accumulates one render's commands and drops state-setting commands
///     whose payload is structurally identical to the last queued value for
///     the same element. The check happens before enqueue, so a redundant
///     set-style never reaches the applier or the wire codec.
/// </summary>
internal sealed class MutationQueue
{
    private readonly List<MutationCommand> _commands = [];
    private readonly Dictionary<int, MutationCommand> _lastState = [];

    internal int Count => _commands.Count;

    internal void Enqueue(MutationCommand command)
    {
        switch (command.Op)
        {
            case MutationOp.SetStyle:
            case MutationOp.SetText:
            case MutationOp.SetEventListener:
                if (_lastState.TryGetValue(command.Id, out var prior) && prior.IsEquivalentTo(command))
                    return;
                _lastState[command.Id] = command;
                break;

            case MutationOp.CreateElement:
            case MutationOp.DestroyElement:
                _lastState.Remove(command.Id);
                break;
        }

        _commands.Add(command);
    }

    /// <summary>Returns the queued commands as a batch and clears the queue.</summary>
    internal MutationBatch Drain()
    {
        var batch = new MutationBatch(_commands);
        _commands.Clear();
        _lastState.Clear();
        return batch;
    }
}
