using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;

// The attribute is applied BEFORE the namespace declaration, so it is named
// in full: a type inside a namespace is not in scope at the top level.
[assembly: Harbor.TestKit.GenerateFakes(
    typeof(ITool),
    typeof(IToolRegistry),
    typeof(ISessionStore),
    typeof(ISessionContext),
    typeof(ILlmClient),
    typeof(IProviderRegistry),
    typeof(IAgent),
    typeof(IAgentLoop),
    typeof(IAgentRegistry),
    typeof(IEventBus),
    typeof(IEventBusMiddleware),
    typeof(IPermissionService))]

namespace Harbor.TestKit;

/// <summary>
///     Opt-in list for the test-double generator (issue #1769).
/// </summary>
/// <remarks>
///     <para>
///         The generator is <c>tests/Harbor.TestKit.Generators</c>, a Roslyn
///         component consumed by this project as an analyzer. It reads this
///         attribute by METADATA NAME (the same convention
///         <c>src/Harbor.CodeGen/ToolGlueGenerator.cs</c> uses for
///         <c>ToolInfoAttribute</c>), so the generator needs no reference to
///         this project and this project needs no Roslyn package.
///     </para>
///     <para>
///         Listing an interface here makes the generator emit
///         <c>Harbor.TestKit.Generated.Fake{Interface}</c> — a concrete class,
///         never a proxy. A shape the generator cannot express is a BUILD
///         ERROR (<c>HARBORFAKE00x</c>), never a half-emitted double.
///     </para>
///     <para>
///         Add an interface only when a test project actually needs a double for
///         it: every entry is a public type in this assembly.
///     </para>
///     <para>
///         Every interface listed here lives in <c>Harbor.Abstractions</c> or
///         <c>Harbor.Abstractions.Contracts</c> — the two assemblies every test
///         project already has. An interface that lives deeper (say
///         <c>IConfigStore</c> in <c>Harbor.Application</c>) does NOT belong in
///         this list: a double for it would drag that assembly's whole closure
///         into every consumer of this one (#1809). Such a double belongs to
///         the test project that already has the closure.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class GenerateFakesAttribute : Attribute
{
    /// <summary>Creates the marker over the interfaces to generate doubles for.</summary>
    /// <param name="interfaces">The interfaces the generator emits doubles for.</param>
    public GenerateFakesAttribute(params Type[] interfaces) => Interfaces = interfaces;

    /// <summary>The interfaces the generator emits doubles for.</summary>
    public Type[] Interfaces { get; }
}
