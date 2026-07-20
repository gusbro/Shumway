// Shumway debugger - IDE-side frame decoder + expression evaluator (ADR-035, phase D2).
//
// Routed here by the language id on the frame's module. Two jobs:
//
//   * NAME a frame. The engine already said "append/3"; we say it back.
//
//   * Show the frame's VARIABLES. These are not C# values and cannot be read out of
//     memory by the debugger: a Prolog variable is a heap cell that may be a chain of
//     references ending in an unbound cell, a compound, a partial string. Rendering one
//     is an engine act â€” it happens inside the debuggee, at the stop, into the snapshot
//     (name and rendered value). By the time the Locals window asks, the answer already
//     exists. We hand it over. That is the whole reason the snapshot carries variables
//     rather than addresses.
//
// Everything here reads only what the engine wrote. No func-eval on the stop path.
// D5's watch-a-goal (evaluating an arbitrary Prolog term) is the one thing that will
// need one, and it is user-initiated â€” the context where func-eval is supported.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Shumway.Embedding.Debugging;

namespace Shumway.Debugger.Concord
{
    /// <summary>The variables an enumeration is walking (the Locals window asks for a
    /// count first, then for items by range).</summary>
    internal sealed class ShumwayEnumDataItem : DkmDataItem
    {
        public IReadOnlyList<DebugVariableView> Variables = new List<DebugVariableView>();
    }

