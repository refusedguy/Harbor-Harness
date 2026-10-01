// ProjectorThreadSafetyTests.cs — #605 (GoF-A19).
//
// DefaultUiProjector is registered AddSingleton by the desktop host
// (apps/Harbor.App.Avalonia/Hosting/ServiceRegistration.cs:130) and its only
// check-doc-cites: record-drift apps/Harbor.App.Avalonia/Hosting/ServiceRegistration.cs:130 now="//" [#947: written over `services.AddSingleton<DefaultUiProjector`; repair deferred to the owner's symbol-rename decision] -->
// consumer, UiRenderEngine, is a singleton too. Before the fix the type's doc
// said it was not thread-safe while the container shared it process-wide, and
// the one mutable field was published without a fence:
//
//     private ProjectionCache? _cache;      // now volatile
//
// WHAT THESE TESTS DO AND DO NOT PROVE — read this before trusting a green run.
//
// They do NOT deterministically reproduce the unsynchronised-publication
// window. That window is a memory-model hazard: on x64/x86-64 the JIT emits
// plain stores for the object initialiser and the reference write, and the
// hardware is TSO, so a stress loop will essentially never observe a
// half-built ProjectionCache even without `volatile`. It is real on weakly
// ordered hardware (arm64 — Apple Silicon, Graviton, Raspberry Pi, all
// plausible Harbor build agents), where the store to the reference may be
// reordered ahead of the stores that populate the object. There is no way to
// force that from managed code without a real race detector.
//
// So the deterministic proof that the fix landed lives in
// tests/Harbor.Architecture.Tests/UiSingletonThreadSafetyRules.cs, which
// asserts the doc↔container correspondence, and in the `volatile` modifier
// itself. What is worth asserting HERE is the behavioural contract the fix
// promises, because that is what a future refactor of Project would break:
// under concurrent callers the projector never throws, never returns null
// through its non-nullable return type, and never hands back a screen whose
// models are unpopulated. A change that made the cache mutable-after-
// publication, moved the assignment off the single initialiser, or dropped one
// of the six required members fails here even on x64.
//
// These are stress tests, not race reproducers: they always pass on correct
// code. They are deliberately bounded (fixed iteration counts, a join with a
// timeout, no sleeps, no shared global state) and touch no Avalonia type, so
// they cannot collide with the known headless `ChatView_Inflates`
// ListBoxItem/StaticResource flake — that flake is an Avalonia resource
// lookup, this is plain managed code in a project with no Avalonia reference.

using System.Collections.Concurrent;
using Harbor.Abstractions.Models;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     Asserts the concurrency contract <see cref="DefaultUiProjector" />
///     documents after #605: sharing one instance across threads yields
///     fully-populated screens and never a torn cache.
/// </summary>
public class ProjectorThreadSafetyTests
{
    private const int Threads = 4;
    private const int IterationsPerThread = 500;

