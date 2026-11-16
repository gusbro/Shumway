using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.Jupyter;

/// <summary>What the host gives the server's Prolog code: its two streams,
/// the output a cell wrote, a stream over a cell's text and the text of an
/// error.
///
/// <para>The builtin table belongs to the process, so each predicate is
/// registered once and finds the server of the engine that calls it.</para></summary>
internal static class ServerPredicates
{
    public const string Requests = "jupyter_requests";
    public const string Responses = "jupyter_responses";

    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>Registers the predicates. Before the server's code is
    /// compiled: a call to a builtin is resolved when its clause is.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered) return;
            BuiltinsRegistry.Register("$jupyter_attach", 0, Attach);
            BuiltinsRegistry.Register("$jupyter_take_output", 1, TakeOutput);
            BuiltinsRegistry.Register("$jupyter_open_text", 2, OpenText);
            BuiltinsRegistry.Register("$jupyter_error_text", 2, ErrorText);
            BuiltinsRegistry.Register("$jupyter_strict_syntax", 2, StrictSyntax);
            _registered = true;
        }
    }

    private static NotebookServer ServerOf(Activation activation)
        => activation.Host is PrologEngine engine && NotebookServer.Of(engine) is { } server
            ? server
            : throw new ShumwayPrologException(
                IsoError.ExistenceError("notebook_server", new AtomTerm("session")));

    // '$jupyter_attach': the session's request and response streams, under
    // their aliases. Once per engine: the registry outlives a query.
    private static bool Attach(Activation activation)
    {
        NotebookServer server = ServerOf(activation);
        StreamRegistry streams = activation.Streams!;
        if (streams.IsAliasTaken(Requests)) return true;
        streams.Add(new StreamHandle(streams.NextId(),
            new PositionTrackingReader(server.Requests, translateNewlines: false),
            "read", alias: Requests));
        streams.Add(new StreamHandle(streams.NextId(), server.Responses, "write", alias: Responses));
        return true;
    }

    // '$jupyter_take_output'(-Chars): what the session wrote since the last
    // call, less one final line end.
    private static bool TakeOutput(Activation activation)
    {
        string text = ServerOf(activation).Output.Take();
        if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
        else if (text.EndsWith('\n')) text = text[..^1];
        return RegisterMarshalling.UnifyRegisterWithTerm(activation, 0, Text(text));
    }

    // '$jupyter_open_text'(+Chars, -Stream): an input stream over a text.
    private static bool OpenText(Activation activation)
    {
        Term source = RegisterMarshalling.ReadRegisterAsTerm(activation, 0);
        if (!source.TryAsText(out string text))
            throw new ShumwayPrologException(IsoError.TypeError("text", source));
        StreamRegistry streams = activation.Streams!;
        StreamHandle handle = streams.Add(new StreamHandle(streams.NextId(),
            new PositionTrackingReader(new StringReader(text), translateNewlines: false), "read"));
        return activation.UnifyRegisterWithCell(1, StreamBuiltins.MakeStreamTerm(activation, handle));
    }

    // '$jupyter_error_text'(+Ball, -Chars): the line a top level shows for
    // an error.
    private static bool ErrorText(Activation activation)
    {
        Term ball = RegisterMarshalling.ReadRegisterAsTerm(activation, 0);
        return RegisterMarshalling.UnifyRegisterWithTerm(activation, 1, Text(Describe(ball)));
    }

    // '$jupyter_strict_syntax'(+New, -Old): whether a clause that does not
    // parse ends the load of its file, set to New; Old is what it was.
    private static bool StrictSyntax(Activation activation)
    {
        ServerOf(activation);
        var engine = (PrologEngine)activation.Host!;
        Term wanted = RegisterMarshalling.ReadRegisterAsTerm(activation, 0);
        if (wanted is not AtomTerm { Name: "true" or "false" } flag)
            throw new ShumwayPrologException(IsoError.TypeError("boolean", wanted));
        bool old = engine.StrictConsultSyntax;
        engine.StrictConsultSyntax = flag.Name == "true";
        return RegisterMarshalling.UnifyRegisterWithTerm(activation, 1, new AtomTerm(old ? "true" : "false"));
    }

    /// <summary>The line a top level shows for an error that was caught as
    /// <paramref name="ball"/>: the error, and the builtin that raised it
    /// when the ball names one.</summary>
    internal static string Describe(Term ball)
    {
        if (ball is not CompoundTerm { Functor: "error", Args.Length: 2 } error)
            return $"error: {AstTermRenderer.RenderQuoted(ball)}";
        // A reader's message is text with a position, not a term to quote.
        if (error.Args[0] is CompoundTerm { Functor: "syntax_error", Args.Length: 1 } syntax
            && syntax.Args[0] is AtomTerm message)
            return $"syntax error: {message.Name}";
        string formal = AstTermRenderer.RenderQuoted(error.Args[0]);
        return error.Args[1] is CompoundTerm { Functor: "/", Args.Length: 2 } indicator
               && indicator.Args[0] is AtomTerm { Name: { Length: > 0 } name } && name[0] != '$'
               && indicator.Args[1] is IntTerm arity
            ? $"error: {formal} in {name}/{arity.Value}"
            : $"error: {formal}";
    }

    private static Term Text(string text) => new StringTerm(text, TextKind.Chars);
}
