using System.Runtime.CompilerServices;
namespace Harbor.Desktop.Abstractions.ViewModels;
/// <summary>
///     Abstract base for every Harbor desktop view-model. Provides the
///     <see cref="Logger" />, observable property helpers, and a
///     <see cref="SetProperty{T}(ref T, T, string)" /> overload that logs
///     changes at Trace level for diagnostics.
/// </summary>
/// <remarks>
///     <para>
///         Platform VMs in <c>apps/Harbor.App.{Avalonia,Wpf,Maui,Blazor}</c>
///         derive from this base (or one of its more specific subclasses like
///         <see cref="ChatViewModelBase" />) and add platform-specific
///         bindings (Avalonia dispatcher, WPF <c>Dispatcher</c>, MAUI
///         <c>MainThread</c>, Blazor <c>Dispatcher</c>).
///     </para>
///     <para>
///         Stays abstract so NetArchTest can distinguish base VMs from
///         concrete platform VMs and so derived classes are forced to provide
///         their own <see cref="Logger" /> via the protected constructor.
///     </para>
/// </remarks>
public abstract class ViewModelBase : ObservableObject
{

    /// <summary>Construct a <see cref="ViewModelBase" /> with the given logger.</summary>
    /// <param name="logger">Logger; must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger" /> is null.</exception>
    protected ViewModelBase(ILogger logger)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    /// <summary>Logger for this VM. Set by the derived class via the constructor.</summary>
    protected ILogger Logger { get; }

    /// <summary>
    ///     Set a backing field and log the change at Trace level. Wraps
    ///     <see cref="ObservableProperty" />'s underlying
    ///     <see cref="ObservableObject.SetProperty{T}(ref T, T, string)" />
    ///     so callsites don't need to repeat the boilerplate logging.
    /// </summary>
    /// <typeparam name="T">Property type.</typeparam>
    /// <param name="field">Backing field ref.</param>
    /// <param name="newValue">New value.</param>
    /// <param name="propertyName">Property name; auto-filled by the compiler.</param>
    /// <returns>True if the value changed; false otherwise.</returns>
    protected bool SetPropertyLogged<T>(ref T field, T newValue, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, newValue)) return false;
        Logger.LogTrace("VM prop change: {Property} = {Value}", propertyName, newValue);
        return this.SetProperty(ref field, newValue, propertyName);
    }
}
