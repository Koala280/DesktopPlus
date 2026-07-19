using System.IO;
using DesktopPlus.Companion;
using Xunit;

namespace DesktopPlus.Tests;

public class CompanionFileOpsTests
{
    // ---- create folder ----

    [Fact]
    public void CreateFolder_Valid_Creates()
    {
        using var t = new TempDir();
        var r = CompanionFileOps.CreateFolder(t.Path, "New");

        Assert.True(r.Ok, r.Error);
        Assert.True(Directory.Exists(t.Combine("New")));
    }

    [Theory]
    [InlineData("a/b")]     // contains separator
    [InlineData("a\\b")]    // contains separator
    [InlineData("..")]      // traversal
    [InlineData(".")]
    [InlineData("a:b")]     // invalid filename char
    [InlineData("")]
    public void CreateFolder_InvalidName_Fails(string name)
    {
        using var t = new TempDir();
        Assert.False(CompanionFileOps.CreateFolder(t.Path, name).Ok);
    }

    [Fact]
    public void CreateFolder_RelativeDir_Fails()
    {
        // Non-fully-qualified paths are rejected outright.
        Assert.False(CompanionFileOps.CreateFolder("relative\\dir", "x").Ok);
    }

    [Fact]
    public void CreateFolder_Duplicate_Fails()
    {
        using var t = new TempDir();
        Assert.True(CompanionFileOps.CreateFolder(t.Path, "Dup").Ok);
        Assert.False(CompanionFileOps.CreateFolder(t.Path, "Dup").Ok);
    }

    // ---- rename ----

    [Fact]
    public void Rename_File_Works()
    {
        using var t = new TempDir();
        string f = t.File("old.txt");

        var r = CompanionFileOps.Rename(f, "new.txt");

        Assert.True(r.Ok, r.Error);
        Assert.False(File.Exists(f));
        Assert.True(File.Exists(t.Combine("new.txt")));
    }

    [Fact]
    public void Rename_InvalidName_Fails()
    {
        using var t = new TempDir();
        string f = t.File("old.txt");
        Assert.False(CompanionFileOps.Rename(f, "sub\\new.txt").Ok);
    }

    [Fact]
    public void Rename_OntoExisting_Fails()
    {
        using var t = new TempDir();
        string f = t.File("a.txt");
        t.File("b.txt");
        Assert.False(CompanionFileOps.Rename(f, "b.txt").Ok);
    }

    [Fact]
    public void Rename_Nonexistent_Fails()
    {
        using var t = new TempDir();
        Assert.False(CompanionFileOps.Rename(t.Combine("ghost.txt"), "x.txt").Ok);
    }

    // ---- delete ----

    [Fact]
    public void Delete_Permanent_RemovesFile()
    {
        using var t = new TempDir();
        string f = t.File("gone.txt");

        var r = CompanionFileOps.Delete(new[] { f }, permanent: true);

        Assert.True(r.Ok, r.Error);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void Delete_Permanent_RemovesDirectoryRecursively()
    {
        using var t = new TempDir();
        string d = t.Dir("tree");
        File.WriteAllText(Path.Combine(d, "inner.txt"), "x");

        var r = CompanionFileOps.Delete(new[] { d }, permanent: true);

        Assert.True(r.Ok, r.Error);
        Assert.False(Directory.Exists(d));
    }

    [Fact]
    public void Delete_Recycle_RemovesFromSource()
    {
        // NOTE: this sends a file to the Windows recycle bin (its purpose). It leaves one
        // throwaway entry there per run; that's expected for verifying the safe-delete default.
        using var t = new TempDir();
        string f = t.File("recycle_me.txt");

        var r = CompanionFileOps.Delete(new[] { f }, permanent: false);

        Assert.True(r.Ok, r.Error);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void Delete_EmptyList_Fails()
    {
        Assert.False(CompanionFileOps.Delete(System.Array.Empty<string>(), permanent: true).Ok);
    }

    // ---- copy / move ----

    [Fact]
    public void Copy_File_KeepsOriginal()
    {
        using var t = new TempDir();
        string f = t.File("src.txt", "data");
        string dest = t.Dir("Dest");

        var r = CompanionFileOps.Transfer(new[] { f }, dest, move: false);

        Assert.True(r.Ok, r.Error);
        Assert.True(File.Exists(f));
        Assert.True(File.Exists(Path.Combine(dest, "src.txt")));
    }

    [Fact]
    public void Move_File_RemovesOriginal()
    {
        using var t = new TempDir();
        string f = t.File("src.txt", "data");
        string dest = t.Dir("Dest");

        var r = CompanionFileOps.Transfer(new[] { f }, dest, move: true);

        Assert.True(r.Ok, r.Error);
        Assert.False(File.Exists(f));
        Assert.True(File.Exists(Path.Combine(dest, "src.txt")));
    }

    [Fact]
    public void Copy_NameCollision_CreatesUniqueName()
    {
        using var t = new TempDir();
        string dest = t.Dir("Dest");
        File.WriteAllText(Path.Combine(dest, "src.txt"), "existing");
        string f = t.File("src.txt", "new");

        var r = CompanionFileOps.Transfer(new[] { f }, dest, move: false);

        Assert.True(r.Ok, r.Error);
        Assert.True(File.Exists(Path.Combine(dest, "src (2).txt")));
    }

    [Fact]
    public void Copy_Directory_Recursively_KeepsOriginal()
    {
        using var t = new TempDir();
        string a = t.Dir("A");
        File.WriteAllText(Path.Combine(a, "inner.txt"), "x");
        Directory.CreateDirectory(Path.Combine(a, "Nested"));
        File.WriteAllText(Path.Combine(a, "Nested", "deep.txt"), "y");
        string dest = t.Dir("Dest");

        var r = CompanionFileOps.Transfer(new[] { a }, dest, move: false);

        Assert.True(r.Ok, r.Error);
        Assert.True(File.Exists(Path.Combine(dest, "A", "inner.txt")));
        Assert.True(File.Exists(Path.Combine(dest, "A", "Nested", "deep.txt")));
        Assert.True(Directory.Exists(a));   // original preserved
    }

    [Fact]
    public void Move_FolderIntoItself_Fails()
    {
        using var t = new TempDir();
        string a = t.Dir("A");
        Assert.False(CompanionFileOps.Transfer(new[] { a }, a, move: true).Ok);
    }

    [Fact]
    public void Move_FolderIntoOwnDescendant_Fails()
    {
        using var t = new TempDir();
        string a = t.Dir("A");
        string sub = Directory.CreateDirectory(Path.Combine(a, "Sub")).FullName;
        Assert.False(CompanionFileOps.Transfer(new[] { a }, sub, move: true).Ok);
    }

    [Fact]
    public void Transfer_RelativeDest_Fails()
    {
        using var t = new TempDir();
        string f = t.File("a.txt");
        Assert.False(CompanionFileOps.Transfer(new[] { f }, "relative\\dest", move: false).Ok);
    }

    [Fact]
    public void Transfer_EmptyList_Fails()
    {
        using var t = new TempDir();
        Assert.False(CompanionFileOps.Transfer(System.Array.Empty<string>(), t.Path, move: false).Ok);
    }

    // ---- open on PC (validation only; never actually launches anything) ----

    [Fact]
    public void OpenOnPc_Nonexistent_Fails()
    {
        using var t = new TempDir();
        Assert.False(CompanionFileOps.OpenOnPc(t.Combine("nope.txt")).Ok);
    }

    [Fact]
    public void OpenOnPc_RelativePath_Fails()
    {
        Assert.False(CompanionFileOps.OpenOnPc("relative.txt").Ok);
    }
}
