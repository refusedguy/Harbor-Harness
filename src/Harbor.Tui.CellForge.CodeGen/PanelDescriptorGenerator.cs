using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>
///     Build-time check for the panel-descriptor chain: a class that declares
///     <c>IPanelDescriptor</c> must declare a <c>StateType</c> property.
/// </summary>
/// <remarks>
///     A descriptor without a state type has no compile-time contract with the
///     state it handles, so the chain silently accepts anything its
///     <c>Match</c> returns true for. The check mirrors Avalonia's typed
///     template validation: the type declaration is part of the rule, not an
///     optional hint. The diagnostic is an error, so an undeclared state type
///     breaks the build at the descriptor instead of painting a wrong panel.
///     <para>
///         The generator reads syntax and the semantic model only. It emits no
///         source and uses no reflection, dynamic code or emit.
///     </para>
/// </remarks>
[Generator]
public sealed class PanelDescriptorGenerator : IIncrementalGenerator
{
    /// <summary>Diagnostic id for a descriptor without a declared state type.</summary>
    public const string MissingStateTypeDiagnosticId = "HUI002";

    private const string DescriptorInterface = "IPanelDescriptor";
    private const string StateTypeProperty = "StateType";

    private static readonly DiagnosticDescriptor MissingStateTypeRule = new(
        id: MissingStateTypeDiagnosticId,
        title: "Panel descriptor must declare StateType",
        messageFormat: "Panel descriptor '{0}' does not declare StateType",
        category: "CellForge.Panels",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every IPanelDescriptor declares the state type it was written for.");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var descriptors = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => IsDescriptorDeclaration(node),
                static (ctx, ct) => Analyze(ctx, ct))
            .WithTrackingName("PanelDescriptorGenerator.Descriptors");

        context.RegisterSourceOutput(descriptors, static (spc, candidate) =>
        {
            if (candidate.DeclaresStateType)
                return;

            spc.ReportDiagnostic(Diagnostic.Create(
                MissingStateTypeRule,
                candidate.Location,
                candidate.Name));
        });
    }

    private static bool IsDescriptorDeclaration(SyntaxNode node) =>
        node is ClassDeclarationSyntax { BaseList: not null };

    private static DescriptorCandidate Analyze(GeneratorSyntaxContext context, System.Threading.CancellationToken ct)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(declaration, ct) as INamedTypeSymbol;
        bool isDescriptor = false;
        bool declaresStateType = false;

        if (symbol is not null)
        {
            foreach (var iface in symbol.AllInterfaces)
            {
                if (iface.Name == DescriptorInterface)
                {
                    isDescriptor = true;
                    break;
                }
            }

            if (isDescriptor)
            {
                foreach (var member in symbol.GetMembers(StateTypeProperty))
                {
                    if (member is IPropertySymbol)
                    {
                        declaresStateType = true;
                        break;
                    }
                }
            }
        }

        return new DescriptorCandidate(
            declaration.Identifier.ValueText,
            !isDescriptor || declaresStateType,
            Location.Create(declaration.SyntaxTree, declaration.Identifier.Span));
    }

    private sealed record DescriptorCandidate(string Name, bool DeclaresStateType, Location Location);
}
