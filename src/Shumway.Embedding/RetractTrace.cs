using System.Diagnostics;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Opt-in dump points instrumenting <see cref="MetaBuiltins.Retract"/>
/// and its helpers. Every public method here carries
/// <c>[Conditional("SHUMWAY_RETRACT_TRACE")]</c>: if the symbol is
/// not defined at compile time the call sites are stripped and these
/// methods incur zero runtime cost. Activate from MSBuild via
/// <c>dotnet build -p:ShumwayRetractTrace=true</c> (the
/// <c>Directory.Build.props</c> in the repo root wires the property
/// to the <c>SHUMWAY_RETRACT_TRACE</c> define).
///
/// <para>The dumps land on <see cref="Console.Error"/> so they don't
/// interleave with a program's <c>stdout</c>. Each line is prefixed
/// <c>[retract]</c> for easy <c>grep</c>-out from a noisy run.</para>
///
/// <para>Origin: tracking down the bug where
/// <c>retract(next_char_i(XChar))</c> on a heavily-mutated dynamic
/// predicate binds <c>XChar</c> to the whole clause term instead of
/// the matched argument (only reproducible under Blint.pl linting
/// itself).</para>
/// </summary>
internal static class RetractTrace
{
    private const string TraceSymbol = "SHUMWAY_RETRACT_TRACE";

    [Conditional(TraceSymbol)]
    public static void Begin(Term pattern, int patternFid, int candidateCount)
    {
        Console.Error.WriteLine(
            $"[retract] begin: pattern={pattern} fid={patternFid} "
            + $"candidates={candidateCount}");
    }

    [Conditional(TraceSymbol)]
    public static void NoMatch(int candidateCount)
    {
        Console.Error.WriteLine($"[retract] no match across {candidateCount} candidates");
    }

    [Conditional(TraceSymbol)]
    public static void PrePush(Activation engine)
    {
        Cell r0 = engine.GetRegister(0);
        Console.Error.WriteLine(
            $"[retract] PRE-PUSH: register[0] = {DescribeCell(r0)} B={engine.B}");
    }

    [Conditional(TraceSymbol)]
    public static void PostPush(Activation engine)
    {
        Cell r0 = engine.GetRegister(0);
        int b = engine.B;
        Cell arityCell = engine.GetStack(b + Activation.CpArityOffset);
        Cell savedArg0 = engine.GetStack(b + Activation.CpArg1Offset);
        Console.Error.WriteLine(
            $"[retract] POST-PUSH: register[0] = {DescribeCell(r0)} B={b} "
            + $"stack[B+arityOff]={arityCell.Data} stack[B+arg1Off]={DescribeCell(savedArg0)}");
    }

    [Conditional(TraceSymbol)]
    public static void StepEntry(Activation engine, bool isResume, int startIndex)
    {
        Cell r0 = engine.GetRegister(0);
        Console.Error.WriteLine(
            $"[retract] STEP entry: resume={isResume} startIdx={startIndex} "
            + $"register[0] = {DescribeCell(r0)}");
        if (r0.Tag == Tag.Ref || r0.Tag == Tag.Str)
        {
            int target = r0.AsHeapIndex;
            int deref = engine.Deref(target);
            Console.Error.WriteLine(
                $"[retract]   register[0].AsHeapIndex={target} deref={deref} "
                + $"heap[deref]={DescribeCell(engine.GetHeap(deref))}");
        }
    }

    [Conditional(TraceSymbol)]
    public static void MatchFound(int matchIndex, Clause candidate)
    {
        Console.Error.WriteLine(
            $"[retract] match: index={matchIndex} term={candidate.Term}");
    }

    [Conditional(TraceSymbol)]
    public static void HeapStateBeforeUnify(
        Activation engine, int patternRegHeap, int candSlot, int savedHb)
    {
        Console.Error.WriteLine(
            $"[retract] pre-unify: patternRegHeap={patternRegHeap} "
            + $"candSlot={candSlot} savedHb={savedHb} hb={engine.Hb} "
            + $"heapTop={engine.HeapTop}");
        DumpCell(engine, patternRegHeap, "pattern@regHeap");
        DumpCell(engine, candSlot, "candidate@candSlot");
        DumpStructure(engine, patternRegHeap, "pattern", maxDepth: 3);
        DumpStructure(engine, candSlot, "candidate", maxDepth: 3);
    }

