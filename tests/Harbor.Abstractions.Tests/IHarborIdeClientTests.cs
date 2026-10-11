using Harbor.Abstractions.Ide;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Abstractions.Tests;

public class IHarborIdeClientTests
{
    [Test]
    public void Interface_Compiles_And_Is_Assignable()
    {
        // This test ensures the interface and its related types compile
        // and can be referenced. A concrete implementation test would
        // live in an integration test project.
        var methods = typeof(IHarborIdeClient).GetMethods();
        await Assert.That(methods.Length).IsGreaterThan(0);
        
        var processResultProps = typeof(ProcessResult).GetProperties();
        await Assert.That(processResultProps.Length).IsEqualTo(3); // ExitCode, Stdout, Stderr
        
        var processHandleProps = typeof(IProcessHandle).GetProperties();
        await Assert.That(processHandleProps.Length).IsGreaterThanOrEqualTo(1); // ProcessId
        
        var signalValues = Enum.GetValues<ProcessSignal>();
        await Assert.That(signalValues.Length).IsEqualTo(3); // Interrupt, Terminate, Kill
    }
}
