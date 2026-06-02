using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 24 chunk 267 — Edinburgh-style I/O (see/seen/seeing,
/// tell/told/telling, get/get0/put/skip, tab/2). Thin layer over
/// the ISO stream registry; Arity-Prolog programs that use the
/// Edinburgh idiom (set current input/output and read with
/// get/get0) now port unchanged.
/// </summary>
public class EdinburghIoTests
{
    [Fact]
    public void Tell_Then_Told_WritesAndClosesFile()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_edinburgh_test_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var engine = new PrologEngine();
            var p = path.Replace('\\', '/');
            Assert.True(engine.Query($"tell('{p}'), write(hello), nl, told.").Success);
            string content = File.ReadAllText(path);
            Assert.Equal("hello" + Environment.NewLine, content);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void See_Then_Get0_ReadsFromFile()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_edinburgh_in_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "abc");
            var engine = new PrologEngine();
            var p = path.Replace('\\', '/');
            var sol = engine.Query(
                $"see('{p}'), get0(A), get0(B), get0(C), seen.");
            Assert.True(sol.Success);
            Assert.Equal((long)'a', ((IntTerm)sol["A"]!).Value);
            Assert.Equal((long)'b', ((IntTerm)sol["B"]!).Value);
            Assert.Equal((long)'c', ((IntTerm)sol["C"]!).Value);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Get_SkipsNonPrintingControlChars()
    {
        // get/1 skips non-printing codes (< 32) but DOES return space
        // and printable chars. "\t\n\rHi" → first read returns 'H' (72).
        // Space (32) is printable, so it would have been returned too.
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_edin_get_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "\t\n\rHi");
            var engine = new PrologEngine();
            var p = path.Replace('\\', '/');
            var sol = engine.Query($"see('{p}'), get(C), seen.");
            Assert.Equal((long)'H', ((IntTerm)sol["C"]!).Value);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Seeing_ReturnsCurrentInputFilename()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_edin_seeing_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "");
            var engine = new PrologEngine();
            var p = path.Replace('\\', '/');
            var sol = engine.Query($"see('{p}'), seeing(F), seen.");
            Assert.True(sol.Success);
            // F is bound to the path atom.
            Assert.Equal(p, ((AtomTerm)sol["F"]!).Name);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Seeing_DefaultsToUser_WhenInputIsUserInput()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("seeing(F).");
        Assert.True(sol.Success);
        Assert.Equal("user", ((AtomTerm)sol["F"]!).Name);
    }

    [Fact]
    public void Telling_ReturnsCurrentOutputFilename()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_edin_telling_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var engine = new PrologEngine();
            var p = path.Replace('\\', '/');
            var sol = engine.Query($"tell('{p}'), telling(F), told.");
            Assert.True(sol.Success);
            Assert.Equal(p, ((AtomTerm)sol["F"]!).Name);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Skip_AdvancesPastTargetCode()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_edin_skip_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "ignore until #then read");
            var engine = new PrologEngine();
            var p = path.Replace('\\', '/');
            // 0'# is the char-code literal for '#' (35).
            var sol = engine.Query(
                $"see('{p}'), skip(0'#), get0(C), seen.");
            // After skip past '#', the next char is 't'.
            Assert.Equal((long)'t', ((IntTerm)sol["C"]!).Value);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Put_WritesCodeToCurrentOutput()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_edin_put_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var engine = new PrologEngine();
            var p = path.Replace('\\', '/');
            // 0'A = 65. Write A, B, C via put.
            engine.Query($"tell('{p}'), put(65), put(66), put(67), told.");
            Assert.Equal("ABC", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Tell_Twice_ClosesPreviousFile()
    {
        // Calling tell again should close the first file and open
        // the second.
        string path1 = Path.Combine(Path.GetTempPath(),
            "shumway_edin_tt1_" + Guid.NewGuid().ToString("N") + ".txt");
        string path2 = Path.Combine(Path.GetTempPath(),
            "shumway_edin_tt2_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var engine = new PrologEngine();
            var p1 = path1.Replace('\\', '/');
            var p2 = path2.Replace('\\', '/');
            engine.Query(
                $"tell('{p1}'), write(one), tell('{p2}'), write(two), told.");
            Assert.Equal("one", File.ReadAllText(path1));
            Assert.Equal("two", File.ReadAllText(path2));
        }
        finally
        {
            if (File.Exists(path1)) File.Delete(path1);
            if (File.Exists(path2)) File.Delete(path2);
        }
    }

    [Fact]
    public void Seen_RevertsToUserInput()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_edin_seen_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "x");
            var engine = new PrologEngine();
            var p = path.Replace('\\', '/');
            var sol = engine.Query($"see('{p}'), seen, seeing(F).");
            Assert.Equal("user", ((AtomTerm)sol["F"]!).Name);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void See_NonexistentFile_ExistenceError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("see('/no/such/path/file.txt')."));
        Assert.Contains("existence_error", ex.Message);
    }

    [Fact]
    public void Get0_EofReturnsMinusOne()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_edin_eof_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "");
            var engine = new PrologEngine();
            var p = path.Replace('\\', '/');
            var sol = engine.Query($"see('{p}'), get0(C), seen.");
            Assert.Equal(-1L, ((IntTerm)sol["C"]!).Value);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Tab2_WritesNSpacesToHandleStream()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_edin_tab2_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var engine = new PrologEngine();
            var p = path.Replace('\\', '/');
            engine.Query(
                $"open('{p}', write, S), tab(S, 5), write(S, x), close(S).");
            Assert.Equal("     x", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
