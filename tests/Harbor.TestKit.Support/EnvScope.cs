namespace Harbor.TestKit;

/// <summary>
///     Restores process environment variables to the values they had when the
///     scope was taken — even when the test body throws.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a scope object and not a hand-written save/restore.</b> The
///         boilerplate this replaces was the same six lines in nine files, and
///         it was wrong in two different ways: some copies restored a value
///         they TYPED instead of the one they READ (so a variable that was set
///         before the test stayed unset after it), and some copies restored it
///         only on the success path. The architecture guard
///         <c>ProcessEnvIsolationRule</c> exists because of exactly that
///         asymmetry, and it names the failure mode precisely: the loss
///         belongs to whoever runs next, which is the worst attribution there
///         is.
///     </para>
///     <para>
///         <b>Symmetry is the contract.</b> Every variable this scope touches
///         is READ before it is written, and the value that is read is the
///         value that is put back — including the case where the variable did
///         not exist at all (restoring "absent" means removing it again, not
///         setting it to the empty string).
///     </para>
///     <para>
///         <b>Parallel safety.</b> The process environment is process-global:
///         <c>Environment.SetEnvironmentVariable</c> takes a lock on a
///         <em>static</em> dictionary, so two tests in the same test project
///         that write different variables still race. No scope object can make
///         that safe by itself, so this one does not pretend to: it takes a
///         PROCESS-WIDE GATE for its whole lifetime, and any other
///         <see cref="EnvScope" /> in the same process waits for it. Two
///         env-mutating tests therefore cannot interleave their
///         set/restore windows, whatever the scheduler decides.
///     </para>
///     <para>
///         The gate is re-entrant within one asynchronous flow (a nested scope
///         does not wait for itself) and it is released by
///         <see cref="Dispose" /> — which the compiler emits for a
///         <c>using</c> even when the body throws. A scope that is never
///         disposed would hold the gate until the acquisition timeout fires
///         for the next waiter, so the timeout is finite and reports itself.
///     </para>
///     <para>
///         The gate is a SECOND line of defence, not the first. The
///         architecture guard already requires every class that writes the
///         environment to declare a <c>[NotInParallel]</c> form, and the classes
///         migrated to this helper keep theirs.
///     </para>
/// </remarks>
public sealed class EnvScope : IDisposable
{
    /// <summary>
    ///     How long a scope waits for the process-wide environment gate before
    ///     giving up. Long enough that a legitimate holder (a whole test
    ///     method) never trips it, short enough that a lost scope is reported
    ///     instead of hanging the run.
    /// </summary>
    public static readonly TimeSpan GateTimeout = TimeSpan.FromMinutes(2);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    ///     Re-entrancy depth of the CURRENT asynchronous flow. An
    ///     <see cref="AsyncLocal{T}" /> (not a thread-local) because the scope
    ///     is created before an <c>await</c> and disposed after it, possibly on
    ///     a different thread — a thread-local would lose the depth and
    ///     release the gate twice.
    /// </summary>
    private static readonly AsyncLocal<int> LiveScopes = new();

    private readonly Dictionary<string, string?> _originals = new(StringComparer.Ordinal);

    private bool _acquired;

    private bool _disposed;

    /// <summary>
    ///     Takes a scope over the named variables, reading each one's current
    ///     value so it can be restored exactly.
    /// </summary>
    /// <param name="names">
    ///     Variable names to snapshot. A name may also be written later through
    ///     <see cref="Set" /> without being listed here — the value is read at
    ///     that point, before the write.
    /// </param>
    public EnvScope(params string[] names)
    {
        EnterGate();

        foreach (string name in names)
        {
            Remember(name);
        }
    }

    /// <summary>
    ///     Runs the body with one variable set, and puts the variable back
    ///     when the body returns or throws.
    /// </summary>
    /// <remarks>
    ///     The shape half the test tree had hand-written nine times: read the
    ///     variable, set it, run the body, restore it. The scope does the
    ///     reading and the restoring, so a caller cannot get the symmetry
    ///     wrong.
    /// </remarks>
    /// <param name="name">The variable to set.</param>
    /// <param name="value">The value to set it to.</param>
    /// <param name="action">The body to run.</param>
    public static async Task WithAsync(string name, string? value, Func<Task> action)
    {
        using var env = new EnvScope(name);
        env.Set(name, value);
        await action().ConfigureAwait(false);
    }

    /// <summary>
    ///     Runs the body with one variable set, and puts the variable back
    ///     when the body returns or throws.
    /// </summary>
    /// <param name="name">The variable to set.</param>
    /// <param name="value">The value to set it to.</param>
    /// <param name="action">The body to run, and its result.</param>
    /// <typeparam name="T">What the body returns.</typeparam>
    public static T With<T>(string name, string? value, Func<T> action)
    {
        using var env = new EnvScope(name);
        env.Set(name, value);
        return action();
    }

    /// <summary>
    ///     The value a variable had when this scope first touched it, or
    ///     <see langword="null" /> when the variable did not exist.
    /// </summary>
    /// <param name="name">Variable name.</param>
    public string? OriginalValue(string name) =>
        _originals.TryGetValue(name, out string? value) ? value : null;

    /// <summary>
    ///     Sets a variable, remembering its current value first so that
    ///     <see cref="Dispose" /> can put it back.
    /// </summary>
    /// <param name="name">Variable name.</param>
    /// <param name="value">
    ///     Value to set. <see langword="null" /> removes the variable, which is
    ///     what <see cref="Environment.SetEnvironmentVariable(string, string?)" />
    ///     does for a null value.
    /// </param>
    public void Set(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Read BEFORE the write — the asymmetry the guard is about.
        Remember(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    ///     Restores every variable this scope touched to the value it had when
    ///     the scope took it.
    /// </summary>
    /// <remarks>
    ///     Best-effort per variable: one variable that cannot be restored must
    ///     not stop the others from being restored, and must not hide the
    ///     failure that brought us here. Calling this twice is a no-op.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach ((string name, string? value) in _originals)
        {
            try
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            catch (ArgumentException)
            {
                // An illegal name for this platform — nothing to restore.
            }
        }

        _originals.Clear();
        ExitGate();
    }

    private void Remember(string name)
    {
        if (!_originals.ContainsKey(name))
        {
            _originals[name] = Environment.GetEnvironmentVariable(name);
        }
    }

    private void EnterGate()
    {
        if (LiveScopes.Value > 0)
        {
            // Already inside an env scope in this flow — do not wait for
            // ourselves, and do not release the gate twice.
            LiveScopes.Value = LiveScopes.Value + 1;
            return;
        }

        if (!Gate.Wait(GateTimeout))
        {
            throw new InvalidOperationException(
                "Timed out after " + GateTimeout.TotalSeconds + "s waiting for the process-wide "
                + "environment gate. Another EnvScope in this test process has not been disposed — "
                + "an environment scope that is taken and never released blocks every later env test "
                + "in the project.");
        }

        _acquired = true;
        LiveScopes.Value = 1;
    }

    private void ExitGate()
    {
        if (LiveScopes.Value > 0)
        {
            LiveScopes.Value = LiveScopes.Value - 1;
        }

        if (_acquired)
        {
            _acquired = false;
            Gate.Release();
        }
    }
}
