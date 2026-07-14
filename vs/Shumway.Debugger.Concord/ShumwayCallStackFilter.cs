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
using System.Text;
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
        public bool EmittedPrologFrames;
        public DebugSnapshot? Snapshot;

        /// <summary>The engine frames seen so far in this walk, and whether the topmost of
        /// them was Notify (which is what makes this a port stop rather than a Break All).
        /// The Prolog stack takes their place, but not until the run of them ENDS: any one of
        /// them might be the frame that can answer the handshake, and we cannot know which
        /// until we have tried.</summary>
        public bool SawEngineFrame;
        public bool TopEngineFrameIsNotify;
        public DkmStackWalkFrame? Anchor;

        /// <summary>Whether this thread is running the MACHINE — i.e. the bytecode
        /// interpreter is somewhere below us.
        ///
        /// <para>An engine frame is not enough to earn the Prolog stack. Other threads of the
        /// process sit inside engine code without executing a single goal: the debug
        /// session's own idle watcher does, sleeping; so would a background compiler or a
        /// second engine. Splicing the machine's stack onto them puts a program's call stack
        /// on a thread that is not running it — plausible, and false. The interpreter's
        /// presence is what makes a thread the one the Prolog stack belongs to.</para></summary>
        public bool SawMachineFrame;

        /// <summary>The engine frames taken out of the walk so far. They are given BACK,
        /// unchanged, if the machine turns out not to be on this thread — swallowing a
        /// thread's frames and then putting nothing in their place would leave it looking
        /// like it had no stack at all.</summary>
        public readonly List<DkmStackWalkFrame> Swallowed = new List<DkmStackWalkFrame>();

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
            try
            {
                ShumwayStackDataItem walk = ShumwayStackDataItem.GetInstance(stackContext);

                if (input == null)
                {
                    // End of the walk. If the engine's frames ran all the way to the bottom
                    // of the stack, this is the last chance to put the Prolog stack in their
                    // place.
                    if (!walk.SawEngineFrame || walk.EmittedPrologFrames)
                        return null;
                    return walk.SawMachineFrame
                        ? Emit(stackContext, walk, null)
                        : GiveBack(walk, null);
                }
                return Filter(stackContext, walk, input);
            }
            catch (Exception)
            {
                return input == null ? null : new[] { input };
            }
        }

        private static DkmStackWalkFrame[] Filter(
            DkmStackContext stackContext, ShumwayStackDataItem walk, DkmStackWalkFrame input)
        {
            if (ShumwaySession.IsEngineModule(input.ModuleInstance?.Name))
            {
                if (ShumwaySession.IsMachineModule(input.ModuleInstance?.Name))
                    walk.SawMachineFrame = true;

                // An engine frame. Every one of them is swallowed — the Prolog stack takes
                // the place of the whole run — but not yet: the handshake with the debuggee
                // is a func-eval, a func-eval is evaluated in the context of a FRAME, and a
                // frame can only name what its own module can see. So we do not get to pick
                // which engine frame answers it. We try each in turn until one does.
                if (!walk.SawEngineFrame)
                {
                    walk.SawEngineFrame = true;
                    // The TOP engine frame decides how we learn where the machine is: if it
                    // is Notify, this is a PORT STOP and the engine has already written the
                    // snapshot on its own terms. Anything else is an asynchronous break, and
                    // the buffer holds the last stop — which would be a lie.
                    walk.TopEngineFrameIsNotify = IsNotifyFrame(input);
                    walk.Anchor = input;

                    // Kept for the Immediate window: a goal evaluation func-evals against
                    // this frame — a REAL CLR frame of the thread's break state, which is
                    // what the C# evaluator accepts. Refreshed at every walk.
                    ShumwaySession.GetState(input.Process)
                        .EvalAnchors[stackContext.Thread.UniqueId] = input;
                }

                ShumwaySessionDataItem session = ShumwaySession.Attach(stackContext, input);
                if (session.Attached && walk.Snapshot == null)
                {
                    // Just read. At a port stop the engine wrote the snapshot before it
                    // tripped the breakpoint; at a Break All it has been leaving a fresh one
                    // every few dozen milliseconds. Either way the answer is already there,
                    // and nothing runs in the debuggee to produce it — which is the only way
                    // it CAN work: Visual Studio refuses to evaluate a method that touches an
                    // intrinsic, and the engine's capture path is full of them.
                    walk.Snapshot = ShumwaySession.ReadSnapshot(input.Process, session);
                    if (walk.Snapshot != null)
                        EnsureModules(input.Process, walk.Snapshot);
                }
                walk.Swallowed.Add(input);
                return Array.Empty<DkmStackWalkFrame>();
            }

            // Not an engine frame: the user's own. If the engine's run just ended, the Prolog
            // stack goes here — above this frame, which is the one that called into it. But
            // only if the MACHINE was in that run: a thread that merely passed through engine
            // code without running a goal has no Prolog stack, and must be shown as it is.
            if (walk.SawEngineFrame && !walk.EmittedPrologFrames)
            {
                return walk.SawMachineFrame
                    ? Emit(stackContext, walk, input)
                    : GiveBack(walk, input);
            }

            return new[] { input };
        }

        /// <summary>This thread went through engine code without running the machine — the
        /// debug session's idle watcher, say, asleep in a timer. It has no Prolog stack, and
        /// it must not be dressed in somebody else's. Its own frames go back exactly as they
        /// came.</summary>
        private static DkmStackWalkFrame[] GiveBack(
            ShumwayStackDataItem walk, DkmStackWalkFrame? below)
        {
            walk.EmittedPrologFrames = true;   // decided: nothing more to splice on this walk
            var frames = new List<DkmStackWalkFrame>(walk.Swallowed);
            if (below != null) frames.Add(below);
            return frames.ToArray();
        }

        /// <summary>The Prolog stack, in place of the engine frames it replaces, followed by
        /// whatever called into the engine (null at the bottom of the stack).</summary>
        private static DkmStackWalkFrame[] Emit(
            DkmStackContext stackContext, ShumwayStackDataItem walk, DkmStackWalkFrame? below)
        {
            walk.EmittedPrologFrames = true;
            DkmStackWalkFrame anchor = walk.Anchor!;

            var frames = new List<DkmStackWalkFrame>();
            if (walk.Snapshot == null || walk.Snapshot.Frames.Count == 0)
            {
                ShumwaySessionDataItem session = ShumwaySession.GetState(anchor.Process);
                frames.Add(Annotated(stackContext, anchor, "[Shumway] " + session.Diagnostic));
            }
            else
            {
                for (int i = 0; i < walk.Snapshot.Frames.Count; i++)
                    frames.Add(Synthesize(stackContext, anchor, walk.Snapshot.Frames[i], i));
            }

            // The monitor side has no voice of its own: when it fails to create a module or
            // arm the notify breakpoint, the only symptom in the IDE is that nothing happens.
            // SHUMWAY_DEBUG_DIAG=1 in the environment devenv was started with makes it speak.
            if (Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_DIAG") == "1")
            {
                ShumwaySessionDataItem session = ShumwaySession.GetState(anchor.Process);
                frames.Add(Annotated(stackContext, anchor,
                    "[Shumway diag] ide: " + session.Diagnostic
                    + " || asked: " + ShumwayIdeDiag.Summary
                    + " || snap: " + Describe(walk.Snapshot)
                    + " || server: " + ShumwaySession.ServerStatus(anchor.Process)));
            }

            if (below != null)
                frames.Add(below);
            return frames.ToArray();
        }

        /// <summary>What the engine actually said — reason, frames, and how many variables
        /// each frame carries. When the Locals window is empty there are exactly two
        /// possibilities, and they look identical from the IDE: the engine sent no variables,
        /// or it sent them and we never got asked for them. This tells them apart.</summary>
        private static string Describe(DebugSnapshot? snapshot)
        {
            if (snapshot == null) return "(none)";
            var text = new System.Text.StringBuilder();
            text.Append(snapshot.Reason).Append(" seq=").Append(snapshot.Sequence)
                .Append(' ').Append(snapshot.File).Append(':').Append(snapshot.Line)
                .Append(" [");
            for (int i = 0; i < snapshot.Frames.Count; i++)
            {
                if (i > 0) text.Append(", ");
                DebugSnapshotFrame frame = snapshot.Frames[i];
                text.Append(frame.Name).Append('/').Append(frame.Arity)
                    .Append(':').Append(frame.Variables.Count).Append("vars");
            }
            return text.Append(']').ToString();
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
            // Registers and annotations are the ANCHOR's — the engine frame this one stands in
            // for. They are not decoration: they are how Visual Studio knows a frame is a real
            // execution location rather than a label. Without them the frame renders, and is
            // navigable, and is invisible to everything that matters — no language is asked
            // for, so no expression evaluator is routed to it (Locals come up empty), and no
            // runtime claims it, so a step is never offered to us. Both were dead in exactly
            // this way until the frames carried them. (PTVS does the same, from the native
            // frame it replaces.)
            return DkmStackWalkFrame.Create(
                stackContext.Thread,
                MakeAddress(input.Process, source, index),
                input.FrameBase,
                input.FrameSize,
                DkmStackWalkFrameFlags.None,
                FrameTitle(source),
                input.Registers,
                input.Annotations);
        }

        /// <summary>What the Call Stack window prints for one Prolog frame:
        /// <c>module:pred(Arg1, ..., ArgN)!clause</c> — the module is the file's base name,
        /// the arguments carry their CURRENT values (they instantiate as the clause runs),
        /// and <c>!clause</c> says which clause of the predicate is being evaluated, 1-based.
        /// A frame with no head skeleton (not compiled debuggable) falls back to
        /// <c>pred/arity</c>; the query (arity -1, <c>?- goal</c>) and the omitted-frames
        /// sentence are not calls and show as themselves.</summary>
        private static string FrameTitle(DebugSnapshotFrame source)
        {
            if (source.Arity < 0) return source.Name;

            string module = "";
            string file = source.File ?? "";
            if (file.Length > 0 && file[0] != '<')
            {
                try { module = System.IO.Path.GetFileNameWithoutExtension(file); }
                catch (Exception) { module = ""; }
            }

            var title = new StringBuilder();
            if (module.Length > 0) title.Append(module).Append(':');
            title.Append(source.Name);
            if (source.HeadArgs.Length > 0) title.Append(source.HeadArgs);
            else if (source.Arity > 0) title.Append('/').Append(source.Arity);
            if (source.ClauseNumber > 0) title.Append('!').Append(source.ClauseNumber);
            return title.ToString();
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
            string wanted = ShumwaySession.Canonical(source.File);
            DkmCustomModuleInstance? module = runtime?.GetModuleInstances()
                .OfType<DkmCustomModuleInstance>()
                .FirstOrDefault(m => string.Equals(
                    ShumwaySession.Canonical(m.FullName), wanted, StringComparison.OrdinalIgnoreCase));
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
                string file = ShumwaySession.Canonical(frame.File);
                if (file.Length == 0) continue;
                if (session.KnownFiles.Contains(file)) continue;
                session.KnownFiles.Add(file);
                fresh.Add(file);
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
