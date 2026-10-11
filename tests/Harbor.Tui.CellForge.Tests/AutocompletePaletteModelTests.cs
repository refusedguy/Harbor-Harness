using Harbor.Tui.CellForge.Widgets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Contract for <see cref="AutocompleteDestructiveGate" /> and
/// <see cref="AutocompleteFilter" /> (issue #1172, slice 2): destructive
/// two-step {id, confirm, run}, mode-aware ranking and the queueable flag.
/// </summary>
public class AutocompletePaletteModelTests
{
    private static AutocompleteItem Command(string id, string title, bool destructive = false, bool queueable = false) =>
        new(id, title, string.Empty, AutocompleteMode.Command, destructive, queueable);

    [Test]
    public async Task Destructive_PlainRow_Runs_Immediately()
    {
        var gate = new AutocompleteDestructiveGate();
        bool run = gate.Commit(Command("help", "/help"));
        await Assert.That(run).IsTrue();
        await Assert.That(gate.IsArmed).IsFalse();
    }

    [Test]
    public async Task Destructive_FirstCommit_Arms_Second_Runs()
    {
        var gate = new AutocompleteDestructiveGate();
        var row = Command("reset", "/reset", destructive: true);

        bool first = gate.Commit(row);
        await Assert.That(first).IsFalse();
        await Assert.That(gate.IsArmed).IsTrue();
        await Assert.That(gate.ArmedId).IsEqualTo("reset");

        bool second = gate.Commit(row);
        await Assert.That(second).IsTrue();
        await Assert.That(gate.IsArmed).IsFalse();
    }

    [Test]
    public async Task Destructive_SwitchingRow_Rearms_OnNewId()
    {
        var gate = new AutocompleteDestructiveGate();
        _ = gate.Commit(Command("a", "/a", destructive: true));

        bool other = gate.Commit(Command("b", "/b", destructive: true));
        await Assert.That(other).IsFalse();
        await Assert.That(gate.ArmedId).IsEqualTo("b");
    }

    [Test]
    public async Task Destructive_Cancel_Disarms()
    {
        var gate = new AutocompleteDestructiveGate();
        _ = gate.Commit(Command("reset", "/reset", destructive: true));
        gate.Cancel();
        await Assert.That(gate.IsArmed).IsFalse();
        await Assert.That(gate.ArmedId).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Filter_CommandMode_Hides_OtherModes()
    {
        var rows = new List<AutocompleteItem>
        {
            Command("help", "/help"),
            new("f", "README.md", string.Empty, AutocompleteMode.Reference),
            new("d", "src/app", string.Empty, AutocompleteMode.Directory),
        };

        List<AutocompleteItem> result = AutocompleteFilter.Filter(string.Empty, rows, AutocompleteMode.Command);
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Id).IsEqualTo("help");
    }

    [Test]
    public async Task Filter_Ranks_ByTitle_Fuzzy()
    {
        var rows = new List<AutocompleteItem>
        {
            Command("quit", "Quit"),
            Command("branch", "Branch Session"),
        };

        List<AutocompleteItem> result = AutocompleteFilter.Filter("br", rows, AutocompleteMode.Command);
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Id).IsEqualTo("branch");
    }

    [Test]
    public async Task Queueable_Flag_GoesToQueue_WhileBusy()
    {
        await Assert.That(AutocompleteFilter.CanQueueWhileBusy(Command("ask", "/ask", queueable: true))).IsTrue();
        await Assert.That(AutocompleteFilter.CanQueueWhileBusy(Command("reset", "/reset", destructive: true))).IsFalse();
    }
}
