// Shumway debugger - the VS package: "Debug Prolog File" (ADR-035, phase D4).
//
// Attach-by-hand was enough to build the thing; it is not enough to USE it. This is the
// one command that turns "open a .pl and press a key" into a debug session: it launches
// shumway.exe on the file with --debug-wait (the engine opens its session, then holds at
// the door until a debugger is actually attached, so no goal has run before the first
// breakpoint could bind) and hands the process to the ORDINARY CoreCLR debug engine.
//
// There is no Shumway debug engine to name. Our Concord components layer onto the managed
// session the CLR engine creates — that is the whole Concord model, and it is why this
// command is thirty lines rather than a debug engine implementation.

using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Extensibility;            // ExtensibilityPoints.Settings()
using Microsoft.VisualStudio.Extensibility.Settings;   // SettingValue.ValueOrDefault
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Shumway.Debugger.Vsix
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuids.PackageString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [InstalledProductRegistration("#110", "#112", "0.1")]
    // The settings (engine path, extra arguments) live in Unified Settings —
    // contributed by ShumwaySettingDefinitions on the in-proc
    // VisualStudio.Extensibility part, read here through the
    // VisualStudioExtensibility service. The old DialogPage is gone: VS 2026's
    // settings UI only shows contributions, and this extension requires 2026.
    // Scripted launches (the E2E smoke) keep working settings-free through the
    // SHUMWAY_EXE / SHUMWAY_ARGS environment fallbacks in ShumwayLaunchOptions.
    // Loaded when a solution is open OR not: a .pl file is usually opened on its own, and a
    // command that only appears once you have created a solution for it is a command nobody
    // finds.
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class ShumwayDebuggerPackage : AsyncPackage
    {
        protected override async Task InitializeAsync(
            CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var commandService = await GetServiceAsync(typeof(IMenuCommandService))
                as OleMenuCommandService;
            if (commandService == null)
                return;

            var id = new CommandID(PackageGuids.CommandSet, PackageGuids.DebugPrologFileCommandId);
            var command = new OleMenuCommand(Execute, id);
            command.BeforeQueryStatus += OnlyForPrologFiles;
            commandService.AddCommand(command);
        }

        /// <summary>The command exists for .pl files and says so by disappearing everywhere
        /// else — a greyed-out item on every C# file would be noise.</summary>
        private void OnlyForPrologFiles(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is not OleMenuCommand command) return;
            string? file = ActiveDocument();
            bool prolog = file != null
                && string.Equals(Path.GetExtension(file), ".pl", StringComparison.OrdinalIgnoreCase);
            command.Visible = prolog;
            command.Enabled = prolog;
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string? file = ActiveDocument();
            if (file == null)
            {
                Complain("Open a .pl file first.");
                return;
            }

            var (configuredEngine, configuredArguments) = ReadConfiguredSettings(out string? readError);
            string? engine = ShumwayLaunchOptions.Resolve(configuredEngine);
            if (engine == null)
            {
                Complain("Cannot find shumway.exe. Set its path in Settings > Shumway "
                    + "Prolog Debugger, or put it on PATH / in SHUMWAY_EXE."
                    + (readError is null ? "" : "\n\nSettings read failed: " + readError));
                return;
            }

            try
            {
                Launch(engine, file, ShumwayLaunchOptions.ResolveArguments(configuredArguments));
            }
            catch (Exception ex)
            {
                Complain("Could not start the debug session: " + ex.Message);
            }
        }

        /// <summary>The two configured values from Unified Settings. The typed API is
        /// tried first, but in this VS 2026 build the VisualStudioExtensibility service
        /// is NOT proffered to VSSDK packages (ServiceUnavailableException) — the
        /// documented query-via-service-provider path is 17.9-era. The working route is
        /// the settings STORE itself: the instance's settings.json (VS documents it as
        /// the human-readable store), located via VSSPROPID_LocalAppDataDir. A failure of
        /// both is REPORTED (<paramref name="readError"/>), not swallowed — a silent
        /// catch here once hid a real read-path break behind a misleading
        /// "cannot find shumway.exe".</summary>
        private (string EnginePath, string ExtraArguments) ReadConfiguredSettings(out string? readError)
        {
            ThreadHelper.ThrowIfNotOnUIThread();   // the store fallback queries IVsShell
#pragma warning disable VSEXTPREVIEW_SETTINGS // the settings API ships as preview
            try
            {
                readError = null;
                return ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    var extensibility = await this.GetServiceAsync<
                        Microsoft.VisualStudio.Extensibility.VisualStudioExtensibility,
                        Microsoft.VisualStudio.Extensibility.VisualStudioExtensibility>();
                    var enginePath = await extensibility.Settings().ReadEffectiveValueAsync(
                        ShumwaySettingDefinitions.EnginePath, DisposalToken);
                    var extraArguments = await extensibility.Settings().ReadEffectiveValueAsync(
                        ShumwaySettingDefinitions.ExtraArguments, DisposalToken);
                    return (enginePath.ValueOrDefault(""), extraArguments.ValueOrDefault(""));
                });
            }
            catch (Exception apiFailure)
            {
                try
                {
                    return ReadFromSettingsStoreFile(out readError);
                }
                catch (Exception storeFailure)
                {
                    readError = "API: " + apiFailure.Message
                        + " / store: " + storeFailure.GetType().Name + ": " + storeFailure.Message;
                    return ("", "");
                }
            }
