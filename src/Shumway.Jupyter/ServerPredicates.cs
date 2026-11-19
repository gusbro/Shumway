using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.Jupyter;

/// <summary>What the host gives the server's Prolog code: its two streams,
/// the output a cell wrote, a stream over a cell's text, the text of an
/// error and the text of an answer.
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
            BuiltinsRegistry.Register("$jupyter_answer_text", 4, AnswerText);
            BuiltinsRegistry.Register("$jupyter_strict_syntax", 2, StrictSyntax);
            BuiltinsRegistry.Register("$jupyter_tracing", 1, Tracing);
            BuiltinsRegistry.Register("$jupyter_documented", 1, Documented);
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

    // '$jupyter_answer_text'(+Term, +Names, +Priority, -Chars): Term as the
    // top level writes it in an answer, its variables named by Names, a list
    // of Name=Variable, and bounded by Priority as an operand is: quoted, a
    // list of characters as a string, cut short past answer_max_depth.
    private static bool AnswerText(Activation activation)
    {
        var engine = (PrologEngine)activation.Host!;
        Term term = RegisterMarshalling.ReadRegisterAsTerm(activation, 0);
        Term names = RegisterMarshalling.ReadRegisterAsTerm(activation, 1);
        Term bound = RegisterMarshalling.ReadRegisterAsTerm(activation, 2);
        if (bound is not IntTerm { Value: >= 0 and <= 1200 } priority)
            throw new ShumwayPrologException(IsoError.TypeError("integer", bound));
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        for (Term cursor = names; cursor is CompoundTerm { Functor: ".", Args.Length: 2 } cell; cursor = cell.Args[1])
            if (cell.Args[0] is CompoundTerm { Functor: "=", Args.Length: 2 } pair
                && pair.Args[0] is AtomTerm name && pair.Args[1] is VarTerm variable)
                renames.TryAdd(variable.Name, name.Name);
        Term shown = ResidualProjection.SubstituteVarNames(
            ResidualProjection.ElideCycleMarkers(
                Shumway.TopLevel.SolutionFormatter.Elide(term, engine.Flags.AnswerMaxDepth)),
            renames);
        string text = AstTermRenderer.Render(shown, (int)priority.Value, engine.Operators,
            quoted: true, portrayText: true);
        return RegisterMarshalling.UnifyRegisterWithTerm(activation, 3, Text(text));
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

    // '$jupyter_tracing'(+Boolean): the engine's four-port tracer, on or off.
    // The tracer shows no goal whose name starts with $, so the switch is not
    // a line of the trace it starts or ends, as a call of notrace/0 would be.
    private static bool Tracing(Activation activation)
    {
        Term wanted = RegisterMarshalling.ReadRegisterAsTerm(activation, 0);
        if (wanted is not AtomTerm { Name: "true" or "false" } flag)
            throw new ShumwayPrologException(IsoError.TypeError("boolean", wanted));
        return EngineBuiltin(flag.Name == "true" ? "trace" : "notrace").Impl(activation);
    }

    private static BuiltinEntry EngineBuiltin(string name)
    {
        foreach (BuiltinEntry entry in BuiltinsRegistry.AllEntries())
            if (entry.Arity == 0 && entry.Name == name) return entry;
        throw new InvalidOperationException($"The engine has no {name}/0.");
    }

    // The reference of the engine's predicates reads their sources: once.
    private static readonly Lazy<(string Name, int Arity)[]> DocumentedPredicates = new(() =>
        PredicateDoc.Entries().Select(entry => (entry.Name, entry.Arity)).Distinct().ToArray());

    // '$jupyter_documented'(-Indicators): Name/Arity of each predicate the
    // engine documents, which are the ones a program is meant to call.
    private static bool Documented(Activation activation)
    {
        Term list = new AtomTerm("[]");
        (string Name, int Arity)[] documented = DocumentedPredicates.Value;
        for (int i = documented.Length - 1; i >= 0; i--)
        {
            var indicator = new CompoundTerm("/", new Term[]
            {
                new AtomTerm(documented[i].Name), new IntTerm(documented[i].Arity),
            });
            list = new CompoundTerm(".", new[] { indicator, list });
        }
        return RegisterMarshalling.UnifyRegisterWithTerm(activation, 0, list);
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
