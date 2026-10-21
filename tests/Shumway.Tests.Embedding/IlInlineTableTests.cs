using System.Diagnostics.Tracing;
using System.Reflection;
using System.Text;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><see cref="IlInlineTable"/> holds, for each method the emitter
/// calls, the inlines the JIT makes for a call to it. The table must name
/// every such method, and on Release it must agree with the JIT of the
/// build, measured by a probe method per callee under the JIT's inlining
/// events; with <c>SHUMWAY_INLINE_TABLE_OUT</c> set to a path the test
/// writes the table there instead of comparing. The JIT remembers a callee
/// it once refused for its own code, so the probes of the methods known
/// before any compilation go first, and the generator is run with this
/// class alone in the process. Exclusive: the JIT's events reach the
/// listener late or not at all under the load of other classes.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class IlInlineTableTests
{
    // A corpus wide enough to make the emitter call every helper it has:
    // structures and lists, arithmetic of every shape, cuts, meta-calls,
    // strings and floats, dynamic predicates, exceptions.
    private const string Corpus = @"
:- dynamic counter/1.
counter(0).
chain(0, _, []) :- !.
chain(N, f(A, g(B)), [A-B, [A, B|N]|T]) :- N > 0, N1 is N - 1, M is N1 * 2 + 1, chain(N1, f(B, g(M)), T).
chain(N, h(A), [A, h(A, N)|T]) :- N > 0, N1 is N - 1, M is N1 - A, chain(N1, f(M, g(A)), T).
chain(N, X, [X, X|T]) :- N > 0, N1 is N - 1, chain(N1, X, T).
app([], L, L).
app([H|T], L, [H|R]) :- app(T, L, R).
nrev([], []).
nrev([H|T], R) :- nrev(T, RT), app(RT, [H], R).
len([], 0).
len([_|T], N) :- len(T, M), N is M + 1.
arith(X, Y, R) :- R is (X * Y + 7) mod 5 - X // 2 + abs(Y) + max(X, Y) + min(X, 1) - sign(Y) + \ X.
cmp(X, Y, lt) :- X < Y, !.
cmp(X, Y, eq) :- X =:= Y, !.
cmp(_, _, gt).
flt(X, R) :- R is X * 1.5 + 2.25 / 3.0.
str(S, L) :- atom_length(S, L).
tests(X) :- atom(X), !.
tests(X) :- integer(X), !.
tests(X) :- var(X), !.
tests(X) :- compound(X), !, functor(X, _, A), A > 0.
tests(X) :- X = ""text"".
meta(G) :- call(G).
meta2(X, L) :- findall(Y, (member(Y, [1, 2, 3]), Y > X), L).
bump :- retract(counter(N)), N1 is N + 1, assertz(counter(N1)).
caught(X) :- catch(throw(my(X)), my(Y), X = Y).
member(X, [X|_]).
member(X, [_|T]) :- member(X, T).
guard(X, Y) :- X > 1, Y = big.
guard(X, Y) :- X =< 1, Y = small.
run :- chain(6, f(1, g(2)), _), nrev([a, b, c, d, e], _), len([1, 2, 3], _),
       arith(7, -3, _), cmp(1, 2, _), cmp(2, 2, _), cmp(3, 2, _), flt(2, _),
       str(hello, _), tests(a), tests(1), tests(_), tests(f(x)), tests(""text""),
       meta(true), meta2(1, _), bump, caught(1), guard(2, _), guard(0, _).
";

    /// <summary>The emitter's static method fields: known before anything
    /// is compiled.</summary>
    private static List<MethodBase> FieldCallees()
    {
        var set = new HashSet<MethodBase>();
        foreach (var f in typeof(IlPredicateCompiler).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            if (typeof(MethodBase).IsAssignableFrom(f.FieldType) && f.GetValue(null) is MethodBase m)
                set.Add(m);
        return set.Where(Probed).OrderBy(IlInlineTable.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>A method the table is for: not one this emitter builds, not
    /// a Debug marker (emitted by Debug builds alone, whose JIT is not the
    /// table's).</summary>
    private static bool Probed(MethodBase m)
        => m is not System.Reflection.Emit.MethodBuilder && m.DeclaringType != typeof(IlDebugMarkers);

    /// <summary>What the emitter called compiling the corpus.</summary>
    private static List<MethodBase> CorpusCallees()
    {
        IlEmit.RecordCallees = true;
        try
        {
            var e = new PrologEngine();
            e.IlPromotion.Threshold = 1;
            e.IlPromotion.BackgroundCompilation = false;
            e.ConsultString(Corpus);
            for (int i = 0; i < 3; i++) Assert.True(e.Query("run.").Success);
            e.IlPromotion.WaitForPendingPromotions();
        }
        finally
        {
            IlEmit.RecordCallees = false;
        }
        return IlEmit.CalleesSeen.Keys.Where(Probed).OrderBy(IlInlineTable.Key, StringComparer.Ordinal).ToList();
    }

#if !NETFRAMEWORK && !DEBUG
    private static string Arch => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();

    /// <summary>The JIT's inlining decisions and load events, by the name
    /// of the method being compiled.</summary>
    private sealed class JitInlines : EventListener
    {
        private readonly Dictionary<string, List<(bool Made, string Inliner, string Inlinee, string Reason)>> _decisions = new();
        private readonly HashSet<string> _loaded = new();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)(0x10 | 0x1000));   // JIT, JitTracing
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName is null || e.PayloadNames is null || e.Payload is null) return;
            string S(string n) { int i = e.PayloadNames.IndexOf(n); return i < 0 ? "" : e.Payload[i]?.ToString() ?? ""; }
            if (e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal))
            {
                string mn = S("MethodName");
                if (mn.StartsWith("ShumwayIlProbe_", StringComparison.Ordinal)) lock (_loaded) _loaded.Add(mn);
                return;
            }
            bool made = e.EventName.StartsWith("MethodJitInliningSucceeded", StringComparison.Ordinal);
            if (!made && !e.EventName.StartsWith("MethodJitInliningFailed", StringComparison.Ordinal)) return;
            string root = S("MethodBeingCompiledName");
            if (!root.StartsWith("ShumwayIlProbe_", StringComparison.Ordinal)) return;
            var d = (made, S("InlinerName"), S("InlineeNamespace") + ":" + S("InlineeName"), made ? "" : S("FailReason"));
            lock (_decisions)
            {
                if (!_decisions.TryGetValue(root, out var l)) _decisions[root] = l = new();
                l.Add(d);
            }
        }

        /// <summary>The decisions for a probe once its load event is in
        /// (the decisions precede it); null when it never arrives.</summary>
        public List<(bool Made, string Inliner, string Inlinee, string Reason)>? Wait(string name)
        {
            for (int i = 0; i < 400; i++)
            {
                lock (_loaded) if (_loaded.Contains(name)) break;
                Thread.Sleep(25);
            }
            lock (_loaded) if (!_loaded.Contains(name)) return null;
            lock (_decisions) return _decisions.TryGetValue(name, out var l) ? new(l) : new();
        }
    }

    private static int _probeSeq;
    private const string Cached = "noinline per IL/cached result";

    /// <summary>A method with one call to <paramref name="m"/>, its
    /// arguments fresh locals (the activation the method's own), behind a
    /// branch the invocation takes so that nothing runs: the JIT compiles it
    /// whole and reports what it inlined. The inlines for the call: every
    /// success the JIT reported when it took the callee, none when it did not.</summary>
    private static (int Inlines, string Verdict) Probe(MethodBase m, JitInlines jit)
    {
        // The runtime delivers its events on a thread of its own and can
        // drop one: a probe whose load never shows is tried once more.
        var first = ProbeOnce(m, jit);
        return first ?? ProbeOnce(m, jit) ?? throw new Xunit.Sdk.XunitException($"the JIT reported no load of the probe for {IlInlineTable.Key(m)}");
    }

    private static (int Inlines, string Verdict)? ProbeOnce(MethodBase m, JitInlines jit)
    {
        string name = $"ShumwayIlProbe_{Interlocked.Increment(ref _probeSeq)}";
        var emit = IlEmit.NewDynamicMethod(name, record: false);
        emit.NoTailCalls = true;
        var skip = emit.DefineLabel("skip");
        emit.LoadArgument(1);
        emit.BranchIfTrue(skip);
        if (!m.IsStatic && m is not ConstructorInfo)
        {
            var dt = m.DeclaringType!;
            if (dt == typeof(Activation)) emit.LoadArgument(0);
            else if (dt.IsValueType) emit.LoadLocalAddress(emit.DeclareLocal(dt));
            else emit.LoadLocal(emit.DeclareLocal(dt));
        }
        foreach (var p in m.GetParameters())
        {
            var t = p.ParameterType;
            if (t.IsByRef) emit.LoadLocalAddress(emit.DeclareLocal(t.GetElementType()!));
            else if (t == typeof(Activation)) emit.LoadArgument(0);
            else emit.LoadLocal(emit.DeclareLocal(t));
        }
        if (m is ConstructorInfo c)
        {
            emit.NewObject(c);
            emit.Pop();
        }
        else
        {
            var mi = (MethodInfo)m;
            emit.Call(mi);
            if (mi.ReturnType != typeof(void)) emit.Pop();
        }
        emit.MarkLabel(skip);
        emit.LoadConstant(true);
        emit.Return();
        var d = emit.CreateDelegate(initLocals: true);
        Assert.True(d(null!, 1));   // compiled here; the call is not taken
        var decisions = jit.Wait(name);
        if (decisions is null) return null;
        // The callee's own verdict is the first decision the probe itself
        // made: a failure nested inside the callee is reported before it.
        // Every success, nested ones included, names the probe as inliner.
        int own = decisions.FindIndex(x => x.Inliner == name);
        if (own < 0) return (0, "no decision");
        var first = decisions[own];
        return first.Made ? (decisions.Count(x => x.Made), "inlined") : (0, first.Reason);
    }
