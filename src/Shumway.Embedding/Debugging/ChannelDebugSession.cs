using System;
using System.Collections.Generic;

namespace Shumway.Embedding.Debugging;

/// <summary>
/// ADR-035 — the engine side of a debugger conversation, end to end: a
/// <see cref="DebugService"/> whose stops go out through a <see cref="DebugChannel"/>
/// and whose next move comes back through the same channel.
///
/// <para>Every stop runs the same three steps, in this order, and the order is the whole
/// design: <b>write</b> the snapshot into pinned memory, <b>notify</b> (which is where
/// the debugger stops the process and reads that memory), then <b>drain</b> the commands
/// it left and do as they say. Nothing runs in the debuggee while it is stopped, because
/// nothing needs to — the answer was already there before the question could be
/// asked.</para>
///
/// <para>This is what the Concord components of D2/D3 attach to. It has no dependency on
/// them, or on Visual Studio, or on Windows: a test can be the debugger just as well, and
/// in this repository, one is.</para>
/// </summary>
public sealed class ChannelDebugSession : IDisposable
{
    private readonly PrologEngine _engine;
    private readonly DebugChannel _channel;
    private readonly DebugService _service;
    private readonly Action<int> _notify;
    private bool _disposed;

    /// <param name="notify">What tells the debugger a stop happened. Defaults to
    /// <see cref="Shumway.Core.Debugging.ShumwayDebugHost.Notify"/>, which is where a real
    /// debugger plants its hidden breakpoint; a test passes its own, because there is no
    /// debugger to stop it and it has to play that part itself.</param>
    public ChannelDebugSession(PrologEngine engine, Action<int>? notify = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _channel = new DebugChannel();
        _notify = notify ?? Shumway.Core.Debugging.ShumwayDebugHost.Notify;
        _service = new DebugService(engine, OnStop);

        _service.Poll = PollWhileRunning;

        // The mixed stack. When the debugger stops in a foreign predicate's C#, the engine
        // thread is frozen inside the call and cannot be asked for anything — so it says
        // where Prolog is on the way IN, and unsays it on the way out. The buffer stays
        // marked `running` throughout: the machine is not stopped, our stepper must not claim
        // the step, and only the interop depth licenses reading the stack.
        _service.OnInteropEnter = stop => _channel.WriteSnapshot(stop, running: true, interopDepth: 1);
        _service.OnInteropExit = () => _channel.SetInteropDepth(0);

        // ADR-035 — "stop at the entry point" (--debug-wait) rides the debugger_break/0
        // path: a managed Debugger.Break() is the one stop VS enters break mode for without
        // a step or async break already pending, which is exactly the startup situation. The
        // IsAttached guard mirrors debugger_break/0: BreakHere calls Debugger.Break()
        // unconditionally, so if the debugger detached between arming and the first goal
        // there must be no break to nobody.
        _service.EntryBreak = act =>
        {
            bool attached = System.Diagnostics.Debugger.IsAttached;
            ShumwayDebugHelper.DiagLine(
                "entry port reached; stopping at the entry point (IsAttached=" + attached + ")");
            if (attached) BreakHere(act);
        };

        ShumwayDebugHelper.Channel = _channel;
        ShumwayDebugHelper.Session = this;
        engine.AttachDebugSession(_service);

        StartIdleWatcher();
    }

    // ----- the engine when it is NOT running -----

    private System.Threading.Thread? _idleWatcher;
    private readonly object _gate = new object();
    private int _lastHeartbeat = -1;

    /// <summary>ADR-035 — how a debugger gets in when the engine is standing still.
    ///
    /// <para>Everything else here happens BETWEEN GOALS: the engine reads the channel as it
    /// runs. An engine that is not running reads nothing — and an engine waiting at the
    /// top-level prompt is the ordinary thing to attach to. That was a genuine deadlock, and
    /// a silent one: the debugger needs a stop in order to build the objects that stand for
    /// a <c>.pl</c> file, a breakpoint can only bind against those objects, and a stop can
    /// only come from a breakpoint. Nobody could go first, so nothing loaded, and Visual
    /// Studio said what it truthfully saw — "no symbols have been loaded for this
    /// document".</para>
    ///
    /// <para>So when the engine is idle, this thread services the channel in its place: it
    /// obeys the commands the debugger left, and grants the stop it asked for (an empty one
    /// — there is no Prolog stack when no Prolog is running, and it must not pretend
    /// otherwise). It runs only while a debugger is attached, only while the heartbeat says
    /// the engine is NOT passing goals — the running engine services itself, and two of us
    /// must never do it at once — and it sleeps the rest of the time.</para></summary>
    private void StartIdleWatcher()
    {
        _idleWatcher = new System.Threading.Thread(() =>
        {
            while (!_disposed)
            {
                System.Threading.Thread.Sleep(IdleTickMs);
                if (_disposed) break;
                if (!System.Diagnostics.Debugger.IsAttached) continue;

                // Is the engine running? Only the engine bumps the heartbeat, and it does so
                // as it passes goals. If it moved, the engine is servicing the channel itself
                // and this thread must keep its hands off.
                int beat = _channel.HeartbeatValue;
                bool running = beat != _lastHeartbeat;
                _lastHeartbeat = beat;
                if (running) continue;

                ServiceChannelWhileIdle();
            }
        })
        {
            IsBackground = true,
            Name = "shumway-debug-idle",
        };
        _idleWatcher.Start();
    }

