// Shumway debugger - call stack filter (ADR-035, phase D2).
//
// The physical C# stack is NOT the Prolog stack. Tier-0 runs an entire Prolog
// program inside a single BytecodeInterpreter.Dispatch frame, so what the CLR can
// show the user is one frame that says "Dispatch" and nothing else. The Prolog
// stack lives in the Activation's environment chain, and only the engine can read
// it — which it does, into the snapshot, before it stops.
//
// So this filter does exactly one thing: where the walk enters the engine, it
// splices in the frames the engine reported, and swallows the machinery.
// Everything else — the user's C# embedder, a [PrologPredicate] bridge, a native
// frame under a P/Invoke — passes through untouched. That is what makes the stack
// MIXED rather than merely Prolog-shaped.
//
// Contract notes (verified in the D0 spike):
// - FilterNextFrame is SYNCHRONOUS, top-down; input == null means end of walk.
// - Returning the input unchanged = pass-through. Returning an empty array = drop.
// - An exception here TRUNCATES the user's call stack. Nothing below throws.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.CustomRuntimes;
using Shumway.Embedding.Debugging;

namespace Shumway.Debugger.Concord
{
    /// <summary>Per-stack-walk state (the walk is one frame at a time, top-down).</summary>
    internal sealed class ShumwayStackDataItem : DkmDataItem
    {
        public bool SawFirstFrame;
        public bool EmittedPrologFrames;
        public DebugSnapshot? Snapshot;

        public static ShumwayStackDataItem GetInstance(DkmStackContext stackContext)
        {
            ShumwayStackDataItem? item = stackContext.GetDataItem<ShumwayStackDataItem>();
            if (item == null)
            {
                item = new ShumwayStackDataItem();
                stackContext.SetDataItem(DkmDataCreationDisposition.CreateNew, item);
            }
            return item;
        }
    }

    /// <summary>A synthesized frame's identity, carried in its instruction address: the
    /// index of the frame in the stop snapshot. It is the only thing that survives the
    /// trip to the expression evaluator, which is handed a frame and nothing else.</summary>
    internal static class ShumwayFrameId
    {
        public static ReadOnlyCollection<byte> Encode(int index)
        {
            return new ReadOnlyCollection<byte>(BitConverter.GetBytes(index));
        }

        public static bool TryDecode(DkmStackWalkFrame frame, out int index)
        {
            index = -1;
            if (frame.InstructionAddress is DkmCustomInstructionAddress custom
                && custom.EntityId != null && custom.EntityId.Count >= 4)
            {
                index = custom.EntityId[0]
                    | (custom.EntityId[1] << 8)
                    | (custom.EntityId[2] << 16)
                    | (custom.EntityId[3] << 24);
                return index >= 0;
            }
            return false;
        }
    }

    public sealed class ShumwayCallStackFilter : IDkmCallStackFilter
    {
        DkmStackWalkFrame[]? IDkmCallStackFilter.FilterNextFrame(
            DkmStackContext stackContext, DkmStackWalkFrame input)
        {
            if (input == null)
                return null; // end of walk

            try
            {
                return Filter(stackContext, input);
            }
            catch (Exception)
            {
                return new[] { input };
            }
        }

        private static DkmStackWalkFrame[] Filter(
            DkmStackContext stackContext, DkmStackWalkFrame input)
        {
            ShumwayStackDataItem walk = ShumwayStackDataItem.GetInstance(stackContext);

            // The top frame decides how we learn where the machine is. If it is the
            // engine's Notify, this is a PORT STOP: the engine already wrote the
            // snapshot, on its own terms, before tripping the breakpoint — read it.
            // Anything else is an ASYNCHRONOUS break (the user hit Break All, or
            // stopped on some C# breakpoint of their own): the machine is at no port,
            // the buffer holds the last stop, and believing it would be a lie. Ask.
            if (!walk.SawFirstFrame)
            {
                walk.SawFirstFrame = true;
                ShumwaySessionDataItem session = ShumwaySession.Attach(stackContext, input);
                if (session.Attached)
                {
                    if (!IsNotifyFrame(input))
                        ShumwaySession.CaptureNow(stackContext, input);
                    walk.Snapshot = ShumwaySession.ReadSnapshot(input.Process, session);
                    if (walk.Snapshot != null)
                        EnsureModules(input.Process, walk.Snapshot);
                }
            }

            if (!ShumwaySession.IsEngineModule(input.ModuleInstance?.Name))
                return new[] { input }; // the user's own frames: not ours to touch

            // An engine frame. The first one is where the Prolog stack belongs; the
            // rest of the machinery is swallowed. (A nested activation — a foreign
            // predicate that queries the engine again — reports the innermost stack,
            // which is the one the user stopped in; its outer engine frames are
            // dropped with the rest.)
            if (walk.EmittedPrologFrames)
                return Array.Empty<DkmStackWalkFrame>();
            walk.EmittedPrologFrames = true;

            if (walk.Snapshot == null || walk.Snapshot.Frames.Count == 0)
            {
                ShumwaySessionDataItem session = ShumwaySession.GetState(input.Process);
                return new[] { Annotated(stackContext, input, "[Shumway] " + session.Diagnostic) };
            }

            var frames = new List<DkmStackWalkFrame>(walk.Snapshot.Frames.Count);
            for (int i = 0; i < walk.Snapshot.Frames.Count; i++)
                frames.Add(Synthesize(stackContext, input, walk.Snapshot.Frames[i], i));
            return frames.ToArray();
        }

