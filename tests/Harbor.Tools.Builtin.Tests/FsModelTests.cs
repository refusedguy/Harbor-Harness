using System.Text.Json;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;
/// <summary>
///     Tests for the shared Composite traversal (<see cref="FsModel" />) that
///     <see cref="TreeTool" /> and <see cref="LsTool" /> both walk (#1588): the
///     dirs-first ordering, the shared hidden rule, and the node model itself.
/// </summary>
public class FsModelTests
{
    [Test]
    public async Task Read_SortsDirsFirstThenFiles_IgnoringCase()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-fsmodel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "B"));
        Directory.CreateDirectory(Path.Combine(root, "a"));
        await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "x");

        try
        {
            var nodes = FsModel.Read(root, includeHidden: true, (_, _) => { });

            await Assert.That(nodes.Count).IsEqualTo(4);
            await Assert.That(nodes[0].Name).IsEqualTo("a");
            await Assert.That(nodes[1].Name).IsEqualTo("B");
            await Assert.That(nodes[2].Name).IsEqualTo("A.txt");
            await Assert.That(nodes[3].Name).IsEqualTo("b.txt");
            await Assert.That(nodes.Take(2).All(n => n.IsDirectory)).IsTrue();
            await Assert.That(nodes.Skip(2).All(n => n.IsDirectory)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Read_HidesDotEntriesByDefault_KeepsThemWhenIncluded()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-fsmodel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".hiddendir"));
        await File.WriteAllTextAsync(Path.Combine(root, ".hidden"), "h");
        await File.WriteAllTextAsync(Path.Combine(root, "visible.txt"), "v");

        try
        {
            var hidden = FsModel.Read(root, includeHidden: false, (_, _) => { });
            await Assert.That(hidden.Count).IsEqualTo(1);
            await Assert.That(hidden[0].Name).IsEqualTo("visible.txt");

            var all = FsModel.Read(root, includeHidden: true, (_, _) => { });
            await Assert.That(all.Count).IsEqualTo(3);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Read_MaterialisesPartWholeNodes()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-fsmodel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(root, "sub", "leaf.txt"), "leaf");
        await File.WriteAllTextAsync(Path.Combine(root, "out.txt"), "leaf");

        try
        {
            var nodes = FsModel.Read(root, includeHidden: true, (_, _) => { });

            var dir = nodes.OfType<FsDirectory>().Single(n => n.Name == "sub");
            await Assert.That(dir.FullName).IsEqualTo(Path.Combine(root, "sub"));
            await Assert.That(dir.Directory.FullName).IsEqualTo(dir.FullName);

            var leaf = nodes.OfType<FsFile>().Single(n => n.Name == "out.txt");
            await Assert.That(leaf.FullName).IsEqualTo(Path.Combine(root, "out.txt"));
            await Assert.That(leaf.File.Length).IsEqualTo(4);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Read_UsesOneReaderAtEveryLevel_AllLevelsShared()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-fsmodel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(root, "sub", "leaf.txt"), "leaf");

        try
        {
            var nodes = FsModel.Read(root, includeHidden: true, (_, _) => { });
            var dir = nodes.OfType<FsDirectory>().Single();
            var children = FsModel.Read(dir.FullName, includeHidden: true, (_, _) => { });

            await Assert.That(children.Count).IsEqualTo(1);
            var leaf = children.OfType<FsFile>().Single();
            await Assert.That(leaf.Name).IsEqualTo("leaf.txt");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Ls_KeepsDirsFirstRowContract()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-fsmodel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "B"));
        Directory.CreateDirectory(Path.Combine(root, "a"));
        await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(root, "A.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(root, ".hidden"), "h");

        try
        {
            var tool = new LsTool(NullLogger<LsTool>.Instance);
            var args = JsonDocument.Parse(
                $$"""{"path":"{{root.Replace("\\", "\\\\")}}"}""").RootElement;
            var result = await tool.ExecuteAsync(args, TestToolContext.Create());

            await Assert.That(result.IsError).IsFalse();
            var outD = result.Output;
            int iDirB = outD.IndexOf("[dir]  B/", StringComparison.Ordinal);
            int iDirA = outD.IndexOf("[dir]  a/", StringComparison.Ordinal);
            int iFileA = outD.IndexOf("[file]", StringComparison.Ordinal);
            await Assert.That(iDirB).IsGreaterThan(0);
            // Не менять направление: OrdinalIgnoreCase даёт 'a' раньше 'B' ('A' < 'B') —
            // контракт dirs-first ignore-case из FsModel.Read, а не ordinal-порядок.
            await Assert.That(iDirA).IsLessThan(iDirB);
            await Assert.That(iFileA).IsGreaterThan(iDirA);
            // The shared hidden rule: dot entries stay out unless `all` is set.
            await Assert.That(outD.Contains(".hidden")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
