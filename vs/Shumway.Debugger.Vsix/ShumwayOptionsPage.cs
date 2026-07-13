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

        /// <summary>Without this, a program with INTEROP cannot be launched at all: its
        /// foreign assembly and its native library have to be named on the command line, and
        /// nothing about the .pl says where they are.</summary>
        [Category("Shumway")]
        [DisplayName("Additional arguments")]
        [Description("Passed to shumway.exe before the file, e.g. "
            + "--foreign-dll C:\\path\\MyForeigns.dll --native-dll C:\\path\\native.dll. "
            + "Leave empty to take them from the SHUMWAY_ARGS environment variable.")]
        public string ExtraArguments { get; set; } = "";

        /// <summary>The extra arguments, taking the environment as a fallback — the same
        /// setting-then-environment order as the engine path. It is what lets a script (the
        /// E2E smoke) drive a launch that needs interop DLLs without clicking through a
        /// dialog, and what lets a team put the flags in a shared .env-style setup rather
        /// than in each developer's options.</summary>
        public static string ResolveArguments(string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return configured!.Trim();

            string? fromEnvironment = Environment.GetEnvironmentVariable("SHUMWAY_ARGS");
            return string.IsNullOrWhiteSpace(fromEnvironment) ? "" : fromEnvironment!.Trim();
        }

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