#pragma warning restore VSEXTPREVIEW_SETTINGS
        }

        /// <summary>Reads the two settings straight from the instance's Unified Settings
        /// store (settings.json under VSSPROPID_LocalAppDataDir). Only OUR two flat
        /// string keys are extracted, so a targeted match + JSON string unescape is
        /// enough — no JSON library dependency. A missing file or key is simply the
        /// default (empty), matching what the settings UI shows.</summary>
        private (string EnginePath, string ExtraArguments) ReadFromSettingsStoreFile(out string? readError)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            readError = null;
            var shell = GetService(typeof(SVsShell)) as IVsShell;
            if (shell == null ||
                shell.GetProperty((int)__VSSPROPID4.VSSPROPID_LocalAppDataDir, out object dirObj) != VSConstants.S_OK
                || dirObj is not string localAppDataDir)
            {
                readError = "cannot resolve the instance's local settings directory";
                return ("", "");
            }
            string storePath = Path.Combine(localAppDataDir, "settings.json");
            if (!File.Exists(storePath))
                return ("", "");   // untouched store: defaults
            string json = File.ReadAllText(storePath);
            return (ExtractJsonStringValue(json, "shumway.enginePath"),
                    ExtractJsonStringValue(json, "shumway.extraArguments"));
        }

        private static string ExtractJsonStringValue(string json, string key)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                json,
                "\"" + System.Text.RegularExpressions.Regex.Escape(key)
                     + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return match.Success ? UnescapeJsonString(match.Groups[1].Value) : "";
        }

        private static string UnescapeJsonString(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char e = s[++i];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u' when i + 4 < s.Length
                        && ushort.TryParse(s.Substring(i + 1, 4),
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out ushort code):
                        sb.Append((char)code); i += 4; break;
                    default: sb.Append('\\').Append(e); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Hand the process to the CoreCLR engine. `--debug-wait` is what makes this
        /// a LAUNCH rather than a race: the engine turns on debug codegen, opens its session,
        /// and then waits for a debugger — so the file is consulted, and the goal run, only
        /// once breakpoints can bind.</summary>
        private void Launch(string enginePath, string prologFile, string? extraArguments)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var debugger = GetService(typeof(SVsShellDebugger)) as IVsDebugger4;
            if (debugger == null)
                throw new InvalidOperationException("the shell debugger is not available");

            string extra = string.IsNullOrWhiteSpace(extraArguments)
                ? ""
                : extraArguments!.Trim() + " ";

            var target = new VsDebugTargetInfo4
            {
                dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
                bstrExe = enginePath,
                // Extra arguments FIRST: --foreign-dll and --native-dll must be registered
                // before the file that uses them is consulted.
                bstrArg = "--debug-wait " + extra + "\"" + prologFile + "\"",
                bstrCurDir = Path.GetDirectoryName(prologFile),
                guidLaunchDebugEngine = PackageGuids.CoreClrEngine,
                LaunchFlags = (uint)__VSDBGLAUNCHFLAGS.DBGLAUNCH_StopDebuggingOnEnd,
            };

            var results = new VsDebugTargetProcessInfo[1];
            debugger.LaunchDebugTargets4(1, new[] { target }, results);
        }

        private string? ActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            try
            {
                return dte?.ActiveDocument?.FullName;
            }
            catch (Exception)
            {
                // ActiveDocument throws rather than returning null when there is no document.
                return null;
            }
        }

        private void Complain(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(
                this, message, "Shumway Prolog Debugger",
                OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}
