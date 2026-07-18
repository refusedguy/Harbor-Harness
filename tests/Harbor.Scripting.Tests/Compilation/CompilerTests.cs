// Compilation layer tests — PassThroughCompiler + TscCompiler.
using Harbor.Scripting.Compilation;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Scripting.Tests.Compilation;

public class PassThroughCompilerTests
{
    [Test]
    public async Task Compile_NonEmptySource_ReturnsSourceUnchanged()
    {
        var compiler = new PassThroughCompiler();

        var result = compiler.Compile("script.ts", "const x = 1;");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("const x = 1;");
    }

    [Test]
    public async Task Compile_EmptySource_ReturnsFailure()
    {
        var compiler = new PassThroughCompiler();

        var result = compiler.Compile("script.ts", "   ");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error).Contains("empty");
    }

    [Test]
    public async Task Compile_PreservesTypeScriptSyntax()
    {
        var compiler = new PassThroughCompiler();
        const string ts = "let x: number = 1;";

        var result = compiler.Compile("script.ts", ts);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(ts);
    }
}

public class TscCompilerTests
{
    [Test]
    public async Task Compile_JsSource_PassesThroughWithoutTsc()
    {
        // Even when tsc is not installed, .js sources must pass through.
        var compiler = new TscCompiler(NullLogger<TscCompiler>.Instance);

        var result = compiler.Compile("script.js", "var x = 1;");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("var x = 1;");
    }

    [Test]
    public async Task Compile_EmptySource_ReturnsFailure()
    {
        var compiler = new TscCompiler(NullLogger<TscCompiler>.Instance);

        var result = compiler.Compile("script.ts", "");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error).Contains("empty");
    }

    [Test]
    public async Task Compile_TsSource_WhenTscMissing_ReturnsActionableFailure()
    {
        var compiler = new TscCompiler(NullLogger<TscCompiler>.Instance);
        if (compiler.IsAvailable)
        {
            return; // Skip when tsc is installed — covered by integration tests.
        }

        var result = compiler.Compile("script.ts", "let x: number = 1;");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error).Contains("tsc");
    }
}
