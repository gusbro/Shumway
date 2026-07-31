using System;
using System.Collections.Generic;
using System.IO;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.DialectInterop;

/// <summary>End-to-end validation of real SWI libraries: load each (under the swi
/// dialect, so the shim auto-loads) and EXERCISE a representative predicate — not
/// just load. Records load + smoke outcomes and writes a report to
/// SHUMWAY_TRIAGE_OUT. Opt-in (SHUMWAY_SWI_LIB). This is the truthful runtime
/// measure the static missing-predicate survey cannot give.</summary>
public sealed class SwiEndToEndValidation
{
    private readonly ITestOutputHelper _out;
    public SwiEndToEndValidation(ITestOutputHelper output) => _out = output;

    // (library, smoke query). A null query = load-only (no representative call).
    private static readonly (string Lib, string? Query)[] Cases =
    {
        ("lists",       "last([1,2,3], X), X == 3, sum_list([1,2,3], 6), max_list([1,5,2], 5)."),
        ("apply",       "foldl([E,A,B]>>(B is A+E), [1,2,3], 0, S), S == 6, include([X]>>(X>1),[1,2,3],[2,3])."),
        ("pairs",       "pairs_keys_values(Ps, [a,b], [1,2]), Ps == [a-1,b-2], pairs_keys([x-1,y-2], [x,y])."),
        ("assoc",       "list_to_assoc([a-1,b-2,c-3], A), get_assoc(b, A, 2), put_assoc(d, A, 4, A2), get_assoc(d, A2, 4)."),
        ("ordsets",     "ord_union([1,3,5],[2,3,4],U), U == [1,2,3,4,5], ord_intersection([1,2,3],[2,3,4],[2,3])."),
        ("error",       "is_of_type(integer, 5), catch(must_be(atom, 5), error(type_error(atom,5),_), true)."),
        ("option",      "option(foo(V), [bar(1), foo(2)]), V == 2, ( option(zzz(_), [a], D) -> D == a ; true )."),
        ("aggregate",   "aggregate_all(count, member(_,[a,b,c]), 3), aggregate_all(sum(X), member(X,[1,2,3]), 6)."),
        ("gensym",      "gensym(foo, F1), F1 == foo1, gensym(foo, F2), F2 == foo2."),
        ("heaps",       "list_to_heap([3-c,1-a,2-b], H), get_from_heap(H, P, K, _), P == 1, K == a."),
        ("rbtrees",     "list_to_rbtree([a-1,b-2], T), rb_lookup(b, V, T), V == 2."),
        ("nb_rbtrees",  "list_to_nb_rbtree([a-1,b-2], T), rb_lookup(a, V, T), V == 1."),
        ("random",      "random_between(5,5,X), X == 5, random_permutation([1],[1])."),
        ("occurs",      "contains_term(a, f(b,a)), \\+ contains_term(z, f(a))."),
        ("terms",       "term_variables(f(X,Y,X), Vs), length(Vs, 2)."),
        ("dif",         "dif(a, b), \\+ dif(a, a)."),
        ("when",        "when(nonvar(X), X == 1), X = 1."),
        ("yall",        "call([X]>>(X > 0), 5), maplist([A,B]>>(B is A*2), [1,2], [2,4])."),
        ("solution_sequences", "findall(X, distinct(member(X,[1,1,2,2,3])), L), L == [1,2,3]."),
        ("charsio",     "with_output_to(string(S), write(hello)), atom_string(hello, S)."),
        ("sort",        "predsort([O,A,B]>>compare(O,A,B), [3,1,2,1], [1,2,3]), msort([3,1,2], [1,2,3])."),
        ("dicts",       null),
        ("thread",      null),
        ("thread_pool", null),
        ("shlib",       null),
        ("csv",         null),
        ("debug",       "debug(mytopic, 'hi ~w', [there])."),
        ("ansi_term",   "with_output_to(string(_), ansi_format([bold], '~w', [hi]))."),
        ("prolog_stack", null),
        ("intercept",   null),
        ("varnumbers",  "numbervars(f(X,Y), 0, _), varnumbers(f('$VAR'(0),'$VAR'(1)), T), T = f(_,_)."),
        ("nb_set",      "empty_nb_set(S), add_nb_set(a, S), nb_set_to_list(S, [a])."),
    };

    [Fact]
    public void Validate()
    {
        string? dir = Environment.GetEnvironmentVariable("SHUMWAY_SWI_LIB");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _out.WriteLine("SKIPPED: SHUMWAY_SWI_LIB not set / missing.");
            return;
        }

        var rows = new List<(string Lib, string Load, string Smoke)>();
        foreach (var (lib, query) in Cases)
        {
            string load, smoke;
            var errCapture = new StringWriter();
            var prevErr = Console.Error;
            Console.SetError(errCapture);
            PrologEngine? e = null;
            try
            {
                e = new PrologEngine();
                e.AddLibraryDirectory(dir, "swi");
                e.ConsultString($":- use_module(library({lib})).");
            }
            catch (Exception ex) { errCapture.Write("\nEXC:" + ex.Message); }
            finally { Console.SetError(prevErr); }

            string warn = errCapture.ToString();
            // The top-level use_module either threw (real load failure) or not. A
            // "failed:" warning means a DEPENDENCY couldn't load — the library
            // itself may still be usable, so we still run the smoke.
            bool topLevelOk = e is not null && !warn.Contains("EXC:");
            bool depWarn = warn.IndexOf("failed:", StringComparison.Ordinal) >= 0;
            load = !topLevelOk ? "LOADFAIL" : (depWarn ? "load(dep!)" : "load");

            if (!topLevelOk || query is null || e is null)
            {
                smoke = query is null ? "(load-only)" : "-";
            }
            else
            {
                try
                {
                    smoke = e.Query(query).Success ? "SMOKE-OK" : "SMOKE-FAIL";
                }
                catch (Exception ex)
                {
                    smoke = "SMOKE-EXC: " + FirstLine(ex.Message);
                }
            }
            rows.Add((lib, load, smoke));
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== SWI library end-to-end validation ===");
        sb.AppendLine($"{"library",-16} {"load",-10} smoke");
        foreach (var (lib, load, smoke) in rows)
            sb.AppendLine($"{lib,-16} {load,-10} {smoke}");
        string report = sb.ToString();
        _out.WriteLine(report);
        string? outFile = Environment.GetEnvironmentVariable("SHUMWAY_TRIAGE_OUT");
        if (!string.IsNullOrWhiteSpace(outFile)) File.WriteAllText(outFile, report);
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        s = nl >= 0 ? s.Substring(0, nl) : s;
        return s.Length > 80 ? s.Substring(0, 80) : s;
    }
}
