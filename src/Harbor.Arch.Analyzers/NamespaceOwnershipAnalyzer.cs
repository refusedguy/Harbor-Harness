// NamespaceOwnershipAnalyzer.cs — HARCH002: a namespace root belongs to the
// project that declares it, decided by the compiler rather than by a file walk.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Arch.Analyzers;

/// <summary>
///     HARCH002 — a namespace root is owned by the project that declares it.
/// </summary>
/// <remarks>
///     <para>
///         The invariant is #452's: a namespace is a layer's vocabulary. Two
///         assemblies declaring the same root means a consumer writing
///         <c>using Harbor.Abstractions.Providers;</c> binds a contract and an
///         implementation at once and cannot tell, by reading, which side of
///         the pyramid the type it just named came from.
///     </para>
///     <para>
///         The text rule that states this invariant needs a THIRD rule to say
///         that every namespace declaration in the product tree is attributed
///         to at least one assembly — because a file walk cannot say which
///         assembly declares a namespace. It cannot: the shared source folders
///         are compiled into several assemblies at once, and the file walk
///         finds them under a directory that owns no project. This analyzer is
///         HANDED the answer: one compilation is one assembly, and
///         <c>compilation.AssemblyName</c> is the declaring project.
///     </para>
///     <para>
///         The ownership table below is DATA, not code: it is the one fact
///         about ownership that no compiler can supply, and the shape of the
///         exception is a decision, not a derivation.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NamespaceOwnershipAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id this rule reports under.</summary>
    public const string DiagnosticId = "HARCH002";

    /// <summary>The rule as the compiler sees it.</summary>
    /// <remarks>
    ///     Positional for the same reason as HARCH001's descriptor: (id, title,
    ///     messageFormat, category, defaultSeverity, isEnabledByDefault,
    ///     description, helpLinkUri, customTags). The ninth argument is
    ///     omitted for the same reason too: an empty array passed to a
    ///     <c>params</c> parameter is what S3878 forbids in a product tree,
    ///     and the tags stay empty because nothing was passed.
    /// </remarks>
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "A namespace root is owned by the project that declares it",
        "'{0}' is declared by '{1}', which does not own that namespace root",
        "Architecture",
        DiagnosticSeverity.Warning,
        true,
        "A namespace is the layer's vocabulary. Two assemblies declaring the same root means a consumer cannot tell, by reading, which side of the pyramid the type it named came from.",
        null);

    /// <summary>
    ///     The roots this rule owns, and the projects allowed to declare under
    ///     them.
    /// </summary>
    /// <remarks>
    ///     The table is DATA, not code: it is the one fact about ownership that
    ///     no compiler can supply, and the shape of the exception is a
    ///     decision, not a derivation.
    /// </remarks>
    internal static readonly (string Root, string[] Owners)[] OwnedRoots =
    [
        ("Harbor.Abstractions", ["Harbor.Abstractions", "Harbor.Abstractions.Contracts", "Harbor.Extensions"]),
        ("Harbor.Registries", ["Harbor.Registries"]),
    ];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(
            AnalyzeNamespace,
            SyntaxKind.NamespaceDeclaration,
            SyntaxKind.FileScopedNamespaceDeclaration);
    }

    /// <summary>
    ///     A namespace declaration under an owned root, in a project that does
    ///     not own it.
    /// </summary>
    private static void AnalyzeNamespace(SyntaxNodeAnalysisContext context)
    {
        // Only the OUTERMOST declaration of a file counts — a nested namespace
        // belongs to its parent's full name, and reporting both would count one
        // declaration twice.
        foreach (SyntaxNode ancestor in context.Node.Ancestors())
        {
            if (ancestor is NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax)
            {
                return;
            }
        }

        string? fullName = FullNameOf(context.Node);
        if (fullName is null)
        {
            return;
        }

        string assembly = context.Compilation.AssemblyName ?? string.Empty;

        foreach ((string root, string[] owners) in OwnedRoots)
        {
            if (!IsUnder(fullName, root))
            {
                continue;
            }

            if (Array.IndexOf(owners, assembly) >= 0)
            {
                return;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), fullName, assembly));
            return;
        }
    }

    /// <summary>
    ///     The fully qualified name of a namespace declaration, enclosing
    ///     namespaces included.
    /// </summary>
    private static string? FullNameOf(SyntaxNode node)
    {
        string own = node switch
        {
            NamespaceDeclarationSyntax declared => declared.Name.ToString(),
            FileScopedNamespaceDeclarationSyntax fileScoped => fileScoped.Name.ToString(),
            _ => string.Empty,
        };

        if (own.Length == 0)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (SyntaxNode ancestor in node.Ancestors())
        {
            if (ancestor is NamespaceDeclarationSyntax declared)
            {
                parts.Add(declared.Name.ToString());
            }
            else if (ancestor is FileScopedNamespaceDeclarationSyntax fileScoped)
            {
                parts.Add(fileScoped.Name.ToString());
            }
        }

        parts.Reverse();
        parts.Add(own);

        return string.Join(".", parts);
    }

    /// <summary>
    ///     Whether a namespace name sits under a root, compared SEGMENT BY
    ///     SEGMENT: <c>Harbor.AbstractionsExtra</c> is not under
    ///     <c>Harbor.Abstractions</c>.
    /// </summary>
    private static bool IsUnder(string fullName, string root)
    {
        string[] nameParts = fullName.Split('.');
        string[] rootParts = root.Split('.');

        if (nameParts.Length < rootParts.Length)
        {
            return false;
        }

        for (int i = 0; i < rootParts.Length; i++)
        {
            if (!string.Equals(nameParts[i], rootParts[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
