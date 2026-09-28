using System;
using System.Collections.Generic;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Services;
namespace Harbor.Tui.Tests;

/// <summary>
///     Tests for <see cref="OverlayController" /> — the single writer for overlay
///     open/close state. Verifies id→setter registration, stack push/pop semantics,
///     and guard clauses for invalid arguments.
/// </summary>
public class OverlayControllerTests
{
    /// <summary>
    ///     <see cref="OverlayController.Register" /> maps an overlay id to a boolean
    ///     setter. After registering "x" with a flag setter, <see cref="OverlayController.Open" />
    ///     invokes the setter with <c>true</c>.
    /// </summary>
    [Test]
    public async Task Register_MapsId_ToSetter()
    {
        var controller = new OverlayController();
        bool flag = false;

        controller.Register("x", v => flag = v);
        controller.Open("x");

        await Assert.That(flag).IsTrue();
    }

    /// <summary>
    ///     <see cref="OverlayController.Open" /> pushes the overlay id onto the
    ///     <see cref="IOverlayStack" /> and calls the registered setter with <c>true</c>.
    /// </summary>
    [Test]
    public async Task Open_PushesId_And_CallsSetter()
    {
        var stack = new OverlayStackService();
        var controller = new OverlayController(stack);
        var calls = new List<bool>();

        controller.Register("settings", v => calls.Add(v));
        controller.Open("settings");

        await Assert.That(calls).Count().IsEqualTo(1);
        await Assert.That(calls[0]).IsTrue();
        await Assert.That(stack.Current).IsEqualTo("settings");
    }

    /// <summary>
    ///     <see cref="OverlayController.Close" /> calls the registered setter with
    ///     <c>false</c> without touching the stack.
    /// </summary>
    [Test]
    public async Task Close_CallsSetter_WithFalse()
    {
        var controller = new OverlayController();
        var calls = new List<bool>();

        controller.Register("palette", v => calls.Add(v));
        controller.Open("palette");
        calls.Clear();
        controller.Close("palette");

        await Assert.That(calls).Count().IsEqualTo(1);
        await Assert.That(calls[0]).IsFalse();
    }

    /// <summary>
    ///     <see cref="OverlayController.CloseTop" /> closes the top overlay (setter
    ///     receives <c>false</c>) and pops it from the stack. Returns <c>true</c>
    ///     when the stack was non-empty.
    /// </summary>
    [Test]
    public async Task CloseTop_Closes_And_PopsTop()
    {
        var stack = new OverlayStackService();
        var controller = new OverlayController(stack);
        var calls = new List<bool>();

        controller.Register("diff", v => calls.Add(v));
        controller.Open("diff");

        var result = controller.CloseTop();

        await Assert.That(result).IsTrue();
        await Assert.That(stack.Current).IsNull();
        await Assert.That(calls).Contains(false);
    }

    /// <summary>
    ///     <see cref="OverlayController.CloseTop" /> returns <c>false</c> when the
    ///     stack is empty and does not call any setter.
    /// </summary>
    [Test]
    public async Task CloseTop_EmptyStack_ReturnsFalse()
    {
        var controller = new OverlayController();
        var calls = new List<bool>();

        var result = controller.CloseTop();

        await Assert.That(result).IsFalse();
        await Assert.That(calls).IsEmpty();
    }

    /// <summary>
    ///     <see cref="OverlayController.HasOverlay" /> reflects whether the stack
    ///     currently holds an overlay.
    /// </summary>
    [Test]
    public async Task HasOverlay_Reflects_StackState()
    {
        var stack = new OverlayStackService();
        var controller = new OverlayController(stack);

        await Assert.That(controller.HasOverlay).IsFalse();

        controller.Register("settings", _ => { });
        controller.Open("settings");

        await Assert.That(controller.HasOverlay).IsTrue();

        controller.CloseTop();

        await Assert.That(controller.HasOverlay).IsFalse();
    }

