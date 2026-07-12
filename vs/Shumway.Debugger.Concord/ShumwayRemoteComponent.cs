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

namespace Shumway.Debugger.Concord
{
    internal sealed class ShumwayServerDataItem : DkmDataItem
    {
        public long ChannelAddress;
        public int NotifyHits;
        public DkmRuntimeInstructionBreakpoint? NotifyBreakpoint;
    }

    public sealed class ShumwayRemoteComponent
        : IDkmCustomMessageForwardReceiver, IDkmRuntimeBreakpointReceived
    {
        DkmCustomMessage? IDkmCustomMessageForwardReceiver.SendLower(DkmCustomMessage customMessage)
        {
            if (customMessage.MessageCode != ShumwayGuids.MsgArmNotifyBreakpoint)
                return null;

            DkmProcess process = customMessage.Process;
            long channelAddress = (long)customMessage.Parameter1;
            int notifyToken = (int)customMessage.Parameter2;

            var state = new ShumwayServerDataItem { ChannelAddress = channelAddress };
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
            return null;
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
