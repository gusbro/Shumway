// Shumway debugger - IDE-side symbol provider (ADR-035, D0 spike leg 3).
//
// Routed to us by the SymbolProviderId in every Shumway DkmModuleId. Spike
// scope: one known .pl document; F9 on any line resolves to a
// DkmCustomInstructionSymbol whose Offset encodes the LINE, and
// GetSourcePosition maps it back — enough to prove VS binds breakpoints in a
// file type it doesn't know, through our provider, down to the runtime
// breakpoint handler. D2/D3 replace the line-echo with the engine's real
// clause/port tables (snap to callable ports, per-goal spans).

using System;
using System.Collections.ObjectModel;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.CustomRuntimes;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Microsoft.VisualStudio.Debugger.Symbols;

namespace Shumway.Debugger.Concord
{
    public sealed class ShumwayLocalSymbols
        : IDkmSymbolDocumentCollectionQuery, IDkmSymbolDocumentSpanQuery, IDkmSymbolQuery,
          IDkmSymbolCompilerIdQuery
    {
        DkmCompilerId IDkmSymbolCompilerIdQuery.GetCompilerId(
            DkmInstructionSymbol instruction, DkmInspectionSession inspectionSession)
        {
            return new DkmCompilerId(ShumwayGuids.ShumwayVendor, ShumwayGuids.ShumwayLanguage);
        }

        /// <summary>Spike: the consulted .pl path, published by the probe. (The
        /// real component keys per-process consulted-file tables off DkmProcess
        /// data items fed by the channel.)</summary>
        internal static string? KnownScriptPath;

        DkmResolvedDocument[] IDkmSymbolDocumentCollectionQuery.FindDocuments(
            DkmModule module, DkmSourceFileId sourceFileId)
        {
            string? known = KnownScriptPath;
            if (known != null
                && string.Equals(sourceFileId.DocumentName, known, StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    DkmResolvedDocument.Create(
                        module, known, ScriptDocument: null,
                        MatchStrength: DkmDocumentMatchStrength.FullPath,
                        Warning: DkmResolvedDocumentWarning.None,
                        TextRequested: false, DataItem: null)
                };
            }
            throw new NotImplementedException(); // not ours — let other providers try
        }

        DkmInstructionSymbol[] IDkmSymbolDocumentSpanQuery.FindSymbols(
            DkmResolvedDocument resolvedDocument, DkmTextSpan textSpan, string text,
            out DkmSourcePosition[] symbolLocation)
        {
            int line = textSpan.StartLine;
            var position = DkmSourcePosition.Create(
                DkmSourceFileId.Create(resolvedDocument.DocumentName, null, null, null),
                new DkmTextSpan(line, line, 0, 0));
            symbolLocation = new[] { position };
            return new DkmInstructionSymbol[]
            {
                DkmCustomInstructionSymbol.Create(
                    resolvedDocument.Module, ShumwayGuids.RuntimeType,
                    EntityId: null, Offset: (ulong)line, AdditionalData: null)
            };
        }

        DkmSourcePosition IDkmSymbolQuery.GetSourcePosition(
            DkmInstructionSymbol instruction, DkmSourcePositionFlags flags,
            DkmInspectionSession inspectionSession, out bool startOfLine)
        {
            if (instruction is DkmCustomInstructionSymbol custom && KnownScriptPath != null)
            {
                int line = (int)custom.Offset;
                startOfLine = true;
                return DkmSourcePosition.Create(
                    DkmSourceFileId.Create(KnownScriptPath, null, null, null),
                    new DkmTextSpan(line, line, 0, 0));
            }
            throw new NotImplementedException();
        }

        object IDkmSymbolQuery.GetSymbolInterface(DkmModule module, Guid interfaceID)
        {
            throw new NotImplementedException();
        }
    }
}
