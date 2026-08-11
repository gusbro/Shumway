// Shumway debugger - the in-proc VisualStudio.Extensibility part.
//
// Exists for ONE reason: Unified Settings (the VS 2026 settings UI) has no
// DialogPage story — settings appear there only as VisualStudio.Extensibility
// CONTRIBUTIONS. This class is the required extension entry point; the actual
// setting definitions live in ShumwaySettingDefinitions. Everything else the
// VSIX does (the command, Concord, the grammar) stays on the classic package.

#pragma warning disable VSEXTPREVIEW_SETTINGS // the settings API ships as preview

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Settings;

namespace Shumway.Debugger.Vsix
{
    [VisualStudioContribution]
    internal sealed class ShumwayExtension : Extension
    {
        public override ExtensionConfiguration? ExtensionConfiguration => new()
        {
            RequiresInProcessHosting = true,
        };
    }

    /// <summary>Where the Prolog engine lives — the two knobs the launch command
    /// needs. Nothing else about a debug session is configurable; the rest is
    /// decided by the program being debugged.</summary>
    internal static class ShumwaySettingDefinitions
    {
        [VisualStudioContribution]
        internal static SettingCategory ShumwayCategory { get; } = new("shumway", "Shumway Prolog Debugger")
        {
            Description = "Launching a .pl file under the Shumway Prolog debugger.",
        };

        // FormattedString + FilePath so the settings editor treats the value as
        // a file path (a bare string field is what let a hand-typed
        // "shuwmway.exe" resolve to nothing). TRAP: the 17.14 SDK's generator
        // emits the PRE-VS2026 schema token `"format": "filePath"`, which the
        // VS 18 settings UI does not recognize — it silently DROPS the whole
        // property from the page. VS 18's own registrations (the Terminal's
        // shell path) use `"format": "path", "pathKind": "file"`, so the
        // csproj's ShumwayPatchSettingsRegistrationFormat target rewrites the
        // generated JSON to that schema. Delete the target when an 18.x
        // Extensibility SDK emits it natively.
        [VisualStudioContribution]
        internal static Setting.FormattedString EnginePath { get; } = new(
            "enginePath", "Path to shumway.exe", ShumwayCategory,
            SettingStringFormat.FilePath, defaultValue: "")
        {
            Description = "The Shumway REPL executable used to run a .pl file under the "
                + "debugger. Leave empty to take it from the SHUMWAY_EXE environment "
                + "variable, or from PATH.",
        };

        /// <summary>Without this, a program with INTEROP cannot be launched at all: its
        /// foreign assembly and its native library have to be named on the command line,
        /// and nothing about the .pl says where they are.</summary>
        [VisualStudioContribution]
        internal static Setting.String ExtraArguments { get; } = new(
            "extraArguments", "Additional arguments", ShumwayCategory, defaultValue: "")
        {
            Description = "Passed to shumway.exe before the file, e.g. "
                + "--foreign-dll C:\\path\\MyForeigns.dll --native-dll C:\\path\\native.dll. "
                + "Leave empty to take them from the SHUMWAY_ARGS environment variable.",
        };
    }
}