    public sealed class ShumwayExpressionEvaluator
        : IDkmLanguageFrameDecoder, IDkmLanguageExpressionEvaluator
    {
        // ---- frame names ----

        void IDkmLanguageFrameDecoder.GetFrameName(
            DkmInspectionContext inspectionContext, DkmWorkList workList,
            DkmStackWalkFrame frame, DkmVariableInfoFlags argumentFlags,
            DkmCompletionRoutine<DkmGetFrameNameAsyncResult> completionRoutine)
        {
            ShumwayIdeDiag.FrameNameAsks++;
            completionRoutine(new DkmGetFrameNameAsyncResult(frame.Description ?? "<prolog>"));
        }

        void IDkmLanguageFrameDecoder.GetFrameReturnType(
            DkmInspectionContext inspectionContext, DkmWorkList workList,
            DkmStackWalkFrame frame,
            DkmCompletionRoutine<DkmGetFrameReturnTypeAsyncResult> completionRoutine)
        {
            // A Prolog goal succeeds or fails; it does not return a value.
            completionRoutine(new DkmGetFrameReturnTypeAsyncResult(string.Empty));
        }

        // ---- locals ----

        void IDkmLanguageExpressionEvaluator.GetFrameLocals(
            DkmInspectionContext inspectionContext, DkmWorkList workList,
            DkmStackWalkFrame frame,
            DkmCompletionRoutine<DkmGetFrameLocalsAsyncResult> completionRoutine)
        {
            ShumwayIdeDiag.FrameLocalsAsks++;

            // ADR-035 D5+ — THE SELECTION SIGNAL. Visual Studio refreshes Locals with the
            // frame the user has selected in the Call Stack window, and that frame carries
            // our display index — the one reliable way to know the selection, which the
            // Set Next Statement interfaces never receive (VS pins their frame to the
            // leaf). Tracked here and mirrored to the server component, so a
            // Ctrl+Shift+F10 targets the frame the user is actually standing on.
            try
            {
                if (ShumwayFrameId.TryDecode(frame, out int selectedIndex))
                {
                    ShumwaySessionDataItem sel = ShumwaySession.GetState(frame.Process);
                    if (sel.SelectedFrame != selectedIndex)
                    {
                        sel.SelectedFrame = selectedIndex;
                        DkmCustomMessage.Create(
                                frame.Process.Connection, frame.Process,
                                ShumwayGuids.MessageSource, ShumwayGuids.MsgSelectedFrame,
                                selectedIndex, null, null, null)
                            .SendLower();
                    }
                }
            }
            catch (Exception ex) { ShumwayLog.Write("selected-frame track threw: " + ex.Message); }

            // ADR-035 D5+ — a Set Next Statement queued this stop is applied NOW, so these
            // locals show the POST-MOVE state (a backward rewind unbinds variables; the
            // user must see that without taking a step first). The engine can only be run
            // from a stop by a func-eval carrying Visual Studio's own inspection session —
            // which is exactly what THIS entry holds and the server-side
            // IDkmRuntimeSetNextStatement does not (its self-created session answers a
            // call "not implemented"). So the runtime side queues + patches the arrow, and
            // the Locals refresh VS issues right after is where the move actually lands.
            // Idempotent engine-side (a second apply is a no-op: P already at the target),
            // and memoised per snapshot sequence so one queued move costs one func-eval.
            try
            {
                if (ShumwayFrameId.TryDecode(frame, out _))
                {
                    ShumwaySessionDataItem session = ShumwaySession.GetState(frame.Process);
                    int pending = ReadPendingSetNext(
                        frame.Process, session, out int pendingFrame);
                    if (pending >= 0
                        && !(session.SnsAppliedLine == pending
                             && session.SnsAppliedFrame == pendingFrame
                             && session.SnsAppliedSeq == SnapshotSeq(frame.Process, session))
                        && session.EvalAnchors.TryGetValue(
                               frame.Thread.UniqueId, out DkmStackWalkFrame? clrFrame)
                        && clrFrame != null)
                    {
                        string call = "Shumway.Core.Debugging.ShumwayDebugHost"
                            + ".SetNextStatement(" + pendingFrame + ", " + pending + ")";
                        ShumwaySession.EvaluateCSharpAsync(
                            workList, inspectionContext.InspectionSession, frame.Thread,
                            clrFrame, call, timeoutMs: 10_000,
                            (raw, err) =>
                            {
                                ShumwayLog.Write("SNS apply-at-locals frame " + pendingFrame
                                    + " line " + pending + " -> " + (raw ?? "FAILED: " + err));
                                session.SnsAppliedLine = pending;
                                session.SnsAppliedFrame = pendingFrame;
                                session.SnsAppliedSeq = SnapshotSeq(frame.Process, session);
                                ServeLocals(inspectionContext, frame, completionRoutine);
                            });
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                ShumwayLog.Write("SNS apply-at-locals threw: " + ex.Message);
            }
            ServeLocals(inspectionContext, frame, completionRoutine);
        }

        private static void ServeLocals(
            DkmInspectionContext inspectionContext, DkmStackWalkFrame frame,
            DkmCompletionRoutine<DkmGetFrameLocalsAsyncResult> completionRoutine)
        {
            IReadOnlyList<DebugVariableView> variables = VariablesOf(frame);
            var enumContext = DkmEvaluationResultEnumContext.Create(
                variables.Count, frame, inspectionContext,
                new ShumwayEnumDataItem { Variables = variables });
            completionRoutine(new DkmGetFrameLocalsAsyncResult(enumContext));
        }

        /// <summary>The Set Next Statement command sitting undrained in the engine's
        /// command region, or -1. Read with ReadMemory — the region is the debugger's own
        /// writing, so reading it back is safe anywhere.</summary>
        private static int ReadPendingSetNext(
            DkmProcess process, ShumwaySessionDataItem session, out int targetFrame)
        {
            targetFrame = 0;
            if (session.CommandAddress == 0 || session.CommandLength <= 0) return -1;
            try
            {
                var bytes = new byte[session.CommandLength];
                process.ReadMemory(
                    (ulong)session.CommandAddress, DkmReadMemoryFlags.None, bytes);
                return DebugWire.PendingSetNextLine(bytes, out targetFrame);
            }
            catch (Exception) { return -1; }
        }

        private static int SnapshotSeq(DkmProcess process, ShumwaySessionDataItem session)
        {
            try
            {
                DebugSnapshot? snap = ShumwaySession.ReadSnapshot(
                    process, ShumwaySession.GetState(process));
                return snap?.Sequence ?? -1;
            }
            catch (Exception) { return -1; }
        }

        void IDkmLanguageExpressionEvaluator.GetFrameArguments(
            DkmInspectionContext inspectionContext, DkmWorkList workList,
            DkmStackWalkFrame frame,
            DkmCompletionRoutine<DkmGetFrameArgumentsAsyncResult> completionRoutine)
        {
            // Head arguments are not distinguished from body variables in a clause's
            // frame: they are the same variables, seen from the caller's side.
            completionRoutine(new DkmGetFrameArgumentsAsyncResult(new DkmEvaluationResult[0]));
        }

        void IDkmLanguageExpressionEvaluator.GetItems(
            DkmEvaluationResultEnumContext enumContext, DkmWorkList workList,
            int startIndex, int count,
            DkmCompletionRoutine<DkmEvaluationEnumAsyncResult> completionRoutine)
        {
            ShumwayEnumDataItem? data = enumContext.GetDataItem<ShumwayEnumDataItem>();
            IReadOnlyList<DebugVariableView> variables =
                data?.Variables ?? (IReadOnlyList<DebugVariableView>)new List<DebugVariableView>();

            int end = Math.Min(startIndex + count, variables.Count);
            int n = Math.Max(end - startIndex, 0);
            var results = new DkmEvaluationResult[n];
            for (int i = 0; i < n; i++)
            {
                DebugVariableView v = variables[startIndex + i];
                results[i] = Success(enumContext.InspectionContext, enumContext.StackFrame,
                    v.Name, v.Value);
            }
            completionRoutine(new DkmEvaluationEnumAsyncResult(results));
        }

        // ---- watches ----

        void IDkmLanguageExpressionEvaluator.EvaluateExpression(
            DkmInspectionContext inspectionContext, DkmWorkList workList,
            DkmLanguageExpression expression, DkmStackWalkFrame stackFrame,
            DkmCompletionRoutine<DkmEvaluateExpressionAsyncResult> completionRoutine)
        {
            ShumwayIdeDiag.EvaluateAsks++;
            string text = (expression.Text ?? string.Empty).Trim();

            // A bare variable name answers from the snapshot â€” free, no code runs.
            foreach (DebugVariableView v in VariablesOf(stackFrame))
            {
                if (string.Equals(v.Name, text, StringComparison.Ordinal))
                {
                    completionRoutine(new DkmEvaluateExpressionAsyncResult(
                        Success(inspectionContext, stackFrame, v.Name, v.Value)));
                    return;
                }
            }

            // ANYTHING ELSE IS A GOAL, and a goal RUNS. But only when the user ASKED.
            // An IMPLICIT evaluation -- a DataTip under the mouse, an automatic watch
            // refresh on focus regain -- arrives with NoSideEffects, and honouring it is
            // not optional politeness: hovering an atom in the source would EXECUTE it
            // (hover `main`, run the program), and the harmless-looking case was the
            // user's mystery -- a caret parked on a predicate name made every focus
            // switch re-evaluate the DataTip, run the token as a goal, and spray
            // first-chance existence_errors into the Output. Same answer C# gives for a
            // method call in a DataTip: refuse, with the click-to-evaluate button
            // (CanEvaluateNow) for the user who really means it.
            if ((inspectionContext.EvaluationFlags & DkmEvaluationFlags.NoSideEffects) != 0)
            {
                ShumwayLog.Write("implicit eval refused (NoSideEffects): '" + text + "'");
                completionRoutine(new DkmEvaluateExpressionAsyncResult(
                    DkmFailedEvaluationResult.Create(
                        inspectionContext, stackFrame, text, text,
                        "a Prolog goal runs only when asked explicitly -- "
                        + "use the Immediate window, or click to evaluate",
                        DkmEvaluationResultFlags.SideEffect
                        | DkmEvaluationResultFlags.CanEvaluateNow, null)));
                return;
            }

            // Explicit from here on -- the user typed it into the Immediate window, or
            // pressed the evaluate button: the goal runs in a fresh activation over the
            // live engine, with this frame's variables substituted by their current
            // values, database side effects and all.
            //
            // CHAINED ON THE CALLER'S WORK LIST, and that is not a style choice: a method
            // call is a func-eval, a func-eval resumes the debuggee's thread, and the
            // debugger will not do that while a synchronous component call is in progress.
            // Evaluated inline it answered "not implemented"; completed from a free thread
            // it answered "Error in the application". Scheduled on the dispatcher that
            // called us, it is the composition the API is built for.
            void Fail(string message)
            {
                ShumwayLog.Write("immediate: '" + text + "' -> " + message);
                completionRoutine(new DkmEvaluateExpressionAsyncResult(
                    DkmFailedEvaluationResult.Create(
                        inspectionContext, stackFrame, text, text, message,
                        DkmEvaluationResultFlags.Invalid, null)));
            }

            try
            {
                if (!ShumwayFrameId.TryDecode(stackFrame, out int frameIndex))
                {
                    Fail("no variable '" + text + "' in this clause, "
                        + "and this frame has no evaluation context");
                    return;
                }

                ShumwaySessionDataItem session = ShumwaySession.GetState(stackFrame.Process);
                DkmStackWalkFrame? clrFrame;
                if (!session.EvalAnchors.TryGetValue(stackFrame.Thread.UniqueId, out clrFrame))
                    clrFrame = null;
                if (clrFrame == null)
                {
                    Fail("no CLR frame to evaluate against");
                    return;
                }

                string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
                string call =
                    "Shumway.Core.Debugging.ShumwayDebugHost.EvaluateGoal("
                    + frameIndex + ", \"" + b64 + "\")";

                ShumwaySession.EvaluateCSharpAsync(
                    workList, inspectionContext.InspectionSession, stackFrame.Thread,
                    clrFrame, call, timeoutMs: 60_000,
                    (raw, csError) =>
                    {
                        try
                        {
                            if (raw == null)
                            {
                                Fail("evaluation failed: " + (csError ?? "no result"));
                                return;
                            }
                            string trimmed = raw.Trim();
                            if (trimmed.Length >= 2 && trimmed[0] == '"'
                                && trimmed[trimmed.Length - 1] == '"')
                                trimmed = trimmed.Substring(1, trimmed.Length - 2);
                            string answer = System.Text.Encoding.UTF8.GetString(
                                Convert.FromBase64String(trimmed));
                            completionRoutine(new DkmEvaluateExpressionAsyncResult(
                                Success(inspectionContext, stackFrame, text, answer)));
                        }
                        catch (Exception ex)
                        {
                            Fail("evaluation failed: " + ex.Message);
                        }
                    });
            }
            catch (Exception ex)
            {
                Fail("evaluation failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        string IDkmLanguageExpressionEvaluator.GetUnderlyingString(DkmEvaluationResult result)
        {
            return string.Empty;
        }

        void IDkmLanguageExpressionEvaluator.GetChildren(
            DkmEvaluationResult result, DkmWorkList workList, int initialRequestSize,
            DkmInspectionContext inspectionContext,
            DkmCompletionRoutine<DkmGetChildrenAsyncResult> completionRoutine)
        {
            // A term is rendered whole by the engine, at the stop. Expanding a compound
            // lazily (the [+] in the Locals window) means asking the engine for a
            // subterm, which is a func-eval, which is a D5 problem â€” and a leaf answer
            // is honest, where a fabricated child list would not be.
            var empty = DkmEvaluationResultEnumContext.Create(
                0, result.StackFrame, inspectionContext, null);
            completionRoutine(new DkmGetChildrenAsyncResult(new DkmEvaluationResult[0], empty));
        }

        void IDkmLanguageExpressionEvaluator.SetValueAsString(
            DkmEvaluationResult result, string value, int timeout, out string? errorText)
        {
            // ADR-035 D5+ — editing a variable's value in Locals/Watch is DESTRUCTIVE by
            // design (the user's spec): a free variable binds; a BOUND one has its value
            // REPLACED (the old binding is trailed away, so backtracking — and a Set Next
            // Statement rewind — restores it); and `_` UN-instantiates. The new term may
            // name the frame's other variables (X = f(Y) aliases the real Y). The
            // Immediate window deliberately keeps pure, non-destructive unification —
            // this routes to the engine's SetFrameVariable instead
            // (DebugService.SetFrameVariable: transactional, attvars refused).
            //
            // SetValueAsString is SYNCHRONOUS, and a func-eval will not run while a
            // synchronous component call holds the dispatcher — the same lesson
            // EvaluateExpression learned. So the call gets its OWN work list, executed
            // to completion right here.
            errorText = null;
            try
            {
                if (!ShumwayFrameId.TryDecode(result.StackFrame, out int frameIndex))
                {
                    errorText = "this frame has no evaluation context";
                    return;
                }
                ShumwaySessionDataItem session =
                    ShumwaySession.GetState(result.StackFrame.Process);
                DkmStackWalkFrame? clrFrame;
                if (!session.EvalAnchors.TryGetValue(
                        result.StackFrame.Thread.UniqueId, out clrFrame)
                    || clrFrame == null)
                {
                    errorText = "no CLR frame to evaluate against";
                    return;
                }

                string nameB64 = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(result.Name));
                string termB64 = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(value));
                string call = "Shumway.Core.Debugging.ShumwayDebugHost.SetFrameVariable("
                    + frameIndex + ", \"" + nameB64 + "\", \"" + termB64 + "\")";

                string? answer = null;
                string? csError = null;
                DkmWorkList workList = DkmWorkList.Create(null);
                ShumwaySession.EvaluateCSharpAsync(
                    workList, result.InspectionContext.InspectionSession,
                    result.StackFrame.Thread, clrFrame, call,
                    timeoutMs: Math.Max(timeout, 10_000),
                    (raw, err) =>
                    {
                        try
                        {
                            if (raw == null) { csError = err; return; }
                            string trimmed = raw.Trim();
                            if (trimmed.Length >= 2 && trimmed[0] == '"'
                                && trimmed[trimmed.Length - 1] == '"')
                                trimmed = trimmed.Substring(1, trimmed.Length - 2);
                            answer = System.Text.Encoding.UTF8.GetString(
                                Convert.FromBase64String(trimmed));
                        }
                        catch (Exception ex) { csError = ex.Message; }
                    });
                workList.Execute();

                ShumwayLog.Write("set value: " + result.Name + " := '" + value + "' -> "
                    + (answer == null ? "ERROR " + csError : answer.Length == 0 ? "ok" : answer));
                if (answer == null)
                {
                    errorText = "evaluation failed: " + (csError ?? "no result");
                    return;
                }
                // "" = the edit took; anything else is the engine's refusal, verbatim.
                if (answer.Length != 0)
                    errorText = answer.Replace('\n', ' ');
            }
            catch (Exception ex)
            {
                errorText = "set value failed: " + ex.Message;
            }
        }

        // ---- shared ----

        /// <summary>The frame's variables, as the ENGINE rendered them at the stop. The
        /// frame carries only its index in the snapshot (see ShumwayFrameId); the values
        /// are read back out of the debuggee's buffer, which is still exactly as it was â€”
        /// the process is stopped.</summary>
        private static IReadOnlyList<DebugVariableView> VariablesOf(DkmStackWalkFrame frame)
        {
            if (ShumwayFrameId.TryDecode(frame, out int index))
            {
                ShumwaySessionDataItem session = ShumwaySession.GetState(frame.Process);
                DebugSnapshot? snapshot = ShumwaySession.ReadSnapshot(frame.Process, session);
                if (snapshot != null && index < snapshot.Frames.Count)
                {
                    IReadOnlyList<DebugVariableView> found = snapshot.Frames[index].Variables;
                    ShumwayIdeDiag.LastLocals = "idx=" + index + " n=" + found.Count;
                    return found;
                }
                ShumwayIdeDiag.LastLocals = "idx=" + index + " snapshot="
                    + (snapshot == null ? "null" : snapshot.Frames.Count + "frames");
                return new List<DebugVariableView>();
            }
            ShumwayIdeDiag.LastLocals = "no frame id on "
                + (frame.InstructionAddress == null ? "a frame with no address"
                    : frame.InstructionAddress.GetType().Name);
            return new List<DebugVariableView>();
        }

        private static DkmEvaluationResult Success(
            DkmInspectionContext inspectionContext, DkmStackWalkFrame frame,
            string name, string value)
        {
            // NOT ReadOnly (ADR-035 D5+): editing a value in Locals/Watch routes to
            // SetValueAsString, which UNIFIES the typed term into the suspended frame —
            // the Prolog meaning of assignment. ReadOnly made Visual Studio refuse the
            // edit up front ("Operation not supported") without ever asking us.
            return DkmSuccessEvaluationResult.Create(
                inspectionContext, frame, name, name,
                DkmEvaluationResultFlags.None, value,
                EditableValue: value, Type: "term",
                Category: DkmEvaluationResultCategory.Data,
                Access: DkmEvaluationResultAccessType.None,
                StorageType: DkmEvaluationResultStorageType.None,
                TypeModifierFlags: DkmEvaluationResultTypeModifierFlags.None,
                Address: null,
                CustomUIVisualizers: null,
                ExternalModules: null,
                DataItem: null);
        }
    }
}
