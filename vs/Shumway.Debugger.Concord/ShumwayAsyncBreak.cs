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
// When Prolog is not running at all — the engine is blocked in a read, the query has
// finished, the thread is deep in someone's C# — no port is ever coming, and honouring the
// pause that way would hang the IDE. Then we decline, and the CLR freezes the process as it
// always did: the C# stack is the truth in that case, and Visual Studio already shows it.

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

            if (!PrologIsRunning(process, state))
            {
                ShumwayLog.Write("pause: Prolog is not running — leaving the break to the CLR");
                throw new NotImplementedException();
            }

            state.AsyncBreakPending = true;
            ShumwayRemoteComponent.RequestBreakNow(process, state);
            ShumwayLog.Write("pause: asked the engine to stop at its next port");

            // And that is all. The process keeps running — for microseconds — and the stop
            // arrives as a notify, where the break is completed against the port it landed
            // on. See ShumwayRemoteComponent.OnRuntimeBreakpointReceived.
        }

        /// <summary>Whether the engine is passing goals right now — the question that decides
        /// whether a pause can be honoured at a port at all. Answered by watching the
        /// heartbeat the engine bumps as it runs, because there is nothing else to ask: a
        /// debugger cannot make a running debuggee tell it anything.</summary>
        private static bool PrologIsRunning(DkmProcess process, ShumwayServerDataItem state)
        {
            int before = ReadHeartbeat(process, state);
            Thread.Sleep(HeartbeatWatchMs);
            return ReadHeartbeat(process, state) != before;
        }

        private static int ReadHeartbeat(DkmProcess process, ShumwayServerDataItem state)
        {
            try
            {
                byte[] header = new byte[DebugWire.HeartbeatOffset + 4];
                process.ReadMemory((ulong)state.SnapshotAddress, DkmReadMemoryFlags.None, header);
                int at = DebugWire.HeartbeatOffset;
                return DebugWire.ReadInt(header, ref at);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
