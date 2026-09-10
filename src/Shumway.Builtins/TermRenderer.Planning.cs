using System.Collections.Generic;
using System.IO;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>The per-shape planners: what each kind of node writes now, and
/// what it owes to jobs. Every branch is the recursive renderer's branch
/// unchanged; only the descent became a push. Jobs are pushed in REVERSE of
/// the order they must run, because the stack is LIFO.</summary>
public static partial class TermRenderer
{
    private static void PlanCompound(
        Activation engine, Cell strCell, TextWriter output, int maxPriority,
        List<RenderJob> stack, TermRenderOptions options)
    {
        int functorIdx = strCell.AsHeapIndex;
        Cell functorCell = engine.GetHeap(functorIdx);
        var (atomId, arity) = FunctorTable.Lookup(functorCell.AsFunctorId);
        string name = NameOfAtom(atomId);

        // numbervars(true): '$VAR'(N) renders as letter sequence A, B, ..., Z,
        // A1, B1, ...
        if (options.Numbervars && arity == 1 && name == "$VAR")
        {
            Cell nCell = engine.GetHeap(functorIdx + 1);
            Resolve(engine, ref nCell);
            if (nCell.Tag == Tag.Int && nCell.AsInt >= 0)
            {
                output.Write(NumbervarsName(nCell.AsInt));
                return;
            }
        }

        // Curly-brace notation: '{}'(Body) is {Body}. UNLIKE list notation it
        // does NOT survive ignore_ops(true) -- write_canonical prints the
        // functional {}(Body). The braces bracket the body, so it renders at
        // full priority.
        if (arity == 1 && name == "{}" && !options.IgnoreOps)
        {
            output.Write('{');
            PushText(stack, output, "}");
            PushNode(stack, engine.GetHeap(functorIdx + 1), output, 1200);
            return;
        }

        if (!options.IgnoreOps && options.Operators is not null)
        {
            if (arity == 2 && options.Operators.TryGetInfix(
                    name, out int infixPrec, out OperatorShape infixShape))
            {
                PlanInfix(engine, functorIdx, name, infixPrec, infixShape,
                          output, maxPriority, stack, options);
                return;
            }
            if (arity == 1 && options.Operators.TryGetPrefix(
                    name, out int prefixPrec, out OperatorShape prefixShape))
            {
                PlanPrefix(engine, functorIdx, name, prefixPrec, prefixShape,
                           output, maxPriority, stack, options);
                return;
            }
            if (arity == 1 && options.Operators.TryGetPostfix(
                    name, out int postPrec, out OperatorShape postShape))
            {
                PlanPostfix(engine, functorIdx, name, postPrec, postShape,
                            output, maxPriority, stack, options);
                return;
            }
        }

        WriteAtomName(name, output, options);
        if (arity == 0) return;
        output.Write('(');
        Push(stack, new RenderJob
        { Op = RenderOp.ArgStep, Cell = strCell, Out = output, Num = 0 });
    }

    /// <summary>One argument of a canonical compound, then the next. Inside
    /// argument lists, comma is precedence 1000 in standard Prolog, so each
    /// argument can carry up to 999 priority without parens.</summary>
    private static void PlanArgStep(
        Activation engine, in RenderJob job, List<RenderJob> stack)
    {
        int functorIdx = job.Cell.AsHeapIndex;
        var (_, arity) = FunctorTable.Lookup(engine.GetHeap(functorIdx).AsFunctorId);
        int i = job.Num;
        if (i >= arity)
        {
            job.Out.Write(')');
            return;
        }
        if (i > 0) job.Out.Write(',');   // ISO: no layout between args
        Push(stack, new RenderJob
        { Op = RenderOp.ArgStep, Cell = job.Cell, Out = job.Out, Num = i + 1 });
        PushNode(stack, engine.GetHeap(functorIdx + 1 + i), job.Out, 999);
    }

