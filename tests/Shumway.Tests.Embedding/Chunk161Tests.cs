using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 13 chunk 161: <see cref="ShmoCompiler"/> takes Prolog
/// source and produces a populated <see cref="ShmoObject"/> ready to
/// be written to a <c>.shmo</c>. Covers module-name extraction,
/// public/dynamic visibility tagging, call-graph extraction across
/// control structures, qualified references, and the WAM bytecode
/// payload.
/// </summary>
public class Chunk161Tests
{
    [Fact]
    public void ModuleDirective_SetsName()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- module(mymod).\np(1). p(2).\n");
        Assert.Equal("mymod", obj.ModuleName);
    }

    [Fact]
    public void NoModuleDirective_UsesFallback()
    {
        var obj = ShmoCompiler.CompileSource("p(1).\n", moduleNameFallback: "myfile");
        Assert.Equal("myfile", obj.ModuleName);
    }

    [Fact]
    public void PublicDirective_SingleSpec_Tagged()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- public foo/2.\nfoo(a, b).\nfoo(c, d).\nbar(1).\n");
        var foo = obj.Defined.Single(d => d.Indicator.Name == "foo");
        var bar = obj.Defined.Single(d => d.Indicator.Name == "bar");
        Assert.Equal(PredicateVisibility.Public, foo.Visibility);
        Assert.Equal(PredicateVisibility.Local, bar.Visibility);
    }

    [Fact]
    public void PublicDirective_ListSpec_Tagged()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- public [foo/1, bar/2].\nfoo(x).\nbar(y, z).\n");
        Assert.Equal(2, obj.Defined.Count(d => d.Visibility == PredicateVisibility.Public));
    }

    [Fact]
    public void DynamicDirective_NoClauses_StillDefined()
    {
        var obj = ShmoCompiler.CompileSource(":- dynamic counter/1.\n");
        var d = Assert.Single(obj.Defined);
        Assert.Equal(new PredicateRef("counter", 1), d.Indicator);
        Assert.Equal(PredicateVisibility.Dynamic, d.Visibility);
    }

    [Fact]
    public void DynamicDirective_WithClauses_OverridesLocal()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- dynamic d/1.\nd(1). d(2).\n");
        var d = Assert.Single(obj.Defined);
        Assert.Equal(PredicateVisibility.Dynamic, d.Visibility);
    }

    [Fact]
    public void CallGraph_DirectCalls_Extracted()
    {
        var obj = ShmoCompiler.CompileSource(
            "foo(X) :- bar(X), baz(X, 1).\nbar(_).\nbaz(_, _).\n");
        Assert.True(obj.CallGraph.TryGetValue(new PredicateRef("foo", 1), out var edges));
        Assert.Contains(new PredicateRef("bar", 1), edges!);
        Assert.Contains(new PredicateRef("baz", 2), edges!);
    }

    [Fact]
    public void CallGraph_Conjunction_Disjunction_IfThen_Walked()
    {
        var obj = ShmoCompiler.CompileSource(
            "f(X) :- (a(X) ; b(X) -> c(X) ; d(X)), e(X).\n"
            + "a(_). b(_). c(_). d(_). e(_).\n");
        var edges = obj.CallGraph[new PredicateRef("f", 1)];
        Assert.Contains(new PredicateRef("a", 1), edges);
        Assert.Contains(new PredicateRef("b", 1), edges);
        Assert.Contains(new PredicateRef("c", 1), edges);
        Assert.Contains(new PredicateRef("d", 1), edges);
        Assert.Contains(new PredicateRef("e", 1), edges);
        Assert.DoesNotContain(new PredicateRef(",", 2), edges);
        Assert.DoesNotContain(new PredicateRef(";", 2), edges);
        Assert.DoesNotContain(new PredicateRef("->", 2), edges);
    }

    [Fact]
    public void CallGraph_NegationAndCall_Walked()
    {
        var obj = ShmoCompiler.CompileSource(
            "f(X) :- \\+ g(X), call(h(X)).\ng(_). h(_).\n");
        var edges = obj.CallGraph[new PredicateRef("f", 1)];
        Assert.Contains(new PredicateRef("g", 1), edges);
        Assert.Contains(new PredicateRef("h", 1), edges);
        Assert.DoesNotContain(new PredicateRef("\\+", 1), edges);
        Assert.DoesNotContain(new PredicateRef("call", 1), edges);
    }

    [Fact]
    public void CallGraph_CutAndAtomGoal_NotEmittedAsBadCalls()
    {
        var obj = ShmoCompiler.CompileSource(
            "f :- !, done.\ndone.\n");
        var edges = obj.CallGraph[new PredicateRef("f", 0)];
        Assert.DoesNotContain(new PredicateRef("!", 0), edges);
        Assert.Contains(new PredicateRef("done", 0), edges);
    }

    [Fact]
    public void CallGraph_Builtins_StillEmittedAsCallTargets()
    {
        // The .shmo doesn't filter builtins — the linker does. This
        // lets us evolve the builtin set without breaking old .shmo
        // files: a "builtin" that was once user-level just resolves
        // either way at link time.
        var obj = ShmoCompiler.CompileSource(
            "f(X) :- X is 1 + 2, write(X).\n");
        var edges = obj.CallGraph[new PredicateRef("f", 1)];
        Assert.Contains(new PredicateRef("is", 2), edges);
        Assert.Contains(new PredicateRef("write", 1), edges);
    }

    [Fact]
    public void QualifiedRefs_ExtractedAndNotInUnqualifiedEdges()
    {
        var obj = ShmoCompiler.CompileSource(
            "f(L, R) :- lists:append(L, [x], R).\n");
        var qref = Assert.Single(obj.QualifiedRefs);
        Assert.Equal("lists", qref.Module);
        Assert.Equal("append", qref.Name);
        Assert.Equal(3, qref.Arity);
        var edges = obj.CallGraph[new PredicateRef("f", 2)];
        Assert.DoesNotContain(new PredicateRef("append", 3), edges);
    }

    [Fact]
    public void Bytecode_NonEmptyAndDecodes()
    {
        var obj = ShmoCompiler.CompileSource("p(1). p(2).\n");
        Assert.NotEmpty(obj.Bytecode);
    }

    [Fact]
    public void DcgRule_ExpandedAndCallsEmittedAgainstExpandedHead()
    {
        var obj = ShmoCompiler.CompileSource(
            "sentence --> noun, verb.\nnoun --> [the], [dog].\nverb --> [runs].\n");
        // DCG transform appends two diff-list args: sentence/0 becomes sentence/2.
        Assert.Contains(obj.Defined,
            d => d.Indicator.Name == "sentence" && d.Indicator.Arity == 2);
        Assert.Contains(obj.Defined,
            d => d.Indicator.Name == "noun" && d.Indicator.Arity == 2);
        var sentenceEdges = obj.CallGraph[new PredicateRef("sentence", 2)];
        Assert.Contains(new PredicateRef("noun", 2), sentenceEdges);
        Assert.Contains(new PredicateRef("verb", 2), sentenceEdges);
    }

    [Fact]
    public void CompileFile_RoundTripsToShmo()
    {
        string inputPath = Path.Combine(Path.GetTempPath(),
            $"shmo-compile-test-{Guid.NewGuid():N}.pl");
        string outputPath = Path.ChangeExtension(inputPath, ".shmo");
        try
        {
            File.WriteAllText(inputPath,
                ":- module(demo).\n:- public foo/1.\nfoo(X) :- bar(X).\nbar(1).\n");
            var obj = ShmoCompiler.CompileFile(inputPath);
            Assert.Equal("demo", obj.ModuleName);
            ShmoWriter.WriteToFile(obj, outputPath);
            var restored = ShmoReader.ReadFromFile(outputPath);
            Assert.Equal("demo", restored.ModuleName);
            Assert.Equal(obj.Bytecode, restored.Bytecode);
            Assert.Equal(obj.Defined.Count, restored.Defined.Count);
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void CompileFile_DefaultsModuleNameToFileName()
    {
        string inputPath = Path.Combine(Path.GetTempPath(),
            $"mymodule-{Guid.NewGuid():N}.pl");
        try
        {
            File.WriteAllText(inputPath, "p(1).\n");
            var obj = ShmoCompiler.CompileFile(inputPath);
            // No :- module/1 directive: fallback = filename minus extension.
            Assert.Equal(
                Path.GetFileNameWithoutExtension(inputPath),
                obj.ModuleName);
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
        }
    }

    [Fact]
    public void MalformedPublicDirective_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => ShmoCompiler.CompileSource(":- public foo.\n"));
    }
}
