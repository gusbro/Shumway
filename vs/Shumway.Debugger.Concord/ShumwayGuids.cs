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

        /// <summary>Custom message codes (IDE → server).</summary>
        public const int MsgArmNotifyBreakpoint = 1; // p1 = snapshot address (long), p2 = Notify metadata token (int)
        public const int MsgEnsureModules = 2;       // p1 = '|'-joined full paths of consulted .pl files

        /// <summary>Ask the server what it has managed to do. The monitor side is otherwise
        /// invisible from the IDE — it has no output of its own, and when module creation or
        /// breakpoint arming fails there, the only symptom is that nothing happens. Replies
        /// with a status string in Parameter1.</summary>
        public const int MsgServerStatus = 3;

        /// <summary>A .pl file has no mvid of its own, and VS needs one to tell two
        /// modules apart. Derive it from the path: same file, same id, every session —
        /// which is exactly the property a breakpoint needs to survive a restart.</summary>
        public static Guid ModuleIdFor(string path)
        {
            byte[] bytes;
            using (var md5 = System.Security.Cryptography.MD5.Create())
                bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
            return new Guid(bytes);
        }
    }
}
