using System.Security.Cryptography;
using System.Text;
using Harbor.Application.Skills;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     Tests for <see cref="SkillFreshnessSeeder" /> (KILLER_FEATURES §2.7
///     Feature 10, issue #23 slice 2): installed <c>SKILL.md</c> hashes vs
///     the <c>skills-lock.json</c> snapshot. Each test pins the hash relation
///     that drives the slice-1 pill — equal (current), differing (changed),
///     null lock (untracked), null install (missing) — plus the empty-lock
///     and shadowing contracts.
/// </summary>
public class SkillFreshnessSeederTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("harbor-seed").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Test]
    public async Task Seed_Current_WhenInstalledMatchesLock()
    {
        string body = "---\nname: review\ndescription: Reviews pull requests\n---\n# Review\n";
        string skills = WriteSkillDir("review", body);
        string lockPath = Path.Combine(_dir, "skills-lock.json");
        WriteLock(lockPath, new Dictionary<string, string?> { ["review"] = Sha256Hex(body) });

        var entries = SkillFreshnessSeeder.Seed(skills, null, lockPath);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Name).IsEqualTo("review");
        await Assert.That(entries[0].InstalledHash).IsEqualTo(entries[0].LockedHash);
    }

    [Test]
    public async Task Seed_Changed_WhenInstalledDiffersFromLock()
    {
        string skills = WriteSkillDir("review", "# Review v2\n");
        string lockPath = Path.Combine(_dir, "skills-lock.json");
        WriteLock(lockPath, new Dictionary<string, string?> { ["review"] = new string('a', 64) });

        var entries = SkillFreshnessSeeder.Seed(skills, null, lockPath);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].InstalledHash).IsNotNull();
        await Assert.That(entries[0].LockedHash).IsNotNull();
        await Assert.That(entries[0].InstalledHash).IsNotEqualTo(entries[0].LockedHash);
    }

    [Test]
    public async Task Seed_Untracked_WhenInstalledWithoutLockEntry()
    {
        string skills = WriteSkillDir("review", "# Review\n");

        var entries = SkillFreshnessSeeder.Seed(skills, null, lockFilePath: null);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].InstalledHash).IsNotNull();
        await Assert.That(entries[0].LockedHash).IsNull();
    }

    [Test]
    public async Task Seed_Missing_WhenLockedWithoutInstall()
    {
        string skills = Path.Combine(_dir, "skills");
        Directory.CreateDirectory(skills);
        string lockPath = Path.Combine(_dir, "skills-lock.json");
        WriteLock(lockPath, new Dictionary<string, string?> { ["ghost"] = new string('b', 64) });

        var entries = SkillFreshnessSeeder.Seed(skills, null, lockPath);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Name).IsEqualTo("ghost");
        await Assert.That(entries[0].InstalledHash).IsNull();
        await Assert.That(entries[0].LockedHash).IsEqualTo(new string('b', 64));
    }

    [Test]
    public async Task Seed_EmptyLock_AllInstalledAreUntracked()
    {
        string skills = WriteSkillDir("review", "# Review\n");
        string lockPath = Path.Combine(_dir, "skills-lock.json");
        File.WriteAllText(lockPath, "{\"version\":1,\"skills\":{}}");

        var entries = SkillFreshnessSeeder.Seed(skills, null, lockPath);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].InstalledHash).IsNotNull();
        await Assert.That(entries[0].LockedHash).IsNull();
    }

    [Test]
    public async Task Seed_MissingLockFile_AllInstalledAreUntracked()
    {
        string skills = WriteSkillDir("review", "# Review\n");
        string lockPath = Path.Combine(_dir, "skills-lock.json");

        var entries = SkillFreshnessSeeder.Seed(skills, null, lockPath);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].LockedHash).IsNull();
    }

    [Test]
    public async Task Seed_MalformedLock_ToleratedAsEmpty()
    {
        string skills = WriteSkillDir("review", "# Review\n");
        string lockPath = Path.Combine(_dir, "skills-lock.json");
        File.WriteAllText(lockPath, "{oops, not json");

        var entries = SkillFreshnessSeeder.Seed(skills, null, lockPath);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].LockedHash).IsNull();
    }

    [Test]
    public async Task Seed_ProjectShadowsGlobal_OnNameCollision()
    {
        string project = Path.Combine(_dir, "project", ".harbor", "skills");
        string global = Path.Combine(_dir, "global", ".harbor", "skills");
        WriteSkillFile(project, "deploy", "# Project deploy\n");
        WriteSkillFile(global, "deploy", "# Global deploy\n");

        var entries = SkillFreshnessSeeder.Seed(project, global, lockFilePath: null);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].InstalledHash).IsEqualTo(Sha256Hex("# Project deploy\n"));
    }

    [Test]
    public async Task Seed_FlatMarkdownFile_IsHashed()
    {
        string skills = Path.Combine(_dir, "skills");
        Directory.CreateDirectory(skills);
        File.WriteAllText(Path.Combine(skills, "deploy.md"), "# Deploy\n");

        var entries = SkillFreshnessSeeder.Seed(skills, null, lockFilePath: null);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Name).IsEqualTo("deploy");
        await Assert.That(entries[0].InstalledHash).IsEqualTo(Sha256Hex("# Deploy\n"));
    }

    [Test]
    public async Task Seed_Entries_SortedOrdinal()
    {
        string skills = Path.Combine(_dir, "skills");
        Directory.CreateDirectory(skills);
        WriteSkillFile(skills, "zeta", "# Z\n");
        WriteSkillFile(skills, "alpha", "# A\n");
        string lockPath = Path.Combine(_dir, "skills-lock.json");
        WriteLock(lockPath, new Dictionary<string, string?> { ["mid"] = new string('c', 64) });

        var entries = SkillFreshnessSeeder.Seed(skills, null, lockPath);

        await Assert.That(entries.Count).IsEqualTo(3);
        await Assert.That(entries[0].Name).IsEqualTo("alpha");
        await Assert.That(entries[1].Name).IsEqualTo("mid");
        await Assert.That(entries[2].Name).IsEqualTo("zeta");
    }

    private string WriteSkillDir(string name, string body)
    {
        string root = Path.Combine(_dir, "skills");
        WriteSkillFile(root, name, body);
        return root;
    }

    private static void WriteSkillFile(string root, string name, string body)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), body);
    }

    private static void WriteLock(string path, Dictionary<string, string?> hashes)
    {
        var sb = new StringBuilder("{\"version\":1,\"skills\":{");
        bool first = true;
        foreach (var kv in hashes)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append('\"').Append(kv.Key).Append("\":{\"computedHash\":");
            if (kv.Value is null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('\"').Append(kv.Value).Append('\"');
            }

            sb.Append('}');
        }

        sb.Append("}}");
        File.WriteAllText(path, sb.ToString());
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
