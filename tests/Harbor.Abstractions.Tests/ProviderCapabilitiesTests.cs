// ProviderCapabilitiesTests.cs. Issue #2673, item D1.
//
// A provider name is not a capability. These tests pin the value contract:
// undeclared means unsupported, and degrading to a provider's real features
// is an intersection rather than a mutation of the requested set.

using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Tests;

public class ProviderCapabilitiesTests
{
    [Test]
    public async Task NewCapabilities_AreEmpty()
    {
        var capabilities = new ProviderCapabilities();

        await Assert.That(capabilities.SupportsStreaming).IsFalse();
        await Assert.That(capabilities.SupportsToolCalls).IsFalse();
        await Assert.That(capabilities.SupportsInterrupt).IsFalse();
        await Assert.That(capabilities.SupportsSteer).IsFalse();
        await Assert.That(capabilities.SupportsNativeFork).IsFalse();
        await Assert.That(capabilities.SupportsRollback).IsFalse();
        await Assert.That(capabilities.EmitsTodoList).IsFalse();
        await Assert.That(capabilities.SupportsPromptCaching).IsFalse();
        await Assert.That(capabilities.Version).IsNull();
        await Assert.That(capabilities.IsEmpty).IsTrue();
    }

    [Test]
    public async Task DeclaredFlags_AreReadBack()
    {
        var capabilities = new ProviderCapabilities(
            supportsStreaming: true,
            supportsToolCalls: true,
            supportsInterrupt: true,
            supportsSteer: true,
            supportsNativeFork: true,
            supportsRollback: true,
            emitsTodoList: true,
            supportsPromptCaching: true,
            version: "v2");

        await Assert.That(capabilities.SupportsStreaming).IsTrue();
        await Assert.That(capabilities.SupportsToolCalls).IsTrue();
        await Assert.That(capabilities.SupportsInterrupt).IsTrue();
        await Assert.That(capabilities.SupportsSteer).IsTrue();
        await Assert.That(capabilities.SupportsNativeFork).IsTrue();
        await Assert.That(capabilities.SupportsRollback).IsTrue();
        await Assert.That(capabilities.EmitsTodoList).IsTrue();
        await Assert.That(capabilities.SupportsPromptCaching).IsTrue();
        await Assert.That(capabilities.Version).IsEqualTo("v2");
        await Assert.That(capabilities.IsEmpty).IsFalse();
    }

    [Test]
    public async Task BlankVersion_IsStoredAsNull()
    {
        var capabilities = new ProviderCapabilities(supportsStreaming: true, version: "  ");

        await Assert.That(capabilities.Version).IsNull();
        await Assert.That(capabilities.IsEmpty).IsFalse();
    }

    [Test]
    public async Task Intersect_KeepsOnlyCommonFlags()
    {
        var requested = new ProviderCapabilities(
            supportsStreaming: true,
            supportsToolCalls: true,
            supportsInterrupt: true,
            supportsSteer: true,
            supportsNativeFork: true,
            supportsRollback: true,
            emitsTodoList: true,
            supportsPromptCaching: true,
            version: "v2");
        var provider = new ProviderCapabilities(
            supportsStreaming: true,
            supportsInterrupt: true,
            version: "v2");

        ProviderCapabilities effective = requested.Intersect(provider);

        await Assert.That(effective.SupportsStreaming).IsTrue();
        await Assert.That(effective.SupportsToolCalls).IsFalse();
        await Assert.That(effective.SupportsInterrupt).IsTrue();
        await Assert.That(effective.SupportsSteer).IsFalse();
        await Assert.That(effective.SupportsNativeFork).IsFalse();
        await Assert.That(effective.SupportsRollback).IsFalse();
        await Assert.That(effective.EmitsTodoList).IsFalse();
        await Assert.That(effective.SupportsPromptCaching).IsFalse();
        await Assert.That(effective.Version).IsEqualTo("v2");
        await Assert.That(requested.SupportsSteer).IsTrue();
        await Assert.That(provider.SupportsToolCalls).IsFalse();
    }

    [Test]
    public async Task Intersect_WithDifferentVersions_ClearsVersion()
    {
        var requested = new ProviderCapabilities(supportsStreaming: true, version: "v1");
        var provider = new ProviderCapabilities(supportsStreaming: true, version: "v2");

        ProviderCapabilities effective = requested.Intersect(provider);

        await Assert.That(effective.SupportsStreaming).IsTrue();
        await Assert.That(effective.Version).IsNull();
    }

    [Test]
    public async Task Intersect_KeepsTheOnlyDeclaredVersion()
    {
        var requested = new ProviderCapabilities(supportsSteer: true, version: "v3");
        var provider = new ProviderCapabilities(supportsSteer: true);

        await Assert.That(requested.Intersect(provider).Version).IsEqualTo("v3");
        await Assert.That(provider.Intersect(requested).Version).IsEqualTo("v3");
    }

    [Test]
    public async Task Intersect_WithUndeclaredProvider_ClearsEveryFlag()
    {
        var capabilities = new ProviderCapabilities(
            supportsStreaming: true,
            supportsToolCalls: true,
            version: "v2");

        ProviderCapabilities effective = capabilities.Intersect(new ProviderCapabilities());

        await Assert.That(effective.SupportsStreaming).IsFalse();
        await Assert.That(effective.SupportsToolCalls).IsFalse();
        await Assert.That(effective.Version).IsEqualTo("v2");
        await Assert.That(effective.IsEmpty).IsFalse();
    }

    [Test]
    public async Task Intersect_TwoEmpty_IsEmpty()
    {
        await Assert.That(new ProviderCapabilities().Intersect(new ProviderCapabilities()).IsEmpty).IsTrue();
    }
}
