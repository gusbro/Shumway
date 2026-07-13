// Shumway debugger - the pause (ADR-035, phase D5).
//
// What "Break All" has to mean for an interpreter.
//
// Freezing the process where it happens to be is the wrong answer, and the reason is not
// performance — it is that there is no correct thing to SHOW. A Prolog machine stopped at
// an arbitrary instruction is halfway through a unification, three levels into a builtin,
// between two environment frames. It has no call stack at that moment. The stack it had at
// the last breakpoint is not where it is, and painting that on the screen is not a slightly
// stale answer, it is a wrong one.
//
// (The engine used to keep a rendered stack lying around for exactly this moment, refreshed
// on a 50 ms clock. It was both: a lie — the stack shown was never quite the program's —
// and ruinously expensive, since a refresh walks the whole environment chain and renders
// every variable of every frame. A real program could not finish under the debugger.)
//
// So a pause is a REQUEST, which is what every interpreter's debugger does: keep running,
// briefly, and stop at the next point where the stack means something. In this engine that
// point is a PORT, and it is microseconds away in any program that is actually running. The
// request goes down the command channel; the engine stops at its next port and reports it;
// the break completes THERE, with a stack that is true.
//
// When Prolog is not running at all — the engine is sitting at its prompt waiting for a
// query — there is no port coming, and this used to DECLINE (a thrown NotImplementedException,
// which is how a stepper declines). There is no fallback for that here: Visual Studio put up
// "Unable to break execution. Not implemented" and the user was left with a dialog instead of
// a debugger. And declining was never necessary — the engine grants a stop when it is idle
// too (ChannelDebugSession's idle watcher, which exists so a breakpoint can be set on an
// engine that has never run a goal). So the request is the same either way: ask, and the stop
// comes back, with a Prolog stack when there is one and without when there is not.
//
// A process with no Shumway session in it is the one case we still decline — and there the
// throw is right, because there is nothing of ours in the debuggee to ask.

using System;
using System.Threading;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Shumway.Embedding.Debugging;

namespace Shumway.Debugger.Concord
{
    public sealed class ShumwayAsyncBreak : IDkmAsyncBreak
    {
        /// <summary>How long to watch the engine's heartbeat before deciding it is not
        /// moving. A running engine bumps it every few hundred goals — far faster than this;
        /// a stopped one never will. Short enough that declining still feels like an instant
        /// pause.</summary>
        private const int HeartbeatWatchMs = 60;

        void IDkmAsyncBreak.AsyncBreak(DkmProcess process, bool immediateBreak)
        {
            ShumwayServerDataItem state = ShumwayRemoteComponent.State(process);

            // Not a Shumway session, or one whose channel we have not found yet: this pause
            // is not ours to answer. Declining is a THROW — Concord reads it as "not mine"
            // and moves on to the next component, which is the CLR's own async break.
            if (state.SnapshotAddress == 0 || state.CommandAddress == 0)
                throw new NotImplementedException();

            state.AsyncBreakPending = true;
            ShumwayRemoteComponent.RequestBreakNow(process, state);
            ShumwayLog.Write("pause: asked the engine to stop");

            // And that is all. A running engine reaches its next port in microseconds and
            // stops there, with a real stack; an idle one grants the stop from its watcher,
            // with no stack, because there is nothing running to have one. Either way the
            // stop arrives as a notify, and the break is completed there. See
            // ShumwayRemoteComponent.OnRuntimeBreakpointReceived.
        }
    }
}
