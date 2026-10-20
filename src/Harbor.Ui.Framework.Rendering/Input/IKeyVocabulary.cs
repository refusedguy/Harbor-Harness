namespace Harbor.Ui.Framework.Rendering.Input;

/// <summary>
///     Key-vocabulary contract: decode a <see cref="KeyEvent" /> into the
///     BCL-only <see cref="UiKeyDto" />. The default implementation is
///     <see cref="DefaultKeyVocabulary" /> (delegates to
///     <see cref="KeyEventMapper" />); hosts may substitute their own for
///     tests without touching the shared mapping table.
/// </summary>
public interface IKeyVocabulary
{
    /// <summary>
    ///     Translate a key event, or return false when it carries no key
    ///     meaning (releases, unmapped codes).
    /// </summary>
    bool TryMap(in KeyEvent evt, out UiKeyDto key);
}

/// <summary>Default <see cref="IKeyVocabulary" /> over the shared mapping table.</summary>
public sealed class DefaultKeyVocabulary : IKeyVocabulary
{
    /// <inheritdoc />
    public bool TryMap(in KeyEvent evt, out UiKeyDto key) =>
        KeyEventMapper.TryMap(evt, out key);
}
