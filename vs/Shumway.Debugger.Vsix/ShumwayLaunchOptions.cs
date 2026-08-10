// Shumway debugger - engine/argument resolution for the launch command.
// The configured values come from Unified Settings (ShumwaySettingDefinitions);
// these helpers add the environment/PATH fallbacks so a script (the E2E smoke)
// or a team .env can drive a launch without touching the settings UI.

using System;
using System.IO;

namespace Shumway.Debugger.Vsix
{
    internal static class ShumwayLaunchOptions
    {
        /// <summary>The extra arguments, taking the environment as a fallback — the same
        /// setting-then-environment order as the engine path. It is what lets a script (the
        /// E2E smoke) drive a launch that needs interop DLLs without clicking through a
        /// dialog, and what lets a team put the flags in a shared .env-style setup rather
        /// than in each developer's settings.</summary>
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
