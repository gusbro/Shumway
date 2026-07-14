// Shumway debugger - IDE-side session state (ADR-035, phase D2).
//
// The debuggee's half of this conversation is DebugChannel + ShumwayDebugHelper. The
// contract, in full:
//
//   * ONCE, at attach, we func-eval ShumwayDebugHelper.Attach(). It hands back the
//     addresses of two pinned buffers. A func-eval is safe here — this is a normal
//     stop, not the breakpoint-notification context where evaluating a function in the
//     debuggee is documented to deadlock (ConcordExtensibilitySamples #61).
//
//   * At a PORT STOP (our hidden breakpoint on Notify), the engine has ALREADY written
//     the whole stop into the snapshot buffer, before tripping the breakpoint. So we
//     read memory. We run nothing in the debuggee, which is the point.
//
//   * At an ASYNCHRONOUS BREAK (the user hit Break All), the machine is at no port and
//     the buffer holds the last real stop, which would be a lie. So we ask:
//     CaptureNow() — again a func-eval, again from a normal stop, and the engine writes
//     the truth.

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Shumway.Embedding.Debugging;

namespace Shumway.Debugger.Concord
{
    /// <summary>Per-process session state, hung off the DkmProcess.</summary>
    internal sealed class ShumwaySessionDataItem : DkmDataItem
    {
        /// <summary>How many frames we have asked to do the handshake. It is not a given
        /// that any particular one CAN: a func-eval is evaluated in the context of a frame,
        /// and a frame can only name what its own module can see (and some carry no symbols
        /// at all). So we try the engine frames in turn — but a handful, not fifty, because
        /// each attempt runs code in the debuggee.</summary>
        public int AttachAttempts;
        public const int MaxAttachAttempts = 8;
        public long SnapshotAddress;
        public int SnapshotLength;
        public long CommandAddress;
        public int CommandLength;
        public string Diagnostic = "not attached";

        /// <summary>.pl files the engine has named in a frame — one DkmModule each.</summary>
        public readonly HashSet<string> KnownFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool Attached => SnapshotAddress != 0;
    }

    /// <summary>What Visual Studio has actually ASKED us, on the IDE side. A frame that shows
    /// no variables and takes no step looks identical whether we answered badly or were never
    /// asked — and the difference is the whole diagnosis. These say which. Counted per
    /// devenv, not per process: they are about the routing, and the routing is global.</summary>
    internal static class ShumwayIdeDiag
    {
        public static int CompilerIdAsks;
        public static int SourcePositionAsks;
        public static int FrameNameAsks;
        public static int FrameLocalsAsks;
        public static int EvaluateAsks;
        public static string LastLocals = "-";

        public static string Summary =>
            "compilerid=" + CompilerIdAsks
            + " srcpos=" + SourcePositionAsks
            + " framename=" + FrameNameAsks
            + " locals=" + FrameLocalsAsks + "(" + LastLocals + ")"
            + " eval=" + EvaluateAsks;
    }

    internal static class ShumwaySession
    {
        /// <summary>The module the hidden notify breakpoint is planted in — and the module
        /// whose types the debugger names when it func-evals. Core, not Embedding: a frame
        /// can only name what its own module references, and the frame we stop on is an
        /// engine frame (often the interpreter), which does not reference Embedding. Core is
        /// the one assembly all of them share.</summary>
        public const string EngineModule = "Shumway.Core.dll";

