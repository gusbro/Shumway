namespace Shumway.Core;

/// <summary>
/// Read-only view over the parser's operator table for runtime queries.
/// Lives in Core (rather than Compiler.Parsing) so that
/// <see cref="Engine.Operators"/> can be typed against it without
/// pulling the parser into the core's dependency graph. The embedding
/// layer registers a concrete adapter on each engine it spins up.
/// </summary>
public interface IOperatorLookup
{
    bool TryGetPrefix(string name, out int precedence, out OperatorShape shape);
    bool TryGetInfix(string name, out int precedence, out OperatorShape shape);
    bool TryGetPostfix(string name, out int precedence, out OperatorShape shape);
}

/// <summary>The seven Prolog operator shapes. Lives in Core for the same
/// reason as <see cref="IOperatorLookup"/>.</summary>
public enum OperatorShape
{
    Fx, Fy, Xf, Yf, Xfx, Xfy, Yfx
}
