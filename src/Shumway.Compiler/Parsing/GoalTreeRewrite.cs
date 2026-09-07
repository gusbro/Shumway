using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Parsing;

/// <summary>Bottom-up rewrite of a goal's CONTROL-FLOW skeleton without the C#
/// stack. A clause body is a right-nested run of <c>,</c>/2, so one frame per
/// conjunct puts a wall around a thousand goals — and a stack overflow kills
/// the process rather than raising something a program could catch. Every
/// clause-pipeline stage that descends through the connectives shares this
/// walk; what differs is which nodes count as connectives and what happens at
/// the goals between them.</summary>
public static class GoalTreeRewrite
{
    /// <summary>One pending connective: its arguments rewritten so far, how
    /// many are done, and whether any of them actually changed (an unchanged
    /// subtree is returned as it was, so structure stays shared).</summary>
    private sealed class Frame
    {
        public CompoundTerm Node = null!;
        public Term[] Args = null!;
        public int Index;
        public bool Changed;
    }

    /// <summary>Rewrites <paramref name="goal"/>, descending only through the
    /// nodes <paramref name="isConnective"/> accepts and applying
    /// <paramref name="rewriteGoal"/> to everything else.</summary>
    public static Term Apply(Term goal,
        Func<CompoundTerm, bool> isConnective, Func<Term, Term> rewriteGoal)
    {
        if (goal is not CompoundTerm root || !isConnective(root))
            return rewriteGoal(goal);

        var stack = new List<Frame>(32) { NewFrame(root) };
        Term? finished = null;
        while (true)
        {
            Frame f = stack[^1];
            if (finished is not null)
            {
                if (!ReferenceEquals(finished, f.Node.Args[f.Index])) f.Changed = true;
                f.Args[f.Index++] = finished;
                finished = null;
            }
            while (f.Index < f.Args.Length)
            {
                Term arg = f.Node.Args[f.Index];
                if (arg is CompoundTerm c && isConnective(c)) break;
                Term mapped = rewriteGoal(arg);
                if (!ReferenceEquals(mapped, arg)) f.Changed = true;
                f.Args[f.Index++] = mapped;
            }
            if (f.Index < f.Args.Length)
            {
                stack.Add(NewFrame((CompoundTerm)f.Node.Args[f.Index]));
                continue;
            }

            Term done = f.Changed
                ? new CompoundTerm(f.Node.Functor, f.Args) { Position = f.Node.Position }
                : f.Node;
            stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) return done;
            finished = done;
        }
    }

    private static Frame NewFrame(CompoundTerm node)
        => new() { Node = node, Args = new Term[node.Args.Length] };
}
