// Shumway debugger - IDE-side frame decoder + expression evaluator (ADR-035, phase D2).
//
// Routed here by the language id on the frame's module. Two jobs:
//
//   * NAME a frame. The engine already said "append/3"; we say it back.
//
//   * Show the frame's VARIABLES. These are not C# values and cannot be read out of
//     memory by the debugger: a Prolog variable is a heap cell that may be a chain of
//     references ending in an unbound cell, a compound, a partial string. Rendering one
//     is an engine act — it happens inside the debuggee, at the stop, into the snapshot
//     (name and rendered value). By the time the Locals window asks, the answer already
//     exists. We hand it over. That is the whole reason the snapshot carries variables
//     rather than addresses.
//
// Everything here reads only what the engine wrote. No func-eval on the stop path.
// D5's watch-a-goal (evaluating an arbitrary Prolog term) is the one thing that will
// need one, and it is user-initiated — the context where func-eval is supported.

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
            IReadOnlyList<DebugVariableView> variables = VariablesOf(frame);
            var enumContext = DkmEvaluationResultEnumContext.Create(
                variables.Count, frame, inspectionContext,
                new ShumwayEnumDataItem { Variables = variables });
            completionRoutine(new DkmGetFrameLocalsAsyncResult(enumContext));
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
            string text = (expression.Text ?? string.Empty).Trim();

            // v1: a watch names a variable of the frame. Evaluating an arbitrary GOAL is
            // a different thing entirely — it would run Prolog inside the debuggee, with
            // side effects, on a machine stopped mid-resolution — and it is deliberately
            // deferred (ADR-035, D5) rather than half-done here.
            foreach (DebugVariableView v in VariablesOf(stackFrame))
            {
                if (string.Equals(v.Name, text, StringComparison.Ordinal))
                {
                    completionRoutine(new DkmEvaluateExpressionAsyncResult(
                        Success(inspectionContext, stackFrame, v.Name, v.Value)));
                    return;
                }
            }

            completionRoutine(new DkmEvaluateExpressionAsyncResult(
                DkmFailedEvaluationResult.Create(
                    inspectionContext, stackFrame, text, text,
                    "no variable '" + text + "' in this clause",
                    DkmEvaluationResultFlags.Invalid, null)));
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
            // subterm, which is a func-eval, which is a D5 problem — and a leaf answer
            // is honest, where a fabricated child list would not be.
            var empty = DkmEvaluationResultEnumContext.Create(
                0, result.StackFrame, inspectionContext, null);
            completionRoutine(new DkmGetChildrenAsyncResult(new DkmEvaluationResult[0], empty));
        }

        void IDkmLanguageExpressionEvaluator.SetValueAsString(
            DkmEvaluationResult result, string value, int timeout, out string? errorText)
        {
            // Binding a variable from the debugger would have to unify, trail, and
            // possibly wake attributed-variable hooks. Not a setter.
            errorText = "Prolog variables cannot be assigned from the debugger.";
        }

        // ---- shared ----

        /// <summary>The frame's variables, as the ENGINE rendered them at the stop. The
        /// frame carries only its index in the snapshot (see ShumwayFrameId); the values
        /// are read back out of the debuggee's buffer, which is still exactly as it was —
        /// the process is stopped.</summary>
        private static IReadOnlyList<DebugVariableView> VariablesOf(DkmStackWalkFrame frame)
        {
            if (ShumwayFrameId.TryDecode(frame, out int index))
            {
                ShumwaySessionDataItem session = ShumwaySession.GetState(frame.Process);
                DebugSnapshot? snapshot = ShumwaySession.ReadSnapshot(frame.Process, session);
                if (snapshot != null && index < snapshot.Frames.Count)
                    return snapshot.Frames[index].Variables;
            }
            return new List<DebugVariableView>();
        }

        private static DkmEvaluationResult Success(
            DkmInspectionContext inspectionContext, DkmStackWalkFrame frame,
            string name, string value)
        {
            return DkmSuccessEvaluationResult.Create(
                inspectionContext, frame, name, name,
                DkmEvaluationResultFlags.ReadOnly, value,
                EditableValue: null, Type: "term",
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
