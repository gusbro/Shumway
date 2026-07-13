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
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Shumway.Debugger.Vsix
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuids.PackageString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [InstalledProductRegistration("#110", "#112", "0.1")]
    // The resource ids are the ones in VSPackage.resx, which is also where the command table
    // is merged. They are not decoration: a page registered against id 0 has no name to show.
    [ProvideOptionPage(typeof(ShumwayOptionsPage), "Shumway", "Prolog Debugger",
        categoryResourceID: 120, pageNameResourceID: 121, supportsAutomation: false)]
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

            var options = (ShumwayOptionsPage)GetDialogPage(typeof(ShumwayOptionsPage));
            string? engine = ShumwayOptionsPage.Resolve(options.ShumwayExePath);
            if (engine == null)
            {
                Complain("Cannot find shumway.exe. Set its path in Tools > Options > Shumway > "
                    + "Prolog Debugger, or put it on PATH / in SHUMWAY_EXE.");
                return;
            }

            try
            {
                Launch(engine, file);
            }
            catch (Exception ex)
            {
                Complain("Could not start the debug session: " + ex.Message);
            }
        }

        /// <summary>Hand the process to the CoreCLR engine. `--debug-wait` is what makes this
        /// a LAUNCH rather than a race: the engine turns on debug codegen, opens its session,
        /// and then waits for a debugger — so the file is consulted, and the goal run, only
        /// once breakpoints can bind.</summary>
        private void Launch(string enginePath, string prologFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var debugger = GetService(typeof(SVsShellDebugger)) as IVsDebugger4;
            if (debugger == null)
                throw new InvalidOperationException("the shell debugger is not available");

            var target = new VsDebugTargetInfo4
            {
                dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess,
                bstrExe = enginePath,
                bstrArg = "--debug-wait \"" + prologFile + "\"",
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
