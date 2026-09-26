namespace Harbor.Tui.CellForge.Input;

/// <summary>No-op mode controller for tests and piped stdin.</summary>
public sealed class NullModeController : ITerminalModeController
{
    public void Enter()
    {
    }

    public void Restore()
    {
    }

    public bool IsRaw => false;
}
