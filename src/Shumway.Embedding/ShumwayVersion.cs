namespace Shumway.Embedding;

/// <summary>The Shumway version that produced an artifact, recorded in every
/// <c>.shmo</c> and <c>.shum</c> file.
///
/// <para>The point is forward-looking: while the on-disk formats are frozen
/// (a reader requires exactly its own format version), a file still cannot
/// say WHICH build wrote it. Stamping the producer means that when the format
/// does evolve, an old file can be identified — and diagnosed — instead of
/// only being rejected.</para>
///
/// <para>Comparison is by (Major, Minor, Patch); the string form is the
/// familiar <c>0.9.0</c>.</para></summary>
public readonly struct ShumwayVersion : IEquatable<ShumwayVersion>,
                                        IComparable<ShumwayVersion>
{
    public ShumwayVersion(int major, int minor, int patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>The version of the running engine — what a writer stamps.</summary>
    public static ShumwayVersion Current => new(
        PrologEngine.VersionMajor, PrologEngine.VersionMinor, PrologEngine.VersionPatch);

    /// <summary>The zero version: what a file written before producers
    /// stamped one reads back as.</summary>
    public static ShumwayVersion None => default;

    public bool IsNone => Major == 0 && Minor == 0 && Patch == 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    public bool Equals(ShumwayVersion other) =>
        Major == other.Major && Minor == other.Minor && Patch == other.Patch;

    public override bool Equals(object? obj) =>
        obj is ShumwayVersion v && Equals(v);

    public override int GetHashCode() =>
        (Major * 397 ^ Minor) * 397 ^ Patch;

    public int CompareTo(ShumwayVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator ==(ShumwayVersion a, ShumwayVersion b) => a.Equals(b);
    public static bool operator !=(ShumwayVersion a, ShumwayVersion b) => !a.Equals(b);
    public static bool operator <(ShumwayVersion a, ShumwayVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ShumwayVersion a, ShumwayVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ShumwayVersion a, ShumwayVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ShumwayVersion a, ShumwayVersion b) => a.CompareTo(b) >= 0;
}