    [Conditional(TraceSymbol)]
    public static void HeapStateAfterUnify(
        Activation engine, int patternRegHeap, int candSlot, bool result)
    {
        Console.Error.WriteLine(
            $"[retract] post-unify: result={result} hb={engine.Hb} "
            + $"heapTop={engine.HeapTop}");
        DumpStructure(engine, patternRegHeap, "pattern", maxDepth: 3);

        // Suspicious-state check: the specific bug we are hunting is
        // "whole-head-bind" — after retract, the pattern's arg derefs
        // to a STR whose functor MATCHES the pattern's own functor
        // (i.e., X was bound to a copy of the head wrapper). Other
        // shapes (arg derefs to a different STR) are legitimate
        // (dynamic predicates can store compound values like
        // `cur_pred_i(main/0)` where the arg holds the `/`/2 indicator).
        Cell pattern = engine.GetHeap(engine.Deref(patternRegHeap));
        if (pattern.Tag == Tag.Str)
        {
            int sa = pattern.AsHeapIndex;
            Cell functorCell = engine.GetHeap(sa);
            if (functorCell.Tag == Tag.Functor)
            {
                int patternFid = functorCell.AsFunctorId;
                var (atomId, arity) = FunctorTable.Lookup(patternFid);
                for (int i = 0; i < arity; i++)
                {
                    int argHeap = engine.Deref(sa + 1 + i);
                    Cell argCell = engine.GetHeap(argHeap);
                    if (argCell.Tag != Tag.Str) continue;
                    Cell argFunctorCell = engine.GetHeap(argCell.AsHeapIndex);
                    if (argFunctorCell.Tag != Tag.Functor) continue;
                    if (argFunctorCell.AsFunctorId != patternFid) continue;
                    Console.Error.WriteLine(
                        $"[retract] !!! BUG: arg{i} of pattern "
                        + $"({AtomTable.GetById(atomId)?.Name}/{arity}) "
                        + $"derefs to Str of the SAME functor — "
                        + "whole-head-bind triggered");
                    DumpStructure(engine, argHeap, $"  arg{i}", maxDepth: 4);
                }
            }
        }
    }

    [Conditional(TraceSymbol)]
    private static void DumpCell(Activation engine, int idx, string label)
    {
        Cell c = engine.GetHeap(idx);
        Console.Error.WriteLine($"[retract]   heap[{idx}] ({label}) = {DescribeCell(c)}");
    }

    [Conditional(TraceSymbol)]
    private static void DumpStructure(Activation engine, int rootIdx, string label,
        int maxDepth)
    {
        int derefIdx = engine.Deref(rootIdx);
        Cell c = engine.GetHeap(derefIdx);
        Console.Error.WriteLine(
            $"[retract]   {label}: heap[{rootIdx}] deref->heap[{derefIdx}] = {DescribeCell(c)}");
        if (maxDepth <= 0) return;
        if (c.Tag == Tag.Str)
        {
            int sa = c.AsHeapIndex;
            Cell f = engine.GetHeap(sa);
            if (f.Tag == Tag.Functor)
            {
                var (atomId, arity) = FunctorTable.Lookup(f.AsFunctorId);
                string name = AtomTable.GetById(atomId)?.Name ?? "?";
                Console.Error.WriteLine($"[retract]     functor heap[{sa}] = {name}/{arity}");
                for (int i = 0; i < arity; i++)
                    DumpStructure(engine, sa + 1 + i, $"{label}.arg{i}", maxDepth - 1);
            }
        }
        else if (c.Tag == Tag.Lis)
        {
            int sa = c.AsHeapIndex;
            DumpStructure(engine, sa, $"{label}.head", maxDepth - 1);
            DumpStructure(engine, sa + 1, $"{label}.tail", maxDepth - 1);
        }
    }

    private static string DescribeCell(Cell c)
    {
        return c.Tag switch
        {
            Tag.Ref => $"Ref({c.AsHeapIndex})",
            Tag.Atom => $"Atom({AtomTable.GetById(c.AsAtomId)?.Name ?? "?"})",
            Tag.Int => $"Int({c.AsInt})",
            Tag.Functor => $"Functor({c.AsFunctorId})",
            Tag.Str => $"Str(->{c.AsHeapIndex})",
            Tag.Lis => $"Lis(->{c.AsHeapIndex})",
            Tag.AttVar => $"AttVar({c.AsHeapIndex})",
            _ => $"{c.Tag}({c.AsHeapIndex})",
        };
    }
}
