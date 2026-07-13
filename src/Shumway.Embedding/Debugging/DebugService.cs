using System;
using System.Collections.Generic;
using Shumway.Core;

namespace Shumway.Embedding.Debugging;

// StopReason lives in DebugWire.cs — the debugger compiles that file too, and a
// debugger and a debuggee that disagree about an enum's values do not fail loudly,
// they show the user a plausible, wrong stack.

/// <summary>What the debugger wants next. The handler sets this before returning
/// from a stop; execution resumes accordingly.</summary>
public enum StepMode
{
    /// <summary>Run until the next breakpoint.</summary>
    Continue,
    /// <summary>Stop at the very next port, however deep — step into.</summary>
    Into,
    /// <summary>Stop at the next port no deeper than the one we stopped at — step
    /// over: the goals a called predicate runs inside itself are skipped, but its
    /// own exit or fail is not.</summary>
    Over,
    /// <summary>Stop at the next port shallower than the one we stopped at — step
    /// out of the current predicate.</summary>
    Out,
}

/// <summary>Everything a debugger needs to know about one stop. Each frame carries its
/// own variables, so <c>Frames[0].Variables</c> is what a Locals window shows.</summary>
public sealed record DebugStopEvent(
    StopReason Reason,
    string Goal,
    string File,
    int Line,
    int Depth,
    IReadOnlyList<PrologEngine.DebugFrame> Frames)
{
    /// <summary>ADR-035 — the breakpoint that fired, as the USER set it. Not the same
    /// question as <see cref="File"/>/<see cref="Line"/>, which say where the machine is:
    /// a breakpoint on a rule's head binds at its first goal, so the two differ by design,
    /// and a debugger matching a hit to the red dot it drew needs this one. Empty for
    /// every stop that is not a breakpoint.</summary>
    public string BreakFile { get; init; } = "";
    public int BreakLine { get; init; }

    /// <summary>The variables of the clause we are stopped in.</summary>
    public IReadOnlyList<(string Name, string Value)> Variables =>
        Frames.Count > 0 ? Frames[0].Variables : Array.Empty<(string, string)>();
}

/// <summary>
/// ADR-035 — the engine-side debug session: breakpoints, port-based stepping, and
/// the call stack. Cross-platform, with no debugger of any kind in the picture; the
/// Concord components of phases D2/D3 drive exactly this through the channel, and
/// the tests drive it directly.
///
/// <para><b>Why the ports and not source lines.</b> A Prolog goal has four ways in
/// and out — call, exit, redo, fail — and the last two cannot be expressed by
/// stepping over return addresses, which is all a conventional frame-based debugger
/// knows how to do. So a step here is "run until the next port that satisfies the
/// step's condition", and the conditions are stated in terms of the machine's
/// logical call depth.</para>
///
/// <para><b>Why depth is recomputed, never counted.</b> It is read from the
/// environment chain at every port. Counting calls and returns would drift the
/// moment anything changed the depth without going through a port — last-call
/// optimisation reusing a frame, a cut discarding choice points, or a
/// <c>:- disable_debug.</c> predicate running goals that report nothing at all.
/// Reading the chain cannot drift, because the chain IS the depth.</para>
/// </summary>
public sealed class DebugService : IDebugSession
{
    private readonly PrologEngine _engine;
    private readonly Action<DebugService, DebugStopEvent> _onStop;

    private StepMode _mode = StepMode.Continue;
    private int _stepDepth;

    // The callee named by the last call port. Only a call port knows the name of the
    // goal it is about to run; every other stop reads it back off the frame it is in.
    //
    // Held as the ID the port reported, NOT as a name: naming it means a demangle and a
    // "name/arity" — two allocations per goal, for a string almost every port throws away.
    // A stop is rare; a port is not. Resolve at the stop.
    private GoalKind _goalKind;
    private int _goalId;

    private enum GoalKind : byte { None, Address, Functor, Builtin }

    /// <summary>The goal the last call port reported, named. Only called at a stop.</summary>
    private string CurrentGoal()
    {
        switch (_goalKind)
        {
            case GoalKind.Address:
                var pred = _engine.LookupPredicateByAddress(_goalId);
                return pred is null
                    ? ""
                    : $"{PrologEngine.DemangleLocalName(pred.Value.Name)}/{pred.Value.Arity}";
            case GoalKind.Functor:
                return FunctorGoalName(_goalId) ?? "";
            case GoalKind.Builtin:
                var entry = Shumway.Builtins.BuiltinsRegistry.GetById(_goalId);
                return $"{entry.Name}/{entry.Arity}";
            default:
                return "";
        }
    }

