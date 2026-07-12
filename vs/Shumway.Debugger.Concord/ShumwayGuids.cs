// Well-known ids shared by the Shumway Concord components (ADR-035).

using System;

namespace Shumway.Debugger.Concord
{
    internal static class ShumwayGuids
    {
        /// <summary>SourceId of IDE→server DkmCustomMessages (vsdconfig filter on
        /// the server's IDkmCustomMessageForwardReceiver).</summary>
        public static readonly Guid MessageSource = new Guid("d451a420-0546-456d-911d-411363b9e55a");

        /// <summary>SourceId stamped on the hidden CLR breakpoint planted on
        /// ShumwayDebugHelper.Notify (vsdconfig filter on IDkmRuntimeBreakpointReceived).</summary>
        public static readonly Guid NotifyBreakpointSource = new Guid("a21369e2-7531-4158-953d-5f95790402cf");

        /// <summary>C# language / Microsoft vendor ids — used to route func-eval
        /// through the Roslyn C# expression evaluator.</summary>
        public static readonly Guid CSharpLanguage = new Guid("3f5162f8-07c6-11d3-9053-00c04fa302a1");
        public static readonly Guid MicrosoftVendor = new Guid("994b45c4-e6e9-11d2-903f-00c04fa302a1");

        /// <summary>The Shumway custom runtime — synthesized frames, .pl breakpoints
        /// and steppers are routed to our components by this RuntimeId.</summary>
        public static readonly Guid RuntimeType = new Guid("3cd5801d-96c7-47d4-b4c4-a796b8fe7de9");

        /// <summary>Symbol provider id — DkmModuleId(mvid, THIS) routes
        /// IDkmSymbol*Query for .pl modules to our IDE component.</summary>
        public static readonly Guid SymbolProvider = new Guid("3aa934bc-02d7-4855-a05b-68a4fdb50918");

        /// <summary>Shumway Prolog language / vendor ids (DkmCompilerId of our
        /// DkmModules; D2 also filters the EE on the language).</summary>
        public static readonly Guid ShumwayLanguage = new Guid("9d7f8a51-0699-4828-99ba-047821c60783");
        public static readonly Guid ShumwayVendor = new Guid("026ae3f6-168e-448c-9518-4ac5d49ce8d1");

        /// <summary>Mvid of the single spike .pl module.</summary>
        public static readonly Guid SpikeModuleMvid = new Guid("c8221ed8-e029-4f0d-9b0d-56fd127d449e");

        /// <summary>Custom message codes (IDE → server).</summary>
        public const int MsgArmNotifyBreakpoint = 1; // p1 = channel address (long), p2 = Notify metadata token (int)
        public const int MsgCreateRuntime = 2;       // p1 = full path of the consulted .pl (string)
    }

    /// <summary>
    /// Spike channel layout (mirrors SpikeDebugHelper's pinned buffer):
    ///   +0  uint  magic 'SHDB' (0x53484442)
    ///   +8  long  tick counter        (debuggee writes)
    ///   +16 byte  command byte        (debugger writes — WriteMemory probe)
    ///   +24 byte  command echo        (debuggee copies +16 here each tick)
    ///   +32 int   notify-bp hit count (SERVER component writes)
    ///   +40 byte  server status       (0=silent, 1=bp armed, 0xE0+stage=error)
    ///   +44 int   .pl F9 breakpoint line (leg 3: EnableRuntimeBreakpoint writes)
    ///   +48 byte  .pl F9 flag (1 = EnableRuntimeBreakpoint reached us)
    ///   +49 byte  step flag  (1 = IDkmRuntimeStepper.Step reached us, leg 5)
    ///   +60 int   server error text length
    ///   +64 ...   server error text (utf8, max 256)
    /// </summary>
    internal static class Channel
    {
        public const uint Magic = 0x53484442;
        public const int OffTicks = 8;
        public const int OffCommand = 16;
        public const int OffEcho = 24;
        public const int OffHits = 32;
        public const int OffServerStatus = 40;
        public const int OffF9Line = 44;
        public const int OffF9Flag = 48;
        public const int OffStepFlag = 49;
        public const int OffErrorLen = 60;
        public const int OffErrorText = 64;
        public const int MaxErrorText = 256;

        public const byte StatusArmed = 1;
        public const byte StatusRuntimeReady = 2;
        public const byte StatusErrorBase = 0xE0;
    }
}
