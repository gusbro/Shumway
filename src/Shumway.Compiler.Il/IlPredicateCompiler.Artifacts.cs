using System.Reflection;
using System.Runtime.CompilerServices;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// The code one compile emits at run time: its delegates, and the images its
/// methods load from their literal tables. The engine claims the atoms the code
/// names while any of it can run (ADR-003); a method stores the artifact's id in
/// its activation before calling a builtin that may change code, which is how a
/// method on the stack is known to be running.
/// </summary>
public sealed class IlArtifact
{
    private static int _nextId;

    private Delegate[] _delegates = System.Array.Empty<Delegate>();
    private TermImage[] _images = System.Array.Empty<TermImage>();

    /// <summary>Process-wide, never 0: what a method stores as running.</summary>
    public int Id { get; } = Interlocked.Increment(ref _nextId);

    /// <summary>The delegates the compile created, the continuation methods'
    /// among them.</summary>
    public IReadOnlyList<Delegate> Delegates => Volatile.Read(ref _delegates);

    /// <summary>The images the methods load by reference.</summary>
    public IReadOnlyList<TermImage> Images => Volatile.Read(ref _images);

    // Replaced, never changed: the continuation methods are added on the
    // worker while the engine's thread may read the artifact.
    internal void Add(Delegate del, IReadOnlyList<object> literals)
    {
        lock (this)
        {
            var delegates = new Delegate[_delegates.Length + 1];
            System.Array.Copy(_delegates, delegates, _delegates.Length);
            delegates[^1] = del;
            Volatile.Write(ref _delegates, delegates);
            List<TermImage>? added = null;
            foreach (object literal in literals)
                if (literal is TermImage image && System.Array.IndexOf(_images, image) < 0
                    && (added is null || !added.Contains(image)))
                    (added ??= new()).Add(image);
            if (added is null) return;
            var images = new TermImage[_images.Length + added.Count];
            System.Array.Copy(_images, images, _images.Length);
            added.CopyTo(images, _images.Length);
            Volatile.Write(ref _images, images);
        }
    }
}

public sealed partial class IlPredicateCompiler
{
    // The artifact of the compile in progress on this thread; null in a
    // persisted emission, whose code claims nothing (a bundle's atoms are
    // permanent) and names no artifact.
    [ThreadStatic] private static IlArtifact? _artifact;

    private static readonly ConditionalWeakTable<Delegate, IlArtifact> _artifactByDelegate = new();
    private static readonly object _artifactLock = new();

    /// <summary>The artifact <paramref name="del"/> belongs to; null for a
    /// delegate no run-time compile created.</summary>
    public static IlArtifact? ArtifactOf(Delegate del)
        => _artifactByDelegate.TryGetValue(del, out var artifact) ? artifact : null;

    private static readonly FieldInfo IlArtifactField =
        typeof(Activation).GetField(nameof(Activation._ilArtifact))!;

    // A compile's artifact, opened at its entry: the outermost scope creates
    // it, or joins the one given (continuation methods join their delegate's).
    private readonly struct ArtifactScope : IDisposable
    {
        private readonly IlArtifact? _prev;

        // Declared: `new ArtifactScope()` binds to a struct's default
        // constructor, not to one whose parameter has a default.
        public ArtifactScope() : this(null) { }

        public ArtifactScope(IlArtifact? join)
        {
            _prev = _artifact;
            _artifact ??= join ?? new IlArtifact();
        }

        public void Dispose() => _artifact = _prev;
    }

    // A delegate the compile in progress created, with the literal table its
    // method reads.
    private static T NoteArtifactDelegate<T>(T del, IReadOnlyList<object> literals) where T : Delegate
    {
        if (_artifact is not { } artifact) return del;
        artifact.Add(del, literals);
        lock (_artifactLock)
            if (!_artifactByDelegate.TryGetValue(del, out _)) _artifactByDelegate.Add(del, artifact);
        return del;
    }

    /// <summary>Before a call to a builtin that may change code: the method
    /// names its artifact as running, so the code stays claimed while the
    /// builtin runs and after it returns, until the dispatch loop regains
    /// control. Every other builtin call stores nothing.</summary>
    private static void EmitArtifactStore(IlEmit emit, int builtinId)
    {
        if (_persistPatches is not null || _artifact is not { } artifact) return;
        if (!Shumway.Builtins.BuiltinsRegistry.GetById(builtinId).MayChangeCode) return;
        emit.LoadArgument(0);
        emit.LoadConstant(artifact.Id);
        emit.StoreField(IlArtifactField);
    }
}