    private static void PlanInfix(
        Activation engine, int functorIdx, string name, int infixPrec,
        OperatorShape infixShape, TextWriter output, int maxPriority,
        List<RenderJob> stack, TermRenderOptions options)
    {
        bool needsParens = infixPrec > maxPriority;
        if (needsParens) output.Write('(');
        int leftMax = infixShape == OperatorShape.Yfx ? infixPrec : infixPrec - 1;
        int rightMax = infixShape == OperatorShape.Xfy ? infixPrec : infixPrec - 1;
        // A y-LEFT operand of equal priority that is open on the right must be
        // parenthesised -- `yfx(fy(1),2)` prints `(fy 1)yfx 2` (Neumerkel
        // #153); its bare text would re-read differently.
        bool leftOpenParens = infixShape == OperatorShape.Yfx
            && OperandOpenRightAt(engine, engine.GetHeap(functorIdx + 1), infixPrec, options);
        // The `,` operator renders tight and unquoted -- `a,b`. An operator in
        // operator position is written raw when its name is a valid bare token
        // there (quoting `,` / `|` would be wrong); a name that is NOT (the
        // empty atom `''` as an operator, or one with layout in it) must be
        // quoted or the output is unreadable.
        string itext = (name == "," || name == "|" || NeedsNoQuoting(name))
            ? name : QuotedAtomName(name);
        Cell leftCell = engine.GetHeap(functorIdx + 1);
        Cell rightCell = engine.GetHeap(functorIdx + 2);

        if (options.TightSymbolicOperators)
        {
            // Fuse-aware spacing for EVERY infix operator: adjacent tokens of
            // the same character class fuse on re-read (`1=\\` lexes `=\\` as
            // one atom). The operands render into their own buffers so their
            // edge chars are known, and a space goes in ONLY where the
            // operator would fuse with one -- EXCEPT (symbolic operators only)
            // when the operand is an unbound variable, whose name is written
            // verbatim per 7.10.5 and is not spaced (`1+/*r*/V`).
            bool symbolic = IsSymbolicName(name);
            var lw = new StringWriter();
            var rw = new StringWriter();
            Push(stack, new RenderJob
            {
                Op = RenderOp.JoinInfix, Out = output, Text = itext,
                Left = lw, Right = rw, Parens = needsParens,
                VarLeft = symbolic && IsUnboundVarCell(engine, leftCell),
                VarRight = symbolic && IsUnboundVarCell(engine, rightCell),
            });
            PushOperand(stack, rightCell, rw, rightMax);
            if (leftOpenParens)
            {
                PushText(stack, lw, ")");
                PushNode(stack, leftCell, lw, 1200);
                PushText(stack, lw, "(");
            }
            else PushOperand(stack, leftCell, lw, leftMax);
            return;
        }

        if (needsParens) PushText(stack, output, ")");
        PushOperand(stack, rightCell, output, rightMax);
        PushText(stack, output, " " + itext + " ");
        if (leftOpenParens)
        {
            PushText(stack, output, ")");
            PushNode(stack, leftCell, output, 1200);
            PushText(stack, output, "(");
        }
        else PushOperand(stack, leftCell, output, leftMax);
    }

    private static void PlanPrefix(
        Activation engine, int functorIdx, string name, int prefixPrec,
        OperatorShape prefixShape, TextWriter output, int maxPriority,
        List<RenderJob> stack, TermRenderOptions options)
    {
        Cell argCell = engine.GetHeap(functorIdx + 1);
        bool needsParens = prefixPrec > maxPriority;
        // ISO writeq: prefix `-` applied to a term whose leftmost token is a
        // non-negative number must parenthesise THE OPERAND -- `- 1` reads
        // back as the negative-number literal -1, not the compound -(1).
        // `+`/`\` have no such literal, so only `-`.
        bool operandParens =
            (name == "-" && RendersLeadingDigit(engine, argCell, options))
            || IsBareOperatorAtomCell(engine, argCell, options)
            // Neumerkel vn #43: prefix `-` applied to an operand that is a
            // LEFT-CLOSED operator of EQUAL priority is parenthesised --
            // `- X^2` -> `- (X^2)`. ONLY for `-`. The >-priority case is
            // already parenthesised by argMax below.
            || (name == "-"
                && OperandIsOperatorPriorityAtLeast(engine, argCell, prefixPrec, options));
        if (needsParens) output.Write('(');
        string prefixText = (!options.Quoted || NeedsNoQuoting(name))
            ? name : QuotedAtomName(name);
        output.Write(prefixText);
        int argMax = prefixShape == OperatorShape.Fy ? prefixPrec : prefixPrec - 1;
        var opw = new StringWriter();
        Push(stack, new RenderJob
        {
            Op = RenderOp.JoinPrefix, Out = output, Text = prefixText,
            Left = opw, Parens = needsParens,
            VarLeft = IsSymbolicName(name) && IsUnboundVarCell(engine, argCell),
        });
        if (operandParens) PushText(stack, opw, ")");
        PushNode(stack, argCell, opw, operandParens ? 1200 : argMax);
        if (operandParens) PushText(stack, opw, "(");
    }

