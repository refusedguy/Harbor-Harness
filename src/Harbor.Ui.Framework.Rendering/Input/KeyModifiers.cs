namespace Harbor.Ui.Framework.Rendering.Input;

/// <summary>
/// Keyboard modifiers, bits 1–4 of the kitty modifier encoding
/// (shift=1, ctrl=2, alt=4, meta=8). Kitty super/hyper/meta all collapse
/// into <see cref="Meta"/>; caps-lock and num-lock are ignored.
/// </summary>
[Flags]
public enum KeyModifiers : byte
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Meta = 8,
}
