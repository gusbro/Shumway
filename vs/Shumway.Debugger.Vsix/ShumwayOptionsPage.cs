// Shumway debugger - Tools > Options page (ADR-035, phase D4).

using System;
using System.ComponentModel;
using System.IO;
using Microsoft.VisualStudio.Shell;

namespace Shumway.Debugger.Vsix
{
    /// <summary>Where the Prolog engine lives. Nothing else about the debug session is
    /// configurable — the rest is decided by the program being debugged.</summary>
    public sealed class ShumwayOptionsPage : DialogPage
    {
        [Category("Shumway")]
        [DisplayName("Path to shumway.exe")]
        [Description("The Shumway REPL executable used to run a .pl file under the debugger. "
            + "Leave empty to take it from the SHUMWAY_EXE environment variable, or from PATH.")]
        public string ShumwayExePath { get; set; } = "";

        /// <summary>The engine to launch, or null if we cannot find one. Explicit setting
        /// first, then the environment, then PATH: the same order a shell would use, so a
        /// developer who already has shumway on PATH does not have to say so twice.</summary>
        public static string? Resolve(string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            string? fromEnvironment = Environment.GetEnvironmentVariable("SHUMWAY_EXE");
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
                return fromEnvironment;

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (path != null)
            {
                foreach (string directory in path.Split(Path.PathSeparator))
                {
                    if (directory.Length == 0) continue;
                    string candidate;
                    try { candidate = Path.Combine(directory.Trim(), "shumway.exe"); }
                    catch (ArgumentException) { continue; }   // a malformed PATH entry
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }
    }
}