    private static void PlanPostfix(
        Activation engine, int functorIdx, string name, int postPrec,
        OperatorShape postShape, TextWriter output, int maxPriority,
        List<RenderJob> stack, TermRenderOptions options)
    {
        Cell argCell = engine.GetHeap(functorIdx + 1);
        bool needsParens = postPrec > maxPriority;
        if (needsParens) output.Write('(');
        int argMax = postShape == OperatorShape.Yf ? postPrec : postPrec - 1;
        // A y-LEFT operand of equal priority that is open on the right must be
        // parenthesised -- yf(fy(1)) prints `(fy 1)yf` (Neumerkel #150/#156/#319).
        bool postOpenParens = postShape == OperatorShape.Yf
            && OperandOpenRightAt(engine, argCell, postPrec, options);
        string postText = (!options.Quoted || NeedsNoQuoting(name))
            ? name : QuotedAtomName(name);

        if (options.TightSymbolicOperators)
        {
            // Fuse-aware spacing, as for infix: `1 yf` needs the space,
            // `(fy 1)yf` / `-1'$VAR'` do not (Neumerkel #149/#150/#355).
            var pw = new StringWriter();
            Push(stack, new RenderJob
            {
                Op = RenderOp.JoinPostfix, Out = output, Text = postText,
                Left = pw, Parens = needsParens,
                VarLeft = IsSymbolicName(name) && IsUnboundVarCell(engine, argCell),
            });
            if (postOpenParens)
            {
                PushText(stack, pw, ")");
                PushNode(stack, argCell, pw, 1200);
                PushText(stack, pw, "(");
            }
            else PushOperand(stack, argCell, pw, argMax);
            return;
        }

        if (needsParens) PushText(stack, output, ")");
        PushText(stack, output, postText);
        PushText(stack, output, " ");
        if (postOpenParens)
        {
            PushText(stack, output, ")");
            PushNode(stack, argCell, output, 1200);
            PushText(stack, output, "(");
        }
        else PushOperand(stack, argCell, output, argMax);
    }

    private static void PlanList(
        Activation engine, Cell lisCell, TextWriter output,
        List<RenderJob> stack, TermRenderOptions options)
    {
        if (options.IgnoreOps)
        {
            // ISO 7.10.5 canonical form (write_canonical): a list is the
            // compound '.'(H, T) and ignore_ops means FUNCTIONAL notation --
            // `'.'(a,[])`, not `[a]`. The dot functor obeys the quoted option
            // like any other atom: write_term defaults quoted(false), so it
            // prints bare -- `.(a,[])`.
            // The spine cells this walk joins to the cycle path. Unlike the
            // bracket form below it needs them whatever max_depth says: see
            // PlanCanonStep.
            var canonSpine = new List<int>();
            Push(stack, new RenderJob { Op = RenderOp.ExitSpine, Spine = canonSpine });
            Push(stack, new RenderJob
            {
                Op = RenderOp.CanonStep, Cell = lisCell, Out = output, First = true,
                Text = options.Quoted ? "'.'(" : ".(", ConsDepth = 0,
                Spine = canonSpine,
            });
            return;
        }
        if (options.PortrayText && TryRenderAsText(engine, lisCell, output, options))
            return;
        if (options.PortrayText && options.DoubleBar
            && TryCollectText(engine, lisCell, out string text, out Cell openTail))
        {
            Resolve(engine, ref openTail);
            // A closed one is the other reading's business, and an empty
            // prefix says nothing: `""||T` is just T.
            bool closed = openTail.Tag == Tag.Atom
                          && openTail.AsAtomId == AtomTable.EmptyListId;
            if (!closed && text.Length > 0)
            {
                WriteQuotedText(text, output);
                output.Write("||");
                PushNode(stack, openTail, output, 0);
                return;
            }
        }

        output.Write('[');
        // The spine cells this list joins to the cycle path, dropped together
        // when it is done -- what the recursive form's RemoveSpine did.
        List<int>? spine = options.MaxDepth == 0 ? new List<int>() : null;
        Push(stack, new RenderJob { Op = RenderOp.ExitSpine, Spine = spine });
        Push(stack, new RenderJob
        {
            Op = RenderOp.ListStep, Cell = lisCell, Out = output, First = true,
            // max_depth: the ENTRY cons already consumed one level (the node
            // gate incremented); every further cons consumes another.
            ConsDepth = options.CurrentDepth, Spine = spine,
        });
    }

