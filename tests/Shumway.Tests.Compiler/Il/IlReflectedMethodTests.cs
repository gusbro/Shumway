using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Shumway.Compiler.Il;
using Xunit;

namespace Shumway.Tests.Compiler.Il;

/// <summary>
/// The IL compiler resolves the runtime methods it emits calls to by reflection
/// into <c>static readonly MethodInfo</c> fields. A signature change on the
/// other side makes <c>GetMethod(...)!</c> return null, and the null-forgiving
/// operator means nothing complains until the field is used to emit — so a
/// stale lookup for a method no site emits stays invisible until some later
/// change starts emitting it. This asserts every one of them resolves.
/// </summary>
public class IlReflectedMethodTests
{
    [Fact]
    public void EveryReflectedMethodInfoResolves()
    {
        var t = typeof(IlPredicateCompiler);
        RuntimeHelpers.RunClassConstructor(t.TypeHandle);

        var fields = t
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(MethodInfo))
            .ToList();

        Assert.NotEmpty(fields);
        var unresolved = fields.Where(f => f.GetValue(null) is null)
                               .Select(f => f.Name)
                               .ToList();
        Assert.True(unresolved.Count == 0,
            "Unresolved reflected methods: " + string.Join(", ", unresolved));
    }
}
