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
    {
        int derefAddr = Resolve(engine, ref cell);

        switch (cell.Tag)
        {
            case Tag.Ref:
                output.Write("_G");
                output.Write(derefAddr.ToString(CultureInfo.InvariantCulture));
                break;
            case Tag.Atom:
                output.Write(NameOfAtom(cell.AsAtomId));
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
                RenderCompound(engine, cell, output);
                break;
            case Tag.Lis:
                RenderList(engine, cell, output);
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

    private static void RenderCompound(Engine engine, Cell strCell, TextWriter output)
    {
        int functorIdx = strCell.AsHeapIndex;
        Cell functorCell = engine.GetHeap(functorIdx);
        var (atomId, arity) = FunctorTable.Lookup(functorCell.AsFunctorId);
        output.Write(NameOfAtom(atomId));
        if (arity == 0) return;
        output.Write('(');
        for (int i = 0; i < arity; i++)
        {
            if (i > 0) output.Write(", ");
            Render(engine, engine.GetHeap(functorIdx + 1 + i), output);
        }
        output.Write(')');
    }

    private static void RenderList(Engine engine, Cell lisCell, TextWriter output)
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
            Render(engine, engine.GetHeap(headIdx), output);
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
            Render(engine, cursor, output);
        }
        output.Write(']');
    }

    private static string NameOfAtom(int id)
    {
        var atom = AtomTable.GetById(id);
        return atom?.Name ?? $"<atom-{id}>";
    }
}