    /// <summary>One cons of `[a, b | t]`. The spine is walked a step at a time
    /// rather than up front so an element still sees exactly the partial cycle
    /// path it saw when this was a loop with the render inside it.</summary>
    private static void PlanListStep(
        Activation engine, in RenderJob job, List<RenderJob> stack, TermRenderOptions options)
    {
        Cell cursor = job.Cell;
        TextWriter output = job.Out;
        Resolve(engine, ref cursor);

        // Spine back-edge (a cyclic cons chain): the entry cons was gated by
        // the caller; every FURTHER cons joins the path here, and a revisit
        // elides IMMEDIATELY (the tail-position policy) -- `L = [a|L]` is
        // `[a|...]`.
        if (!job.First && options.MaxDepth == 0 && cursor.Tag == Tag.Lis)
        {
            var spinePath = options.OnPath ??= new HashSet<int>();
            if (!spinePath.Add(cursor.AsHeapIndex))
            {
                output.Write("|...]");
                return;
            }
            job.Spine!.Add(cursor.AsHeapIndex);
        }

        if (!engine.TryUnconsListLike(cursor, out Cell head, out Cell tail))
        {
            // The tail is whatever it dereffed to.
            if (cursor.Tag == Tag.Atom && cursor.AsAtomId == AtomTable.EmptyListId)
            {
                output.Write(']');                 // proper list -- clean close
                return;
            }
            output.Write('|');                     // ISO: compact improper tail
            // A revisited TAIL elides immediately -- [1|f([1|...])] ends at the
            // second f, not with another unroll.
            if (options.MaxDepth == 0 && IsOnPath(options, cursor))
            {
                output.Write("...");
                output.Write(']');
                return;
            }
            PushText(stack, output, "]");
            PushNode(stack, cursor, output, 999);
            return;
        }

        int consDepth = job.ConsDepth;
        if (!job.First)
        {
            consDepth++;
            if (options.MaxDepth > 0 && consDepth > options.MaxDepth)
            {
                output.Write("|...]");
                return;
            }
            output.Write(',');                     // ISO: no layout between elements
        }

        // The next cons runs AFTER this element, so it goes on first.
        Push(stack, new RenderJob
        {
            Op = RenderOp.ListStep, Cell = tail, Out = output, First = false,
            ConsDepth = consDepth, Spine = job.Spine,
        });

        // Each element is an argument-priority (999) position: a ','/2 element
        // must parenthesise (`[(a,b)]`, not `[a,b]`), as must any operator
        // >= 1000. Plain Node, NOT Operand: an ATOM that is an operator is a
        // legal argument bare (ISO 6.3.3 -- `[:-,-]`, not `[(:-),(-)]`); the
        // operand-position parens are only for operator operands. A revisited
        // ELEMENT elides immediately (never unrolls) -- L = [L, a | b] prints
        // [...,a|b].
        Cell headResolved = head;
        Resolve(engine, ref headResolved);
        if (options.MaxDepth == 0 && IsOnPath(options, headResolved))
            output.Write("...");
        else
            PushNode(stack, head, output, 999);
    }

    /// <summary>One cons of the functional form `'.'(H, T)`.
    ///
    /// <para>The spine is walked here rather than through the node gate, so
    /// the cycle check has to be here too, and it cannot be conditional on
    /// max_depth being off the way the bracket form's is: max_depth does not
    /// bound this walk at all (`[a,b,c]` under max_depth(2) prints in full as
    /// `.(a,.(b,.(c,[])))`). Without it a cyclic spine never terminated --
    /// `X = [a|X], write_canonical(X)` wrote `.(a,.(a,` until something gave
    /// out.</para></summary>
    private static void PlanCanonStep(
        Activation engine, in RenderJob job, List<RenderJob> stack, TermRenderOptions options)
    {
        Cell cursor = job.Cell;
        TextWriter output = job.Out;
        Resolve(engine, ref cursor);

        // The entry cons was already joined to the path by the node gate, but
        // only when max_depth is off; with it on, this walk owns that too.
        if (cursor.Tag == Tag.Lis && (!job.First || options.MaxDepth > 0))
        {
            var spinePath = options.OnPath ??= new HashSet<int>();
            if (!spinePath.Add(cursor.AsHeapIndex))
            {
                output.Write("...");
                output.Write(new string(')', job.ConsDepth));
                return;
            }
            job.Spine!.Add(cursor.AsHeapIndex);
        }

        if (!engine.TryUnconsListLike(cursor, out Cell head, out Cell tail))
        {
            PushText(stack, output, new string(')', job.ConsDepth));
            PushNode(stack, cursor, output, 999);
            return;
        }
        output.Write(job.Text);
        Push(stack, new RenderJob
        {
            Op = RenderOp.CanonStep, Cell = tail, Out = output, First = false,
            Text = job.Text, ConsDepth = job.ConsDepth + 1, Spine = job.Spine,
        });
        PushText(stack, output, ",");
        PushNode(stack, head, output, 999);
    }
}
