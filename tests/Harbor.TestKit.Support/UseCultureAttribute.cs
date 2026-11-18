using TUnit.Core.Executors;

namespace Harbor.TestKit;

/// <summary>
///     Pins <see cref="System.Globalization.CultureInfo.CurrentCulture" /> and
///     <see cref="System.Globalization.CultureInfo.CurrentUICulture" /> for one
///     test, class or assembly, then puts the ambient values back (#2704 T05).
/// </summary>
/// <remarks>
///     <para>
///         The wrapper is deliberately thin. TUnit 1.75's
///         <see cref="CultureAttribute" /> owns a test executor and a hook
///         executor that set the culture around the test body, so restoration
///         is not a <c>finally</c> the test author has to remember: it happens
///         on the failure path too. Deriving from it keeps that contract in one
///         place; Harbor only adds a name that lives with the other shared
///         fixtures and needs no TUnit executor namespace at the call site.
///         The base <c>AttributeUsage</c> is inherited as TUnit's analyzer
///         requires; redeclaring it here is a build error (TUnit0028).
///     </para>
///     <para>
///         Culture is process/async-flow state. A test that swaps it in its own
///         body can leak the swap into a neighbour that runs later in the same
///         project; an attribute-backed executor cannot, which is the point of
///         moving the handful of culture-sensitive tests onto it.
///     </para>
/// </remarks>
public sealed class UseCultureAttribute(string cultureName) : CultureAttribute(cultureName);
