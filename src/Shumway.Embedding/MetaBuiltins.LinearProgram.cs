using Shumway.Builtins;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    /// <summary><c>'$lp_optimise'(+NVars, +Rows, +Objective, +Maximise, -Status, -Value, -Vertex)</c>
    /// — the CLP(R) optimiser's simplex.
    ///
    /// <para>Rows and Objective are flat lists of numbers, each group of
    /// <c>NVars + 1</c> reading <c>a·x + c &gt;= 0</c> (the objective's constant
    /// rides along and lands in Value). Maximise is the atom <c>true</c> or
    /// <c>false</c>. Status comes back as <c>optimal</c>, <c>unbounded</c> or
    /// <c>infeasible</c>; on <c>optimal</c>, Vertex is the list of NVars values
    /// that attains it, which is what branch and bound reads.</para>
    ///
    /// <para>Marshalled here rather than solved in Prolog: a tableau is dense
    /// array arithmetic, and pivoting one in Prolog lists would rebuild every
    /// row per pivot.</para></summary>
    public static bool LpOptimise(Activation engine)
    {
        int n = (int)ReadInteger(engine, 0, "$lp_optimise");
        double[] flat = ReadNumbers(engine, 1, "$lp_optimise");
        double[] objective = ReadNumbers(engine, 2, "$lp_optimise");
        Cell maxCell = ResolveLocal(engine, engine.GetRegister(3));
        bool maximise = maxCell.Tag == Tag.Atom
            && AtomTable.GetById(maxCell.AsAtomId)?.Name == "true";

        if (objective.Length != n + 1 || flat.Length % (n + 1) != 0)
            throw new PrologRuntimeException(
                "type_error", "lp_matrix", engine, engine.GetRegister(1));

        int width = n + 1;
        var rows = new double[flat.Length / width][];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new double[width];
            System.Array.Copy(flat, i * width, rows[i], 0, width);
        }

        LpStatus status = LinearProgram.Solve(
            rows, objective, maximise, out double value, out double[] vertex);

        string name = status switch
        {
            LpStatus.Optimal => "optimal",
            LpStatus.Unbounded => "unbounded",
            _ => "infeasible",
        };
        if (!engine.UnifyRegisterWithCell(
                4, Cell.Atom(AtomTable.Intern(name, permanent: true).Id)))
            return false;
        if (status != LpStatus.Optimal)
            return engine.UnifyRegisterWithCell(5, Cell.Int(0))
                && engine.UnifyRegisterWithCell(6, Cell.Atom(AtomTable.EmptyListId));

        if (!engine.UnifyRegisterWithCell(5, Cell.Ref(engine.MakeFloat(value))))
            return false;
        var cells = new List<Cell>(vertex.Length);
        foreach (double x in vertex) cells.Add(Cell.Ref(engine.MakeFloat(x)));
        return engine.UnifyRegisterWithHeapAt(6, BuildListFromCells(engine, cells));
    }

    /// <summary>Reads a proper list of numbers from a register.</summary>
    private static double[] ReadNumbers(Activation engine, int reg, string who)
    {
        var values = new List<double>();
        Cell node = ResolveLocal(engine, engine.GetRegister(reg));
        while (true)
        {
            if (node.Tag == Tag.Atom && node.AsAtomId == AtomTable.EmptyListId)
                return values.ToArray();
            if (!engine.TryUnconsListLike(node, out Cell head, out Cell tail))
                throw new PrologRuntimeException(
                    "type_error", "list", engine, engine.GetRegister(reg));
            Cell h = ResolveLocal(engine, head);
            values.Add(h.Tag switch
            {
                Tag.Int => h.AsInt,
                Tag.Float => Cell.DecodeFloat(h, engine.GetHeap(h.FloatPairedIndex)),
                Tag.BigInt => (double)engine.AsBigInt(h),
                _ => throw new PrologRuntimeException(
                    "type_error", "number", engine, h),
            });
            node = ResolveLocal(engine, tail);
        }
    }

    /// <summary>Reads an integer argument, for a builtin that needs a count.</summary>
    private static long ReadInteger(Activation engine, int reg, string who)
    {
        Cell c = ResolveLocal(engine, engine.GetRegister(reg));
        if (c.Tag == Tag.Int) return c.AsInt;
        throw new PrologRuntimeException("type_error", "integer", engine, c);
    }
}
