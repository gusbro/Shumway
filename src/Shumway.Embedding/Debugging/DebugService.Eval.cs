using System;
using System.Collections.Generic;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding.Debugging;

public sealed partial class DebugService
{
    private bool SnsMoveSuppressesStopAt(Activation engine)
        => _snsMovedToSite >= 0
           && _engine.SiteAtOrBefore(engine.P) == _snsMovedToSite;

    /// <summary>ADR-035 D5+ — Set Next Statement onto a sibling clause's head: rewind to
    /// the CALLER's mark for the goal that called this predicate, point P at that goal's
    /// site (its argument setup re-runs — it reads only Y slots and heap), and arm the
    /// dispatch intercept so the call enters the CHOSEN clause instead of the predicate's
    /// entry. Committed to that clause — the user picked it; if its head fails to unify,
    /// the call fails. The caller's own stop decisions are suppressed (the arrow shows
    /// the chosen head; the next visible stop is inside the chosen clause).</summary>
    /// <summary>ADR-035 D5+ — the caller anchor a clause re-enter rewinds to: the newest
    /// valid mark recorded in an ANCESTOR environment of the frame being re-entered —
    /// the innermost enclosing USER goal. Deliberately not the next display frame: a
    /// meta-called predicate's direct caller is glue (phrase/2 driving a DCG
    /// nonterminal, call/N — the user's DCG report), prelude code with no sites, no
    /// marks, and often no frame at all (tail-call optimised), which also makes the
    /// display pair its position unreliably. Marks tell the truth: they are recorded
    /// only in debuggable code, stack discipline keeps them consistent, and the newest
    /// one in an ancestor env IS the enclosing user goal whose re-run re-derives the
    /// callee's arguments and re-dispatches the call — where the armed clause entry
    /// takes over.</summary>
    private bool TryFindReenterAnchor(
        Activation outer, int frameIndex,
        out PortMark mark, out int callerEnv, out int callerSiteStart, out string refusal)
    {
        mark = default;
        callerEnv = -1;
        callerSiteStart = -1;
        refusal = "cannot re-enter the call: no enclosing goal with a rewind mark";
        if (!_engine.TryGetDisplayFrameContext(outer, frameIndex, out _, out int frameEnv))
            return false;

        var ancestors = new HashSet<int>();
        bool self = true;
        foreach (int e in outer.EnumerateEnvChain(frameEnv))
        {
            if (self) { self = false; continue; }   // the frame's own env is not a caller
            ancestors.Add(e);
        }
        if (ancestors.Count == 0) return false;

        for (int i = _portMarks.Count - 1; i >= 0; i--)
        {
            var m = _portMarks[i];
            if (!ReferenceEquals(m.Engine, outer) || !ancestors.Contains(m.E)) continue;
            if (!MarkIsValid(outer, m)) continue;
            var sites = _engine.ClauseSites(m.P);
            int start = -1;
            foreach (var (sitePc, _) in sites)
                if (sitePc <= m.P && sitePc > start) start = sitePc;
            if (start < 0) continue;
            mark = m;
            callerEnv = m.E;
            callerSiteStart = start;
            return true;
        }
        return false;
    }

    private string ReenterClause(
        Activation outer, int frameIndex, int targetLine, int framePc, int clauseStart)
    {
        if (!TryFindReenterAnchor(outer, frameIndex,
                out var m, out int callerEnv, out int callerSiteStart, out string refusal))
            return refusal;

        int predAddress = _engine.PredicateAddressOf(framePc);
        if (predAddress < 0)
            return "cannot re-enter the call: the predicate's entry is unknown";
        if (!ArmClauseEntry(outer, predAddress, clauseStart))
            return "cannot re-enter the call: the chosen clause is unknown";

        RestoreMark(outer, m, callerSiteStart);
        outer.SetE(callerEnv);
        _snsMovedToSite = _engine.SiteAt(callerSiteStart);
        _snsApplied = (frameIndex, targetLine, true);
        // The rewind parked the machine at the CALLER's goal: the next step measures
        // against that depth, not the popped frame's (see the cross-frame twin above).
        _lastStopDepth = PortDepth(outer);
        _lastStopWasRedo = false;
        return "";
    }

