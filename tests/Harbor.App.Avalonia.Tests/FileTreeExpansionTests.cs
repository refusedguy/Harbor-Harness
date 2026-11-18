// FileTreeExpansionTests.cs — issue #2682 Z15.
//
// The expand/collapse-all buttons in the shell's file tree only mutate
// FileTreeNode.IsExpanded; the two-way style binding on TreeViewItem does the
// painting. These tests assert the mutation itself: which rows change, which
// rows are left alone, and that the flag raises PropertyChanged so a change
// made after the rows were realized can reach the TreeView.

using System.ComponentModel;
using Harbor.App.Avalonia.Services;
using TUnit.Assertions;

namespace Harbor.App.Avalonia.Tests;

public class FileTreeExpansionTests
{
    private static FileTreeNode Dir(string name, bool expanded = false) => new()
    {
        Name = name,
        IsDirectory = true,
        IsExpanded = expanded,
    };

    private static FileTreeNode File(string name) => new()
    {
        Name = name,
        IsDirectory = false,
    };

    private static FileTreeNode ProjectTree()
    {
        FileTreeNode root = Dir("root");
        FileTreeNode src = Dir("src");
        FileTreeNode tests = Dir("tests");
        src.Children.Add(Dir("nested"));
        src.Children.Add(File("Program.cs"));
        tests.Children.Add(File("ProgramTests.cs"));
        root.Children.Add(src);
        root.Children.Add(tests);
        return root;
    }

    [Test]
    public async Task ExpandAll_Opens_Every_Directory_Including_The_Root()
    {
        FileTreeNode root = ProjectTree();

        FileTreeExpansion.ExpandAll([root]);

        await Assert.That(root.IsExpanded).IsTrue();
        await Assert.That(root.Children[0].IsExpanded).IsTrue();
        await Assert.That(root.Children[0].Children[0].IsExpanded).IsTrue();
        await Assert.That(root.Children[1].IsExpanded).IsTrue();
    }

    [Test]
    public async Task ExpandAll_Does_Not_Turn_A_File_Row_Into_A_Folder()
    {
        FileTreeNode root = Dir("root");
        FileTreeNode file = File("readme.md");
        root.Children.Add(file);

        FileTreeExpansion.ExpandAll([root]);

        await Assert.That(file.IsExpanded).IsFalse();
        await Assert.That(file.IsDirectory).IsFalse();
    }

    [Test]
    public async Task CollapseAll_Keeps_The_Root_Open_So_Children_Stay_Reachable()
    {
        FileTreeNode root = ProjectTree();
        FileTreeExpansion.ExpandAll([root]);

        FileTreeExpansion.CollapseAll([root]);

        await Assert.That(root.IsExpanded).IsTrue();
        await Assert.That(root.Children[0].IsExpanded).IsFalse();
        await Assert.That(root.Children[0].Children[0].IsExpanded).IsFalse();
        await Assert.That(root.Children[1].IsExpanded).IsFalse();
    }

    [Test]
    public async Task Toggle_Flips_A_Directory_And_Reports_The_New_State()
    {
        FileTreeNode folder = Dir("folder", expanded: false);

        bool first = FileTreeExpansion.Toggle(folder);
        bool second = FileTreeExpansion.Toggle(folder);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(folder.IsExpanded).IsFalse();
    }

    [Test]
    public async Task Toggle_Leaves_A_File_Alone()
    {
        FileTreeNode file = File("notes.txt");
        file.IsExpanded = true;

        bool result = FileTreeExpansion.Toggle(file);

        await Assert.That(result).IsTrue();
        await Assert.That(file.IsExpanded).IsTrue();
    }

    [Test]
    public async Task IsExpanded_Raises_PropertyChanged_For_The_TreeView_Binding()
    {
        FileTreeNode node = Dir("folder");
        var changed = new List<string>();
        ((INotifyPropertyChanged)node).PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        node.IsExpanded = true;
        node.IsExpanded = true;

        await Assert.That(changed.Count()).IsEqualTo(1);
        await Assert.That(changed[0]).IsEqualTo(nameof(FileTreeNode.IsExpanded));
    }
}
