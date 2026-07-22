using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>ADR-035 — the debug session every query's activation is born
    /// with, or <c>null</c> (the normal case) for an undebugged engine. Set by
    /// <see cref="SetTracing"/> today; a Concord debug session will set it
    /// too.</summary>
    internal Shumway.Core.IDebugSession? DebugSession { get; private set; }

    /// <summary>True while a four-port tracer is attached (<c>trace/0</c>).</summary>
    public bool Tracing => DebugSession is Debugging.DebugTracer;

    /// <summary>ADR-035 — the goal the running query was set up from, as the user would
    /// read it back. The <c>__query__</c> wrapper is compiled, not written, so nothing
    /// downstream of compilation knows what it says; a call stack whose top frame is a bare
    /// <c>?-</c> tells the user nothing. Captured at query setup (debug sessions only —
    /// rendering a term is not free, and an undebugged engine has no one to tell).</summary>
    internal string? CurrentQueryText { get; private set; }

    /// <summary>What to CALL the running query in a debugger, when the goal the engine was
    /// handed is not the goal the user typed. A top level wraps what you type (Shumway's own
    /// wraps it in a <c>copy_term/3</c> so it can show residual constraints), and rendering
    /// that back produced a frame named after machinery the user never wrote. Set it to what
    /// they typed; the engine falls back to rendering the goal when it is not set.</summary>
    public string? QueryLabel { get; set; }

    /// <summary>Turns the four-port tracer on or off (the engine side of
    /// <c>trace/0</c> / <c>notrace/0</c>). Port lines go to
    /// <paramref name="output"/>, defaulting to the engine's own output sink so
    /// a hosted engine's trace lands wherever its program's writes do.</summary>
    public void SetTracing(bool on, System.IO.TextWriter? output = null)
        => DebugSession = on ? new Debugging.DebugTracer(this, output ?? Out) : null;

    // ----- ADR-035: breakpoints -----
    //
    // The armed SOURCE SITES are the truth. The byte patches in the program are
    // derived from them, and are re-derived whenever the code space changes (a
    // relink, a compaction, a consult) — which is why a breakpoint set once keeps
    // working across queries instead of pointing at whatever moved into its old
    // address.

    private readonly HashSet<int> _breakpointSites = new();
    private readonly Dictionary<int, byte> _breakpointPatches = new();
    private HashSet<int> _compiledSites = new();
    private byte[]? _patchedProgram;

    /// <summary>ADR-035 — the source sites currently armed
    /// (<see cref="Shumway.Core.DebugSiteTable"/> ids).</summary>
    public IReadOnlyCollection<int> Breakpoints => _breakpointSites;

    /// <summary>ADR-035 — arms every stop site on this source line, and returns how
    /// many bound. Zero means the breakpoint cannot bind ANYWHERE below the line: the
    /// file has no code left — or it belongs to a predicate compiled without debug
    /// (<c>:- disable_debug.</c>), or the program is not debug-compiled at all. A
    /// debugger renders that as a hollow breakpoint rather than pretending it took.
    ///
    /// <para>A line with no stop site of its own — blank, a comment, or a rule's head,
    /// whose "clause entered" point IS its first goal's — snaps FORWARD to the next
    /// line that has one, which is what every debugger does with a breakpoint set on a
    /// line that is not code. <see cref="BoundLine"/> reports where it landed.</para>
    ///
    /// <para>Binding is decided against THIS engine's compiled code, not against the
    /// global site table: the table is process-wide, so two engines that both
    /// consulted a string source share its site ids, and only the code that is
    /// actually loaded here can be stopped in. Forces the code space to link if it
    /// has not yet, since before that there is nothing to answer with.</para></summary>
    public int AddBreakpoint(string file, int line) => AddBreakpoint(file, line, null);

    /// <summary>ADR-035 D5 — a CONDITIONAL breakpoint: <paramref name="condition"/> is a
    /// Prolog goal evaluated when the breakpoint is reached, in the frame it fired in
    /// (its variables substituted by name, like the Immediate window's). The breakpoint
    /// stops only when the goal SUCCEEDS; a goal that fails lets the program run on as if
    /// the breakpoint were not there. A condition that cannot run — a syntax error, an
    /// exception, a timeout — STOPS and says why (see
    /// <see cref="DebugStopEvent.ConditionError"/>): a broken condition that silently
    /// swallowed its breakpoint would be undiagnosable. Null (the 2-arg overload) means
    /// unconditional — and REPLACES any previous condition on this breakpoint, because
    /// the debugger writes its whole desired state each time.</summary>
    public int AddBreakpoint(string file, int line, string? condition)
    {
        // Serialized against query setup — the session's idle watcher calls this from
        // its own thread, and F9 landing exactly as a query starts raced the setup's
        // table rebuild. See SetupQueryFromTerm.
        lock (_debugArmGate)
        {
            // Remembered whether it binds or not. A breakpoint set on a file that has not
            // been consulted YET is the normal case under a launch — the user draws it,
            // then starts the program — and binding it against code that does not exist is
            // not possible, so for want of this line it was quietly dropped and the program
            // ran clean through every breakpoint in it. It binds in
            // RebindPendingBreakpoints, when the code arrives.
            _requestedBreakpoints.Add((file, line));
            if (string.IsNullOrWhiteSpace(condition))
                _breakpointConditions.Remove((file, line));
            else
                _breakpointConditions[(file, line)] = condition!;

            EnsureCodeLinked();
            int fileId = Shumway.Core.DebugSiteTable.InternFile(file);

            int target = SnapToCompiledLine(fileId, line);
            if (target < 0) return 0;

            int bound = 0;
            foreach (int id in Shumway.Core.DebugSiteTable.SitesOnLine(fileId, target))
            {
                if (!_compiledSites.Contains(id)) continue;
                _breakpointSites.Add(id);
                _breakpointRequests[id] = (fileId, line);
                bound++;
            }
            if (bound > 0) RefreshBreakpoints();
            return bound;
        }
    }

    // Which breakpoint each armed site belongs to — the line the USER asked for, which
    // is not always the line the code is on (a breakpoint on a rule's head binds at its
    // first goal). A debugger that has to match a hit back to the red dot it drew needs
    // the line it drew, not the line we bound.
    private readonly Dictionary<int, (int FileId, int Line)> _breakpointRequests = new();

    // Every breakpoint the debugger has ASKED for, bound or not. The armed sites above are
    // derived from these; these are the truth.
    private readonly HashSet<(string File, int Line)> _requestedBreakpoints = new();

    // ADR-035 D5 — the condition each requested breakpoint carries, keyed like the request.
    // Absent = unconditional (the ordinary case pays one failed lookup per hit, nothing more).
    private readonly Dictionary<(string File, int Line), string> _breakpointConditions = new();

    /// <summary>ADR-035 D5 — the condition of the breakpoint armed at this address, or null
    /// when the breakpoint is unconditional (or the stop is not a breakpoint's). Looked up
    /// through the same site→request map a hit is reported through, so the condition follows
    /// the breakpoint wherever its line actually bound.</summary>
    internal string? BreakpointConditionAt(int pc)
    {
        int siteId = SiteAt(pc);
        if (siteId >= 0 && _breakpointRequests.TryGetValue(siteId, out var request)
            && _breakpointConditions.TryGetValue(
                (Shumway.Core.DebugSiteTable.FileName(request.FileId), request.Line),
                out string? condition))
            return condition;
        return null;
    }

    /// <summary>ADR-035 D4 — binds the breakpoints that had nowhere to bind. Called when new
    /// code arrives (a consult), which is the moment a breakpoint set on a file that had not
    /// been loaded yet finally has something to attach to.
    ///
    /// <para>This is what makes a LAUNCH work at all: the user draws the red dot, presses the
    /// button, and the file is consulted afterwards. Without it the breakpoint is asked for
    /// against an empty program, binds nothing, and is forgotten — and the program runs to
    /// completion untouched, which is exactly what it did.</para></summary>
    private void RebindPendingBreakpoints()
    {
        if (_requestedBreakpoints.Count == 0) return;

        // Relink first. The set of sites a breakpoint may bind to (_compiledSites) is rebuilt
        // when a query is SET UP, not when a file is consulted — so right after a consult it
        // still describes the program as it was before, and the clauses that just arrived are
        // invisible. EnsureCodeLinked would not do it: it sees a linked code space and returns
        // happy. A trivial query is what actually rebuilds the map.
        foreach (var _ in QueryAll("true.")) break;

        // A copy: AddBreakpoint writes to the set (idempotently — it is a set), and
        // enumerating a collection one is adding to is not allowed even when nothing changes.
        // The rebind re-asks for the breakpoint AS THE USER SET IT — condition included; the
        // 2-arg overload would silently strip it.
        foreach ((string file, int line) in _requestedBreakpoints.ToArray())
            AddBreakpoint(file, line,
                _breakpointConditions.TryGetValue((file, line), out string? c) ? c : null);
    }

    /// <summary>ADR-035 — the breakpoint (as the user asked for it) armed at this
    /// address, or null if the stop is not one.</summary>
    internal (string File, int Line)? BreakpointRequestAt(int pc)
    {
        int siteId = SiteAt(pc);
        if (siteId >= 0 && _breakpointRequests.TryGetValue(siteId, out var request))
            return (Shumway.Core.DebugSiteTable.FileName(request.FileId), request.Line);
        return null;
    }

    /// <summary>ADR-035 — the line an <see cref="AddBreakpoint"/> for this line would
    /// actually bind at, or -1 if none. A debugger moves the red dot there.</summary>
    public int BoundLine(string file, int line)
    {
        EnsureCodeLinked();
        return SnapToCompiledLine(Shumway.Core.DebugSiteTable.InternFile(file), line);
    }

    // The source span of every debuggable clause: where its head is written, and the
    // first and last lines it can be stopped at. Built alongside _stopPcs.
    private readonly List<(int FileId, int HeadLine, int FirstLine, int LastLine)>
        _clauseLines = new();

    /// <summary>ADR-035 — the line a breakpoint on <paramref name="line"/> binds at, or
    /// -1 for a hollow one.
    ///
    /// <para>A line with a stop site binds where it is. A line without one binds forward
    /// to the next site OF THE CLAUSE IT IS IN — which is how a breakpoint on a rule's
    /// head (whose entry point IS its first goal's) or on a blank line inside a body
    /// finds its code. It does NOT wander past the end of that clause: a breakpoint on a
    /// blank line between predicates, or inside a <c>:- disable_debug.</c> region, has
    /// nothing to bind to, and saying so is better than silently arming a line the user
    /// was not looking at.</para></summary>
    private int SnapToCompiledLine(int fileId, int line)
    {
        foreach (int id in _compiledSites)
        {
            var site = Shumway.Core.DebugSiteTable.Get(id);
            if (site.FileId == fileId && site.Line == line) return line;
        }

        int best = -1;
        foreach (var clause in _clauseLines)
        {
            if (clause.FileId != fileId) continue;
            if (line < clause.HeadLine || line > clause.LastLine) continue;
            int target = FirstSiteLineAtOrAfter(fileId, Math.Max(line, clause.FirstLine));
            if (target > 0 && (best < 0 || target < best)) best = target;
        }
        return best;
    }

    /// <summary>ADR-035 D5+ (Set Next Statement) — every stop site of the clause that
    /// contains <paramref name="pc"/>, as (site pc, source line): the positions the
    /// next-statement pointer can point at. Empty when the pc names no debuggable
    /// clause.</summary>
    internal IReadOnlyList<(int Pc, int Line)> ClauseSites(int pc)
    {
        if (_clauseStarts.Length == 0 || _stopPcs.Length == 0 || pc < 0)
            return Array.Empty<(int, int)>();
        int i = Array.BinarySearch(_clauseStarts, pc);
        if (i < 0) i = ~i - 1;
        if (i < 0) return Array.Empty<(int, int)>();
        int start = _clauseStarts[i];
        int end = i + 1 < _clauseStarts.Length ? _clauseStarts[i + 1] : int.MaxValue;

        var result = new List<(int, int)>();
        int j = Array.BinarySearch(_stopPcs, start);
        if (j < 0) j = ~j;
        for (; j < _stopPcs.Length && _stopPcs[j] < end; j++)
        {
            var site = Shumway.Core.DebugSiteTable.Get(_stopSiteIds[j]);
            result.Add((_stopPcs[j], site.Line));
        }
        return result;
    }

    /// <summary>ADR-035 D5+ — every clause of the predicate whose code contains
    /// <paramref name="pc"/>: its entry address (the head-matching code — where a
    /// re-enter jumps) and its head's source span. What a Set Next Statement aimed at a
    /// SIBLING clause's head resolves against.</summary>
    internal IReadOnlyList<(int ClauseStartPc, int FileId, int HeadLine, int FirstLine)>
        ClauseHeadTargets(int pc)
    {
        var result = new List<(int, int, int, int)>();
        int i = IndexOfPredicateAt(pc);
        if (i < 0 || _clauseStarts.Length == 0) return result;
        var entries = SortedPredicateEntries();
        int predStart = entries[i];
        var pred = _currentPredicatesByAddress![predStart];
        if (!WithinPredicate(pred, predStart, pc)) return result;
        int predEnd = predStart + pred.Bytecode.Length;

        int j = Array.BinarySearch(_clauseStarts, predStart);
        if (j < 0) j = ~j;
        for (; j < _clauseStarts.Length && _clauseStarts[j] < predEnd; j++)
        {
            int clauseStart = _clauseStarts[j];
            var sites = ClauseSites(clauseStart);
            if (sites.Count == 0) continue;
            int firstSite = SiteAt(sites[0].Pc);
            if (firstSite < 0) continue;
            var info = Shumway.Core.DebugSiteTable.Get(firstSite);
            if (ClauseLineSpan(info.FileId, info.Line) is not { } span) continue;
            result.Add((clauseStart, info.FileId, span.HeadLine, span.FirstLine));
        }
        return result;
    }

    /// <summary>ADR-035 D5+ — the entry address of the predicate whose code contains
    /// <paramref name="pc"/>, or -1. The re-enter dispatch intercept matches on it.</summary>
    internal int PredicateAddressOf(int pc)
    {
        int i = IndexOfPredicateAt(pc);
        if (i < 0) return -1;
        int predStart = SortedPredicateEntries()[i];
        return WithinPredicate(_currentPredicatesByAddress![predStart], predStart, pc)
            ? predStart : -1;
    }

    /// <summary>ADR-035 D5+ — the source span of the debuggable clause that contains
    /// <paramref name="line"/> in <paramref name="fileId"/>: where its head is written and
    /// the first/last stoppable lines. Used to recognise a Set Next Statement aimed at the
    /// HEAD (a line in [HeadLine, FirstLine)) as the back-to-head rewind. Null when no
    /// clause spans the line.</summary>
    internal (int HeadLine, int FirstLine, int LastLine)? ClauseLineSpan(int fileId, int line)
    {
        foreach (var c in _clauseLines)
        {
            if (c.FileId != fileId) continue;
            if (line >= c.HeadLine && line <= c.LastLine)
                return (c.HeadLine, c.FirstLine, c.LastLine);
        }
        return null;
    }

    private int FirstSiteLineAtOrAfter(int fileId, int line)
    {
        int best = -1;
        foreach (int id in _compiledSites)
        {
            var site = Shumway.Core.DebugSiteTable.Get(id);
            if (site.FileId != fileId || site.Line < line) continue;
            if (best < 0 || site.Line < best) best = site.Line;
        }
        return best;
    }

    /// <summary>ADR-035 — disarms the breakpoint the user set on this source line. The
    /// line is snapped exactly as <see cref="AddBreakpoint"/> snapped it, or a breakpoint
    /// set on a rule's head could never be removed from the head line it is drawn
    /// on.</summary>
    public void RemoveBreakpoint(string file, int line)
    {
        lock (_debugArmGate)   // vs query setup: see AddBreakpoint
        {
            _requestedBreakpoints.Remove((file, line));
            _breakpointConditions.Remove((file, line));
            int fileId = Shumway.Core.DebugSiteTable.InternFile(file);
            int target = SnapToCompiledLine(fileId, line);
            if (target < 0) target = line;

            foreach (int id in Shumway.Core.DebugSiteTable.SitesOnLine(fileId, target))
            {
                _breakpointSites.Remove(id);
                _breakpointRequests.Remove(id);
            }
            RefreshBreakpoints();
        }
    }

    /// <summary>ADR-035 — links the code space if no query has yet done so, which is
    /// what makes the engine's stop sites known. A trivial goal is the cheapest way
    /// to ask for exactly the work a query's setup does.</summary>
    private void EnsureCodeLinked()
    {
        if (_currentPredicatesByAddress is not null) return;
        foreach (var _ in QueryAll("true.")) break;
    }

    /// <summary>ADR-035 — turns last-call optimisation on or off for queries from here
    /// on. A debugger turns it OFF, because LCO reclaims a predicate's frame before its
    /// final goal runs and a frame the machine has reclaimed is a frame the debugger
    /// cannot show. To change it for the query already running — which is what a
    /// debugger stopped inside one actually wants — see
    /// <see cref="Debugging.DebugService.SetLastCallOptimisation"/>, or set the
    /// <c>debug_lco</c> prolog flag from a goal.</summary>
    public void SetDebugLastCall(bool on) => _flags.DebugLco = on;

    public void ClearBreakpoints()
    {
        lock (_debugArmGate)   // vs query setup: see AddBreakpoint
        {
            _requestedBreakpoints.Clear();
            _breakpointConditions.Clear();
            _breakpointSites.Clear();
            _breakpointRequests.Clear();
            RefreshBreakpoints();
        }
    }

    /// <summary>ADR-035 — re-applies the patches to the buffer the debugged activation is
    /// executing RIGHT NOW, so a breakpoint set or cleared while a query is stopped takes effect
    /// on that same query. A no-op before the first query, where the next
    /// <see cref="SetupQueryFromTerm"/> will do it anyway.
    ///
    /// <para>The buffer comes from the LIVE activation, never from a cached reference: a
    /// mid-query <c>assertz</c> can reallocate the bytecode array (grow-and-copy), and the
    /// activation then runs the NEW array. Un-patching a stale cached array would restore a dead
    /// buffer and leave the live one with an orphaned <c>Break</c> byte — the crash this replaces.
    /// Whatever the activation runs now is the one and only buffer to touch; if it did not change,
    /// it is the same array, and if it did, it is the new one.</para></summary>
    private void RefreshBreakpoints()
    {
        // _lastQueryEngine is the activation of the query in flight (or the one just stopped),
        // and it is saved/restored around an Immediate-window evaluation, so this is the buffer
        // the code the user is looking at actually runs — following a mid-query realloc.
        byte[]? live = _lastQueryEngine?.CurrentProgram ?? _patchedProgram;
        if (live is null) return;
        // A live buffer already carries our Break bytes (a realloc copies them), so un-patching
        // it must find a Break at every recorded pc — anything else is real drift, not the
        // routine fresh-rebuild case (which only happens at query setup).
        SyncBreakpoints(live, bufferCarriesOurPatches: true);
    }

    /// <summary>ADR-035 — makes <paramref name="program"/>'s patched bytes agree with the armed
    /// sites. First removes the patches this engine applied before, then patches what is armed
    /// now, recording each original byte for the interpreter to re-dispatch a <c>Break</c>.
    ///
    /// <para><paramref name="bufferCarriesOurPatches"/> says whether <paramref name="program"/>
    /// is the buffer our recorded patches live in — true for the buffer the activation runs
    /// (same array or a grow-and-copy of it, which carries the <c>Break</c> bytes) and for a
    /// REUSED persistent buffer at setup; false only for a FRESHLY-REBUILT one, which was linked
    /// clean from the compiled predicates and never carried a Break, so there is nothing to
    /// remove and the recorded originals (from the now-dead buffer) must not be written into
    /// it.</para></summary>
    private void SyncBreakpoints(byte[] program, bool bufferCarriesOurPatches)
    {
        if (bufferCarriesOurPatches)
            foreach (var (pc, original) in _breakpointPatches)
            {
                if ((uint)pc < (uint)program.Length
                    && program[pc] == (byte)Shumway.Core.Opcode.Break)
                    program[pc] = original;
                else
                {
                    // We recorded a Break at this pc but the buffer the activation runs has none.
                    // The guard above keeps us from writing a stale original over live code, but
                    // this should NEVER happen — it means the breakpoint table drifted from the
                    // executed buffer, exactly the class of bug this design exists to prevent, so
                    // surface it loudly for investigation rather than papering over it.
                    string msg = $"breakpoint table out of step: recorded a Break at pc={pc} but "
                        + $"the live buffer holds 0x{(pc < program.Length ? program[pc] : 0):X2}";
                    Debugging.ShumwayDebugHelper.DiagLine("[Shumway diag] " + msg);
                    System.Diagnostics.Debug.Fail(msg);
                }
            }
        _breakpointPatches.Clear();
        _patchedProgram = program;

        if (_breakpointSites.Count == 0 || _currentPredicatesByAddress is null) return;
        foreach (var (predAddr, pred) in _currentPredicatesByAddress)
        {
            foreach (var stop in pred.DebugStops)
            {
                if (!_breakpointSites.Contains(stop.SiteId)) continue;
                int pc = predAddr + stop.Offset;
                if ((uint)pc >= (uint)program.Length) continue;
                if (_breakpointPatches.ContainsKey(pc)) continue;   // one site, many clauses
                _breakpointPatches[pc] = program[pc];
                program[pc] = (byte)Shumway.Core.Opcode.Break;
            }
        }
    }

    // Every stop site in the loaded program, by program address, sorted. Built
    // alongside _compiledSites; empty unless something was compiled debuggable.
    private int[] _stopPcs = Array.Empty<int>();
    private int[] _stopSiteIds = Array.Empty<int>();

    /// <summary>ADR-035 — the source site AT this program address, or -1 if the
    /// address is not a stop site. What a session that receives <c>OnBreak(pc)</c>
    /// uses to say where it stopped.</summary>
    public int SiteAt(int pc)
    {
        int i = Array.BinarySearch(_stopPcs, pc);
        return i >= 0 ? _stopSiteIds[i] : -1;
    }

    /// <summary>ADR-035 — the source site this program address is INSIDE: the last
    /// stop site at or before it. A pc in the middle of a goal's instructions —
    /// which is where the four ports find it — belongs to the goal whose site
    /// precedes it. Returns -1 before the first site in the program.</summary>
    public int SiteAtOrBefore(int pc)
    {
        if (_stopPcs.Length == 0) return -1;
        int i = Array.BinarySearch(_stopPcs, pc);
        if (i < 0) i = ~i - 1;
        return i >= 0 ? _stopSiteIds[i] : -1;
    }

    // Every debuggable clause in the loaded program, by program address, sorted.
    // Built alongside _stopPcs; empty unless something was compiled debuggable.
    private int[] _clauseStarts = Array.Empty<int>();
    private Shumway.Compiler.Wam.DebugClauseFrame[] _clauseFrames
        = Array.Empty<Shumway.Compiler.Wam.DebugClauseFrame>();

    /// <summary>ADR-035 — the first clause at or after a predicate's entry address. A
    /// predicate does not begin with its clause: the dispatch prologue comes first, and the
    /// frame map is keyed by CLAUSE. Used for the top-level query, whose own address is the
    /// only thing we know about it.</summary>
    private int FirstClauseStartAtOrAfter(int predicateAddress)
    {
        if (_clauseStarts.Length == 0) return -1;
        int i = Array.BinarySearch(_clauseStarts, predicateAddress);
        if (i < 0) i = ~i;                       // the first clause that starts after it
        return i < _clauseStarts.Length ? _clauseStarts[i] : -1;
    }

    /// <summary>ADR-035 — the clause executing at this program address.</summary>
    private Shumway.Compiler.Wam.DebugClauseFrame? ClauseAt(int pc)
    {
        if (_clauseStarts.Length == 0 || pc < 0) return null;
        int i = Array.BinarySearch(_clauseStarts, pc);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        var clause = _clauseFrames[i];
        return pc < clause.End ? clause : null;
    }

    /// <summary>ADR-035 — one entry of the Prolog call stack, with the variables of the
    /// clause it is running, rendered as the user wrote them.</summary>
    public readonly record struct DebugFrame(
        string Name, int Arity, string File, int Line, int Pc,
        IReadOnlyList<(string Name, string Value)> Variables)
    {
        /// <summary>ADR-035 — the frame as the CALL it is: the head's arguments with their
        /// CURRENT values, parenthesised and ready to display — <c>(120, foo/2, _G5)</c> —
        /// instantiating as the clause runs. Empty when the clause was not compiled
        /// debuggable (there is no head skeleton to fill in), and for the query and the
        /// omitted-frames sentence, which are not calls.</summary>
        public string HeadArgs { get; init; } = "";

        /// <summary>ADR-035 — which clause of its predicate this frame is running, 1-based,
        /// in source order: the <c>!2</c> of <c>total(...)!2</c>. Zero when unknown.</summary>
        public int ClauseNumber { get; init; }

        /// <summary>ADR-035 D5+ — the source lines Set Next Statement accepts ON THIS
        /// FRAME (cross-frame moves rewind the frames above it first). Filled by the
        /// debug service when a stop is published; empty otherwise.</summary>
        public IReadOnlyList<int> SetNextLines { get; init; } = Array.Empty<int>();

        public override string ToString() => $"{Name}/{Arity} at {File}:{Line}";
    }

    /// <summary>ADR-035 — the Prolog call stack, innermost frame first, recomposed
    /// from the activation's environment chain. Never from the C# stack: the Tier-0
    /// interpreter runs the whole program inside one <c>Dispatch</c> frame, so the
    /// C# stack says nothing about where Prolog is.
    ///
    /// <para>What the machine reclaims, the debugger cannot show. A predicate whose
    /// frame last-call optimisation has already popped is not on this list — which
    /// is exactly why debug code compiles its last call as
    /// <c>debug_lastcall</c> and a debugger turns <c>debug_lco</c> off.</para></summary>
    public IReadOnlyList<DebugFrame> CaptureFrames(Activation engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return CaptureFrames(engine, engine.P, engine.E, engine.Cp, liveTop: true);
    }

    /// <summary>ADR-035 — the call stack of a computation the machine is not standing
    /// in. At the redo port the machine has not yet restored the choice point, so
    /// <c>P</c> and the environment chain still describe the computation that just
    /// failed; the stack the debugger must show is the one the retried clause will run
    /// in, which the choice point carries.</summary>
    public IReadOnlyList<DebugFrame> CaptureFrames(
        Activation engine, int pc, int e, int cp, bool liveTop = false)
    {
        ArgumentNullException.ThrowIfNull(engine);

        // TWO PASSES, and the reason is the deep stack. A recursion 2 700 frames deep is a
        // real thing to be stopped in, and nobody reads 2 700 frames: what they read is the
        // few at the top (where they are) and the few at the bottom (how they got in). Between
        // them is the same clause 2 600 times.
        //
        // Building all of them means rendering every variable of every one — the expensive
        // part of a stop by far — and then not showing most of them, because the stack has to
        // cross a fixed-size buffer. So the first pass finds the frames and NOTHING else
        // (a binary search apiece), and the second builds only the ones that will be seen,
        // with one synthetic frame in the middle saying how many are not.
        var sites = new List<(int Pc, int Env)>();
        CollectFrameSites(engine, sites, pc, e, cp, liveTop);

        // One bag per capture: the frames of one stop share their bindings, and their
        // renderings — see DebugValueBag.
        var bag = new DebugValueBag(this, engine);

        var frames = new List<DebugFrame>();
        var plan = BuildFramePlan(sites);
        for (int k = 0; k < plan.Shown.Length; k++)
        {
            if (plan.OmittedCount > 0 && k == plan.OmitAfter)
                frames.Add(new DebugFrame(
                    plan.OmittedCount == 1
                        ? "... 1 frame omitted ..."
                        : $"... {plan.OmittedCount:N0} frames omitted ...",
                    OmittedFramesArity, "", 0, -1, Array.Empty<(string, string)>()));
            int idx = plan.Shown[k];
            AddFrame(engine, frames, sites[idx].Pc, sites[idx].Env, bag);
        }
        return frames;
    }

    /// <summary>ADR-035 — the (pc, environment) behind one frame of the CURRENT stop's
    /// display list, by the index the debugger's frames carry. Mirrors
    /// <see cref="CaptureFrames(Activation, int, int, int)"/>'s head/tail selection exactly,
    /// omitted-frames sentence included (that index answers false: it is not a frame).
    /// What the Immediate window's goal evaluation resolves a frame index against.</summary>
    internal bool TryGetDisplayFrameContext(
        Activation engine, int displayIndex, out int pc, out int env)
    {
        pc = -1;
        env = -1;
        if (displayIndex < 0) return false;
        // ADR-035 D5+ — a PENDING clause re-enter is presented as a synthetic top frame
        // (the chosen predicate at its chosen head, not yet entered — no machine context
        // exists for it). Display indices from the debugger include it; the real frames
        // shift down by one. Centralised HERE because every display-index consumer
        // (Set Next Statement, the Immediate window's goal evaluation, bind-into-frame)
        // resolves through this method.
        if (engine.DebugClauseEntryArmed)
        {
            if (displayIndex == 0) return false;
            displayIndex--;
        }
        var sites = new List<(int Pc, int Env)>();
        // liveTop, like CaptureFrames(engine): the display indices MUST align with the
        // stack the debugger shows.
        CollectFrameSites(engine, sites, engine.P, engine.E, engine.Cp, liveTop: true);

        var plan = BuildFramePlan(sites);
        if (plan.OmittedCount <= 0)
        {
            if (displayIndex >= plan.Shown.Length) return false;
            (pc, env) = sites[plan.Shown[displayIndex]];
            return true;
        }
        if (displayIndex < plan.OmitAfter)
        {
            (pc, env) = sites[plan.Shown[displayIndex]];
            return true;
        }
        if (displayIndex == plan.OmitAfter) return false;   // "... N frames omitted ..."
        int si = displayIndex - 1;                          // past the sentence
        if (si >= plan.Shown.Length) return false;
        (pc, env) = sites[plan.Shown[si]];
        return true;
    }

    /// <summary>ADR-035 — the variables of one frame as TERMS, not renderings: what the
    /// Immediate window substitutes into a goal. A variable whose slot has not been
    /// written yet (or holds something that is not a term) is simply absent — the goal's
    /// variable of that name stays free.</summary>
    internal IReadOnlyList<(string Name, Term Value)> MaterializeFrameVariables(
        Activation engine, int pc, int env)
    {
        var result = new List<(string, Term)>();
        foreach (var (name, value, _, _) in MaterializeFrameVariablesWithAddresses(engine, pc, env))
            result.Add((name, value));
        return result;
    }

    /// <summary>ADR-035 D5+ — the frame's variables as terms AND as heap ADDRESSES on the
    /// suspended activation, which is what the bind-into-frame commit needs: the address is
    /// the real cell a committed binding unifies against, where the term is only a copy.
    /// <c>Addr</c> is the DEREFERENCED slot address for a heap-referencing slot, or -1 when
    /// the slot holds an inline value (bound immediate — nothing to bind into) or could not
    /// be read. <c>IsAttVar</c> flags an attributed variable (bind-into-frame refuses those:
    /// unifying one schedules hook wakeups the suspended machine is in no state to run).</summary>
    internal IReadOnlyList<(string Name, Term Value, int Addr, bool IsAttVar)>
        MaterializeFrameVariablesWithAddresses(Activation engine, int pc, int env)
    {
        var clause = ClauseAt(pc);
        if (env < 0 || clause is null || clause.Value.Variables.Count == 0)
            return Array.Empty<(string, Term, int, bool)>();

        var result = new List<(string, Term, int, bool)>();
        foreach (var v in clause.Value.Variables)
        {
            try
            {
                Cell cell = engine.GetY(env, v.Slot);
                if (cell.Tag is Tag.RawInt or Tag.PstrBuffer) continue;
                int at;
                int addr = -1;
                if (cell.Tag == Tag.Ref)
                {
                    at = engine.Deref(cell.AsHeapIndex);
                    addr = at;
                }
                else
                {
                    at = engine.AllocateHeap(1);
                    engine.SetHeap(at, cell);
                }
                bool isAttVar = addr >= 0 && engine.GetHeap(addr).Tag == Tag.AttVar;
                result.Add((v.Name, TermReader.Materialize(engine, at), addr, isAttVar));
            }
            catch (Exception)
            {
                // Best-effort, like every frame read: a value that cannot be materialized
                // leaves its variable free rather than failing the whole evaluation.
            }
        }
        return result;
    }

    /// <summary>ADR-035 — everything a nested Immediate-window evaluation clobbers.
    ///
    /// <para>An evaluated goal runs as a REAL query — <c>SetupQueryFromTerm</c>, a fresh
    /// activation, the live database — which is exactly the semantics asked for
    /// (an <c>assertz</c> persists like any mid-query nested activation's). But query setup
    /// also rebuilds the per-query debug tables and the address→predicate map, and the
    /// SUSPENDED query — the one the user is stopped in, and will resume with F5 — still
    /// needs its own: its wrapper's addresses are not in the new map, and a stack walk
    /// through them after the eval would mislabel the bottom of the user's stack. So the
    /// eval brackets itself: save these, run, put them back. The code space itself is
    /// append-only; nothing the eval linked invalidates the suspended query's
    /// addresses.</para></summary>
    internal sealed class DebugEvalScope
    {
        public IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? Predicates;
        public int[]? SortedEntries;
        public HashSet<int> CompiledSites = new();
        public int[] StopPcs = Array.Empty<int>();
        public int[] StopSiteIds = Array.Empty<int>();
        public int[] ClauseStarts = Array.Empty<int>();
        public Shumway.Compiler.Wam.DebugClauseFrame[] ClauseFrames
            = Array.Empty<Shumway.Compiler.Wam.DebugClauseFrame>();
        public (int, int, int, int)[] ClauseLines = Array.Empty<(int, int, int, int)>();
        public string? QueryText;
        public string? Label;
        public Activation? LastEngine;
    }

    internal DebugEvalScope BeginDebugEvaluation()
    {
        var scope = new DebugEvalScope
        {
            Predicates = _currentPredicatesByAddress,
            SortedEntries = _sortedPredEntries,
            CompiledSites = _compiledSites,
            StopPcs = _stopPcs,
            StopSiteIds = _stopSiteIds,
            ClauseStarts = _clauseStarts,
            ClauseFrames = _clauseFrames,
            ClauseLines = _clauseLines.ToArray(),
            QueryText = CurrentQueryText,
            Label = QueryLabel,
            LastEngine = _lastQueryEngine,
        };
        _debugEvalDepth++;
        return scope;
    }

    internal void EndDebugEvaluation(DebugEvalScope scope)
    {
        _debugEvalDepth--;
        _currentPredicatesByAddress = scope.Predicates;
        _sortedPredEntries = scope.SortedEntries;
        _compiledSites = scope.CompiledSites;
        _stopPcs = scope.StopPcs;
        _stopSiteIds = scope.StopSiteIds;
        _clauseStarts = scope.ClauseStarts;
        _clauseFrames = scope.ClauseFrames;
        _clauseLines.Clear();
        _clauseLines.AddRange(scope.ClauseLines);
        CurrentQueryText = scope.QueryText;
        QueryLabel = scope.Label;
        _lastQueryEngine = scope.LastEngine;
    }

    /// <summary>ADR-035 D5 — how many debug evaluations (Immediate-window goals, breakpoint
    /// conditions) are running right now. While one is, the OUTER query is suspended
    /// mid-flight — its activation, its Break bytes, its in-flight choice points all live in
    /// the current code space — so a nested query's setup must not treat itself as the safe
    /// point it usually is: no auto-compaction ("no in-flight choice points hold
    /// addresses into it" premise is false here; the compaction is merely deferred to the
    /// next real query), and no breakpoint re-sync (the armed table describes the OUTER
    /// query's buffer and must keep doing so).</summary>
    private int _debugEvalDepth;

    /// <summary>ADR-035 D5 — the persistent buffer was REBUILT by a debug evaluation's
    /// nested setup (the outer query had already invalidated it), which skips the breakpoint
    /// sync: the fresh buffer never received the armed Break bytes. The next real setup
    /// consumes this to pass <c>bufferCarriesOurPatches: false</c> for a buffer it would
    /// otherwise assume it had patched.</summary>
    private bool _persistentRebuiltPatchFree;

    /// <summary>How many frames a stop carries before it starts leaving some out, and how
    /// they are divided when it does: the innermost <see cref="HeadFrames"/> — where the
    /// machine is — and the outermost <see cref="TailFrames"/> — how it got there. What lies
    /// between is the middle of a recursion, and it is the same clause over and over.</summary>
    private const int MaxFrames = 120;
    private const int HeadFrames = 80;
    private const int TailFrames = 20;

    /// <summary>The longest cycle the recursion detector looks for — a period of predicates
    /// that repeats down the stack (1 = plain recursion, 2 = mutual, and so on).</summary>
    private const int MaxCyclePeriod = 8;

    /// <summary>The arity of the "... N frames omitted ..." frame — not a predicate, and not
    /// the query either (which is -1). A debugger renders a negative arity as no arity at
    /// all, so it shows as the sentence it is.</summary>
    private const int OmittedFramesArity = -2;

    /// <summary>ADR-035 — which site indices a stop displays, and where the
    /// "... N frames omitted ..." sentence falls among them. <see cref="Shown"/> lists the
    /// site indices to render, in display order (innermost first); <see cref="OmitAfter"/> is
    /// how many of them precede the sentence (-1 when nothing is elided); <see cref="OmittedCount"/>
    /// is how many sites the sentence stands for.</summary>
    private readonly record struct FramePlan(int[] Shown, int OmitAfter, int OmittedCount);

    /// <summary>ADR-035 — the display plan for a captured stack: the single place that decides
    /// which frames to show and which to elide, so <see cref="CaptureFrames(Activation, int, int, int)"/>
    /// and <see cref="TryGetDisplayFrameContext"/> can never disagree about it.
    ///
    /// <para>Under <see cref="MaxFrames"/> every frame shows. Over it the middle is left out —
    /// but a stack that deep is almost always a RECURSION, the same short cycle of predicates
    /// repeated hundreds of times, and a blind head/tail cut slices through the middle of a
    /// cycle at each edge. <see cref="TryBuildCyclePlan"/> keeps the SAME budget — the innermost
    /// <see cref="HeadFrames"/> and outermost <see cref="TailFrames"/>, so the display never
    /// shrinks below ~100 frames — but SNAPS each cut to a cycle boundary, so both ends show
    /// whole cycles: the innermost (where the machine is) and the outermost (where the recursion
    /// STARTED, together with the non-recursive frames that led into it). A cut may keep a few
    /// frames more than the budget to reach the boundary; that is deliberate. Seeing the origin
    /// end whole is what lets a user read where the chain came from and Run-to-cursor onto the
    /// goal after it. Falls back to a blind head/tail cut when there is no cycle spanning the
    /// cut region.</para></summary>
    private static FramePlan BuildFramePlan(IReadOnlyList<(int Pc, int Env)> sites)
    {
        int n = sites.Count;
        if (n <= MaxFrames)
        {
            var all = new int[n];
            for (int i = 0; i < n; i++) all[i] = i;
            return new FramePlan(all, -1, 0);
        }

        if (TryBuildCyclePlan(sites, out var cyclePlan)) return cyclePlan;

        // Fallback: innermost HeadFrames + the sentence + outermost TailFrames.
        var shown = new int[HeadFrames + TailFrames];
        for (int i = 0; i < HeadFrames; i++) shown[i] = i;
        for (int i = 0; i < TailFrames; i++) shown[HeadFrames + i] = n - TailFrames + i;
        return new FramePlan(shown, HeadFrames, n - HeadFrames - TailFrames);
    }

    /// <summary>Detect the dominant repeating cycle in the stack and build a plan that cuts on
    /// its boundaries. The signature of a frame is its code position (<c>Pc</c>): the same pc
    /// is the same clause resumed at the same call site, so a recursion — however its clauses
    /// are selected — reads as a run of identical pcs (plain recursion, period 1) or a run that
    /// repeats every P frames (mutual recursion, period P). Returns false when no cycle is long
    /// enough to elide, or when the frames OUTSIDE the cycle are themselves too many to show.</summary>
    private static bool TryBuildCyclePlan(
        IReadOnlyList<(int Pc, int Env)> sites, out FramePlan plan)
    {
        plan = default;
        int n = sites.Count;

        // The longest contiguous block that repeats with a period of at most MaxCyclePeriod.
        int bestStart = -1, bestPeriod = 0, bestLen = 0;
        for (int p = 1; p <= MaxCyclePeriod; p++)
        {
            int i = 0;
            while (i < n)
            {
                int runStart = i;
                while (i + p < n && sites[i].Pc == sites[i + p].Pc) i++;
                if (i > runStart)
                {
                    int len = (i - runStart) + p;   // periodic block [runStart, runStart+len)
                    if (len > bestLen) { bestLen = len; bestStart = runStart; bestPeriod = p; }
                }
                i++;
            }
        }

        if (bestStart < 0) return false;

        int fullCycles = bestLen / bestPeriod;
        if (fullCycles < 2) return false;           // not a recursion worth cutting on

        int bandStart = bestStart;
        int bandEnd = bestStart + fullCycles * bestPeriod;   // exclusive, whole cycles

        // Keep the head/tail BUDGET, but move each cut onto a cycle boundary of the band so the
        // two ends show whole cycles. The inner cut rounds UP (>= HeadFrames), the outer cut
        // rounds DOWN (leaves >= TailFrames), so the display stays at least HeadFrames+TailFrames.
        int innerCut = SnapUpToCycle(HeadFrames, bandStart, bandEnd, bestPeriod);
        int outerCut = SnapDownToCycle(n - TailFrames, bandStart, bandEnd, bestPeriod);

        int omitted = outerCut - innerCut;
        if (omitted < bestPeriod) return false;     // the band does not span the cut — head/tail

        // Shown = [0, innerCut) ++ [outerCut, n): the innermost budget (ending on a boundary),
        // then everything from the outer boundary — the outermost whole cycles AND the
        // non-recursive frames that started the chain.
        int shownCount = innerCut + (n - outerCut);
        var shown = new int[shownCount];
        int k = 0;
        for (int i = 0; i < innerCut; i++) shown[k++] = i;
        for (int i = outerCut; i < n; i++) shown[k++] = i;

        plan = new FramePlan(shown, innerCut, omitted);
        return true;
    }

    /// <summary>The smallest cycle boundary at or after <paramref name="x"/> within the band
    /// [<paramref name="bandStart"/>, <paramref name="bandEnd"/>); <paramref name="x"/> itself
    /// when it is before the band (nothing to snap), <paramref name="bandEnd"/> when past it.</summary>
    private static int SnapUpToCycle(int x, int bandStart, int bandEnd, int period)
    {
        if (x <= bandStart) return x;
        if (x >= bandEnd) return bandEnd;
        int m = (x - bandStart + period - 1) / period;   // ceil
        return Math.Min(bandStart + m * period, bandEnd);
    }

    /// <summary>The largest cycle boundary at or before <paramref name="x"/> within the band;
    /// clamped to the band's ends.</summary>
    private static int SnapDownToCycle(int x, int bandStart, int bandEnd, int period)
    {
        if (x <= bandStart) return bandStart;
        if (x >= bandEnd) return bandEnd;
        int m = (x - bandStart) / period;                // floor
        return bandStart + m * period;
    }

    /// <summary>Pass one: WHERE the frames are — a (pc, environment) pair each — without
    /// building any of them. Same walk, same rules, same stopping condition (the query is the
    /// bottom of every stack).</summary>
    private void CollectFrameSites(
        Activation engine, List<(int Pc, int Env)> sites, int pc, int e, int cp,
        bool liveTop = false)
    {
        // The environment chain holds exactly the clauses that HAVE a frame, innermost
        // first. Only the clause we are standing in can be frameless (a frameless
        // clause makes no non-tail call, so it can never be a caller waiting to
        // resume) — and if it is, the first environment on the chain is already its
        // caller's. That one question decides the whole alignment.
        var envs = new List<int>();
        foreach (int env in engine.EnumerateEnvChain(e)) envs.Add(env);
        bool ownFrame = ClauseAt(pc)?.HasFrame ?? false;

        if (AddSite(sites, pc, ownFrame && envs.Count > 0 ? envs[0] : -1))
            return;

        // At a LIVE port in a clause whose environment is allocated, the caller chain is
        // exactly the saved continuations on the environment chain, and the Cp REGISTER is
        // dead state: between two calls of the body it still holds the PREVIOUS completed
        // call's return address. Yielding it fabricated a ghost frame — the same clause
        // shown twice, once at the current goal and once at the goal that already returned
        // (surfaced by prueba.pl's fuzzy/0 after member/2: a real predicate call sets Cp
        // where a builtin does not, so it took a prelude RULE mid-body to expose it). The
        // redo path is different: there pc is the retried clause but its environment does
        // not exist yet (allocate has not re-run), e/cp are the CALLER's — for that shape
        // the register IS the continuation, and the legacy walk below stands.
        if (liveTop && ownFrame && envs.Count > 0)
        {
            for (int i = 0; i < envs.Count; i++)
            {
                int returnPc = engine.EnvSavedCp(envs[i]);
                if (returnPc < 0) break;
                // Step back a byte to land inside the call itself (see below).
                if (AddSite(sites, returnPc - 1, i + 1 < envs.Count ? envs[i + 1] : -1))
                    return;
            }
            return;
        }

        int envIndex = ownFrame ? 1 : 0;
        foreach (int returnPc in engine.EnumerateCallReturnAddresses(e, cp))
        {
            // A return address points at the instruction AFTER the call, which is
            // where the NEXT goal's code begins — so looking its line up directly
            // would blame a caller for the goal it has not run yet. Step back a byte
            // to land inside the call itself, the goal the frame is really waiting on.
            //
            // The query is the BOTTOM of the stack, and it is not recursive: once it is on
            // the list, the walk is done. What lies past it is the address the query returns
            // to — the top level's own code, which no Prolog frame describes — and it looked
            // enough like the wrapper to be named `?-` a second time. One query, one frame.
            if (AddSite(sites, returnPc - 1, envIndex < envs.Count ? envs[envIndex] : -1))
                break;
            envIndex++;
        }
    }

    /// <summary>Records one frame's site, if the address names a predicate at all. Returns
    /// true when it was the QUERY's — the bottom of the stack, and the end of the walk.
    /// </summary>
    private bool AddSite(List<(int Pc, int Env)> sites, int pc, int env)
    {
        int i = IndexOfPredicateAt(pc);
        if (i < 0) return false;
        // ADR-035 fully-transparent control: a ,/;/-> construct (or its lowered
        // $disj_N / $call_* plumbing) is flow, not a goal — it takes NO frame in
        // the call stack. Skip it but keep walking; the caller advances envIndex
        // regardless, so the next real frame still pairs with its own environment.
        if (IsTransparentControlFunctor(
                _currentPredicatesByAddress![SortedPredicateEntries()[i]].FunctorId))
            return false;
        sites.Add((pc, env));
        return IsQueryEntry(i);
    }

    /// <summary>The predicate whose code contains <paramref name="pc"/>, as an index into
    /// <see cref="SortedPredicateEntries"/>; -1 when the address names none.</summary>
    private int IndexOfPredicateAt(int pc)
    {
        if (_currentPredicatesByAddress is null || pc < 0) return -1;
        var entries = SortedPredicateEntries();
        int i = Array.BinarySearch(entries, pc);
        if (i < 0) i = ~i - 1;
        return i;
    }

    private bool IsQueryEntry(int entryIndex)
    {
        var pred = _currentPredicatesByAddress![SortedPredicateEntries()[entryIndex]];
        var (atomId, _) = FunctorTable.Lookup(pred.FunctorId);
        return DemangleLocalName(AtomTable.GetById(atomId)?.Name ?? "?") == "__query__";
    }

    /// <summary>Is this address really inside the predicate the binary search landed on, or
    /// did the search merely CLAMP to it? The search takes the last entry at or before the
    /// address, so an address past the end of every predicate — a return into the launcher,
    /// say — comes back named as the last one, which is a guess and not a fact.</summary>
    private static bool WithinPredicate(
        Shumway.Compiler.Wam.CompiledPredicate pred, int predicateAddress, int pc)
        => pc >= predicateAddress && pc < predicateAddress + pred.Bytecode.Length;

    /// <summary>A call stack is a column, not a page: a query long enough to wrap turns the
    /// whole stack unreadable, and the tail of a goal is not what identifies it.</summary>
    private static string Ellipsize(string text, int max)
        => text.Length <= max ? text : text.Substring(0, max - 3) + "...";

    /// <summary>How much of a variable's value a frame carries. A stack is a hundred frames
    /// of a few variables each, and it has to fit in one buffer; a single term big enough to
    /// fill it on its own would take the rest of the stack with it.</summary>
    private const int MaxVariableChars = 512;

    /// <summary>Adds the frame at <paramref name="pc"/>. Returns true when it was the
    /// QUERY's — the bottom of the stack, and the end of the walk.</summary>
    private bool AddFrame(
        Activation engine, List<DebugFrame> frames, int pc, int env, DebugValueBag bag)
    {
        if (_currentPredicatesByAddress is null || pc < 0) return false;
        var entries = SortedPredicateEntries();
        int i = Array.BinarySearch(entries, pc);
        if (i < 0) i = ~i - 1;
        if (i < 0) return false;
        var pred = _currentPredicatesByAddress[entries[i]];
        var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
        string name = DemangleLocalName(AtomTable.GetById(atomId)?.Name ?? "?");

        // The wrapper the engine puts the goal in. An error's stack trace hides it, and
        // rightly — the user did not write it. A DEBUGGER may not: stopped inside a
        // top-level query the user IS standing there, and a query of nothing but builtins
        // (`?- writeln(uno), debugger_break, writeln(dos).`) has no other frame at all. The
        // debugger showed an empty stack and looked broken. It shows the query now — as
        // `?-`, which is what the user typed, and NOT as a predicate: arity -1 says "this is
        // not a Name/Arity", and the debugger renders it without one.
        bool isQuery = name == "__query__";
        if (isQuery)
        {
            // `?-` on its own says only "a query is running", which the user could see from
            // the fact that they are stopped. It shows the GOAL — the text they typed —
            // because that is the frame's identity, the way `step/2` is a clause's.
            name = CurrentQueryText is null ? "?-" : "?- " + Ellipsize(CurrentQueryText, 120);
            arity = -1;

            // The environment of a query whose frame last-call optimisation has already
            // reclaimed belongs to somebody else by now, and reading the wrapper's variable
            // map out of a stranger's frame produced confident nonsense — the answer reported
            // as a loop counter. If this address is not really inside the wrapper's code (the
            // search only landed here by clamping), there are no variables to be had.
            if (!WithinPredicate(pred, entries[i], pc)) env = -1;

            int firstClause = FirstClauseStartAtOrAfter(entries[i]);
            if (firstClause >= 0) pc = firstClause;
        }

        // ADR-035 — a synthesised control-construct helper ('$catchgoal_N', '$neg_N', …) shows
        // as the construct the user wrote (catch/3, \+/1, …), not the lowered helper. Its head
        // args are the helper's free variables, which do not correspond to the construct's
        // surface arguments, so they are not rendered.
        bool isConstruct = false;
        if (!isQuery)
        {
            var mapped = DebugConstructName(name, arity);
            if (!ReferenceEquals(mapped.Name, name) && mapped.Name != name)
            {
                name = mapped.Name;
                arity = mapped.Arity;
                isConstruct = true;
            }
        }

        int siteId = SiteAtOrBefore(pc);
        var site = siteId >= 0
            ? Shumway.Core.DebugSiteTable.Get(siteId)
            : default;
        string file = siteId >= 0
            ? Shumway.Core.DebugSiteTable.FileName(site.FileId)
            : "";
        var clause = ClauseAt(pc);
        frames.Add(new DebugFrame(
            name, arity, file, siteId >= 0 ? site.Line : 0, pc,
            ReadVariables(engine, pc, env, bag))
        {
            HeadArgs = isQuery || isConstruct ? "" : RenderHeadArgs(engine, clause, env, bag),
            ClauseNumber = isQuery || isConstruct ? 0 : clause?.ClauseNumber ?? 0,
        });
        return isQuery;
    }

    /// <summary>ADR-035 — the variables of the clause running at <paramref name="pc"/>,
    /// read out of the environment frame at <paramref name="env"/> and rendered the way
    /// the user wrote them. An unbound variable renders as <c>_</c> plus its heap cell,
    /// which is how it will keep printing until something binds it.
    ///
    /// <para>Empty when the clause has no frame, or was not compiled debuggable: its
    /// variables live in X registers the next call overwrites, and there is no honest
    /// answer to give. Debug codegen exists precisely to stop that from happening —
    /// it makes every named variable permanent.</para></summary>
    /// <summary>ADR-035 — the frame's head arguments with their current values, rendered
    /// for the call-stack line: <c>total([item(_, 25)|T], Acc, Total)</c> shown as
    /// <c>([item(_, 25)], 10, _G5)</c>. The skeleton is the head as WRITTEN; each named
    /// variable in it is substituted by its current value (through the capture's bag, so a
    /// term shared with the Locals list is rendered once), an anonymous or not-yet-written
    /// one by <c>_</c>. Each argument is cut to <see cref="MaxHeadArgChars"/> — a stack line
    /// is read at a glance; the full value is in Locals.</summary>
    private string RenderHeadArgs(
        Activation engine, Shumway.Compiler.Wam.DebugClauseFrame? clause, int env,
        DebugValueBag bag)
    {
        if (clause?.HeadArgs is not { Count: > 0 } args) return "";

        var slots = clause.Value.Variables;
        Term Substitute(Term t)
        {
            switch (t)
            {
                case VarTerm v:
                    if (v.Name.Length == 0 || v.Name[0] == '_') return new VarTerm("_");
                    if (env >= 0)
                    {
                        foreach (var s in slots)
                            if (s.Name == v.Name)
                                return new VarTerm(bag.Render(engine.GetY(env, s.Slot)));
                    }
                    return new VarTerm("_");
                case CompoundTerm c:
                    var parts = new Term[c.Args.Length];
                    for (int i = 0; i < parts.Length; i++) parts[i] = Substitute(c.Args[i]);
                    return new CompoundTerm(c.Functor, parts);
                default:
                    return t;
            }
        }

        try
        {
            var text = new System.Text.StringBuilder("(");
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) text.Append(", ");
                // The substituted values arrive as VarTerms NAMED by their rendering — the
                // renderer prints a variable's name verbatim, which splices an already
                // rendered (and already capped) value into the skeleton without
                // materializing anything twice.
                text.Append(Ellipsize(
                    AstTermRenderer.Render(Substitute(args[i]), 999, Operators, quoted: true),
                    MaxHeadArgChars));
            }
            return text.Append(')').ToString();
        }
        catch (Exception)
        {
            return "";   // best-effort, like every frame read
        }
    }

    /// <summary>One argument of a call-stack line. The LINE has to be read at a glance —
    /// the whole call, clause number and all, in a window column; a variable's full value
    /// (itself capped at <see cref="MaxVariableChars"/>) belongs to the Locals window.</summary>
    private const int MaxHeadArgChars = 64;

    private IReadOnlyList<(string Name, string Value)> ReadVariables(
        Activation engine, int pc, int env, DebugValueBag bag)
    {
        if (env < 0) return Array.Empty<(string, string)>();
        var clause = ClauseAt(pc);
        if (clause is null || clause.Value.Variables.Count == 0)
            return Array.Empty<(string, string)>();

        var result = new List<(string, string)>(clause.Value.Variables.Count);
        foreach (var v in clause.Value.Variables)
            result.Add((v.Name, bag.Render(engine.GetY(env, v.Slot))));
        return result;
    }

    /// <summary>
    /// ADR-035 — one rendering per VALUE, however many frames hold it.
    ///
    /// <para>A call stack is mostly the same bindings seen from different clauses: the
    /// caller passed <c>Data</c> down, so every frame of the recursion holds the same term
    /// — the same heap cell. Rendering it per frame did the expensive part of a stop (walk
    /// the term, build the AST, print it) once per frame instead of once, and serialised
    /// the same characters once per frame too. This bag lives for ONE capture (bindings
    /// change between stops) and keys on the dereferenced cell: same cell, same string —
    /// the very instance, which is what lets the channel write it once and point at it.</para>
    /// </summary>
    private sealed class DebugValueBag
    {
        private readonly PrologEngine _host;
        private readonly Activation _engine;
        private readonly Dictionary<(Tag, long), string> _byCell = new();

        public DebugValueBag(PrologEngine host, Activation engine)
        {
            _host = host;
            _engine = engine;
        }

        /// <summary>The rendering of whatever this cell is bound to right now — shared with
        /// every other variable bound to the same thing.</summary>
        public string Render(Cell slot)
        {
            try
            {
                // A VARIABLE WHOSE TURN HAS NOT COME. `allocate` leaves the Y slots
                // untouched — RawInt(0), a control word, not a term — because standard WAM
                // codegen writes a permanent at its FIRST occurrence and never reads it
                // before. It is a plain unbound variable as far as the user is concerned —
                // it has no value yet. (Handing it to the materializer instead threw a
                // NotSupportedException — caught, but printed into Visual Studio's Output
                // window at every Break All.)
                if (slot.Tag == Tag.RawInt) return "_";

                // Not a term at all — the raw backing store of a partial string, which no
                // variable is ever bound TO.
                if (slot.Tag == Tag.PstrBuffer) return "<internal>";

                // THE KEY IS THE DEREFERENCED CELL. Two variables bound to the same term
                // dereference to the same cell — same tag, same payload — wherever their own
                // slots live. An unbound variable's identity is its final Ref cell, so the
                // same variable shows the same `_G` in every frame that shares it, by
                // construction. (Equal-but-distinct terms in different cells do NOT share —
                // the bag dedups sharing, not equality; the channel's string table catches
                // the equal-content case at serialisation.)
                Cell cell = slot;
                int at = -1;
                if (cell.Tag == Tag.Ref)
                {
                    at = _engine.Deref(cell.AsHeapIndex);
                    cell = _engine.GetHeap(at);
                    if (cell.Tag == Tag.Ref) at = cell.AsHeapIndex;   // unbound: its own address
                }
                var key = (cell.Tag, cell.Data);
                if (_byCell.TryGetValue(key, out string? cached)) return cached;

                // Materialization reads from the heap; a value that is not already there (a
                // Y slot holding a direct value cell) is staged into one fresh heap cell.
                // Copying the CELL keeps sharing intact.
                if (at < 0)
                {
                    at = _engine.AllocateHeap(1);
                    _engine.SetHeap(at, cell);
                }
                Term term = TermReader.Materialize(_engine, at);

                // Ellipsized, and not as a nicety. A real program binds real data: a Blint
                // variable holds the parsed contents of the file it is linting, and rendering
                // it whole put a megabyte of text into a variable the Locals window shows on
                // one line — which nobody can read, and which overran the channel that had to
                // carry the WHOLE stack. (Seeing inside a big term is what expanding it in the
                // Locals window is for; that is a func-eval, and it is on the D5 list.)
                // QUOTED (writeq-style): a Locals value feeds the Watch-window edit, and
                // an unquoted atom '1234' round-tripped as the INTEGER 1234.
                string value = Ellipsize(
                    AstTermRenderer.Render(term, 999, _host.Operators, quoted: true),
                    MaxVariableChars);
                _byCell[key] = value;
                return value;
            }
            catch (Exception)
            {
                // Reading a frame is best-effort by nature: a debugger must never take
                // the program down because it could not render something.
                return "<unavailable>";
            }
        }
    }

    /// <summary>ADR-035 — the source site of the clause a choice point's retry address
    /// will actually run.
    ///
    /// <para>A retry address does not point at a clause. It points at the link of a
    /// dispatch chain — <c>trust 72</c>, say — and in an indexed predicate the whole
    /// chain sits ahead of every clause body, so the retry address precedes all the
    /// predicate's source sites and asking which site it is <i>inside</i> answers
    /// "none". Two hops get there: the link names its clause (an <c>address</c>
    /// operand) or the clause simply follows it; and the clause's site sits a few
    /// bytes past its first instruction, behind the <c>meta dbg_info</c>. Returns the
    /// address of the site, so a frame built on it lands on the right line.</para>
    /// </summary>
    internal int RetryClauseSite(int retryPc)
    {
        var program = _patchedProgram;
        if (program is null || retryPc < 0 || retryPc >= program.Length) return retryPc;

        byte opByte = OpcodeAt(program, retryPc);
        var op = (Shumway.Core.Opcode)opByte;
        int body = op switch
        {
            // try / retry / trust carry the clause address as their first operand.
            Shumway.Core.Opcode.Try or Shumway.Core.Opcode.Retry or Shumway.Core.Opcode.Trust
                => BitConverter.ToInt32(program, retryPc + 1),
            // try_me_else / retry_me_else / trust_me name the NEXT clause; their own
            // clause is the code that follows them.
            _ => retryPc + Shumway.Core.OpcodeTable.Get(opByte).Size,
        };
        if (body < 0 || body >= program.Length) return retryPc;

        // The first site at or after the clause's first instruction — but not past the
        // end of the predicate, or a clause with no site of its own (one the compiler
        // built, or a predicate compiled without debug) would borrow the next
        // predicate's line.
        if (_stopPcs.Length == 0) return body;
        int i = Array.BinarySearch(_stopPcs, body);
        if (i < 0) i = ~i;
        if (i >= _stopPcs.Length) return body;

        var entries = SortedPredicateEntries();
        int j = Array.BinarySearch(entries, body);
        if (j < 0) j = ~j - 1;
        int endOfPredicate = j >= 0 && j + 1 < entries.Length ? entries[j + 1] : int.MaxValue;

        return _stopPcs[i] < endOfPredicate ? _stopPcs[i] : body;
    }

    /// <summary>The opcode really at <paramref name="pc"/> — an armed breakpoint has
    /// overwritten the byte with <c>Break</c>, and the original is in the table.</summary>
    private byte OpcodeAt(byte[] program, int pc)
    {
        byte b = program[pc];
        return b == (byte)Shumway.Core.Opcode.Break
               && _breakpointPatches.TryGetValue(pc, out byte original)
            ? original
            : b;
    }

    /// <summary>ADR-035 — the predicate an address falls INSIDE, as opposed to
    /// <see cref="LookupPredicateByAddress"/>, which only recognises an entry point.
    /// The redo port needs it: a choice point's retry address points into the middle
    /// of a clause chain, never at its head.</summary>
    internal (string Name, int Arity)? PredicateContaining(int address)
    {
        if (_currentPredicatesByAddress is null || address < 0) return null;
        var entries = SortedPredicateEntries();
        int i = Array.BinarySearch(entries, address);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        var pred = _currentPredicatesByAddress[entries[i]];
        var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
        string name = DemangleLocalName(AtomTable.GetById(atomId)?.Name ?? "?");
        return name == "__query__" ? null : (name, arity);
    }

    /// <summary>ADR-035 — the module a compiled predicate belongs to: the prefix of its MANGLED
    /// name before the <c>$</c> (a module-local predicate is compiled as <c>module$name</c>).
    /// Null for a global/public predicate, a builtin, a synthesised helper, or the query wrapper
    /// — none carry a module prefix. Read straight off the code the frame is running, so it is
    /// the exact string the mangling used, whatever it is; the debugger's module resolution then
    /// does not depend on how that string was derived at compile time.</summary>
    internal string? ModulePrefixAt(int address)
    {
        if (_currentPredicatesByAddress is null || address < 0) return null;
        var entries = SortedPredicateEntries();
        int i = Array.BinarySearch(entries, address);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        var pred = _currentPredicatesByAddress[entries[i]];
        var (atomId, _) = FunctorTable.Lookup(pred.FunctorId);
        string mangled = AtomTable.GetById(atomId)?.Name ?? "";
        int sep = mangled.IndexOf('$');
        return sep > 0 ? mangled.Substring(0, sep) : null;
    }

    /// <summary>ADR-035 — the module the current frame is in, the way the CALL STACK LINE names
    /// it: the same source-file base name the frame decoder prints (<c>Blint:main</c> comes from
    /// <c>Blint.pl</c>). Two ways, most precise first:
    /// <list type="number">
    /// <item>the frame's OWN mangled module prefix, when it is running a module-local predicate
    /// (<see cref="ModulePrefixAt"/>);</item>
    /// <item>otherwise the base name of the frame's SOURCE FILE — a public predicate is compiled
    /// global (no prefix) and a control-construct helper (<c>$catchgoal_N</c>) is global too, but
    /// the call-stack line still shows the module of the <c>.pl</c> they came from, and so do we.
    /// This is returned as-is (not filtered against the known modules): whether it truly defines
    /// the goal is decided at resolution, which falls back to the unique defining module when the
    /// file's name — e.g. a filename that differs from a <c>:- module</c> name — does not.</item>
    /// </list>
    /// Null only when the frame has no source file (a synthetic <c>&lt;string&gt;</c> consult).</summary>
    internal string? ModuleForFrame(int pc)
    {
        string? own = ModulePrefixAt(pc);
        if (own is not null) return own;

        int siteId = SiteAtOrBefore(pc);
        if (siteId >= 0)
        {
            var site = Shumway.Core.DebugSiteTable.Get(siteId);
            return ModuleNameFromFile(Shumway.Core.DebugSiteTable.FileName(site.FileId));
        }
        return null;
    }

    /// <summary>The module name a file's base name gives — the same <c>GetFileNameWithoutExtension</c>
    /// the call-stack frame decoder uses, with any trailing <c>.pl</c> stripped (a materialised
    /// <c>Blint.pl.pl</c> reads as <c>Blint</c>). Null for a synthetic file (<c>&lt;string&gt;</c>).</summary>
    private static string? ModuleNameFromFile(string file)
    {
        if (string.IsNullOrEmpty(file) || file[0] == '<') return null;
        string baseName;
        try { baseName = System.IO.Path.GetFileNameWithoutExtension(file); }
        catch { return null; }
        while (baseName.EndsWith(".pl", StringComparison.OrdinalIgnoreCase))
            baseName = baseName.Substring(0, baseName.Length - 3);
        return baseName.Length == 0 ? null : baseName;
    }

    /// <summary>ADR-035 — is <paramref name="mangledName"/>/<paramref name="arity"/> a defined
    /// predicate in the current code space? Decides whether a module-qualified name resolves
    /// before falling back to the plain (global / builtin) name.</summary>
    internal bool HasDefinedPredicate(string mangledName, int arity)
    {
        if (_currentPredicatesByAddress is null) return false;
        foreach (var pred in _currentPredicatesByAddress.Values)
        {
            var (atomId, ar) = FunctorTable.Lookup(pred.FunctorId);
            if (ar != arity) continue;
            if ((AtomTable.GetById(atomId)?.Name ?? "") == mangledName) return true;
        }
        return false;
    }

    /// <summary>ADR-035 — every module prefix present in the current code space (the
    /// <c>$</c>-prefix of a mangled local). Lets a typed module name be matched to the real one,
    /// forgiving a trailing <c>.pl</c> and case.</summary>
    internal HashSet<string> DefinedModulePrefixes()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (_currentPredicatesByAddress is not null)
            foreach (var pred in _currentPredicatesByAddress.Values)
            {
                var (atomId, _) = FunctorTable.Lookup(pred.FunctorId);
                string mangled = AtomTable.GetById(atomId)?.Name ?? "";
                int sep = mangled.IndexOf('$');
                if (sep > 0) set.Add(mangled.Substring(0, sep));
            }
        return set;
    }

    /// <summary>ADR-035 — resolve module qualification in an Immediate-window goal.
    /// <list type="bullet">
    /// <item><c>Module:Goal</c> runs Goal in Module (predicates addressed as <c>Module$name</c>).
    /// A typed Module that differs from a real one only by a trailing <c>.pl</c> or case is
    /// matched to it; an unknown Module falls back to the stopped frame's.</item>
    /// <item>An unqualified goal is tried in <paramref name="frameModule"/> first — the module of
    /// the frame the user is stopped in — so a module-local predicate is callable by the name the
    /// source uses (<c>show_usage</c>, not <c>blint$show_usage</c>); if no such local exists it is
    /// left as written, a global / public predicate or a builtin.</item>
    /// </list>
    /// Control constructs (<c>,</c> <c>;</c> <c>-&gt;</c> <c>*-&gt;</c> <c>\+</c> <c>not</c>
    /// <c>once</c> <c>ignore</c> <c>call</c>) are transparent: their goal arguments are resolved,
    /// they themselves are not.</summary>
    internal Term ResolveGoalModule(Term goal, string? frameModule)
        => RewriteModuleGoal(goal, frameModule);

    private Term RewriteModuleGoal(Term g, string? mod)
    {
        if (g is CompoundTerm c)
        {
            if (c.Functor == ":" && c.Args.Length == 2 && c.Args[0] is AtomTerm mAtom)
                return RewriteModuleGoal(c.Args[1], ResolveTypedModule(mAtom.Name, mod));

            if (c.Args.Length == 2 && c.Functor is "," or ";" or "->" or "*->")
                return new CompoundTerm(c.Functor,
                    new[] { RewriteModuleGoal(c.Args[0], mod), RewriteModuleGoal(c.Args[1], mod) });

            if (c.Args.Length >= 1 && c.Functor is "\\+" or "not" or "once" or "ignore" or "call")
            {
                var args = (Term[])c.Args.Clone();
                args[0] = RewriteModuleGoal(args[0], mod);
                return new CompoundTerm(c.Functor, args);
            }
        }
        return MangleModuleLeaf(g, mod);
    }

    private Term MangleModuleLeaf(Term g, string? mod)
    {
        string name;
        int arity;
        switch (g)
        {
            case AtomTerm a: name = a.Name; arity = 0; break;
            case CompoundTerm c: name = c.Functor; arity = c.Args.Length; break;
            default: return g;
        }
        if (name.Length == 0 || name[0] == '$') return g;   // already mangled / a helper

        // The frame's module first — the module named on the current call-stack line. A local
        // there shadows a global of the same name, so it is the precise answer.
        if (mod is not null && HasDefinedPredicate(mod + "$" + name, arity))
            return Remangle(g, mod + "$" + name);

        // The frame's module did not define it (or could not be determined — a public predicate
        // or a lowered helper whose file named no module). Fall back to the module that UNIQUELY
        // defines the name: the only module the unqualified call could mean. Ambiguous (two
        // modules) or absent → leave it global (a public predicate or a builtin).
        string? unique = UniqueModuleDefining(name, arity);
        return unique is not null ? Remangle(g, unique + "$" + name) : g;
    }

    private static Term Remangle(Term g, string mangled)
        => g is AtomTerm ? new AtomTerm(mangled) : new CompoundTerm(mangled, ((CompoundTerm)g).Args);

    /// <summary>The one module whose <c>module$name/arity</c> is defined, or null when none is or
    /// more than one is (ambiguous — not ours to guess).</summary>
    private string? UniqueModuleDefining(string name, int arity)
    {
        string? found = null;
        foreach (var m in DefinedModulePrefixes())
            if (HasDefinedPredicate(m + "$" + name, arity))
            {
                if (found is not null) return null;
                found = m;
            }
        return found;
    }

    private string ResolveTypedModule(string typed, string? frameModule)
    {
        var prefixes = DefinedModulePrefixes();
        if (prefixes.Contains(typed)) return typed;
        foreach (var p in prefixes)
            if (string.Equals(StripPl(p), StripPl(typed), StringComparison.OrdinalIgnoreCase))
                return p;
        return frameModule ?? typed;

        static string StripPl(string s)
            => s.EndsWith(".pl", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 3) : s;
    }

    private int[]? _sortedPredEntries;

    private int[] SortedPredicateEntries()
    {
        if (_sortedPredEntries is not null) return _sortedPredEntries;
        var keys = new int[_currentPredicatesByAddress!.Count];
        int n = 0;
        foreach (int addr in _currentPredicatesByAddress.Keys) keys[n++] = addr;
        Array.Sort(keys);
        return _sortedPredEntries = keys;
    }

    /// <summary>ADR-035 — attaches a debug session (or <c>null</c> to detach).
    /// Every query's activation is born with it, and it receives the four Prolog
    /// ports plus the stop sites of any debug-compiled code. This is what a real
    /// debugger uses; <see cref="SetTracing"/> is the same seam with the tracer
    /// as the session.</summary>
    public void AttachDebugSession(Shumway.Core.IDebugSession? session)
        => DebugSession = session;

    /// <summary>ADR-035 D5+ — whether the RUNTIME debug machinery is on: ports raised,
    /// every binding trailed, last-call optimisation off. True by default (a session
    /// attached the classic way debugs from its first goal); a lazily-opened session
    /// (<see cref="Debugging.DebugOptions.ActivateOnAttach"/>) starts with this FALSE —
    /// queries run at near-release Tier-0 speed — and flips it when a debugger actually
    /// attaches. Compile-time debuggability (<c>compile_mode=debug</c>) is independent:
    /// code is compiled debuggable either way.</summary>
    internal bool DebugFullyArmed { get; set; } = true;

    /// <summary>The LCO choice full debug applies WHEN it arms (the pin / option
    /// resolution done once at <see cref="EnableDebugging"/>).</summary>
    internal bool DebugLcoWhenArmed { get; set; }

    /// <summary>The activation of the query in flight (or the one just stopped) — what a
    /// lazily-arming debug session must reach to turn the machinery on mid-run.</summary>
    internal Activation? LiveActivation => _lastQueryEngine;

    // ADR-035 — set once, when the first debug session's diagnostic logging is armed, so a
    // second EnableDebugging (after a dispose + re-enable) does not stack a second handler.
    private static bool _debugDiagLoggingArmed;

    /// <summary>
    /// ADR-035 — turn on source-level debugging for THIS engine, so a debugger attached to
    /// this PROCESS can set breakpoints in the <c>.pl</c> files it consults, step, inspect
    /// the mixed Prolog+C# call stack, and run goals in the Immediate window. It is the
    /// embedding-API equivalent of the REPL's <c>--debug</c>: the point is to debug Shumway
    /// when it is one part of a larger .NET application, in that application's own process,
    /// rather than only in the standalone REPL.
    ///
    /// <para>CALL IT BEFORE CONSULTING the code you want to debug. Debuggability is a
    /// property of the CODE, decided when it is compiled: predicates compiled after this call
    /// keep their variable names, their frames and their source positions; predicates
    /// compiled before it already threw those away. (Loading a bundle counts as consulting —
    /// a bundle that still carries its module sources is re-compiled debuggable, and the
    /// debugger is pointed at that embedded source; a source-stripped bundle is resolved the
    /// ordinary way, by module name to a <c>.pl</c> on disk.)</para>
    ///
    /// <para>Returns the session; keep it alive for as long as you want to be debuggable, and
    /// dispose it to end debugging (it unpins the channel and detaches). There is one debug
    /// session per process — calling this twice without disposing the first throws.</para>
    /// </summary>
    public Debugging.ChannelDebugSession EnableDebugging(Debugging.DebugOptions? options = null)
    {
        if (Debugging.ShumwayDebugHelper.Session is not null)
            throw new InvalidOperationException(
                "a debug session is already active in this process; dispose it before "
                + "enabling debugging again (there is one debugger per process).");

        options ??= new Debugging.DebugOptions();

        // Debug metadata + debug codegen: named variables kept, frames forced, env trimming
        // and redundant-cut elision off, per-goal source positions recorded. This is the
        // switch the REPL's --debug throws; without it the compiler produces release code
        // with nothing for a debugger to stop at or show.
        _flags.EmitDebugInfo = true;
        _flags.DebugCodegen = true;

        // A reclaimed frame is a frame nobody can show, so a debug session wants LCO off — but
        // SHUMWAY_DEBUG_LCO is a PIN, and a pin the code overrides is not one, so honour it
        // when set and take the caller's choice only otherwise. Under ActivateOnAttach the
        // resolved choice applies only WHEN the session arms — until then LCO stays on,
        // which is most of what makes the lazy mode fast.
        DebugLcoWhenArmed = Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_LCO") is null
            ? options.LastCallOptimisation
            : _flags.DebugLco;
        if (options.ActivateOnAttach)
        {
            DebugFullyArmed = false;
            _flags.DebugLco = true;
        }
        else
        {
            _flags.DebugLco = DebugLcoWhenArmed;
        }

        // SHUMWAY_DEBUG_DIAG=1 — log every exception the engine THROWS, caught or not, with
        // its stack. A handled house-keeping throw is invisible from outside and loud from
        // inside a debugger (Visual Studio prints "Exception thrown" into Output for each);
        // this tells the bug being hunted from the noise. Armed once for the process.
        if (!_debugDiagLoggingArmed
            && Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_DIAG") == "1")
        {
            _debugDiagLoggingArmed = true;
            string trace = Path.Combine(
                Path.GetTempPath(), "shumway-debug", "engine-exceptions.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(trace)!);
                AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                {
                    try
                    {
                        File.AppendAllText(trace,
                            DateTime.Now.ToString("HH:mm:ss.fff") + "  "
                            + e.Exception.GetType().Name + ": " + e.Exception.Message + "\n"
                            + e.Exception.StackTrace + "\n\n");
                    }
                    catch (Exception) { /* a diagnostic must never be the thing that fails */ }
                };
            }
            catch (Exception) { /* no temp dir — run without the log */ }
        }

        // The files we are ABOUT to consult, said before we consult them: a breakpoint drawn
        // before the process stops anywhere binds against a module, a module is a .pl file,
        // and a just-started process has no frames for the debugger to learn the names from.
        // Optional — every file is also announced as it is consulted.
        if (options.SourceFiles is { Count: > 0 } files)
        {
            var full = new string[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                try { full[i] = Path.GetFullPath(files[i]); }
                catch (Exception) { full[i] = files[i]; }
            }
            Debugging.ShumwayDebugHelper.SourceFiles = full;
        }

        var session = new Debugging.ChannelDebugSession(this)
        {
            ActivateOnAttach = options.ActivateOnAttach,
        };

        // ADR-036 — the second endpoint. When a port is configured (option, or the
        // SHUMWAY_DAP_PORT environment default the option carries), the session also
        // listens for a VS Code / DAP client on the loopback interface. Both endpoints
        // coexist; whichever debugger connects first drives.
        if (options.DapPort is int dapPort)
            session.StartDapServer(dapPort);

        if (options.WaitForAttach)
            WaitForDebuggerReady(session, options.AttachTimeout);

        return session;
    }

    /// <summary>ADR-035 — block until a debugger has attached to this process AND finished
    /// arming its breakpoints, or <paramref name="timeout"/> elapses. Split out of
    /// <see cref="EnableDebugging"/> so a bundle-loading path can defer the wait until AFTER
    /// its modules have been consulted (that consult materialises + announces their source,
    /// which is what an attaching debugger must find before the goal runs).</summary>
    internal void WaitForDebuggerReady(
        Debugging.ChannelDebugSession session, TimeSpan timeout)
    {
        // --debug-wait means WAIT. The whole reason to launch a program this way is to debug
        // it from its first goal, so there is NO deadline on the attach itself: a user who
        // takes a minute to open Visual Studio and attach still lands at the entry, rather
        // than finding the program already run to the end. (A program launched to be debugged
        // that runs on without a debugger is useless; hanging until Ctrl-C is the honest
        // behaviour. The old 10 s timeout is what made it "wait, then run past me".)
        Debugging.ShumwayDebugHelper.DiagLine("waiting for a debugger to attach...");
        while (!System.Diagnostics.Debugger.IsAttached)
            System.Threading.Thread.Sleep(50);

        // Attached is not ready: the debugger still has to find the channel and arm the
        // breakpoints the user drew before pressing the button. Consulting now would run the
        // program straight past them. This wait IS bounded — it is the "has it gone quiet"
        // wait (milliseconds), not the "will anyone ever come" wait above.
        int quietMs = (int)timeout.TotalMilliseconds;
        session.WaitForDebuggerCommands(quietMs > 0 ? quietMs : 0);

        // --debug-wait's other promise: the program stops when the debugger is ready — at the
        // entry, not somewhere the user has to guess at. Arm a stop on the first goal.
        if (System.Diagnostics.Debugger.IsAttached)
        {
            session.ArmEntryBreak();
            Debugging.ShumwayDebugHelper.DiagLine(
                "debugger attached; armed stop-at-entry for the first goal");
        }
        else
        {
            Debugging.ShumwayDebugHelper.DiagLine(
                "attach was lost before the entry stop could be armed");
        }
    }

    /// <summary>ADR-035: names the predicate whose entry point is exactly
    /// <paramref name="address"/> — the shape a call/execute operand has, so a
    /// dictionary probe is enough and no containment search is needed. Returns
    /// <c>null</c> for an address that is not a predicate entry (the synthetic
    /// <c>__query__</c> wrapper included), which a debug session reads as "not
    /// a goal worth reporting".</summary>
    internal (string Name, int Arity)? LookupPredicateByAddress(int address)
    {
        var map = _currentPredicatesByAddress;
        if (map is null || !map.TryGetValue(address, out var pred)) return null;
        var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
        string name = AtomTable.GetById(atomId)?.Name ?? "?";
        return name == "__query__" ? null : (name, arity);
    }

    /// <summary>ADR-035 — is this predicate one a debugger may stop IN?
    ///
    /// <para>The prelude and the libraries are implicitly <c>:- disable_debug</c>, and they
    /// are compiled that way — but a PORT is raised by the interpreter at every call and
    /// every proceed, whatever the callee was compiled from. So a step landed in
    /// <c>copy_term/3</c>, in <c>$prelude$$attr_goals_of/2</c>, in the top level's own
    /// wrapper goals: code the user did not write, cannot see, and did not ask to step
    /// through. Stepping has to stay in THEIR program, which means a port in code that is
    /// not theirs must not stop it.</para></summary>
    internal bool IsDebuggableAddress(int address)
        => FunctorAtAddress(address) is int fid && !_nonDebuggableFunctors.Contains(fid);

    /// <summary>ADR-035 — the functor whose compiled code contains <paramref name="address"/>,
    /// or null if the address names none. Shared by the CONTAINER check
    /// (<see cref="IsDebuggableAddress"/> — "is the code I am standing in the user's?") and the
    /// CALLEE check (<see cref="IsDebuggableCallee"/> — "should a call to here stop?").</summary>
    /// <summary>The (address → predicate) mapping the current debug tables were derived
    /// from — the scale guard's identity snapshot (see the query-setup rebuild).</summary>
    private Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? _debugTablesBuiltFor;

    /// <summary>ADR-035 — derives the per-program debug tables (armable stop sites, the
    /// pc→site arrays, clause frames, clause line spans) from the compiled predicates.
    /// One pass over each predicate's stops with a TWO-POINTER walk into its frames —
    /// the old shape re-scanned every stop per frame, quadratic in clause count for
    /// clause-heavy predicates.</summary>
    private void RebuildDebugTables()
    {
        var sites = new HashSet<int>();
        var byPc = new SortedDictionary<int, int>();
        var frames = new SortedDictionary<int, Shumway.Compiler.Wam.DebugClauseFrame>();
        _clauseLines.Clear();
        foreach (var (predAddr, pred) in _currentPredicatesByAddress!)
        {
            foreach (var stop in pred.DebugStops)
            {
                sites.Add(stop.SiteId);
                byPc[predAddr + stop.Offset] = stop.SiteId;
            }
            // The clause frame maps take the same second relocation as the stop
            // sites: clause-local, then predicate-local, then program-absolute.
            // Frames and stops are both emitted in ascending offset order; the stop
            // cursor only ever moves forward.
            int stopCursor = 0;
            var stops = pred.DebugStops;
            for (int i = 0; i < pred.DebugFrames.Count; i++)
            {
                var f = pred.DebugFrames[i];
                frames[predAddr + f.Start] = new Shumway.Compiler.Wam.DebugClauseFrame(
                    predAddr + f.Start, predAddr + f.End, f.HasFrame, f.Variables)
                {
                    HeadArgs = f.HeadArgs,
                    ClauseNumber = f.ClauseNumber,
                };

                // The clause's source span: from where its head is written down to
                // the last line it can be stopped at. What a breakpoint on a line
                // with no code of its own snaps within, and no further.
                while (stopCursor < stops.Count && stops[stopCursor].Offset < f.Start)
                    stopCursor++;
                int fileId = -1, first = int.MaxValue, last = -1;
                for (int s = stopCursor; s < stops.Count && stops[s].Offset < f.End; s++)
                {
                    var site = Shumway.Core.DebugSiteTable.Get(stops[s].SiteId);
                    fileId = site.FileId;
                    if (site.Line < first) first = site.Line;
                    if (site.Line > last) last = site.Line;
                }
                if (last < 0) continue;
                int headLine = i < pred.ClauseSourcePositions.Count
                    ? pred.ClauseSourcePositions[i].Line
                    : first;
                _clauseLines.Add((fileId, Math.Min(headLine, first), first, last));
            }
        }
        _compiledSites = sites;
        _stopPcs = new int[byPc.Count];
        _stopSiteIds = new int[byPc.Count];
        byPc.Keys.CopyTo(_stopPcs, 0);
        byPc.Values.CopyTo(_stopSiteIds, 0);

        _clauseStarts = new int[frames.Count];
        _clauseFrames = new Shumway.Compiler.Wam.DebugClauseFrame[frames.Count];
        frames.Keys.CopyTo(_clauseStarts, 0);
        frames.Values.CopyTo(_clauseFrames, 0);
    }

    // The last predicate RANGE this resolved — a one-entry memo. Ports ask about the
    // same few addresses in a loop (the same call sites, over and over), so the answer
    // is almost always the memo, not the binary search. Reset wherever the predicate
    // layout is rebuilt (_sortedPredEntries invalidation). Engine-thread only, like
    // every port-path structure (func-evals hijack the same thread).
    private int _fidMemoLo = int.MaxValue;
    private int _fidMemoHi = int.MaxValue;
    private int _fidMemoFid;

    private int? FunctorAtAddress(int address)
    {
        if (address >= _fidMemoLo && address < _fidMemoHi) return _fidMemoFid;
        if (_currentPredicatesByAddress is null || address < 0) return null;
        var entries = SortedPredicateEntries();
        int i = Array.BinarySearch(entries, address);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        // The memo range reproduces the search's clamp semantics exactly: everything
        // from this predicate's start to the NEXT predicate's start resolves here.
        _fidMemoLo = entries[i];
        _fidMemoHi = i + 1 < entries.Length ? entries[i + 1] : int.MaxValue;
        _fidMemoFid = _currentPredicatesByAddress[entries[i]].FunctorId;
        return _fidMemoFid;
    }

    /// <summary>ADR-035 — should a CALL landing at <paramref name="address"/> stop? Unlike
    /// <see cref="IsDebuggableAddress"/> (the CONTAINER question) this also refuses a
    /// TRANSPARENT control construct: calling a <c>$disj_N</c> / <c>$call_*</c> helper is
    /// flow, not a goal, so the step passes straight through it to the real callee. The
    /// distinction matters because a <c>$disj_N</c> region CONTAINS user goals — it is a valid
    /// place to be standing (a user goal in a disjunction branch), just not a valid thing to
    /// stop ON when it is the callee.</summary>
    internal bool IsDebuggableCallee(int address)
        => FunctorAtAddress(address) is int fid && IsDebuggableFunctor(fid);

    /// <summary>ADR-035 — is the code at <paramref name="address"/> a transparent control
    /// construct's (a <c>$disj_N</c> / <c>$call_*</c> helper)? The one part of the callee
    /// question that holds regardless of where the CALL SITE is: flow is never a goal.</summary>
    internal bool IsTransparentCalleeAddress(int address)
        => FunctorAtAddress(address) is int fid && IsTransparentControlFunctor(fid);

    internal bool IsDebuggableFunctor(int functorId)
        => !_nonDebuggableFunctors.Contains(functorId)
           && !IsTransparentControlFunctor(functorId);

    /// <summary>ADR-035 — is this functor a pure CONTROL CONSTRUCT (or its lowered
    /// plumbing), which the debugger renders TRANSPARENTLY: no stop port and no
    /// call-stack frame? A standard Prolog tracer (SWI, GProlog) never surfaces
    /// <c>,</c> / <c>;</c> / <c>-&gt;</c> / <c>*-&gt;</c> as goals — they are flow,
    /// not calls — so stepping goes straight from a clause to the real user goals.
    /// The <em>meta</em>-predicates the user invoked by name (<c>catch/3</c>,
    /// <c>once/1</c>, <c>ignore/1</c>, <c>\+</c>) are NOT control constructs: they
    /// stay visible (see <see cref="DebugConstructName"/>).
    ///
    /// <para>What is transparent: the bare operators (never normally reached as a
    /// predicate, but harmless to list); the synthesised disjunction / if-then-else
    /// helper (<c>$disj_N</c>, covering both <c>(A;B)</c> and <c>(C-&gt;T;E)</c>);
    /// and the prelude runtime meta-dispatch helpers that re-enter call dispatch
    /// for a variable goal (<c>$call</c>, <c>$call_conj</c>, <c>$call_disj</c>,
    /// <c>$call_arrow</c>). <c>$call_neg</c> is deliberately left visible — it is
    /// the runtime form of <c>\+</c>, a meta-goal.</para></summary>
    // Transparency is a pure function of the functor's NAME, and functor ids are stable
    // for the life of the process — but computing it walks functor table → atom table →
    // demangle → string switch, and the call PORT asked at every goal. Cached forever.
    private readonly Dictionary<int, bool> _transparentByFid = new();

    internal bool IsTransparentControlFunctor(int functorId)
    {
        if (_transparentByFid.TryGetValue(functorId, out bool cached)) return cached;
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        var name = AtomTable.GetById(atomId)?.Name;
        bool t = name is not null && IsTransparentControlName(DemangleLocalName(name), arity);
        _transparentByFid[functorId] = t;
        return t;
    }

    private static bool IsTransparentControlName(string demangled, int arity)
    {
        switch (demangled)
        {
            case "," or ";" or "->" or "*->" when arity == 2:
            case "$call" or "$call_conj" or "$call_disj" or "$call_arrow":
                return true;
        }
        return demangled.StartsWith("$disj_", StringComparison.Ordinal);
    }

    /// <summary>ADR-035 — is <paramref name="pc"/> inside the synthetic <c>__query__</c>
    /// wrapper the engine puts a top-level goal in? The wrapper is compiled user query code,
    /// so it is a DEBUGGABLE address — but it is not code the user wrote, and its call port to
    /// the entry goal maps to the end of the source (it has no line of its own). "Stop at the
    /// entry point" must skip it and land in the entry predicate itself.</summary>
    internal bool IsQueryWrapperAddress(int pc)
    {
        int i = IndexOfPredicateAt(pc);
        return i >= 0 && IsQueryEntry(i);
    }

    /// <summary>Translates each address in <paramref name="addresses"/>
    /// to the <c>Name/Arity</c> of the predicate that *contains* it
    /// (the largest predicate-entry address ≤ the given address) via
    /// the current query's link-time predicates-by-address map.
    /// Used by the runtime error path to assemble a Prolog-side stack
    /// trace.</summary>
    private IReadOnlyList<(string Name, int Arity)> ResolveAddressesToFunctors(
        IEnumerable<int> addresses)
    {
        var map = _currentPredicatesByAddress;
        if (map is null) return Array.Empty<(string, int)>();
        // Sort predicate-entry addresses once so we can binary-search
        // each query for its containing predicate.
        int[] sortedEntries = map.Keys.OrderBy(a => a).ToArray();
        var result = new List<(string, int)>();
        var seen = new HashSet<int>();
        foreach (int addr in addresses)
        {
            int idx = Array.BinarySearch(sortedEntries, addr);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0) continue;
            int entryAddr = sortedEntries[idx];
            if (!seen.Add(entryAddr)) continue;
            var pred = map[entryAddr];
            var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
            string name = AtomTable.GetById(atomId)?.Name ?? "?";
            // Hide the synthetic __query__ predicate from user-visible
            // stack traces — it's an implementation detail of how the
            // engine wraps queries in a top-level clause.
            if (name == "__query__") continue;
            result.Add((name, arity));
        }
        return result;
    }

    /// <summary>Captures the current call stack as a list of
    /// <c>Name/Arity</c> entries — the innermost active predicate is
    /// at index 0, its caller at index 1, and so on. Exposed for
    /// debugging and used internally to populate
    /// <see cref="LastErrorStackTrace"/> when a runtime error escapes
    ///.</summary>
    private (IReadOnlyList<(string, int)> Plain, IReadOnlyList<StackFrame> WithPositions)
        CaptureStackTrace(Activation engine)
    {
        // Innermost address: the predicate the engine's PC is sitting
        // inside. Walk the env chain via the engine helper for the
        // ancestors.
        var addresses = new List<int>();
        addresses.Add(engine.P);
        foreach (int retAddr in engine.EnumerateCallReturnAddresses())
            addresses.Add(retAddr);
        return ResolveAddressesWithPositions(addresses);
    }

    /// <summary>Variant of <see cref="ResolveAddressesToFunctors"/>
    /// that also returns each frame's source position.
    /// Returned as a pair: the legacy <c>(name, arity)</c> tuples for
    /// <see cref="LastErrorStackTrace"/> back-compat, plus the
    /// position-enriched <see cref="StackFrame"/> list for the new
    /// surface.</summary>
    private (IReadOnlyList<(string Name, int Arity)> Plain,
             IReadOnlyList<StackFrame> WithPositions)
        ResolveAddressesWithPositions(IEnumerable<int> addresses)
    {
        var map = _currentPredicatesByAddress;
        if (map is null)
            return (Array.Empty<(string, int)>(), Array.Empty<StackFrame>());
        int[] sortedEntries = map.Keys.OrderBy(a => a).ToArray();
        var plain = new List<(string, int)>();
        var frames = new List<StackFrame>();
        var seen = new HashSet<int>();
        foreach (int addr in addresses)
        {
            int idx = Array.BinarySearch(sortedEntries, addr);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0) continue;
            int entryAddr = sortedEntries[idx];
            if (!seen.Add(entryAddr)) continue;
            var pred = map[entryAddr];
            var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
            string name = AtomTable.GetById(atomId)?.Name ?? "?";
            if (name == "__query__") continue;
            plain.Add((name, arity));
            // Locate the most recent Meta(DbgInfo) opcode at or before the
            // PC inside this predicate's bytecode. Its payload
            // is the clause index; we use it to pick the precise per-clause
            // source position from ClauseSourcePositions, falling back to
            // the predicate's first-clause position when no Meta opcode is
            // present (single-clause predicates or older bundle blobs).
            SourcePosition framePos = FindClausePosition(pred, addr - entryAddr);
            frames.Add(new StackFrame(name, arity, framePos));
        }
        return (plain, frames);
    }

    /// <summary>Scans <paramref name="pred"/>'s bytecode from offset 0 up to
    /// (but not past) <paramref name="predLocalPc"/> for the most recent
    /// <see cref="Opcode.Meta"/> + <see cref="MetaSubOpcode.DbgInfo"/>
    /// opcode and returns the clause position its 4-byte payload indexes
    /// into. Returns the predicate's first-clause position when no Meta
    /// opcode is found (single-clause predicates, or bundle-rebuilt
    /// predicates whose <see cref="CompiledPredicate.ClauseSourcePositions"/>
    /// is empty).</summary>
    private static SourcePosition FindClausePosition(
        Shumway.Compiler.Wam.CompiledPredicate pred, int predLocalPc)
    {
        if (pred.ClauseSourcePositions.Count == 0) return pred.SourcePosition;
        byte[] code = pred.Bytecode;
        int pc = 0;
        int lastClauseIndex = -1;
        while (pc < code.Length && pc <= predLocalPc)
        {
            byte opByte = code[pc];
            if (opByte == (byte)Opcode.Meta
                && pc + 1 < code.Length
                && (MetaSubOpcode)code[pc + 1] == MetaSubOpcode.DbgInfo)
            {
                lastClauseIndex = BytecodeIO.ReadInt32(code, pc + 2);
                pc += 6;
                continue;
            }
            var info = OpcodeTable.Get(opByte);
            if (!info.IsDefined || info.Size == 0) break;
            pc += info.Size;
        }
        if (lastClauseIndex >= 0 && lastClauseIndex < pred.ClauseSourcePositions.Count)
            return pred.ClauseSourcePositions[lastClauseIndex];
        return pred.SourcePosition;
    }

    /// <summary>Cross-thread gate for the debug-table surface: query setup rebuilds the
    /// per-query tables on the engine's thread, and the session's idle watcher arms
    /// breakpoints from its own. See <see cref="SetupQueryFromTerm"/>.</summary>
    private readonly object _debugArmGate = new();

    /// <summary>ADR-036 — the arm gate, for the debug session's idle watcher: it must
    /// take THIS before the session's own stop gate (the order the engine thread uses —
    /// consult/setup under the arm gate, then a stop under the session gate), or the
    /// two-thread arm-vs-consult pair deadlocks by lock inversion.</summary>
    internal object DebugArmGate => _debugArmGate;

}
