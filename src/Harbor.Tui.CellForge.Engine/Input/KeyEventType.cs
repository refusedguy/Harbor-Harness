// #436: engine-owned port of src/Harbor.Ui.Framework.Rendering/Input/KeyEventType.cs — verbatim except its namespace.
// The engine is a standalone leaf (zero Harbor references); this vocabulary lives here now.

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// Key lifecycle phase. Press/repeat/release distinction is only available
/// when the kitty protocol reports event types (flag 2); legacy terminals
/// always produce <see cref="KeyEventType.Press"/>.
/// </summary>
public enum KeyEventType : byte
{
    Press = 0,
    Repeat = 1,
    Release = 2,
}
