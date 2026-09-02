using Harbor.Ui.Framework.Projection;

namespace Harbor.DesignSystem.Tests;

/// <summary>
/// Theme marketplace store: built-in + user listing, defensive handling of
/// broken files (errors captured, no crash), built-in seeding, and
/// user-overrides-builtin name resolution.
/// </summary>
public class ThemeStoreTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"harbor-themes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task Scan_ListsBuiltInsFirst()
    {
        var store = new ThemeStore(TempDir());

        var entries = store.Scan();

        await Assert.That(entries).Count().IsEqualTo(HarborTheme.BuiltIn.Count);
        for (int i = 0; i < HarborTheme.BuiltIn.Count; i++)
        {
            await Assert.That(entries[i].Source).IsEqualTo(ThemeSource.Builtin);
            await Assert.That(entries[i].IsValid).IsTrue();
            await Assert.That(entries[i].Name).IsEqualTo(HarborTheme.BuiltIn[i].Name);
        }
    }

    [Test]
    public async Task Scan_MissingDirectory_DoesNotCrash()
    {
        var store = new ThemeStore(Path.Combine(TempDir(), "does-not-exist"));

        var entries = store.Scan();

        await Assert.That(entries).Count().IsEqualTo(HarborTheme.BuiltIn.Count);
    }

    [Test]
    public async Task Scan_InvalidThemeFile_CapturedAsErrorEntry()
    {
        string dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "good.json"), """{ "name": "good", "accent": "#abcdef" }""");
            await File.WriteAllTextAsync(Path.Combine(dir, "broken.json"), "{ not json ");
            await File.WriteAllTextAsync(Path.Combine(dir, "badslot.json"), """{ "accent": "#nope" }""");

            var entries = new ThemeStore(dir).Scan();

            var user = entries.Where(e => e.Source == ThemeSource.User).ToList();
            await Assert.That(user).Count().IsEqualTo(3);

            var good = user.Single(e => e.FileName == "good.json");
            await Assert.That(good.IsValid).IsTrue();
            await Assert.That(good.Theme!.Name).IsEqualTo("good");

            var broken = user.Single(e => e.FileName == "broken.json");
            await Assert.That(broken.IsValid).IsFalse();
            await Assert.That(broken.Errors).IsNotEmpty();

            var badslot = user.Single(e => e.FileName == "badslot.json");
            await Assert.That(badslot.IsValid).IsFalse();
            await Assert.That(badslot.Errors.Single()).Contains("accent");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task LoadUserThemes_ReturnsOnlyValidEntries()
    {
        string dir = TempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "good.json"), """{ "name": "good" }""");
            await File.WriteAllTextAsync(Path.Combine(dir, "broken.json"), "{ nope ");

            var themes = new ThemeStore(dir).LoadUserThemes();

            await Assert.That(themes).Count().IsEqualTo(1);
            await Assert.That(themes[0].Name).IsEqualTo("good");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task SeedBuiltIns_WritesThenSkipsExisting()
    {
        string dir = TempDir();
        try
        {
            var store = new ThemeStore(dir);

            var first = store.SeedBuiltIns();
            var second = store.SeedBuiltIns();

            await Assert.That(first).Count().IsEqualTo(HarborTheme.BuiltIn.Count);
            await Assert.That(second).IsEmpty();

            foreach (var theme in HarborTheme.BuiltIn)
            {
                string json = await File.ReadAllTextAsync(Path.Combine(dir, theme.Name + ".json"));
                var parsed = ThemeJson.Parse(json, HarborTheme.HarborDark);
                await Assert.That(parsed.IsSuccess).IsTrue();
                await Assert.That(parsed.Theme).IsEqualTo(theme);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Resolve_UserThemeOverridesBuiltinByName()
    {
        string dir = TempDir();
        try
        {
            var store = new ThemeStore(dir);
            store.SeedBuiltIns();
            // user edits the shipped harbor-dark.json
            await File.WriteAllTextAsync(
                Path.Combine(dir, "harbor-dark.json"),
                """{ "name": "harbor-dark", "accent": "#00ff00" }""");

            var resolved = store.Resolve("harbor-dark");
            var untouched = store.Resolve("harbor-warm");

            await Assert.That(resolved!.Accent).IsEqualTo(new RgbColor(0x00, 0xFF, 0x00));
            await Assert.That(untouched!.Name).IsEqualTo("harbor-warm");
            await Assert.That(store.Resolve("no-such-theme")).IsNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