    /// <summary>
    ///     <see cref="OverlayController.Open" /> with an unknown id is a no-op:
    ///     no exception is thrown and no setter is invoked.
    /// </summary>
    [Test]
    public async Task Open_UnknownId_IsNoOp()
    {
        var controller = new OverlayController();
        var calls = new List<bool>();

        controller.Register("settings", v => calls.Add(v));
        controller.Open("does-not-exist");

        await Assert.That(calls).IsEmpty();
    }

    /// <summary>
    ///     <see cref="OverlayController.Close" /> with an unknown id is a no-op:
    ///     no exception is thrown and no setter is invoked.
    /// </summary>
    [Test]
    public async Task Close_UnknownId_IsNoOp()
    {
        var controller = new OverlayController();
        var calls = new List<bool>();

        controller.Register("settings", v => calls.Add(v));
        controller.Close("does-not-exist");

        await Assert.That(calls).IsEmpty();
    }

    /// <summary>
    ///     <see cref="OverlayController.Register" /> throws
    ///     <see cref="System.ArgumentException" /> when the overlay id is empty.
    /// </summary>
    [Test]
    public async Task Register_EmptyId_Throws()
    {
        var controller = new OverlayController();

        var ex = Assert.Throws<System.ArgumentException>(() => controller.Register(string.Empty, _ => { }));
        await Assert.That(ex.ParamName).IsEqualTo("id");
    }

    /// <summary>
    ///     <see cref="OverlayController.Register" /> throws
    ///     <see cref="System.ArgumentNullException" /> when the setter is <c>null</c>.
    /// </summary>
    [Test]
    public async Task Register_NullSetter_Throws()
    {
        var controller = new OverlayController();

        var ex = Assert.Throws<System.ArgumentNullException>(() => controller.Register("x", null!));
        await Assert.That(ex.ParamName).IsEqualTo("setter");
    }

    /// <summary>
    ///     Issue #476 — <see cref="IOverlayStack" /> is a singleton, and a fresh
    ///     <see cref="OverlayController" /> is built for every renderer swap
    ///     (theme switch, backend hot-swap, golden run). The constructor subscribes
    ///     to <see cref="IOverlayStack.Changed" /> and
    ///     <see cref="IOverlayStack.Popped" />, so a controller that does not
    ///     detach on <c>Dispose</c> stays rooted in the stack for the process
    ///     lifetime. This test counts live subscribers across N
    ///     create/dispose cycles and requires the count to return to zero each
    ///     time, so it grows on the old code and stays flat on the fix.
    /// </summary>
    [Test]
    public async Task Dispose_AcrossControllerCycles_DoesNotGrow_StackSubscribers()
    {
        var stack = new CountingOverlayStack();
        const int cycles = 25;

        for (var i = 0; i < cycles; i++)
        {
            using (var controller = new OverlayController(stack))
            {
                controller.Register("palette", _ => { });
                controller.Open("palette");

                await Assert.That(stack.ChangedSubscribers).IsEqualTo(1);
                await Assert.That(stack.PoppedSubscribers).IsEqualTo(1);
            }

            // The controller is out of scope. The singleton stack must no longer
            // hold either handler — otherwise every swap leaks one more.
            await Assert.That(stack.ChangedSubscribers)
                .IsEqualTo(0)
                .Because($"cycle {i} left a Changed subscriber on the singleton stack");
            await Assert.That(stack.PoppedSubscribers)
                .IsEqualTo(0)
                .Because($"cycle {i} left a Popped subscriber on the singleton stack");
        }

        await Assert.That(stack.ChangedSubscriberPeak).IsEqualTo(1);
        await Assert.That(stack.PoppedSubscriberPeak).IsEqualTo(1);
    }

