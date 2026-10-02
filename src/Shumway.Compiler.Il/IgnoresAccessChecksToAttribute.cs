namespace System.Runtime.CompilerServices;

/// <summary>Lets a dynamic assembly reach the named assembly's non-public
/// members; the runtime recognizes the attribute by name. ADR-061's
/// continuation methods carry it for the engine's assemblies.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
internal sealed class IgnoresAccessChecksToAttribute : Attribute
{
    public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;

    public string AssemblyName { get; }
}
