using System.Runtime.CompilerServices;
using Shumway.Embedding;

#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
#endif

namespace Shumway.Tests.Embedding
{
    /// <summary>In this suite a bundle's compiled code is bound at load, so a
    /// test of persisted IL runs that IL from its first query instead of the
    /// bytecode. The tests of when a predicate takes its code set the
    /// threshold themselves.</summary>
    internal static class BundleCodeAtLoad
    {
        /// <summary>What an engine outside this suite starts with.</summary>
        internal static int ProductDefault { get; private set; }

        // CA2255: the suite's default has to be in place before any engine exists.
#pragma warning disable CA2255
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void Set()
        {
            ProductDefault = IlPromotionStore.DefaultPersistedThreshold;
            IlPromotionStore.DefaultPersistedThreshold = 0;
        }
    }
}