    // Whether a functor is one of the engine's own helpers ($-prefixed once demangled),
    // and what it is called if it is not — decided ONCE per functor. Functor ids are
    // stable for the life of the process, and the answer is a property of the name, so
    // the second call port on the same predicate costs a dictionary probe and nothing
    // else. (Null = internal: raise no port for it.)
    private readonly Dictionary<int, string?> _functorGoalNames = new();

    private string? FunctorGoalName(int functorId)
    {
        if (_functorGoalNames.TryGetValue(functorId, out string? cached)) return cached;

        var (atomId, arity) = FunctorTable.Lookup(functorId);
        string name = PrologEngine.DemangleLocalName(AtomTable.GetById(atomId)?.Name ?? "?");
        string? goal = IsInternal(name) ? null : $"{name}/{arity}";
        _functorGoalNames[functorId] = goal;
        return goal;
    }

    // ADR-035 — the site of a breakpoint we just stopped at, if the code there is
    // about to CALL something. The call port that follows is the same event as the
    // breakpoint, not a second one, and reporting it again would stop the user twice
    // on one line. Cleared by the next call port, whether or not it matched.
    private int _reportedCallSite = -1;

    public DebugService(PrologEngine engine, Action<DebugService, DebugStopEvent> onStop)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _onStop = onStop ?? throw new ArgumentNullException(nameof(onStop));
    }

    /// <summary>Set from inside the stop handler to say what should happen next.
    /// Defaults to <see cref="StepMode.Continue"/> at every stop, so a handler that
    /// says nothing lets the program run on.</summary>
    public void Resume(StepMode mode)
    {
        _mode = mode;
        _stepDepth = _lastStopDepth;
    }

    /// <summary>The machine this session is watching — set at every port, and left set
    /// between them.
    ///
    /// <para>It has to survive between ports for the asynchronous break: when the user
    /// hits Break All, the engine is not at a port and never will be until it reaches
    /// the next goal, but the debugger wants the stack NOW. It stops the process from
    /// outside and asks (<see cref="CaptureNow"/>), and the answer can only come from
    /// the machine that was last running.</para></summary>
    public Activation? Current { get; private set; }

    /// <summary>ADR-035 — the stack as it stands, right now, at no port at all.
    ///
    /// <para>A Break All lands wherever the machine happened to be — mid-head-unification,
    /// inside a builtin, anywhere. There is no port, so nothing has been reported and the
    /// channel holds whatever the last stop left, which would be a lie. This builds the
    /// truth on demand: the same frames, from the same environment chain, at the pc the
    /// machine is standing on. Safe to call precisely because the process is stopped —
    /// the activation is not running while the debugger is looking at it.</para>
    ///
    /// <para>Returns null if nothing is running (between queries).</para></summary>
    public DebugStopEvent? CaptureNow()
    {
        Activation? engine = Current;
        if (engine is null) return null;

        var frames = _engine.CaptureFrames(engine);
        string goal = frames.Count > 0 ? $"{frames[0].Name}/{frames[0].Arity}" : CurrentGoal();
        var site = SiteOf(engine.P);
        return new DebugStopEvent(
            StopReason.AsyncBreak, goal, site.File, site.Line, PortDepth(engine), frames);
    }

    /// <summary>Turns last-call optimisation on or off for the query ALREADY RUNNING —
    /// which is what a debugger stopped inside one wants, and what
    /// <c>debug_lastcall</c> being an opcode that reads a flag rather than a compiled-in
    /// decision is for. Frames LCO already reclaimed do not come back, but from the next
    /// call on, the stack is whole.</summary>
    public void SetLastCallOptimisation(bool on)
    {
        if (Current is not null) Current.LastCallOptimisation = on;
    }

    private int _lastStopDepth;

    // ----- commands from a debugger, while the program is running -----

    /// <summary>Called every <see cref="PollInterval"/> ports, so a debugger can arm a
    /// breakpoint on a program that is already running. Everything else it might say
    /// (step, continue) is said at a stop, where the engine reads the channel anyway; a
    /// breakpoint is the one thing that has to arrive mid-flight.</summary>
    public Action? Poll { get; set; }

    /// <summary>Ports between polls. Small enough that F9 feels immediate on any real
    /// program, large enough that the check is lost in the noise of a port that is
    /// already walking an environment chain.</summary>
    public const int PollInterval = 512;

    private int _pollTick;

    // ----- ports -----

    void IDebugSession.OnBreak(Activation engine, int pc)
    {
        Current = engine;
        // A breakpoint always stops, whatever the step mode: it is the one thing the
        // user asked for by name.
        //
        // The stop says where the machine IS, as every stop does. It ALSO says which
        // breakpoint fired, as the user set it — a different question with a different
        // answer, since a breakpoint on a rule's head binds at its first goal, and a
        // debugger has to match the hit to the red dot it drew.
        _breakRequest = _engine.BreakpointRequestAt(pc);
        Stop(engine, StopReason.Breakpoint, PortDepth(engine), SiteOf(pc), goal: null);
        _breakRequest = null;
        _reportedCallSite = _engine.SiteAtOrBefore(pc);
    }

    void IDebugSession.OnCallAddress(Activation engine, int address, bool tailCall)
    {
        // A dictionary probe, and no port for an address that names no predicate.
        if (_engine.LookupPredicateByAddress(address) is null) return;
        _goalKind = GoalKind.Address;
        _goalId = address;
        OnCall(engine);
    }

    void IDebugSession.OnCallFunctor(Activation engine, int functorId, bool tailCall)
    {
        if (FunctorGoalName(functorId) is null) return;   // an engine helper: not a goal
        _goalKind = GoalKind.Functor;
        _goalId = functorId;
        OnCall(engine);
    }

    void IDebugSession.OnCallBuiltin(Activation engine, int builtinId, bool tailCall)
    {
        // Builtins are not stepped into — there is no Prolog inside them to show, and
        // they run to completion within the one dispatch. Recording the name is still
        // worth it: it is what a stop immediately after them should be blamed on.
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        if (IsInternal(entry.Name)) return;
        _goalKind = GoalKind.Builtin;
        _goalId = builtinId;
    }

    private void OnCall(Activation engine)
    {
        // The breakpoint we just reported was ON this call. Do not report it twice.
        // Only worth asking right after a breakpoint stop — the rest of the time there is
        // nothing to be equal to, and this is a binary search over every stop site.
        if (_reportedCallSite >= 0)
        {
            int site = _engine.SiteAtOrBefore(engine.P);
            bool alreadyReported = site == _reportedCallSite;
            _reportedCallSite = -1;
            if (alreadyReported) return;
        }

        MaybeStopAtPort(engine, StopReason.Call);
    }

    void IDebugSession.OnBuiltinResult(Activation engine, int builtinId, bool succeeded) { }

    void IDebugSession.OnExit(Activation engine)
        => MaybeStopAtPort(engine, StopReason.Exit);

    void IDebugSession.OnRedo(Activation engine, int retryPc)
    {
        Current = engine;
        if (_mode == StepMode.Continue) return;

        // The machine is standing in the computation that just FAILED — P, the
        // environment chain and Cp all still describe it. None of that is what the
        // user needs to see: the redo port is about the goal being retried, which the
        // choice point describes and the retry address points into.
        int depth = engine.PendingRedoEnvDepth + 1;
        bool stop = _mode switch
        {
            StepMode.Into => true,
            StepMode.Over => depth <= _stepDepth,
            StepMode.Out => depth < _stepDepth,
            _ => false,
        };
        if (!stop) return;

        var (e, cp) = engine.TopChoicePointContext;
        // -1 is a backtrackable builtin re-satisfying (between/3, repeat/0, clause/2):
        // there is no bytecode clause to point at, so the goal we are in stands.
        int pc = retryPc >= 0 ? _engine.RetryClauseSite(retryPc) : engine.P;
        var pred = _engine.PredicateContaining(pc);

        _mode = StepMode.Continue;
        _lastStopDepth = depth;
        var site = SiteOf(pc);
        _onStop(this, new DebugStopEvent(
            StopReason.Redo,
            pred is null ? CurrentGoal() : $"{pred.Value.Name}/{pred.Value.Arity}",
            site.File, site.Line, depth,
            _engine.CaptureFrames(engine, pc, e, cp)));
    }

    void IDebugSession.OnFail(Activation engine)
        => MaybeStopAtPort(engine, StopReason.Fail);

    void IDebugSession.MarkHeapRoots(Action<int> markCell) { }
    void IDebugSession.RelocateHeapRoots(Func<int, int> relocIndex) { }

    // ----- depth -----

    /// <summary>The logical call depth of the goal a port is about to run, or has just
    /// finished running.
    ///
    /// <para>It is <c>EnvDepth + 1</c> at every port, and that is not a fudge — it is
    /// the same fact seen from either end. At a call port the callee has not allocated
    /// its frame yet, so it sits one below the live chain. At an exit port the frame is
    /// already gone (the fused epilogues deallocate before the port fires; a fact never
    /// had one), so the goal that just exited sits one below what remains. Last-call
    /// optimisation falls out for free: it reclaims the caller's frame *before* the
    /// call, so the callee reads the caller's own depth — which is exactly right, since
    /// it has taken the caller's place.</para></summary>
    private static int PortDepth(Activation engine) => engine.EnvDepth + 1;

    // ----- the step condition -----

    private void MaybeStopAtPort(Activation engine, StopReason reason)
    {
        // Every port, whether we stop at it or not: this is how an asynchronous break
        // later knows which machine to ask. One field store per goal.
        Current = engine;

        // A breakpoint can be set on a program that is already RUNNING — F9 during a long
        // query is the ordinary case, not an exotic one — and the engine only ever looks
        // at the channel when it stops. So it looks here too, between goals, rarely
        // enough to cost nothing and often enough that the user does not notice the wait.
        if (Poll is not null && ++_pollTick >= PollInterval)
        {
            _pollTick = 0;
            Poll();
        }

        // Nobody is stepping, so nothing below this line can change what happens next —
        // and this is where a port must cost nothing. The depth in particular: it is
        // read by WALKING the environment chain, which under a debugger (LCO off, every
        // frame retained) is as long as the recursion is deep. Computing it at every
        // port of a running program made the cost of running one QUADRATIC in its call
        // depth: a 300k-deep tail recursion never came back. Ask only when the answer
        // is used.
        if (_mode == StepMode.Continue) return;

        int depth = PortDepth(engine);
        bool stop = _mode switch
        {
            // Into: the next port, however deep.
            StepMode.Into => true,
            // Over: the next port no deeper than the goal we were on — its own exit or
            // fail included. Everything the goal does *inside* itself is deeper, and is
            // skipped. (In a port model there is no depth that separates "this goal
            // exited" from "the next goal is called": they are siblings. So a step over
            // lands on the exit port of the goal stepped over, as it does in SWI.)
            StepMode.Over => depth <= _stepDepth,
            // Out: shallower than the goal we were on — i.e. out of it entirely.
            StepMode.Out => depth < _stepDepth,
            _ => false,
        };
        if (!stop) return;

        // Only a call port knows the goal it is about to run; the others are read back
        // off the frame the machine is stopped in.
        Stop(engine, reason, depth, SiteOf(engine.P),
            goal: reason == StopReason.Call ? CurrentGoal() : null);
    }

    /// <param name="goal">The goal to name, when the port knows it (only a call port
    /// does). Otherwise null: read it back off the frame we are stopped in, which is
    /// the predicate containing <c>P</c> — the one that is exiting, retrying, or has
    /// hit a breakpoint.</param>
    private void Stop(
        Activation engine, StopReason reason, int depth, (string File, int Line) site, string? goal)
    {
        _mode = StepMode.Continue;   // a handler that says nothing lets it run on
        _lastStopDepth = depth;
        Current = engine;

        var frames = _engine.CaptureFrames(engine);
        goal ??= frames.Count > 0 ? $"{frames[0].Name}/{frames[0].Arity}" : "";

        _onStop(this, new DebugStopEvent(reason, goal, site.File, site.Line, depth, frames)
        {
            BreakFile = _breakRequest?.File ?? "",
            BreakLine = _breakRequest?.Line ?? 0,
        });
    }

    // The breakpoint being reported, for the length of one stop. See OnBreak.
    private (string File, int Line)? _breakRequest;

    private (string File, int Line) SiteOf(int pc)
    {
        int siteId = _engine.SiteAtOrBefore(pc);
        if (siteId < 0) return ("", 0);
        var site = DebugSiteTable.Get(siteId);
        return (DebugSiteTable.FileName(site.FileId), site.Line);
    }

    private static bool IsInternal(string name) =>
        name.Length > 0 && (name[0] == '$' || name == "__query__");
}
