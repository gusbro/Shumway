using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <c>close/2</c> and <c>keysort/2</c> — stdlib builtins Blint.pl
/// reaches for that Shumway didn't expose. <c>close/2</c> takes a
/// Stream + Options list and tolerates close-time errors when the
/// list contains <c>force(true)</c>. <c>keysort/2</c> stable-sorts
/// a list of K-V pairs by K in the standard order of terms.
/// </summary>
public class Close2AndKeysortTests
{
    [Fact]
    public void Close2_PlainOptions_ClosesStream()
    {
        var e = new PrologEngine();
        string path = Path.Combine(Path.GetTempPath(), $"shumway-close2-{Guid.NewGuid():N}.txt");
        try
        {
            var sol = e.Query($"open('{path.Replace("\\", "/")}', write, S, []), close(S, []).");
            Assert.True(sol.Success);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Close2_ForceTrue_TolerantOfErrors()
    {
        var e = new PrologEngine();
        string path = Path.Combine(Path.GetTempPath(), $"shumway-close2f-{Guid.NewGuid():N}.txt");
        try
        {
            // force(true) shouldn't change the success behaviour for
            // a normal close — just exercise the option-parsing path.
            var sol = e.Query($"open('{path.Replace("\\", "/")}', write, S, []), close(S, [force(true)]).");
            Assert.True(sol.Success);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Keysort_StableSortByKey()
    {
        var e = new PrologEngine();
        var sol = e.Query("keysort([3-c, 1-a, 2-b, 1-d], L).");
        Assert.True(sol.Success);
        // Expected: [1-a, 1-d, 2-b, 3-c] (stable: 1-a before 1-d).
        var l = sol["L"];
        // Walk the list cell-by-cell.
        var items = new List<string>();
        Term cursor = l!;
        while (cursor is CompoundTerm c && c.Functor == "." && c.Args.Length == 2)
        {
            items.Add(c.Args[0].ToString()!);
            cursor = c.Args[1];
        }
        Assert.Equal(new[] { "-(1, a)", "-(1, d)", "-(2, b)", "-(3, c)" }, items);
    }

    [Fact]
    public void Keysort_EmptyList()
    {
        var e = new PrologEngine();
        var sol = e.Query("keysort([], L).");
        Assert.True(sol.Success);
        Assert.Equal(new AtomTerm("[]"), sol["L"]);
    }

    [Fact]
    public void Keysort_NonPairElement_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(keysort([foo, 1-a], _), error(type_error(pair, _), _), true).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Keysort_SortsByKeyNotValue()
    {
        // Same key, different values; the sort comparator only looks
        // at keys, so the order of values follows insertion order.
        var e = new PrologEngine();
        var sol = e.Query("keysort([2-x, 1-z, 1-y], L).");
        Assert.True(sol.Success);
        var items = new List<string>();
        Term cursor = sol["L"]!;
        while (cursor is CompoundTerm c && c.Functor == "." && c.Args.Length == 2)
        {
            items.Add(c.Args[0].ToString()!);
            cursor = c.Args[1];
        }
        Assert.Equal(new[] { "-(1, z)", "-(1, y)", "-(2, x)" }, items);
    }
}