        /// <summary>Modules whose frames ARE the Prolog machine, and which the user
        /// therefore must not see: the interpreter runs the whole program inside one
        /// Dispatch frame, and the debug plumbing above it is ours, not theirs. They are
        /// replaced, in one go, by the frames the engine reports.</summary>
        public static bool IsEngineModule(string? moduleName)
        {
            if (string.IsNullOrEmpty(moduleName)) return false;
            return moduleName!.Equals("Shumway.Interpreter.dll", StringComparison.OrdinalIgnoreCase)
                || moduleName.Equals("Shumway.Core.dll", StringComparison.OrdinalIgnoreCase)
                || moduleName.Equals("Shumway.Embedding.dll", StringComparison.OrdinalIgnoreCase)
                || moduleName.Equals("Shumway.Compiler.dll", StringComparison.OrdinalIgnoreCase)
                || moduleName.Equals("Shumway.Builtins.dll", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The module that IS the machine: the bytecode interpreter. Its presence on
        /// a thread is what says that thread is running a Prolog program — which is a
        /// different question from whether the thread is inside engine code, and the one that
        /// decides who gets the Prolog stack. Other threads sit in engine code without running
        /// a goal (the debug session's own idle watcher, asleep), and giving them the
        /// machine's frames would show a program's call stack on a thread that is not running
        /// it.</summary>
        public static bool IsMachineModule(string? moduleName)
        {
            return !string.IsNullOrEmpty(moduleName)
                && moduleName!.Equals("Shumway.Interpreter.dll", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>ONE SPELLING OF A PATH. A module is identified by its file, and a file
        /// arrives spelled several ways: the engine's command line, a `consult('c:/x/y.pl')`
        /// the user typed with forward slashes, the name a frame reports. Compared as strings,
        /// those are three different files — so a frame ended up matched against no module at
        /// all, and showed grey, with no language and nothing to click. Compared as PATHS they
        /// are one.</summary>
        public static string Canonical(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return System.IO.Path.GetFullPath(path); }
            catch (Exception) { return path!; }
        }

        /// <summary>The session, keyed by the process it belongs to — and kept HERE, not in a
        /// DkmDataItem on the DkmProcess.
        ///
        /// <para>A data item is not as shared as it looks. The stack filter attached, wrote the
        /// channel addresses into one, and the expression evaluator — same assembly, same
        /// devenv, same process — read back a data item with nothing in it, decided no session
        /// existed, and showed an empty Locals window with no error to show for it. Data items
        /// are per-component; these two are different components. This dictionary is not.</para></summary>
        private static readonly Dictionary<Guid, ShumwaySessionDataItem> Sessions =
            new Dictionary<Guid, ShumwaySessionDataItem>();

        public static ShumwaySessionDataItem GetState(DkmProcess process)
        {
            lock (Sessions)
            {
                ShumwaySessionDataItem? state;
                if (!Sessions.TryGetValue(process.UniqueId, out state))
                {
                    state = new ShumwaySessionDataItem();
                    Sessions[process.UniqueId] = state;
                }
                return state;
            }
        }

        /// <summary>The handshake, once per process. Never throws: a filter that throws
        /// truncates the user's call stack.</summary>
        public static ShumwaySessionDataItem Attach(
            DkmStackContext stackContext, DkmStackWalkFrame frame)
        {
            ShumwaySessionDataItem state = GetState(frame.Process);
            if (state.Attached || state.AttachAttempts >= ShumwaySessionDataItem.MaxAttachAttempts)
                return state;
            state.AttachAttempts++;

            // D4 — the channel comes from the FILE the engine published when its session
            // opened, not from reading its memory through a frame. The frame-based read
            // worked, but only where there was a frame: a LAUNCHED process never stops, so
            // it could never be attached to, so no breakpoint could ever be armed in one.
            // The file is there before the program is.
            int? processId = frame.Process.LivePart?.Id;
            ShumwayChannelInfo? channel = processId == null
                ? null
                : ShumwayChannelFile.Read(processId.Value);
            if (channel != null && channel.Usable)
            {
                state.SnapshotAddress = channel.SnapshotAddress;
                state.SnapshotLength = channel.SnapshotLength;
                state.CommandAddress = channel.CommandAddress;
                state.CommandLength = channel.CommandLength;
                state.Diagnostic = "attached";
                ArmNotifyBreakpoint(frame.Process, state, channel.NotifyMetadataToken);
                return state;
            }

            state.Diagnostic = "no debug session in the debuggee "
                + "(run it with --debug, or construct a ChannelDebugSession)";
            return state;
        }

        /// <summary>The old handshake: read the engine's published addresses out of static
        /// fields, through a frame. Kept because it is the fallback if the channel file cannot
        /// be written (a process with no writable temp directory) and because it is the only
        /// thing that would work if the debuggee were on ANOTHER MACHINE, where the file is
        /// not ours to read. Not currently reached.</summary>
        private static ShumwaySessionDataItem AttachByEvaluation(
            DkmStackContext stackContext, DkmStackWalkFrame frame, ShumwaySessionDataItem state)
        {
            string where = (frame.ModuleInstance?.Name ?? "?")
                + "!" + (frame.BasicSymbolInfo?.MethodName ?? "?");
            try
            {
                // FIELD READS, not a method call. Reading a field inspects memory; calling a
                // method runs code in the debuggee, on a thread — and Visual Studio will not
                // run code on a thread that is not the current one. The engine's thread very
                // often is not (a Break All lands the current thread wherever it likes), and
                // the first run of this in VS proved it: the same frame that could read
                // ShumwayDebugHost.NotifyCount could not call ShumwayDebugHost.Attach().
                const string Host = "Shumway.Core.Debugging.ShumwayDebugHost.";

                string? versionText = Evaluate(stackContext, frame, Host + "SessionFormatVersion", out string? error);
                if (versionText == null)
                {
                    state.Diagnostic = "cannot read the engine's debug state at " + where + ": " + error;
                    return state;
                }
                int version = ParseInt(versionText);
                if (version == 0)
                {
                    state.Diagnostic = "no debug session in the debuggee "
                        + "(run it with --debug, or construct a ChannelDebugSession)";
                    return state;
                }
                if (version != DebugWire.FormatVersion)
                {
                    // Engine and debugger built from different sources. Say so: reading the
                    // buffer anyway would produce a plausible, wrong stack.
                    state.Diagnostic = "the engine speaks debug format v" + version
                        + ", this debugger speaks v" + DebugWire.FormatVersion;
                    return state;
                }

                state.SnapshotAddress = ParseLong(Evaluate(stackContext, frame, Host + "SnapshotAddress", out _));
                state.SnapshotLength = ParseInt(Evaluate(stackContext, frame, Host + "SnapshotLength", out _));
                state.CommandAddress = ParseLong(Evaluate(stackContext, frame, Host + "CommandAddress", out _));
                state.CommandLength = ParseInt(Evaluate(stackContext, frame, Host + "CommandLength", out _));

                if (state.SnapshotAddress == 0 || state.SnapshotLength == 0)
                {
                    state.Diagnostic = "the engine published no channel address";
                    state.SnapshotAddress = 0;
                    return state;
                }

                state.Diagnostic = "attached (by evaluation)";
                string? tokenText = Evaluate(stackContext, frame, Host + "NotifyMetadataToken", out _);
                ArmNotifyBreakpoint(frame.Process, state, ParseInt(tokenText));
            }
            catch (Exception ex)
            {
                state.Diagnostic = "attach: " + ex.GetType().Name + ": " + ex.Message;
            }
            return state;
        }

        private static int ParseInt(string? text)
        {
            int value;
            return text != null
                && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value : 0;
        }

        private static long ParseLong(string? text)
        {
            long value;
            return text != null
                && long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value : 0;
        }

        /// <summary>The hidden breakpoint on ShumwayDebugHelper.Notify is what turns a
        /// port into a stop. Planting it is a monitor-side act, so the IDE only supplies
        /// the coordinates: the method's metadata token (read out of the debuggee itself
        /// — no symbol file, no assumption about where the DLL came from) and the address
        /// of the snapshot buffer.</summary>
        private static void ArmNotifyBreakpoint(
            DkmProcess process, ShumwaySessionDataItem state, int token)
        {
            if (token == 0)
            {
                state.Diagnostic = "attached (the engine published no notify token)";
                return;
            }

            DkmCustomMessage.Create(
                    process.Connection, process,
                    ShumwayGuids.MessageSource, ShumwayGuids.MsgArmNotifyBreakpoint,
                    state.SnapshotAddress, token,
                    // The server needs both ends of the channel: the snapshot to read a
                    // stop, and the command region to answer it.
                    state.CommandAddress.ToString(CultureInfo.InvariantCulture), null)
                .SendLower();
        }

        /// <summary>What the server component has managed to do. It runs on the monitor side
        /// and has no output of its own; this is a round trip to ask it.</summary>
        public static string ServerStatus(DkmProcess process)
        {
            try
            {
                DkmCustomMessage? reply = DkmCustomMessage.Create(
                        process.Connection, process, ShumwayGuids.MessageSource,
                        ShumwayGuids.MsgServerStatus, null, null)
                    .SendLower();
                return reply?.Parameter1 as string ?? "(no reply)";
            }
            catch (Exception ex)
            {
                return "(status failed: " + ex.Message + ")";
            }
        }

        /// <summary>The stop, read out of the debuggee's memory. Nothing runs over
        /// there: whatever the engine had to say, it said before it stopped.</summary>
        public static DebugSnapshot? ReadSnapshot(DkmProcess process, ShumwaySessionDataItem state)
        {
            if (!state.Attached) return null;
            try
            {
                var bytes = new byte[state.SnapshotLength];
                process.ReadMemory((ulong)state.SnapshotAddress, DkmReadMemoryFlags.None, bytes);

                // A debugger and a debuggee that disagree about the layout of this buffer do
                // not fail loudly — they show a plausible, wrong stack, or none. So say it.
                // The two are built together and shipped apart: an engine rebuilt after the
                // extension was installed is the ordinary way to end up here, and "no symbols"
                // or an empty stack is a terrible way to be told.
                int at = 0;
                int version = DebugWire.ReadInt(bytes, ref at);
                if (version != DebugWire.FormatVersion)
                {
                    state.Diagnostic = "the Shumway debugger extension is out of date: the "
                        + "engine speaks channel format v" + version + ", this extension speaks v"
                        + DebugWire.FormatVersion + " — rebuild and reinstall the VSIX";
                    return null;
                }

                DebugSnapshot? snapshot = DebugWire.ReadSnapshot(bytes);

                // The engine is RUNNING, so this is the record of a stop that is over, not a
                // description of where the program is — and the process was frozen from
                // outside (a raw Break All we declined, a breakpoint in the user's C#, an
                // exception). Showing the last Prolog stack here would not be a stale answer,
                // it would be a wrong one: the program is not standing in those frames. Say
                // there is no Prolog stack, and let the C# the machine really is in speak for
                // itself. A pause that CAN be answered never gets here — it stops the engine
                // at a port first (see ShumwayAsyncBreak).
                //
                // EXCEPT inside a foreign call. There the engine is running by every measure
                // that matters — it has not stopped, and our stepper must not claim a step —
                // and yet the stack in the buffer is exactly true: the engine published it as
                // it crossed into the user's C#, and it is blocked in that call, which is
                // where the debugger is standing. That is the whole point of the interop
                // debugger: the C# frames Visual Studio shows, over the Prolog frames that
                // called them, in ONE stack.
                if (snapshot != null && snapshot.Running && snapshot.InteropDepth <= 0)
                    return null;

                return snapshot;
            }
            catch (Exception ex)
            {
                state.Diagnostic = "read: " + ex.Message;
                return null;
            }
        }

        // THE ASYNCHRONOUS BREAK used to live here, as a func-eval: ask the engine where it
        // is, since at a Break All it is at no port and has reported nothing. Visual Studio
        // will not have it. It runs code in the debuggee only on the thread it considers
        // current — and the engine's thread very often is not — and it refuses outright once
        // the method touches an intrinsic, which the capture path does, deep inside the
        // machine ("Evaluation of native methods in this context is not supported").
        //
        // So the engine no longer waits to be asked: while it runs, it leaves a fresh
        // snapshot in the buffer every few dozen milliseconds (ChannelDebugSession's sample
        // clock). A Break All just reads it. Which means this component now does exactly two
        // things to the debuggee — read its memory, and write commands into it — and never
        // runs a line of its code. That is a better design than the one it replaced, and it
        // was VS that insisted on it.

        /// <summary>Synchronous evaluation of a C# expression against a CLR frame. A field
        /// read only inspects memory; a method CALL runs code in the debuggee and is only
        /// permitted on the current thread. Both come through here.</summary>
        private static string? Evaluate(
            DkmStackContext stackContext, DkmStackWalkFrame frame,
            string expression, out string? error)
        {
            error = null;
            DkmLanguage language = DkmLanguage.Create(
                "C#", new DkmCompilerId(ShumwayGuids.MicrosoftVendor, ShumwayGuids.CSharpLanguage));
            DkmInspectionSession session = DkmInspectionSession.Create(frame.Process, null);
            try
            {
                DkmInspectionContext inspection = DkmInspectionContext.Create(
                    session, frame.RuntimeInstance, stackContext.Thread,
                    Timeout: 5000,
                    EvaluationFlags: DkmEvaluationFlags.None,
                    FuncEvalFlags: DkmFuncEvalFlags.None,
                    Radix: 10, Language: language, ReturnValue: null);

                using (DkmLanguageExpression expr = DkmLanguageExpression.Create(
                    language, DkmEvaluationFlags.None, expression, null))
                {
                    string? value = null;
                    string? failure = null;
                    DkmWorkList workList = DkmWorkList.Create(null);
                    inspection.EvaluateExpression(workList, expr, frame, result =>
                    {
                        try
                        {
                            if (result.ErrorCode == 0 && result.ResultObject is DkmSuccessEvaluationResult ok)
                                value = ok.Value;
                            else if (result.ResultObject is DkmFailedEvaluationResult bad)
                                failure = bad.ErrorMessage;
                            else
                                failure = "hr=0x" + result.ErrorCode.ToString("X8");
                            result.ResultObject?.Close();
                        }
                        catch (Exception cbEx)
                        {
                            failure = cbEx.Message;
                        }
                    });
                    workList.Execute();

                    if (value == null) error = failure ?? "no-result";
                    return value;
                }
            }
            finally
            {
                session.Close();
            }
        }
    }
}