    /// <summary>
    ///     Issue #476 — the leaked subscriber is a correctness hazard, not just a
    ///     leak: a controller whose renderer is already gone must not react to a
    ///     later stack event. Here the handlers are replayed directly, modelling a
    ///     raise whose invocation list was captured before teardown. A live
    ///     controller on the same singleton stack is the control, so the assertions
    ///     prove the replay reached the stack and the dead one stayed inert —
    ///     rather than passing because the replay went nowhere.
    /// </summary>
    [Test]
    public async Task DisposedController_LateStackEvent_DoesNotPaint()
    {
        var stack = new CountingOverlayStack();
        var deadCalls = new List<bool>();
        var liveCalls = new List<bool>();

        var dead = new OverlayController(stack);
        dead.Register("settings", v => deadCalls.Add(v));
        dead.Open("settings");

        // Drain the stack so the control starts from HasOverlay == false; any
        // transition to true below is attributable to the replayed event.
        dead.CloseTop();
        deadCalls.Clear();

        using var live = new OverlayController(stack);
        live.Register("settings", v => liveCalls.Add(v));

        await Assert.That(live.HasOverlay).IsFalse();

        dead.Dispose();

        await Assert.That(dead.IsDisposed).IsTrue();
        await Assert.That(dead.HasOverlay).IsFalse();
        await Assert.That(stack.ChangedSubscribers).IsEqualTo(1)
            .Because("only the live controller should remain attached");

        // A late Changed must not resurrect a dead controller's overlay state.
        stack.RaisePushToStaleSubscribers("settings");

        await Assert.That(live.HasOverlay).IsTrue().Because("the control must have reacted");
        await Assert.That(dead.HasOverlay).IsFalse()
            .Because("a disposed controller must not paint a frame for a gone renderer");
        await Assert.That(deadCalls).IsEmpty();

        // A late Popped must not drive a setter bound to a torn-down view model.
        stack.RaisePopToStaleSubscribers();

        await Assert.That(liveCalls).Count().IsEqualTo(1).Because("the control must have reacted");
        await Assert.That(liveCalls[0]).IsFalse();
        await Assert.That(deadCalls).IsEmpty();
    }

    /// <summary>
    ///     Issue #476 — after <c>Dispose</c> the controller is inert:
    ///     acquire-side calls (<see cref="OverlayController.Open" /> /
    ///     <see cref="OverlayController.Register" />) fail fast, release-side
    ///     calls are idempotent no-ops, and neither touches the shared stack.
    /// </summary>
    [Test]
    public async Task DisposedController_RejectsAcquire_AndNoOpsRelease()
    {
        var stack = new CountingOverlayStack();
        var calls = new List<bool>();
        var controller = new OverlayController(stack);
        controller.Register("settings", v => calls.Add(v));
        controller.Open("settings");
        calls.Clear();

        controller.Dispose();

        var open = Assert.Throws<ObjectDisposedException>(() => controller.Open("settings"));
        await Assert.That(open.ObjectName).IsEqualTo(typeof(OverlayController).FullName);
        Assert.Throws<ObjectDisposedException>(() => controller.Register("other", _ => { }));

        // Release stays usable: teardown races deliver these after dispose.
        controller.Close("settings");
        await Assert.That(controller.CloseTop()).IsFalse();

        // Nothing was pushed or popped on the shared singleton stack.
        await Assert.That(stack.PushCalls).IsEqualTo(1);
        await Assert.That(stack.PopCalls).IsEqualTo(0);
        await Assert.That(stack.Current).IsEqualTo("settings");
        await Assert.That(calls).IsEmpty();
    }

    /// <summary>
    ///     <see cref="OverlayController.Dispose" /> is idempotent — a double
    ///     teardown must not double-decrement the stack's subscriber bookkeeping
    ///     or throw.
    /// </summary>
    [Test]
    public async Task Dispose_IsIdempotent()
    {
        var stack = new CountingOverlayStack();
        var controller = new OverlayController(stack);

        controller.Dispose();
        controller.Dispose();

        await Assert.That(controller.IsDisposed).IsTrue();
        await Assert.That(stack.ChangedSubscribers).IsEqualTo(0);
        await Assert.That(stack.PoppedSubscribers).IsEqualTo(0);
    }

    /// <summary>
    ///     Sanity guard for the harness: the counting stack really does observe
    ///     subscriptions, so a flat subscriber count above means "detached", not
    ///     "never subscribed".
    /// </summary>
    [Test]
    public async Task CountingStack_ObservesSubscription_Lifecycle()
    {
        var stack = new CountingOverlayStack();
        var controller = new OverlayController(stack);

        await Assert.That(stack.ChangedSubscribers).IsEqualTo(1);
        await Assert.That(stack.PoppedSubscribers).IsEqualTo(1);

        controller.Dispose();

        await Assert.That(stack.ChangedSubscribers).IsEqualTo(0);
        await Assert.That(stack.PoppedSubscribers).IsEqualTo(0);
    }

