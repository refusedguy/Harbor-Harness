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

        await Assert.That(calls).HasCount(1);
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

        await Assert.That(calls).HasCount(1);
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
}
