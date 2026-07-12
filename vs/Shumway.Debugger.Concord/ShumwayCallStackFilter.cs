// Shumway debugger - call stack filter (ADR-035, Phase D0 spike shape).
//
// Replaces the physical interpreter frame(s) with synthesized Prolog frames.
// D0 scope: detect frames belonging to the interpreter MODULE and substitute
// annotated placeholder frames, proving the filter + VSIX + vsdconfig chain on
// VS 2026. D2 turns the placeholders into real frames built from the debuggee's
// pinned stop snapshot (DkmCustomInstructionAddress with file/line/predicate).
//
// Contract notes (verified against the samples + PTVS):
// - FilterNextFrame is SYNCHRONOUS; input == null signals end-of-stack.
// - Returning the input frame unchanged = pass-through (mixed-stack behavior:
//   C# interop bridges and native frames flow through untouched).

using System;
using System.Linq;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.CustomRuntimes;

namespace Shumway.Debugger.Concord
{
    public sealed class ShumwayCallStackFilter : IDkmCallStackFilter
    {
        DkmStackWalkFrame[]? IDkmCallStackFilter.FilterNextFrame(
            DkmStackContext stackContext, DkmStackWalkFrame input)
        {
            if (input == null)
                return null; // end of stack walk

            string? moduleName = input.ModuleInstance?.Name;
            bool isInterpreterFrame =
                string.Equals(moduleName, "SpikeDebuggee.dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(moduleName, "Shumway.Interpreter.dll", StringComparison.OrdinalIgnoreCase);

            if (!isInterpreterFrame)
                return new[] { input };

            var data = ShumwayStackDataItem.GetInstance(stackContext);
            data.ReplacedFrames++;

            // D0 legs 1+2: on the first interpreter frame of each walk, run the
            // one-time debuggee probe (func-eval + pinned channel + server handoff)
            // and prepend a diagnostic frame reporting probe/channel state.
            bool firstFrame = data.ReplacedFrames == 1;
            if (firstFrame)
                ShumwayDebuggeeProbe.RunOnce(stackContext, input);

            // Two synthetic frames per replaced interpreter frame: proves frame
            // multiplicity (one physical Dispatch frame -> N logical Prolog frames).
            // Leg 5: once the custom runtime exists, the frames carry OUR runtime's
            // instruction addresses (Offset = pretend source line) so stepping
            // arbitration routes F10/F11 to our IDkmRuntimeStepper.
            bool addressed = false;
            DkmInstructionAddress? MakeAddress(int line)
            {
                var runtime = input.Process.GetRuntimeInstances()
                    .OfType<DkmCustomRuntimeInstance>()
                    .FirstOrDefault(r => r.Id.RuntimeType == ShumwayGuids.RuntimeType);
                var module = runtime?.GetModuleInstances()
                    .OfType<DkmCustomModuleInstance>()
                    .FirstOrDefault();
                if (runtime == null || module == null)
                    return null;
                addressed = true;
                return DkmCustomInstructionAddress.Create(
                    runtime, module, EntityId: null, Offset: (ulong)line,
                    AdditionalData: null, CPUInstruction: null);
            }

            var callee = DkmStackWalkFrame.Create(
                stackContext.Thread,
                MakeAddress(3),
                input.FrameBase,
                0,
                DkmStackWalkFrameFlags.None,
                $"[Prolog] parent/2  (spike, replaced #{data.ReplacedFrames})",
                null,
                null);

            var caller = DkmStackWalkFrame.Create(
                stackContext.Thread,
                MakeAddress(7),
                input.FrameBase,
                0,
                DkmStackWalkFrameFlags.None,
                $"[Prolog] grandparent/2  (spike, replaced #{data.ReplacedFrames})",
                null,
                null);

            if (!firstFrame)
                return new[] { callee, caller };

            var probe = ShumwayDebuggeeProbe.GetState(input.Process);
            string report = probe.ProbeReport
                + " | " + ShumwayDebuggeeProbe.ReadStatus(input.Process)
                + " addr=" + (addressed ? "OURS" : "none");
            var diagnostic = DkmStackWalkFrame.Create(
                stackContext.Thread, null, input.FrameBase, 0,
                DkmStackWalkFrameFlags.None,
                $"[Shumway spike] {report}",
                null, null);
            // The diagnostic frame goes BELOW the Prolog frames on purpose: it is
            // annotated (no instruction address), and an address-less TOP frame
            // makes the stepping manager fall back to the CLR runtime instead of
            // consulting our IDkmRuntimeStepper.
            return new[] { callee, caller, diagnostic };
        }
    }
}
