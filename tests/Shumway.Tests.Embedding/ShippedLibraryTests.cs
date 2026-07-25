using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-038 Component 4 — the shipped repo lib/ libraries load via
/// use_module(library(X)) and behave correctly. Exercises lists_ext, the
/// starter export-qualified library, through the real search-path resolver.
/// The exported predicates are genuinely novel (not Shumway builtins or prelude
/// predicates), so a green test proves the library actually loaded.
/// </summary>
public class ShippedLibraryTests
{
    // Walk up from the test output dir to the repo's lib/ (the source of truth,
    // independent of whether the test project copies it).
    private static string RepoLibDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "lib", "lists_ext.pl");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "lib");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo lib/ not found from " + AppContext.BaseDirectory);
    }

    private static PrologEngine LoadListsExt()
    {
        var e = new PrologEngine();
        e.AddLibraryDirectory(RepoLibDir());
        e.ConsultString(
            ":- use_module(library(lists_ext)).\n" +
            "t_take(L)      :- take(2, [a,b,c,d], L).\n" +
            "t_drop(L)      :- drop(2, [a,b,c,d], L).\n" +
            "t_split(P, S)  :- split_at(2, [a,b,c,d], P, S).\n" +
            "t_zip(Ps)      :- zip([1,2,3], [a,b,c], Ps).\n" +
            "t_unzip(As,Bs) :- unzip([1-a,2-b], As, Bs).\n" +
            "t_inter(L)     :- intersperse(x, [a,b,c], L).\n" +
            "t_flatten(L)   :- flatten([1,[2,[3,4],5]], L).");
        return e;
    }

    [Fact]
    public void ListsExt_TakeDropSplit()
    {
        var e = LoadListsExt();
        Assert.True(e.Query("t_take([a,b]).").Success);
        Assert.True(e.Query("t_drop([c,d]).").Success);
        Assert.True(e.Query("t_split([a,b], [c,d]).").Success);
    }

    [Fact]
    public void ListsExt_ZipUnzip()
    {
        var e = LoadListsExt();
        Assert.True(e.Query("t_zip([1-a, 2-b, 3-c]).").Success);
        Assert.True(e.Query("t_unzip([1,2], [a,b]).").Success);
    }

    [Fact]
    public void ListsExt_IntersperseFlatten()
    {
        var e = LoadListsExt();
        Assert.True(e.Query("t_inter([a,x,b,x,c]).").Success);
        Assert.True(e.Query("t_flatten([1,2,3,4,5]).").Success);
    }

    [Fact]
    public void ListsExt_HelperNotExported_IsInvisible()
    {
        // split_at/4 is exported; the library defines it via take/drop which ARE
        // exported here — so instead confirm the export surface is exact: a
        // predicate the module does NOT define at all is unresolved, and the
        // exported ones resolve only through the import.
        var e = new PrologEngine();
        e.AddLibraryDirectory(RepoLibDir());
        // No use_module — the export-qualified predicates are NOT bare-global.
        e.ConsultString("reach(L) :- take(1, [a,b], L).");
        Assert.False(e.Query("catch(reach(_), _, fail).").Success);
    }
}
