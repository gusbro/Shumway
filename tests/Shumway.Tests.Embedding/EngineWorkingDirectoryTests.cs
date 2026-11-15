using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-071: an engine with a working directory of its own. Every
/// test checks that the process directory is where it was, and no test
/// changes it.</summary>
public sealed class EngineWorkingDirectoryTests : IDisposable
{
    private static readonly bool Windows = Environment.OSVersion.Platform == PlatformID.Win32NT;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "shumway-cwd-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _processDirectory = Directory.GetCurrentDirectory();

    public void Dispose()
    {
        Assert.Equal(_processDirectory, Directory.GetCurrentDirectory());
        for (int i = 0; i < 20; i++)
        {
            try { Directory.Delete(_root, recursive: true); return; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static PrologEngine EngineIn(string directory)
    {
        var e = new PrologEngine { Out = TextWriter.Null };
        e.SetCwdScope(CwdScope.Engine, directory);
        return e;
    }

    private static string Canonical(string directory)
        => directory.Replace('\\', '/').TrimEnd('/') + "/";

    private static string Atom(Term? t) => Assert.IsType<AtomTerm>(t).Name;

    [Fact]
    public void ByDefault_TheEngineUsesTheProcessDirectory()
    {
        var e = new PrologEngine();
        Assert.Equal(CwdScope.Process, e.CwdScope);
        Assert.Equal(Directory.GetCurrentDirectory(), e.WorkingDirectory);
        // Unchanged, not made absolute: nothing a program observes moves.
        Assert.Equal("data/x.txt", e.ResolvePath("data/x.txt"));
        Assert.True(e.Query("current_prolog_flag(cwd_scope, process).").Success);
        var sol = e.Query("working_directory(D, D).");
        Assert.True(sol.Success);
        Assert.Equal(Canonical(Directory.GetCurrentDirectory()), Atom(sol["D"]));
    }

    [Fact]
    public void TwoEngines_EachWritesInItsOwnDirectory()
    {
        string a = Dir("a"), b = Dir("b");
        string name = "out-" + Guid.NewGuid().ToString("N") + ".txt";
        var ea = EngineIn(a);
        var eb = EngineIn(b);
        Assert.True(ea.Query($"open('{name}', write, S), write(S, from_a), close(S).").Success);
        Assert.True(eb.Query($"open('{name}', write, S), write(S, from_b), close(S).").Success);

        Assert.Equal("from_a", File.ReadAllText(Path.Combine(a, name)));
        Assert.Equal("from_b", File.ReadAllText(Path.Combine(b, name)));
        // The control: with the process directory in play the file would be there.
        Assert.NotEqual(a, _processDirectory);
        Assert.NotEqual(b, _processDirectory);
        Assert.False(File.Exists(Path.Combine(_processDirectory, name)));
    }

    [Fact]
    public void WorkingDirectory2_MovesThisEngineOnly()
    {
        string a = Dir("a"), b = Dir("b");
        Directory.CreateDirectory(Path.Combine(a, "sub"));
        var ea = EngineIn(a);
        var eb = EngineIn(b);

        var moved = ea.Query("working_directory(Old, sub), working_directory(New, New).");
        Assert.True(moved.Success);
        Assert.Equal(Canonical(a), Atom(moved["Old"]));
        Assert.Equal(Canonical(Path.Combine(a, "sub")), Atom(moved["New"]));
        Assert.Equal(Path.Combine(a, "sub"), ea.WorkingDirectory);

        var other = eb.Query("working_directory(D, D).");
        Assert.Equal(Canonical(b), Atom(other["D"]));

        // A relative path follows the move.
        Assert.True(ea.Query("open('here.txt', write, S), close(S).").Success);
        Assert.True(File.Exists(Path.Combine(a, "sub", "here.txt")));
    }

    [Fact]
    public void WorkingDirectory2_ADirectoryThatDoesNotExist_IsAnExistenceError()
    {
        var e = EngineIn(Dir("a"));
        Assert.True(e.Query(
            "catch(working_directory(_, nowhere), error(existence_error(_, _), _), true).").Success);
        Assert.Equal(Path.Combine(_root, "a"), e.WorkingDirectory);
    }

    [Fact]
    public void TheFlag_ReportsTheScope_AndCannotBeSet()
    {
        var e = EngineIn(Dir("a"));
        Assert.True(e.Query("current_prolog_flag(cwd_scope, engine).").Success);
        Assert.True(e.Query(@"
            catch(set_prolog_flag(cwd_scope, process),
                  error(permission_error(modify, flag, cwd_scope), _), true).").Success);
        Assert.Equal(CwdScope.Engine, e.CwdScope);
    }

    [Fact]
    public void TheHost_ChangesTheScopeAtAnyTime()
    {
        string a = Dir("a"), b = Dir("b");
        var e = EngineIn(a);

        // Engine again with no directory: it stays where it stands.
        e.SetCwdScope(CwdScope.Engine);
        Assert.Equal(a, e.WorkingDirectory);
        // Engine again with one: it moves there.
        e.SetCwdScope(CwdScope.Engine, b);
        Assert.Equal(b, e.WorkingDirectory);

        // Back to the process: its own directory is dropped.
        e.SetCwdScope(CwdScope.Process);
        Assert.Equal(CwdScope.Process, e.CwdScope);
        Assert.Equal(_processDirectory, e.WorkingDirectory);
        Assert.Equal("x.txt", e.ResolvePath("x.txt"));
        Assert.True(e.Query("current_prolog_flag(cwd_scope, process).").Success);

        // And to a directory of its own once more, between two queries.
        e.SetCwdScope(CwdScope.Engine, a);
        Assert.True(e.Query("open('late.txt', write, S), close(S).").Success);
        Assert.True(File.Exists(Path.Combine(a, "late.txt")));
    }

    [Fact]
    public void TheHost_MovesTheEnginesDirectory()
    {
        string a = Dir("a");
        Directory.CreateDirectory(Path.Combine(a, "sub"));
        var e = EngineIn(a);
        e.WorkingDirectory = "sub";            // relative to where it stands
        Assert.Equal(Path.Combine(a, "sub"), e.WorkingDirectory);
        Assert.Equal(Canonical(Path.Combine(a, "sub")), Atom(e.Query("working_directory(D, D).")["D"]));
        Assert.Throws<DirectoryNotFoundException>(() => e.WorkingDirectory = "nowhere");
        Assert.Equal(Path.Combine(a, "sub"), e.WorkingDirectory);
    }

    [Fact]
    public void ADirectoryOfItsOwn_HasToExist()
    {
        var e = new PrologEngine();
        Assert.Throws<DirectoryNotFoundException>(
            () => e.SetCwdScope(CwdScope.Engine, Path.Combine(_root, "nowhere")));
        Assert.Equal(CwdScope.Process, e.CwdScope);
    }

    [Fact]
    public void Consult_FromPrologAndFromTheHost_IsRelativeToTheEngine()
    {
        string a = Dir("a"), b = Dir("b");
        File.WriteAllText(Path.Combine(a, "facts.pl"), "who(a).\n");
        File.WriteAllText(Path.Combine(b, "facts.pl"), "who(b).\n");
        var ea = EngineIn(a);
        var eb = EngineIn(b);

        Assert.True(ea.Query("consult(facts).").Success);
        eb.ConsultFile("facts.pl");

        Assert.Equal("a", Atom(ea.Query("who(X).")["X"]));
        Assert.Equal("b", Atom(eb.Query("who(X).")["X"]));
    }

    [Fact]
    public void EnsureLoaded_AndInclude_AreRelativeToTheEngine()
    {
        string a = Dir("a");
        File.WriteAllText(Path.Combine(a, "one.pl"), "one(1).\n");
        File.WriteAllText(Path.Combine(a, "two.pl"), "two(2).\n");
        var e = EngineIn(a);
        Assert.True(e.Query("ensure_loaded(one).").Success);
        // Text with no file of its own: the include has only the engine's directory.
        e.ConsultString(":- include('two.pl').\n");
        Assert.True(e.Query("one(1), two(2).").Success);
    }

    [Fact]
    public void FileSystemBuiltins_AreRelativeToTheEngine()
    {
        string a = Dir("a");
        File.WriteAllText(Path.Combine(a, "data.txt"), "12345");
        Directory.CreateDirectory(Path.Combine(a, "inner"));
        var e = EngineIn(a);

        Assert.True(e.Query("exists_file('data.txt'), exists_directory(inner).").Success);
        Assert.True(e.Query("file_size('data.txt', 5).").Success);
        Assert.True(e.Query("directory_files('.', Fs), memberchk('data.txt', Fs), memberchk(inner, Fs).").Success);

        var abs = e.Query("absolute_file_name('data.txt', A).");
        Assert.Equal(Canonical(a) + "data.txt", Atom(abs["A"]));

        Assert.True(e.Query("copy_file('data.txt', 'copy.txt'), rename('copy.txt', 'moved.txt').").Success);
        Assert.True(File.Exists(Path.Combine(a, "moved.txt")));
        Assert.True(e.Query("delete('moved.txt'), \\+ exists_file('moved.txt').").Success);
        Assert.False(File.Exists(Path.Combine(a, "moved.txt")));

        // The control: none of it is visible from an engine elsewhere.
        var elsewhere = EngineIn(Dir("b"));
        Assert.True(elsewhere.Query("\\+ exists_file('data.txt'), \\+ exists_directory(inner).").Success);
    }

    [Fact]
    public void SeeAndTell_AreRelativeToTheEngine()
    {
        string a = Dir("a");
        var e = EngineIn(a);
        Assert.True(e.Query("tell('t.txt'), write('hello.'), nl, told.").Success);
        Assert.True(File.Exists(Path.Combine(a, "t.txt")));
        var sol = e.Query("see('t.txt'), read(X), seen.");
        Assert.True(sol.Success);
        Assert.Equal("hello", Atom(sol["X"]));
    }

    [Fact]
    public void AStreamKeepsTheNameItWasOpenedWith()
    {
        // The two scopes are indistinguishable from Prolog with one engine.
        string a = Dir("a");
        var own = EngineIn(a);
        var sol = own.Query(
            "open('named.txt', write, S), stream_property(S, file_name(F)), close(S).");
        Assert.True(sol.Success);
        Assert.Equal("named.txt", Atom(sol["F"]));
    }

    [Fact]
    public void SavedState_IsWrittenInTheEnginesDirectory()
    {
        string a = Dir("a");
        var e = EngineIn(a);
        e.ConsultString(":- dynamic(k/1).\n");
        Assert.True(e.Query("assertz(k(1)), save_state('state.sav').").Success);
        Assert.True(File.Exists(Path.Combine(a, "state.sav")));
        // Read back by another engine standing in the same directory.
        var reader = EngineIn(a);
        Assert.True(reader.Query("restore_state('state.sav').").Success);
        Assert.True(reader.Query("k(1).").Success);
    }

    [Fact]
    public void Shell_StartsTheChildInTheEnginesDirectory()
    {
        string a = Dir("a");
        var e = EngineIn(a);
        Assert.True(e.Query(Windows ? "shell('cd > where.txt')." : "shell('pwd > where.txt').").Success);
        string seen = File.ReadAllText(Path.Combine(a, "where.txt")).Trim();
        Assert.Equal(Path.GetFullPath(a).TrimEnd('\\', '/'), seen.TrimEnd('\\', '/'),
            ignoreCase: Windows);
    }

    [Fact]
    public void ProcessCreate_StartsTheChildInTheEnginesDirectory_UnlessCwdSaysOtherwise()
    {
        string a = Dir("a");
        Directory.CreateDirectory(Path.Combine(a, "sub"));
        var e = EngineIn(a);
        e.ConsultString(":- use_module(library(process)).\n" + (Windows
            ? "sys_shell(Command, path('cmd.exe'), ['/d', '/c', Command]).\n"
            : "sys_shell(Command, path(sh), ['-c', Command]).\n"));
        string print = Windows ? "cd > where.txt" : "pwd > where.txt";

        Assert.True(e.Query(
            $"sys_shell('{print}', Exe, Args), process_create(Exe, Args, [process(P)]), process_wait(P, _).").Success);
        Assert.Equal(a.TrimEnd('\\', '/'),
            File.ReadAllText(Path.Combine(a, "where.txt")).Trim().TrimEnd('\\', '/'),
            ignoreCase: Windows);

        // A relative cwd/1 is relative to the engine's directory.
        Assert.True(e.Query(
            $"sys_shell('{print}', Exe, Args), process_create(Exe, Args, [cwd(sub), process(P)]), process_wait(P, _).").Success);
        Assert.Equal(Path.Combine(a, "sub").TrimEnd('\\', '/'),
            File.ReadAllText(Path.Combine(a, "sub", "where.txt")).Trim().TrimEnd('\\', '/'),
            ignoreCase: Windows);
    }

    [Fact]
    public void TheProcessScope_IsTheDefault_AndTakesNoDirectory()
    {
        var e = new PrologEngine();
        e.SetCwdScope(CwdScope.Process);
        Assert.Equal(CwdScope.Process, e.CwdScope);
        Assert.Throws<ArgumentException>(() => e.SetCwdScope(CwdScope.Process, Dir("a")));
    }

    [Fact]
    public void ResolvePath_IsWhatAForeignPredicateUses()
    {
        string a = Dir("a");
        var e = EngineIn(a);
        Assert.Equal(Path.Combine(a, "x.txt"), e.ResolvePath("x.txt"));
        Assert.Equal(Path.Combine(a, "x.txt"), e.ResolvePath(Path.Combine(a, "x.txt")));
        Assert.Equal("", e.ResolvePath(""));
    }

    [Fact]
    public void TheInitialDirectory_DefaultsToTheProcessDirectoryAtCreation()
    {
        var e = new PrologEngine();
        e.SetCwdScope(CwdScope.Engine);
        Assert.Equal(_processDirectory, e.WorkingDirectory);
        // Its own from here on: moving it leaves the process where it was.
        string a = Dir("a");
        Assert.True(e.Query($"working_directory(_, '{a.Replace('\\', '/')}').").Success);
        Assert.Equal(a, e.WorkingDirectory);
    }
}
