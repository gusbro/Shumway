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
public sealed partial class DebugService : IDebugSession
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

        var frames = PresentFrames(engine, AttachResiduals(engine, _engine.CaptureFrames(engine)));
        string goal = frames.Count > 0 ? $"{frames[0].Name}/{frames[0].Arity}" : CurrentGoal();
        // A pending re-enter's stop presents at the chosen head, not the parked caller.
        var site = engine.DebugClauseEntryArmed
            ? (_armedPresentation.File, _armedPresentation.HeadLine)
            : SiteOf(engine.P);
        return new DebugStopEvent(
            StopReason.AsyncBreak, goal, site.Item1, site.Item2, PortDepth(engine),
            WithSetNextLines(frames))
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

    /// <summary>Hidden variables smuggling the residual-constraint projection out of a
    /// wrapped evaluation goal (the REPL's QueryWrapper recipe), plus the display-name
    /// pairs the renderer needs. Null when the goal named no variables.</summary>
    private const string EvalCopiesVar = "_DbgEvalCopies_8f2c";
    private const string EvalResidualsVar = "_DbgEvalResiduals_8f2c";
    private List<(string Display, string Var)>? _pendingResidVars;

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

        // A leading '!' runs the goal ON the suspended activation itself — frame
        // variables are the REAL cells, so a posted constraint narrows them and a
        // binding sticks, with once-semantics and Prolog's own trail as the
        // transaction: failure (append `, fail` for a dry run) or an error
        // unwinds to the entry marks and the frame is untouched. Side effects
        // (assertz, output) follow normal Prolog rules and do not undo.
        if (trimmed.StartsWith("!", StringComparison.Ordinal))
        {
            string rest = trimmed.Substring(1).Trim();
            if (rest.Length == 0 || rest == ".")
                return "prefix a goal with ! to run it on the real frame, e.g. !X #> 5.";
            return EvaluateGoalOnFrame(outer, frameIndex, rest);
        }

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
        List<int>? attVarRoots = null;
        // Display-name -> goal-variable-name pairs for the residual projection:
        // every variable the answer could talk about, under the name the USER
        // typed (a frame variable substitutes as _G<addr>, but the answer must
        // say `A in 6..9`, not `_G123 in 6..9`).
        var residVars = new List<(string Display, string Var)>();
        if (_engine.TryGetDisplayFrameContext(outer, frameIndex, out int pc, out int env))
        {
            frameModule = _engine.ModuleForFrame(pc);
            foreach (var (name, value, addr, isAttVar)
                in _engine.MaterializeFrameVariablesWithAddresses(outer, pc, env))
            {
                if (!names.Contains(name)) continue;
                goal = SubstituteVariable(goal, name, value);
                substituted.Add(name);
                if (value is VarTerm frameVt) residVars.Add((name, frameVt.Name));
                if (value is VarTerm vt && addr >= 0)
                {
                    if (isAttVar)
                    {
                        commitRefusal = " [" + name + " is an attributed variable: "
                            + "evaluated on a copy — prefix the goal with ! to bind or "
                            + "post on the real frame]";
                        (attVarRoots ??= new List<int>()).Add(addr);
                    }
                    else
                        (commit ??= new List<CommitVar>()).Add(new CommitVar(name, vt.Name, addr));
                }
            }
        }

        // An ATTRIBUTED frame variable substitutes as a bare _G<addr> variable — the
        // goal's materialisation gives it a fresh cell with NO attributes, so
        // get_attr/3, copy_term/3, frozen/1 or posting a new constraint on it silently
        // saw an unconstrained variable. The transplant (see AttachResiduals) fixes
        // that: the suspended variable's attribute graph is rebuilt as ag(M, A, V)
        // triples over the same _G names and '$dbg_attach'/1 reattaches them before the
        // user's goal runs — in the EVAL activation only, the suspended machine
        // untouched. Constraints the goal posts live and die with the evaluation.
        if (attVarRoots is not null
            && _engine.BuildResidualAttrInfo(outer, attVarRoots) is { } transplant)
        {
            goal = new CompoundTerm(",", new Term[]
            {
                new CompoundTerm("$dbg_attach", new Term[] { transplant.AttrInfo }),
                goal,
            });
            // '$dbg_fix_foreign' reads per-activation FOREIGN payloads off the
            // suspended activation; cleared with the evaluation's other state in
            // AbandonPendingEvaluation.
            _engine.DebugTransplantSource = outer;
        }
        var report = new List<string>();
        foreach (string n in names)
            if (!substituted.Contains(n) && !report.Contains(n)) report.Add(n);

        // Residual display, the REPL's recipe (QueryWrapper/SolutionFormatter):
        // conjoin copy_term/3 over every visible variable, so a constraint
        // library projects the residual goals the answer should show —
        // `A in 6..9` after `A #> 5` instead of a silent `true`.
        foreach (string n in report) residVars.Add((n, n));
        if (residVars.Count > 0)
        {
            Term varsList = new AtomTerm("[]");
            for (int i = residVars.Count - 1; i >= 0; i--)
                varsList = new CompoundTerm(".", new Term[]
                    { new VarTerm(residVars[i].Var), varsList });
            goal = new CompoundTerm(",", new Term[]
            {
                goal,
                new CompoundTerm("copy_term", new Term[]
                {
                    varsList,
                    new VarTerm(EvalCopiesVar),
                    new VarTerm(EvalResidualsVar),
                }),
            });
        }
        _pendingResidVars = residVars.Count > 0 ? residVars : null;
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
        _evalOuterFrames = AttachResiduals(outer, _engine.CaptureFrames(outer));
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
                        : Ellipsize(AstTermRenderer.Render(
                            value, 999, _engine.Operators, quoted: true), 2048));
                }
                rendered = text.ToString();
            }
            // The residual constraints the solution left on the goal's variables
            // — `A in 6..9` after a post — appended under the bindings, in the
            // user's own variable names.
            if (_pendingResidVars is { } residVars)
            {
                string residLines = RenderEvalResiduals(_pendingEnum!.Current, residVars);
                if (residLines.Length > 0)
                    rendered = rendered == "true"
                        ? residLines
                        : rendered + ",\n" + residLines;
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
                    TermReader.Materialize(outer, after), 999, _engine.Operators, quoted: true);
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

    /// <summary>ADR-035 D5+ — the Watch-window EDIT of a frame variable, and it is
    /// DESTRUCTIVE by design (the user's spec; the Immediate window deliberately keeps
    /// pure unification): a bound variable's value is REPLACED — the old binding is
    /// trailed away (so backtracking and a rewind restore it) and the new term unified
    /// in — and assigning <c>_</c> UN-instantiates it. The new term may reference the
    /// frame's other variables by name (X = f(Y) aliases the real Y). Transactional: a
    /// failed unification leaves the frame exactly as it was. Returns "" or the
    /// refusal.</summary>
    public string SetFrameVariable(int frameIndex, string varName, string termText)
    {
        if (Current is not { } outer) return "nothing is stopped";
        if (!_engine.TryGetDisplayFrameContext(outer, frameIndex, out int pc, out int env))
            return "this frame has no variable context";

        int addr = -1;
        bool isAttVar = false;
        bool found = false;
        var shared = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, _, a, att) in
                 _engine.MaterializeFrameVariablesWithAddresses(outer, pc, env))
        {
            if (a >= 0) shared.TryAdd(name, a);
            if (name == varName) { addr = a; isAttVar = att; found = true; }
        }
        if (!found) return "no variable '" + varName + "' in this frame";
        if (isAttVar)
            return "'" + varName + "' is an attributed variable; editing it would "
                + "schedule unification hooks the suspended machine cannot run";
        if (addr < 0)
            return "'" + varName + "' has no editable cell in this frame";

        Term newTerm;
        try
        {
            // The Watch edit box holds a bare term; the parser wants a clause-terminated
            // one. The SPACE before the period matters: "7." would lex as a float.
            string text = termText.Trim();
            if (!text.EndsWith(".", StringComparison.Ordinal)) text += " .";
            (newTerm, _) = _engine.ParseGoal(text);
        }
        catch (Exception ex)
        {
            return "cannot parse '" + termText + "': " + ex.Message;
        }

        int bMark = outer.BindingTrailTop;
        int eMark = outer.ExtraTrailTop;
        int hMark = outer.HeapTop;

        // `_` — un-instantiate: clear the binding, trailing the old value. (A no-op on a
        // variable that is already free.)
        if (newTerm is VarTerm { Name: "_" })
        {
            if (IsUnboundAt(outer, addr)) return "";
            outer.DebugUnbindCell(addr);
            FrameStateChanged = true;
            return "";
        }

        try
        {
            // A bound target is CLEARED first — that is what makes the edit an edit and
            // not a unification test. The clear is trailed, so the transactional unwind
            // below (and any later backtrack past this point) restores the original.
            if (!IsUnboundAt(outer, addr))
                outer.DebugUnbindCell(addr);
            Cell built = Materializer.MaterializeAsCellSharing(outer, newTerm, shared);
            if (outer.Unify(addr, HeapAddrOf(outer, built)))
            {
                FrameStateChanged = true;
                return "";
            }
        }
        catch (Exception ex)
        {
            outer.UnwindTrails(bMark, eMark);
            outer.SetHeapTop(hMark);
            return "cannot set '" + varName + "': " + ex.Message;
        }

        outer.UnwindTrails(bMark, eMark);
        outer.SetHeapTop(hMark);
        return "the new value does not unify against the frame's other bindings; "
            + "nothing was changed";
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
    private const int PortMarkCapacity = 8192;

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

        // Only ports in the USER'S code leave marks: a mark is a Set Next Statement
        // rewind target, and a target is a statement of a debuggable clause. Prelude and
        // library internals fire ports too (every call does under a session), and a deep
        // library recursion — numlist building a 400k list is ONE goal — recorded
        // hundreds of thousands of useless marks, flooding the capacity and evicting the
        // user's few precious ones.
        if (!_engine.IsDebuggableAddress(engine.P)) return;

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

    /// <summary>One-shot per stop: a Set Next Statement that has ALREADY been applied at
    /// this stop (the eager Locals-refresh func-eval) must not be applied again by the
    /// resume drain's copy of the same command. Idempotence by re-running is not enough
    /// cross-frame: the first apply pops frames, the display indices shift, and the
    /// re-run would resolve "frame N" against a different frame — under recursion, one
    /// whose clause happily accepts the same line, rewinding TWICE.</summary>
    private (int Frame, int Line, bool Set) _snsApplied;

    public string SetNextStatement(int frameIndex, int targetLine)
    {
        if (Current is not { } outer) return "nothing is stopped";
        if (frameIndex < 0) return "not a Prolog frame";
        if (_snsApplied.Set && _snsApplied.Frame == frameIndex && _snsApplied.Line == targetLine)
            return "";   // the same move, already applied at this stop
        if (_lastStopReason is StopReason.Redo or StopReason.Fail)
            return "Set Next Statement is not available at a redo/fail stop "
                + "(the machine is mid-backtrack)";
        if (outer.LastCallOptimisation)
            return "Set Next Statement needs last-call optimisation off "
                + "(set_prolog_flag(debug_lco, off))";

        // A pending re-enter holds the machine parked at the caller's goal — the entered
        // predicate is no longer any display frame, but its clause heads REMAIN targets:
        // the user may change which clause to enter any number of times before resuming
        // (the report: SNS onto clause 3's head, then — without continuing — back onto
        // clause 2's, refused). A re-target just replaces the armed choice.
        if (outer.DebugClauseEntryArmed && frameIndex == 0)
        {
            int armedPred = outer.DebugClauseEntryPredicate;
            foreach (var (cs, _, headLine, firstLine) in _engine.ClauseHeadTargets(armedPred))
                if (targetLine == headLine
                    || (targetLine >= headLine && targetLine < firstLine))
                {
                    if (!ArmClauseEntry(outer, armedPred, cs))
                        return "cannot re-enter the call: the chosen clause is unknown";
                    _snsApplied = (frameIndex, targetLine, true);
                    return "";
                }
        }

        if (!_engine.TryGetDisplayFrameContext(outer, frameIndex, out int pc, out int env))
            return "this frame has no statement context";

        var sites = _engine.ClauseSites(pc);
        if (sites.Count == 0) return "this clause has no statement positions";

        // The frame's own position: for the top frame the machine's P; for a lower frame
        // the call site its display frame stands on (the goal whose callees are the
        // frames above it).
        int currentPc = frameIndex == 0 ? outer.P : pc;

        int currentSite = _engine.SiteAtOrBefore(currentPc);
        var currentInfo = currentSite >= 0
            ? Shumway.Core.DebugSiteTable.Get(currentSite) : default;

        // The head span means "restart the clause body": rewind to the FIRST goal's mark.
        // Head-unification bindings are BELOW that mark, so they survive — the same
        // meaning C#'s Set Next Statement to a method's first line has (parameters keep
        // their values).
        var span = _engine.ClauseLineSpan(currentInfo.FileId, targetLine);
        if (span is { } s && targetLine >= s.HeadLine && targetLine < s.FirstLine
            && s.FirstLine == sites[0].Line)
            targetLine = sites[0].Line;

        // Resolve the target line to a site of THIS clause.
        int targetPc = -1;
        foreach (var (sitePc, line) in sites)
            if (line == targetLine) { targetPc = sitePc; break; }
        if (targetPc < 0)
        {
            // A SIBLING clause's head (ADR-035 D5+, the user's case): standing in clause
            // N of Pred, Set Next Statement onto the head of ANY clause of Pred re-enters
            // the CALL by that clause — rewind to the caller's goal, re-run its argument
            // setup, and dispatch straight into the chosen clause.
            foreach (var (clauseStart, _, headLine, firstLine) in _engine.ClauseHeadTargets(pc))
                if (targetLine == headLine
                    || (targetLine >= headLine && targetLine < firstLine))
                    return ReenterClause(outer, frameIndex, targetLine, pc, clauseStart);

            return "line " + targetLine + " is not a statement of this clause; "
                + "statements are at: " + string.Join(", ",
                    sites.Select(x => x.Line).Distinct().OrderBy(x => x));
        }

        // The start of the site the frame stands on (currentPc may point mid-site).
        int currentSiteStart = -1;
        foreach (var (sitePc, _) in sites)
            if (sitePc <= currentPc && sitePc > currentSiteStart) currentSiteStart = sitePc;

        if (frameIndex > 0)
        {
            // CROSS-FRAME (ADR-035 D5+, the user's generalization): Set Next Statement on
            // a LOWER frame of the call stack. Every such move first rewinds INTO that
            // frame — the callee frames above it are popped by restoring one of ITS
            // recorded marks (B discards the callees' choice points, the trails undo
            // their bindings, E returns to the frame's own environment) — and from there
            // the move is the ordinary top-frame algorithm. The frame's marks survived
            // the callees by stack discipline: only DEEPER marks are purged as ports
            // fire.
            //
            // Backward or current: the target site's own mark is the rewind. Forward: the
            // rewind is to the frame's CURRENT goal (the call the frames above came
            // from), then a pure move forward — recording marks for the skipped sites,
            // same as the top frame.
            int anchorPc = targetPc <= currentSiteStart ? targetPc : currentSiteStart;
            var frameMark = FindMark(outer, env, anchorPc);
            if (frameMark is not { } fm)
            {
                var okLines = AcceptableRewindLines(outer, env);
                return "cannot rewind into this frame at line " + targetLine
                    + (okLines.Count > 0
                        ? "; rewindable lines are: " + string.Join(", ", okLines)
                        : "; no rewindable position is recorded for this frame");
            }

            RestoreMark(outer, fm, targetPc);
            outer.SetE(env);
            outer.DisarmDebugClauseEntry();   // any other move cancels a pending re-enter
            if (targetPc > currentSiteStart)
                foreach (var (sitePc, _) in sites)
                    if (sitePc >= currentSiteStart && sitePc < targetPc)
                        RecordPureMoveMark(outer, env, sitePc);
            _snsMovedToSite = _engine.SiteAt(targetPc);
            _snsApplied = (frameIndex, targetLine, true);
            // The move POPPED frames: the depth the next step measures against is the
            // moved-to frame's, not the stop's (a stale deep reference made the first
            // F10 after a cross-frame move accept every port inside the next callee —
            // Step Over behaved as Step Into, once).
            _lastStopDepth = PortDepth(outer);
            _lastStopWasRedo = false;
            return "";
        }

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
            foreach (var (sitePc, _) in sites)
                if (sitePc >= currentSiteStart && sitePc < targetPc)
                    RecordPureMoveMark(outer, env, sitePc);

            outer.DisarmDebugClauseEntry();   // any other move cancels a pending re-enter
            outer.RedirectPc(targetPc);
            _snsMovedToSite = _engine.SiteAt(targetPc);
            _snsApplied = (frameIndex, targetLine, true);
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

        outer.DisarmDebugClauseEntry();   // any other move cancels a pending re-enter
        RestoreMark(outer, mark, targetPc);
        _snsMovedToSite = _engine.SiteAt(targetPc);
        _snsApplied = (frameIndex, targetLine, true);
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

    /// <summary>The Immediate window's <c>!goal</c>: runs the goal ON the suspended
    /// activation via the re-entrant solve (the SolveOnce machinery — a nested
    /// once-semantics Dispatch on the live machine, register-transparent). Frame
    /// variables resolve to their REAL heap cells through the sharing materializer, so
    /// a posted constraint narrows the frame's own variable and a binding sticks —
    /// trailed, so a later backtrack of the program past this point undoes it exactly
    /// as if the program had posted it here itself. Failure or an error unwinds to the
    /// entry marks: the frame is untouched (which makes <c>!(G, fail)</c> the free
    /// dry-run). No timeout: on-frame execution is an explicit request on the real
    /// machine.</summary>
    private string EvaluateGoalOnFrame(Activation outer, int frameIndex, string goalText)
    {
        Term goal;
        IReadOnlyList<string> names;
        try
        {
            string text = goalText;
            if (!text.EndsWith(".", StringComparison.Ordinal)) text += ".";
            (goal, names) = _engine.ParseGoal(text);
        }
        catch (Exception ex)
        {
            return "syntax error: " + ex.Message;
        }

        var solve = outer.ReentrantSolve;
        if (solve is null)
            return "the stopped activation cannot run a nested goal here";

        // Frame variables: a free (or attributed) one seeds the sharing map by its
        // heap ADDRESS — real sharing, the whole point of '!'; a bound one whose
        // address is unavailable substitutes as its value (plain data either way).
        string? frameModule = null;
        var shared = new Dictionary<string, int>();
        var frameNames = new HashSet<string>(StringComparer.Ordinal);
        if (_engine.TryGetDisplayFrameContext(outer, frameIndex, out int framePc, out int frameEnv))
        {
            frameModule = _engine.ModuleForFrame(framePc);
            foreach (var (name, value, addr, _)
                in _engine.MaterializeFrameVariablesWithAddresses(outer, framePc, frameEnv))
            {
                if (!names.Contains(name)) continue;
                frameNames.Add(name);
                if (addr >= 0) shared[name] = addr;
                else goal = SubstituteVariable(goal, name, value);
            }
        }
        goal = _engine.ResolveGoalModule(goal, frameModule);

        // The transaction marks. Success KEEPS bindings (they are trailed against the
        // program's own older choice points); failure and error restore everything.
        int savedB = outer.B, savedB0 = outer.B0, savedH = outer.HeapTop;
        int savedBindingTop = outer.BindingTrailTop, savedExtraTop = outer.ExtraTrailTop;
        bool ok;
        try
        {
            Cell goalCell = Materializer.MaterializeAsCellSharing(outer, goal, shared);
            ok = solve(goalCell);
        }
        catch (Exception ex)
        {
            RestoreOnFrameMarks(outer, savedB, savedB0, savedBindingTop, savedExtraTop, savedH);
            return "error: " + ex.Message + " [frame restored]";
        }
        if (!ok)
        {
            RestoreOnFrameMarks(outer, savedB, savedB0, savedBindingTop, savedExtraTop, savedH);
            return "false [frame unchanged]";
        }

        // The suspended state visibly changed: the front ends re-pull frames, locals
        // and constraints (same signal as Set Next Statement / a committed edit).
        FrameStateChanged = true;

        var parts = new List<string>();
        foreach (var kv in shared)
        {
            if (frameNames.Contains(kv.Key)) continue;   // frame vars: Locals shows them
            parts.Add(kv.Key + " = " + AstTermRenderer.Render(
                TermReader.Materialize(outer, kv.Value), 999, _engine.Operators, quoted: true));
        }
        string result = (parts.Count == 0 ? "true" : string.Join(",\n", parts))
            + " [applied to the frame]";

        // The residual constraints the goal left ON THE FRAME's variables —
        // the same projection the Constraints view runs, rendered under the
        // user's names so `!X in 1..8` answers with the resulting `X in 6..8`.
        if (shared.Count > 0
            && ProjectResiduals(outer, shared.Values.ToList()) is { } projected)
        {
            var addrToDisplay = new Dictionary<int, string>();
            foreach (var kv in shared) addrToDisplay[kv.Value] = kv.Key;
            var renames = new Dictionary<string, string>();
            foreach (var kv in projected.AddrToCopyName)
                if (addrToDisplay.TryGetValue(kv.Key, out string? display))
                    renames[kv.Value] = display;
            var residLines = new List<string>();
            foreach (var goals in projected.ByOwner.Values)
                foreach (Term g in goals)
                    residLines.Add(Ellipsize(AstTermRenderer.Render(
                        ResidualProjection.SubstituteVarNames(g, renames),
                        999, _engine.Operators, quoted: true), 512));
            if (residLines.Count > 0)
                result += "\n" + string.Join(",\n", residLines);
        }
        return result;
    }

    private static void RestoreOnFrameMarks(
        Activation outer, int b, int b0, int bindingTop, int extraTop, int heapTop)
    {
        outer.SetB(b);
        outer.UnwindTrails(bindingTop, extraTop);
        outer.SetHeapTop(heapTop);
        outer.SetB0(b0);
    }

    /// <summary>Renders the residual goals a wrapped evaluation projected
    /// (<see cref="EvalResidualsVar"/>), with the copy variables mapped back to
    /// the names the user typed — the lean port of the REPL's
    /// SolutionFormatter naming walk. Empty string when nothing residual.</summary>
    private string RenderEvalResiduals(
        Solution solution, List<(string Display, string Var)> residVars)
    {
        // Copy-name -> display name: the copies list is aligned with residVars;
        // then each copy is walked against the value it was copied from so a
        // name nested inside a larger value still resolves.
        var copyToDisplay = new Dictionary<string, string>();
        var copies = new List<Term>();
        Term cursor = solution[EvalCopiesVar] ?? new AtomTerm("[]");
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
        {
            copies.Add(cons.Args[0]);
            cursor = cons.Args[1];
        }
        for (int i = 0; i < copies.Count && i < residVars.Count; i++)
            if (copies[i] is VarTerm cv) copyToDisplay.TryAdd(cv.Name, residVars[i].Display);
        for (int i = 0; i < copies.Count && i < residVars.Count; i++)
            ResidualProjection.MapCopyNames(
                copies[i], solution[residVars[i].Var], residVars[i].Display, copyToDisplay);
        // An unbound goal variable's engine name also reads as the user's name.
        foreach (var (display, varName) in residVars)
            if (solution[varName] is VarTerm ov) copyToDisplay.TryAdd(ov.Name, display);

        var parts = new List<string>();
        Term resCursor = solution[EvalResidualsVar] ?? new AtomTerm("[]");
        while (resCursor is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
        {
            parts.Add(Ellipsize(AstTermRenderer.Render(
                ResidualProjection.SubstituteVarNames(cons.Args[0], copyToDisplay),
                999, _engine.Operators, quoted: true), 512));
            resCursor = cons.Args[1];
        }
        return parts.Count == 0 ? "" : string.Join(",\n", parts);
    }
}
