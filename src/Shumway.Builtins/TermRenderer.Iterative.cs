using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>The renderer's descent, on an EXPLICIT stack.
///
/// <para>How deeply a term nests is the program's choice, so a recursive
/// renderer spends a C# frame per level, and a .NET stack overflow cannot be
/// caught: it takes the process down with no goal to unwind and nothing to
/// report. Everything here is the same code the recursive form ran, in the
/// same order, with the descent turned into jobs.</para>
///
/// <para>Two things had to be preserved exactly, and both are why this is a
/// job machine rather than a loop. First, the fuse-aware spacing needs the
/// TEXT of an operand before it can decide (`X = -1` must not come back as
/// `X=-1`, which lexes `=-` as one token), so those operands still render into
/// their own buffer and a join step assembles from it -- the same buffering,
/// in the same places, so nothing that streamed before stops streaming.
/// Second, the cycle bookkeeping is enter/exit: a node joins the path, its
/// subtree renders, it leaves. A `finally` did that before; here an exit job
/// pushed UNDER a node's children runs when they are done. The list spine
/// interleaves the two, joining the path one cons at a time as it goes, and an
/// element sees exactly the partial path it saw before.</para></summary>
public static partial class TermRenderer
{
    private enum RenderOp
    {
        /// <summary>Render a cell at a priority into a writer.</summary>
        Node,
        /// <summary>The same, in OPERATOR-OPERAND position.</summary>
        Operand,
        /// <summary>Write literal text.</summary>
        Text,
        /// <summary>Leave a node: undo its path / unroll / depth claim.</summary>
        ExitNode,
        /// <summary>One cons of a list spine, in `[a, b | t]` form.</summary>
        ListStep,
        /// <summary>One cons of a list spine, in `'.'(H, T)` form.</summary>
        CanonStep,
        /// <summary>One argument of a canonical compound. A step rather than a
        /// job per argument plus a job per comma: half the queue traffic for
        /// the shape that dominates ordinary output.</summary>
        ArgStep,
        /// <summary>Drop the spine cells this list joined to the path.</summary>
        ExitSpine,
        JoinInfix,
        JoinPrefix,
        JoinPostfix,
    }

    /// <summary>One queued job. Every field is copied on each push and pop, so
    /// the fields OVERLAP where no single job kind uses both: a job carries at
    /// most one number, at most two references beyond its writer, and its
    /// booleans live in one byte. Named accessors keep the planners reading as
    /// if the fields were separate. Widening this struct is the easiest way to
    /// make rendering slower.</summary>
    private struct RenderJob
    {
        public RenderOp Op;
        public byte Bits;
        public Cell Cell;
        public int Num;
        public TextWriter Out;
        public string? Text;
        public object? A;
        public object? B;

        private const byte BVarLeft = 1, BVarRight = 2, BParens = 4,
                           BPathAdded = 8, BUnrolled = 16, BDepth = 32, BFirst = 64;

        private readonly bool Get(byte bit) => (Bits & bit) != 0;
        private void Set(byte bit, bool on)
            => Bits = (byte)(on ? Bits | bit : Bits & ~bit);

        /// <summary>Node / Operand: the priority ceiling.</summary>
        public int Prec { readonly get => Num; set => Num = value; }
        /// <summary>List steps: how many conses deep the spine is.</summary>
        public int ConsDepth { readonly get => Num; set => Num = value; }
        /// <summary>Join: the left (or only) operand's buffer.</summary>
        public StringWriter? Left { readonly get => (StringWriter?)A; set => A = value; }
        /// <summary>Join: the right operand's buffer.</summary>
        public StringWriter? Right { readonly get => (StringWriter?)B; set => B = value; }
        /// <summary>List steps: the spine cells joined to the cycle path.</summary>
        public List<int>? Spine { readonly get => (List<int>?)A; set => A = value; }

        public bool VarLeft { readonly get => Get(BVarLeft); set => Set(BVarLeft, value); }
        public bool VarRight { readonly get => Get(BVarRight); set => Set(BVarRight, value); }
        public bool Parens { readonly get => Get(BParens); set => Set(BParens, value); }
        public bool PathAdded { readonly get => Get(BPathAdded); set => Set(BPathAdded, value); }
        public bool Unrolled { readonly get => Get(BUnrolled); set => Set(BUnrolled, value); }
        public bool DepthTaken { readonly get => Get(BDepth); set => Set(BDepth, value); }
        public bool First { readonly get => Get(BFirst); set => Set(BFirst, value); }
    }

    private static void Push(List<RenderJob> stack, in RenderJob job) => stack.Add(job);

    private static void PushText(List<RenderJob> stack, TextWriter target, string text)
        => stack.Add(new RenderJob { Op = RenderOp.Text, Out = target, Text = text });

