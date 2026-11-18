// Design-system consumer smoke: proves the standalone package story.
// References only Harbor.DesignSystem (see .csproj) and verifies at runtime
// that the package assembly carries zero Harbor.* assembly dependencies.
using Harbor.DesignSystem;

var asm = typeof(HarborTheme).Assembly;
var harborRefs = asm.GetReferencedAssemblies()
    .Where(a => a.Name is not null && a.Name.StartsWith("Harbor", StringComparison.Ordinal))
    .Select(a => a.Name!)
    .OrderBy(n => n)
    .ToList();

HarborTheme theme = TerminalColorPalette.Current;
Console.WriteLine($"consumer of {asm.GetName().Name} v{asm.GetName().Version}");
Console.WriteLine($"active theme : {theme.Name}");
Console.WriteLine($"  accent     #{theme.Accent.R:X2}{theme.Accent.G:X2}{theme.Accent.B:X2}");
Console.WriteLine($"  background #{theme.Background.R:X2}{theme.Background.G:X2}{theme.Background.B:X2}");
Console.WriteLine($"  text       #{theme.Text.R:X2}{theme.Text.G:X2}{theme.Text.B:X2}");
Console.WriteLine($"spacing scale: {DesignTokens.Space4}/{DesignTokens.Space8}/{DesignTokens.Space16}/{DesignTokens.Space32} px");

if (harborRefs.Count > 0)
{
    Console.Error.WriteLine($"FAIL: Harbor.DesignSystem references [{string.Join(", ", harborRefs)}]");
    return 1;
}

Console.WriteLine("standalone OK: zero Harbor.* assembly references");
return 0;
