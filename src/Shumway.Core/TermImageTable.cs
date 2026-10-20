namespace Shumway.Core;

/// <summary>ADR-063: the process's images, interned once each, for the ids
/// persisted IL bakes. A persisted module is loaded once per process and
/// shared by every engine, so an id baked into it must name the same image
/// in every engine, which an engine's own pool cannot promise. The same
/// lock-free-read, locked-intern discipline as the resume marker table;
/// entries are never removed.</summary>
public static class TermImageTable
{
    private static readonly Dictionary<TermImage, int> _byContent = new();
    private static TermImage[] _byId = new TermImage[64];
    private static int _count;
    private static readonly object _lock = new();

    /// <summary>The id of <paramref name="image"/>, interning it on its first use.</summary>
    public static int Intern(TermImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_lock)
        {
            if (_byContent.TryGetValue(image, out int id)) return id;
            id = _count;
            var table = _byId;
            if (id >= table.Length)
            {
                var grown = new TermImage[table.Length * 2];
                Array.Copy(table, grown, table.Length);
                Volatile.Write(ref _byId, grown);
                table = grown;
            }
            table[id] = image;
            _count = id + 1;
            _byContent[image] = id;
            return id;
        }
    }

    /// <summary>The image an id names; the id came from <see cref="Intern"/>.</summary>
    public static TermImage Get(int id)
    {
        var table = Volatile.Read(ref _byId);
        if ((uint)id >= (uint)table.Length || table[id] is not { } image)
            throw new InvalidOperationException($"Image id {id} names no image of this process.");
        return image;
    }
}