    /// <summary>
    ///     <see cref="IOverlayStack" /> double that counts live subscribers on
    ///     both events and can replay events to handlers that have already
    ///     unsubscribed (modelling a raise whose invocation list was captured
    ///     before teardown ran).
    /// </summary>
    private sealed class CountingOverlayStack : IOverlayStack
    {
        private readonly Stack<string> _stack = new();

        // Append-only record of every handler that was ever attached. Detaching
        // never prunes it, so Raise*ToStaleSubscribers can replay to a handler
        // that already unsubscribed — the "raise in flight during teardown" case.
        private readonly List<Action<string?, IReadOnlyList<string>>> _everChanged = new();
        private readonly List<Action<string?>> _everPopped = new();

        private Action<string?, IReadOnlyList<string>>? _changed;
        private Action<string?>? _popped;
        private int _changedSubscribers;
        private int _poppedSubscribers;

        public event Action<string?, IReadOnlyList<string>>? Changed
        {
            add
            {
                _everChanged.Add(value);
                _changed += value;
                _changedSubscribers++;
                ChangedSubscriberPeak = Math.Max(ChangedSubscriberPeak, _changedSubscribers);
            }
            remove
            {
                _changed -= value;
                _changedSubscribers--;
            }
        }

        public event Action<string?>? Popped
        {
            add
            {
                _everPopped.Add(value);
                _popped += value;
                _poppedSubscribers++;
                PoppedSubscriberPeak = Math.Max(PoppedSubscriberPeak, _poppedSubscribers);
            }
            remove
            {
                _popped -= value;
                _poppedSubscribers--;
            }
        }

        public string? Current => _stack.Count > 0 ? _stack.Peek() : null;

        public IReadOnlyList<string> Stack => _stack.ToArray();

        /// <summary>Handlers currently attached to <c>Changed</c>.</summary>
        public int ChangedSubscribers => _changedSubscribers;

        /// <summary>Handlers currently attached to <c>Popped</c>.</summary>
        public int PoppedSubscribers => _poppedSubscribers;

        /// <summary>High-water mark of concurrent <c>Changed</c> subscribers.</summary>
        public int ChangedSubscriberPeak { get; private set; }

        /// <summary>High-water mark of concurrent <c>Popped</c> subscribers.</summary>
        public int PoppedSubscriberPeak { get; private set; }

        public int PushCalls { get; private set; }

        public int PopCalls { get; private set; }

        public void Push(string id)
        {
            PushCalls++;
            if (string.IsNullOrEmpty(id)) return;
            if (_stack.Count > 0 && _stack.Peek() == id) return;
            _stack.Push(id);
            _changed?.Invoke(Current, Stack);
        }

        public string? PopTop()
        {
            PopCalls++;
            if (_stack.Count == 0) return null;
            var popped = _stack.Pop();
            _changed?.Invoke(Current, Stack);
            _popped?.Invoke(popped);
            return popped;
        }

        /// <summary>
        ///     Replays a full <c>Push</c> — stack mutation included — to every
        ///     handler ever attached, including ones that already unsubscribed.
        ///     Models a raise whose invocation list was captured before teardown.
        ///     A disposed controller must no-op; a live one must react.
        /// </summary>
        public void RaisePushToStaleSubscribers(string id)
        {
            if (_stack.Count == 0 || _stack.Peek() != id) _stack.Push(id);
            foreach (var handler in _everChanged.ToArray())
                handler(Current, Stack);
        }

        /// <summary>
        ///     Replays a full <c>PopTop</c> — stack mutation included — to every
        ///     handler ever attached, including ones that already unsubscribed.
        ///     A disposed controller must no-op; a live one must react.
        /// </summary>
        public void RaisePopToStaleSubscribers()
        {
            if (_stack.Count == 0) return;
            var popped = _stack.Pop();
            foreach (var handler in _everPopped.ToArray())
                handler(popped);
        }
    }
}