        private static bool IsNotifyFrame(DkmStackWalkFrame frame)
        {
            if (!string.Equals(frame.ModuleInstance?.Name, ShumwaySession.EngineModule,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            string? method = frame.BasicSymbolInfo?.MethodName;
            return method != null && method.IndexOf("Notify", StringComparison.Ordinal) >= 0;
        }

        /// <summary>One Prolog frame: named, and addressed at its source line.</summary>
        private static DkmStackWalkFrame Synthesize(
            DkmStackContext stackContext, DkmStackWalkFrame input,
            DebugSnapshotFrame source, int index)
        {
            return DkmStackWalkFrame.Create(
                stackContext.Thread,
                MakeAddress(input.Process, source, index),
                input.FrameBase,
                0,
                DkmStackWalkFrameFlags.None,
                source.Name + "/" + source.Arity,
                null,
                null);
        }

        /// <summary>The address is what makes a synthesized frame navigable: the module
        /// IS the .pl file, the offset IS the line, and the entity id carries the frame's
        /// INDEX — which is how the expression evaluator, handed nothing but a frame,
        /// finds that frame's variables in the snapshot again. Without the module (the
        /// engine has not named this file yet) the frame still shows; it just can't be
        /// double-clicked, and its locals are empty.</summary>
        private static DkmInstructionAddress? MakeAddress(
            DkmProcess process, DebugSnapshotFrame source, int index)
        {
            if (string.IsNullOrEmpty(source.File))
                return null;

            DkmCustomRuntimeInstance? runtime = process.GetRuntimeInstances()
                .OfType<DkmCustomRuntimeInstance>()
                .FirstOrDefault(r => r.Id.RuntimeType == ShumwayGuids.RuntimeType);
            DkmCustomModuleInstance? module = runtime?.GetModuleInstances()
                .OfType<DkmCustomModuleInstance>()
                .FirstOrDefault(m => string.Equals(m.FullName, source.File, StringComparison.OrdinalIgnoreCase));
            if (runtime == null || module == null)
                return null;

            return DkmCustomInstructionAddress.Create(
                runtime, module,
                EntityId: ShumwayFrameId.Encode(index),
                Offset: (ulong)Math.Max(source.Line, 0),
                AdditionalData: null,
                CPUInstruction: null);
        }

        private static DkmStackWalkFrame Annotated(
            DkmStackContext stackContext, DkmStackWalkFrame input, string text)
        {
            return DkmStackWalkFrame.Create(
                stackContext.Thread, null, input.FrameBase, 0,
                DkmStackWalkFrameFlags.None, text, null, null);
        }

        /// <summary>Every .pl the engine names in a frame becomes a module, once. The
        /// creation itself is the server's job (a monitor API), and it must happen in a
        /// real event context — from inside a stack walk it throws. So we tell the
        /// server what we saw; if it cannot act on it now it will at the next pause,
        /// and the frames become navigable one stop later rather than never.</summary>
        private static void EnsureModules(DkmProcess process, DebugSnapshot snapshot)
        {
            ShumwaySessionDataItem session = ShumwaySession.GetState(process);
            var fresh = new List<string>();
            foreach (DebugSnapshotFrame frame in snapshot.Frames)
            {
                if (string.IsNullOrEmpty(frame.File)) continue;
                if (session.KnownFiles.Contains(frame.File)) continue;
                session.KnownFiles.Add(frame.File);
                fresh.Add(frame.File);
            }
            if (fresh.Count == 0)
                return;

            try
            {
                DkmCustomMessage.Create(
                        process.Connection, process, ShumwayGuids.MessageSource,
                        ShumwayGuids.MsgEnsureModules, string.Join("|", fresh.ToArray()), null, null, null)
                    .SendLower();
            }
            catch (Exception ex)
            {
                session.Diagnostic = "modules: " + ex.Message;
            }
        }
    }
}
