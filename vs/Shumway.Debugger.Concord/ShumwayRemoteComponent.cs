// Shumway debugger - server-level Concord component (ADR-035, D0 spike legs 1+2).
//
// Receives the channel address + Notify metadata token from the IDE component
// (DkmCustomMessage, SourceId-filtered), plants a hidden CLR breakpoint on
// SpikeDebugHelper.Notify, and on each hit bumps a counter in the debuggee's
// pinned channel buffer (WriteMemory) and suppresses the event so execution
// continues. Errors are reported through the channel too (status + utf8 text)
// so the spike harness can see exactly which stage failed.

using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.Clr;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.CustomRuntimes;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Microsoft.VisualStudio.Debugger.Stepping;
using Microsoft.VisualStudio.Debugger.Symbols;

namespace Shumway.Debugger.Concord
{
    internal sealed class ShumwayServerDataItem : DkmDataItem
    {
        public long ChannelAddress;
        public string ScriptPath = "";
        public int NotifyHits;
        public bool RuntimeCreated;
        public DkmRuntimeInstructionBreakpoint? NotifyBreakpoint;
    }

    public sealed class ShumwayRemoteComponent
        : IDkmCustomMessageForwardReceiver, IDkmRuntimeBreakpointReceived,
          IDkmRuntimeMonitorBreakpointHandler, IDkmRuntimeStepper
    {
        DkmCustomMessage? IDkmCustomMessageForwardReceiver.SendLower(DkmCustomMessage customMessage)
        {
            if (customMessage.MessageCode == ShumwayGuids.MsgArmNotifyBreakpoint)
                ArmNotifyBreakpoint(customMessage);
            return null;
        }

        /// <summary>Leg 3: materialize the Shumway custom runtime + one module per
        /// consulted .pl, so F9 breakpoints and steppers route to our components.
        /// Runs SERVER-side (DkmCustomRuntimeInstance.Create is a monitor API), and
        /// specifically from a real EVENT context (the first notify-breakpoint hit)
        /// — creating Dkm objects from the custom-message handler that the stack
        /// filter triggers throws ObjectDisposedException('DkmDataContainer').
        /// Arg shapes follow PTVS's RemoteComponent.CreateModuleRequest handler.</summary>
        private static void CreateRuntime(DkmProcess process, string scriptPath, long channelAddress)
        {
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

                bool known = runtime.GetModuleInstances()
                    .OfType<DkmCustomModuleInstance>()
                    .Any(m => string.Equals(m.FullName, scriptPath, StringComparison.OrdinalIgnoreCase));
                if (!known)
                {
                    var module = DkmModule.Create(
                        new DkmModuleId(ShumwayGuids.SpikeModuleMvid, ShumwayGuids.SymbolProvider),
                        scriptPath,
                        new DkmCompilerId(ShumwayGuids.ShumwayVendor, ShumwayGuids.ShumwayLanguage),
                        process.Connection, null);

                    // Create WITHOUT the module, then SetModule: that raises the
                    // symbols-loaded notification, which makes VS re-evaluate
                    // pending breakpoints against our symbol provider.
                    var moduleInstance = DkmCustomModuleInstance.Create(
                        System.IO.Path.GetFileName(scriptPath), scriptPath,
                        0, runtime, null, null,
                        DkmModuleFlags.None, DkmModuleMemoryLayout.Unknown,
                        0, 0, 0, "Shumway Prolog", false, null, null, null);
                    moduleInstance.SetModule(module, true);
                }

                process.WriteMemory(
                    (ulong)(channelAddress + Channel.OffServerStatus), new[] { Channel.StatusRuntimeReady });
            }
            catch (Exception ex)
            {
                WriteStatus(process, channelAddress,
                    (byte)(Channel.StatusErrorBase + 9),
                    "create-runtime: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ArmNotifyBreakpoint(DkmCustomMessage customMessage)
        {
            DkmProcess process = customMessage.Process;
            long channelAddress = (long)customMessage.Parameter1;
            int notifyToken = (int)customMessage.Parameter2;
            string scriptPath = (string)customMessage.Parameter3;

            var state = new ShumwayServerDataItem
            {
                ChannelAddress = channelAddress,
                ScriptPath = scriptPath,
            };
            process.SetDataItem(DkmDataCreationDisposition.CreateAlways, state);

            byte stage = 0;
            try
            {
                stage = 1; // locate the CLR runtime instance
                DkmClrRuntimeInstance clr = process.GetRuntimeInstances()
                    .OfType<DkmClrRuntimeInstance>()
                    .First();

                stage = 2; // locate the debuggee module
                DkmClrModuleInstance module = clr.GetModuleInstances()
                    .OfType<DkmClrModuleInstance>()
                    .First(m => string.Equals(m.Name, "SpikeDebuggee.dll", StringComparison.OrdinalIgnoreCase));

                stage = 3; // build the CLR instruction address at IL offset 0
                DkmClrInstructionAddress address;
                try
                {
                    address = DkmClrInstructionAddress.Create(
                        clr, module, new DkmClrMethodId(notifyToken, 1),
                        NativeOffset: uint.MaxValue, ILOffset: 0, CPUInstruction: null);
                }
                catch
                {
                    // Fallback: some hosts want a concrete native offset.
                    address = DkmClrInstructionAddress.Create(
                        clr, module, new DkmClrMethodId(notifyToken, 1),
                        NativeOffset: 0, ILOffset: 0, CPUInstruction: null);
                }

                stage = 4; // plant + enable the hidden breakpoint
                var bp = DkmRuntimeInstructionBreakpoint.Create(
                    ShumwayGuids.NotifyBreakpointSource, Thread: null,
                    InstructionAddress: address, IsBarrier: false, DataItem: null);
                bp.Enable();
                state.NotifyBreakpoint = bp;

                WriteStatus(process, channelAddress, Channel.StatusArmed, null);
            }
            catch (Exception ex)
            {
                WriteStatus(process, channelAddress,
                    (byte)(Channel.StatusErrorBase + stage), $"stage {stage}: {ex.Message}");
            }
        }

        // ---- Leg 3: .pl breakpoints routed by RuntimeId to this handler ----

        void IDkmRuntimeMonitorBreakpointHandler.EnableRuntimeBreakpoint(DkmRuntimeBreakpoint runtimeBreakpoint)
        {
            var state = runtimeBreakpoint.Process.GetDataItem<ShumwayServerDataItem>();
            if (state == null)
                return;
            int line = 0;
            if (runtimeBreakpoint is DkmRuntimeInstructionBreakpoint irb
                && irb.InstructionAddress is DkmCustomInstructionAddress custom)
                line = (int)custom.Offset;
            runtimeBreakpoint.Process.WriteMemory(
                (ulong)(state.ChannelAddress + Channel.OffF9Line), BitConverter.GetBytes(line));
            runtimeBreakpoint.Process.WriteMemory(
                (ulong)(state.ChannelAddress + Channel.OffF9Flag), new byte[] { 1 });
            // Spike scope: record that binding reached the runtime side. D3 patches
            // the engine's Break opcode via the command channel here.
        }

        void IDkmRuntimeMonitorBreakpointHandler.TestRuntimeBreakpoint(DkmRuntimeBreakpoint runtimeBreakpoint)
        {
            // Bindable as far as the spike is concerned.
        }

        void IDkmRuntimeMonitorBreakpointHandler.DisableRuntimeBreakpoint(DkmRuntimeBreakpoint runtimeBreakpoint)
        {
        }

        // ---- Leg 5: stepping arbitration routed by RuntimeId ----

        void IDkmRuntimeStepper.BeforeEnableNewStepper(DkmRuntimeInstance runtimeInstance, DkmStepper stepper)
        {
        }

        bool IDkmRuntimeStepper.OwnsCurrentExecutionLocation(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper, DkmStepArbitrationReason reason)
        {
            // Spike: claim any step that reaches us (the top frame carries our
            // runtime's instruction address after the filter upgrade).
            return true;
        }

        void IDkmRuntimeStepper.Step(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper, DkmStepArbitrationReason reason)
        {
            var state = runtimeInstance.Process.GetDataItem<ShumwayServerDataItem>();
            if (state != null)
                runtimeInstance.Process.WriteMemory(
                    (ulong)(state.ChannelAddress + Channel.OffStepFlag), new byte[] { 1 });
            // Spike scope: complete the step immediately (real D3: write the port
            // step spec to the command channel, resume, complete on Notify).
            stepper.OnStepComplete(stepper.Thread, false);
        }

        void IDkmRuntimeStepper.StopStep(DkmRuntimeInstance runtimeInstance, DkmStepper stepper)
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

        void IDkmRuntimeStepper.NotifyStepComplete(DkmRuntimeInstance runtimeInstance, DkmStepper stepper)
        {
        }

        void IDkmRuntimeBreakpointReceived.OnRuntimeBreakpointReceived(
            DkmRuntimeBreakpoint runtimeBreakpoint, DkmThread thread,
            bool hasException, DkmEventDescriptorS eventDescriptor)
        {
            // vsdconfig filters on our SourceId, but double-check defensively.
            if (runtimeBreakpoint.SourceId != ShumwayGuids.NotifyBreakpointSource)
                return;

            DkmProcess process = thread.Process;
            ShumwayServerDataItem? state = process.GetDataItem<ShumwayServerDataItem>();
            if (state != null)
            {
                state.NotifyHits++;
                process.WriteMemory(
                    (ulong)(state.ChannelAddress + Channel.OffHits),
                    BitConverter.GetBytes(state.NotifyHits));

                // First hit: a real monitor-side event context — the only place
                // Dkm object creation survives (see CreateRuntime).
                if (!state.RuntimeCreated)
                {
                    state.RuntimeCreated = true;
                    CreateRuntime(process, state.ScriptPath, state.ChannelAddress);
                }
            }

            // Leg 2 spike scope: intercept + auto-continue. D3 maps hits to the
            // user's bound breakpoints via bp.OnHit(thread, false).
            eventDescriptor.Suppress();
        }

        private static void WriteStatus(DkmProcess process, long channel, byte status, string? error)
        {
            try
            {
                process.WriteMemory((ulong)(channel + Channel.OffServerStatus), new[] { status });
                if (error != null)
                {
                    byte[] text = Encoding.UTF8.GetBytes(
                        error.Length > Channel.MaxErrorText ? error.Substring(0, Channel.MaxErrorText) : error);
                    process.WriteMemory((ulong)(channel + Channel.OffErrorLen), BitConverter.GetBytes(text.Length));
                    process.WriteMemory((ulong)(channel + Channel.OffErrorText), text);
                }
            }
            catch
            {
                // Channel reporting is best-effort; never take down the debug session.
            }
        }
    }
}
