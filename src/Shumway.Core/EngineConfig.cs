namespace Shumway.Core;

/// <summary>
/// Tunable parameters for an <see cref="Engine"/>. Sizes are in <see cref="Cell"/>
/// units (or <see cref="ExtraTrailEntry"/> for the extra trail, <c>int</c> for the
/// binding trail). A maximum of <c>0</c> means unlimited (the engine still throws
/// on .NET array-size limits, naturally).
/// </summary>
public sealed class EngineConfig
{
    public int InitialHeapSize { get; init; } = 65536;
    public int MaxHeapSize { get; init; }

    public int InitialStackSize { get; init; } = 8192;
    public int MaxStackSize { get; init; }

    public int InitialBindingTrailSize { get; init; } = 1024;
    public int MaxBindingTrailSize { get; init; }

    public int InitialExtraTrailSize { get; init; } = 64;
    public int MaxExtraTrailSize { get; init; }

    public int InitialRegisterCount { get; init; } = 64;
    public int MaxRegisterCount { get; init; }
}
