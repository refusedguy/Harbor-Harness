using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #2680 F157 — <see cref="ShellIntegrationScript" /> generates the shell
///     snippet that exports HARBOR_* variables. These tests pin the exact
///     generated text, the shell quoting and the name validation that keeps a
///     generated line from escaping the export statement.
/// </summary>
public class ShellIntegrationScriptTests
{
    [Test]
    public async Task Bash_Script_Exports_Sorted_Variables()
    {
        string script = ShellIntegrationScript.Generate(
            ShellIntegrationShell.Bash,
            [
                new KeyValuePair<string, string>("HARBOR_MODEL", "kilocode/tencent/hy3:free"),
                new KeyValuePair<string, string>("HARBOR_HOME", "/home/nbook/.harbor")
            ]);

        await Assert.That(script).IsEqualTo(
            "# Harbor shell integration (bash)\n"
            + "export HARBOR_HOME='/home/nbook/.harbor';\n"
            + "export HARBOR_MODEL='kilocode/tencent/hy3:free';\n");
    }

    [Test]
    public async Task Zsh_Script_Uses_Zsh_Header()
    {
        string script = ShellIntegrationScript.Generate(
            ShellIntegrationShell.Zsh,
            [new KeyValuePair<string, string>("HARBOR_TUI", "cellforge")]);

        await Assert.That(script).IsEqualTo(
            "# Harbor shell integration (zsh)\n"
            + "export HARBOR_TUI='cellforge';\n");
    }

    [Test]
    public async Task Empty_Input_Produces_Header_Only()
    {
        string script = ShellIntegrationScript.Generate(ShellIntegrationShell.Bash, []);

        await Assert.That(script).IsEqualTo("# Harbor shell integration (bash)\n");
    }

    [Test]
    public async Task Duplicate_Names_Use_The_Last_Value()
    {
        string script = ShellIntegrationScript.Generate(
            ShellIntegrationShell.Bash,
            [
                new KeyValuePair<string, string>("HARBOR_MODE", "first"),
                new KeyValuePair<string, string>("HARBOR_MODE", "second")
            ]);

        await Assert.That(script).IsEqualTo(
            "# Harbor shell integration (bash)\n"
            + "export HARBOR_MODE='second';\n");
    }

    [Test]
    public async Task Quote_Preserves_Shell_Metacharacters()
    {
        await Assert.That(ShellIntegrationScript.Quote("plain")).IsEqualTo("'plain'");
        await Assert.That(ShellIntegrationScript.Quote("it's")).IsEqualTo("'it'\\''s'");
        await Assert.That(ShellIntegrationScript.Quote("a $HOME `id` \\ b")).IsEqualTo("'a $HOME `id` \\ b'");
        await Assert.That(ShellIntegrationScript.Quote("line1\nline2")).IsEqualTo("'line1\nline2'");
        await Assert.That(ShellIntegrationScript.Quote(string.Empty)).IsEqualTo("''");
    }

    [Test]
    public async Task Generated_Script_Escapes_Embedded_Quotes()
    {
        var variable = new KeyValuePair<string, string>("HARBOR_MODE", "it's fine");
        string script = ShellIntegrationScript.Generate(ShellIntegrationShell.Bash, [variable]);

        await Assert.That(script.Contains("export HARBOR_MODE='it'\\''s fine';")).IsTrue();
        await Assert.That(script.Contains("it's fine")).IsFalse();
    }

    [Test]
    public async Task Valid_Variable_Names_Are_Accepted()
    {
        string[] names =
        [
            "HARBOR_HOME",
            "HARBOR_A1_B",
            "HARBOR__UNDERSCORE"
        ];

        foreach (string name in names)
        {
            await Assert.That(ShellIntegrationScript.IsValidVariableName(name)).IsTrue();
        }
    }

    [Test]
    public async Task Invalid_Variable_Names_Are_Rejected()
    {
        string?[] names =
        [
            null,
            string.Empty,
            "FOO",
            "harbor_HOME",
            "HARBOR_",
            "HARBOR_1BAD",
            "HARBOR_A-B",
            "HARBOR_A B",
            "HARBOR_A=1; rm -rf /"
        ];

        foreach (string? name in names)
        {
            await Assert.That(ShellIntegrationScript.IsValidVariableName(name)).IsFalse();
        }
    }

    [Test]
    public async Task Generate_Refuses_An_Invalid_Variable_Name()
    {
        await Assert.That(() => ShellIntegrationScript.Generate(
                ShellIntegrationShell.Bash,
                [new KeyValuePair<string, string>("HARBOR_A=1; rm -rf /", "x")]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Generate_Refuses_Null_Variables()
    {
        await Assert.That(() => ShellIntegrationScript.Generate(ShellIntegrationShell.Bash, null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Quote_Refuses_Null()
    {
        await Assert.That(() => ShellIntegrationScript.Quote(null!))
            .Throws<ArgumentNullException>();
    }
}
