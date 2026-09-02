using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Spring physics (HDS v1 panel-resize motion): determinism, overshoot,
/// settling, and snap contract. All assertions run on discrete frames —
/// no wall clock anywhere.
/// </summary>
public class SpringFxTests
{
    [Test]
    public async Task Step_SettledSpring_IsNoOp()
    {
        var spring = new SpringFx(0.5);
        double a = spring.Step();
        double b = spring.Step();

        await Assert.That(a).IsEqualTo(0.5);
        await Assert.That(b).IsEqualTo(0.5);
        await Assert.That(spring.Settled).IsTrue();
    }

    [Test]
    public async Task Retarget_Animates_AndSettlesOnTarget()
    {
        var spring = new SpringFx(0.5);
        spring.Retarget(0.9);

        for (int i = 0; i < 120 && !spring.Settled; i++)
        {
            _ = spring.Step();
        }

        await Assert.That(spring.Settled).IsTrue();
        await Assert.That(spring.Position).IsEqualTo(0.9);
        await Assert.That(spring.Velocity).IsEqualTo(0);
    }

    [Test]
    public async Task Step_OvershootsTarget_LightSpringFeel()
    {
        var spring = new SpringFx(0.0);
        spring.Retarget(1.0);

        bool crossed = false;
        for (int i = 0; i < 40 && !spring.Settled; i++)
        {
            _ = spring.Step();
            crossed |= spring.Position > 1.0;
        }

        await Assert.That(crossed).IsTrue(); // ζ=0.5 ⇒ harmonica-style ~11 % overshoot
    }

    [Test]
    public async Task Step_IsDeterministic()
    {
        double[] a = Trace(0.2, 0.8);
        double[] b = Trace(0.2, 0.8);

        for (int i = 0; i < a.Length; i++)
        {
            await Assert.That(a[i]).IsEqualTo(b[i]);
        }
    }

    private static double[] Trace(double from, double to)
    {
        var spring = new SpringFx(from);
        spring.Retarget(to);
        var path = new double[24];
        for (int i = 0; i < path.Length; i++)
        {
            path[i] = spring.Step();
        }

        return path;
    }

    [Test]
    public async Task SnapTo_TeleportsWithoutAnimation()
    {
        var spring = new SpringFx(0.0);
        spring.Retarget(1.0);
        _ = spring.Step(); // mid-flight
        spring.SnapTo(0.3);

        await Assert.That(spring.Position).IsEqualTo(0.3);
        await Assert.That(spring.Target).IsEqualTo(0.3);
        await Assert.That(spring.Velocity).IsEqualTo(0);
        await Assert.That(spring.Settled).IsTrue();
        await Assert.That(spring.Step()).IsEqualTo(0.3);
    }

    [Test]
    public async Task Retarget_SameTarget_DoesNotStartMotion()
    {
        var spring = new SpringFx(0.4);
        spring.Retarget(0.4);

        await Assert.That(spring.Settled).IsTrue();
        await Assert.That(spring.Step()).IsEqualTo(0.4);
    }

    [Test]
    public async Task Step_WorksInBothDirections()
    {
        var down = new SpringFx(0.9);
        down.Retarget(0.1);
        for (int i = 0; i < 120 && !down.Settled; i++)
        {
            _ = down.Step();
        }

        await Assert.That(down.Settled).IsTrue();
        await Assert.That(down.Position).IsEqualTo(0.1);
    }

    [Test]
    public async Task Step_Decelerates_AfterInitialKick()
    {
        var spring = new SpringFx(0.0);
        spring.Retarget(1.0);

        double earlyMax = 0;
        for (int i = 0; i < 10; i++)
        {
            _ = spring.Step();
            earlyMax = Math.Max(earlyMax, Math.Abs(spring.Velocity));
        }

        double lateMax = 0;
        for (int i = 0; i < 50 && !spring.Settled; i++)
        {
            _ = spring.Step();
            if (i >= 40)
            {
                lateMax = Math.Max(lateMax, Math.Abs(spring.Velocity));
            }
        }

        await Assert.That(earlyMax).IsGreaterThan(lateMax);
    }
}
