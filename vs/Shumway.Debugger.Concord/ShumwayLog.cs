// Shumway debugger - the components' only voice (ADR-035, phase D4).
//
// A Concord component runs inside the debug monitor. It has no console, no output window,
// and when it fails the only symptom in the IDE is that nothing happens: the breakpoint
// stays hollow, the step does nothing, the stack is empty. Worse, the usual way to ask it
// what went wrong -- the [Shumway diag] frame -- needs a STOP, and the failures that matter
// most are the ones that prevent stops from ever occurring.
//
// So, when SHUMWAY_DEBUG_DIAG=1 is set in the environment Visual Studio was started with,
// the components write a line per event to %TEMP%\shumway-debug\component.log. Off by
// default: a debugger that writes to disk on every step is not one anybody wants.

using System;
using System.Globalization;
using System.IO;
using Microsoft.VisualStudio.Debugger;

namespace Shumway.Debugger.Concord
{
    internal static class ShumwayLog
    {
        private static readonly bool Enabled =
            Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_DIAG") == "1";

        /// <summary>ADR-035 D5+ — a line to the DEBUG OUTPUT WINDOW, ALWAYS (not gated on
        /// the diag file): user-facing feedback for an action VS itself gives none for —
        /// Set Next Statement's result, above all, where the user needs to see the move
        /// took or why it was refused. Best-effort; a debugger that cannot post a message
        /// still debugs. Also mirrored to the diag file when it is on.</summary>
        public static void Output(DkmProcess process, string message)
        {
            Write(message);
            try
            {
                DkmUserMessage.Create(
                    process.Connection, process,
                    DkmUserMessageOutputKind.UnfilteredOutputWindowMessage,
                    "Shumway: " + message + Environment.NewLine,
                    MessageBoxFlags.MB_OK, 0).Post();
            }
            catch (Exception)
            {
                // No Output window here (a headless run, a context that forbids it): the
                // diag file line above is the fallback.
            }
        }

        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            if (!Enabled) return;
            try
            {
                string directory = Path.Combine(Path.GetTempPath(), "shumway-debug");
                Directory.CreateDirectory(directory);
                lock (Gate)
                {
                    File.AppendAllText(
                        Path.Combine(directory, "component.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                            + "  " + message + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // A debugger that cannot write its log still debugs.
            }
        }
    }
}
