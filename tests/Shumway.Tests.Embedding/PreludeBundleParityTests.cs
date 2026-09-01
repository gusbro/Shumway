using System.Linq;
using System.Text.RegularExpressions;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The prelude's user-facing surface must survive the BAKED-bundle
/// boot path. A live consult reaches every prelude predicate through the
/// consult-direct fallback, but an engine booted from a bundle (FromBundle —
/// every --exe, WebShumway's stdlib) resolves only the module's PUBLICS bare:
/// a documented predicate missing from the `:- public` header works in the
/// REPL and raises existence_error from a bundle. That drift shipped once
/// (countall/2 and the prologue batch listed in the browser's own reference
/// while its engine could not call them) — these pins close the class.</summary>
public sealed class PreludeBundleParityTests
{
    // ---- source invariant: documented ⇒ builtin or :- public ----

    [Fact]
    public void EveryDocumentedPreludePredicate_IsBuiltinOrDeclaredPublic()
    {
        // Registers the builtin tables.
        _ = new PrologEngine();

        // The three `:- public` spellings the prelude uses: name/A,
        // 'name'/A and (name)/A.
        var publics = new System.Collections.Generic.HashSet<(string, int)>();
        foreach (Match m in Regex.Matches(Prelude.Source,
            @":-\s*public\s+(?:\(\s*([^)\s]+)\s*\)|'([^']+)'|([^\s'/(]+))\s*/\s*(\d+)\s*\."))
        {
            string name = m.Groups[1].Success ? m.Groups[1].Value
                : m.Groups[2].Success ? m.Groups[2].Value
                : m.Groups[3].Value;
            publics.Add((name, int.Parse(m.Groups[4].Value)));
        }

        var missing = new System.Collections.Generic.List<string>();
        foreach (Match m in Regex.Matches(Prelude.Source,
            @"^\s*%!\s*('[^']+'|[^\s(|]+)(\(([^)]*)\))?\s+\|",
            RegexOptions.Multiline))
        {
            string name = m.Groups[1].Value.Trim('\'');
            string args = m.Groups[3].Success ? m.Groups[3].Value : "";
            int arity = args.Length == 0 ? 0 : SplitTopLevel(args);
            if (publics.Contains((name, arity))) continue;
            int fid = Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern(name, permanent: true).Id, arity);
            if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out _)) continue;
            missing.Add($"{name}/{arity}");
        }

        Assert.True(missing.Count == 0,
            "documented but neither builtin nor :- public (unreachable from a "
            + "baked bundle): " + string.Join(", ", missing));
    }

    /// <summary>Arity of a template's argument list: top-level commas only
    /// (an argument like <c>[X|Xs]</c> or <c>f(A,B)</c> counts once).</summary>
    private static int SplitTopLevel(string args)
    {
        int depth = 0, count = 1;
        foreach (char c in args)
        {
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) count++;
        }
        return count;
    }

    // ---- behavioral pin: the prologue batch works from a baked bundle ----

    [Fact]
    public void BakedBundle_ResolvesThePrologueBatch()
    {
        var shmo = ShmoCompiler.CompileSource(
            "probe_root.\n"
            + "add3(A, B, C, D) :- D is A + B + C.\n"
            + "acc3(A, B, C, S0, S) :- S is S0 + A + B + C.\n",
            moduleNameFallback: "probe");
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { shmo },
            EntryPoints = new[] { new PredicateRef("probe_root", 0) },
            BakePrelude = true,
        });
        Assert.True(link.Success, string.Join("; ",
            link.Diagnostics.Select(d => d.Message)));

        var engine = PrologEngine.FromBundle(link.Bundle!);
        foreach (string q in new[]
        {
            "countall(between(1, 10, _), N), N == 10.",
            "nth0(1, [a, b, c], b, R), R == [a, c].",
            "nth1(2, [a, b, c], b, R), R == [a, c].",
            "maplist(add3, [1, 2], [10, 20], [100, 200], L), L == [111, 222].",
            "foldl(acc3, [1, 2], [3, 4], [5, 6], 0, S), S == 21.",
        })
        {
            Assert.True(engine.Query(q).Success, q);
        }
    }

    [Fact]
    public void BakedBundle_TreatsPreludePredicatesAsBuiltIn()
    {
        // The introspection contract must not depend on HOW the prelude got
        // installed: a baked $prelude entry records its predicates as
        // prelude functors exactly like the live consult, so
        // predicate_property reports built_in, current_predicate skips them
        // and listing stays quiet about them.
        var shmo = ShmoCompiler.CompileSource("probe_root2.\n",
            moduleNameFallback: "probe2");
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { shmo },
            EntryPoints = new[] { new PredicateRef("probe_root2", 0) },
            BakePrelude = true,
        });
        Assert.True(link.Success);

        var baked = PrologEngine.FromBundle(link.Bundle!);
        var live = new PrologEngine();
        foreach (string q in new[]
        {
            "predicate_property(findall(_, _, _), built_in).",
            "predicate_property(member(_, _), built_in).",
            "predicate_property(countall(_, _), built_in).",
            "\\+ current_predicate(member/2).",
            "\\+ current_predicate(findall/3).",
        })
        {
            Assert.True(live.Query(q).Success, "live: " + q);
            Assert.True(baked.Query(q).Success, "baked: " + q);
        }
    }
}