    private static void PushNode(
        List<RenderJob> stack, Cell cell, TextWriter target, int prec)
        => stack.Add(new RenderJob
        { Op = RenderOp.Node, Cell = cell, Out = target, Prec = prec });

    private static void PushOperand(
        List<RenderJob> stack, Cell cell, TextWriter target, int prec)
        => stack.Add(new RenderJob
        { Op = RenderOp.Operand, Cell = cell, Out = target, Prec = prec });

    public static void Render(Activation engine, Cell cell, TextWriter output,
        TermRenderOptions options, int maxPriority)
    {
        var stack = new List<RenderJob>(16);
        PushNode(stack, cell, output, maxPriority);
        int depthOnEntry = options.CurrentDepth;
        try
        {
            while (stack.Count > 0)
            {
                RenderJob job = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                switch (job.Op)
                {
                    case RenderOp.Text: job.Out.Write(job.Text); break;
                    case RenderOp.Node: PlanNode(engine, in job, stack, options); break;
                    case RenderOp.Operand: PlanOperand(engine, in job, stack, options); break;
                    case RenderOp.ExitNode:
                        if (job.PathAdded) options.OnPath!.Remove(job.Cell.AsHeapIndex);
                        if (job.Unrolled) options.UnrolledOnce!.Remove(job.Cell.AsHeapIndex);
                        if (job.DepthTaken) options.CurrentDepth--;
                        break;
                    case RenderOp.ExitSpine: RemoveSpine(options, job.Spine); break;
                    case RenderOp.ListStep: PlanListStep(engine, in job, stack, options); break;
                    case RenderOp.CanonStep: PlanCanonStep(engine, in job, stack, options); break;
                    case RenderOp.ArgStep: PlanArgStep(engine, in job, stack); break;
                    case RenderOp.JoinInfix: JoinInfix(in job); break;
                    case RenderOp.JoinPrefix: JoinPrefix(in job); break;
                    default: JoinPostfix(in job); break;
                }
            }
        }
        catch
        {
            // The exit jobs that would have undone the bookkeeping are still
            // on the stack and will never run. A `finally` per level did this
            // before; the options object outlives the call, so leaving a cell
            // on the path would make the NEXT render elide a term that is not
            // cyclic at all.
            options.CurrentDepth = depthOnEntry;
            options.OnPath?.Clear();
            options.UnrolledOnce?.Clear();
            throw;
        }
    }

