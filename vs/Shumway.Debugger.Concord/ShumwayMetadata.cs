// Shumway debugger - the notify method's token, read off the disk (ADR-035, phase D4).
//
// The hidden breakpoint goes on ShumwayDebugHost.Notify, and to plant it we need that
// method's metadata token. The engine publishes it -- in the channel file it writes when its
// debug session opens. Which is too late, and the ordering is not a detail:
//
//   Visual Studio raises "Shumway.Core.dll loaded" the instant BEFORE the first use of the
//   assembly, with the debuggee FROZEN. The engine has not run the line that opens its
//   session, so there is no channel file. And we cannot wait for one: the thread that would
//   write it is the thread we are holding.
//
// So we do not ask the process. We read its DLL. The token is in the metadata, it is the
// same token the running process will report, and it is available before the process has
// done anything at all -- which is exactly when we need it.

using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Shumway.Debugger.Concord
{
    internal static class ShumwayMetadata
    {
        private const string HostNamespace = "Shumway.Core.Debugging";
        private const string HostType = "ShumwayDebugHost";
        private const string NotifyMethod = "Notify";

        /// <summary>The metadata token of <c>ShumwayDebugHost.Notify</c> in the assembly at
        /// <paramref name="path"/>, or 0 if this is not an engine assembly (which is the
        /// ordinary case for every other DLL a process loads).</summary>
        public static int FindNotifyToken(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return 0;

            try
            {
                using (var stream = File.OpenRead(path))
                using (var pe = new PEReader(stream))
                {
                    if (!pe.HasMetadata) return 0;
                    MetadataReader metadata = pe.GetMetadataReader();

                    foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
                    {
                        TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
                        if (!metadata.GetString(type.Name).Equals(HostType, StringComparison.Ordinal))
                            continue;
                        if (!metadata.GetString(type.Namespace).Equals(HostNamespace, StringComparison.Ordinal))
                            continue;

                        foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                        {
                            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                            if (metadata.GetString(method.Name).Equals(NotifyMethod, StringComparison.Ordinal))
                                return MetadataTokens.GetToken(metadata, methodHandle);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShumwayLog.Write("metadata: " + path + ": " + ex.Message);
            }
            return 0;
        }
    }
}
