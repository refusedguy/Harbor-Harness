using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Extensions;

/// <summary>
///     THE VIOLATION UNDER TEST (issue #2114).
/// </summary>
/// <remarks>
///     <para>
///         Harbor.Extensions is the pure BCL helper layer. Its row in the
///         layer matrix is an EMPTY reference set
///         (<c>FullLayerMatrixTests.Matrix["Harbor.Extensions"]</c>), and
///         <c>AbstractionsSplitLayerRules.Extensions_HasZeroHarborProjectReferences</c>
///         asserts that the assembly references no Harbor project at all.
///     </para>
///     <para>
///         This file is the other half of the planted violation: the
///         <c>ProjectReference</c> alone would be a DECLARED-but-unbound
///         edge, and the IL gate reads the assembly's REFERENCES, so a
///         reference nothing binds would not be seen. The type below binds
///         <c>Harbor.Abstractions.Contracts</c>, which makes the edge real.
///     </para>
/// </remarks>
internal static class ViolationProbe
{
    /// <summary>
    ///     The forbidden edge, expressed as a type the layer must not know.
    /// </summary>
    internal static ModelRef? ForbiddenReference => null;
}
