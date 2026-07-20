using System;
using System.Collections.Generic;
using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding.Debugging;

/// <summary>
/// A four-port tracer (ADR-035, phase D1) — the first consumer of the
/// <see cref="IDebugSession"/> seam, and the one that validates it with no
/// debugger in the loop. Attached by <c>trace/0</c>, it prints a line per
/// port in the classic Prolog form:
///
/// <code>
///    Call: (2) append([1,2], [3], _G7)
///    Exit: (2) append([1,2], [3], [1,2,3])
///    Redo: (1) member(X, [a,b])
///    Fail: (3) foo(zzz)
/// </code>
///
/// <para><b>The goal stack.</b> The tracer keeps its own stack of active goals
/// rather than walking the machine's env chain, because the two answer different
/// questions: the env chain says which frames exist, the port trace says which
/// goals are logically open. Two machine details make the mapping exact:</para>
/// <list type="bullet">
/// <item>A <c>tailCall</c> replaces the top entry instead of nesting under it —
/// which is what last-call optimisation does to the frame, so the printed depth
/// tracks the real stack rather than the source nesting.</item>
/// <item>Every entry records the value of <c>B</c> at the moment its goal was
/// called. On backtracking, the goals that just died are exactly those whose
/// recorded <c>B</c> is at or above the choice point being resumed — so the
/// tracer reconstructs the failed prefix from machine state, and never has to
/// guess.</item>
/// </list>
///
/// <para><b>Argument rendering.</b> At the call port the goal's argument
/// <em>cells</em> are copied to a small heap block. Copying cells (not
/// materialized terms) keeps the sharing: an argument that is an unbound
/// variable is copied as a reference to that same variable, so re-reading the
/// block at the exit port shows what the goal actually bound. The block is
/// registered as a GC root via <see cref="IDebugSession.MarkHeapRoots"/> /
/// <see cref="IDebugSession.RelocateHeapRoots"/>, so a collection mid-trace
/// keeps it alive and rewrites its index.</para>
/// </summary>
public sealed class DebugTracer : IDebugSession
{
    private readonly PrologEngine _owner;
    private readonly TextWriter _out;
    private readonly List<Entry> _stack = new();

    /// <summary>Goals whose name starts with <c>$</c> are engine-internal
    /// (the lowered control constructs, tabling plumbing, the query wrapper); a
    /// trace that showed them would bury the user's program. The test is on the
    /// DEMANGLED name — a module-local helper reaches us as <c>user$$disj_1</c>,
    /// and it is the part after the module prefix that says what it is.</summary>
    private static bool IsInternal(string demangled) =>
        demangled.Length > 0 && (demangled[0] == '$' || demangled == "__query__");

    private struct Entry
    {
        public string Name;
        public int Arity;
        public int ArgBase;     // heap index of the copied argument cells (-1: none)
        public int B;           // engine.B when the goal was called
        public int Depth;       // printed depth: parent's depth + 1
        public bool Exited;     // has succeeded — still redoable if it left a CP
    }

    public DebugTracer(PrologEngine owner, TextWriter output)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    // ----- ports -----

    void IDebugSession.OnCallAddress(Activation engine, int address, bool tailCall)
    {
        var pred = _owner.LookupPredicateByAddress(address);
        if (pred is null) return;
        Push(engine, pred.Value.Name, pred.Value.Arity, tailCall);
    }

    void IDebugSession.OnCallFunctor(Activation engine, int functorId, bool tailCall)
    {
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        string name = AtomTable.GetById(atomId)?.Name ?? "?";
        Push(engine, name, arity, tailCall);
    }

