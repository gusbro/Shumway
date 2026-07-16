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

        /// <summary>ADR-035 D5 — the Prolog condition each breakpoint carries, keyed by the
        /// runtime breakpoint's UniqueId. Filled by ParseCondition (the moment Visual Studio
        /// tells us what the user typed in the breakpoint's settings), read by WriteCommands
        /// when it rewrites the engine's full breakpoint state. Keyed by INSTANCE, not by
        /// file:line, because VS tears a breakpoint down and recreates it when its settings
        /// change — a recreated breakpoint without a condition must not inherit the old
        /// one's.</summary>
        public readonly Dictionary<Guid, string> ConditionsByBp = new Dictionary<Guid, string>();

        /// <summary>The step in flight, if any. Completed by the port that satisfies it.</summary>
        public DkmStepper? Stepper;
        public DebugCommandKind PendingStep = DebugCommandKind.None;

        /// <summary>True while the process is stopped AT A PORT — which is what makes the
        /// Prolog runtime, rather than the CLR, the owner of the execution location.</summary>
        public bool StoppedAtPort;

        public readonly List<string> PendingFiles = new List<string>();
        public readonly HashSet<string> CreatedFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What went wrong, and how many stops we have seen. The monitor side has no
        /// other voice: when it fails, the only symptom in the IDE is that nothing
        /// happens.</summary>
        public string LastError = "";
        public int Stops;

        /// <summary>How many times Visual Studio ASKED us about a step — whether it thinks
        /// the Prolog runtime owns where we are, and whether it handed us the step. A step
        /// that does nothing looks the same from the IDE whether it never reached us or
        /// reached us and the engine ignored it.</summary>
        public int OwnsAsks;
        public int StepCalls;

        /// <summary>How many times the process has been let go. If a "step" resumes the
        /// process and stops it again without ever asking us, the step was taken by SOMEONE
        /// ELSE'S runtime — which is a different bug from a step that did nothing.</summary>
        public int Resumes;

        /// <summary>Until the engine has stopped once, we have nowhere to create the .pl
        /// modules from — and without them no breakpoint can bind, so it will never stop. So
        /// we ask it to stop once for nothing. Cleared by the first stop.</summary>
        public bool NeedHello = true;

        /// <summary>The user asked to pause, and we asked the engine to stop at its next
        /// port rather than letting the process be frozen where it stood. Set when the pause
        /// is taken, cleared when the port arrives and the break is completed.</summary>
        public bool AsyncBreakPending;

        public static string Key(string file, int line) =>
            file + "|" + line.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class ShumwayRemoteComponent
        : IDkmCustomMessageForwardReceiver, IDkmRuntimeBreakpointReceived,
          IDkmProcessExecutionNotification, IDkmRuntimeMonitorBreakpointHandler,
          IDkmRuntimeStepper, IDkmModuleInstanceLoadNotification,
          IDkmLanguageConditionEvaluator
    {
        /// <summary>The engine's own module loading is the earliest moment at which a Shumway
        /// process can be recognised — and, under a LAUNCH, the only one that comes before the
        /// program runs. Nothing has stopped and nothing will: no user breakpoint is armed
        /// yet, so there is nothing to stop AT. So this is where the session starts.
        ///
        /// <para>The channel is read from the file the engine published when it opened its
        /// session (see ShumwayChannelFile), which is why this needs neither a stopped thread
        /// nor a frame nor an expression evaluation — none of which exist here.</para></summary>
        void IDkmModuleInstanceLoadNotification.OnModuleInstanceLoad(
            DkmModuleInstance moduleInstance, DkmWorkList workList,
            DkmEventDescriptorS eventDescriptor)
        {
            ShumwayLog.Write("module load: " + moduleInstance.Name);
            // Matched WITHOUT regard to the .dll suffix: a single-file --exe reports the engine
            // as "Shumway.Core", a multi-file build as "Shumway.Core.dll". See NormalizeModule.
            if (!ShumwaySession.IsSameModule(moduleInstance.Name, ShumwaySession.EngineModule))
                return;

            DkmProcess process = moduleInstance.Process;
            ShumwayServerDataItem state = GetState(process);
            if (state.NotifyBreakpoint != null)
                return;

            int? processId = process.LivePart?.Id;
            if (processId == null)
            {
                ShumwayLog.Write("  the engine's module loaded, but the process has no live part");
                return;
            }

            // The token names the method the hidden breakpoint goes on. The normal source is
            // the DLL on disk — the one thing available before the engine has run a line, which
            // is what a LAUNCH needs (the channel does not exist yet). But a shumway-link --exe
            // is a SINGLE-FILE bundle: Shumway.Core.dll is embedded in the executable, there is
            // no file at moduleInstance.FullName to read, and FindNotifyToken comes back 0. When
            // we are ATTACHING, though, the engine has already opened its session and PUBLISHED
            // its channel file, and that file carries the token. So: read the channel first (it
            // is harmless if absent), try the disk, and fall back to the channel file's token.
            // Without this an attach to a --exe never armed the notify breakpoint — no bootstrap
            // stop, so the stack filter never attached (the user saw the raw C# engine stack)
            // and Break All found no channel ("not implemented").
            LoadChannel(process, state);
            int token = ShumwayMetadata.FindNotifyToken(moduleInstance.FullName);
            if (token == 0)
            {
                ShumwayChannelInfo? channel = ShumwayChannelFile.Read(processId.Value);
                if (channel != null && channel.NotifyMetadataToken != 0)
                {
                    token = channel.NotifyMetadataToken;
                    ShumwayLog.Write("  no on-disk metadata (single-file exe?); "
                        + "took the notify token from the channel file: " + token);
                }
            }
            if (token == 0)
            {
                ShumwayLog.Write("  no ShumwayDebugHost.Notify token (disk or channel) for "
                    + moduleInstance.FullName);
                return;
            }

            ArmNotifyBreakpoint(process, state, token);

            // ATTACH. Under a launch the channel does not exist yet and this does nothing —
            // the commands go down later, when the engine tells us where the channel is. But
            // when we ATTACH to an engine that has been running for hours, the channel is
            // already there, and this is the moment the session begins: nothing has stopped,
            // nothing is going to stop by itself, and until something does there are no
            // modules for a breakpoint to bind against ("no symbols have been loaded for this
            // document" — which was the truth). So ask, here, for the one stop that breaks
            // the circle. The engine grants it even standing still: see
            // ChannelDebugSession's idle watcher.
            WriteCommands(process, state);
            ShumwayLog.Write("  armed at module load (token " + token + "): " + Status(state));
        }

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
                    // this is the first moment we can tell the engine about it. It also
                    // carries the Hello — the stop we need in order to be able to stop.
                    WriteCommands(process, state);
                    break;

                case ShumwayGuids.MsgEnsureModules:
                    // Record them; do NOT create them here. This message is sent from inside a
                    // stack walk, and creating Dkm objects there half-works, which is worse
                    // than not working: the module gets created and then SetModule throws on
                    // the walk's transient container — and the retry, in a context that would
                    // have succeeded, fails because the module id is already taken. It cost an
                    // hour to see. The Hello stop is where creation happens, and the only
                    // place it happens.
                    foreach (string raw in ((string)customMessage.Parameter1).Split('|'))
                    {
                        string path = ShumwaySession.Canonical(raw);
                        if (path.Length > 0 && !state.CreatedFiles.Contains(path)
                            && !state.PendingFiles.Contains(path))
                            state.PendingFiles.Add(path);
                    }
                    break;

                case ShumwayGuids.MsgServerStatus:
                    return DkmCustomMessage.Create(
                        process.Connection, process, ShumwayGuids.MessageSource,
                        ShumwayGuids.MsgServerStatus, Status(state), null);
            }
            return null;
        }

        private static string Status(ShumwayServerDataItem state)
        {
            return "notify=" + (state.NotifyBreakpoint != null ? "armed" : "NOT-ARMED")
                + " modules=" + state.CreatedFiles.Count
                + " pending=" + state.PendingFiles.Count
                + " bps=" + state.Breakpoints.Count
                + " cmdaddr=0x" + state.CommandAddress.ToString("X")
                + " stops=" + state.Stops
                + " atport=" + state.StoppedAtPort
                + " ownsasks=" + state.OwnsAsks
                + " steps=" + state.StepCalls
                + " resumes=" + state.Resumes
                + " pendingstep=" + state.PendingStep
                + (state.PendingFiles.Count > 0 ? " next='" + state.PendingFiles[0] + "'" : "")
                + (state.LastError.Length > 0 ? " ERR[" + state.LastError + "]" : "");
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
            ShumwayLog.Write("breakpoint enabled at " + file + ":" + line + " -> " + Status(state));
        }

        void IDkmRuntimeMonitorBreakpointHandler.DisableRuntimeBreakpoint(
            DkmRuntimeBreakpoint runtimeBreakpoint)
        {
            if (!TryLocate(runtimeBreakpoint, out string file, out int line))
                return;

            ShumwayServerDataItem state = GetState(runtimeBreakpoint.Process);
            state.Breakpoints.Remove(ShumwayServerDataItem.Key(file, line));
            state.ConditionsByBp.Remove(runtimeBreakpoint.UniqueId);
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

        // ---- conditional breakpoints (ADR-035 D5) ----
        //
        // The condition is a PROLOG GOAL, and the engine is where Prolog runs — so the
        // engine evaluates it, at the Break opcode, BEFORE deciding to notify anyone: a
        // hit whose condition fails resumes without a single cross-process round trip,
        // which is what makes a hot conditional breakpoint affordable, and nothing here
        // ever func-evals in breakpoint context (the documented hang, samples issue #61).
        //
        // Visual Studio's part is the UI: the user types the condition in the breakpoint's
        // settings, VS wraps it in a DkmEvaluationBreakpointCondition whose language is the
        // breakpoint's (ours, via the module's DkmCompilerId), and routes it here by the
        // vsdconfig LanguageId filter. ParseCondition is the moment we LEARN the text —
        // there is no property on the runtime breakpoint to read it from — so we record it
        // and rewrite the engine's full breakpoint state, condition attached.

        void IDkmLanguageConditionEvaluator.ParseCondition(
            DkmEvaluationBreakpointCondition evaluationCondition, out string errorText)
        {
            // Accepted without parsing: the parser lives in the engine, and the engine is
            // the debuggee — there is nothing here that can read Prolog. A condition that
            // does not parse stops at its first hit and says why (the snapshot's
            // conditionError), which is also what a C# condition that throws does.
            errorText = null!;
            try
            {
                // Source.Text is what the user typed; Source.Operator distinguishes "is
                // true" from "has changed". Only the former is a Prolog goal; "when
                // changed" needs last-value tracking the engine does not do (yet), and
                // saying so HERE puts the message on the breakpoint's settings window.
                if (evaluationCondition.Source.Operator
                    != DkmBreakpointConditionOperator.BreakWhenTrue)
                {
                    errorText = "Shumway breakpoints support 'Is true' conditions only";
                    return;
                }
                DkmRuntimeBreakpoint bp = evaluationCondition.RuntimeBreakpoint;
                ShumwayServerDataItem state = GetState(bp.Process);
                state.ConditionsByBp[bp.UniqueId] = evaluationCondition.Source.Text ?? "";
                WriteCommands(bp.Process, state);
                ShumwayLog.Write("condition parsed for bp " + bp.UniqueId + ": '"
                    + evaluationCondition.Source.Text + "' -> " + Status(state));
            }
            catch (Exception ex)
            {
                ShumwayLog.Write("ParseCondition threw: " + ex);
            }
        }

        void IDkmLanguageConditionEvaluator.EvaluateCondition(
            DkmEvaluationBreakpointCondition evaluationCondition,
            Microsoft.VisualStudio.Debugger.CallStack.DkmStackWalkFrame stackFrame,
            out bool stop, out string errorText)
        {
            // The engine already decided: a hit that reached OnHit is one whose condition
            // held — or could not run, in which case the snapshot says why and the stop
            // must happen WITH the message (a broken condition that silently swallowed its
            // breakpoint would be undiagnosable). Nothing runs in the debuggee here; the
            // answer is read out of the pinned snapshot like everything else.
            stop = true;
            errorText = null!;
            try
            {
                ShumwayServerDataItem state = GetState(stackFrame.Process);
                DebugSnapshot? snapshot = ReadSnapshot(stackFrame.Process, state);
                if (snapshot != null && snapshot.ConditionError.Length != 0)
                {
                    errorText = snapshot.ConditionError;
                    ShumwayLog.Write("condition error surfaced: " + errorText);
                }
            }
            catch (Exception ex)
            {
                ShumwayLog.Write("EvaluateCondition threw: " + ex);
            }
        }

        // ---- stepping ----

        bool IDkmRuntimeStepper.OwnsCurrentExecutionLocation(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper, DkmStepArbitrationReason reason)
        {
            // Only when the machine is stopped at a port. Anywhere else — inside a builtin,
            // mid-unification, in the user's own C# — the CLR owns the location and should
            // keep it: a step there is a C# step, and it is not ours to take.
            ShumwayServerDataItem owner = GetState(runtimeInstance.Process);
            owner.OwnsAsks++;
            return StoppedInProlog(runtimeInstance.Process);
        }

        void IDkmRuntimeStepper.Step(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper, DkmStepArbitrationReason reason)
        {
            BeginStep(runtimeInstance.Process, stepper);
        }

        /// <summary>Take a step. Shared with <see cref="ShumwayClrStepper"/>, which is where
        /// the step actually arrives in a managed debuggee — the mechanism is the same
        /// wherever Visual Studio chooses to offer it.</summary>
        internal static void BeginStep(DkmProcess process, DkmStepper stepper)
        {
            ShumwayServerDataItem state = GetState(process);
            state.StepCalls++;
            state.Stepper = stepper;
            state.PendingStep = StepKindOf(stepper);
            WriteCommands(process, state);
            // Nothing else to arrange. VS resumes the process; the engine reads the mode
            // out of the channel and stops at the first port that satisfies it, which
            // comes back to us as a notify.
        }

        internal static void CancelStep(DkmProcess process)
        {
            ShumwayServerDataItem state = GetState(process);
            state.Stepper = null;
            state.PendingStep = DebugCommandKind.None;
            WriteCommands(process, state);
        }

        /// <summary>Ask the engine to stop at its next port. This is the pause — see
        /// <see cref="ShumwayAsyncBreak"/> for why a pause is a request and not a freeze.
        /// The command rides the same idempotent full-state write as everything else.</summary>
        internal static void RequestBreakNow(DkmProcess process, ShumwayServerDataItem state)
            => WriteCommands(process, state);

        /// <summary>Is the machine standing still in Prolog — which is what makes a step
        /// OURS to take rather than the CLR's?
        ///
        /// <para>ASKED OF THE ENGINE, not of a flag we set. We used to answer from
        /// <see cref="ShumwayServerDataItem.StoppedAtPort"/>, which is turned on when a stop
        /// arrives through the hidden breakpoint — and so was blind to every stop that comes
        /// any other way. <c>debugger_break/0</c> is one: the program asks the RUNTIME to
        /// break, and no breakpoint of ours is involved. We would decline the step, the CLR
        /// would try to step a synthesized Prolog frame that is not its code, and Visual
        /// Studio would say "Unable to step. Operation not supported" — which was true, and
        /// unhelpful.</para>
        ///
        /// <para>The engine already publishes the answer: it clears the channel's
        /// <c>running</c> word for the length of a stop and sets it again when it resumes.
        /// One memory read, and no flag to keep in step with reality.</para></summary>
        internal static bool StoppedInProlog(DkmProcess process)
        {
            ShumwayServerDataItem state = GetState(process);
            if (state.SnapshotAddress == 0) return false;
            try
            {
                var header = new byte[DebugWire.RunningOffset + 4];
                process.ReadMemory((ulong)state.SnapshotAddress, DkmReadMemoryFlags.None, header);
                int at = 0;
                if (DebugWire.ReadInt(header, ref at) != DebugWire.FormatVersion)
                    return false;   // a channel we cannot read is not one we can act on
                at = DebugWire.RunningOffset;
                return DebugWire.ReadInt(header, ref at) == 0;   // 0 = stopped, and it is ours
            }
            catch (Exception)
            {
                return false;
            }
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
            CancelStep(runtimeInstance.Process);
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
            // A THROW HERE IS A HANG. Concord swallows it, the debuggee is resumed, and
            // whatever the user was waiting for — a pause, a step — is waited for for ever,
            // because the one event that could have completed it was this one. (An
            // OutOfMemoryException out of a snapshot reader that trusted a frame count is how
            // we found that out: Break All on Blint left Visual Studio "breaking" while the
            // program ran happily to its answer.) So: catch, say so, and still answer the
            // question that was asked.
            try
            {
                OnStop(runtimeBreakpoint, thread, eventDescriptor);
            }
            catch (Exception ex)
            {
                ShumwayLog.Write("  STOP HANDLER THREW: " + ex);
                ShumwayServerDataItem broken = GetState(thread.Process);
                if (broken.AsyncBreakPending)
                {
                    broken.AsyncBreakPending = false;
                    try
                    {
                        thread.Process.OnAsyncBreakComplete(
                            DkmAsyncBreakStatus.ActiveBreak, thread);
                    }
                    catch (Exception inner)
                    {
                        ShumwayLog.Write("  and completing the break threw: " + inner.Message);
                    }
                }
            }
        }

        private static void OnStop(
            DkmRuntimeBreakpoint runtimeBreakpoint, DkmThread thread,
            DkmEventDescriptorS eventDescriptor)
        {
            if (runtimeBreakpoint.SourceId != ShumwayGuids.NotifyBreakpointSource)
                return;

            // The hidden breakpoint is ours and the user must never see it. What the user
            // sees is what we report below — their breakpoint, or their step.
            eventDescriptor.Suppress();

            DkmProcess process = thread.Process;
            ShumwayServerDataItem state = GetState(process);
            state.Stops++;

            // The FIRST stop of a launched session is one the engine gives us on purpose,
            // while it waits at the door (--debug-wait): the breakpoint was armed from the
            // DLL on disk, before the engine had opened its session, so this is where we
            // finally learn where its channel is — and which files it is about to consult,
            // which is what a breakpoint needs to bind against.
            LoadChannel(process, state);
            ShumwayLog.Write("stop #" + state.Stops + " (hello=" + state.NeedHello
                + ") " + Status(state));

            // THE ONE CONTEXT where creating the .pl modules works (D0 established this the
            // hard way: from a stack walk it throws on the walk's transient container, and at
            // a process pause the monitor returns E_FAIL). Which is why the first stop is one
            // we asked for ourselves, and why we stop asking now.
            bool wasHello = state.NeedHello;
            state.NeedHello = false;
            EnsureModules(process, state);
            if (wasHello)
                WriteCommands(process, state);   // take the Hello back out of the channel

            DebugSnapshot? snapshot = ReadSnapshot(process, state);
            ShumwayLog.Write("  snapshot: " + (snapshot == null ? "NULL"
                : snapshot.Reason + " seq=" + snapshot.Sequence + " running=" + snapshot.Running
                  + " frames=" + snapshot.Frames.Count + " goal=" + snapshot.Goal
                  + " pendingBreak=" + state.AsyncBreakPending));
            if (snapshot == null || wasHello)
                return; // the bootstrap stop is not the user's: let it run on

            // THE ENGINE HAS CONSULTED A FILE. Not a stop: a fact, delivered the only way a
            // module can be built (from inside a stop event). EnsureModules, just above, has
            // already built it — from the file list we re-read out of the channel. Say nothing
            // to the user and let the program go on.
            if (snapshot.Reason == StopReason.SourcesChanged)
            {
                state.StoppedAtPort = false;
                return;
            }

            // THE STEP THAT CANNOT BE SATISFIED. Control has left Prolog — the query gave up
            // its answer, or ran out of answers — and no port is coming. A step is a promise
            // to stop at the next port that satisfies it, and there is none to keep it with.
            //
            // So we CANCEL it and let the program run on, rather than stop the user in the
            // host's C# (they stepped past the end of their program; there is nothing there
            // they asked to see). Leaving it in flight is what broke: Visual Studio waited
            // for a stop that would never arrive, believed the program was still running, and
            // answered every key with "Unable to step. Operation not supported."
            if (snapshot.Reason == StopReason.StepAbandoned)
            {
                DkmStepper? orphan = state.Stepper;
                state.Stepper = null;
                state.PendingStep = DebugCommandKind.None;
                state.StoppedAtPort = false;
                if (orphan != null)
                {
                    ShumwayLog.Write("  step abandoned: control left Prolog");
                    try { orphan.CancelStepper(runtimeBreakpoint.RuntimeInstance); }
                    catch (Exception ex) { ShumwayLog.Write("  cancel threw: " + ex.Message); }
                }
                return;
            }

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

            // The pause the user asked for has landed. It landed at a PORT — a real point in
            // the program, with a real stack — which is the whole reason we did not simply
            // let Visual Studio freeze the process where it stood: a Prolog machine caught
            // mid-unification has no stack to show, and the last one it had is not where it
            // is. Completing the break (rather than reporting a breakpoint hit) is what tells
            // VS that the pause it requested is the thing that happened.
            if (state.AsyncBreakPending && snapshot.Reason == StopReason.AsyncBreak)
            {
                state.AsyncBreakPending = false;
                ShumwayLog.Write("  async break landed at " + snapshot.File + ":" + snapshot.Line);
                process.OnAsyncBreakComplete(DkmAsyncBreakStatus.ActiveBreak, thread);
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
            // Not a place to create modules either (the monitor returns E_FAIL). Only a real
            // breakpoint event is, which is what the Hello is for.
        }

        void IDkmProcessExecutionNotification.OnProcessResume(
            DkmProcess process, DkmProcessExecutionCounters processCounters)
        {
            ShumwayServerDataItem state = GetState(process);
            state.Resumes++;
            state.StoppedAtPort = false;
        }

        /// <summary>One module per .pl. The module IS the file — that identity is what
        /// makes a source position out of nothing more than (module, line), and what lets
        /// F9 in an editor find code that has no symbols on disk at all.</summary>
        private static void EnsureModules(DkmProcess process, ShumwayServerDataItem state)
        {
            if (state.PendingFiles.Count == 0)
                return;

            int stage = 0;
            try
            {
                stage = 1;
                DkmCustomRuntimeInstance? runtime = process.GetRuntimeInstances()
                    .OfType<DkmCustomRuntimeInstance>()
                    .FirstOrDefault(r => r.Id.RuntimeType == ShumwayGuids.RuntimeType);
                if (runtime == null)
                {
                    stage = 2;
                    runtime = DkmCustomRuntimeInstance.Create(
                        process, new DkmRuntimeInstanceId(ShumwayGuids.RuntimeType, 0), null);
                }

                foreach (string path in state.PendingFiles.ToArray())
                {
                    stage = 3;
                    var module = DkmModule.Create(
                        new DkmModuleId(ShumwayGuids.ModuleIdFor(path), ShumwayGuids.SymbolProvider),
                        path,
                        new DkmCompilerId(ShumwayGuids.ShumwayVendor, ShumwayGuids.ShumwayLanguage),
                        process.Connection, null);

                    stage = 4;
                    // The name here is what the Call Stack "Module" column shows. Use the base
                    // name WITHOUT the .pl extension (and without a doubled ".pl" from an embedded
                    // "<module>.pl.pl" materialisation), so a frame reads "Blint!blint(...)", the
                    // module named ONCE — not "Blint.pl!Blint:blint(...)" with the file and the
                    // qualifier both spelling it out. Navigation is unaffected: it matches on the
                    // DkmModule's name (the full path), not this display name.
                    var moduleInstance = DkmCustomModuleInstance.Create(
                        ModuleDisplayName(path), path,
                        0, runtime, null, null,
                        DkmModuleFlags.None, DkmModuleMemoryLayout.Unknown,
                        0, 0, 0, "Shumway Prolog", false, null, null, null);

                    // Create WITHOUT the module, then SetModule: that raises the
                    // symbols-loaded notification, which is what makes VS re-evaluate
                    // pending breakpoints against our symbol provider.
                    stage = 5;
                    moduleInstance.SetModule(module, true);

                    state.CreatedFiles.Add(path);
                    state.PendingFiles.Remove(path);
                }
            }
            catch (Exception ex)
            {
                // Wrong context (the stack-walk case). The files stay pending and the next
                // real stop picks them up.
                state.LastError = "modules@" + stage + ": " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>The module's Call-Stack display name: its base name with the <c>.pl</c>
        /// extension removed (and any that remain from an embedded <c>&lt;module&gt;.pl.pl</c>
        /// materialisation). Mirrors <c>ShumwayCallStackFilter</c>'s module derivation so the
        /// column and the frame agree.</summary>
        private static string ModuleDisplayName(string path)
        {
            string name;
            try { name = System.IO.Path.GetFileNameWithoutExtension(path); }
            catch (Exception) { name = path; }
            while (name.EndsWith(".pl", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 3);
            return name.Length > 0 ? name : System.IO.Path.GetFileName(path);
        }

        // ---- the channel ----

        /// <summary>Picks up the channel the engine published, once it has. Idempotent, and
        /// called at every moment where it might newly exist: the engine's module load (where
        /// it usually does not yet) and every stop (where it does).
        ///
        /// <para>It also brings the SOURCE FILES the engine is about to consult. Those matter
        /// before a single goal runs: a breakpoint binds against a module, a module is a .pl
        /// file, and until the debugger knows the file names there is nothing for the user's
        /// red dot to attach to. Under a launch nobody has stopped anywhere yet, so there are
        /// no frames to learn them from — the engine simply says.</para></summary>
        private static void LoadChannel(DkmProcess process, ShumwayServerDataItem state)
        {
            int? processId = process.LivePart?.Id;
            if (processId == null) return;

            ShumwayChannelInfo? channel = ShumwayChannelFile.Read(processId.Value);
            if (channel == null || !channel.Usable) return;

            bool first = state.SnapshotAddress == 0;
            state.SnapshotAddress = channel.SnapshotAddress;
            state.CommandAddress = channel.CommandAddress;

            // RE-READ, at every stop, not once. The engine rewrites this file as it consults —
            // and which files a program is made of is not settled when it starts: a top level
            // consults on demand. A file we have not heard of has no module, and a frame in it
            // is grey: no language, no source, nothing to click.
            int learned = 0;
            foreach (string raw in channel.Files)
            {
                string file = ShumwaySession.Canonical(raw);
                if (file.Length > 0 && !state.CreatedFiles.Contains(file)
                    && !state.PendingFiles.Contains(file))
                {
                    state.PendingFiles.Add(file);
                    learned++;
                }
            }
            if (first)
                ShumwayLog.Write("  channel found: " + Status(state));
            else if (learned > 0)
                ShumwayLog.Write("  " + learned + " newly consulted file(s): " + Status(state));
        }

        private static DebugSnapshot? ReadSnapshot(DkmProcess process, ShumwayServerDataItem state)
        {
            if (state.SnapshotAddress == 0) return null;
            try
            {
                // THE WHOLE REGION. Reading a prefix of it was a quiet bug of its own: a stack
                // longer than the prefix came back with its frames cut off — and a reader that
                // walks a frame count through bytes it does not have is a reader that reads
                // rubbish. The buffer is a fixed size, the engine says so, and it is small.
                var bytes = new byte[DebugWire.SnapshotCapacity];
                process.ReadMemory((ulong)state.SnapshotAddress, DkmReadMemoryFlags.None, bytes);
                DebugSnapshot? snapshot = DebugWire.ReadSnapshot(bytes);
                if (snapshot == null)
                    ShumwayLog.Write("  snapshot unreadable (format v"
                        + (bytes[0] | (bytes[1] << 8)) + "?)");
                return snapshot;
            }
            catch (Exception ex)
            {
                ShumwayLog.Write("  snapshot read threw: " + ex.GetType().Name + ": " + ex.Message);
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
                string condition;
                state.ConditionsByBp.TryGetValue(entry.Value.UniqueId, out condition);
                commands.Add(new DebugWireCommand
                {
                    Kind = DebugCommandKind.AddBreakpoint,
                    File = entry.Key.Substring(0, bar),
                    Line = int.Parse(entry.Key.Substring(bar + 1), CultureInfo.InvariantCulture),
                    Condition = condition ?? "",
                });
            }
            if (state.PendingStep != DebugCommandKind.None)
                commands.Add(new DebugWireCommand { Kind = state.PendingStep });
            if (state.NeedHello)
                commands.Add(new DebugWireCommand { Kind = DebugCommandKind.Hello });
            if (state.AsyncBreakPending)
                commands.Add(new DebugWireCommand { Kind = DebugCommandKind.BreakNow });

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
                    .First(m => ShumwaySession.IsSameModule(m.Name, ShumwaySession.EngineModule));

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
            catch (Exception ex)
            {
                // Without the notify breakpoint there are no Prolog stops at all — but the
                // stack still works (the engine leaves a fresh snapshot while it runs), so
                // the session is degraded, not dead. Never take one down from here.
                state.LastError = "notify-bp: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        internal static ShumwayServerDataItem State(DkmProcess process) => GetState(process);

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
