// Shumway debugger - stepping, taken from the CLR (ADR-035, phase D3).
//
// The Prolog frames belong to a custom runtime, and a step in one of them ought to be
// offered to that runtime's stepper. In a MANAGED debuggee it never is. The physical
// thread is stopped in engine C#; the CLR runtime owns that location and Visual Studio
// does not arbitrate — measured, not guessed: through every smoke run our custom
// stepper's OwnsCurrentExecutionLocation was called exactly zero times, while the
// process visibly resumed and re-broke. F11 was silently doing a C# step, and the stale
// snapshot made the stack look as though nothing had happened.
//
// (PTVS, which does the same thing over CPython, gets asked — because there the competing
// runtime is native. That is the whole difference, and it is not one we can arrange.)
//
// So we take the step where it is actually offered: on the CLR runtime, at AboveNormal
// priority, ahead of the built-in stepper. When the machine is stopped at a Prolog port,
// the step is ours — it goes down the command channel and the engine stops at the port
// that satisfies it. When it is not (a breakpoint in the user's own C#, a foreign
// predicate, native code under a P/Invoke), we decline, and the CLR steps as it always
// did. That is what keeps the stack mixed rather than merely Prolog-shaped.

using System;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.Stepping;

namespace Shumway.Debugger.Concord
{
    public sealed class ShumwayClrStepper : IDkmRuntimeStepper
    {
        /// <summary>Declining is a THROW, not a false: false would tell Visual Studio the CLR
        /// does not own its own execution location, and C# stepping would come apart. A
        /// NotImplementedException means "not mine" — Concord moves on to the next component,
        /// which is the built-in stepper.</summary>
        private static bool Ours(DkmRuntimeInstance runtimeInstance)
        {
            return ShumwayRemoteComponent.State(runtimeInstance.Process).StoppedAtPort;
        }

        bool IDkmRuntimeStepper.OwnsCurrentExecutionLocation(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper, DkmStepArbitrationReason reason)
        {
            ShumwayServerDataItem state = ShumwayRemoteComponent.State(runtimeInstance.Process);
            state.OwnsAsks++;
            if (state.StoppedAtPort)
                return true;   // it is Prolog we are standing in, whatever the CLR thinks
            throw new NotImplementedException();
        }

        void IDkmRuntimeStepper.Step(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper, DkmStepArbitrationReason reason)
        {
            if (!Ours(runtimeInstance))
                throw new NotImplementedException();

            ShumwayRemoteComponent.BeginStep(runtimeInstance.Process, stepper);
            // Nothing else to arrange. VS resumes the process; the engine reads the mode out
            // of the channel and stops at the first port that satisfies it, which comes back
            // to us as a notify — and that is where the step completes.
        }

        void IDkmRuntimeStepper.StopStep(DkmRuntimeInstance runtimeInstance, DkmStepper stepper)
        {
            if (ShumwayRemoteComponent.State(runtimeInstance.Process).Stepper == null)
                throw new NotImplementedException();   // not our step to cancel
            ShumwayRemoteComponent.CancelStep(runtimeInstance.Process);
        }

        void IDkmRuntimeStepper.BeforeEnableNewStepper(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper)
        {
            throw new NotImplementedException();
        }

        void IDkmRuntimeStepper.AfterSteppingArbitration(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper,
            DkmStepArbitrationReason reason, DkmRuntimeInstance newControllingRuntimeInstance)
        {
            throw new NotImplementedException();
        }

        void IDkmRuntimeStepper.OnNewControllingRuntimeInstance(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper,
            DkmStepArbitrationReason reason, DkmRuntimeInstance controllingRuntimeInstance)
        {
            throw new NotImplementedException();
        }

        bool IDkmRuntimeStepper.StepControlRequested(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper,
            DkmStepArbitrationReason reason, DkmRuntimeInstance callingRuntimeInstance)
        {
            throw new NotImplementedException();
        }

        void IDkmRuntimeStepper.TakeStepControl(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper, bool leaveGuardsInPlace,
            DkmStepArbitrationReason reason, DkmRuntimeInstance callingRuntimeInstance)
        {
            throw new NotImplementedException();
        }

        void IDkmRuntimeStepper.NotifyStepComplete(
            DkmRuntimeInstance runtimeInstance, DkmStepper stepper)
        {
            throw new NotImplementedException();
        }
    }
}
