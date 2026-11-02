using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The stop sites of an included file's clauses name that file: a
/// breakpoint drawn in it stops there, and one drawn on the same line number
/// of the including file stops only in that file's own code. The sites had no
/// file of their own: a consult left them in the engine's default file, and a
/// compiled object put them in the including file.</summary>
[Collection("debugger")]
public sealed class IncludeDebugSiteTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_incdbg_" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _objRoot;

    public IncludeDebugSiteTests()
    {
        Directory.CreateDirectory(_dir);
        _root = Path.Combine(_dir, "dbg_inc_root.pl");
        File.WriteAllText(_root,
            ":- set_prolog_flag(compile_mode, debug).\n" +   // 1
            ":- include(dbg_inc_part).\n" +                   // 2
            "m(X) :- s(X).\n");                               // 3
        _objRoot = Path.Combine(_dir, "dbg_inc_obj.pl");
        File.WriteAllText(_objRoot,
            ":- include(dbg_inc_part).\n" +                   // 1
            "m(X) :- s(X).\n");                               // 2
        File.WriteAllText(Path.Combine(_dir, "dbg_inc_part.pl"),
            "% included\n" +                                  // 1
            "s(a).\n" +                                       // 2
            "s(b).\n");                                       // 3
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Where(int siteId)
    {
        var site = DebugSiteTable.Get(siteId);
        return $"{Path.GetFileName(DebugSiteTable.FileName(site.FileId))}:{site.Line}";
    }

    /// <summary>Records every breakpoint hit as file:line.</summary>
    private sealed class Recorder : IDebugSession
    {
        // net48 has no default interface implementations.
        void IDebugSession.OnInlineGoal(Activation engine) { }
        void IDebugSession.OnLeaveProlog(Activation engine) { }

        private readonly PrologEngine _engine;
        public readonly List<string> Stops = new();

        public Recorder(PrologEngine engine) => _engine = engine;

        public void OnBreak(Activation engine, int pc) => Stops.Add(Where(_engine.SiteAt(pc)));

        public void OnCallAddress(Activation e, int a, bool t) { }
        public void OnCallFunctor(Activation e, int f, bool t) { }
        public void OnCallBuiltin(Activation e, int b, bool t) { }
        public void OnBuiltinResult(Activation e, int b, bool ok) { }
        public void OnExit(Activation e) { }
        public void OnRedo(Activation e, int pc) { }
        public void OnFail(Activation e) { }
        public void MarkHeapRoots(Action<int> mark) { }
        public void RelocateHeapRoots(
            Activation engine, Func<int, int> reloc, Func<int, int> relocBoundary) { }
    }

    private static List<string> StopsFor(PrologEngine engine, string goal)
    {
        var rec = new Recorder(engine);
        engine.AttachDebugSession(rec);
        Assert.NotEmpty(engine.QueryAll(goal).ToList());
        engine.AttachDebugSession(null);
        return rec.Stops;
    }

    [Fact]
    public void ABreakpointInAnIncludedFileStopsThere()
    {
        var engine = new PrologEngine();
        engine.ConsultFile(_root);
        Assert.True(engine.AddBreakpoint("dbg_inc_part.pl", 3) > 0);
        Assert.Equal(new[] { "dbg_inc_part.pl:3" }, StopsFor(engine, "s(X)."));
    }

    [Fact]
    public void ABreakpointInTheIncludingFileStopsOnlyInItsOwnCode()
    {
        var engine = new PrologEngine();
        engine.ConsultFile(_root);
        Assert.True(engine.AddBreakpoint("dbg_inc_root.pl", 3) > 0);
        // s(b) is on line 3 of the included file, not of this one.
        Assert.Empty(StopsFor(engine, "s(X)."));
        var stops = StopsFor(engine, "m(X).");
        Assert.NotEmpty(stops);
        Assert.All(stops, s => Assert.Equal("dbg_inc_root.pl:3", s));
    }

    [Fact]
    public void ACompiledObjectKeepsTheIncludedFileOfItsStops()
    {
        var obj = ShmoCompiler.CompileFile(_objRoot, ShmoBuildMode.Debuggable);
        var mod = CompiledModuleCodec.Decode(obj.Bytecode);
        // Both are local to the file's module, so their names come mangled.
        string StopsOf(string name, int arity) => string.Join(" ", mod.Predicates
            .Single(p => AtomTable.GetById(FunctorTable.Lookup(p.FunctorId).Item1)!.Name
                    == "dbg_inc_obj$" + name
                && p.Arity == arity)
            .DebugStops.Select(s => Where(s.SiteId)).Distinct());
        Assert.Equal("dbg_inc_part.pl:2 dbg_inc_part.pl:3", StopsOf("s", 1));
        Assert.Equal("dbg_inc_obj.pl:2", StopsOf("m", 1));
    }
}
