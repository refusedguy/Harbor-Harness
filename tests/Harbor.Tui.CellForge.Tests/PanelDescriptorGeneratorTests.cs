using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2430: the generator rejects a panel descriptor that does not
///     declare its state type, and accepts one that does.
/// </summary>
public class PanelDescriptorGeneratorTests
{
    private const string Contract = """
        namespace Dsl
        {
            public interface IPanelDescriptor
            {
                System.Type StateType { get; }
                bool Match(object state);
                object Build(object state);
            }
        }
        """;

    [Test]
    public async Task Descriptor_WithStateType_ReportsNoDiagnostic()
    {
        const string source = """
            using System;
            using Dsl;
            public sealed class CounterPanel : IPanelDescriptor
            {
                public Type StateType => typeof(CounterState);
                public bool Match(object state) => state is CounterState;
                public object Build(object state) => "counter";
            }
            public sealed class CounterState { }
            """;

        var run = Run(source);

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Descriptor_WithoutStateType_ReportsHui002()
    {
        const string source = """
            using Dsl;
            public sealed class CounterPanel : IPanelDescriptor
            {
                public bool Match(object state) => true;
                public object Build(object state) => "counter";
            }
            """;

        var run = Run(source);

        await Assert.That(run.Diagnostics.Length).IsEqualTo(1);
        await Assert.That(run.Diagnostics[0].Id).IsEqualTo(PanelDescriptorGenerator.MissingStateTypeDiagnosticId);
        await Assert.That(run.Diagnostics[0].GetMessage()).Contains("CounterPanel");
    }

    [Test]
    public async Task Descriptor_StateTypeOnlyOnBase_ReportsHui002()
    {
        const string source = """
            using System;
            using Dsl;
            public abstract class BasePanel : IPanelDescriptor
            {
                public Type StateType => typeof(object);
                public abstract bool Match(object state);
                public abstract object Build(object state);
            }
            public sealed class ChildPanel : BasePanel
            {
                public override bool Match(object state) => true;
                public override object Build(object state) => "child";
            }
            """;

        var run = Run(source);

        // BasePanel declares StateType; ChildPanel inherits the interface but
        // does not declare the property, so the chain cannot see its type.
        await Assert.That(run.Diagnostics.Length).IsEqualTo(1);
        await Assert.That(run.Diagnostics[0].GetMessage()).Contains("ChildPanel");
    }

    [Test]
    public async Task UnrelatedClass_IsIgnored()
    {
        const string source = """
            public sealed class NotAPanel
            {
                public System.Type StateType => typeof(object);
            }
            """;

        var run = Run(source);

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
    }

    private static GeneratorDriverRunResult Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "DescriptorCheck",
            [CSharpSyntaxTree.ParseText(Contract), CSharpSyntaxTree.ParseText(source)]);
        var driver = CSharpGeneratorDriver.Create(
            generators: [new PanelDescriptorGenerator().AsSourceGenerator()]);
        return driver.RunGenerators(compilation).GetRunResult();
    }
}
