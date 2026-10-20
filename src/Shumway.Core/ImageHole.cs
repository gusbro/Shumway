namespace Shumway.Core;

/// <summary>ADR-063: a cell of a <see cref="TermImage"/> that a register
/// decides at copy time. <see cref="In"/>: the copy writes the register's
/// cell at the offset, as <c>unify_value</c> does in write mode. <see cref="Out"/>:
/// the copy points the register at the offset's cell, which the image holds
/// as an unbound variable, as <c>unify_variable</c> does. <see cref="Y"/>
/// names a permanent variable of the frame, else an argument register.</summary>
public readonly struct ImageHole : IEquatable<ImageHole>
{
    public const byte In = 0;
    public const byte Out = 1;
    public const byte Y = 2;

    public ImageHole(int offset, byte kind, int slot)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (kind > (Out | Y)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
        Offset = offset;
        Kind = kind;
        Slot = slot;
    }

    /// <summary>The cell, relative to the image's first.</summary>
    public int Offset { get; }

    /// <summary><see cref="In"/> or <see cref="Out"/>, with <see cref="Y"/>
    /// for a permanent variable.</summary>
    public byte Kind { get; }

    /// <summary>The argument register, or the frame slot under <see cref="Y"/>.</summary>
    public int Slot { get; }

    public bool IsOut => (Kind & Out) != 0;
    public bool IsY => (Kind & Y) != 0;

    /// <summary>The hole as one number, for hashing and bundles.</summary>
    public long Key => ((long)Offset << 32) | ((long)Kind << 24) | (uint)Slot;

    public static ImageHole FromKey(long key)
        => new((int)(key >> 32), (byte)((key >> 24) & 0xFF), (int)(key & 0xFFFFFF));

    public bool Equals(ImageHole other) => Key == other.Key;
    public override bool Equals(object? obj) => obj is ImageHole h && Equals(h);
    public override int GetHashCode() => Key.GetHashCode();
    public override string ToString()
        => $"{(IsOut ? "out" : "in")} {(IsY ? "Y" : "X")}{Slot} at {Offset}";
}
