// Shumway debugger - server-level Concord component (ADR-035, phases D2 + D3).
//
// The monitor side of the session. It owns four things:
//
//   * The HIDDEN breakpoint on ShumwayDebugHelper.Notify — the one place the engine calls
//     when it decides to stop. Every Prolog stop arrives here first.
//
//   * The custom runtime and one module per consulted .pl. Creating Dkm objects only
//     works in a real EVENT context (D0: from a message the stack walk sent, it throws on
//     the walk's transient container), so it is tried at every process pause.
//
//   * BREAKPOINTS. VS binds F9 in a .pl through our symbol provider and hands the bound
//     breakpoint to EnableRuntimeBreakpoint. We do not patch anything ourselves — the
//     engine does, and it is the only one that can: a Prolog "line" is a set of bytecode
//     stop sites, and which byte to overwrite is a question only the compiler's own site
//     table can answer. So the answer goes down the command channel and the engine arms
//     it, whether it is running or stopped.
//
//   * STEPPING. A step is not a range of addresses to run past — that is a frame-based
//     debugger's idea, and it cannot express redo or fail. It is "run until the next PORT
//     satisfying a condition", which is the engine's business. So Step writes the mode and
//     gets out of the way, and the port that satisfies it comes back as a notify.
//
// The command region always carries the WHOLE desired state (clear + every armed
// breakpoint, plus a step if one is pending). There is no acknowledgement in this channel
// and none is needed: a full state is idempotent.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.Clr;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.CustomRuntimes;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Microsoft.VisualStudio.Debugger.Stepping;
using Microsoft.VisualStudio.Debugger.Symbols;
using Shumway.Embedding.Debugging;

namespace Shumway.Debugger.Concord
{
    internal sealed class ShumwayServerDataItem : DkmDataItem
    {
        public long SnapshotAddress;
        public long CommandAddress;
        public DkmRuntimeInstructionBreakpoint? NotifyBreakpoint;

        /// <summary>The user's breakpoints, by the line they are DRAWN on — which is the
        /// line the engine reports back when one is hit, precisely so this lookup can be
        /// exact (see PrologEngine.BreakpointRequestAt).</summary>
        public readonly Dictionary<string, DkmRuntimeBreakpoint> Breakpoints =
            new Dictionary<string, DkmRuntimeBreakpoint>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The step in flight, if any. Completed by the port that satisfies it.</summary>
        public DkmStepper? Stepper;
        public DebugCommandKind PendingStep = DebugCommandKind.None;

        /// <summary>True while the process is stopped AT A PORT — which is what makes the
        /// Prolog runtime, rather than the CLR, the owner of the execution location.</summary>
        public bool StoppedAtPort;