#endif

    [Fact]
    public void TheTableNamesEveryCalleeAndMatchesTheJit()
    {
#if !NETFRAMEWORK && !DEBUG
        using var jit = new JitInlines();
        var measured = new Dictionary<MethodBase, (int Inlines, string Verdict)>();
        foreach (var m in FieldCallees()) measured[m] = Probe(m, jit);   // before any compilation: no verdict remembered yet
#endif
        var callees = new HashSet<MethodBase>(FieldCallees());
        foreach (var m in CorpusCallees()) callees.Add(m);
        var ordered = callees.OrderBy(IlInlineTable.Key, StringComparer.Ordinal).ToList();

        var unknown = ordered.Where(m => !IlInlineTable.Knows(m)).Select(IlInlineTable.Key).ToList();
        string? outPath = Environment.GetEnvironmentVariable("SHUMWAY_INLINE_TABLE_OUT");
        if (string.IsNullOrEmpty(outPath))
        {
            Assert.True(unknown.Count == 0,
                $"{unknown.Count} of the emitter's callees are not in the table (regenerate it with SHUMWAY_INLINE_TABLE_OUT): "
                + string.Join(", ", unknown.Take(12)));
            Assert.True(IlInlineTable.Count >= 100, $"table holds {IlInlineTable.Count} methods");
        }
#if !NETFRAMEWORK && !DEBUG
        foreach (var m in ordered)
            if (!measured.ContainsKey(m)) measured[m] = Probe(m, jit);
        if (!string.IsNullOrEmpty(outPath))
        {
            var sb = new StringBuilder();
            sb.Append("// Generated by IlInlineTableTests with SHUMWAY_INLINE_TABLE_OUT set: the\n");
            sb.Append("// inlines the JIT makes for a call to each method the emitter calls, from\n");
            sb.Append("// the JIT's own events over a probe method per callee. Do not edit by hand.\n");
            sb.Append($"// Measured with .NET {Environment.Version} on {Arch}.\n");
            sb.Append("using System.Collections.Generic;\n\nnamespace Shumway.Compiler.Il;\n\n");
            sb.Append("internal static partial class IlInlineTable\n{\n");
            sb.Append("    /// <summary>The major of the runtime the table was measured with.</summary>\n");
            sb.Append($"    public const int MeasuredMajor = {Environment.Version.Major};\n");
            sb.Append("    /// <summary>The architecture the table was measured on.</summary>\n");
            sb.Append($"    public const string MeasuredArchitecture = \"{Arch}\";\n");
            sb.Append("    private static readonly Dictionary<string, int> Table = new(System.StringComparer.Ordinal)\n    {\n");
            foreach (var m in ordered)
                sb.Append($"        [\"{IlInlineTable.Key(m)}\"] = {measured[m].Inlines},   // {measured[m].Verdict}\n");
            sb.Append("    };\n}\n");
            File.WriteAllText(outPath, sb.ToString());
            return;
        }
        // The table is the JIT's of one runtime on one architecture: another
        // major is measured again, and a difference on another architecture
        // says which it is.
        Assert.True(IlInlineTable.MeasuredMajor == Environment.Version.Major,
            $"the table was measured with .NET {IlInlineTable.MeasuredMajor} on {IlInlineTable.MeasuredArchitecture}; "
            + $"this is .NET {Environment.Version.Major} on {Arch}: regenerate it with SHUMWAY_INLINE_TABLE_OUT, this class alone");
        // A verdict the JIT remembered from an earlier compilation in this
        // process says nothing about the callee: only a fresh one counts.
        var wrong = new List<string>();
        foreach (var m in ordered)
        {
            var (n, verdict) = measured[m];
            if (verdict == Cached || IlInlineTable.Inlines(m) == n) continue;
            wrong.Add($"{IlInlineTable.Key(m)}: table {IlInlineTable.Inlines(m)}, JIT {n} ({verdict})");
        }
        Assert.True(wrong.Count == 0,
            $"{wrong.Count} of {ordered.Count} differ from the JIT of .NET {Environment.Version} on {Arch} "
            + $"(the table's: .NET {IlInlineTable.MeasuredMajor} on {IlInlineTable.MeasuredArchitecture}; regenerate with SHUMWAY_INLINE_TABLE_OUT, this class alone): "
            + string.Join("; ", wrong.Take(15)));
        Assert.Contains(measured.Values, x => x.Inlines >= 3);   // a helper whose inlines nest: the measure sees past the first level
#endif
    }
}
