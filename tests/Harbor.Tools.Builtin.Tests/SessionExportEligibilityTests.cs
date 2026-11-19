using CSharpFunctionalExtensions;
using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #2680 F173. <see cref="SessionExportEligibility" /> is a pure policy leaf.
///     These tests pin how a supplied kill-switch value is resolved without
///     setting or reading a process environment variable.
/// </summary>
public class SessionExportEligibilityTests
{
    [Test]
    public async Task AbsentOrBlankValueMeansEnabled()
    {
        await Assert.That(SessionExportEligibility.Classify(null)).IsEqualTo(ExportSwitchDecision.Enabled);
        await Assert.That(SessionExportEligibility.Classify(string.Empty)).IsEqualTo(ExportSwitchDecision.Enabled);
        await Assert.That(SessionExportEligibility.Classify("  \t ")).IsEqualTo(ExportSwitchDecision.Enabled);

        await Assert.That(SessionExportEligibility.IsEnabled(null)).IsTrue();
        await Assert.That(SessionExportEligibility.IsEnabled("   ")).IsTrue();
    }

    [Test]
    public async Task RecognisedValuesResolveToExpectedDecision()
    {
        (string Value, ExportSwitchDecision Expected)[] cases =
        [
            ("1", ExportSwitchDecision.Enabled),
            ("true", ExportSwitchDecision.Enabled),
            ("TRUE", ExportSwitchDecision.Enabled),
            ("yes", ExportSwitchDecision.Enabled),
            ("Yes", ExportSwitchDecision.Enabled),
            ("on", ExportSwitchDecision.Enabled),
            ("ON", ExportSwitchDecision.Enabled),
            ("0", ExportSwitchDecision.Disabled),
            ("false", ExportSwitchDecision.Disabled),
            ("FALSE", ExportSwitchDecision.Disabled),
            ("no", ExportSwitchDecision.Disabled),
            ("No", ExportSwitchDecision.Disabled),
            ("off", ExportSwitchDecision.Disabled),
            ("OFF", ExportSwitchDecision.Disabled),
        ];

        foreach ((string value, ExportSwitchDecision expected) in cases)
        {
            await Assert.That(SessionExportEligibility.Classify(value)).IsEqualTo(expected);
            await Assert.That(SessionExportEligibility.IsEnabled(value))
                .IsEqualTo(expected is ExportSwitchDecision.Enabled);
        }
    }

    [Test]
    public async Task ValuesAreTrimmedBeforeClassification()
    {
        await Assert.That(SessionExportEligibility.Classify("  false  ")).IsEqualTo(ExportSwitchDecision.Disabled);
        await Assert.That(SessionExportEligibility.Classify("\tyes\n")).IsEqualTo(ExportSwitchDecision.Enabled);
        await Assert.That(SessionExportEligibility.IsEnabled("  ON \t")).IsTrue();
    }

    [Test]
    public async Task UnrecognisedValueIsInvalidAndFailsClosed()
    {
        string[] invalidValues = ["maybe", "2", "-1", "enabled", "trueish", "true false"];

        foreach (string value in invalidValues)
        {
            await Assert.That(SessionExportEligibility.Classify(value)).IsEqualTo(ExportSwitchDecision.Invalid);
            await Assert.That(SessionExportEligibility.IsEnabled(value)).IsFalse();
            await Assert.That(SessionExportEligibility.TryParse(value, out bool enabled)).IsFalse();
            await Assert.That(enabled).IsFalse();
        }
    }

    [Test]
    public async Task TryParseDistinguishesValidDenyFromBadValue()
    {
        await Assert.That(SessionExportEligibility.TryParse("false", out bool denied)).IsTrue();
        await Assert.That(denied).IsFalse();

        await Assert.That(SessionExportEligibility.TryParse("true", out bool allowed)).IsTrue();
        await Assert.That(allowed).IsTrue();

        await Assert.That(SessionExportEligibility.TryParse(null, out bool defaulted)).IsTrue();
        await Assert.That(defaulted).IsTrue();
    }

    [Test]
    public async Task EnvironmentVariableNameIsStable()
    {
        await Assert.That(SessionExportEligibility.EnvironmentVariableName).IsEqualTo("HARBOR_EXPORT_ENABLED");
    }

    // #3399 behavior: the eligibility policy is now the gate in front of the
    // real session-export path. These tests drive the same Result-returning
    // guard the CLI export verbs call, and assert that an ineligible export is
    // refused WITHOUT ever invoking the operation that reads/writes the file.

    [Test]
    public async Task EnsureAllowed_ReturnsSuccessWhenEnabled()
    {
        Result result = SessionExportEligibility.EnsureAllowed("yes");
        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task EnsureAllowed_ReturnsFailureWithReasonWhenDisabled()
    {
        Result result = SessionExportEligibility.EnsureAllowed("off");
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains(SessionExportEligibility.EnvironmentVariableName);
        await Assert.That(result.Error).Contains("disabled");
        await Assert.That(result.Error).Contains("off");
    }

    [Test]
    public async Task EnsureAllowed_ReturnsFailureWithReasonWhenInvalid()
    {
        Result result = SessionExportEligibility.EnsureAllowed("maybe");
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains(SessionExportEligibility.EnvironmentVariableName);
        await Assert.That(result.Error).Contains("unrecognised value");
        await Assert.That(result.Error).Contains("maybe");
    }

    [Test]
    public async Task DisabledSession_IsNotExported()
    {
        bool exportInvoked = false;
        Result result = await SessionExportEligibility.Guard(
            () =>
            {
                exportInvoked = true;
                return Task.FromResult(Result.Success());
            },
            "0");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(exportInvoked).IsFalse();
        await Assert.That(result.Error).Contains("disabled");
    }

    [Test]
    public async Task InvalidSwitch_DoesNotInvokeExport()
    {
        bool exportInvoked = false;
        Result result = await SessionExportEligibility.Guard(
            () =>
            {
                exportInvoked = true;
                return Task.FromResult(Result.Success());
            },
            "not-a-switch");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(exportInvoked).IsFalse();
        await Assert.That(result.Error).Contains("unrecognised value");
    }

    [Test]
    public async Task EnabledSession_IsExported()
    {
        bool exportInvoked = false;
        Result result = await SessionExportEligibility.Guard(
            () =>
            {
                exportInvoked = true;
                return Task.FromResult(Result.Failure("export exploded"));
            },
            null);

        await Assert.That(exportInvoked).IsTrue();
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("export exploded");
    }
}