    /// <summary>
    ///     Build a spread of distinct, non-empty states so the callers do not
    ///     all hit the same-screen fast path on the same instance.
    /// </summary>
    private static UiState[] BuildStates(int count)
    {
        var store = new UiStore();
        var states = new List<UiState>(count);

        for (int i = 0; i < count; i++)
        {
            store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.User, $"user-{i}"));
            store.Dispatch(new ChatAppMsg.AppendLine(ChatRole.Assistant, $"assistant-{i}"));
            store.Dispatch(new AppMsg.InputText($"draft-{i}"));
            store.Dispatch(new AppMsg.Viewport(20 + i));
            states.Add(store.State);
        }

        return [.. states];
    }

    private static void AssertFullyPopulated(UiScreenModel screen)
    {
        if (screen is null)
        {
            throw new InvalidOperationException("Project returned null through a non-nullable return type.");
        }

        if (screen.Header is null)
        {
            throw new InvalidOperationException("Project returned a screen with an unpopulated Header.");
        }

        if (screen.Transcript is null)
        {
            throw new InvalidOperationException("Project returned a screen with an unpopulated Transcript.");
        }

        if (screen.StatusBar is null)
        {
            throw new InvalidOperationException("Project returned a screen with an unpopulated StatusBar.");
        }

        if (screen.Input is null)
        {
            throw new InvalidOperationException("Project returned a screen with an unpopulated Input.");
        }

        if (string.IsNullOrEmpty(screen.StateRevision))
        {
            throw new InvalidOperationException("Project returned a screen with an empty StateRevision.");
        }
    }

    /// <summary>
    ///     The desktop host's actual usage shape: ONE shared projector, many
    ///     threads, each projecting its own state. Every screen that comes back
    ///     must be complete, and no caller may observe a half-published cache.
    /// </summary>
    [Test]
    public async Task SharedProjector_ConcurrentCallers_NeverObserveAPartialScreen()
    {
        var projector = new DefaultUiProjector();
        UiState[] states = BuildStates(8);

        var failures = new ConcurrentBag<string>();
        var tasks = new Task[Threads];

        for (int t = 0; t < Threads; t++)
        {
            int thread = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < IterationsPerThread; i++)
                {
                    UiState state = states[(thread + i) % states.Length];
                    try
                    {
                        AssertFullyPopulated(projector.Project(state));
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"thread {thread} iteration {i}: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }
                }
            });
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(failures.ToArray()).IsEmpty();
    }

    /// <summary>
    ///     The worst case for publication: every caller hammers the SAME state
    ///     instance, so all of them take the <c>ReferenceEquals(cache.State,
    ///     state)</c> fast path and return <c>cache.Screen</c> — a field of the
    ///     cache object, not a local. If the reference were published before
    ///     that object was populated, this is the path that returns null.
    /// </summary>
    [Test]
    public async Task SharedProjector_SameStateFastPath_NeverReturnsAnUnpopulatedScreen()
    {
        var projector = new DefaultUiProjector();
        UiState state = BuildStates(1)[0];

        // Publish the cache first, so every racing caller reads an existing
        // object rather than racing the very first publication.
        AssertFullyPopulated(projector.Project(state));

        var failures = new ConcurrentBag<string>();
        var tasks = new Task[Threads];

        for (int t = 0; t < Threads; t++)
        {
            int thread = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < IterationsPerThread; i++)
                {
                    try
                    {
                        UiScreenModel screen = projector.Project(state);
                        if (screen is null)
                        {
                            failures.Add($"thread {thread} iteration {i}: fast path returned null.");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"thread {thread} iteration {i}: {ex.GetType().Name}: {ex.Message}");
                        return;
                    }
                }
            });
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(failures.ToArray()).IsEmpty();
    }

    /// <summary>
    ///     A publisher and a reader released together by a barrier, repeated,
    ///     to give the release/acquire pair the widest window the managed
    ///     runtime will allow. See the file header: this is a stress test, not
    ///     a deterministic reproduction of the publication window.
    /// </summary>
    [Test]
    public async Task SharedProjector_PublisherAndReaderRacing_AlwaysProduceCompleteScreens()
    {
        const int Rounds = 500;

        var projector = new DefaultUiProjector();
        UiState[] states = BuildStates(4);
        var failures = new ConcurrentBag<string>();

        for (int round = 0; round < Rounds; round++)
        {
            using var gate = new Barrier(2);

            // LongRunning rather than Task.Run: both halves block on the
            // barrier, and two pool threads that never rendezvous would lean on
            // the pool's thread-injection delay to make progress. Dedicated
            // threads make the rendezvous immediate and the runtime bounded.
            Task publisher = Task.Factory.StartNew(
                () =>
                {
                    gate.SignalAndWait();
                    try
                    {
                        AssertFullyPopulated(projector.Project(states[round % states.Length]));
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"publisher round {round}: {ex.GetType().Name}: {ex.Message}");
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Task reader = Task.Factory.StartNew(
                () =>
                {
                    gate.SignalAndWait();
                    try
                    {
                        UiScreenModel screen = projector.Project(states[(round + 1) % states.Length]);
                        if (screen is null)
                        {
                            failures.Add($"reader round {round}: returned null.");
                        }
                        else
                        {
                            AssertFullyPopulated(screen);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"reader round {round}: {ex.GetType().Name}: {ex.Message}");
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            await Task.WhenAll(publisher, reader).WaitAsync(TimeSpan.FromSeconds(30));
        }

        await Assert.That(failures.ToArray()).IsEmpty();
    }
}
