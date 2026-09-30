// ICommonConfigModelRefReader.cs — the READ half of the shared-config contract pair.
//
// Its counterpart is Harbor.Desktop.Abstractions.Configuration.ICommonConfigStore:
// the whole-config repository you read AND write. This one reads a single value out
// of that repository and cannot write. They are two capabilities, not one contract
// declared twice — see the file header for why they cannot be merged, and
// docs/adr/DECISIONS.md ADR-009 for the decision.
//
// #453 renamed this from ICommonConfigReader. "Reader" said only what the member
// cannot do, so choosing between it and ICommonConfigStore still meant opening
// both to learn that this one reads a single ModelRef rather than the config. The
// two names now differ in what they hand out, which is the #717 resolution for
// two different things under one name.

using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Ui.Framework.Configuration;

/// <summary>
///     Read-only contract for the ONE thing session bootstrap needs out of the
///     per-user common config: the provider/model the user last selected. Every
///     other field (theme, log level, recent sessions) stays behind
///     <c>ICommonConfigStore</c>, and writing stays there too.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a second contract, and why these two are not one:</b> both
///         assemblies are Presentation, and
///         <c>Harbor.Desktop.Abstractions</c> declares a DIRECT
///         <c>ProjectReference</c> to <c>Harbor.Ui.Framework</c> — plus one to
///         each of <c>Ui.Framework.ViewModels</c>, <c>.State</c>, <c>.Services</c>,
///         <c>.Sessions</c> and <c>.Rendering</c>. So the reverse edge that would
///         let <c>SessionFactory</c> name <c>ICommonConfigStore</c> directly would
///         close a cycle, and the split is load-bearing rather than incidental.
///         <c>Harbor.Terminal.Abstractions</c> is one more edge on that same
///         direction, not the one that causes the cycle; an earlier version of this
///         comment named it as the cause, which was wrong in a way that mattered —
///         a stated reason nobody checks is worse than no reason.
///     </para>
///     <para>
///         The capabilities are genuinely different, and that is why merging is not
///         the answer even ignoring the cycle. <c>ICommonConfigStore</c> is
///         three members over the whole <c>CommonConfig</c> — Load, Save, Update —
///         and reports every failure as a <see cref="Result{T}" />. This is one
///         member, read-only, and "the user has not chosen yet" is a normal state
///         rather than an error to report, so it is a <see cref="Maybe{T}" /> with
///         no failure channel at all.
///     </para>
///     <para>
///         <b>Why the return is ONE reference:</b> it used to be
///         <c>Task&lt;(string? ProviderId, string? ModelId)?&gt;</c> — a pair of
///         optional strings inside another optional, which spells four states:
///         both, neither, provider-only, model-only. Nothing in the type could tell
///         the last two apart, so each consumer had to decide on its own whether a
///         half counts, and they had already decided differently three times. The
///         producing end was the half that survived: #598 fixed the consuming
///         signature and left this one handing the pair over, with a
///         hand-written half-pair test left behind in the adapter.
///         <see cref="ModelRef" /> is the repo's type for a provider/model
///         reference and both its halves are non-null by construction, so a half is
///         unrepresentable rather than merely discouraged — see issues #598 and
///         #453.
///     </para>
/// </remarks>
public interface ICommonConfigModelRefReader
{
    /// <summary>
    ///     The provider/model the shared config names, or <c>Maybe.None</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     A reference when the config names a usable one. <c>None</c> otherwise:
    ///     no config written yet (the normal state of every install before
    ///     onboarding), the config could not be read, or it names nothing this
    ///     method can qualify — a blank or invalid provider half, or a blank
    ///     model half. Those are one state, not three, and the type says so.
    /// </returns>
    public Task<Maybe<ModelRef>> ReadModelRefAsync(
        CancellationToken cancellationToken = default);
}
