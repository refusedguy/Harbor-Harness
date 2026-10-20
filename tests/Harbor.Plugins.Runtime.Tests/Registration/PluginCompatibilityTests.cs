using Harbor.Plugins.Registration;

namespace Harbor.Plugins.Runtime.Tests.Registration;

/// <summary>
///     Tests for the #1721 compatibility rule: whether a plugin that declares a
///     <c>RequiredHarborVersion</c> may run against the Harbor that is running.
/// </summary>
public sealed class PluginCompatibilityTests
{
    /// <summary>
    ///     The boundary: a plugin that asks for exactly the running version is
    ///     compatible. "At least this" includes "exactly this".
    /// </summary>
    [Test]
    public async Task IsCompatible_RuntimeEqualsRequired_IsCompatible()
    {
        await Assert.That(PluginCompatibility.IsCompatible(new Version(0, 4, 0), new Version(0, 4, 0))).IsTrue();
    }

    /// <summary>
    ///     A plugin that asks for an OLDER Harbor than the running one is compatible:
    ///     the declaration is a floor, and the running Harbor is above it.
    /// </summary>
    [Test]
    public async Task IsCompatible_RuntimeNewerThanRequired_IsCompatible()
    {
        await Assert.That(PluginCompatibility.IsCompatible(new Version(1, 0, 0), new Version(0, 4, 0))).IsTrue()
            .Because("RequiredHarborVersion is a minimum, not a pin: a Harbor newer than the plugin "
                   + "asks for still satisfies it");
    }

    /// <summary>
    ///     A plugin that asks for a NEWER Harbor than the running one is not
    ///     compatible. This is the refusal the gate exists for.
    /// </summary>
    [Test]
    public async Task IsCompatible_RuntimeOlderThanRequired_IsNotCompatible()
    {
        await Assert.That(PluginCompatibility.IsCompatible(new Version(0, 4, 0), new Version(0, 6, 0))).IsFalse()
            .Because("the plugin declares it needs a Harbor this runtime is not");
    }

    /// <summary>
    ///     The plugin spec lets a plugin declare major+minor only, so "0.4" and
    ///     "0.4.0" name the same Harbor. Reading them through Version's own ordering
    ///     would make "0.4" older than "0.4.0" and refuse a plugin the runtime does
    ///     satisfy.
    /// </summary>
    [Test]
    public async Task IsCompatible_MajorMinorForm_MatchesTheFullFormOfTheSameHarbor()
    {
        await Assert.That(PluginCompatibility.IsCompatible(new Version(0, 4, 0), new Version(0, 4))).IsTrue();
        await Assert.That(PluginCompatibility.IsCompatible(new Version(0, 4), new Version(0, 4, 0))).IsTrue();
    }

    /// <summary>
    ///     A plugin that declares no floor has nothing to enforce, so it is not
    ///     refused. The registrar logs the missing declaration separately.
    /// </summary>
    [Test]
    public async Task IsCompatible_NoDeclaredFloor_IsCompatible()
    {
        await Assert.That(PluginCompatibility.IsCompatible(new Version(0, 4, 0), null)).IsTrue();
    }

    /// <summary>
    ///     A compatible plugin passes the gate.
    /// </summary>
    [Test]
    public async Task Check_Compatible_Succeeds()
    {
        var result = PluginCompatibility.Check("web-search", new Version(0, 4, 0), new Version(0, 4, 0));

        await Assert.That(result.IsSuccess).IsTrue();
    }

    /// <summary>
    ///     An incompatible plugin fails the gate, and the refusal names the plugin,
///     the Harbor version it needs and the Harbor version that is running. A
///     refusal that only says "something went wrong" sends the user hunting.
    /// </summary>
    [Test]
    public async Task Check_Incompatible_FailsNamingThePluginTheFloorAndTheRuntime()
    {
        var result = PluginCompatibility.Check("web-search", new Version(0, 6, 0), new Version(0, 4, 0));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("web-search");
        await Assert.That(result.Error).Contains("0.6.0");
        await Assert.That(result.Error).Contains("0.4.0");
    }

    /// <summary>
    ///     A plugin that declares no floor passes the gate.
    /// </summary>
    [Test]
    public async Task Check_NoDeclaredFloor_Succeeds()
    {
        var result = PluginCompatibility.Check("web-search", null, new Version(0, 4, 0));

        await Assert.That(result.IsSuccess).IsTrue();
    }
}
