using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The invariant "a lock is a monitor": no field of the engine's
/// assemblies is a System.Threading.Lock, which lets two threads in at once
/// on browser-wasm with threads. The desktop runtime cannot show that, so the
/// type is what is checked.</summary>
public sealed class LocksAreMonitorsTests
{
    [Fact]
    public void NoFieldOfTheEngine_IsASystemThreadingLock()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Queue<Assembly>();
        todo.Enqueue(typeof(PrologEngine).Assembly);
        var offenders = new List<string>();
        int fields = 0;
        bool sawAtomTable = false;
        while (todo.Count > 0)
        {
            Assembly assembly = todo.Dequeue();
            if (!seen.Add(assembly.GetName().Name!)) continue;
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
                if (reference.Name!.StartsWith("Shumway", StringComparison.Ordinal))
                    todo.Enqueue(Assembly.Load(reference));
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
            foreach (Type type in types)
            {
                if (type.FullName == "Shumway.Core.AtomTable") sawAtomTable = true;
                foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    fields++;
                    if (field.FieldType.FullName == "System.Threading.Lock")
                        offenders.Add($"{type.FullName}.{field.Name}");
                }
            }
        }

        // Anti-vacuity: the walk reached the core and saw its fields.
        Assert.True(sawAtomTable, "the walk did not reach Shumway.Core");
        Assert.True(fields > 1000, $"only {fields} fields seen");
        Assert.Empty(offenders);
    }
}
