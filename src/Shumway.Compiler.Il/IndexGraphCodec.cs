using System.IO;
using System.Text;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Serialises an <see cref="IndexGraph"/> for a bundle. Keys are stored
/// name-relative — atom keys as their atom name, functor keys as name + arity —
/// so a fresh process interns them into its own tables on decode (the same
/// scheme the persisted-IL functor patches use). Integer keys and cursors carry
/// their values directly. The encode runs in the build process (build-time ids
/// → names); the decode in the run process (names → runtime ids).
/// </summary>
internal static class IndexGraphCodec
{
    public static byte[] Encode(IndexGraph graph)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((uint)graph.Nodes.Length);
        foreach (var node in graph.Nodes)
        {
            bw.Write((byte)node.Kind);
            bw.Write(node.ArgIdx);
            if (node.Kind == IndexNodeKind.Term)
            {
                WriteTarget(bw, node.VarTarget);
                WriteTarget(bw, node.ConstTarget);
                WriteTarget(bw, node.ListTarget);
                WriteTarget(bw, node.StructTarget);
            }
            else
            {
                // ADR-027 second-level indexing: the sub-path (-1/-1 for a plain
                // arg lookup). Struct nodes never carry a sub-path.
                bw.Write(node.Sub0);
                bw.Write(node.Sub1);
                int[] keys = node.Keys!;
                var targets = node.Targets!;
                bw.Write((uint)keys.Length);
                for (int i = 0; i < keys.Length; i++)
                {
                    WriteKey(bw, node.Kind, keys[i]);
                    WriteTarget(bw, targets[i]);
                }
                WriteTarget(bw, node.DefaultTarget);
            }
        }
        bw.Flush();
        return ms.ToArray();
    }

    public static IndexGraph Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        int count = (int)br.ReadUInt32();
        var nodes = new IndexNode[count];
        for (int n = 0; n < count; n++)
        {
            var kind = (IndexNodeKind)br.ReadByte();
            int argIdx = br.ReadInt32();
            if (kind == IndexNodeKind.Term)
            {
                nodes[n] = new IndexNode
                {
                    Kind = kind, ArgIdx = argIdx,
                    VarTarget = ReadTarget(br),
                    ConstTarget = ReadTarget(br),
                    ListTarget = ReadTarget(br),
                    StructTarget = ReadTarget(br),
                };
            }
            else
            {
                int sub0 = br.ReadInt32();
                int sub1 = br.ReadInt32();
                int entries = (int)br.ReadUInt32();
                var keys = new int[entries];
                var targets = new IndexTarget[entries];
                for (int i = 0; i < entries; i++)
                {
                    keys[i] = ReadKey(br, kind);
                    targets[i] = ReadTarget(br);
                }
                nodes[n] = new IndexNode
                {
                    Kind = kind, ArgIdx = argIdx,
                    Keys = keys, Targets = targets,
                    DefaultTarget = ReadTarget(br),
                    Sub0 = sub0, Sub1 = sub1,
                };
            }
        }
        return new IndexGraph { Nodes = nodes };
    }

    private static void WriteTarget(BinaryWriter bw, IndexTarget t)
    {
        bw.Write(t.IsNode);
        bw.Write(t.Value);
    }

    private static IndexTarget ReadTarget(BinaryReader br)
    {
        bool isNode = br.ReadBoolean();
        int value = br.ReadInt32();
        return new IndexTarget(isNode, value);
    }

    private static void WriteKey(BinaryWriter bw, IndexNodeKind kind, int key)
    {
        switch (kind)
        {
            case IndexNodeKind.Atom:
                bw.Write(AtomTable.GetById(key)?.Name
                    ?? throw new System.InvalidOperationException(
                        $"IndexGraph encode: atom id {key} has no name."));
                break;
            case IndexNodeKind.Int:
                bw.Write(key);   // value, no name relativity
                break;
            case IndexNodeKind.Struct:
            {
                var (atomId, arity) = FunctorTable.Lookup(key);
                bw.Write(AtomTable.GetById(atomId)?.Name
                    ?? throw new System.InvalidOperationException(
                        $"IndexGraph encode: functor id {key} has no name."));
                bw.Write(arity);
                break;
            }
        }
    }

    private static int ReadKey(BinaryReader br, IndexNodeKind kind)
    {
        switch (kind)
        {
            case IndexNodeKind.Atom:
                return AtomTable.Intern(br.ReadString(), permanent: true).Id;
            case IndexNodeKind.Int:
                return br.ReadInt32();
            case IndexNodeKind.Struct:
            {
                string name = br.ReadString();
                int arity = br.ReadInt32();
                return FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);
            }
            default:
                return 0;
        }
    }
}
