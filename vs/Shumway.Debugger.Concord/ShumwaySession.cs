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
        public bool AttachAttempted;
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

    internal static class ShumwaySession
    {
        /// <summary>The engine assembly — where the helper lives, and the module the
        /// hidden notify breakpoint is planted in.</summary>
        public const string EngineModule = "Shumway.Embedding.dll";

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

        public static ShumwaySessionDataItem GetState(DkmProcess process)
        {
            ShumwaySessionDataItem? state = process.GetDataItem<ShumwaySessionDataItem>();
            if (state == null)
            {
                state = new ShumwaySessionDataItem();
                process.SetDataItem(DkmDataCreationDisposition.CreateNew, state);
            }
            return state;
        }

        /// <summary>The handshake, once per process. Never throws: a filter that throws
        /// truncates the user's call stack.</summary>
        public static ShumwaySessionDataItem Attach(
            DkmStackContext stackContext, DkmStackWalkFrame frame)
        {
            ShumwaySessionDataItem state = GetState(frame.Process);
            if (state.AttachAttempted) return state;
            state.AttachAttempted = true;

            try
            {
                string? handshake = FuncEval(stackContext, frame,
                    "Shumway.Embedding.Debugging.ShumwayDebugHelper.Attach()", out string? error);
                if (handshake == null)
                {
                    state.Diagnostic = "attach failed: " + error;
                    return state;
                }

                // The C# expression evaluator renders a string quoted; strip it.
                handshake = handshake.Trim();
                if (handshake.Length >= 2 && handshake[0] == '"'
                    && handshake[handshake.Length - 1] == '"')
                {
                    handshake = handshake.Substring(1, handshake.Length - 2);
                }
                if (handshake.Length == 0)
                {
                    state.Diagnostic = "no debug session in the debuggee "
                        + "(nothing called ChannelDebugSession)";
                    return state;
                }

                // "v1;snapshot=<hex>,<len>;commands=<hex>,<len>"
                if (!ParseHandshake(handshake, state, out string? parseError))
                {
                    state.Diagnostic = "attach: " + parseError + " [" + handshake + "]";
                    return state;
                }
                state.Diagnostic = "attached";
                ArmNotifyBreakpoint(stackContext, frame, state);
            }
            catch (Exception ex)
            {
                state.Diagnostic = "attach: " + ex.GetType().Name + ": " + ex.Message;
            }
            return state;
        }

        private static bool ParseHandshake(
            string handshake, ShumwaySessionDataItem state, out string? error)
        {
            error = null;
            string[] parts = handshake.Split(';');
            if (parts.Length < 3 || !parts[0].StartsWith("v", StringComparison.Ordinal))
            {
                error = "malformed handshake";
                return false;
            }
            if (parts[0] != "v" + DebugWire.FormatVersion.ToString(CultureInfo.InvariantCulture))
            {
                // The engine and the debugger were built from different sources. Say so:
                // reading the buffer anyway would produce a plausible, wrong stack.
                error = "engine speaks " + parts[0] + ", this debugger speaks v"
                    + DebugWire.FormatVersion;
                return false;
            }

            foreach (string part in parts)
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string key = part.Substring(0, eq);
                string[] pair = part.Substring(eq + 1).Split(',');
                if (pair.Length != 2) continue;

                long address = long.Parse(pair[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                int length = int.Parse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
                if (key == "snapshot") { state.SnapshotAddress = address; state.SnapshotLength = length; }
                else if (key == "commands") { state.CommandAddress = address; state.CommandLength = length; }
            }
            if (state.SnapshotAddress == 0) { error = "no snapshot address"; return false; }
            return true;
        }

        /// <summary>The hidden breakpoint on ShumwayDebugHelper.Notify is what turns a
        /// port into a stop. Planting it is a monitor-side act, so the IDE only supplies
        /// the coordinates: the method's metadata token (read out of the debuggee itself
        /// — no symbol file, no assumption about where the DLL came from) and the address
        /// of the snapshot buffer.</summary>
        private static void ArmNotifyBreakpoint(
            DkmStackContext stackContext, DkmStackWalkFrame frame, ShumwaySessionDataItem state)
        {
            string? tokenText = FuncEval(stackContext, frame,
                "typeof(Shumway.Embedding.Debugging.ShumwayDebugHelper)"
                + ".GetMethod(\"Notify\").MetadataToken", out string? error);
            if (tokenText == null)
            {
                state.Diagnostic = "attached (no notify token: " + error + ")";
                return;
            }

            int token = int.Parse(tokenText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            DkmCustomMessage.Create(
                    frame.Process.Connection, frame.Process,
                    ShumwayGuids.MessageSource, ShumwayGuids.MsgArmNotifyBreakpoint,
                    state.SnapshotAddress, token,
                    // The server needs both ends of the channel: the snapshot to read a
                    // stop, and the command region to answer it.
                    state.CommandAddress.ToString(CultureInfo.InvariantCulture), null)
                .SendLower();
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
                return DebugWire.ReadSnapshot(bytes);
            }
            catch (Exception ex)
            {
                state.Diagnostic = "read: " + ex.Message;
                return null;
            }
        }

        /// <summary>The asynchronous break: the user stopped the process at no port at
        /// all, so ask the engine where it actually is.</summary>
        public static void CaptureNow(DkmStackContext stackContext, DkmStackWalkFrame frame)
        {
            FuncEval(stackContext, frame,
                "Shumway.Embedding.Debugging.ShumwayDebugHelper.CaptureNow()", out _);
        }

        /// <summary>Synchronous func-eval of a C# expression against a CLR frame.</summary>
        private static string? FuncEval(
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
