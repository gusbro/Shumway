// Shumway debugger - IDE-side symbol provider for .pl files (ADR-035, phase D2).
//
// Routed to us by the SymbolProviderId carried in every Shumway DkmModuleId. This is
// what lets Visual Studio navigate a file type it knows nothing about: there is no
// PDB, no sequence points, nothing on disk to read. There doesn't need to be — the
// engine already knows which line each frame is standing on, and the module IS the
// file, so a source position is just (module name, offset).
//
// D3 replaces FindSymbols's line-echo with the engine's real port table (F9 must snap
// to a line that can actually be stopped at, which is not every line).

using System;
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
        /// <summary>Every Shumway module carries this: it is how the Locals window and
        /// the frame decoder know to ask US, and not the C# evaluator, about a frame.</summary>
        DkmCompilerId IDkmSymbolCompilerIdQuery.GetCompilerId(
            DkmInstructionSymbol instruction, DkmInspectionSession inspectionSession)
        {
            ShumwayIdeDiag.CompilerIdAsks++;
            return new DkmCompilerId(ShumwayGuids.ShumwayVendor, ShumwayGuids.ShumwayLanguage);
        }

        /// <summary>One module per consulted .pl, named by its full path — so matching a
        /// document is comparing the module's name with the file the user has open.</summary>
        DkmResolvedDocument[] IDkmSymbolDocumentCollectionQuery.FindDocuments(
            DkmModule module, DkmSourceFileId sourceFileId)
        {
            if (string.Equals(module.Name, sourceFileId.DocumentName, StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    DkmResolvedDocument.Create(
                        module, module.Name, ScriptDocument: null,
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
            symbolLocation = new[]
            {
                DkmSourcePosition.Create(
                    DkmSourceFileId.Create(resolvedDocument.DocumentName, null, null, null),
                    new DkmTextSpan(line, line, 0, 0))
            };
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
            ShumwayIdeDiag.SourcePositionAsks++;
            if (instruction is DkmCustomInstructionSymbol custom && custom.Module != null)
            {
                int line = (int)custom.Offset;
                startOfLine = true;
                return DkmSourcePosition.Create(
                    DkmSourceFileId.Create(custom.Module.Name, null, null, null),
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
