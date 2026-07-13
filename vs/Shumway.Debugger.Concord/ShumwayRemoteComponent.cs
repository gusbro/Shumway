// Shumway debugger - server-level Concord component (ADR-035, phase D2).
//
// Two monitor-side jobs the IDE component cannot do itself:
//
//   * PLANT the hidden breakpoint on ShumwayDebugHelper.Notify — the one place the
//     engine calls when it decides to stop. The IDE supplies the metadata token.
//
//   * MATERIALIZE the custom runtime and one module per consulted .pl, which is what
//     gives synthesized frames an address (and therefore source navigation, locals and,
//     in D3, breakpoints). Creating Dkm objects only works in a real EVENT context: the
//     D0 spike proved that doing it from a message the stack-walk filter sent throws
//     ObjectDisposedException on the walk's transient container. So we try when asked,
//     and we try again at every process pause — whichever comes first wins, and the
//     one that fails costs nothing.
//
// D3 gives this component its real work: mapping a notify hit onto the user's bound
// breakpoint (bp.OnHit) and driving IDkmRuntimeStepper through the command channel.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.Clr;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.CustomRuntimes;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Microsoft.VisualStudio.Debugger.Symbols;

namespace Shumway.Debugger.Concord
{
    internal sealed class ShumwayServerDataItem : DkmDataItem
    {
        public long SnapshotAddress;
        public DkmRuntimeInstructionBreakpoint? NotifyBreakpoint;

        /// <summary>Files the IDE has seen in a frame but which have no module yet.</summary>
        public readonly List<string> PendingFiles = new List<string>();
        public readonly HashSet<string> CreatedFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ShumwayRemoteComponent
        : IDkmCustomMessageForwardReceiver, IDkmRuntimeBreakpointReceived,
          IDkmProcessExecutionNotification
    {
        DkmCustomMessage? IDkmCustomMessageForwardReceiver.SendLower(DkmCustomMessage customMessage)
        {
            DkmProcess process = customMessage.Process;
            ShumwayServerDataItem state = GetState(process);

            switch (customMessage.MessageCode)
            {
                case ShumwayGuids.MsgArmNotifyBreakpoint:
                    state.SnapshotAddress = (long)customMessage.Parameter1;
                    ArmNotifyBreakpoint(process, state, (int)customMessage.Parameter2);
                    break;

                case ShumwayGuids.MsgEnsureModules:
                    var paths = ((string)customMessage.Parameter1).Split('|');
                    foreach (string path in paths)
                    {
                        if (path.Length > 0 && !state.CreatedFiles.Contains(path))
                            state.PendingFiles.Add(path);
                    }
                    EnsureModules(process, state); // may fail here; retried at the next pause
                    break;
            }
            return null;
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
                // Without the notify breakpoint there are no port stops — but Break All
                // still works (the IDE asks the engine directly), so the session is
                // degraded, not dead. Never take the debug session down from here.
            }
        }

        void IDkmRuntimeBreakpointReceived.OnRuntimeBreakpointReceived(
            DkmRuntimeBreakpoint runtimeBreakpoint, DkmThread thread,
            bool hasException, DkmEventDescriptorS eventDescriptor)
        {
            if (runtimeBreakpoint.SourceId != ShumwayGuids.NotifyBreakpointSource)
                return;

            DkmProcess process = thread.Process;
            EnsureModules(process, GetState(process)); // a real event context

            // D2 is the READ side: a port stop is not yet mapped onto a user breakpoint
            // (that is D3's bp.OnHit), so let the engine run on. The stack, the source
            // position and the locals are all exercised from a Break All or from any C#
            // breakpoint the user sets, which is exactly what D2 set out to prove.
            eventDescriptor.Suppress();
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
        }

        /// <summary>One module per .pl. The module IS the file — that identity is what
        /// makes a source position out of nothing more than (module, line), and what will
        /// let F9 in an editor find the code in D3.</summary>
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
                // Wrong context (the stack-walk case). The files stay pending and the
                // next process pause picks them up.
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
