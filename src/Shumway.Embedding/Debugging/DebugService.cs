using System;
using System.Collections.Generic;
using Shumway.Compiler.Ast;
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

    /// <summary>ADR-035 D5 — why a CONDITIONAL breakpoint stopped even though its condition
    /// did not succeed: the condition could not run (a syntax error, an exception, a
    /// timeout). Empty for every ordinary stop — including a conditional breakpoint whose
    /// condition simply held. A debugger shows this to the user; the alternative was a
    /// broken condition silently swallowing its breakpoint, which is undiagnosable.</summary>
    public string ConditionError { get; init; } = "";

    /// <summary>ADR-035 D5+ — the source lines Set Next Statement would ACCEPT at this stop
    /// (see <see cref="DebugService.ValidSetNextLines"/>). Carried in the snapshot so the
    /// debugger can validate Ctrl+Shift+F10 synchronously — it cannot func-eval to ask.</summary>
    public IReadOnlyList<int> SetNextLines { get; init; } = Array.Empty<int>();

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
                if (pred is null) return "";
                var (aName, aArity) = PrologEngine.DebugConstructName(
                    PrologEngine.DemangleLocalName(pred.Value.Name), pred.Value.Arity);
                return $"{aName}/{aArity}";
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
        // Stepping or continuing the real program abandons any parked Immediate-window
        // evaluation — it restores the suspended query's depth/tables first, so the step below
        // is measured from the stop the user is actually leaving.
        AbandonPendingEvaluation();
        _mode = mode;
        _stepDepth = _lastStopDepth;
        _stepFromRedo = _lastStopWasRedo;
    }

    /// <summary>Whether the stop this step is being taken FROM was a redo port. It changes
    /// Step Out by one: a redo port reports the retried goal's CALL depth (a retried clause
    /// does not deepen the environment chain — see <see cref="IDebugSession.OnRedo"/>), which
    /// is one shallower than the goal's body. So the goal's continuation — the next sibling,
    /// where "out of this goal" lands — sits at the SAME depth as the redo, not below it. Step
    /// Out from a redo therefore stops at <c>depth &lt;= _stepDepth</c>, where a call/breakpoint
    /// stop (whose depth IS the goal's own) uses the strict <c>depth &lt; _stepDepth</c>. Without
    /// this, stepping out of a goal shown at a redo port skips its whole continuation and runs
    /// on out of the ENCLOSING clause — "runs the whole program" from inside a backtracking
    /// predicate.</summary>
    private bool _stepFromRedo;

    /// <summary>Whether the last reported stop was a redo port. Read by <see cref="Resume"/>
    /// into <see cref="_stepFromRedo"/>.</summary>
    private bool _lastStopWasRedo;

    // ----- stop at the entry point (--debug-wait) -----

    /// <summary>ADR-035 — when the very first goal of the very first query should stop the
    /// debugger, at the program's entry. Set once a <c>--debug-wait</c> launch has seen a
    /// debugger attach; cleared the moment it fires.</summary>
    private bool _breakAtEntry;

    /// <summary>What to do at the entry port. Wired by <see cref="ChannelDebugSession"/> to
    /// its <c>BreakHere</c> — the <c>debugger_break/0</c> mechanism (a managed
    /// <c>Debugger.Break()</c>), which is the one stop VS honours WITHOUT a step or an async
    /// break already pending, and so the only one that can land unbidden at startup. Left as
    /// a callback so a test can observe the entry port without a real debugger to break
    /// into.</summary>
    internal Action<Activation>? EntryBreak { get; set; }

    /// <summary>ADR-035 — arm "stop at the entry point". The next debuggable call port —
    /// the first goal the program runs — fires <see cref="EntryBreak"/> instead of running
    /// on. A no-op if no <see cref="EntryBreak"/> is wired.</summary>
    public void ArmEntryBreak() => _breakAtEntry = true;

    /// <summary>The machine this session is watching — set at every port, and left set
    /// between them.
    ///
    /// <para>It has to survive between ports for the asynchronous break: when the user
    /// hits Break All, the engine is not at a port and never will be until it reaches
    /// the next goal, but the debugger wants the stack NOW. It stops the process from
    /// outside and asks (<see cref="CaptureNow"/>), and the answer can only come from
    /// the machine that was last running.</para></summary>
    public Activation? Current { get; internal set; }

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
            StopReason.AsyncBreak, goal, site.File, site.Line, PortDepth(engine), frames)
        {
            SetNextLines = ValidSetNextLines(),   // ADR-035 D5+
        };
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

    /// <summary>ADR-035 — record a stop that did not come through <see cref="Stop"/>:
    /// <c>debugger_break/0</c>, which stops the debugger by asking the runtime rather than by
    /// tripping our own breakpoint. The DEPTH is the part that matters. A step over or out is
    /// measured against the depth of the goal you stepped FROM, and without this it would be
    /// measured against whatever the last real stop left behind — so F10 from a
    /// <c>debugger_break</c> would run to somewhere arbitrary.</summary>
    internal void NoteStop(int depth)
    {
        _mode = StepMode.Continue;   // a handler that says nothing lets it run on
        _lastStopDepth = depth;
        _lastStopWasRedo = false;
    }

    // ----- the Immediate window: evaluate a goal against the live engine -----

    /// <summary>An evaluation is in flight OR parked: the goal typed in the Immediate window
    /// owns the engine's per-query tables — either running right now, or suspended between
    /// solutions waiting for the user to ask for the next with <c>;</c>.</summary>
    private bool _evalActive;

    internal bool EvaluationInFlight => _evalActive;
    private Activation? _evalOuter;
    private IReadOnlyList<PrologEngine.DebugFrame> _evalOuterFrames
        = Array.Empty<PrologEngine.DebugFrame>();
    private string _evalGoalText = "";
    private Action? _evalDisarmTimeout;

    // ----- a parked, resumable evaluation (member(X,[a,b,c]) ; ; ...) -----
    //
    // The Immediate window is one call per line, so backtracking across lines means keeping the
    // eval's enumerator ALIVE between calls: the first line runs the goal to its first solution
    // and PARKS the enumerator; a bare ";" line pumps MoveNext for the next. While parked, the
    // engine's debug tables are swapped BACK to the suspended query's (double-buffered in
    // _outerScope / _evalTables) so the Call Stack and Locals still show where the user is
    // stopped, not the eval — the enumerator is invisibly suspended underneath.
    private bool _evalPumping;                                   // inside MoveNext right now
    private bool _evalParked;                                    // a solution is shown, ; resumes
    private System.Collections.Generic.IEnumerator<Solution>? _pendingEnum;
    private List<string>? _pendingReport;                        // var names to render per solution

    /// <summary>ADR-035 D5+ — one frame variable a solution may bind INTO the suspended
    /// frame: the name the user wrote, the name its substituted variable carries in the
    /// solution, and the frame cell's heap address on the suspended activation.</summary>
    private readonly record struct CommitVar(string FrameName, string SolutionKey, int FrameAddr);

    private List<CommitVar>? _pendingCommit;      // free frame vars the goal mentions
    private string? _pendingCommitRefusal;        // attvar note, when binding is disabled
    private bool _commitLocked;                   // a solution committed: ';' is over

    /// <summary>ADR-035 D5+ — a commit changed the suspended frame's bindings: the stop
    /// snapshot is stale and must be RE-CAPTURED, not restored (the channel session's
    /// post-eval bracket consumes this via <see cref="TakeFrameStateChanged"/>).</summary>
    internal bool FrameStateChanged { get; private set; }

    internal bool TakeFrameStateChanged()
    {
        bool v = FrameStateChanged;
        FrameStateChanged = false;
        return v;
    }
    private PrologEngine.DebugEvalScope? _outerScope;            // the suspended query's tables
    private PrologEngine.DebugEvalScope? _evalTables;            // the eval's tables, while parked
    private System.Threading.CancellationTokenSource? _pendingCts;
    private int _pendingSavedDepth;
    private StepMode _pendingSavedMode;
    private bool _pendingSavedRedo;
    private int _pendingSolutionCount;

    /// <summary>ADR-035 — the fallback: an evaluation that must not stop. With it set, a
    /// breakpoint (or <c>debugger_break/0</c>) reached by the evaluated goal is ignored
    /// rather than reported — for a host whose nested break states misbehave, or a user
    /// who wants evals to just run. Default off: a breakpoint the user set is a breakpoint
    /// the user set, whoever reaches it.</summary>
    public static bool SuppressStopsDuringEvaluation { get; set; }
        = Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_EVAL_QUIET") == "1";

    /// <summary>How long an evaluated goal may run before it is cancelled — UNTIL its
    /// first stop: a goal standing at a breakpoint is the user's to resume or abort, for
    /// as long as they care to look, and a timer that fired under them would abort the
    /// evaluation they were in the middle of inspecting.</summary>
    public static TimeSpan EvaluationTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>ADR-035 — the Immediate window. Parses <paramref name="goalText"/>,
    /// substitutes each of its variables that names a variable of display frame
    /// <paramref name="frameIndex"/> with that variable's CURRENT value, and runs the
    /// result as a real query — a new activation over the live engine, database effects
    /// and all, with the exact semantics of any nested mid-query activation. Returns the
    /// first solution's bindings ("X = 5, Y = out(5)"), "true", "false", or an error
    /// sentence.
    ///
    /// <para>A goal with more than one solution can be walked like the REPL: after the first
    /// solution the evaluation is PARKED, and a line consisting only of "<c>;</c>" asks for the
    /// next. Any other line abandons the parked evaluation and starts fresh; so does stepping
    /// or continuing the program. While parked the debugger shows the SUSPENDED query as
    /// usual — the parked enumerator is invisible until the next <c>;</c>.</para></summary>
    public string EvaluateGoal(int frameIndex, string goalText)
    {
        if (_evalPumping) return "an evaluation is already running";

        string trimmed = goalText?.Trim() ?? "";

        // A bare ";" continues the parked evaluation — the next solution, as in the REPL.
        if (trimmed is ";" or ";.")
        {
            if (_commitLocked)
                return "the previous solution's bindings were committed to the frame; "
                    + "re-solving is disabled (undoing them would need the frame unbound)";
            if (_pendingEnum is null)
                return "no evaluation to continue — type a goal first";
            return PumpPendingEvaluation();
        }

        // A real goal abandons any parked evaluation and starts a new one.
        AbandonPendingEvaluation();

        Activation? outer = Current;
        if (outer is null) return "nothing is stopped: no frame to evaluate against";
        if (string.IsNullOrWhiteSpace(trimmed)) return "nothing to evaluate";

        // Parse first — nothing is saved or run for a goal that does not read.
        Term goal;
        IReadOnlyList<string> names;
        try
        {
            string text = trimmed;
            if (!text.EndsWith(".", StringComparison.Ordinal)) text += ".";
            (goal, names) = _engine.ParseGoal(text);
        }
        catch (Exception ex)
        {
            return "syntax error: " + ex.Message;
        }

        // The frame's variables, as terms. The goal's variables that match by name are
        // substituted; the rest stay free and come back as the answer's bindings.
        //
        // ADR-035 D5+ — BIND-INTO-FRAME. A frame variable that is FREE substitutes as a
        // bare VarTerm (named _G<addr>, its own heap cell's identity): the goal runs with a
        // fresh variable exactly as before, but now we REMEMBER the pairing — frame name,
        // the solution key that variable will carry, and the frame cell's heap ADDRESS —
        // and when a solution arrives, its value for that key is unified back INTO the
        // suspended frame (see TryCommitSolutionToFrame). The user's design: run the goal
        // as always, then commit what it bound.
        string? frameModule = null;
        var substituted = new HashSet<string>(StringComparer.Ordinal);
        List<CommitVar>? commit = null;
        string? commitRefusal = null;
        if (_engine.TryGetDisplayFrameContext(outer, frameIndex, out int pc, out int env))
        {
            frameModule = _engine.ModuleForFrame(pc);
            foreach (var (name, value, addr, isAttVar)
                in _engine.MaterializeFrameVariablesWithAddresses(outer, pc, env))
            {
                if (!names.Contains(name)) continue;
                goal = SubstituteVariable(goal, name, value);
                substituted.Add(name);
                if (value is VarTerm vt && addr >= 0)
                {
                    if (isAttVar)
                        commitRefusal = " [" + name + " is an attributed variable: "
                            + "binding into the frame is disabled]";
                    else
                        (commit ??= new List<CommitVar>()).Add(new CommitVar(name, vt.Name, addr));
                }
            }
        }
        var report = new List<string>();
        foreach (string n in names)
            if (!substituted.Contains(n) && !report.Contains(n)) report.Add(n);
        _pendingCommit = commitRefusal is null ? commit : null;
        _pendingCommitRefusal = commitRefusal;
        _commitLocked = false;

        // Resolve module qualification: an explicit Module:Goal, and an unqualified predicate
        // against the module of the frame the user is stopped in — so a module-local predicate
        // (Blint's `show_usage`) is callable by the name the source uses, not only by its mangled
        // `blint$show_usage`. Done here, while the outer query's code space is still the current
        // one (the eval's own setup has not run yet).
        goal = _engine.ResolveGoalModule(goal, frameModule);

        // THE BRACKET. The outer stack is captured NOW — the eval's query setup rebuilds the
        // per-query tables, and after that the suspended query's frames cannot be walked
        // correctly until they are put back. _outerScope holds the suspended query's tables for
        // the whole life of the (possibly parked) evaluation; AbandonPendingEvaluation restores
        // them, on exhaustion, error, a new goal, or a step.
        _evalActive = true;
        _evalOuter = outer;
        _evalGoalText = Ellipsize(trimmed, 80);
        _evalOuterFrames = _engine.CaptureFrames(outer);
        _outerScope = _engine.BeginDebugEvaluation();
        _pendingSavedDepth = _lastStopDepth;
        _pendingSavedMode = _mode;
        _pendingSavedRedo = _lastStopWasRedo;
        _mode = StepMode.Continue;

        // GetEnumerator does not run the query yet — the first MoveNext (in PumpPendingEvaluation)
        // does — so the eval's tables do not overwrite the outer's until we are ready.
        _pendingCts = new System.Threading.CancellationTokenSource();
        _pendingEnum = _engine.QueryAll(goal, _pendingCts.Token).GetEnumerator();
        _pendingReport = report;
        _pendingSolutionCount = 0;
        return PumpPendingEvaluation();
    }

    /// <summary>Advance the parked evaluation by one solution and re-park (or clean up when it
    /// is exhausted / times out / errors). Renders the solution's bindings the way the first
    /// call does. Restores the eval's own tables before pumping and swaps the suspended query's
    /// back after, so the debugger's view is correct whenever we return to the user.</summary>
    private string PumpPendingEvaluation()
    {
        _evalPumping = true;
        bool sawStop = false;
        _evalDisarmTimeout = () =>
        {
            // The goal reached a breakpoint: it is the user's now, for as long as they want to
            // look. Only the RUNNING part of an evaluation is on the clock.
            sawStop = true;
            try { _pendingCts?.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        };
        try
        {
            // Resuming a parked eval: put its tables back before it runs again.
            if (_evalParked)
            {
                _engine.EndDebugEvaluation(_evalTables!);
                _evalParked = false;
            }
            try { _pendingCts!.CancelAfter(EvaluationTimeout); }
            catch (ObjectDisposedException) { }

            bool has;
            try { has = _pendingEnum!.MoveNext(); }
            catch (OperationCanceledException)
            {
                AbandonPendingEvaluation();
                return sawStop
                    ? "the evaluation was aborted"
                    : $"timed out after {EvaluationTimeout.TotalSeconds:0.#} s "
                        + "(the goal was still running)";
            }
            catch (Exception ex)
            {
                AbandonPendingEvaluation();
                return "error: " + ex.Message;
            }

            if (!has)
            {
                bool any = _pendingSolutionCount > 0;
                AbandonPendingEvaluation();
                return any ? "no more solutions" : "false";
            }

            _pendingSolutionCount++;
            var report = _pendingReport!;
            // Render BEFORE swapping tables — the value terms live on the eval activation's heap
            // and the render reads the operator table, all of which is still current here.
            string rendered;
            if (report.Count == 0)
            {
                rendered = "true";
            }
            else
            {
                var solution = _pendingEnum!.Current;
                var text = new System.Text.StringBuilder();
                foreach (string n in report)
                {
                    if (text.Length > 0) text.Append(",\n");
                    Term? value = solution[n];
                    text.Append(n).Append(" = ").Append(value is null
                        ? "_"
                        : Ellipsize(AstTermRenderer.Render(value, 999, _engine.Operators), 2048));
                }
                rendered = text.ToString();
            }

            // ADR-035 D5+ — commit this solution's bindings INTO THE SUSPENDED FRAME. Read
            // here, while the eval's tables and heap are still current (the solution's
            // terms materialize off the eval activation); the unification itself touches
            // only the OUTER activation's heap and trail, which no table swap affects.
            // A commit that instantiated at least one frame variable ends the walk: the
            // frame is bound to THIS solution now, and walking to the next would need it
            // unbound — so the parked choice points die with the enumerator, and ';' says
            // why. A commit that bound nothing (or rolled back) leaves ';' available.
            var (committed, note) = TryCommitSolutionToFrame(_pendingEnum!.Current);
            if (note is not null) rendered += note;
            if (_pendingCommitRefusal is not null) rendered += _pendingCommitRefusal;
            if (committed > 0)
            {
                FrameStateChanged = true;
                AbandonPendingEvaluation();
                _commitLocked = true;
                return rendered;
            }

            ParkPendingEvaluation();
            return rendered;
        }
        finally
        {
            _evalDisarmTimeout = null;
            _evalPumping = false;
        }
    }

    /// <summary>ADR-035 D5+ — unify this solution's bindings into the SUSPENDED frame.
    ///
    /// <para>The user's design, and the reason it is one mechanism and not a special case:
    /// the goal ran exactly as always (frame variables substituted — the free ones by a
    /// bare variable), and now whatever the solution SAYS those variables are is unified
    /// against the frame's own heap cells, on the suspended activation, with real trailing.
    /// So <c>X = f(1)</c> commits a structure, <c>member(X, Xs)</c> commits the member
    /// found, <c>X = Y</c> commits an ALIASING (both frame cells end up sharing), and a
    /// solution that contradicts the frame's own aliasing rolls back whole: the marks
    /// below make the commit transactional.</para>
    ///
    /// <para>Trailing gives the honest semantics: the bindings behave exactly as if the
    /// program had executed the unification at the stop point — a later backtrack past
    /// this point undoes them, like any binding made here.</para></summary>
    private (int Committed, string? Note) TryCommitSolutionToFrame(Solution solution)
    {
        if (_pendingCommit is not { Count: > 0 } commit || _evalOuter is not { } outer)
            return (0, null);

        // 1. The solution's values for the committed keys, materialized off the EVAL
        //    activation's heap — which is why this runs before the tables swap back.
        //    A key the solution cannot answer is skipped, not fabricated.
        var values = new Term?[commit.Count];
        for (int i = 0; i < commit.Count; i++)
        {
            try { values[i] = solution[commit[i].SolutionKey]; }
            catch (Exception) { values[i] = null; }
        }

        // 2. Seed the sharing map. A value that is a BARE variable is a frame variable
        //    that stayed free or got ALIASED: its solution-side name must resolve to the
        //    frame's own cell, so that the same name embedded inside another value
        //    (X = f(Y)) builds a reference to the REAL Y, not a copy.
        var shared = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < commit.Count; i++)
            if (values[i] is VarTerm vt) shared.TryAdd(vt.Name, commit[i].FrameAddr);

        // 3. Build + unify, transactionally, on the OUTER activation only.
        int bMark = outer.BindingTrailTop;
        int eMark = outer.ExtraTrailTop;
        int hMark = outer.HeapTop;
        var derefBefore = new int[commit.Count];
        var unboundBefore = new bool[commit.Count];
        for (int i = 0; i < commit.Count; i++)
        {
            derefBefore[i] = outer.Deref(commit[i].FrameAddr);
            unboundBefore[i] = IsUnboundAt(outer, commit[i].FrameAddr);
        }

        bool ok = true;
        try
        {
            for (int i = 0; i < commit.Count && ok; i++)
            {
                Term? v = values[i];
                if (v is null) continue;
                // The variable "stayed itself": nothing to unify (and unifying a cell
                // with itself is a no-op anyway — this just skips the allocation).
                if (v is VarTerm self && shared.TryGetValue(self.Name, out int mapped)
                    && mapped == commit[i].FrameAddr)
                    continue;
                Cell built = Materializer.MaterializeAsCellSharing(outer, v, shared);
                ok = outer.Unify(commit[i].FrameAddr, HeapAddrOf(outer, built));
            }
        }
        catch (Exception)
        {
            ok = false;
        }

        if (!ok)
        {
            outer.UnwindTrails(bMark, eMark);
            outer.SetHeapTop(hMark);
            return (0, "\n[this solution does not unify with the frame's own bindings — "
                + "nothing was committed]");
        }

        // 4. What actually CHANGED, frame-visibly: a variable whose dereference moved
        //    (bound to a value, or aliased to another cell). A commit that changed
        //    nothing releases its trial cells and leaves the ';' walk available.
        int committed = 0;
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < commit.Count; i++)
        {
            int after = outer.Deref(commit[i].FrameAddr);
            bool changed = after != derefBefore[i]
                || (unboundBefore[i] && !IsUnboundAt(outer, commit[i].FrameAddr));
            if (!changed) continue;
            committed++;
            string val;
            try
            {
                val = AstTermRenderer.Render(
                    TermReader.Materialize(outer, after), 999, _engine.Operators);
            }
            catch (Exception) { val = "_"; }
            text.Append('\n').Append(commit[i].FrameName).Append(" = ")
                .Append(Ellipsize(val, 2048));
        }
        if (committed == 0)
        {
            outer.UnwindTrails(bMark, eMark);
            outer.SetHeapTop(hMark);
            return (0, null);
        }
        text.Append("\n(").Append(committed)
            .Append(committed == 1 ? " binding" : " bindings")
            .Append(" committed to the frame)");
        return (committed, text.ToString());
    }

    // ---- ADR-035 D5+ — Set Next Statement ----

    /// <summary>One recorded call-port mark: everything a rewind needs to put the machine
    /// back to "about to run this goal". With <see cref="Activation.TrailEverything"/> on,
    /// unwinding the two trails to the recorded tops restores EVERY binding made since —
    /// the trail is the note-taking; no goal re-executes, no side effect repeats.</summary>
    private readonly record struct PortMark(
        Activation Engine, int P, int E, int BindingTrailTop, int ExtraTrailTop,
        int HeapTop, int B, int B0, int GcCount);

    private readonly List<PortMark> _portMarks = new();
    private const int PortMarkCapacity = 2048;

    /// <summary>Record the machine's position at a call port. STACK DISCIPLINE keeps the
    /// list honest without bookkeeping: control standing at frame E means every mark of a
    /// DEEPER frame is dead (that frame returned, or backtracking discarded it), and a
    /// mark whose trail top exceeds the current one was undone by backtracking — both are
    /// popped here, lazily, before the new mark goes on. A mark of another activation (a
    /// previous query) is likewise dead.</summary>
    private void RecordPortMark(Activation engine)
    {
        if (_evalActive || _conditionEval) return;   // evaluations are not rewind targets

        // A port at any site other than the one Set Next Statement moved to means the
        // moved-to goal has run: its stop suppression is over. See _snsMovedToSite.
        if (_snsMovedToSite >= 0
            && _engine.SiteAtOrBefore(engine.P) != _snsMovedToSite)
            _snsMovedToSite = -1;

        var marks = _portMarks;
        while (marks.Count > 0)
        {
            var top = marks[^1];
            if (!ReferenceEquals(top.Engine, engine)
                || top.E > engine.E
                || top.BindingTrailTop > engine.BindingTrailTop
                || top.ExtraTrailTop > engine.ExtraTrailTop)
            {
                marks.RemoveAt(marks.Count - 1);
                continue;
            }
            break;
        }
        if (marks.Count >= PortMarkCapacity) marks.RemoveAt(0);   // oldest target lost
        marks.Add(new PortMark(
            engine, engine.P, engine.E, engine.BindingTrailTop, engine.ExtraTrailTop,
            engine.HeapTop, engine.B, engine.B0, engine.HeapGcCount));
    }

    /// <summary>ADR-035 D5+ — move the next-statement pointer of the TOP frame.
    ///
    /// <para>FORWARD (a later goal of the same clause): the pointer moves, the skipped
    /// goals never run — C# semantics, bindings they would have made do not happen.</para>
    ///
    /// <para>BACKWARD (an earlier goal): the machine REWINDS to the recorded mark of that
    /// goal's call port — B restored (newer choice points discarded), both trails unwound
    /// to the recorded tops (undoing every binding made since; TrailEverything makes that
    /// complete), heap top restored. Nothing re-executes: the user asked to stand there
    /// again and run forward themselves. Refused — with the list of lines that WOULD be
    /// accepted — when no valid mark exists for the target: the goal never ran in this
    /// frame instance, a cut discarded the choice point the mark saved, or a heap
    /// collection rewrote the world since.</para>
    ///
    /// <para>THE HEAD LINE (or the blank span between head and first goal) rewinds to the
    /// CALLER's mark for the call that created this frame: the frame is popped, and
    /// continuing re-runs the call — dispatch, head unification and all. Head matching is
    /// pure, so re-running it is safe.</para>
    ///
    /// <para>Returns "" on success, or a message explaining the refusal.</para></summary>
    private StopReason _lastStopReason = StopReason.AsyncBreak;

    public string SetNextStatement(int frameIndex, int targetLine)
    {
        if (Current is not { } outer) return "nothing is stopped";
        if (frameIndex != 0) return "Set Next Statement is supported on the top frame only";
        if (_lastStopReason is StopReason.Redo or StopReason.Fail)
            return "Set Next Statement is not available at a redo/fail stop "
                + "(the machine is mid-backtrack)";
        if (!_engine.TryGetDisplayFrameContext(outer, 0, out int pc, out int env))
            return "this frame has no statement context";
        if (outer.LastCallOptimisation)
            return "Set Next Statement needs last-call optimisation off "
                + "(set_prolog_flag(debug_lco, off))";

        var sites = _engine.ClauseSites(pc);
        if (sites.Count == 0) return "this clause has no statement positions";

        int currentSite = _engine.SiteAtOrBefore(outer.P);
        var currentInfo = currentSite >= 0
            ? Shumway.Core.DebugSiteTable.Get(currentSite) : default;

        // The head span means "restart the clause body": rewind to the FIRST goal's mark.
        // Head-unification bindings are BELOW that mark, so they survive — the same
        // meaning C#'s Set Next Statement to a method's first line has (parameters keep
        // their values). Re-matching the head itself would be a cross-frame rewind into
        // the caller; deferred.
        var span = _engine.ClauseLineSpan(currentInfo.FileId, targetLine);
        if (span is { } s && targetLine >= s.HeadLine && targetLine < s.FirstLine
            && s.FirstLine == sites[0].Line)
            targetLine = sites[0].Line;

        // Resolve the target line to a site of THIS clause.
        int targetPc = -1;
        foreach (var (sitePc, line) in sites)
            if (line == targetLine) { targetPc = sitePc; break; }
        if (targetPc < 0)
            return "line " + targetLine + " is not a statement of this clause; "
                + "statements are at: " + string.Join(", ",
                    sites.Select(x => x.Line).Distinct().OrderBy(x => x));

        int currentPc = outer.P;
        if (targetPc == currentPc) return "";
        if (targetPc > currentPc)
        {
            // FORWARD: just move. The skipped goals do not run — which is exactly why the
            // move must leave marks behind: it is PURE, so the machine state at every
            // skipped site (and at the site being left) IS the current state. Recording a
            // mark apiece keeps the valid-target set invariant under pure moves — the user
            // can change their mind and move BACK to any of them before running anything
            // (the reported case: forward to the clause's fail, then back to the first
            // goal — refused, because the skipped goals had never fired their ports).
            int currentSiteStart = -1;
            foreach (var (sitePc, _) in sites)
                if (sitePc <= currentPc && sitePc > currentSiteStart) currentSiteStart = sitePc;
            foreach (var (sitePc, _) in sites)
                if (sitePc >= currentSiteStart && sitePc < targetPc)
                    RecordPureMoveMark(outer, env, sitePc);

            outer.RedirectPc(targetPc);
            _snsMovedToSite = _engine.SiteAt(targetPc);
            FrameStateChanged = true;
            return "";
        }

        // BACKWARD: find the newest valid mark for that site in this frame.
        var candidate = FindMark(outer, env, targetPc);
        if (candidate is not { } mark)
        {
            var acceptable = AcceptableRewindLines(outer, env);
            return "cannot rewind to line " + targetLine
                + (acceptable.Count > 0
                    ? "; rewindable lines are: " + string.Join(", ", acceptable)
                    : "; no rewindable position is recorded for this frame");
        }

        RestoreMark(outer, mark, targetPc);
        _snsMovedToSite = _engine.SiteAt(targetPc);
        return "";
    }

    /// <summary>ADR-035 D5+ — the site an applied Set Next Statement moved to. Stop
    /// decisions AT that site are suppressed — the arrow is already there, and the user's
    /// next step must EXECUTE the goal under it, not "stop" where they already stand
    /// (their report: after a move, the first F10/F11 did nothing and only the second ran
    /// the goal). Covers BOTH decisions the site can raise (its Break byte and its call
    /// port). Disarmed by <see cref="RecordPortMark"/> at the first port of any OTHER
    /// site — execution has moved past the goal, normal stopping resumes — which holds
    /// under F5 too (ports record marks whatever the step mode), so a loop coming back
    /// around to a breakpoint on the moved-to line stops normally.</summary>
    private int _snsMovedToSite = -1;

    private bool SnsMoveSuppressesStopAt(Activation engine)
        => _snsMovedToSite >= 0
           && _engine.SiteAtOrBefore(engine.P) == _snsMovedToSite;

    /// <summary>A rewind mark for a site a pure move skipped: the mark's position is the
    /// site, the state is the CURRENT machine state — restoring it unwinds nothing, which
    /// is correct, because nothing ran.</summary>
    private void RecordPureMoveMark(Activation outer, int env, int sitePc)
    {
        if (_portMarks.Count >= PortMarkCapacity) _portMarks.RemoveAt(0);
        _portMarks.Add(new PortMark(
            outer, sitePc, env, outer.BindingTrailTop, outer.ExtraTrailTop,
            outer.HeapTop, outer.B, outer.B0, outer.HeapGcCount));
    }

    /// <summary>ADR-035 D5+ — the source lines Set Next Statement would ACCEPT at the
    /// current stop, published in every stop's snapshot so the debugger can validate a
    /// Ctrl+Shift+F10 SYNCHRONOUSLY (its CanSetNextStatement) and move the arrow / show a
    /// reason without a func-eval it cannot make. Forward: every later statement of the
    /// clause. Backward: an earlier statement with a live recorded mark. The head span maps
    /// to the first goal. Empty at a redo/fail stop, with LCO on, or off a frame with no
    /// statement context.</summary>
    public IReadOnlyList<int> ValidSetNextLines()
    {
        if (Current is not { } outer
            || _lastStopReason is StopReason.Redo or StopReason.Fail
            || outer.LastCallOptimisation
            || !_engine.TryGetDisplayFrameContext(outer, 0, out int pc, out int env))
            return Array.Empty<int>();

        var sites = _engine.ClauseSites(pc);
        if (sites.Count == 0) return Array.Empty<int>();
        int currentPc = outer.P;

        var lines = new SortedSet<int>();
        bool firstGoalReachable = false;
        foreach (var (sitePc, line) in sites)
        {
            // Forward: always. The CURRENT statement: a no-op move, accepted — exactly as
            // C# accepts Set Next Statement to the line the arrow is on. Backward: only
            // with a live recorded mark.
            bool reachable = sitePc >= currentPc
                || FindMark(outer, env, sitePc) is not null;
            if (!reachable) continue;
            lines.Add(line);
            if (sitePc == sites[0].Pc) firstGoalReachable = true;
        }
        // The clause HEAD span (head line .. first goal line) restarts the body — offer it
        // whenever the first goal is reachable: rewindable from deeper in, or simply the
        // CURRENT position (stopped at the top of the body, "back to the head" is a no-op
        // — refusing it there was just confusing).
        if (firstGoalReachable)
        {
            int firstSite = _engine.SiteAtOrBefore(sites[0].Pc);
            if (firstSite >= 0)
            {
                var fi = Shumway.Core.DebugSiteTable.Get(firstSite);
                var span = _engine.ClauseLineSpan(fi.FileId, fi.Line);
                if (span is { } s)
                    for (int hl = s.HeadLine; hl < s.FirstLine; hl++) lines.Add(hl);
            }
        }
        return lines.ToList();
    }

    private PortMark? FindMark(Activation outer, int env, int targetPc)
    {
        int targetSite = _engine.SiteAt(targetPc);
        for (int i = _portMarks.Count - 1; i >= 0; i--)
        {
            var m = _portMarks[i];
            if (!ReferenceEquals(m.Engine, outer) || m.E != env) continue;
            if (_engine.SiteAtOrBefore(m.P) != targetSite) continue;
            if (!MarkIsValid(outer, m)) continue;
            return m;
        }
        return null;
    }

    private bool MarkIsValid(Activation outer, PortMark m)
        => m.GcCount == outer.HeapGcCount
           && m.BindingTrailTop <= outer.BindingTrailTop
           && m.ExtraTrailTop <= outer.ExtraTrailTop
           && m.HeapTop <= outer.HeapTop
           && outer.IsChoicePointInChain(m.B);

    private List<int> AcceptableRewindLines(Activation outer, int env)
    {
        var lines = new SortedSet<int>();
        foreach (var m in _portMarks)
        {
            if (!ReferenceEquals(m.Engine, outer) || m.E != env) continue;
            if (!MarkIsValid(outer, m)) continue;
            int site = _engine.SiteAtOrBefore(m.P);
            if (site >= 0) lines.Add(Shumway.Core.DebugSiteTable.Get(site).Line);
        }
        return lines.ToList();
    }

    private void RestoreMark(Activation outer, PortMark mark, int targetPc)
    {
        outer.SetB(mark.B);
        outer.UnwindTrails(mark.BindingTrailTop, mark.ExtraTrailTop);
        outer.SetHeapTop(mark.HeapTop);
        outer.SetB0(mark.B0);
        outer.RedirectPc(targetPc);
        FrameStateChanged = true;
    }

    private static bool IsUnboundAt(Activation outer, int addr)
    {
        int d = outer.Deref(addr);
        Cell c = outer.GetHeap(d);
        return c.Tag == Tag.Ref && c.AsHeapIndex == d;
    }

    /// <summary>A heap address <see cref="Activation.Unify"/> can take, for any cell
    /// <see cref="Materializer"/> returns: a Ref is already an address; a value cell
    /// (atom, int, inline list/struct reference) gets one allocated to hold it.</summary>
    private static int HeapAddrOf(Activation outer, Cell built)
    {
        if (built.Tag == Tag.Ref) return built.AsHeapIndex;
        int a = outer.AllocateHeap(1);
        outer.SetHeap(a, built);
        return a;
    }

    /// <summary>Suspend the evaluation between solutions: snapshot its tables, put the suspended
    /// query's tables back so the debugger's view is the user's stop again, show that query, and
    /// stop the clock while we wait for the next <c>;</c>.</summary>
    private void ParkPendingEvaluation()
    {
        _evalTables = _engine.BeginDebugEvaluation();   // the eval's tables, to restore on resume
        _engine.EndDebugEvaluation(_outerScope!);        // the suspended query's, for now
        _evalParked = true;
        Current = _evalOuter;
        try { _pendingCts!.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>End the parked evaluation for good — exhausted, abandoned for a new goal, or
    /// dropped because the user stepped on. Tears down the enumerator, restores the suspended
    /// query's tables and view, and clears all pending state. Idempotent.</summary>
    private void AbandonPendingEvaluation()
    {
        if (_outerScope is null && _pendingEnum is null) return;

        try { _pendingEnum?.Dispose(); } catch { /* teardown of the eval activation */ }
        try { _pendingCts?.Dispose(); } catch { }

        // Authoritatively restore the suspended query's tables, whether we were parked (already
        // swapped) or mid-pump (still the eval's).
        if (_outerScope is not null) _engine.EndDebugEvaluation(_outerScope);

        Activation? outer = _evalOuter;
        _pendingEnum = null;
        _pendingReport = null;
        _pendingCommit = null;
        _pendingCts = null;
        _outerScope = null;
        _evalTables = null;
        _evalParked = false;
        _evalActive = false;
        _evalOuter = null;
        _evalOuterFrames = Array.Empty<PrologEngine.DebugFrame>();
        _evalGoalText = "";

        // The machine the debugger is watching is the SUSPENDED one again, and the step the
        // user takes next is from the stop they were at.
        Current = outer;
        _lastStopDepth = _pendingSavedDepth;
        _lastStopWasRedo = _pendingSavedRedo;
        _mode = _pendingSavedMode;
    }

    /// <summary>ADR-035 — drop any parked Immediate-window evaluation and restore the suspended
    /// query's view. Called on detach so a goal left mid-backtracking (evaluated but never
    /// walked to the end with <c>;</c>, then the debugger detached) does not leak its
    /// activation. A no-op when nothing is parked.</summary>
    public void CancelEvaluation() => AbandonPendingEvaluation();

    /// <summary>Replaces every occurrence of the named variable in the goal with the
    /// frame's value for it.</summary>
    private static Term SubstituteVariable(Term goal, string name, Term value)
    {
        switch (goal)
        {
            case VarTerm v when v.Name == name:
                return value;
            case CompoundTerm c:
                Term[]? parts = null;
                for (int i = 0; i < c.Args.Length; i++)
                {
                    Term arg = SubstituteVariable(c.Args[i], name, value);
                    if (!ReferenceEquals(arg, c.Args[i]) && parts is null)
                    {
                        parts = new Term[c.Args.Length];
                        Array.Copy(c.Args, parts, c.Args.Length);
                    }
                    if (parts is not null) parts[i] = arg;
                }
                return parts is null ? goal : new CompoundTerm(c.Functor, parts);
            default:
                return goal;
        }
    }

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
        // A breakpoint reached by a CONDITION's own goal is never a stop — the condition
        // is the debugger's plumbing, not the program, and stopping inside it would
        // recurse into evaluating it again. Checked first, before anything else runs.
        if (_conditionEval) return;

        // The suppression fallback: an evaluation that must not stop runs straight
        // through its breakpoints. See SuppressStopsDuringEvaluation.
        if (_evalActive && SuppressStopsDuringEvaluation) return;

        Current = engine;

        // A breakpoint AT the goal a Set Next Statement just moved to does not re-fire
        // before the goal runs: the user deliberately placed the arrow there; the C#
        // debugger behaves the same way. One-shot — the loop coming back around to this
        // site stops normally.
        if (SnsMoveSuppressesStopAt(engine)) return;

        // ADR-035 D5 — a conditional breakpoint: the condition goal decides, HERE, on the
        // engine's own thread, before any debugger hears about the hit. A condition that
        // fails means the program runs on — no notify, no cross-process round trip, which
        // is what makes a hot conditional breakpoint affordable. A condition that cannot
        // run stops WITH its error: silence would swallow the breakpoint undiagnosably.
        string conditionError = "";
        if (_engine.BreakpointConditionAt(pc) is string condition)
        {
            bool stops = EvaluateBreakpointCondition(engine, condition, out string? error);
            if (ShumwayDebugHelper.DiagEnabled)
                ShumwayDebugHelper.DiagLine("breakpoint condition '" + condition + "' -> "
                    + (error is not null ? "ERROR: " + error : stops ? "holds (stop)" : "fails (run on)"));
            if (!stops) return;
            conditionError = error ?? "";
        }
        else if (ShumwayDebugHelper.DiagEnabled)
        {
            // Diag-only: the routing answer. A breakpoint the user set a condition on that
            // reports "no condition" here means the condition never reached the engine —
            // the VS-side capture (ParseCondition) did not fire or did not forward.
            ShumwayDebugHelper.DiagLine("breakpoint hit (no condition attached): stop");
        }

        // A breakpoint always stops, whatever the step mode: it is the one thing the
        // user asked for by name.
        //
        // The stop says where the machine IS, as every stop does. It ALSO says which
        // breakpoint fired, as the user set it — a different question with a different
        // answer, since a breakpoint on a rule's head binds at its first goal, and a
        // debugger has to match the hit to the red dot it drew.
        _breakRequest = _engine.BreakpointRequestAt(pc);
        _conditionError = conditionError;
        Stop(engine, StopReason.Breakpoint, PortDepth(engine), SiteOf(pc), goal: null);
        _conditionError = "";
        _breakRequest = null;
        _reportedCallSite = _engine.SiteAtOrBefore(pc);
    }

    // The condition-eval bracket flag and the error being reported, for the length of one
    // stop — same lifetime discipline as _breakRequest.
    private bool _conditionEval;
    private string _conditionError = "";

    /// <summary>ADR-035 D5 — evaluates a conditional breakpoint's goal in the frame the
    /// breakpoint fired in. Returns whether the breakpoint STOPS; when it stops because
    /// the condition could not run, <paramref name="error"/> says why (null for a
    /// condition that simply held).
    ///
    /// <para>The recipe is the Immediate window's (<see cref="EvaluateGoal"/>): parse,
    /// substitute the frame's variables by name, resolve module qualification against the
    /// frame's module, run as a real nested query over the live engine. The differences
    /// are the differences between plumbing and a user at a prompt: only the FIRST
    /// solution matters (the enumerator is disposed right after, cutting the rest); no
    /// parking; nothing the condition reaches may stop (breakpoints inside it are skipped
    /// via <see cref="_conditionEval"/>, ports via the saved <see cref="StepMode.Continue"/>);
    /// and a runaway condition is cancelled at <see cref="EvaluationTimeout"/> rather than
    /// hanging the program. Bindings the condition makes do not leak — the substituted
    /// values are materialised copies, and the eval runs in its own activation. Database
    /// side effects (an <c>assertz</c>) persist, exactly as a C# breakpoint condition
    /// with side effects would; conditions are expected to be tests.</para></summary>
    private bool EvaluateBreakpointCondition(Activation engine, string text, out string? error)
    {
        error = null;

        Term goal;
        IReadOnlyList<string> names;
        try
        {
            string t = text.Trim();
            if (!t.EndsWith(".", StringComparison.Ordinal)) t += ".";
            (goal, names) = _engine.ParseGoal(t);
        }
        catch (Exception ex)
        {
            error = "breakpoint condition syntax error: " + ex.Message;
            return true;
        }

        // The bracket, exactly as the Immediate window's: the eval's query setup rebuilds
        // the per-query tables, and the suspended query needs its own back afterwards.
        var savedMode = _mode;
        var savedDepth = _lastStopDepth;
        var savedRedo = _lastStopWasRedo;
        var savedCurrent = Current;
        _mode = StepMode.Continue;
        _conditionEval = true;
        var scope = _engine.BeginDebugEvaluation();
        try
        {
            // EVERYTHING from here runs inside the protection, the frame-variable
            // substitution included. This method is called from INSIDE the outer query's
            // dispatch loop: an exception that escaped it would land in the outer
            // RunCatching, where the PROGRAM's own catch/3 would eat it (the program
            // takes an error path it never takes without a debugger) or, uncaught, kill
            // the query outright — which is exactly how the first Blint crash presented.
            // Nothing the condition machinery does may ever leak into the program.

            // The frame the breakpoint fired in is frame 0 of the stop the user would
            // see. Substituted BEFORE the nested query's setup swaps the tables — these
            // reads walk the outer query's own.
            string? frameModule = null;
            if (_engine.TryGetDisplayFrameContext(engine, 0, out int framePc, out int frameEnv))
            {
                frameModule = _engine.ModuleForFrame(framePc);
                foreach (var (name, value) in _engine.MaterializeFrameVariables(engine, framePc, frameEnv))
                    if (names.Contains(name))
                    {
                        goal = SubstituteVariable(goal, name, value);
                        if (ShumwayDebugHelper.DiagEnabled)
                            ShumwayDebugHelper.DiagLine(
                                "  condition var " + name + " := "
                                + Ellipsize(value.ToString() ?? "", 120));
                    }
            }
            goal = _engine.ResolveGoalModule(goal, frameModule);

            using var cts = new System.Threading.CancellationTokenSource(EvaluationTimeout);
            using var solutions = _engine.QueryAll(goal, cts.Token).GetEnumerator();
            return solutions.MoveNext();
        }
        catch (OperationCanceledException)
        {
            error = $"breakpoint condition timed out after {EvaluationTimeout.TotalSeconds:0.#} s: "
                + Ellipsize(text, 80);
            return true;
        }
        catch (Exception ex)
        {
            error = "breakpoint condition error: " + ex.Message;
            return true;
        }
        finally
        {
            _engine.EndDebugEvaluation(scope);
            _conditionEval = false;
            _mode = savedMode;
            _lastStopDepth = savedDepth;
            _lastStopWasRedo = savedRedo;
            Current = savedCurrent;
        }
    }

    void IDebugSession.OnCallAddress(Activation engine, int address, bool tailCall)
    {
        RecordPortMark(engine);   // ADR-035 D5+ — every goal is a rewind target
        // A dictionary probe, and no port for an address that names no predicate.
        if (_engine.LookupPredicateByAddress(address) is null) return;
        // A ,/;/-> control construct (or its $disj_N / $call_* plumbing) is flow, not a
        // goal — never a stop, wherever it is called from.
        if (_engine.IsTransparentCalleeAddress(address)) return;
        // WHERE THE CALL IS WRITTEN is what decides the stop — the same rule builtins have
        // always had (see OnCallBuiltin): `member(X, L)` on the user's line is the user's
        // goal, whatever member/2 was compiled from, and a stepper that skipped it executed
        // it fused with the goal before (the prueba.pl F11 report). The callee side still
        // counts for the OTHER direction: a call into the USER's code from somewhere they
        // cannot see (prelude meta-dispatch running a findall goal) stops on entry, which
        // is the one visible port that call has. Skipped only when BOTH ends are invisible
        // — prelude internals calling prelude internals.
        if (!_engine.IsDebuggableCallee(address)
            && !_engine.IsDebuggableAddress(engine.P)) return;
        _goalKind = GoalKind.Address;
        _goalId = address;
        OnCall(engine);
    }

    void IDebugSession.OnCallFunctor(Activation engine, int functorId, bool tailCall)
    {
        RecordPortMark(engine);   // ADR-035 D5+
        if (FunctorGoalName(functorId) is null) return;   // an engine helper: not a goal
        if (_engine.IsTransparentControlFunctor(functorId)) return;   // flow, not a goal
        // Same site-or-callee rule as OnCallAddress above.
        if (!_engine.IsDebuggableFunctor(functorId)
            && !_engine.IsDebuggableAddress(engine.P)) return;
        _goalKind = GoalKind.Functor;
        _goalId = functorId;
        OnCall(engine);
    }

    void IDebugSession.OnCallBuiltin(Activation engine, int builtinId, bool tailCall)
    {
        RecordPortMark(engine);   // ADR-035 D5+
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        if (IsInternal(entry.Name)) return;
        _goalKind = GoalKind.Builtin;
        _goalId = builtinId;
        Current = engine;

        // A FOREIGN builtin is the user's own C#, and they may well have a breakpoint in it.
        // When Visual Studio stops there, the engine thread is frozen INSIDE this call and
        // cannot be asked anything — but the Prolog stack under the C# is precisely what the
        // user came for, and it is a fact right now, on the way in. So publish it here, and
        // say we are inside; the debugger shows it only for as long as that holds.
        //
        // Paid per foreign call, and only while a debugger is listening. It is the one hook
        // that walks the stack outside a stop; every other port returns before doing any work
        // (see MaybeStopAtPort) precisely so that running under the debugger stays cheap.
        if (OnInteropEnter is not null && PrologEngine.IsForeignBuiltin(builtinId))
        {
            _interopDepth++;
            PublishInterop(engine, entry.Name + "/" + entry.Arity);
        }

        // A BUILTIN IS A GOAL, and a step stops at the next goal. Half a clause is builtins —
        // `X is N - 1`, `writeln(X)`, `atom_codes(A, C)` — and a stepper that skipped them
        // stepped over four lines at a time, landing wherever the next user predicate happened
        // to be. Stopping here is stopping BEFORE it runs, at the goal's own line, which is
        // what the user asked to see. (Stepping INTO one is still not a thing: there is no
        // Prolog inside it. The next step goes on to the goal after.)
        //
        // Where the CALLER is is what decides it, not the builtin: a builtin has no source of
        // its own, and its call port fires with the machine standing on the goal's line in the
        // clause that wrote it. So a `succ_or_zero/1` deep in the prelude does not stop
        // anybody, and `X is N - 1` in the user's clause does.
        if (!_engine.IsDebuggableAddress(engine.P)) return;
        OnCall(engine);
    }

    private void PublishInterop(Activation engine, string goal)
    {
        var site = SiteOf(engine.P);
        OnInteropEnter!(new DebugStopEvent(
            StopReason.Call, goal, site.File, site.Line,
            PortDepth(engine), _engine.CaptureFrames(engine)));
    }

    /// <summary>Set by a channel session: the Prolog stack as it stands at the moment control
    /// crosses into a foreign predicate's C#, and the moment it comes back. Null when nobody
    /// is listening, which is what keeps the walk above from happening at all.</summary>
    internal Action<DebugStopEvent>? OnInteropEnter { get; set; }

    internal Action? OnInteropExit { get; set; }

    /// <summary>Foreign calls we are inside, not just how many we have made: a foreign
    /// predicate may call back into Prolog, which may call another foreign predicate.</summary>
    private int _interopDepth;

    private void OnCall(Activation engine)
    {
        // ADR-035 — the entry stop of a --debug-wait launch. This is the first goal the
        // program runs, and stopping here is what "stop at the entry point" means. It goes
        // through EntryBreak (a managed Debugger.Break, the debugger_break/0 path) rather
        // than a port stop, because at startup no step and no async break is pending, and a
        // port stop VS was not waiting for is silently dropped by the monitor. Fires once.
        // Fire only once execution is INSIDE the user's own code — engine.P in a debuggable
        // predicate that is NOT the synthetic `__query__` wrapper. The first call port is the
        // wrapper (`?- main`) calling the entry goal; the wrapper is compiled query code, so it
        // IS a debuggable address, but it has no source line of its own — its port maps to the
        // end of the file (the caret landed on Blint.pl's last line, not main's). Skipping it
        // lands the entry break at main's first goal — the first thing the program is about to
        // DO — which is what "stop at the entry" means.
        if (_breakAtEntry
            && _engine.IsDebuggableAddress(engine.P)
            && !_engine.IsQueryWrapperAddress(engine.P))
        {
            _breakAtEntry = false;
            _reportedCallSite = -1;
            Current = engine;
            EntryBreak?.Invoke(engine);
            return;
        }

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

    void IDebugSession.OnBuiltinResult(Activation engine, int builtinId, bool succeeded)
    {
        // Out of the C# again: the stack we published on the way in is history from here, and
        // saying so is what stops the debugger showing it at a stop that has nothing to do
        // with this call. If an OUTER foreign call is still running (it called back into
        // Prolog, which called this one), the stack it is standing in is not the one we
        // published either — so re-publish rather than leave the deeper one behind.
        if (_interopDepth == 0) return;
        _interopDepth--;
        if (_interopDepth > 0) PublishInterop(engine, CurrentGoal());
        else OnInteropExit?.Invoke();
    }

    void IDebugSession.OnInlineGoal(Activation engine)
    {
        RecordPortMark(engine);   // ADR-035 D5+
        // A `!`, an `is/2`, an `=/2`, a comparison: a goal the user wrote, about to run,
        // with no call of its own — the debug_port opcode in front of it raises what the
        // dispatch never will. It is a landing like any call port (the same rules, the
        // same breakpoint dedup: a breakpoint armed ON the goal patches the port's own
        // byte, reports first, and this fires right after it at the same site). The goal
        // has no callee to name, so the stop names the frame it is standing in.
        _goalKind = GoalKind.None;
        OnCall(engine);
    }

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
        int depth = engine.PendingRedoEnvDepthCapped(_stepDepth + 1) + 1;   // capped: see MaybeStopAtPort
        bool stop = _mode switch
        {
            StepMode.Into => true,

            // A REDO AT THE STEP'S OWN DEPTH IS INSIDE THE GOAL YOU STEPPED OVER. Retrying a
            // clause of the callee does not deepen the environment chain — the callee's frame
            // is not allocated until the clause runs — so its redo port reads at exactly the
            // depth of the call that started it. Stopping there is stopping in the middle of
            // the thing the user said to skip: F10 over `blint_pred_name1(Pred, Pred1)` landed
            // on line 842, another clause of blint_pred_name1, which they had asked not to see.
            // ("Se para en la salida de cada subgoal previo.")
            //
            // So a step over stops at a redo only when it belongs to an ENCLOSING goal —
            // strictly shallower — which is the same rule the exit port already follows, and
            // for the same reason: that is the clause you are in, not the one you skipped.
            StepMode.Over => depth < _stepDepth,
            StepMode.Out => depth < _stepDepth,
            _ => false,
        };
        if (!stop) return;
        depth = engine.PendingRedoEnvDepth + 1;   // exact, now that it is going to be shown

        var (e, cp) = engine.TopChoicePointContext;
        // -1 is a backtrackable builtin re-satisfying (between/3, repeat/0, clause/2):
        // there is no bytecode clause to point at, so the goal we are in stands.
        int pc = retryPc >= 0 ? _engine.RetryClauseSite(retryPc) : engine.P;

        // Retrying a clause of the prelude's or a library's — not the user's program — or of a
        // TRANSPARENT control construct: backtracking into a ;/-> to try its other branch is
        // flow, not a goal (the branch goals' own redos are what the user sees). This is the
        // "should a redo OF this predicate stop?" question, so it uses the callee check, which
        // refuses both. (Same spirit as the exit port — see MaybeStopAtPort.)
        if (!_engine.IsDebuggableCallee(pc)) return;

        var pred = _engine.PredicateContaining(pc);

        _mode = StepMode.Continue;
        _lastStopDepth = depth;
        _lastStopWasRedo = true;   // a step taken from here is measured against the CALL depth
        _lastStopReason = StopReason.Redo;   // ADR-035 D5+ — SNS refused here
        var site = SiteOf(pc);
        // Name a surviving meta-construct helper for what the user wrote ($findall_N → findall/3,
        // $catchgoal_N → catch/3), as the frame walk does — never the raw lowered helper.
        string redoGoal = CurrentGoal();
        if (pred is { } p)
        {
            var (cn, ca) = PrologEngine.DebugConstructName(p.Name, p.Arity);
            redoGoal = $"{cn}/{ca}";
        }
        _onStop(this, new DebugStopEvent(
            StopReason.Redo,
            redoGoal,
            site.File, site.Line, depth,
            WithEvalBoundary(engine, _engine.CaptureFrames(engine, pc, e, cp))));
    }

    void IDebugSession.OnFail(Activation engine)
        => MaybeStopAtPort(engine, StopReason.Fail);

    void IDebugSession.OnLeaveProlog(Activation engine)
    {
        // Nobody is stepping: there is nothing to abandon, and the fact that the query
        // finished is not news to anyone.
        if (_mode == StepMode.Continue) return;
        _mode = StepMode.Continue;

        // Not a stop to LOOK at — the machine is not in the program, and the frames the
        // debugger would draw are the host's C#. It is a message: the step you are waiting
        // on cannot be satisfied, stop waiting.
        _onStop(this, new DebugStopEvent(
            StopReason.StepAbandoned, "", "", 0, 0,
            Array.Empty<PrologEngine.DebugFrame>()));
    }

    void IDebugSession.MarkHeapRoots(Action<int> markCell) { }
    void IDebugSession.RelocateHeapRoots(Func<int, int> relocIndex)
    {
        // ADR-035 D5+ — a compaction moved the heap and rewrote the trail: every recorded
        // rewind mark indexes a world that no longer exists. Dropping them (rather than
        // relocating trail positions, which the collector does not expose) makes those
        // targets refuse honestly; the GcCount check is the belt to this suspender.
        _portMarks.Clear();
    }

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

    /// <summary>The same depth, counted no further than <paramref name="cap"/> — exact while
    /// it is at most that, and greater than it when it is deeper, which is the only other
    /// thing a step condition asks. See <see cref="Activation.EnvDepthCapped"/>.</summary>
    private static int PortDepthCapped(Activation engine, int cap)
        => engine.EnvDepthCapped(Math.Max(cap, 1)) + 1;

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

        // The first port after an applied Set Next Statement fires AT the moved-to goal —
        // where the arrow already stands. Stopping there would make the user's next step a
        // no-op; the step must EXECUTE that goal. One-shot, see SnsMoveSuppressesStopAt.
        if (SnsMoveSuppressesStopAt(engine)) return;

        // The exit or fail of code the user did not write — the prelude, a library, the top
        // level's own wrapper goals. A port fires there like anywhere else (the interpreter
        // raises one at every proceed, whatever the code was compiled from), and a step that
        // honoured it left the user standing in `$prelude$$attr_goals_of/2` wondering what
        // they had done.
        //
        // Exit and fail only. At a CALL port the machine is still in the CALLER, and the
        // question is about the CALLEE — which OnCallAddress / OnCallFunctor already asked,
        // and had to: the user's own predicate, called from inside maplist/3, is theirs to
        // stop in, however unwritable the caller is.
        if (reason != StopReason.Call && !_engine.IsDebuggableAddress(engine.P)) return;

        // A STEP LANDS ON A GOAL — the next thing the user's program is about to DO — and
        // ONLY on a goal. An EXIT port never stops a step.
        //
        // Both halves were learned from a user's F10. First "step over stops at the end of
        // some other clause": the exit of the goal STEPPED OVER fires with the machine at the
        // callee's proceed, so the caret jumped to the last line of whatever clause succeeded.
        // Then, with only ENCLOSING exits honoured, "F10 on the last goal stays where it is,
        // and Step Out stops on the last goal of the clause I am leaving": the enclosing
        // clause's own exit ALSO shows the callee's last line — the machine is standing there
        // — so the caret did not move. An exit port never points at anything the program is
        // about to do; the call port of the next goal that runs comes right after it and
        // does. However many clause-ends unwind in between, the step lands there.
        //
        // A FAIL is different: a goal that ran out of solutions is the thing that just
        // happened, there is no "next goal" to show instead, and the machine is standing
        // where it failed. It lands like a call.
        if (reason == StopReason.Exit) return;
        bool landing = reason == StopReason.Call || reason == StopReason.Fail;

        // CAPPED, and that is the whole cost of stepping. Every condition below compares the
        // depth against the depth the step was taken from, so a port deeper than that is
        // uninteresting whatever its exact depth — but ASKING costs a walk of the environment
        // chain, and a step over a goal that runs for a while passes millions of ports at
        // whatever depth that goal reaches. Blint: 140 seconds to step over a goal that runs
        // in 20 without a debugger, all of it counting frames nobody asked about. The exact
        // depth is computed once, at a stop, where one walk is nothing.
        int depth = PortDepthCapped(engine, _stepDepth + 1);
        bool stop = _mode switch
        {
            // Into: the next goal, however deep — the first goal of the callee, if it has one.
            StepMode.Into => landing,
            // Over: the next goal at this clause's depth or shallower. Everything the goal we
            // stepped over does inside itself is deeper, and is skipped; if this clause has no
            // next goal, the caller's next goal (depth < ours) is where the program goes.
            StepMode.Over => landing && depth <= _stepDepth,
            // Out: out of the goal we were on entirely — the next goal of an enclosing
            // clause, wherever the unwind lands. From a REDO port the base depth is the
            // retried goal's CALL depth (one shallower than its body), so its continuation
            // sits at that same depth — <= rather than < — see _stepFromRedo.
            StepMode.Out => landing && (_stepFromRedo ? depth <= _stepDepth : depth < _stepDepth),
            _ => false,
        };
        if (!stop) return;

        // Only a call port knows the goal it is about to run; the others are read back
        // off the frame the machine is stopped in.
        Stop(engine, reason, PortDepth(engine), SiteOf(engine.P),
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
        _lastStopWasRedo = false;    // call / breakpoint / fail: depth IS the goal's own
        _lastStopReason = reason;    // ADR-035 D5+ — SNS is refused at redo/fail stops
        Current = engine;

        var frames = WithEvalBoundary(engine, _engine.CaptureFrames(engine));
        // "" is CurrentGoal()'s answer for a port with no callee to name (an inline
        // goal's) — the frame the machine is standing in is the honest name then too.
        if (string.IsNullOrEmpty(goal))
            goal = frames.Count > 0 ? $"{frames[0].Name}/{frames[0].Arity}" : "";

        _onStop(this, new DebugStopEvent(reason, goal, site.File, site.Line, depth, frames)
        {
            BreakFile = _breakRequest?.File ?? "",
            BreakLine = _breakRequest?.Line ?? 0,
            ConditionError = _conditionError,
            // ADR-035 D5+ — the Set Next Statement targets valid HERE, so the debugger's
            // synchronous CanSetNextStatement can accept/refuse a Ctrl+Shift+F10 and name
            // the reason without a func-eval.
            SetNextLines = ValidSetNextLines(),
        });
    }

    /// <summary>ADR-035 D5+ — expose the current stop's valid SNS lines to the channel
    /// session's snapshot writer (CaptureNow builds its own DebugStopEvent).</summary>
    internal IReadOnlyList<int> CurrentValidSetNextLines() => ValidSetNextLines();

    // The breakpoint being reported, for the length of one stop. See OnBreak.
    private (string File, int Line)? _breakRequest;

    /// <summary>ADR-035 — the mixed stack of a stop INSIDE an Immediate-window evaluation:
    /// the evaluated goal's frames, a boundary saying where they came from, and under it
    /// the suspended query the user was stopped in — captured when the evaluation began,
    /// which is exact: the suspended activation cannot move while its thread runs the
    /// eval. (C# shows a bare <c>[Function Evaluation]</c> cut here; the engine knows both
    /// activations, so it shows both stacks.) Also the moment the evaluation's timeout is
    /// disarmed: a goal standing at a breakpoint is the user's, for as long as they
    /// care to look.</summary>
    private IReadOnlyList<PrologEngine.DebugFrame> WithEvalBoundary(
        Activation engine, IReadOnlyList<PrologEngine.DebugFrame> frames)
    {
        if (!_evalActive || ReferenceEquals(engine, _evalOuter)) return frames;
        _evalDisarmTimeout?.Invoke();

        var mixed = new List<PrologEngine.DebugFrame>(
            frames.Count + 1 + _evalOuterFrames.Count);
        mixed.AddRange(frames);
        mixed.Add(new PrologEngine.DebugFrame(
            $"[Immediate: {_evalGoalText}]", -2, "", 0, -1,
            Array.Empty<(string, string)>()));
        mixed.AddRange(_evalOuterFrames);
        return mixed;
    }

    private static string Ellipsize(string text, int max)
        => text.Length <= max ? text : text.Substring(0, max - 3) + "...";

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