    void IDebugSession.OnCallBuiltin(Activation engine, int builtinId, bool tailCall)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        Push(engine, entry.Name, entry.Arity, tailCall);
    }

    void IDebugSession.OnBuiltinResult(Activation engine, int builtinId, bool succeeded)
    {
        // A builtin's result is known the moment its dispatch returns, so its
        // port follows immediately — but a backtrackable one (between/3,
        // clause/2, …) leaves a choice point, and Exited keeps it redoable.
        if (succeeded) Exit(engine);
        else Fail(engine);
    }

    void IDebugSession.OnExit(Activation engine) => Exit(engine);

    /// <summary>The tracer prints ports, not source lines, and it stops for
    /// nobody — a stop site is not an event it has anything to say about. The
    /// debug session that drives Visual Studio is the one that acts on these.</summary>
    void IDebugSession.OnBreak(Activation engine, int siteId) { }

    void IDebugSession.OnRedo(Activation engine, int retryPc)
    {
        // Every goal called after the choice point now at engine.B was pushed
        // is dead: its recorded B is at or above it, and the CP that could have
        // revived it is below. Report those top-down; what remains on top is
        // the goal that owns the choice point, which is the one being retried.
        int b = engine.B;
        while (_stack.Count > 0 && _stack[^1].B >= b)
            Pop("Fail", engine);

        if (_stack.Count == 0) return;
        var e = _stack[^1];
        // Only a goal that had already succeeded is being REDONE. If the goal
        // on top is still running, this backtrack is the machine picking the
        // next clause after a head-unification failure — internal to the call,
        // and not a port: the goal has neither succeeded nor failed yet.
        if (!e.Exited) return;
        Report(engine, "Redo", e, e.Depth);
        e.Exited = false;               // it is running again
        _stack[^1] = e;
    }

    void IDebugSession.OnFail(Activation engine)
    {
        while (_stack.Count > 0)
            Pop("Fail", engine);
    }

    /// <summary>The exit port: the goal that just succeeded is the deepest one
    /// still running.</summary>
    private void Exit(Activation engine)
    {
        int i = TopRunning();
        if (i < 0) return;
        var e = _stack[i];
        Report(engine, "Exit", e, e.Depth);
        e.Exited = true;
        _stack[i] = e;

        // Prune what can never be redone. An exited goal whose call-time B is
        // at or above the machine's current B left no choice point behind — it
        // is deterministic, and keeping it would grow the stack without bound
        // through a long deterministic conjunction. Everything above it was
        // called later, so it is deterministic too.
        while (_stack.Count > 0 && _stack[^1].Exited && _stack[^1].B >= engine.B)
            _stack.RemoveAt(_stack.Count - 1);
    }

    /// <summary>The fail port of a single goal (a builtin that returned false):
    /// same target as <see cref="Exit"/>, opposite outcome.</summary>
    private void Fail(Activation engine)
    {
        int i = TopRunning();
        if (i < 0) return;
        // Goals above a failing one can only be exited leftovers of its own
        // subcomputation; they die with it.
        while (_stack.Count > i + 1) _stack.RemoveAt(_stack.Count - 1);
        Pop("Fail", engine);
    }

    /// <summary>Index of the deepest goal that has not yet exited — the one the
    /// machine is currently inside.</summary>
    private int TopRunning()
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
            if (!_stack[i].Exited) return i;
        return -1;
    }

    private void Pop(string port, Activation engine)
    {
        Report(engine, port, _stack[^1], _stack[^1].Depth);
        _stack.RemoveAt(_stack.Count - 1);
    }

    // ----- GC roots (the copied argument blocks) -----

    void IDebugSession.MarkHeapRoots(Action<int> markCell)
    {
        foreach (var e in _stack)
        {
            if (e.ArgBase < 0) continue;
            for (int i = 0; i < e.Arity; i++)
                markCell(e.ArgBase + i);
        }
    }

    void IDebugSession.RelocateHeapRoots(
        Activation engine, Func<int, int> relocIndex, Func<int, int> relocBoundary)
    {
        for (int i = 0; i < _stack.Count; i++)
        {
            var e = _stack[i];
            if (e.ArgBase < 0) continue;
            e.ArgBase = relocIndex(e.ArgBase);
            _stack[i] = e;
        }
    }

    // ----- internals -----

    private void Push(Activation engine, string name, int arity, bool tailCall)
    {
        name = PrologEngine.DemangleLocalName(name);
        if (IsInternal(name)) return;

        // The caller is the deepest goal still running; a tail call, though,
        // reuses the caller's frame and so takes the caller's own place —
        // its depth, not one below it. That is what keeps a tail-recursive
        // predicate's trace flat instead of marching one column right per
        // iteration.
        int parent = TopRunning();
        int depth = parent < 0 ? 1
            : tailCall ? _stack[parent].Depth
            : _stack[parent].Depth + 1;

        var entry = new Entry
        {
            Name = name,
            Arity = arity,
            B = engine.B,
            Depth = depth,
            ArgBase = -1,
        };
        if (arity > 0)
        {
            // Copy the argument CELLS (not materialized terms) so an unbound
            // argument stays shared with the caller's variable and the exit
            // port sees what the goal bound.
            int b = engine.AllocateHeap(arity);
            for (int i = 0; i < arity; i++)
                engine.SetHeap(b + i, engine.GetRegister(i));
            entry.ArgBase = b;
        }

        // The displaced caller of a tail call has no exit port of its own left
        // to report — the callee returns straight to ITS caller — but it may
        // still own choice points, so it stays on the stack (marked exited) to
        // be found again on a redo, and is pruned like any other exited goal
        // once the machine shows it is deterministic.
        if (tailCall && parent >= 0)
        {
            var c = _stack[parent];
            c.Exited = true;
            _stack[parent] = c;
            while (_stack.Count > parent && _stack[^1].Exited && _stack[^1].B >= engine.B)
                _stack.RemoveAt(_stack.Count - 1);
        }
        _stack.Add(entry);

        Report(engine, "Call", entry, depth);
    }

    private void Report(Activation engine, string port, in Entry e, int depth)
    {
        _out.WriteLine($"   {port}: ({depth}) {RenderGoal(engine, e)}");
    }

    private string RenderGoal(Activation engine, in Entry e)
    {
        if (e.Arity == 0 || e.ArgBase < 0)
            return e.Name;

        var args = new Term[e.Arity];
        for (int i = 0; i < e.Arity; i++)
            args[i] = TermReader.Materialize(engine, e.ArgBase + i);
        return AstTermRenderer.Render(new CompoundTerm(e.Name, args), 999, _owner.Operators);
    }
}