    /// <summary>One node: writes what it can and queues what it cannot. This
    /// is the recursive Render's switch, with the two compound arms handing
    /// off to a planner instead of descending.</summary>
    private static void PlanNode(
        Activation engine, in RenderJob job, List<RenderJob> stack, TermRenderOptions options)
    {
        Cell cell = job.Cell;
        TextWriter output = job.Out;
        int derefAddr = Resolve(engine, ref cell);

        // portrayed(true): the user's portray/1 gets first shot at every
        // subterm; on success its output IS the rendering.
        if (options.Portray is { } portray
            && cell.Tag is not (Tag.Ref or Tag.AttVar)
            && portray(engine, cell, output))
            return;

        switch (cell.Tag)
        {
            case Tag.Ref:
            case Tag.AttVar:
                // An attributed variable is still an unbound variable -- it
                // renders exactly like a plain one. Its attributes are not
                // part of its written form.
                if (options.VariableNames is not null
                    && options.VariableNames.TryGetValue(derefAddr, out string? vName))
                {
                    output.Write(vName);
                    return;
                }
                output.Write("_G");
                output.Write(derefAddr.ToString(CultureInfo.InvariantCulture));
                return;
            case Tag.Atom:
                WriteAtomName(NameOfAtom(cell.AsAtomId), output, options);
                return;
            case Tag.Int:
                output.Write(cell.AsInt.ToString(CultureInfo.InvariantCulture));
                return;
            case Tag.BigInt:
                output.Write(engine.AsBigInt(cell).ToString(CultureInfo.InvariantCulture));
                return;
            case Tag.Rational:
            {
                // Rendered as the operator term `Num rdiv Den` (re-readable --
                // `rdiv` is a 400,yfx operator that re-evaluates to the value).
                // Parenthesise where an enclosing operator's priority forbids
                // a 400-priority operand.
                var r = engine.AsRational(cell);
                bool paren = job.Prec < 400;
                if (paren) output.Write('(');
                output.Write(r.Num.ToString(CultureInfo.InvariantCulture));
                output.Write(" rdiv ");
                output.Write(r.Den.ToString(CultureInfo.InvariantCulture));
                if (paren) output.Write(')');
                return;
            }
            case Tag.Float:
            {
                double v = Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex));
                output.Write(Number.FormatPrologFloat(v));
                return;
            }
            case Tag.Str:
            {
                // With max_depth set the depth limit already terminates a
                // rational tree -- and it decides HOW MUCH of the cycle shows
                // (max_depth(3) on X=f(X) is f(f(f(...)))), so the cycle gate
                // must not cut first.
                bool added = false, unrolling = false;
                if (options.MaxDepth == 0
                    && !EnterCycleNode(options, cell.AsHeapIndex, out added, out unrolling))
                {
                    output.Write("...");
                    return;
                }
                bool tookDepth = false;
                if (options.MaxDepth > 0)
                {
                    if (options.CurrentDepth >= options.MaxDepth)
                    {
                        output.Write("...");
                        return;
                    }
                    options.CurrentDepth++;
                    tookDepth = true;
                }
                Push(stack, new RenderJob
                {
                    Op = RenderOp.ExitNode, Cell = cell,
                    PathAdded = added, Unrolled = unrolling, DepthTaken = tookDepth,
                });
                PlanCompound(engine, cell, output, job.Prec, stack, options);
                return;
            }
            // A packed list is the list it denotes, so it renders through the
            // same arm -- which is also what gives it quoting, ignore_ops,
            // max_depth and numbervars.
            case Tag.Lis:
            case Tag.Pstr:
            {
                bool added = false, unrolling = false;
                if (options.MaxDepth == 0 && cell.Tag == Tag.Lis
                    && !EnterCycleNode(options, cell.AsHeapIndex, out added, out unrolling))
                {
                    output.Write("...");
                    return;
                }
                bool tookDepth = false;
                if (options.MaxDepth > 0)
                {
                    if (options.CurrentDepth >= options.MaxDepth)
                    {
                        output.Write("...");
                        return;
                    }
                    options.CurrentDepth++;
                    tookDepth = true;
                }
                Push(stack, new RenderJob
                {
                    Op = RenderOp.ExitNode, Cell = cell,
                    PathAdded = added, Unrolled = unrolling, DepthTaken = tookDepth,
                });
                PlanList(engine, cell, output, stack, options);
                return;
            }
            default:
                output.Write('<');
                output.Write(cell.Tag.ToString());
                output.Write('>');
                return;
        }
    }

    /// <summary>An operator's OPERAND. Parenthesises a bare operator-atom --
    /// `-(-,-)` writes as `(-)-(-)`, not `- - -`, which the reader rejects
    /// (ISO 6.3.1.3) -- and is a max_depth LEVEL in its own right, unlike the
    /// argument of a canonical compound or an element of a list, where an atom
    /// at the limit still prints.</summary>
    private static void PlanOperand(
        Activation engine, in RenderJob job, List<RenderJob> stack, TermRenderOptions options)
    {
        if (options.MaxDepth > 0 && options.CurrentDepth >= options.MaxDepth)
        {
            job.Out.Write("...");
            return;
        }
        if (IsBareOperatorAtomCell(engine, job.Cell, options))
        {
            job.Out.Write('(');
            PushText(stack, job.Out, ")");
            PushNode(stack, job.Cell, job.Out, 1200);
            return;
        }
        PushNode(stack, job.Cell, job.Out, job.Prec);
    }

    private static void JoinInfix(in RenderJob job)
    {
        string ls = job.Left!.ToString(), rs = job.Right!.ToString();
        string itext = job.Text!;
        TextWriter output = job.Out;
        output.Write(ls);
        if (ls.Length > 0 && ((!job.VarLeft && CharsFuse(ls[^1], itext[0]))
                              || ZeroThenQuote(ls, itext)))
            output.Write(' ');
        output.Write(itext);
        if (rs.Length > 0 && !job.VarRight && CharsFuse(itext[^1], rs[0])) output.Write(' ');
        output.Write(rs);
        if (job.Parens) output.Write(')');
    }

    private static void JoinPrefix(in RenderJob job)
    {
        string os = job.Left!.ToString();
        string prefixText = job.Text!;
        TextWriter output = job.Out;
        // Space only where the tokens would otherwise fuse -- `fy 1` but
        // `--a` / `' op'[]` / `-A` (Neumerkel #274/#133/#279) -- plus ALWAYS
        // before a parenthesised operand: `fy(...)` would re-read as
        // FUNCTIONAL notation, a different term (`fy (fy 1)yf` vs
        // `fy(fy 1)yf`, #319; `- (1)`, `- (X^2)`).
        if (os.Length > 0
            && ((!job.VarLeft && CharsFuse(prefixText[^1], os[0])) || os[0] == '('))
            output.Write(' ');
        output.Write(os);
        if (job.Parens) output.Write(')');
    }

    private static void JoinPostfix(in RenderJob job)
    {
        string ps = job.Left!.ToString();
        string postText = job.Text!;
        TextWriter output = job.Out;
        output.Write(ps);
        if (ps.Length > 0
            && ((!job.VarLeft && CharsFuse(ps[^1], postText[0]))
                || ZeroThenQuote(ps, postText)))
            output.Write(' ');
        output.Write(postText);
        if (job.Parens) output.Write(')');
    }
}
