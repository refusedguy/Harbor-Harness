using System;

namespace Harbor.Abstractions.Contracts;

/// <summary>
///     Marks a seam — an interface, a class, or a method — as observed: the
///     instrumentation source generator (<c>Harbor.CodeGen.ObservedGenerator</c>)
///     emits a decorator that opens a span and records the call's metrics around
///     it.
/// </summary>
/// <remarks>
///     <para>
///         The attribute is a DECLARATION of intent, not a runtime hook. It carries no
///         behaviour, needs no reflection, and — because the generator emits plain C# —
///         keeps the whole mechanism AOT-safe.
///     </para>
///     <para>
///         Which statement governs a given seam member is decided by the policy order
///         (attribute, then type, then namespace, then assembly); exactly one statement
///         wins and nothing is merged across levels. See
///         <c>docs/OBSERVED_INSTRUMENTATION.md</c> for the full contract, the derived
///         span and instrument names, and the switch that turns the whole mechanism off
///         at compile time.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ObservedAttribute : Attribute
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ObservedAttribute" /> class with the
    ///     default scope (<see cref="ObservedScope.SpanAndMetrics" />) and derived names.
    /// </summary>
    public ObservedAttribute()
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ObservedAttribute" /> class with an
    ///     explicit span name.
    /// </summary>
    /// <param name="span">
    ///     The span name (dot-separated, e.g. <c>"store.append_message"</c>). Span names are a
    ///     contract: renaming one silently breaks every dashboard that reads it.
    /// </param>
    public ObservedAttribute(string span)
    {
        Span = span;
    }

    /// <summary>
    ///     Gets or sets the span name. When left unset, the generator derives it from the
    ///     seam and the operation (e.g. <c>"store.append_message"</c>).
    /// </summary>
    public string? Span { get; set; }

    /// <summary>
    ///     Gets or sets the counter name. When left unset, the generator derives it from
    ///     the seam (e.g. <c>"store.calls"</c>).
    /// </summary>
    public string? Counter { get; set; }

    /// <summary>
    ///     Gets or sets the duration histogram name. When left unset, the generator derives
    ///     it from the seam (e.g. <c>"store.duration.ms"</c>).
    /// </summary>
    public string? Duration { get; set; }

    /// <summary>
    ///     Gets or sets what the generated decorator emits. Defaults to
    ///     <see cref="ObservedScope.SpanAndMetrics" />.
    /// </summary>
    public ObservedScope Scope { get; set; } = ObservedScope.SpanAndMetrics;

    /// <summary>
    ///     Gets or sets a value indicating whether the call's arguments are captured as
    ///     span attributes. Defaults to <see langword="false" />: arguments are
    ///     potentially user data, so capturing them is an explicit, per-seam decision.
    /// </summary>
    public bool CaptureArguments { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the call's result is captured as a span
    ///     attribute. Defaults to <see langword="false" />, for the same reason
    ///     <see cref="CaptureArguments" /> defaults to <see langword="false" />.
    /// </summary>
    public bool CaptureResult { get; set; }

    /// <summary>
    ///     Gets or sets the members of the annotated type that are NOT instrumented. Only
    ///     meaningful when the attribute sits on a type, where it otherwise applies to
    ///     every member of that type.
    /// </summary>
    public string[]? Exclude { get; set; }
}

/// <summary>
///     What an observed seam emits. The values are additive in meaning but not
///     combinable: the generated decorator emits exactly what the winning statement
///     says.
/// </summary>
public enum ObservedScope
{
    /// <summary>
    ///     Nothing is emitted. The statement is an explicit opt-out and beats every
    ///     coarser policy level (type, namespace, assembly).
    /// </summary>
    None = 0,

    /// <summary>
    ///     A span is opened around the call.
    /// </summary>
    Span = 1,

    /// <summary>
    ///     A call counter and a duration histogram are recorded around the call.
    /// </summary>
    Metrics = 2,

    /// <summary>
    ///     Both a span and the metrics are emitted. The default.
    /// </summary>
    SpanAndMetrics = 3,
}

/// <summary>
///     States the coarse instrumentation policy for a namespace or an assembly: the
///     nearest marker type carrying this attribute, or the assembly-level attribute,
///     is the policy that governs every seam in that scope which no more specific
///     statement covers.
/// </summary>
/// <remarks>
///     <para>
///         C# has no namespace attributes, so a namespace policy is a marker TYPE in
///         the namespace (for example <c>internal static class TelemetryPolicy</c>
///         carrying <c>[ObservedPolicy(SpanPrefix = "store")]</c>). The assembly
///         policy is the same attribute applied to the assembly.
///     </para>
///     <para>
///         A policy is the coarse mode. It is meant for a namespace whose every seam
///         is worth measuring; anything finer belongs in an explicit
///         <see cref="ObservedAttribute" />.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class ObservedPolicyAttribute : Attribute
{
    /// <summary>
    ///     Gets or sets the prefix the generator puts in front of every derived name in
    ///     this scope (e.g. <c>"store"</c> yields <c>"store.calls"</c>). When left unset,
    ///     the seam's own name is used (e.g. <c>"session_store.calls"</c>).
    /// </summary>
    public string? SpanPrefix { get; set; }

    /// <summary>
    ///     Gets or sets what the policy emits. Defaults to
    ///     <see cref="ObservedScope.SpanAndMetrics" />.
    /// </summary>
    public ObservedScope Scope { get; set; } = ObservedScope.SpanAndMetrics;

    /// <summary>
    ///     Gets or sets a value indicating whether arguments are captured as span
    ///     attributes in this scope. Defaults to <see langword="false" />.
    /// </summary>
    public bool CaptureArguments { get; set; }
}
