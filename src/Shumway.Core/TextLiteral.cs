namespace Shumway.Core;

/// <summary>
/// A text literal as the compiler interned it: the characters plus what the
/// list's elements are (ADR-047). The pool is keyed by the pair, so
/// <c>"abc"</c> read under <c>double_quotes=chars</c> and under
/// <c>double_quotes=codes</c> get different ids — which is what keeps the
/// presentation out of the instruction set: <c>get_pstr</c> / <c>put_pstr</c>
/// still carry one pool index and nothing else.
/// </summary>
public readonly record struct TextLiteral(string Text, TextKind Kind);
