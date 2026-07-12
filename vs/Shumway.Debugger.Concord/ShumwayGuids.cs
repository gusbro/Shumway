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

        /// <summary>Custom message codes (IDE → server).</summary>
        public const int MsgArmNotifyBreakpoint = 1; // p1 = channel address (long), p2 = Notify metadata token (int)
    }

    /// <summary>
    /// Spike channel layout (mirrors SpikeDebugHelper's pinned buffer):
    ///   +0  uint  magic 'SHDB' (0x53484442)
    ///   +8  long  tick counter        (debuggee writes)
    ///   +16 byte  command byte        (debugger writes — WriteMemory probe)
    ///   +24 byte  command echo        (debuggee copies +16 here each tick)
    ///   +32 int   notify-bp hit count (SERVER component writes)
    ///   +40 byte  server status       (0=silent, 1=bp armed, 0xE0+stage=error)
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
        public const int OffErrorLen = 60;
        public const int OffErrorText = 64;
        public const int MaxErrorText = 256;

        public const byte StatusArmed = 1;
        public const byte StatusErrorBase = 0xE0;
    }
}