        public readonly List<string> PendingFiles = new List<string>();
        public readonly HashSet<string> CreatedFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static string Key(string file, int line) =>
            file + "|" + line.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class ShumwayRemoteComponent
        : IDkmCustomMessageForwardReceiver, IDkmRuntimeBreakpointReceived,
          IDkmProcessExecutionNotification, IDkmRuntimeMonitorBreakpointHandler,
          IDkmRuntimeStepper
    {
        DkmCustomMessage? IDkmCustomMessageForwardReceiver.SendLower(DkmCustomMessage customMessage)
        {
            DkmProcess process = customMessage.Process;
            ShumwayServerDataItem state = GetState(process);

            switch (customMessage.MessageCode)
            {
                case ShumwayGuids.MsgArmNotifyBreakpoint:
                    state.SnapshotAddress = (long)customMessage.Parameter1;
                    state.CommandAddress = long.Parse(
                        (string)customMessage.Parameter3, CultureInfo.InvariantCulture);
                    ArmNotifyBreakpoint(process, state, (int)customMessage.Parameter2);
                    // A breakpoint the user set before we were listening is already bound;
                    // this is the first moment we can tell the engine about it.
                    WriteCommands(process, state);
                    break;

                case ShumwayGuids.MsgEnsureModules:
                    foreach (string path in ((string)customMessage.Parameter1).Split('|'))
                    {
                        if (path.Length > 0 && !state.CreatedFiles.Contains(path))
                            state.PendingFiles.Add(path);
                    }
                    EnsureModules(process, state); // may fail here; retried at the next pause
                    break;
            }
            return null;
        }

        // ---- breakpoints ----

        /// <summary>VS bound an F9 in a .pl through our symbol provider. The address it
        /// hands back is one of OURS: the module is the file, the offset is the line.</summary>
        void IDkmRuntimeMonitorBreakpointHandler.EnableRuntimeBreakpoint(
            DkmRuntimeBreakpoint runtimeBreakpoint)
        {
            if (!TryLocate(runtimeBreakpoint, out string file, out int line))
                return;

            ShumwayServerDataItem state = GetState(runtimeBreakpoint.Process);
            state.Breakpoints[ShumwayServerDataItem.Key(file, line)] = runtimeBreakpoint;
            WriteCommands(runtimeBreakpoint.Process, state);
        }

        void IDkmRuntimeMonitorBreakpointHandler.DisableRuntimeBreakpoint(
            DkmRuntimeBreakpoint runtimeBreakpoint)
        {
            if (!TryLocate(runtimeBreakpoint, out string file, out int line))
                return;

            ShumwayServerDataItem state = GetState(runtimeBreakpoint.Process);
            state.Breakpoints.Remove(ShumwayServerDataItem.Key(file, line));
            WriteCommands(runtimeBreakpoint.Process, state);
        }

        /// <summary>Whether the breakpoint can bind at all. The engine is the only one who
        /// knows (a line with no code, or one inside a <c>:- disable_debug.</c> region,
        /// binds nowhere), and it cannot be asked from here without running something in
        /// the debuggee. So we accept, and a breakpoint that never binds simply never
        /// fires — which is what a hollow breakpoint looks like anyway. Reporting
        /// hollowness properly needs the engine to answer back through the channel: D5.</summary>
        void IDkmRuntimeMonitorBreakpointHandler.TestRuntimeBreakpoint(
            DkmRuntimeBreakpoint runtimeBreakpoint)
        {
        }

        private static bool TryLocate(DkmRuntimeBreakpoint breakpoint, out string file, out int line)
        {
            file = "";
            line = 0;
            if (breakpoint is DkmRuntimeInstructionBreakpoint instruction
                && instruction.InstructionAddress is DkmCustomInstructionAddress custom
                && custom.ModuleInstance != null)
            {
                file = custom.ModuleInstance.FullName;
                line = (int)custom.Offset;
                return true;
            }
            return false;
        }

        // ---- stepping ----

        bool IDkmRuntimeStepper.OwnsCurrentExecutionLocation(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper, DkmStepArbitrationReason reason)
        {
            // Only when the machine is stopped at a port. Anywhere else — inside a builtin,
            // mid-unification, in the user's own C# — the CLR owns the location and should
            // keep it: a step there is a C# step, and it is not ours to take.
            return GetState(runtimeInstance.Process).StoppedAtPort;
        }

        void IDkmRuntimeStepper.Step(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper, DkmStepArbitrationReason reason)
        {
            ShumwayServerDataItem state = GetState(runtimeInstance.Process);
            state.Stepper = stepper;
            state.PendingStep = StepKindOf(stepper);
            WriteCommands(runtimeInstance.Process, state);
            // Nothing else to arrange. VS resumes the process; the engine reads the mode
            // out of the channel and stops at the first port that satisfies it, which
            // comes back to us as a notify.
        }

        private static DebugCommandKind StepKindOf(DkmStepper stepper)
        {
            switch (stepper.StepKind)
            {
                case DkmStepKind.Into: return DebugCommandKind.StepInto;
                case DkmStepKind.Out: return DebugCommandKind.StepOut;
                default: return DebugCommandKind.StepOver;
            }
        }

        void IDkmRuntimeStepper.StopStep(DkmRuntimeInstance runtimeInstance, DkmStepper stepper)
        {
            ShumwayServerDataItem state = GetState(runtimeInstance.Process);
            state.Stepper = null;
            state.PendingStep = DebugCommandKind.None;
            WriteCommands(runtimeInstance.Process, state);
        }

        void IDkmRuntimeStepper.BeforeEnableNewStepper(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper)
        {
        }

        void IDkmRuntimeStepper.AfterSteppingArbitration(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper,
            DkmStepArbitrationReason reason, DkmRuntimeInstance newControllingRuntimeInstance)
        {
        }

        void IDkmRuntimeStepper.OnNewControllingRuntimeInstance(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper,
            DkmStepArbitrationReason reason, DkmRuntimeInstance controllingRuntimeInstance)
        {
        }

        bool IDkmRuntimeStepper.StepControlRequested(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper,
            DkmStepArbitrationReason reason, DkmRuntimeInstance callingRuntimeInstance)
        {
            return true;
        }

        void IDkmRuntimeStepper.TakeStepControl(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper, bool leaveGuardsInPlace,
            DkmStepArbitrationReason reason, DkmRuntimeInstance callingRuntimeInstance)
        {
        }

        void IDkmRuntimeStepper.NotifyStepComplete(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper)
        {
        }

        // ---- the stop ----

        void IDkmRuntimeBreakpointReceived.OnRuntimeBreakpointReceived(
            DkmRuntimeBreakpoint runtimeBreakpoint, DkmThread thread,
            bool hasException, DkmEventDescriptorS eventDescriptor)
        {
            if (runtimeBreakpoint.SourceId != ShumwayGuids.NotifyBreakpointSource)
                return;

            // The hidden breakpoint is ours and the user must never see it. What the user
            // sees is what we report below — their breakpoint, or their step.
            eventDescriptor.Suppress();

            DkmProcess process = thread.Process;
            ShumwayServerDataItem state = GetState(process);
            EnsureModules(process, state); // a real event context

            DebugSnapshot? snapshot = ReadSnapshot(process, state);
            if (snapshot == null)
                return; // nothing to report: let it run on

            state.StoppedAtPort = true;

            if (snapshot.Reason == StopReason.Breakpoint)
            {
                // Matched on the line the user SET it on, not the line it bound to (a
                // breakpoint on a rule's head binds at its first goal). The snapshot
                // carries both, precisely so this lookup is exact.
                DkmRuntimeBreakpoint? bound;
                if (state.Breakpoints.TryGetValue(
                        ShumwayServerDataItem.Key(snapshot.BreakFile, snapshot.BreakLine), out bound))
                {
                    // A breakpoint hit cancels any step in flight — VS calls StopStep.
                    bound.OnHit(thread, false);
                    return;
                }
                // An engine-side breakpoint nobody drew (the REPL's own, a stale arm).
                // Not ours to stop on.
                state.StoppedAtPort = false;
                return;
            }

            // Any other port that reached us is a step landing: the engine only reports a
            // port when a step asked it to.
            DkmStepper? stepper = state.Stepper;
            if (stepper != null)
            {
                state.Stepper = null;
                state.PendingStep = DebugCommandKind.None;
                stepper.OnStepComplete(thread, false);
                return;
            }
            state.StoppedAtPort = false;
        }

        // ---- modules ----

        void IDkmProcessExecutionNotification.OnProcessPause(
            DkmProcess process, DkmProcessExecutionCounters processCounters)
        {
            EnsureModules(process, GetState(process));
        }

        void IDkmProcessExecutionNotification.OnProcessResume(
            DkmProcess process, DkmProcessExecutionCounters processCounters)
        {
            GetState(process).StoppedAtPort = false;
        }

        /// <summary>One module per .pl. The module IS the file — that identity is what
        /// makes a source position out of nothing more than (module, line), and what lets
        /// F9 in an editor find code that has no symbols on disk at all.</summary>
        private static void EnsureModules(DkmProcess process, ShumwayServerDataItem state)
        {
            if (state.PendingFiles.Count == 0)
                return;
            try
            {
                DkmCustomRuntimeInstance? runtime = process.GetRuntimeInstances()
                    .OfType<DkmCustomRuntimeInstance>()
                    .FirstOrDefault(r => r.Id.RuntimeType == ShumwayGuids.RuntimeType);
                if (runtime == null)
                {
                    runtime = DkmCustomRuntimeInstance.Create(
                        process, new DkmRuntimeInstanceId(ShumwayGuids.RuntimeType, 0), null);
                }

                foreach (string path in state.PendingFiles.ToArray())
                {
                    var module = DkmModule.Create(
                        new DkmModuleId(ShumwayGuids.ModuleIdFor(path), ShumwayGuids.SymbolProvider),
                        path,
                        new DkmCompilerId(ShumwayGuids.ShumwayVendor, ShumwayGuids.ShumwayLanguage),
                        process.Connection, null);

                    var moduleInstance = DkmCustomModuleInstance.Create(
                        System.IO.Path.GetFileName(path), path,
                        0, runtime, null, null,
                        DkmModuleFlags.None, DkmModuleMemoryLayout.Unknown,
                        0, 0, 0, "Shumway Prolog", false, null, null, null);

                    // Create WITHOUT the module, then SetModule: that raises the
                    // symbols-loaded notification, which is what makes VS re-evaluate
                    // pending breakpoints against our symbol provider.
                    moduleInstance.SetModule(module, true);

                    state.CreatedFiles.Add(path);
                    state.PendingFiles.Remove(path);
                }
            }
            catch (Exception)
            {
                // Wrong context (the stack-walk case). The files stay pending and the next
                // process pause picks them up.
            }
        }

        // ---- the channel ----

        private static DebugSnapshot? ReadSnapshot(DkmProcess process, ShumwayServerDataItem state)
        {
            if (state.SnapshotAddress == 0) return null;
            try
            {
                var bytes = new byte[64 * 1024];
                process.ReadMemory((ulong)state.SnapshotAddress, DkmReadMemoryFlags.None, bytes);
                return DebugWire.ReadSnapshot(bytes);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Writes the whole state the engine should be in: every breakpoint the
        /// user has, and the step it should take next if one is pending. Idempotent by
        /// construction, so it does not matter whether the engine has drained the previous
        /// one — which is the only reason a channel with no acknowledgement works.</summary>
        private static void WriteCommands(DkmProcess process, ShumwayServerDataItem state)
        {
            if (state.CommandAddress == 0)
                return;

            var commands = new List<DebugWireCommand>
            {
                new DebugWireCommand { Kind = DebugCommandKind.ClearBreakpoints },
            };
            foreach (KeyValuePair<string, DkmRuntimeBreakpoint> entry in state.Breakpoints)
            {
                int bar = entry.Key.LastIndexOf('|');
                commands.Add(new DebugWireCommand
                {
                    Kind = DebugCommandKind.AddBreakpoint,
                    File = entry.Key.Substring(0, bar),
                    Line = int.Parse(entry.Key.Substring(bar + 1), CultureInfo.InvariantCulture),
                });
            }
            if (state.PendingStep != DebugCommandKind.None)
                commands.Add(new DebugWireCommand { Kind = state.PendingStep });

            try
            {
                process.WriteMemory(
                    (ulong)state.CommandAddress, DebugWire.EncodeCommands(commands));
            }
            catch (Exception)
            {
                // A process that has exited, or one not yet listening. Nothing to do: the
                // next write carries the same full state.
            }
        }

        // ---- the hidden notify breakpoint ----

        private static void ArmNotifyBreakpoint(
            DkmProcess process, ShumwayServerDataItem state, int notifyToken)
        {
            if (state.NotifyBreakpoint != null)
                return;
            try
            {
                DkmClrRuntimeInstance clr = process.GetRuntimeInstances()
                    .OfType<DkmClrRuntimeInstance>()
                    .First();

                DkmClrModuleInstance module = clr.GetModuleInstances()
                    .OfType<DkmClrModuleInstance>()
                    .First(m => string.Equals(m.Name, ShumwaySession.EngineModule,
                        StringComparison.OrdinalIgnoreCase));

                DkmClrInstructionAddress address;
                try
                {
                    address = DkmClrInstructionAddress.Create(
                        clr, module, new DkmClrMethodId(notifyToken, 1),
                        NativeOffset: uint.MaxValue, ILOffset: 0, CPUInstruction: null);
                }
                catch
                {
                    // Some hosts want a concrete native offset.
                    address = DkmClrInstructionAddress.Create(
                        clr, module, new DkmClrMethodId(notifyToken, 1),
                        NativeOffset: 0, ILOffset: 0, CPUInstruction: null);
                }

                var bp = DkmRuntimeInstructionBreakpoint.Create(
                    ShumwayGuids.NotifyBreakpointSource, Thread: null,
                    InstructionAddress: address, IsBarrier: false, DataItem: null);
                bp.Enable();
                state.NotifyBreakpoint = bp;
            }
            catch (Exception)
            {
                // Without the notify breakpoint there are no Prolog stops at all — but
                // Break All still works (the IDE asks the engine directly), so the session
                // is degraded, not dead. Never take a debug session down from here.
            }
        }

        private static ShumwayServerDataItem GetState(DkmProcess process)
        {
            ShumwayServerDataItem? state = process.GetDataItem<ShumwayServerDataItem>();
            if (state == null)
            {
                state = new ShumwayServerDataItem();
                process.SetDataItem(DkmDataCreationDisposition.CreateNew, state);
            }
            return state;
        }
    }
}