    /// <summary>How often the idle engine looks at the channel. Fast enough that setting a
    /// breakpoint at the prompt feels immediate; slow enough to be nothing at all.</summary>
    public const int IdleTickMs = 100;

    private void ServiceChannelWhileIdle()
    {
        // TRY for the gate; never wait for it. The engine holds it for the WHOLE of a stop —
        // it is stopped inside the lock, and does not come back until the user says so — and
        // a thread that blocks on it therefore blocks for as long as the user stares at the
        // screen. That is not a lock, it is a deadlock with good manners: the watcher hangs
        // in Monitor.Enter, in a debuggee that is stopped, and the debugger stops with it.
        // If the engine holds the gate it is already talking to the debugger, and there is
        // nothing here to do anyway. Skip the tick.
        if (!System.Threading.Monitor.TryEnter(_gate)) return;
        try
        {
            bool stopWanted = false;
            foreach (var command in _channel.DrainCommands())
            {
                switch (command.Kind)
                {
                    case DebugCommandKind.Hello:
                    case DebugCommandKind.BreakNow:
                        stopWanted = true;
                        break;
                    default:
                        Apply(_service, command);
                        break;
                }
            }
            if (!stopWanted) return;

            // A stop with no stack, because there is no stack: nothing is running. It is
            // still a REAL stop — which is all the debugger needs it to be, since what it
            // wants from it is the chance to build its modules and bind its breakpoints.
            _service.NoteStop(0);
            OnStopLocked(new DebugStopEvent(
                StopReason.AsyncBreak, "", "", 0, 0, Array.Empty<PrologEngine.DebugFrame>()));
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    /// <summary>ADR-035 — the channel, worked between goals rather than at a stop. Two jobs.
    ///
    /// <para><b>Commands.</b> Setting a breakpoint on a program that is already running is
    /// the ordinary case (F9 during a long query), and it is the ONLY thing a debugger says
    /// while the engine is moving. So only breakpoints are obeyed here. A step or a continue
    /// read off the channel mid-flight would be one the debugger never issued — nobody asks
    /// a running program to resume — and acting on it would silently change the step mode of
    /// a query nobody is stopped in.</para>
    ///
    /// <para><b>The pause.</b> When the user hits Break All, the answer is NOT to freeze the
    /// process and describe wherever it landed. A Prolog machine stopped mid-instruction is
    /// halfway through a unification or inside a builtin: it has no call stack to show, and
    /// the last one it had is not where it is. So a pause is a REQUEST
    /// (<see cref="DebugCommandKind.BreakNow"/>): the engine reads it here, between goals,
    /// and stops at the next port — a real stop, microseconds later, with a stack that is
    /// true. That is what every interpreter's debugger does with a pause, and it is why the
    /// engine no longer keeps a rendered stack lying around "just in case" (it did, on a
    /// 50 ms clock; rendering the whole environment chain twenty times a second is what made
    /// a real program under the debugger never finish).</para></summary>
    private void PollWhileRunning()
    {
        // Prolog is moving, and a debugger that wants to pause needs to know it — otherwise
        // it cannot tell "stop at the next port, it is microseconds away" from "no port is
        // ever coming, freeze the process". One word per poll.
        _channel.Heartbeat();

        bool stopNow = false;
        StopReason reason = StopReason.AsyncBreak;
        foreach (var command in _channel.DrainCommands())
        {
            switch (command.Kind)
            {
                case DebugCommandKind.AddBreakpoint:
                case DebugCommandKind.RemoveBreakpoint:
                case DebugCommandKind.ClearBreakpoints:
                case DebugCommandKind.SetLastCallOptimisation:
                    Apply(_service, command);
                    break;

                // The user asked to pause. Stop at THIS port: we are standing on one.
                case DebugCommandKind.BreakNow:
                    stopNow = true;
                    reason = StopReason.AsyncBreak;
                    break;

                // A stop nobody wants, for its side effect: a debugger can only build the
                // things that represent a source file — the things a breakpoint binds
                // against — from inside a real stop event, and until it has them no
                // breakpoint can bind, so no stop can ever happen. One of them has to go
                // first. This is it. See DebugCommandKind.Hello.
                case DebugCommandKind.Hello:
                    stopNow = true;
                    reason = StopReason.AsyncBreak;
                    break;
            }
        }

        if (!stopNow) return;

        DebugStopEvent? here = _service.CaptureNow();

        // THIS STOP IS WHERE THE NEXT STEP IS FROM. A step is measured against the depth of
        // the stop it was taken at, and this stop does not go through the service's own Stop()
        // — so without this, an F10 at a Break All was measured against whatever the LAST real
        // stop left behind (or zero, if there had never been one). Paused 200 frames deep,
        // the step waited for a port at depth ≤ 0: the program ran to completion and never
        // stopped again.
        _service.NoteStop(here?.Depth ?? 0);

        OnStop(_service, here ?? new DebugStopEvent(
            reason, "", "", 0, 0, Array.Empty<PrologEngine.DebugFrame>()));
    }

    /// <summary>The channel the debugger reads and writes. Its addresses are what
    /// <see cref="ShumwayDebugHelper.Attach"/> hands out.</summary>
    public DebugChannel Channel => _channel;

    /// <summary>ADR-035 — arm "stop at the entry point": the first goal of the next query
    /// stops the debugger, at the program's start. Used by the <c>--debug-wait</c> path once
    /// a debugger has attached, so the user lands at the entry rather than watching the
    /// program run past it.</summary>
    public void ArmEntryBreak() => _service.ArmEntryBreak();

    /// <summary>
    /// ADR-035 D4 — hold the door until the debugger has actually SAID something.
    ///
    /// <para>"A debugger is attached" is not the same as "a debugger is ready". Under a
    /// launch, <c>Debugger.IsAttached</c> goes true the instant the process starts, while
    /// the components on the other side are still finding the channel and arming the
    /// breakpoints the user drew before pressing the button. Consulting the program in that
    /// window means running it past every one of them.</para>
    ///
    /// <para>So we wait for the first command batch — the debugger writes its whole desired
    /// state as soon as it is ready, so ANY command is the signal — and apply it. Then the
    /// program is consulted, with its breakpoints already armed. Returns false on timeout,
    /// which is not fatal: a debugger that never speaks is one the user attached for a
    /// different reason, and the program should still run.</para>
    /// </summary>
    public bool WaitForDebuggerCommands(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        long nextPing = 0;
        long quietUntil = 0;      // set once the debugger has said anything at all
        bool heard = false;

        while (Environment.TickCount64 < deadline)
        {
            // The debugger's FIRST word is not its last. It answers the bootstrap stop with
            // the state it has — which, the first time, is nothing: it has only just learned
            // which file we are about to consult, and Visual Studio has not yet bound the
            // breakpoints the user drew on it. Those arrive milliseconds later, and treating
            // the first batch as "ready" ran the whole program in the gap (measured: 33 ms
            // early, every breakpoint missed). So we wait for the debugger to go QUIET.
            if (heard && Environment.TickCount64 >= quietUntil)
                return true;

            // Give the debugger a STOP to work with. Its hidden breakpoint is armed before we
            // have run anything (it reads the token out of the DLL on disk — it has to, since
            // this session did not exist when the assembly loaded), but a breakpoint that is
            // never reached tells it nothing: the channel, and the names of the files we are
            // about to consult, are still unread. This is a port that costs nothing and
            // happens nowhere, whose only purpose is to be stopped at.
            if (Environment.TickCount64 >= nextPing)
            {
                nextPing = Environment.TickCount64 + 200;
                _notify((int)StopReason.AsyncBreak);
            }

            var commands = _channel.DrainCommands();
            if (commands.Count > 0)
            {
                foreach (var command in commands)
                {
                    // Only breakpoints: a step or a continue here would be one the debugger
                    // never issued (see PollWhileRunning) — nobody steps a program that has
                    // not started.
                    switch (command.Kind)
                    {
                        case DebugCommandKind.AddBreakpoint:
                        case DebugCommandKind.RemoveBreakpoint:
                        case DebugCommandKind.ClearBreakpoints:
                        case DebugCommandKind.SetLastCallOptimisation:
                            Apply(_service, command);
                            break;
                    }
                }
                heard = true;
                quietUntil = Environment.TickCount64 + QuietMs;
            }
            System.Threading.Thread.Sleep(20);
        }
        return heard;
    }

    /// <summary>How long the debugger has to stay silent before we believe it has finished
    /// setting up. Long enough for Visual Studio to bind a breakpoint against a module it has
    /// only just been told about (measured at tens of milliseconds), short enough that a
    /// program launched under a debugger still starts promptly.</summary>
    public const int QuietMs = 750;

    /// <summary>ADR-035 — writes the CURRENT stack into the channel, at no port at all,
    /// and returns the snapshot's sequence number (0 if nothing is running).
    ///
    /// <para>This is the asynchronous break. The user hits Break All; the process stops
    /// wherever it happens to be, which is nowhere in particular; the channel still holds
    /// the last real stop, and showing that would be a lie. So the debugger asks — by
    /// func-eval, which is safe here because this is a normal stop and not the
    /// breakpoint-notification context where a func-eval deadlocks — and the engine
    /// answers from the machine that was last running.</para></summary>
    public int CaptureNow()
    {
        DebugStopEvent? stop = _service.CaptureNow();
        if (stop is null) return 0;
        // A step taken from this stop is measured against THIS depth — see PollWhileRunning.
        _service.NoteStop(stop.Depth);
        _channel.WriteSnapshot(stop);
        return _channel.Sequence;
    }

    /// <summary>ADR-035 — <c>debugger_break/0</c>: stop the debugger HERE, at this goal.
    ///
    /// <para>In a managed process a break is something the program itself can ask the
    /// runtime for, and the debugger honours it — no breakpoint, no channel, no negotiation.
    /// So this is the one path into the debugger that needs nothing to have gone right
    /// first: no symbols loaded, no modules built, no breakpoint bound. Which is exactly
    /// what makes it the tool for debugging the debugger, as well as the program.</para>
    ///
    /// <para>The snapshot goes in first, as at every stop, so the stack is already in memory
    /// when the debugger takes the process. The command channel is drained afterwards, so a
    /// step taken from here works like a step taken from anywhere else.</para></summary>
    internal void BreakHere(Shumway.Core.Activation activation)
    {
        // The suppression fallback covers debugger_break/0 inside an evaluated goal too.
        if (_service.EvaluationInFlight && DebugService.SuppressStopsDuringEvaluation)
            return;

        lock (_gate)
        {
            _service.Current = activation;
            DebugStopEvent? here = _service.CaptureNow();
            if (here is null) return;

            // A step taken from here is measured against THIS depth, like a step from any
            // other stop.
            _service.NoteStop(here.Depth);

            _channel.WriteSnapshot(here);
            System.Diagnostics.Debugger.Break();

            foreach (var command in _channel.DrainCommands())
                Apply(_service, command);
            _channel.SetRunning();
        }
    }

    /// <summary>ADR-035 — the Immediate window's goal evaluation, bracketed by the channel:
    /// the eval's own stops overwrite the snapshot buffer, and when it finishes Visual
    /// Studio returns the user to the ORIGINAL break state — whose Locals must find the
    /// frames they were reading, not the evaluated goal's. Runs on the engine's own
    /// stopped thread (a func-eval), which already holds the stop gate — the nested
    /// stops re-enter it reentrantly, same thread.</summary>
    internal string EvaluateGoal(int frameIndex, string goalText)
    {
        // A breakpoint the user drew WHILE STOPPED is not armed yet. In break state the
        // engine thread is parked inside the notify holding the gate, and the channel is
        // drained only when it RESUMES — so an F9 the user set a moment ago sits unread in
        // the command region, and the goal we are about to run would sail straight past it.
        // Apply it first, exactly as a step would (a step's own resume drains the channel
        // before it runs). Then a breakpoint set at the stop is honoured by the evaluation,
        // the same as one set before it.
        ApplyPendingBreakpointCommands();

        byte[] saved = _channel.SaveSnapshotBytes();
        try
        {
            return _service.EvaluateGoal(frameIndex, goalText);
        }
        finally
        {
            _channel.RestoreSnapshotBytes(saved);
        }
    }

    /// <summary>Drain the command region and apply the breakpoint changes in it NOW, so code
    /// that runs from a stop before the normal resume-time drain — an Immediate-window
    /// evaluation — sees the breakpoints the user has set while stopped.
    ///
    /// <para>Only breakpoint and LCO commands are obeyed; a step, a continue, or a pause is
    /// written back untouched, because it belongs to the resume that the outer break has not
    /// taken yet — draining is not the same as consuming someone else's command. (In
    /// practice none is present: a user typing in the Immediate window has not asked to
    /// resume. The write-back is the belt to that braces.)</para></summary>
    private void ApplyPendingBreakpointCommands()
    {
        List<DebugCommand>? deferred = null;
        foreach (var command in _channel.DrainCommands())
        {
            switch (command.Kind)
            {
                case DebugCommandKind.AddBreakpoint:
                case DebugCommandKind.RemoveBreakpoint:
                case DebugCommandKind.ClearBreakpoints:
                case DebugCommandKind.SetLastCallOptimisation:
                    Apply(_service, command);
                    break;
                default:
                    (deferred ??= new List<DebugCommand>()).Add(command);
                    break;
            }
        }
        if (deferred != null)
            _channel.WriteCommands(deferred.ToArray());
    }

    /// <summary>ADR-035 — "I have just consulted a file you have not heard of." A stop nobody
    /// asked for and nobody sees: the debugger reads the new file list, builds the module for
    /// it (which it can only do from inside a real stop), and lets the program straight on.
    ///
    /// <para>Only when a debugger is actually there — a program consulting a hundred files at
    /// its own top level must not trip a hundred stops for nobody.</para></summary>
    internal void SourceFileConsulted()
    {
        if (_disposed || !System.Diagnostics.Debugger.IsAttached) return;
        lock (_gate)
            OnStopLocked(new DebugStopEvent(
                StopReason.SourcesChanged, "", "", 0, 0, Array.Empty<PrologEngine.DebugFrame>()));
    }

    private void OnStop(DebugService service, DebugStopEvent stop)
    {
        // The idle watcher answers the channel when the engine is not running. It decides
        // that by the heartbeat, which cannot be perfectly timed against a query that is
        // just starting — so the two take turns rather than race.
        lock (_gate)
            OnStopLocked(stop);
    }

    private void OnStopLocked(DebugStopEvent stop)
    {
        DebugService service = _service;

        // 1. Write, so the answer is there before the question.
        _channel.WriteSnapshot(stop);

        // 2. Notify. A debugger is attached: this is where the process stops, and it
        //    does not come back until the debugger says so.
        _notify((int)stop.Reason);

        // 3. Drain. Whatever the debugger decided while we were stopped is now waiting.
        foreach (var command in _channel.DrainCommands())
            Apply(service, command);

        // 4. Say that the stack in the buffer is now HISTORY. From here until the next stop
        //    there is no Prolog stack to show, and a debugger that freezes the process (a
        //    raw Break All, a breakpoint in C#) must not be handed the last one as if it
        //    were current.
        _channel.SetRunning();
    }

    private void Apply(DebugService service, DebugCommand command)
    {
        switch (command.Kind)
        {
            case DebugCommandKind.Continue:
                service.Resume(StepMode.Continue);
                break;
            case DebugCommandKind.StepInto:
                service.Resume(StepMode.Into);
                break;
            case DebugCommandKind.StepOver:
                service.Resume(StepMode.Over);
                break;
            case DebugCommandKind.StepOut:
                service.Resume(StepMode.Out);
                break;
            case DebugCommandKind.AddBreakpoint:
                // The condition rides the add (empty = unconditional): the debugger's
                // full-state rewrites make setting, changing and clearing a condition
                // the same idempotent operation.
                _engine.AddBreakpoint(command.File, command.Line,
                    command.Condition.Length == 0 ? null : command.Condition);
                break;
            case DebugCommandKind.RemoveBreakpoint:
                _engine.RemoveBreakpoint(command.File, command.Line);
                break;
            case DebugCommandKind.ClearBreakpoints:
                _engine.ClearBreakpoints();
                break;
            case DebugCommandKind.SetLastCallOptimisation:
                // Both: the flag, so later queries agree, and the machine we are stopped
                // in, so it takes effect on the next goal rather than the next query. A
                // debugger turns this off to see the stack it is standing on right now.
                _engine.SetDebugLastCall(command.Flag);
                service.SetLastCallOptimisation(command.Flag);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.CancelEvaluation();   // drop any goal parked mid-backtracking
        _engine.AttachDebugSession(null);

        // The watcher must be gone before the channel it reads is unpinned. It wakes at least
        // every tick, so this is a short wait; the bound is there because a session that
        // cannot be disposed is worse than one that leaks a thread.
        _idleWatcher?.Join(IdleTickMs * 5);
        _idleWatcher = null;

        if (ReferenceEquals(ShumwayDebugHelper.Channel, _channel))
        {
            ShumwayDebugHelper.Channel = null;
            ShumwayDebugHelper.Session = null;
        }
        _channel.Dispose();
    }
}
