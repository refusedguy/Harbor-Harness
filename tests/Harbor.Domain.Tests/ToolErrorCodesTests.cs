using Harbor.Abstractions.Models;
using TUnit.Assertions;

namespace Harbor.Domain.Tests;

/// <summary>
///     #1760: the structured tool-error vocabulary.
/// </summary>
/// <remarks>
///     <para>
///         A code is a stable promise; the message beside it is free text.
///         These tests pin the three properties the rest of the system leans
///         on: the codes are stable, an UNKNOWN code is legal (it is carried,
///         not swallowed), and the name a code gets does not depend on the
///         build knowing it.
///     </para>
/// </remarks>
public class ToolErrorCodesTests
{
    [Test]
    public async Task IsKnown_EveryEmittedCode_IsTrue()
    {
        // The vocabulary Harbor emits. A code that is NOT in this list is
        // still legal (see the unknown-code test) — but every code Harbor
        // itself produces must be recognised by the build that produced it.
        string[] emitted =
        [
            ToolErrorCodes.Error,
            ToolErrorCodes.Timeout,
            ToolErrorCodes.AbortedBeforeDispatch,
            ToolErrorCodes.NotStarted,
            ToolErrorCodes.OutcomeUnknown,
            ToolErrorCodes.NotFound,
            ToolErrorCodes.ValidationFailed,
            ToolErrorCodes.PermissionDenied,
            ToolErrorCodes.PermissionFailed,
            ToolErrorCodes.HookDenied,
            ToolErrorCodes.Cancelled,
            ToolErrorCodes.Abandoned,
            ToolErrorCodes.ExecutionFailed,
            ToolErrorCodes.MalformedArguments,
        ];

        foreach (string code in emitted)
        {
            await Assert.That(ToolErrorCodes.IsKnown(code)).IsTrue();
            await Assert.That(ToolErrorCodes.NameFor(code)).IsNotEqualTo("ToolError");
        }
    }

    [Test]
    public async Task IsKnown_UnknownCode_IsFalse_AndTheCodeIsCarried()
    {
        // A plugin may return a code this build has never heard of. That is
        // not a defect: the code is carried verbatim, it reaches the model,
        // and it survives storage. "Not known" only means "this build has no
        // name and no policy for it".
        const string unknown = "PLUGIN_TOOL_SOMETHING";

        await Assert.That(ToolErrorCodes.IsKnown(unknown)).IsFalse();
        await Assert.That(ToolErrorCodes.IsKnown(null)).IsFalse();

        ToolErrorInfo error = ToolErrorCodes.Create(unknown, "the plugin said so");
        await Assert.That(error.Code).IsEqualTo(unknown);
        await Assert.That(error.Reason).IsEqualTo("the plugin said so");
        // The shape never depends on the vocabulary: an unknown code still
        // gets a name.
        await Assert.That(error.Name).IsEqualTo("ToolError");
    }

    [Test]
    public async Task Create_KnownCode_CarriesTheCanonicalName()
    {
        ToolErrorInfo error = ToolErrorCodes.Create(ToolErrorCodes.Timeout, "deadline 1000ms");

        await Assert.That(error.Code).IsEqualTo("TOOL_TIMEOUT");
        await Assert.That(error.Name).IsEqualTo("ToolTimeoutError");
        await Assert.That(error.Reason).IsEqualTo("deadline 1000ms");
    }

    [Test]
    public async Task NotStartedAndOutcomeUnknown_AreTheUnresolvedCallCodes()
    {
        // The two codes #1751 already owns must not be spelled twice: the
        // unresolved-call repair and the tool-error vocabulary are one
        // vocabulary.
        await Assert.That(ToolErrorCodes.NotStarted).IsEqualTo(UnresolvedToolCall.NotStartedCode);
        await Assert.That(ToolErrorCodes.OutcomeUnknown).IsEqualTo(UnresolvedToolCall.OutcomeUnknownCode);
        await Assert.That(UnresolvedToolCall.NotStartedText("read"))
            .StartsWith(ToolErrorCodes.NotStarted);
        await Assert.That(UnresolvedToolCall.OutcomeUnknownText("read"))
            .StartsWith(ToolErrorCodes.OutcomeUnknown);
    }

    [Test]
    public async Task Format_PutsTheCodeInFront()
    {
        await Assert.That(ToolErrorCodes.Format(ToolErrorCodes.Timeout, "Tool 'x' timed out after 1s."))
            .IsEqualTo("TOOL_TIMEOUT: Tool 'x' timed out after 1s.");
    }

    [Test]
    public async Task FailedResult_LeadsWithItsCode_AndCarriesIt()
    {
        // The invariant the whole pipeline leans on: a failed result's text
        // starts with its code, and the structured field says the same thing.
        ToolResult result = ToolResult.Failed(
            "Tool 'x' timed out after 1s.",
            ToolErrorCodes.Create(ToolErrorCodes.Timeout));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).IsEqualTo("TOOL_TIMEOUT: Tool 'x' timed out after 1s.");
        await Assert.That(result.ErrorInfo!.Code).IsEqualTo(ToolErrorCodes.Timeout);
    }

    [Test]
    public async Task SuccessResult_CarriesNoError()
    {
        ToolResult result = ToolResult.Success("done");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.ErrorInfo).IsNull();
    }
}
