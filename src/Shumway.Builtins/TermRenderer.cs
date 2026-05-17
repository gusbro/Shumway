using System.Globalization;
using System.IO;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Walks a heap cell and writes its Prolog source representation to a
/// <see cref="TextWriter"/>. Used by <see cref="IOBuiltins"/> for
/// <c>write/1</c> and friends; also handy for debugging from C#.
///
/// <para>Rendering is the canonical form: atoms unquoted (no escaping for
/// special characters yet), integers in base 10, floats in <c>"R"</c> format,
/// compound terms as <c>functor(arg, arg)</c>, and cons-chains as bracketed
/// lists <c>[a, b, c]</c> (or <c>[a, b | T]</c> for partial / improper
/// lists). Unbound variables are rendered as <c>_Gn</c> with their heap
/// index — matching the convention used by <c>TermReader</c> in the
/// embedding layer.</para>
/// </summary>
public static class TermRenderer
{
    public static void Render(Engine engine, Cell cell, TextWriter output)
        => Render(engine, cell, output, TermRenderOptions.Default);

    public static void Render(Engine engine, Cell cell, TextWriter output, TermRenderOptions options)
    {
        int derefAddr = Resolve(engine, ref cell);

        switch (cell.Tag)
        {
            case Tag.Ref:
                output.Write("_G");
                output.Write(derefAddr.ToString(CultureInfo.InvariantCulture));
                break;
            case Tag.Atom:
                WriteAtomName(NameOfAtom(cell.AsAtomId), output, options);
                break;
            case Tag.Int:
                output.Write(cell.AsInt.ToString(CultureInfo.InvariantCulture));
                break;
            case Tag.Float:
            {
                double v = Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex));
                output.Write(v.ToString("R", CultureInfo.InvariantCulture));
                break;
            }
            case Tag.Str:
                RenderCompound(engine, cell, output, options);
                break;
            case Tag.Lis:
                RenderList(engine, cell, output, options);
                break;
            case Tag.Pstr:
                output.Write('"');
                output.Write(engine.AsPstrString(derefAddr));
                output.Write('"');
                break;
            default:
                output.Write('<');
                output.Write(cell.Tag.ToString());
                output.Write('>');
                break;
        }
    }

    private static int Resolve(Engine engine, ref Cell cell)
    {
        if (cell.Tag != Tag.Ref) return -1;
        int addr = engine.Deref(cell.AsHeapIndex);
        cell = engine.GetHeap(addr);
        return addr;
    }

    private static void RenderCompound(Engine engine, Cell strCell, TextWriter output, TermRenderOptions options)
    {
        int functorIdx = strCell.AsHeapIndex;
        Cell functorCell = engine.GetHeap(functorIdx);
        var (atomId, arity) = FunctorTable.Lookup(functorCell.AsFunctorId);
        string name = NameOfAtom(atomId);

        // numbervars(true): '$VAR'(N) renders as letter sequence A, B, ..., Z, A1, B1, ...
        if (options.Numbervars && arity == 1 && name == "$VAR")
        {
            Cell nCell = engine.GetHeap(functorIdx + 1);
            Resolve(engine, ref nCell);
            if (nCell.Tag == Tag.Int)
            {
                long n = nCell.AsInt;
                if (n >= 0)
                {
                    output.Write(NumbervarsName(n));
                    return;
                }
            }
        }

        // Operator-form rendering: consult the lookup table if enabled.
        if (!options.IgnoreOps && options.Operators is not null)
        {
            if (arity == 2 && options.Operators.TryGetInfix(name, out int _, out var _))
            {
                Render(engine, engine.GetHeap(functorIdx + 1), output, options);
                output.Write(' ');
                WriteAtomName(name, output, options);
                output.Write(' ');
                Render(engine, engine.GetHeap(functorIdx + 2), output, options);
                return;
            }
            if (arity == 1 && options.Operators.TryGetPrefix(name, out int _, out var _))
            {
                WriteAtomName(name, output, options);
                output.Write(' ');
                Render(engine, engine.GetHeap(functorIdx + 1), output, options);
                return;
            }
            if (arity == 1 && options.Operators.TryGetPostfix(name, out int _, out var _))
            {
                Render(engine, engine.GetHeap(functorIdx + 1), output, options);
                output.Write(' ');
                WriteAtomName(name, output, options);
                return;
            }
        }

        WriteAtomName(name, output, options);
        if (arity == 0) return;
        output.Write('(');
        for (int i = 0; i < arity; i++)
        {
            if (i > 0) output.Write(", ");
            Render(engine, engine.GetHeap(functorIdx + 1 + i), output, options);
        }
        output.Write(')');
    }

    private static void RenderList(Engine engine, Cell lisCell, TextWriter output, TermRenderOptions options)
    {
        output.Write('[');
        bool first = true;
        Cell cursor = lisCell;
        while (true)
        {
            Resolve(engine, ref cursor);
            if (cursor.Tag != Tag.Lis) break;
            if (!first) output.Write(", ");
            int headIdx = cursor.AsHeapIndex;
            Render(engine, engine.GetHeap(headIdx), output, options);
            cursor = engine.GetHeap(headIdx + 1);
            first = false;
        }
        // Cursor is now whatever the tail dereffed to.
        Resolve(engine, ref cursor);
        if (cursor.Tag == Tag.Atom && cursor.AsAtomId == AtomTable.EmptyListId)
        {
            // Proper list — clean close.
        }
        else
        {
            output.Write(" | ");
            Render(engine, cursor, output, options);
        }
        output.Write(']');
    }

    /// <summary>Writes an atom name with quoting applied when
    /// <paramref name="options"/>.<c>Quoted</c> is set and the name
    /// isn't a plain alphanumeric identifier. The rule is conservative:
    /// any name that starts with a non-letter, contains a non-identifier
    /// character, or is the empty string gets single-quoted.</summary>
    private static void WriteAtomName(string name, TextWriter output, TermRenderOptions options)
    {
        if (!options.Quoted || NeedsNoQuoting(name))
        {
            output.Write(name);
            return;
        }
        output.Write('\'');
        foreach (char c in name)
        {
            if (c == '\'') output.Write("\\'");
            else if (c == '\\') output.Write("\\\\");
            else output.Write(c);
        }
        output.Write('\'');
    }

    /// <summary>A name needs no quoting if it's a non-empty sequence of
    /// alphanumeric / underscore characters starting with a lowercase
    /// letter, OR is one of the bracket atoms <c>[]</c> / <c>{}</c>.</summary>
    private static bool NeedsNoQuoting(string name)
    {
        if (name.Length == 0) return false;
        if (name == "[]" || name == "{}" || name == ",") return true;
        char first = name[0];
        if (!(char.IsLower(first) || first == '_')) return false;
        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }

    /// <summary>Converts <c>'$VAR'(N)</c>'s integer payload into the
    /// ISO-standard alphabetic variable name: 0 → A, 1 → B, …, 25 → Z,
    /// 26 → A1, 27 → B1, etc.</summary>
    private static string NumbervarsName(long n)
    {
        char letter = (char)('A' + n % 26);
        long suffix = n / 26;
        return suffix == 0
            ? letter.ToString()
            : letter.ToString() + suffix.ToString(CultureInfo.InvariantCulture);
    }

    private static string NameOfAtom(int id)
    {
        var atom = AtomTable.GetById(id);
        return atom?.Name ?? $"<atom-{id}>";
    }
}
