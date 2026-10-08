using Shumway.Builtins;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-059: which system predicates a program may define, in the
/// global module and in a named module, what the load reports, and what the
/// introspection shows afterwards.</summary>
public sealed class RedefinitionRulesTests
{
    private static (PrologEngine Engine, string Warnings) Load(string source)
    {
        var warnings = new System.IO.StringWriter();
        var e = new PrologEngine { Out = new System.IO.StringWriter(), Warnings = warnings };
        e.ConsultString(source);
        return (e, warnings.ToString());
    }

    private static bool Refused(string warnings, string pi)
        => warnings.Contains($"no permission to modify static procedure ({pi.Replace("/", ")/")}");

    [Theory]
    [InlineData("call(_) :- true.", "call/1")]            // control
    [InlineData("atom_length(_, 42).", "atom_length/2")]  // ISO, C#
    [InlineData("findall(_, _, []).", "findall/3")]        // ISO, prelude
    [InlineData("between(_, _, 1).", "between/3")]         // engine, C#
    [InlineData("length(_, 42).", "length/2")]             // engine, prelude
    public void TheGlobalModuleCannotDefineASystemPredicate(string clause, string pi)
    {
        var (e, w) = Load(clause);
        Assert.True(Refused(w, pi), $"no error for {pi}: {w}");
        Assert.False(e.Query("predicate_property(" + pi.Split('/')[0]
            + "(" + string.Join(",", Enumerable.Repeat("_", int.Parse(pi.Split('/')[1])))
            + "), redefined).").Success);
    }

    [Theory]
    [InlineData("member(mine, _).", "member/2", "findall(X, member(X, [a]), L), L == [mine].")]
    [InlineData("append(_, _, mine).", "append/3", "append([a], [b], L), L == mine.")]
    public void TheGlobalModuleRedefinesALibraryPredicateWithAWarning(
        string clause, string pi, string check)
    {
        var (e, w) = Load(clause);
        Assert.Contains($"% {pi}: the definition in ", w);
        Assert.Contains("overrides the library predicate", w);
        Assert.True(e.Query(check).Success, check);
    }

    [Fact]
    public void AModuleDefinesEngineAndLibraryPredicatesSilentlyButNeverIso()
    {
        var (e, w) = Load("""
            :- module(m, [t/3]).
            between(_, _, own).
            member(own, _).
            atom_length(_, 42).
            t(B, M, N) :- between(1, 2, B), member(M, [a]), atom_length(abc, N).
            """);
        Assert.True(Refused(w, "atom_length/2"), w);
        Assert.DoesNotContain("between", w);
        Assert.DoesNotContain("overrides", w);
        Assert.True(e.Query("m:t(B, M, N), B == own, M == own, N == 3.").Success);
        // Outside the module the system's predicates are untouched.
        Assert.True(e.Query("between(1, 2, X), X == 1, member(Y, [a]), Y == a.").Success);
    }

    [Fact]
    public void TheIntrospectionShowsTheProgramsDefinition()
    {
        var (e, _) = Load("member(mine, _).");
        var sw = new System.IO.StringWriter();
        e.Out = sw;
        Assert.True(e.Query("listing(member/2).").Success);
        Assert.Contains("member(mine, _)", sw.ToString());
        Assert.True(e.Query("findall(P, predicate_property(member(_, _), P), L), "
                            + "L == [static, defined, redefined].").Success);
        Assert.True(e.Query("current_predicate(member/2).").Success);
        Assert.True(e.Query("predicate_property(atom_length(_, _), built_in), "
                            + "predicate_property(atom_length(_, _), iso).").Success);
    }

    [Fact]
    public void TheLibraryKeepsItsOwnDefinitions()
    {
        // nonmember/2 calls member/2: the library's, not the program's.
        var (e, _) = Load("member(mine, _).");
        Assert.True(e.Query("nonmember(mine, [a]).").Success);
    }

    [Fact]
    public void ADialectLibraryKeepsTheSystemsIsoPredicatesWithoutAReport()
    {
        // Another system's library implementing a standard predicate in Prolog
        // (Scryer's dcgs defines phrase/2,3): the clauses go, ours stays, and the
        // load is clean. The same file without a dialect is a program's text.
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "shumway-adr059-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "mydcgs.pl"),
                ":- module(mydcgs, [phrase/2, greet//0]).\n"
                + "phrase(_, [broken]).\n"
                + "greet --> [hi].\n");
            var warnings = new System.IO.StringWriter();
            var e = new PrologEngine { Out = new System.IO.StringWriter(), Warnings = warnings };
            e.AddLibraryDirectory(dir, "scryer");
            Assert.True(e.Query("use_module(library(mydcgs)).").Success);
            Assert.DoesNotContain("permission", warnings.ToString());
            Assert.True(e.Query("phrase(greet, [hi]).").Success);

            var (_, w) = Load(System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "mydcgs.pl")));
            Assert.True(Refused(w, "phrase/2"), w);
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    /// <summary>ISO 13211-1 section 8 with Cor.2, and 13211-3 8.18, by name and arity. The engine
    /// must classify exactly these as ISO: one missing is a predicate a module
    /// could redefine against the standard, one extra is a predicate of ours
    /// the standard does not freeze.</summary>
    private static readonly string[] IsoBuiltins =
    {
        "=/2", "unify_with_occurs_check/2", "\\=/2",
        "var/1", "atom/1", "integer/1", "float/1", "atomic/1", "compound/1", "nonvar/1",
        "number/1", "callable/1", "ground/1",
        "@=</2", "==/2", "\\==/2", "@</2", "@>/2", "@>=/2", "compare/3",
        "functor/3", "arg/3", "=../2", "copy_term/2", "term_variables/2", "subsumes_term/2",
        "acyclic_term/1",
        "is/2", "=:=/2", "=\\=/2", "</2", "=</2", ">/2", ">=/2",
        "clause/2", "current_predicate/1",
        "asserta/1", "assertz/1", "retract/1", "abolish/1", "retractall/1",
        "findall/3", "bagof/3", "setof/3",
        "current_input/1", "current_output/1", "set_input/1", "set_output/1",
        "open/3", "open/4", "close/1", "close/2", "flush_output/0", "flush_output/1",
        "stream_property/2", "at_end_of_stream/0", "at_end_of_stream/1", "set_stream_position/2",
        "get_char/1", "get_char/2", "get_code/1", "get_code/2", "peek_char/1", "peek_char/2",
        "peek_code/1", "peek_code/2", "put_char/1", "put_char/2", "put_code/1", "put_code/2",
        "nl/0", "nl/1",
        "get_byte/1", "get_byte/2", "peek_byte/1", "peek_byte/2", "put_byte/1", "put_byte/2",
        "read_term/2", "read_term/3", "read/1", "read/2", "write_term/2", "write_term/3",
        "write/1", "write/2", "writeq/1", "writeq/2", "write_canonical/1", "write_canonical/2",
        "op/3", "current_op/3", "char_conversion/2", "current_char_conversion/2",
        "\\+/1", "once/1", "repeat/0", "false/0",
        "call/2", "call/3", "call/4", "call/5", "call/6", "call/7", "call/8",
        "atom_length/2", "atom_concat/3", "sub_atom/5", "atom_chars/2", "atom_codes/2",
        "char_code/2", "number_chars/2", "number_codes/2",
        "set_prolog_flag/2", "current_prolog_flag/2", "halt/0", "halt/1",
        "sort/2", "keysort/2",
        "phrase/2", "phrase/3",
    };

    private static readonly string[] ControlConstructs =
        { ",/2", ";/2", "->/2", "!/0", "call/1", "true/0", "fail/0", "catch/3", "throw/1" };

    private static int Fid(string pi)
    {
        int slash = pi.LastIndexOf('/');
        return FunctorTable.Intern(AtomTable.Intern(pi[..slash], permanent: true).Id,
                                   int.Parse(pi[(slash + 1)..]));
    }

    [Fact]
    public void TheIsoCategoryIsExactlyTheStandards()
    {
        var e = new PrologEngine();
        e.Query("true.");
        foreach (string pi in IsoBuiltins)
            Assert.True(e.SystemKindOf(Fid(pi)) == PredicateKind.Iso,
                $"{pi} is {e.SystemKindOf(Fid(pi))?.ToString() ?? "unknown"}, not iso");
        foreach (string pi in ControlConstructs)
            Assert.True(e.SystemKindOf(Fid(pi)) == PredicateKind.Control,
                $"{pi} is {e.SystemKindOf(Fid(pi))?.ToString() ?? "unknown"}, not control");

        var declared = new HashSet<string>();
        foreach (var b in BuiltinsRegistry.AllEntries())
            if (b.Kind == PredicateKind.Iso) declared.Add($"{b.Name}/{b.Arity}");
        foreach (var d in PredicateDoc.Entries())
            if (d.Kind == PredicateKind.Iso) declared.Add($"{d.Name}/{d.Arity}");
        declared.ExceptWith(IsoBuiltins);
        Assert.True(declared.Count == 0, "declared iso but not in the standard: "
            + string.Join(", ", declared));
    }
}
