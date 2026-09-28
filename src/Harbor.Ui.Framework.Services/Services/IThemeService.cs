namespace Harbor.Ui.Framework.Services;

/// <summary>
///     Aggregate of the three theme roles — read (<see cref="IThemeReader" />),
///     apply (<see cref="IThemeApplier" />), and JSON live-reload
///     (<see cref="IThemeWatcher" />). Each desktop app implements this
///     to manipulate its own theme resource system.
/// </summary>
/// <remarks>
///     Kept as the DI convenience alias (one registration, one singleton) so
///     existing composition roots keep working. Depend on the narrow role
///     interfaces instead — a type that only reads <c>IsDark</c> does not need
///     the watcher role, and implementations that cannot honour a role simply
///     do not implement it (see #469, where <c>JsonThemeLoader</c> declared this
///     interface while every apply member threw).
/// </remarks>
public interface IThemeService : IThemeReader, IThemeApplier, IThemeWatcher
{
}
