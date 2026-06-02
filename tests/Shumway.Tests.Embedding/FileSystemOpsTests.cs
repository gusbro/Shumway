using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 24 chunk 271 — Arity-Prolog file-system operations
/// (mkdir, rmdir, delete, rename, directory, exists_file,
/// exists_directory, chdir).
/// </summary>
public class FileSystemOpsTests : IDisposable
{
    private readonly string _tmp;

    public FileSystemOpsTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(),
            "shumway_fs_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string P(string rel) => Path.Combine(_tmp, rel).Replace('\\', '/');

    [Fact]
    public void Mkdir_CreatesDirectory()
    {
        var e = new PrologEngine();
        var p = P("newdir");
        Assert.True(e.Query($"mkdir('{p}').").Success);
        Assert.True(Directory.Exists(Path.Combine(_tmp, "newdir")));
    }

    [Fact]
    public void Mkdir_OnExisting_Succeeds()
    {
        var e = new PrologEngine();
        var p = P("a");
        e.Query($"mkdir('{p}').");
        Assert.True(e.Query($"mkdir('{p}').").Success);
    }

    [Fact]
    public void Rmdir_RemovesEmptyDirectory()
    {
        var e = new PrologEngine();
        var p = P("toremove");
        e.Query($"mkdir('{p}').");
        Assert.True(e.Query($"rmdir('{p}').").Success);
        Assert.False(Directory.Exists(Path.Combine(_tmp, "toremove")));
    }

    [Fact]
    public void Rmdir_NonEmpty_Fails()
    {
        var e = new PrologEngine();
        var p = P("notempty");
        Directory.CreateDirectory(Path.Combine(_tmp, "notempty"));
        File.WriteAllText(Path.Combine(_tmp, "notempty", "f.txt"), "x");
        Assert.False(e.Query($"rmdir('{p}').").Success);
    }

    [Fact]
    public void Rmdir_Missing_ExistenceError()
    {
        var e = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => e.Query($"rmdir('{P("absent")}').") );
        Assert.Contains("existence_error", ex.Message);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        File.WriteAllText(Path.Combine(_tmp, "f.txt"), "hi");
        var e = new PrologEngine();
        Assert.True(e.Query($"delete('{P("f.txt")}').").Success);
        Assert.False(File.Exists(Path.Combine(_tmp, "f.txt")));
    }

    [Fact]
    public void Delete_Missing_ExistenceError()
    {
        var e = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => e.Query($"delete('{P("absent.txt")}').") );
        Assert.Contains("existence_error", ex.Message);
    }

    [Fact]
    public void Rename_MovesFile()
    {
        File.WriteAllText(Path.Combine(_tmp, "old.txt"), "x");
        var e = new PrologEngine();
        Assert.True(e.Query($"rename('{P("old.txt")}', '{P("new.txt")}').").Success);
        Assert.False(File.Exists(Path.Combine(_tmp, "old.txt")));
        Assert.True(File.Exists(Path.Combine(_tmp, "new.txt")));
    }

    [Fact]
    public void Rename_TargetExists_PermissionError()
    {
        File.WriteAllText(Path.Combine(_tmp, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_tmp, "b.txt"), "y");
        var e = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => e.Query($"rename('{P("a.txt")}', '{P("b.txt")}').") );
        Assert.Contains("permission_error", ex.Message);
    }

    [Fact]
    public void ExistsFile_True_ForFiles_False_ForDirs()
    {
        File.WriteAllText(Path.Combine(_tmp, "f.txt"), "x");
        var e = new PrologEngine();
        Assert.True(e.Query($"exists_file('{P("f.txt")}').").Success);
        Assert.False(e.Query($"exists_file('{P("absent")}').").Success);
        Assert.False(e.Query($"exists_file('{_tmp.Replace('\\','/')}').").Success);
    }

    [Fact]
    public void ExistsDirectory_True_ForDirs_False_ForFiles()
    {
        File.WriteAllText(Path.Combine(_tmp, "f.txt"), "x");
        var e = new PrologEngine();
        Assert.True(e.Query($"exists_directory('{_tmp.Replace('\\','/')}').").Success);
        Assert.False(e.Query($"exists_directory('{P("f.txt")}').").Success);
        Assert.False(e.Query($"exists_directory('{P("absent")}').").Success);
    }

    [Fact]
    public void Directory6_EnumeratesEntries()
    {
        File.WriteAllText(Path.Combine(_tmp, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(_tmp, "b.txt"), "world!");
        Directory.CreateDirectory(Path.Combine(_tmp, "sub"));
        var e = new PrologEngine();
        var sol = e.Query(
            $"findall(N, directory('{_tmp.Replace('\\','/')}', N, _, _, _, _), Names), sort(Names, S).");
        Assert.True(sol.Success);
        // We expect at least the three names we created (sorted).
        var rendered = AstTermRenderer.Render(sol["S"]!);
        Assert.Contains("a.txt", rendered);
        Assert.Contains("b.txt", rendered);
        Assert.Contains("sub", rendered);
    }

    [Fact]
    public void Directory6_ReportsSizeAndDirectoryFlag()
    {
        File.WriteAllText(Path.Combine(_tmp, "data.txt"), "abcd");  // 4 bytes
        Directory.CreateDirectory(Path.Combine(_tmp, "subdir"));
        var e = new PrologEngine();
        var sol = e.Query(
            $"directory('{_tmp.Replace('\\','/')}', 'data.txt', _, _, _, S).");
        Assert.True(sol.Success);
        Assert.Equal(4L, ((IntTerm)sol["S"]!).Value);

        var sol2 = e.Query(
            $"directory('{_tmp.Replace('\\','/')}', 'subdir', M, _, _, _).");
        Assert.True(sol2.Success);
        // Mode 16 (Directory) is in the bitfield.
        long mode = ((IntTerm)sol2["M"]!).Value;
        Assert.True((mode & 16L) != 0, $"expected directory bit set, got {mode}");
    }

    [Fact]
    public void Directory6_Missing_ExistenceError()
    {
        var e = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => e.Query($"directory('{P("nodir")}', _, _, _, _, _).") );
        Assert.Contains("existence_error", ex.Message);
    }

    [Fact]
    public void Chdir_RoundtripsThroughWorkingDirectory()
    {
        var e = new PrologEngine();
        var sol = e.Query("chdir(D).");
        Assert.True(sol.Success);
        // D is bound to an atom path.
        Assert.IsType<AtomTerm>(sol["D"]);
    }
}
