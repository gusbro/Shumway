namespace Shumway.Compiler.Il;

/// <summary>The dispatch graphs loaded bundles carry for predicates stripped of
/// their WAM body (--strip-wam), by runtime functor id, decoded when the bundle
/// loads. One instance per engine, shared by every query's activation through
/// <see cref="Shumway.Core.Activation.PersistedIndexGraphs"/>: a query takes
/// only the graphs it dispatches through, so decoding and registering all of
/// them at each setup made every query pay for the whole prelude.</summary>
public sealed class PersistedIndexGraphs
{
    private readonly Dictionary<int, IndexGraph> _graphs = new();

    public int Count => _graphs.Count;

    /// <summary>Decodes a persisted graph and keeps it for the functor; keys are
    /// interned in the current process by the codec.</summary>
    public void Add(int functorId, byte[] graphBytes)
        => _graphs[functorId] = IndexGraphCodec.Decode(graphBytes);

    internal IndexGraph? Get(int functorId)
        => _graphs.TryGetValue(functorId, out var graph) ? graph : null;
}
