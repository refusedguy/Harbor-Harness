namespace Harbor.Ipc.Protocol;

/// <summary>
///     Tracks which connected client "owns" a running session (sprint 6 A3).
///     A second client's <see cref="StartAgentRequest"/> for an owned session
///     is refused with a structured SESSION_BUSY error instead of silently
///     re-initializing the agent mid-run; events of an owned session are
///     addressed to the owner only.
/// </summary>
public sealed class SessionLeaseRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

    /// <summary>Acquire the lease; false when another client already owns it.</summary>
    public bool TryAcquire(string sessionId, string clientId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_lock)
        {
            if (_owners.TryGetValue(sessionId, out string? owner))
            {
                return owner == clientId;
            }

            _owners[sessionId] = clientId;
            return true;
        }
    }

    /// <summary>Release a lease held by <paramref name="clientId"/> (idempotent).</summary>
    public void Release(string sessionId, string clientId)
    {
        lock (_lock)
        {
            if (_owners.TryGetValue(sessionId, out string? owner) && owner == clientId)
            {
                _owners.Remove(sessionId);
            }
        }
    }

    /// <summary>Release every session owned by <paramref name="clientId"/>.</summary>
    public void ReleaseAll(string clientId)
    {
        lock (_lock)
        {
            List<string> doomed = [.. _owners.Where(kv => kv.Value == clientId).Select(kv => kv.Key)];
            foreach (string session in doomed)
            {
                _owners.Remove(session);
            }
        }
    }

    /// <summary>The owning client id, or null when the session is free.</summary>
    public string? GetOwner(string sessionId)
    {
        lock (_lock)
        {
            return _owners.TryGetValue(sessionId, out string? owner) ? owner : null;
        }
    }
}
