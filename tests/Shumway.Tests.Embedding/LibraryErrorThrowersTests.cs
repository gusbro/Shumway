using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>library(error)'s throwers (domain_error/2,3, type_error/2,3 and
/// the rest), as Scryer and SWI export them. The engine replaces Scryer's
/// library(error) with its own must_be/2 and can_be/2, and the rest of its
/// exports were missing: a library that reached an error path (crypto's
/// hex_bytes/2) raised existence_error(domain_error/3) instead of its error.
/// Without a context the error carries [], Scryer's convention.</summary>
public sealed class LibraryErrorThrowersTests
{
    private static string Caught(PrologEngine e, string goal)
    {
        var printed = new StringWriter();
        e.Out = printed;
        Assert.True(e.Query($"catch(({goal}), E, true), writeq(E).").Success, goal);
        return printed.ToString().Trim();
    }

    private static PrologEngine Engine()
        => new() { Out = new StringWriter(), Warnings = new StringWriter() };

    [Theory]
    [InlineData("instantiation_error", "error(instantiation_error,[])")]
    [InlineData("instantiation_error(foo/1)", "error(instantiation_error,foo/1)")]
    [InlineData("domain_error(hex_encoding, x)", "error(domain_error(hex_encoding,x),[])")]
    [InlineData("domain_error(hex_encoding, x, hex_bytes/2)", "error(domain_error(hex_encoding,x),hex_bytes/2)")]
    [InlineData("type_error(integer, a)", "error(type_error(integer,a),[])")]
    [InlineData("type_error(integer, a, succ/2)", "error(type_error(integer,a),succ/2)")]
    [InlineData("representation_error(max_arity)", "error(representation_error(max_arity),[])")]
    [InlineData("resource_error(memory)", "error(resource_error(memory),[])")]
    [InlineData("call_with_error_context(domain_error(d, x), k-v)", "error(domain_error(d,x),[k-v])")]
    [InlineData("call_with_error_context(call_with_error_context(type_error(t, x), a-1), b-2)",
                "error(type_error(t,x),[b-2,a-1])")]
    [InlineData("call_with_error_context(true, notapair)",
                "error(type_error(pair,notapair),call_with_error_context/2)")]
    public void EachThrowsItsError(string goal, string error)
    {
        Assert.Equal(error, Caught(Engine(), goal));
    }

    [Fact]
    public void CallWithErrorContextRunsTheGoalInTheCallersModule()
    {
        var e = Engine();
        e.ConsultString(":- module(m, [go/0]).\nlocal :- type_error(t, x).\n"
            + "go :- call_with_error_context(local, where-m).\n");
        Assert.Equal("error(type_error(t,x),[where-m])", Caught(e, "go"));
    }

    [Fact]
    public void AScryerLibraryReachesThemThroughLibraryError()
    {
        // Scryer's error.pl, recognised by its marker, gives way to the
        // engine's own; the library that imports it calls domain_error/3.
        string dir = Path.Combine(Path.GetTempPath(), $"shumway-errlib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "error.pl"),
                ":- module(error, [domain_error/3]).\n'$first_non_octet'(_, _).\n");
            File.WriteAllText(Path.Combine(dir, "checks.pl"),
                ":- module(checks, [check/1]).\n:- use_module(library(error), [domain_error/3]).\n"
                + "check(X) :- ( X == ok -> true ; domain_error(okness, X, check/1) ).\n");
            var e = Engine();
            e.AddLibraryDirectory(dir, "scryer");
            e.ConsultString(":- use_module(library(checks)).\n");
            Assert.Equal("error(domain_error(okness,bad),check/1)", Caught(e, "check(bad)"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void AProgramMayDefineItsOwn()
    {
        // ADR-059: a library predicate, redefined with a warning.
        var warnings = new StringWriter();
        var e = new PrologEngine { Out = new StringWriter(), Warnings = warnings };
        e.ConsultString("type_error(_, _) :- throw(mine).\n");
        Assert.Contains("overrides the library predicate", warnings.ToString());
        Assert.Equal("mine", Caught(e, "type_error(a, b)"));
    }
}
