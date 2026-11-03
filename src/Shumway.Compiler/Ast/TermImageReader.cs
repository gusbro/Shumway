using System.Globalization;
using Shumway.Core;

namespace Shumway.Compiler.Ast;

/// <summary>ADR-063: the term an image holds, as the reader of a heap term
/// produces it: for a fact asserted at run time, built when something wants
/// the clause as a term (a listing, a bundle, the compile of its predicate). A
/// variable is named by its cell, <c>_G7</c>; a closed run of packed text is a
/// <see cref="StringTerm"/>, as the clause store's reader keeps one. Iterative,
/// and a sub-term two arguments share is read twice, as from the heap.</summary>
public static class TermImageReader
{
    // Expand a cell into a term; assemble a structure or a list from the terms
    // its arguments became; prepend a run of packed text to its tail.
    private const byte Expand = 0, Structure = 1, List = 2, Text = 3;

    private readonly record struct Frame(byte Kind, Cell Cell, int Arity, string? Name, int Id);

    public static Term ToTerm(TermImage image)
    {
        var cells = image.Cells;
        var work = new Stack<Frame>();
        var results = new Stack<Term>();
        int consFid = -1;
        work.Push(new Frame(Expand, image.RootCell(0), 0, null, 0));
        while (work.Count > 0)
        {
            var f = work.Pop();
            switch (f.Kind)
            {
                case Expand:
                {
                    Cell c = f.Cell;
                    switch (c.Tag)
                    {
                        case Tag.Ref:
                        {
                            int at = c.AsHeapIndex;
                            Cell v = cells[at];
                            if (v.Tag == Tag.Float)
                            {
                                results.Push(new FloatTerm(Cell.DecodeFloat(v, cells[at + 1])));
                            }
                            else if (v.Tag == Tag.Pstr)
                            {
                                work.Push(new Frame(Text, default, (int)v.AsPstrKind, image.PstrRun(at), 0));
                                work.Push(new Frame(Expand, cells[image.PstrTailOffset(at)], 0, null, 0));
                            }
                            else
                            {
                                results.Push(new VarTerm("_G" + at.ToString(CultureInfo.InvariantCulture)));
                            }
                            break;
                        }
                        case Tag.Atom:
                            results.Push(new AtomTerm(AtomTable.GetById(c.AsAtomId)?.Name ?? "?", c.AsAtomId));
                            break;
                        case Tag.Int:
                            results.Push(new IntTerm(c.AsInt));
                            break;
                        case Tag.Str:
                        {
                            // The arguments in order, so the first is on top when
                            // the structure is assembled.
                            int k = c.AsHeapIndex;
                            int fid = cells[k].AsFunctorId;
                            var (atomId, arity) = FunctorTable.Lookup(fid);
                            work.Push(new Frame(Structure, default, arity, AtomTable.GetById(atomId)?.Name ?? "?", fid));
                            for (int i = 0; i < arity; i++)
                                work.Push(new Frame(Expand, cells[k + 1 + i], 0, null, 0));
                            break;
                        }
                        case Tag.Lis:
                        {
                            int k = c.AsHeapIndex;
                            work.Push(new Frame(List, default, 2, null, 0));
                            work.Push(new Frame(Expand, cells[k], 0, null, 0));
                            work.Push(new Frame(Expand, cells[k + 1], 0, null, 0));
                            break;
                        }
                        default:
                            throw new InvalidOperationException($"An image holds no {c.Tag} cell as a value.");
                    }
                    break;
                }
                case Structure:
                {
                    var args = new Term[f.Arity];
                    for (int i = 0; i < args.Length; i++) args[i] = results.Pop();
                    results.Push(new CompoundTerm(f.Name!, args, f.Id));
                    break;
                }
                case List:
                {
                    Term head = results.Pop();
                    Term tail = results.Pop();
                    results.Push(new CompoundTerm(".", new[] { head, tail }, ConsFid(ref consFid)));
                    break;
                }
                default:
                {
                    Term tail = results.Pop();
                    var kind = (TextKind)f.Arity;
                    string run = f.Name!;
                    if (tail is AtomTerm { Name: "[]" })
                    {
                        results.Push(new StringTerm(run, kind));
                    }
                    else if (tail is StringTerm next && next.Kind == kind)
                    {
                        results.Push(new StringTerm(run + next.Content, kind));
                    }
                    else
                    {
                        bool chars = kind == TextKind.Chars;
                        for (int i = run.Length - 1; i >= 0; i--)
                        {
                            Term head = chars ? new AtomTerm(run[i].ToString()) : new IntTerm(run[i]);
                            tail = new CompoundTerm(".", new[] { head, tail }, ConsFid(ref consFid));
                        }
                        results.Push(tail);
                    }
                    break;
                }
            }
        }
        return results.Pop();
    }

    private static int ConsFid(ref int memo)
        => memo >= 0 ? memo : memo = FunctorTable.Intern(AtomTable.ConsFunctorId, 2);
}
