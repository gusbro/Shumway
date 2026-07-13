using System;

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

        ShumwayDebugHelper.Channel = _channel;
        ShumwayDebugHelper.Session = this;
        engine.AttachDebugSession(_service);
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
    /// <para><b>The asynchronous break.</b> When the user hits Break All the process stops at
    /// no port, nothing has been reported, and the debugger wants the stack NOW. It cannot
    /// ask for it: Visual Studio will evaluate a method in the debuggee only on the thread it
    /// considers current, and refuses outright once that method touches an intrinsic — which
    /// the capture path does, deep inside the machine. Both of those were learned by running
    /// it, and together they close the door on asking.</para>
    ///
    /// <para>So the engine does not wait to be asked. It leaves a recent answer lying in the
    /// buffer, refreshed on a CLOCK rather than on a count of goals — so the cost is bounded
    /// by time, not by how fast the program happens to run. What a Break All shows is a real
    /// port the machine passed through, at most <see cref="SampleIntervalMs"/> ago; not a
    /// synthetic mid-unification state, and not a lie.</para></summary>
    private void PollWhileRunning()
    {
        bool hello = false;
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
                case DebugCommandKind.Hello:
                    hello = true;
                    break;
            }
        }

        if (hello)
        {
            // A stop nobody wants, for its side effect: a debugger can only build the things
            // that represent a source file — the things a breakpoint binds against — from
            // inside a real stop event, and until it has them no breakpoint can bind, so no
            // stop can ever happen. One of them has to go first. This is it. See
            // DebugCommandKind.Hello.
            OnStop(_service, _service.CaptureNow()
                ?? new DebugStopEvent(StopReason.AsyncBreak, "", "", 0, 0, Array.Empty<PrologEngine.DebugFrame>()));
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastSample < SampleIntervalMs) return;
        _lastSample = now;

        DebugStopEvent? sample = _service.CaptureNow();
        if (sample is not null) _channel.WriteSnapshot(sample);
    }

    /// <summary>How stale the asynchronous break's answer may be. Short enough that no human
    /// can tell; long enough that rendering a stack of terms twenty times a second is nothing
    /// beside what a debug session already costs.</summary>
    public const int SampleIntervalMs = 50;

    private long _lastSample;

    /// <summary>The channel the debugger reads and writes. Its addresses are what
    /// <see cref="ShumwayDebugHelper.Attach"/> hands out.</summary>
    public DebugChannel Channel => _channel;

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
        _channel.WriteSnapshot(stop);
        return _channel.Sequence;
    }

    private void OnStop(DebugService service, DebugStopEvent stop)
    {
        // 1. Write, so the answer is there before the question.
        _channel.WriteSnapshot(stop);

        // 2. Notify. A debugger is attached: this is where the process stops, and it
        //    does not come back until the debugger says so.
        _notify((int)stop.Reason);

        // 3. Drain. Whatever the debugger decided while we were stopped is now waiting.
        foreach (var command in _channel.DrainCommands())
            Apply(service, command);
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
                _engine.AddBreakpoint(command.File, command.Line);
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
        _engine.AttachDebugSession(null);
        if (ReferenceEquals(ShumwayDebugHelper.Channel, _channel))
        {
            ShumwayDebugHelper.Channel = null;
            ShumwayDebugHelper.Session = null;
        }
        _channel.Dispose();
    }
}
