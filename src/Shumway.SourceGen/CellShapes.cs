using System.Linq;
using Microsoft.CodeAnalysis;

namespace Shumway.SourceGen;

/// <summary>How generated code reads and writes a value of a type straight
/// from and to the heap (RegisterMarshalling's cell readers and writers),
/// for the shapes that have a direct form: the scalars, string, a list or an
/// array of those, and a <c>[PrologTerm]</c> type of the same compilation
/// (one from another assembly may predate its cell methods). A type with no
/// direct form is null: the code converts it through a Term tree.</summary>
internal static class CellShapes
{
    private const string Rm = "global::Shumway.Embedding.RegisterMarshalling";
    private const string PrologTermAttribute = "Shumway.Embedding.PrologTermAttribute";

    /// <summary>A direct form. <see cref="Read"/> is a call with the
    /// placeholders <c>{cell}</c> (the cell expression) and <c>{out}</c> (the
    /// out declaration, of type <see cref="ReadType"/>); <see cref="Write"/> an
    /// expression with <c>{value}</c>. Both run where <c>engine</c> and
    /// <c>host</c> are in scope.</summary>
    internal sealed record Shape(string ReadType, string Read, string Write);

    public static Shape? Of(ITypeSymbol type, Compilation compilation)
    {
        if (Element(type, compilation) is { } e)
            return new Shape(e.TypeName,
                e.Reader + "(engine, host, {cell}, {out})",
                e.Writer + "(engine, host, {value})");

        if (type is IArrayTypeSymbol { Rank: 1 } array
            && Element(array.ElementType, compilation) is { } ae)
            return new Shape(ae.TypeName + "[]",
                Rm + ".ReadArray<" + ae.TypeName + ">(engine, host, {cell}, " + ae.Reader + ", {out})",
                Rm + ".ListCell<" + ae.TypeName + ">(engine, host, {value}, " + ae.Writer + ")");

        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named
            && named.ConstructedFrom.ToDisplayString() is
                "System.Collections.Generic.List<T>"
                or "System.Collections.Generic.IList<T>"
                or "System.Collections.Generic.IEnumerable<T>"
                or "System.Collections.Generic.IReadOnlyList<T>"
                or "System.Collections.Generic.ICollection<T>"
                or "System.Collections.Generic.IReadOnlyCollection<T>"
            && Element(named.TypeArguments[0], compilation) is { } le)
            return new Shape("global::System.Collections.Generic.List<" + le.TypeName + ">",
                Rm + ".ReadList<" + le.TypeName + ">(engine, host, {cell}, " + le.Reader + ", {out})",
                Rm + ".ListCell<" + le.TypeName + ">(engine, host, {value}, " + le.Writer + ")");

        return null;
    }

    /// <summary>The read call of a type through a Term tree, for a member
    /// with no direct form: it returns the converters' value or raises
    /// their error.</summary>
    public static Shape ViaTerm(string typeName) => new(typeName,
        Rm + ".ReadViaTerm<" + typeName + ">(engine, host, {cell}, {out})",
        Rm + ".CellViaTerm<" + typeName + ">(engine, host, {value})");

    private sealed record ElementForm(string TypeName, string Reader, string Writer);

    private static ElementForm? Element(ITypeSymbol type, Compilation compilation)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Int64: return new("long", Rm + ".ReadLong", Rm + ".LongCell");
            case SpecialType.System_Int32: return new("int", Rm + ".ReadInt", Rm + ".IntCell");
            case SpecialType.System_Double: return new("double", Rm + ".ReadDouble", Rm + ".DoubleCell");
            case SpecialType.System_Boolean: return new("bool", Rm + ".ReadBool", Rm + ".BoolCell");
            case SpecialType.System_String: return new("string", Rm + ".ReadString", Rm + ".StringCell");
        }
        if (type is INamedTypeSymbol named
            && !named.IsGenericType
            && SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, compilation.Assembly)
            && named.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PrologTermAttribute))
        {
            string name = named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return new(name, name + ".TryFromPrologCell", Rm + ".PrologTermCell<" + name + ">");
        }
        return null;
    }

    /// <summary><paramref name="template"/> with its placeholders filled.</summary>
    public static string Fill(string template, string? cell = null, string? @out = null, string? value = null)
    {
        string s = template;
        if (cell is not null) s = s.Replace("{cell}", cell);
        if (@out is not null) s = s.Replace("{out}", @out);
        if (value is not null) s = s.Replace("{value}", value);
        return s;
    }
}
