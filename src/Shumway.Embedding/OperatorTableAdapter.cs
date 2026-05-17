using Shumway.Compiler.Parsing;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Adapter that exposes a <see cref="OperatorTable"/> through the
/// <see cref="IOperatorLookup"/> interface that Core ships. Used by the
/// engine renderer (via <see cref="Engine.Operators"/>) to decide
/// between operator-form and canonical-form output without forcing
/// Shumway.Core or Shumway.Builtins to depend on the parser assembly.
/// </summary>
internal sealed class OperatorTableAdapter : IOperatorLookup
{
    private readonly OperatorTable _table;

    public OperatorTableAdapter(OperatorTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    public bool TryGetPrefix(string name, out int precedence, out OperatorShape shape)
    {
        if (_table.TryGetPrefix(name, out int p, out OperatorType t))
        {
            precedence = p;
            shape = Map(t);
            return true;
        }
        precedence = 0;
        shape = OperatorShape.Fx;
        return false;
    }

    public bool TryGetInfix(string name, out int precedence, out OperatorShape shape)
    {
        if (_table.TryGetInfix(name, out int p, out OperatorType t))
        {
            precedence = p;
            shape = Map(t);
            return true;
        }
        precedence = 0;
        shape = OperatorShape.Xfx;
        return false;
    }

    public bool TryGetPostfix(string name, out int precedence, out OperatorShape shape)
    {
        if (_table.TryGetPostfix(name, out int p, out OperatorType t))
        {
            precedence = p;
            shape = Map(t);
            return true;
        }
        precedence = 0;
        shape = OperatorShape.Xf;
        return false;
    }

    private static OperatorShape Map(OperatorType t) => t switch
    {
        OperatorType.Fx => OperatorShape.Fx,
        OperatorType.Fy => OperatorShape.Fy,
        OperatorType.Xf => OperatorShape.Xf,
        OperatorType.Yf => OperatorShape.Yf,
        OperatorType.Xfx => OperatorShape.Xfx,
        OperatorType.Xfy => OperatorShape.Xfy,
        OperatorType.Yfx => OperatorShape.Yfx,
        _ => OperatorShape.Xfx,
    };
}
