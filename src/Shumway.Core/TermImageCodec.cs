namespace Shumway.Core;

/// <summary>ADR-063: a <see cref="TermImage"/> in a bundle, cell by cell.
/// An atom or a functor goes by name, since their ids are per process;
/// every other cell is its raw bits, the indices in them relative to the
/// image already.</summary>
public static class TermImageCodec
{
    public static void Write(BinaryWriter bw, TermImage image)
    {
        ArgumentNullException.ThrowIfNull(bw);
        ArgumentNullException.ThrowIfNull(image);
        bw.Write((byte)image.Root);
        bw.Write(image.Length);
        foreach (Cell cell in image.Cells)
        {
            bw.Write((byte)cell.Tag);
            switch (cell.Tag)
            {
                case Tag.Atom:
                    WriteName(bw, AtomTable.GetById(cell.AsAtomId)?.Name
                        ?? throw new InvalidOperationException($"Image atom id {cell.AsAtomId} has no name."));
                    break;
                case Tag.Functor:
                {
                    var (atomId, arity) = FunctorTable.Lookup(cell.AsFunctorId);
                    WriteName(bw, AtomTable.GetById(atomId)?.Name
                        ?? throw new InvalidOperationException($"Image functor atom id {atomId} has no name."));
                    bw.Write(arity);
                    break;
                }
                default:
                    bw.Write(cell.Data);
                    break;
            }
        }
    }

    public static TermImage Read(BinaryReader br)
    {
        ArgumentNullException.ThrowIfNull(br);
        var root = (Tag)br.ReadByte();
        int length = br.ReadInt32();
        if (length <= 0)
            throw new InvalidDataException("An image in a bundle has no cells.");
        var cells = new Cell[length];
        for (int j = 0; j < length; j++)
        {
            var tag = (Tag)br.ReadByte();
            switch (tag)
            {
                case Tag.Atom:
                    cells[j] = Cell.Atom(AtomTable.Intern(ReadName(br), permanent: true).Id);
                    break;
                case Tag.Functor:
                {
                    string name = ReadName(br);
                    int arity = br.ReadInt32();
                    cells[j] = Cell.Functor(FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity));
                    break;
                }
                default:
                    cells[j] = new Cell(br.ReadInt64());
                    if (cells[j].Tag != tag)
                        throw new InvalidDataException("An image cell's bits in a bundle disagree with its tag.");
                    break;
            }
        }
        return new TermImage(cells, root);
    }

    private static void WriteName(BinaryWriter bw, string name)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static string ReadName(BinaryReader br)
    {
        int length = br.ReadInt32();
        if (length < 0) throw new InvalidDataException("An image name in a bundle has a negative length.");
        return System.Text.Encoding.UTF8.GetString(br.ReadBytes(length));
    }
}
