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
    /// <see cref="ShumwayDebugHelper.Notify"/>, which is where a real debugger plants its
    /// hidden breakpoint; a test passes its own, because there is no debugger to stop it
    /// and it has to play that part itself.</param>
    public ChannelDebugSession(PrologEngine engine, Action<int>? notify = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _channel = new DebugChannel();
        _notify = notify ?? ShumwayDebugHelper.Notify;
        _service = new DebugService(engine, OnStop);

        ShumwayDebugHelper.Channel = _channel;
        engine.AttachDebugSession(_service);
    }

    /// <summary>The channel the debugger reads and writes. Its addresses are what
    /// <see cref="ShumwayDebugHelper.Attach"/> hands out.</summary>
    public DebugChannel Channel => _channel;

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
            ShumwayDebugHelper.Channel = null;
        _channel.Dispose();
    }
}