    /// <summary>Builds and arms the clause-entry function for a re-enter (or a RE-ARM: a
    /// second Set Next Statement onto a different clause head while the first is still
    /// pending — the machine is already parked at the caller's goal, only the choice
    /// changes). The entry function reproduces STANDARD Prolog clause selection from the
    /// chosen clause: enter it with a choice point for the ones AFTER it — if the chosen
    /// clause fails (and did not cut), the following clauses are tried, and when they run
    /// out the call fails to the caller's older alternatives. Built on the
    /// builtin-choice-point machinery (state save/restore identical to a bytecode CP; the
    /// resume delegate re-arms for the clause after and continues at the clause's code),
    /// so it works for every compiled layout — chain and indexed.</summary>
    /// <summary>The synthetic top frame a PENDING re-enter presents: the chosen predicate
    /// at the chosen clause's head line, with no variables (the head has not unified —
    /// showing anything else would be a lie; the machine truthfully sits at the caller's
    /// goal). Read only while <see cref="Activation.DebugClauseEntryArmed"/>.</summary>
    private (string Name, int Arity, string File, int HeadLine) _armedPresentation;

    private bool ArmClauseEntry(Activation outer, int predAddress, int clauseStart)
    {
        var clauseTable = _engine.ClauseHeadTargets(predAddress);
        var clauseCode = new List<int>(clauseTable.Count);
        int chosenIndex = -1;
        int chosenFileId = -1, chosenHeadLine = 0;
        foreach (var (cs, fileId, headLine, _) in clauseTable)
        {
            if (cs == clauseStart)
            {
                chosenIndex = clauseCode.Count;
                chosenFileId = fileId;
                chosenHeadLine = headLine;
            }
            clauseCode.Add(cs);
        }
        if (chosenIndex < 0) return false;
        var pred = _engine.PredicateContaining(predAddress);
        int arity = pred is { } pd ? pd.Arity : 0;
        _armedPresentation = (
            pred is { } pn ? pn.Name : "?", arity,
            chosenFileId >= 0 ? Shumway.Core.DebugSiteTable.FileName(chosenFileId) : "",
            chosenHeadLine);

        Func<Activation, int, bool> resume = null!;
        resume = (eng, k) =>
        {
            if (k >= clauseCode.Count) return false;   // clauses exhausted: fail onward
            if (k + 1 < clauseCode.Count)
                eng.PushIlChoicePoint(resume, k + 1, arity);
            eng.ResumeAtReturnPc(clauseCode[k]);
            return true;
        };

        outer.ArmDebugClauseEntry(predAddress, eng =>
        {
            if (chosenIndex + 1 < clauseCode.Count)
                eng.PushIlChoicePoint(resume, chosenIndex + 1, arity);
            return clauseCode[chosenIndex];
        });
        return true;
    }

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
    /// current stop for one display frame, published in every stop's snapshot so the
    /// debugger can validate a Ctrl+Shift+F10 SYNCHRONOUSLY (its CanSetNextStatement) and
    /// move the arrow / show a reason without a func-eval it cannot make. Top frame:
    /// forward and the current statement always, backward with a live recorded mark. A
    /// LOWER frame: every move rewinds into the frame first, so a target is valid only
    /// through a mark — its own (backward/current) or the frame's current goal's
    /// (forward). The head span maps to the first goal. Empty at a redo/fail stop, with
    /// LCO on, or off a frame with no statement context.</summary>
    public IReadOnlyList<int> ValidSetNextLines(int frameIndex = 0)
    {
        if (Current is not { } outer
            || _lastStopReason is StopReason.Redo or StopReason.Fail
            || outer.LastCallOptimisation)
            return Array.Empty<int>();

        // The PENDING-re-enter synthetic frame: its valid targets are the armed
        // predicate's clause heads — the user may change which clause to enter until
        // they resume. (Single-line clauses count by their one line.)
        if (frameIndex == 0 && outer.DebugClauseEntryArmed)
        {
            var headLines = new SortedSet<int>();
            foreach (var (_, _, headLine, firstLine)
                     in _engine.ClauseHeadTargets(outer.DebugClauseEntryPredicate))
            {
                headLines.Add(headLine);
                for (int hl = headLine; hl < firstLine; hl++) headLines.Add(hl);
            }
            return headLines.ToList();
        }

        if (!_engine.TryGetDisplayFrameContext(outer, frameIndex, out int pc, out int env))
            return Array.Empty<int>();

        var sites = _engine.ClauseSites(pc);
        if (sites.Count == 0) return Array.Empty<int>();
        int currentPc = frameIndex == 0 ? outer.P : pc;
        int currentSiteStart = -1;
        foreach (var (sitePc, _) in sites)
            if (sitePc <= currentPc && sitePc > currentSiteStart) currentSiteStart = sitePc;

        var lines = new SortedSet<int>();
        bool firstGoalReachable = false;
        foreach (var (sitePc, line) in sites)
        {
            bool reachable;
            if (frameIndex == 0)
            {
                // Forward: always. The CURRENT statement: a no-op move, accepted —
                // exactly as C# accepts Set Next Statement to the line the arrow is on.
                // Backward: only with a live recorded mark.
                reachable = sitePc >= currentPc
                    || FindMark(outer, env, sitePc) is not null;
            }
            else
            {
                // A lower frame: the anchor mark decides (see SetNextStatement).
                int anchorPc = sitePc <= currentSiteStart ? sitePc : currentSiteStart;
                reachable = FindMark(outer, env, anchorPc) is not null;
            }
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

        // SIBLING clause heads (the re-enter move): every clause of this frame's
        // predicate is a target when a caller anchor exists — the move rewinds there and
        // re-dispatches the call into the chosen clause. The anchor walk skips meta-call
        // glue (phrase/2 over a DCG, call/N), so DCG nonterminals get their siblings too.
        // A single-line clause (head and body on one line — Blint's
        // `parse_body(F, [])-->parse_token(F).`) counts by that one line: its head span
        // is empty, and leaving it out made clause 1 untargetable.
        if (TryFindReenterAnchor(outer, frameIndex, out _, out _, out _, out _))
            foreach (var (_, _, headLine, firstLine) in _engine.ClauseHeadTargets(pc))
            {
                lines.Add(headLine);
                for (int hl = headLine; hl < firstLine; hl++) lines.Add(hl);
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

    /// <summary>Diagnostic: every recorded mark of the CURRENT stop's activation with its
    /// site/line and, when invalid, which validity leg failed. Test/diag surface only.</summary>
    public string DescribeMarks()
        => Current is { } outer ? DescribeMarks(outer) : "nothing stopped";

    internal string DescribeMarks(Activation outer)
    {
        var text = new System.Text.StringBuilder();
        foreach (var m in _portMarks)
        {
            if (!ReferenceEquals(m.Engine, outer)) continue;
            int site = _engine.SiteAtOrBefore(m.P);
            int line = site >= 0 ? Shumway.Core.DebugSiteTable.Get(site).Line : -1;
            string why = m.GcCount != outer.HeapGcCount ? "gc " : "";
            if (m.BindingTrailTop > outer.BindingTrailTop) why += "btrail ";
            if (m.ExtraTrailTop > outer.ExtraTrailTop) why += "xtrail ";
            if (m.HeapTop > outer.HeapTop) why += "heap ";
            if (!outer.IsChoicePointInChain(m.B)) why += "B-not-in-chain ";
            text.Append($"line {line} P={m.P} E={m.E} B={m.B} bt={m.BindingTrailTop} " +
                $"ht={m.HeapTop} gc={m.GcCount} {(why.Length == 0 ? "OK" : "DEAD: " + why)}\n");
        }
        text.Append($"now: E={outer.E} B={outer.B} bt={outer.BindingTrailTop} " +
            $"ht={outer.HeapTop} gc={outer.HeapGcCount} marks={_portMarks.Count}\n");
        return text.ToString();
    }

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
        _engine.DebugTransplantSource = null;
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
        // SAVE/RESTORE discipline for the transplant source (see ProjectResiduals):
        // a condition can evaluate while an Immediate goal's source is live.
        Activation? savedTransplantSource = _engine.DebugTransplantSource;
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
            List<int>? attVarRoots = null;
            if (_engine.TryGetDisplayFrameContext(engine, 0, out int framePc, out int frameEnv))
            {
                frameModule = _engine.ModuleForFrame(framePc);
                foreach (var (name, value, addr, isAttVar)
                    in _engine.MaterializeFrameVariablesWithAddresses(engine, framePc, frameEnv))
                    if (names.Contains(name))
                    {
                        goal = SubstituteVariable(goal, name, value);
                        if (isAttVar && addr >= 0)
                            (attVarRoots ??= new List<int>()).Add(addr);
                        if (ShumwayDebugHelper.DiagEnabled)
                            ShumwayDebugHelper.DiagLine(
                                "  condition var " + name + " := "
                                + Ellipsize(value.ToString() ?? "", 120));
                    }
            }
            // Attributed frame variables carry their constraints into the condition —
            // same transplant as the Immediate window's (see EvaluateGoal).
            if (attVarRoots is not null
                && _engine.BuildResidualAttrInfo(engine, attVarRoots) is { } transplant)
            {
                goal = new CompoundTerm(",", new Term[]
                {
                    new CompoundTerm("$dbg_attach", new Term[] { transplant.AttrInfo }),
                    goal,
                });
                _engine.DebugTransplantSource = engine;
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
            _engine.DebugTransplantSource = savedTransplantSource;
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
        _snsApplied = default;
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
            WithEvalBoundary(
                engine, AttachResiduals(engine, _engine.CaptureFrames(engine, pc, e, cp)))));
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
    void IDebugSession.RelocateHeapRoots(
        Activation engine, Func<int, int> relocIndex, Func<int, int> relocBoundary)
    {
        // ADR-035 D5+ — a compaction moved the heap: RELOCATE the rewind marks through it
        // rather than dropping them (the original clear made backward Set Next Statement
        // targets vanish after stepping a few goals of any real program — a mid-step GC
        // killed them all). The collection's own guarantees make the remap exact:
        //   - the slide is ORDER-PRESERVING, so a mark's saved allocation point maps
        //     through the forwarding count (relocBoundary) and still separates
        //     before-the-mark cells from after-the-mark cells;
        //   - trailed cells are ROOTS, so no trail entry ever points at a collected cell,
        //     and the trails are relocated in place, never compacted — the saved trail
        //     TOPS stay true exactly as recorded;
        //   - B / B0 / E / P are stack and code positions, untouched by a heap collection.
        // GcCount is refreshed so MarkIsValid keeps accepting the relocated marks. Marks
        // of OTHER activations index other heaps: left alone.
        for (int i = 0; i < _portMarks.Count; i++)
        {
            var m = _portMarks[i];
            if (!ReferenceEquals(m.Engine, engine)) continue;
            _portMarks[i] = m with
            {
                HeapTop = relocBoundary(m.HeapTop),
                GcCount = engine.HeapGcCount,
            };
        }
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

        var frames = WithEvalBoundary(
            engine, AttachResiduals(engine, _engine.CaptureFrames(engine)));
        // "" is CurrentGoal()'s answer for a port with no callee to name (an inline
        // goal's) — the frame the machine is standing in is the honest name then too.
        if (string.IsNullOrEmpty(goal))
            goal = frames.Count > 0 ? $"{frames[0].Name}/{frames[0].Arity}" : "";

        _snsApplied = default;   // a fresh stop: any queued SNS from the last one is history
        _onStop(this, new DebugStopEvent(
            reason, goal!, site.File, site.Line, depth,
            WithSetNextLines(PresentFrames(engine, frames)))
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

    /// <summary>ADR-035 D5+ — prepend the PENDING-re-enter synthetic frame when one is
    /// armed (see <see cref="_armedPresentation"/>): the Call Stack shows the chosen
    /// predicate at the chosen head, Locals show no variables (honest — nothing entered
    /// yet), and the frames below are the real machine (the caller the rewind parked
    /// at). <see cref="PrologEngine.TryGetDisplayFrameContext"/> applies the matching
    /// index shift for every consumer.</summary>
    private IReadOnlyList<PrologEngine.DebugFrame> PresentFrames(
        Activation engine, IReadOnlyList<PrologEngine.DebugFrame> frames)
    {
        if (!engine.DebugClauseEntryArmed) return frames;
        var ap = _armedPresentation;
        var list = new List<PrologEngine.DebugFrame>(frames.Count + 1)
        {
            new(ap.Name, ap.Arity, ap.File, ap.HeadLine, -1,
                Array.Empty<(string, string)>()),
        };
        list.AddRange(frames);
        return list;
    }

    /// <summary>ADR-035 D5+ — decorate each display frame with ITS valid Set Next
    /// Statement lines (cross-frame moves). Capped: a pathological stack does not pay a
    /// per-frame mark scan beyond what a user would ever click. The omitted-frames
    /// sentence and frames with no context answer empty naturally.</summary>
    private IReadOnlyList<PrologEngine.DebugFrame> WithSetNextLines(
        IReadOnlyList<PrologEngine.DebugFrame> frames)
    {
        const int Cap = 64;
        var result = new List<PrologEngine.DebugFrame>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
            result.Add(i < Cap
                ? frames[i] with { SetNextLines = ValidSetNextLines(i) }
                : frames[i]);
        return result;
    }

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
