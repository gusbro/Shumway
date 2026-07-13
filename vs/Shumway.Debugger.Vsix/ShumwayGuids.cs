// Shumway debugger - VSIX package ids (ADR-035, phase D4).

using System;

namespace Shumway.Debugger.Vsix
{
    internal static class PackageGuids
    {
        public const string PackageString = "CADEB786-9FD5-44C9-9522-E82E357571C7";
        public const string CommandSetString = "C74E33BF-2316-41CF-A971-B3BC83745619";
        public const string OptionsPageString = "A83AE392-C303-48DE-80AE-6E630B2EC99C";

        public static readonly Guid CommandSet = new Guid(CommandSetString);

        /// <summary>The command the user sees: "Debug Prolog File".</summary>
        public const int DebugPrologFileCommandId = 0x0100;

        /// <summary>The CoreCLR debug engine. Shumway is a .NET program: the engine that
        /// launches it is the ordinary managed one, and our Concord components layer on top
        /// of the session it creates. There is no Shumway "debug engine" to name — that is
        /// the whole point of the Concord model, and the reason this file has no engine id
        /// of its own.</summary>
        public static readonly Guid CoreClrEngine = new Guid("2E36F1D4-B23C-435D-AB41-18E608940038");
    }
}
